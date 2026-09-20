// IO Power Control v0.1.17 - power capacity, protection and load-shedding controller for
// Space Engineers, built against Industrial Overhaul v1.7.7. History: CHANGELOG.md.
//
// This is NOT a power meter. It answers four separate questions:
//   1. What are we producing right now?        (producer CurrentOutput, exact)
//   2. What are we consuming right now?        (producer output sum, exact - see MEASUREMENT)
//   3. What COULD the connected system draw?   (modelled, with coverage disclosed)
//   4. What can safely be switched off?        (policy + protection + shedding engine)
//
// MEASUREMENT - what the PB API actually gives us, and what it does not:
//   * IMyPowerProducer.CurrentOutput/MaxOutput exist and are exact, in MW. Everything in the
//     GENERATION block and the whole of CURRENT DEMAND is derived from them.
//   * Every connected grid shares one electrical network, so the sum of ALL producer output
//     (reactors, engines, solar, wind, AND discharging batteries) IS the network's current
//     consumption. That identity is why current demand is exact and needs no block catalog.
//   * There is NO ingame power-consumer interface and no per-block consumption property.
//     The ONLY per-block electrical figure the API exposes is the DetailedInfo STRING.
//     Potential demand is therefore a MODEL, and its coverage is displayed, never hidden.
//   * Because DetailedInfo is a string parse, it is refreshed for a CHUNK of blocks per cycle,
//     not for every block every tick. Per-grid load attribution is consequently a SAMPLED
//     ESTIMATE and is labelled "est" wherever it is shown. Network totals are not estimates.
//
// ABSOLUTE INVARIANTS:
//   * The only block mutations this script performs are IMyFunctionalBlock.Enabled=false on a
//     shed block, Enabled=true restoring a block IT shed, and IMyBatteryBlock.ChargeMode
//     Recharge->Auto (and back). Nothing else on any block is ever written.
//   * A block the script did not itself switch off is NEVER switched on.
//   * Protection is re-validated against LIVE block state immediately before the block is
//     disabled - never from the classification cache. A connector that locked since the last
//     scan must not be sheddable for even one cycle.
//   * UNKNOWN classification is never shed. Failure to classify is a reason to leave a block
//     alone, not a reason to treat it as expendable.
//   * On external (connector-attached) constructs, only an explicitly safe category set is
//     ever touched. Thrusters, gyros, controllers, connectors and landing gear on someone
//     else's ship are observed and never controlled. A power script must not create a
//     mechanical safety failure while trying to save electricity.
const string VERSION = "0.1.19";
const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
static readonly StringComparer SCI = StringComparer.OrdinalIgnoreCase;

// ---------------------------------------------------------------------------------------
// CATEGORIES. A block category is WHAT IT IS. Its tier (below) is HOW EXPENDABLE IT IS right
// now, which depends on role and mode. Keeping them separate is what lets the policy live in
// a table instead of in conditionals scattered through the script.
const int C_LIFE = 0, C_CTRL = 1, C_PROP = 2, C_GEN = 3, C_DOCK = 4, C_WEAP = 5, C_SENS = 6,
  C_COMM = 7, C_BATT = 8, C_JUMP = 9, C_IND = 10, C_PROD = 11, C_YARD = 12, C_FUEL = 13,
  C_UTIL = 14, C_DECOR = 15, C_MECH = 16, C_UNK = 17, NCAT = 18;
static readonly string[] CATN = new string[] { "LifeSupport", "Control", "Propulsion",
  "Generation", "Docking", "Weapons", "Sensors", "Comms", "Battery", "Jump", "Industrial",
  "Production", "Shipyard", "Fuel", "Utility", "Decor", "Mechanical", "Unknown" };

// TIERS, most protected first. Shedding walks candidates from the BOTTOM tier up and stops at
// the tier the current condition permits. PROTECTED is not "tier 0 priority" - it is an
// absolute control constraint checked separately, and no policy row may ever assign it.
const int T_PROT = 0, T_CRIT = 1, T_ESS = 2, T_NORM = 3, T_IND = 4, T_DISC = 5, NTIER = 6;
static readonly string[] TIERN = new string[]
  { "Protected", "Critical", "Essential", "Normal", "Industrial", "Discretionary" };

const int R_STATION = 0, R_SHIP = 1;
static readonly string[] ROLEN = new string[] { "STATION", "SHIP" };
const int M_ECON = 0, M_NORM = 1, M_RED = 2, M_EMERG = 3;
static readonly string[] MODEN = new string[] { "ECONOMY", "NORMAL", "REDALERT", "EMERGENCY" };
const int S_UNKNOWN = 0, S_FLIGHT = 1, S_DOCKED = 2, S_LANDED = 3, S_CONSTRUCTION = 4,
  S_STATIC = 5;
static readonly string[] STATEN = new string[]
  { "UNKNOWN", "FLIGHT", "DOCKED", "LANDED", "CONSTRUCTION", "STATIC" };
// POWER CONDITION is an electrical fact. MODE is an operator decision. They are deliberately
// different things: an electrical collapse may force EMERGENCY condition, but only a person
// declares RedAlert.
const int K_NORMAL = 0, K_CAUTION = 1, K_WARNING = 2, K_CRITICAL = 3, K_EMERG = 4;
static readonly string[] CONDN = new string[]
  { "NORMAL", "CAUTION", "WARNING", "CRITICAL", "EMERGENCY" };

// Where a potential-draw number came from. Printed by `scan` so a wrong number can be traced
// to the tier that produced it instead of argued about.
const int SRC_NONE = 0, SRC_DETAIL = 1, SRC_IFACE = 2, SRC_CATALOG = 3, SRC_LEARNED = 4,
  SRC_CFG = 5, SRC_BUILTIN = 6;
static readonly string[] SRCN = new string[]
  { "unknown", "detail", "iface", "catalog", "learned", "config", "builtin" };

// ---------------------------------------------------------------------------------------
// POLICY TABLE. One row per role x mode. Unlisted categories fall back to the NORMAL tier.
// This is data: changing what a station sheds in Red Alert is an edit to one string here, not
// a hunt through the shedding engine. Unknown is pinned to CRITICAL in every row - the
// failure-safe rule expressed as policy rather than as a special case in code.
// Battery is DISCRETIONARY in every row on purpose: the only battery action this script takes
// is Recharge->Auto, which is instantly reversible and costs nothing but charging time, so it
// is always the cheapest relief available and should always be spent first.
static readonly string[] POLICY = new string[] {
"STATION|ECONOMY|LifeSupport=C,Control=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=E,Comms=E,Sensors=N,Propulsion=E,Weapons=N,Utility=N,Production=I,Industrial=I,Shipyard=D,Jump=D,Decor=D,Battery=D",
"STATION|NORMAL|LifeSupport=C,Control=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=E,Comms=E,Sensors=E,Propulsion=E,Weapons=N,Utility=N,Production=I,Industrial=I,Shipyard=D,Jump=D,Decor=D,Battery=D",
"STATION|REDALERT|LifeSupport=C,Control=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Weapons=C,Sensors=C,Comms=C,Fuel=E,Propulsion=E,Utility=N,Production=D,Industrial=D,Shipyard=D,Jump=D,Decor=D,Battery=D",
"STATION|EMERGENCY|LifeSupport=C,Control=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=E,Comms=E,Sensors=N,Propulsion=N,Weapons=N,Utility=I,Production=D,Industrial=D,Shipyard=D,Jump=D,Decor=D,Battery=D",
"SHIP|ECONOMY|LifeSupport=C,Control=C,Propulsion=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=C,Sensors=E,Comms=E,Weapons=N,Utility=N,Jump=D,Production=I,Industrial=I,Shipyard=D,Decor=D,Battery=D",
"SHIP|NORMAL|LifeSupport=C,Control=C,Propulsion=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=C,Sensors=E,Comms=E,Weapons=N,Utility=N,Jump=N,Production=I,Industrial=I,Shipyard=D,Decor=D,Battery=D",
"SHIP|REDALERT|LifeSupport=C,Control=C,Propulsion=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=C,Weapons=C,Sensors=C,Comms=C,Utility=N,Jump=D,Production=D,Industrial=D,Shipyard=D,Decor=D,Battery=D",
"SHIP|EMERGENCY|LifeSupport=C,Control=C,Propulsion=C,Generation=C,Docking=C,Mechanical=C,Unknown=C,Fuel=C,Comms=E,Sensors=N,Weapons=N,Utility=I,Jump=D,Production=D,Industrial=D,Shipyard=D,Decor=D,Battery=D" };
int[][] _tier; // [role * 4 + mode][category] -> tier

// ---------------------------------------------------------------------------------------
// CLASSIFICATION HINTS. Interfaces are tried FIRST and settle almost every vanilla block.
// These substrings only run for a block no interface claimed - which is exactly where
// Industrial Overhaul machinery lands, because IO adds subtypes rather than interfaces.
// Matched against SubtypeId first, then the display name, case-insensitively. Data, not
// logic: extending IO coverage is adding a row here, and `scan` prints what is still
// unmatched so the rows can be written from evidence.
// Checked AHEAD of the interface fallback, because IO implements steam plant as gas blocks.
// Everything here is critical power-generation support: it is never shed, in any mode.
static readonly string[] GENSUP = new string[] {
  "steam", "geothermal", "wellhead", "well_head", "boiler", "condenser", "superheat",
  "feedwater", "coolant", "turbinefeed" };

static readonly string[] HINTS = new string[] {
"refin=Production", "assembl=Production", "survivalkit=Production", "crusher=Industrial",
"furnace=Industrial", "smelt=Industrial", "blast=Industrial", "kiln=Industrial",
"centrifuge=Industrial", "press=Industrial", "mixer=Industrial", "distill=Industrial",
"cracker=Industrial", "electroly=Industrial", "chemical=Industrial", "processor=Industrial",
"sifter=Industrial", "washer=Industrial", "separator=Industrial", "pulveriz=Industrial",
"boiler=Generation", "steam=Generation", "turbine=Generation", "generator=Generation",
"engine=Generation", "reactor=Generation", "rtg=Generation", "solar=Generation",
"wind=Generation", "dynamo=Generation", "condenser=Generation", "shipyard=Shipyard",
"welder=Industrial", "grinder=Industrial", "drill=Industrial", "projector=Utility",
"pump=Utility", "compressor=Fuel", "tank=Fuel", "fuel=Fuel", "gasoline=Fuel", "oil=Fuel",
"jump=Jump", "battery=Battery", "capacitor=Battery", "accumulator=Battery",
"antenna=Comms", "beacon=Comms", "radar=Sensors", "sensor=Sensors", "camera=Sensors",
"turret=Weapons", "gun=Weapons", "launcher=Weapons", "cannon=Weapons", "missile=Weapons",
"railgun=Weapons", "weapon=Weapons", "shield=Weapons", "light=Decor", "lamp=Decor",
"door=Decor", "lcd=Decor", "panel=Decor", "seat=Control", "cockpit=Control",
"cryo=LifeSupport", "medical=LifeSupport", "oxygen=LifeSupport", "air=LifeSupport",
"vent=LifeSupport", "gravity=Utility", "conveyor=Utility", "sorter=Utility",
"connector=Docking", "merge=Docking", "landing=Docking", "rotor=Mechanical",
"piston=Mechanical", "hinge=Mechanical", "suspension=Mechanical", "wheel=Mechanical",
"thrust=Propulsion", "gyro=Propulsion", "timer=Control", "programmable=Control",
"remote=Control", "event=Control" };
// Parsed HINTS as parallel arrays with a first-letter bit each - see BuildTables.
string[] _hintKey; int[] _hintCat; int[] _hintBit; int _hintN;
string[] _gensupLower; int[] _gensupBit;

// ---------------------------------------------------------------------------------------
// IO SUBTYPE CATALOG - Tier 3 of the potential-demand model. DELIBERATELY EMPTY IN v0.1.
// Every entry here would be a number invented off-server, and an invented rated load is worse
// than a disclosed unknown: it silently inflates the model while the coverage figure says the
// model is complete. The `scan` command exists precisely to fill this table from the live IO
// 1.7.7 server. Format: "SubtypeId=MW", matched case-insensitively against SubtypeId, else
// against the full TypeId/SubtypeId. A catalog entry outranks a learned one.
// GENERATED - do not hand-edit. Regenerate with:
//   python IO_Power_Control/tools/extract_io_catalog.py "<mod>/Data" "<game>/Content/Data"
// Every figure is read from Industrial Overhaul's own .sbc definitions (RequiredPowerInput,
// OperationalPowerConsumption, PowerConsumptionMoving, RequiredIdlePower; largest wins,
// because Potential asks for the ceiling). Nothing here is estimated: a block that declares
// no figure gets no row and keeps being reported as unknown.
// Validated against the live server - CementKiln 3, LargeRefinery 3, GeothermalWellHead 0.5
// match what those blocks report through DetailedInfo, and LargePistonBase 0.002 /
// LargeBlockSlideDoor 0.001 match BuildInfo and a measured demand delta.
static readonly string[] CATALOG = new string[] {
"AdvancedAssembler=4", "AirDuctLight=0.00006", "AirVentFan=0.1", "AirVentFanFull=0.1",
"AirVentFull=0.1", "AirtightHangarDoorWarfare2A=0.001", "AirtightHangarDoorWarfare2B=0.001",
"AirtightHangarDoorWarfare2C=0.001", "AtmBlock=0.002", "AutoLoom=1.5", "BasicAssembler=0.1",
"BasicAssemblerSG=0.1", "BitumenExtractor=1.5", "Blast Furnace=2", "CementKiln=3",
"Centrifuge=6", "ChemicalPlant=3.5", "CoalFurnace=0.25", "Collector=0.002",
"CollectorFlat=0.002", "CollectorSmall=0.002", "ContractBlock=0.002", "CorridorLight=0.00006",
"CorridorNarrowStowage=0.00006", "CorridorRoundLight=0.00006", "CylinderGasolineTank=0.001",
"CylinderRocketFuelTank=0.001", "DeuteriumExtractor=10", "DeuteriumRamscoop=25",
"DeuteriumTankSmall=0.5", "DeuteriumTankSmallFrame=0.5", "EmotionControllerLarge=0.0001",
"EmotionControllerSmall=0.0001", "EnclosedDeuteriumTank=0.5", "EnclosedGasolineTank=0.001",
"EnclosedRocketFuelTank=0.001", "EventControllerLarge=0.0005", "EventControllerSmall=0.0005",
"Extruder=1.5", "FastNeutronReactor=0.5", "FoodDispenser=0.002", "FoodProcessor=0.18",
"FuelRefinery=2", "GasolineRefinery=2", "GeothermalWellHead=0.5", "HoloLCDLarge=0.00006",
"HoloLCDSmall=0.00002", "Incinerator=5", "IrrigationSystem=0.5", "Jukebox=0.02",
"LabEquipment=0.0001", "LabEquipment1=0.00006", "LabEquipment2=0.00006",
"LabEquipment3=0.0001", "LargeAdvancedStator=0.002", "LargeAssembler=1.5",
"LargeAssemblerIndustrial=1.5", "LargeAssemblerNew=1.5", "LargeBasicMission=0.01",
"LargeBlockAcidBatteryBlock=6", "LargeBlockAlgaeFarm=0.0002",
"LargeBlockAlgaeFarmReskin=0.0002", "LargeBlockBatteryBlock=12",
"LargeBlockBatteryBlockWarfare2=12", "LargeBlockBatteryReskin=6",
"LargeBlockBatteryReskinOffset=6", "LargeBlockBillboard=0.00006",
"LargeBlockBillboardRound=0.00006", "LargeBlockBroadcastController=0.0001",
"LargeBlockConduitLight=0.00006", "LargeBlockConduitLightInv=0.00006",
"LargeBlockConsole=0.0002", "LargeBlockCorner_LCD_1=0.00006", "LargeBlockCorner_LCD_2=0.00006",
"LargeBlockCorner_LCD_Flat_1=0.00006", "LargeBlockCorner_LCD_Flat_2=0.00006",
"LargeBlockFloodlight=0.001", "LargeBlockFloodlightAngled=0.001",
"LargeBlockFloodlightCornerL=0.001", "LargeBlockFloodlightCornerR=0.001",
"LargeBlockFrontLight=0.001", "LargeBlockGate=0.001", "LargeBlockGyro=0.01",
"LargeBlockHalfTrofferLight=0.00006", "LargeBlockHalfTrofferLightInv=0.00006",
"LargeBlockInsetAquarium=0.00006", "LargeBlockInsetEntertainmentCorner=0.02",
"LargeBlockInsetKitchen=0.00006", "LargeBlockInsetLight=0.00006",
"LargeBlockInsetTerrariumDesert=0.00006", "LargeBlockInsetTerrariumForest=0.00006",
"LargeBlockInsetWallLight=0.00006", "LargeBlockLabDeskMicroscope=0.00006",
"LargeBlockLightRound=0.00006", "LargeBlockLightSquare=0.00006",
"LargeBlockLight_1corner=0.00008", "LargeBlockLight_2corner=0.00008",
"LargeBlockOxygenFarm=0.02", "LargeBlockOxygenFarmReskin=0.02",
"LargeBlockOxygenGeneratorLab=1", "LargeBlockOxygenTankLab=0.001",
"LargeBlockPrototechBattery=48", "LargeBlockPrototechGyro=0.01",
"LargeBlockPrototechOxygenGenerator=1", "LargeBlockRemoteControl=0.01",
"LargeBlockSlideDoor=0.001", "LargeBlockTransponder=0.0005", "LargeBlockTrofferLight=0.00006",
"LargeCameraBlock=0.00003", "LargeCameraTopMounted=0.00003", "LargeCapacitor=150",
"LargeCurvedLCDPanel=0.00006", "LargeDefensiveCombat=0.01", "LargeDeuteriumTank=0.5",
"LargeDeuteriumTankNoLegs=0.5", "LargeDiagonalLCDPanel=0.00006", "LargeElectronMatrix=50",
"LargeExhaustCap=0.0002", "LargeExhaustPipe=0.0002", "LargeFlightMovement=0.01",
"LargeFullBlockLCDPanel=0.00006", "LargeGasolineTank=0.001", "LargeGasolineTankNoLegs=0.001",
"LargeHeatVentBlock=0.0002", "LargeHinge=0.002", "LargeHydrogenTank=0.001",
"LargeHydrogenTankBulk=0.001", "LargeHydrogenTankIndustrial=0.001",
"LargeHydrogenTankSmall=0.001", "LargeHydrogenTankSmallLab=0.001", "LargeInsetPlanter=0.00006",
"LargeJumpDrive=32", "LargeJumpDriveReskin=32", "LargeLCDPanel=0.00006",
"LargeLCDPanel3x3=0.0009", "LargeLCDPanel5x3=0.0015", "LargeLCDPanel5x5=0.0025",
"LargeLCDPanelWide=0.00012", "LargeLightPanel=0.00006", "LargeOffensiveCombat=0.01",
"LargePathRecorderBlock=0.01", "LargePistonBase=0.002", "LargePistonBaseReskin=0.002",
"LargeProjector=0.0002", "LargePrototechAssembler=4", "LargePrototechJumpDrive=64",
"LargePrototechRefinery=4", "LargeRefinery=3", "LargeRefineryIndustrial=3",
"LargeRocketFuelTank=0.001", "LargeRocketFuelTankNoLegs=0.001", "LargeSearchlight=0.001",
"LargeStator=0.002", "LargeTextPanel=0.00006", "LgParachute=0.001", "MedicalStation=0.0001",
"MediumHinge=0.002", "MediumPistonBase=0.002", "MetalWheelSuspension3x3=0.3",
"MicroelectronicsFactory=2", "MunitionsFactory=1", "NanoAssembler=7.5", "NuclearReactor=0.5",
"OffroadShortSuspension1x1=0.66", "OffroadShortSuspension1x1mirrored=0.66",
"OffroadShortSuspension2x2=1.33", "OffroadShortSuspension2x2Mirrored=1.33",
"OffroadShortSuspension3x3=2", "OffroadShortSuspension3x3mirrored=2",
"OffroadShortSuspension5x5=6", "OffroadShortSuspension5x5mirrored=6",
"OffroadSmallShortSuspension1x1=0.075", "OffroadSmallShortSuspension1x1mirrored=0.075",
"OffroadSmallShortSuspension2x2=0.12", "OffroadSmallShortSuspension2x2Mirrored=0.12",
"OffroadSmallShortSuspension3x3=0.25", "OffroadSmallShortSuspension3x3mirrored=0.25",
"OffroadSmallShortSuspension5x5=0.72", "OffroadSmallShortSuspension5x5mirrored=0.72",
"OffroadSmallSuspension1x1=0.075", "OffroadSmallSuspension1x1mirrored=0.075",
"OffroadSmallSuspension2x2=0.12", "OffroadSmallSuspension2x2Mirrored=0.12",
"OffroadSmallSuspension3x3=0.25", "OffroadSmallSuspension3x3mirrored=0.25",
"OffroadSmallSuspension5x5=0.72", "OffroadSmallSuspension5x5mirrored=0.72",
"OffroadSuspension1x1=0.66", "OffroadSuspension1x1mirrored=0.66", "OffroadSuspension2x2=1.33",
"OffroadSuspension2x2Mirrored=1.33", "OffroadSuspension3x3=2",
"OffroadSuspension3x3mirrored=2", "OffroadSuspension5x5=1.5", "OffroadSuspension5x5mirrored=6",
"OffsetLight=0.00006", "OffsetSpotlight=0.0002", "OilCracker=8", "OrePurifier=5",
"Oxygen=0.001", "OxygenGeneratorSmall=0.25", "OxygenTankSmall=0.001",
"PassageSciFiLight=0.00006", "PlateStamp=1.5", "PolymerFactory=5", "Reprocessor=8",
"RockCrusher=2.5", "RocketFuelRefinery=2", "RotatingLightLarge=0.0002",
"RotatingLightSmall=0.0002", "ShortSuspension1x1=0.66", "ShortSuspension1x1mirrored=0.66",
"ShortSuspension2x2=1.33", "ShortSuspension2x2Mirrored=1.33", "ShortSuspension3x3=2",
"ShortSuspension3x3mirrored=2", "ShortSuspension5x5=6", "ShortSuspension5x5mirrored=6",
"SiliconFuser=3.5", "SmParachute=0.001", "SmallAdvancedStator=0.0002",
"SmallAdvancedStatorSmall=0.0002", "SmallAirVent=0.01", "SmallAirVentFan=0.01",
"SmallAirVentFanFull=0.01", "SmallAirVentFull=0.01", "SmallBasicMission=0.01",
"SmallBlockAcidBatteryBlock=2", "SmallBlockBatteryBlock=4", "SmallBlockBatteryBlockWarfare2=4",
"SmallBlockBatteryReskin=4", "SmallBlockBroadcastController=0.0001",
"SmallBlockConsoleModuleScreens=0.00002", "SmallBlockCorner_LCD_1=0.00002",
"SmallBlockCorner_LCD_2=0.00002", "SmallBlockCorner_LCD_Flat_1=0.00002",
"SmallBlockCorner_LCD_Flat_2=0.00002", "SmallBlockFloodlight=0.0002",
"SmallBlockFloodlightAngled=0.0002", "SmallBlockFloodlightAngledRotated=0.0002",
"SmallBlockFloodlightCornerL=0.0002", "SmallBlockFloodlightCornerR=0.0002",
"SmallBlockFloodlightDown=0.0002", "SmallBlockFrontLight=0.0002", "SmallBlockGyro=0.002",
"SmallBlockInsetLight=0.00006", "SmallBlockJukeboxReskin=0.02", "SmallBlockLightRound=0.00006",
"SmallBlockLightSquare=0.00006", "SmallBlockLight_1corner=0.00008",
"SmallBlockLight_2corner=0.00008", "SmallBlockOxygenGeneratorLab=0.25",
"SmallBlockPrototechBattery=32", "SmallBlockPrototechGyro=0.002",
"SmallBlockRemoteControl=0.01", "SmallBlockSmallAcidBatteryBlock=0.1",
"SmallBlockSmallBatteryBlock=0.2", "SmallBlockSmallLight=0.00006",
"SmallBlockTransponder=0.0005", "SmallCameraBlock=0.00003", "SmallCameraTopMounted=0.00003",
"SmallCapacitor=15", "SmallCurvedLCDPanel=0.00002", "SmallCylinderGasolineTank=0.001",
"SmallCylinderRocketFuelTank=0.001", "SmallDefensiveCombat=0.01",
"SmallDiagonalLCDPanel=0.00002", "SmallElectronMatrix=5", "SmallEnclosedGasolineTank=0.001",
"SmallEnclosedRocketFuelTank=0.001", "SmallExhaustCap=0.0002", "SmallExhaustPipe=0.0002",
"SmallFlightMovement=0.01", "SmallFullBlockLCDPanel=0.00002", "SmallGasolineTank=0.001",
"SmallGasolineTankMini=0.001", "SmallHeatVentBlock=0.0002", "SmallHinge=0.002",
"SmallHydrogenTank=0.001", "SmallHydrogenTankBulk=0.001", "SmallHydrogenTankLab=0.001",
"SmallHydrogenTankSmall=0.0002", "SmallLCDPanel=0.00004", "SmallLCDPanelWide=0.00008",
"SmallLight=0.00006", "SmallLightPanel=0.00006", "SmallOffensiveCombat=0.01",
"SmallOxygenTankSmall=0.001", "SmallPathRecorderBlock=0.01", "SmallPistonBase=0.0002",
"SmallPistonBaseReskin=0.0002", "SmallProjector=0.0001", "SmallPrototechJumpDrive=24",
"SmallPrototechRefinery=1", "SmallRocketFuelTank=0.001", "SmallRocketFuelTankMini=0.001",
"SmallSearchlight=0.0002", "SmallShortSuspension1x1=0.075",
"SmallShortSuspension1x1mirrored=0.075", "SmallShortSuspension2x2=0.12",
"SmallShortSuspension2x2Mirrored=0.12", "SmallShortSuspension3x3=0.25",
"SmallShortSuspension3x3mirrored=0.25", "SmallShortSuspension5x5=0.72",
"SmallShortSuspension5x5mirrored=0.72", "SmallStator=0.0002", "SmallSuspension1x1=0.075",
"SmallSuspension1x1mirrored=0.075", "SmallSuspension2x2=0.12",
"SmallSuspension2x2Mirrored=0.12", "SmallSuspension3x3=0.25",
"SmallSuspension3x3mirrored=0.25", "SmallSuspension5x5=0.72",
"SmallSuspension5x5mirrored=0.72", "SmallTextPanel=0.00002", "SolarConcentrator=0.02",
"SolarConcentratorMount=0.002", "StoreBlock=0.002", "SurvivalKit=0.1", "SurvivalKitLarge=0.1",
"SurvivalKitLargeReskin=0.1", "SurvivalKitSmallReskin=0.1", "Suspension1x1=0.66",
"Suspension1x1mirrored=0.66", "Suspension2x2=1.33", "Suspension2x2Mirrored=1.33",
"Suspension3x3=2", "Suspension3x3mirrored=2", "Suspension5x5=6", "Suspension5x5mirrored=6",
"TransparentLCDLarge=0.00006", "TransparentLCDSmall=0.00002", "TrussPillarLight=0.00006",
"TrussPillarLightSmall=0.00006", "VendingMachine=0.002", "VirtualMassLarge=0.6",
"VirtualMassSmall=0.025", "WireDrawer=1.5" };
// Two catalogs, deliberately at DIFFERENT precedence. The generated one is a definition-time
// rating and must NOT override what a running block reports about itself - DetailedInfo
// reflects upgrades, damage and the block as it actually exists. The user's rows are the
// opposite: a number the operator measured on purpose, which outranks everything.
Dictionary<string, double> _builtin;  // from CATALOG, below DetailedInfo
Dictionary<string, double> _user;     // from [PowerControl.Catalog], above everything
Dictionary<string, double> _learned;  // definition -> largest max-input ever seen. Persisted.

// Name tags. Cheap to check and they survive a Custom Data wipe, which is why both a tag and
// an INI key are supported for protection.
const string TAG_PROT = "[PC-PROTECTED]";
const string TAG_SHED = "[PC-SHED]";     // opt IN a block that policy would otherwise spare
const string TAG_IGN = "[PC-IGNORE]";    // never touched, never counted as a consumer
const string TAG_LCD = "[PC-LCD]";       // optional page suffix: [PC-LCD:grids] etc.
const string TAG_LCD_STEM = "[PC-LCD";
const string SEC = "PowerControl";
const string SECCAT = "PowerControl.Catalog";
// Everything below this marker in the PB Custom Data is owned and rewritten by the script.
// Config is parsed from ABOVE it only, so a scan report can never corrupt the configuration.
const string RPTMARK = "; ==== POWER CONTROL GENERATED REPORT - EDITS BELOW THIS LINE ARE LOST ====";

class Cfg {
  public string RoleCfg = "Auto";
  public string ModeCfg = "Normal";
  public string StateCfg = "Auto";
  public bool AutoShed = true;
  public bool ControlDocked = true;   // only ever the safe category set - see SAFE_EXTERNAL
  public double UpdateSeconds = 1;    // dashboard/decision cadence
  public double RescanSeconds = 30;   // topology + classification refresh
  public double CautionReserveMW = 15, WarningReserveMW = 8, CriticalReserveMW = 0;
  public double CautionReservePct = 20, WarningReservePct = 10, CriticalReservePct = 2;
  public double ShedReserveMW = 4;    // shed below this reserve
  public double RestoreReserveMW = 20; // restore only above this reserve (hysteresis band)
  public double RecoverySeconds = 30;  // reserve must hold above restore threshold this long
  public double RestoreStepSeconds = 10; // and this long between each restored item
  public int MaxShedPerCycle = 4;
  // Shedding requires observed electrical strain, not merely thin credible headroom. Turn
  // this off only where neither stress signal can ever be true and the operator accepts
  // shedding on the headroom figure alone.
  public bool RequireStress = true;
  public double StressHoldSeconds = 10;
  // Capacity Risk debounce, advisory display only. Worsening is news and arrives quickly;
  // improvement has to hold, because a momentary trough in demand is not a recovery.
  public double RiskPromoteSeconds = 2;
  public double RiskRecoverSeconds = 15;
  // Battery drain only counts as stress above this, or 2% of credible generation if larger.
  // 0.1 MW was too sensitive to be meaningful: a bank reporting ~11 MW gross out against
  // ~12 MW gross in crosses that constantly while moving no net energy at all.
  public double BattStressMW = 0.5;
  // ...and stored energy must have fallen CONSISTENTLY WITH the measured discharge over the
  // same interval, which is what a control oscillation can never do. Expressed as a fraction
  // of the integrated expected energy, so it is independent of bank size: the previous
  // share-of-capacity rule qualified in 11 s on a 3 MWh bank and 18 minutes on a 300 MWh one
  // for the same real deficit. Loose on purpose - SE reports simultaneous gross charge and
  // discharge, so demanding equality would fail on the very artefact this tolerates.
  public double DeclineConsistency = 0.5;
  // Absolute floor, an energy rather than a share, whose only job is to reject "stored did not
  // move at all". Being absolute is what keeps it from reintroducing the scaling problem.
  public double MinDeclineMWh = 0.002;
  // EXIT from an established stress, which is not the entry test run backwards. The drain must
  // fall below this fraction of the entry bar and STAY there - ordinary variation between a
  // 2 MW and a 5 MW deficit is not a recovery.
  public double BattRecoverFrac = 0.5;
  public double StressRecoverSeconds = 15;
  // Elapsed time after ANY shed action before another is considered. MaxShedPerCycle caps one
  // action; this paces the actions themselves. Without it, ShedStep runs at the tick rate and
  // a 12 MW deficit cost five blocks in five seconds.
  public double ShedSettleSeconds = 5;
  // Counts battery DISCHARGE capacity as available generation. "Auto" means yes for a SHIP
  // and no for a STATION. It converts Reserve from "generation we have" into "generation we
  // have plus a tank that is draining", which is a shorter-lived promise - true and useful on
  // a battery ship, and self-deception on a base. Set true/false to force it either way.
  public string CountBatteryDischarge = "Auto";
  public double HistorySeconds = 10;
  public int HistorySamples = 360;    // 360 x 10s = 60 minutes
  public int EventLines = 40;
  public int ScanChunk = 40;          // blocks re-read from DetailedInfo per cycle
  public int InstrBudgetPercent = 60;
}
Cfg _c = new Cfg();

// Categories the script may switch off on an EXTERNAL (connector-attached) construct. This
// list is the whole of section 22 of the specification: nothing that could strand or release
// a docked ship is in it, and it is checked independently of the policy tier, so no mode can
// widen it.
static readonly int[] SAFE_EXTERNAL = new int[] { C_PROD, C_IND, C_YARD, C_JUMP, C_BATT };

// ---------------------------------------------------------------------------------------
// RUNTIME MODEL
// ---------------------------------------------------------------------------------------
// One per candidate consumer. Only IMyFunctionalBlock instances become BI records: a cargo
// container or a block of armour is not a consumer and must not be counted as an unknown one,
// or the coverage figure becomes meaningless noise.
class BI {
  public IMyTerminalBlock B;
  public IMyFunctionalBlock F;  // non-null iff the block can be switched at all
  public long Id;
  public long GridId;
  public int Con;               // index into _cons
  public int Cat = C_UNK;
  public string Def = "";       // TypeId/SubtypeId
  public string Sub = "";
  public double MaxIn;          // MW the block could draw. 0 with Src==SRC_NONE means UNKNOWN.
  public double CurIn;          // MW observed at the last DetailedInfo refresh of this block
  public bool HasCur;           // CurIn is a MEASUREMENT, so CurIn==0 means genuinely idle
  public int Src = SRC_NONE;
  public bool CfgProt;          // protected by tag or Custom Data
  public bool CfgShed;          // opted in by tag or Custom Data
  public bool Ignore;
  public double BrownSince = -1;  // when this block started reading enabled-but-not-working
  public bool IsProducer;       // a real IMyPowerProducer: supply, never counted as demand
}
// A power producer. Batteries are producers too but are accounted separately everywhere,
// because counting a battery as generation capacity is how a base convinces itself it has
// headroom it does not have.
class PI {
  public IMyPowerProducer P;
  public long Id;
  public int Con;
  public string Name = "";
  public bool WasWorking = true;
  public double LastMax;
  public double Proven;   // highest CurrentOutput ever WITNESSED from this producer
  public bool Env;        // MaxOutput already tracks conditions (solar, wind)
  public bool SeenWorking; // witnessed working in THIS session - see the offline->online reset
}
class BAT {
  public IMyBatteryBlock Bat;
  public long Id;
  public int Con;
}
// A construct: the home grid plus its mechanically attached subgrids, or one docked ship.
// Keyed by the smallest grid EntityId it contains, which is stable across renames.
class CON {
  public long Key;
  public string Name = "?";
  public bool Home;
  public bool Docked;
  public int Grids, Blocks;
  public double GenCur, GenMax;         // exact, from producers
  public double LoadEst, PotLoad;       // sampled estimate / model
  public int BattCount;
  public double BattIn, BattMaxIn, BattStored, BattMaxStored, BattOut;
  public int Unknowns;
  public bool IsNew;
}
// One shed action the script owns and therefore may reverse. Kind 0 = block disabled,
// kind 1 = battery taken out of Recharge. Persisted to Storage so a recompile does not
// abandon equipment in the off state with nobody remembering why.
class SHED {
  public long Id;
  public int Kind;
  public int Prior;      // kind 1: the ChargeMode we found. kind 0: unused.
  public double Relief;  // MW believed recovered, for the event log
  public string Name = "";
  public double At;
}
// One second of resolution for a minute. The 10-second history cannot see a three-second
// battery excursion, and a three-second excursion is exactly what was being mistaken for
// stress - so the evidence needed to judge the thresholds did not exist at any resolution we
// were recording.
// One per block considered at the last shed action. Verdicts cover every outcome reachable
// once the structural filters have passed, so there is no silent path left.
const int CV_TIER = 0, CV_CTRL = 1, CV_RELIEF = 2, CV_INCLUDED = 3;
static readonly string[] CVN = new string[] {
  "excluded: tier below depth", "excluded: not controllable",
  "EXCLUDED: relief<=0.01", "included" };
class CAUD {
  public long Id;
  public string Name = "";
  public int Cat, Tier, V, Pos;
  public bool En, HasCur, FreshOk;
  public double Cur, Max, Rel, FreshCur, FreshMax;
  public string Detail;
}

struct FAST {
  public float T, Demand, GenCur, GenCred, BattNet, BattOut, Stored;
  public short Brown;
  public bool Str;
}

struct SAMP {
  public float T, GenCur, GenMax, Demand, Reserve, Stored, Net, Pot;
  public byte Cond;
}

// RUNTIME COST. Measured, not inferred - the production failure was diagnosed by reading code
// because there was no instrumentation to read instead. Cheap: two property reads per phase.
const int PH_BOOT = 0, PH_DISCOVER = 1, PH_DETAIL = 2, PH_MEASURE = 3, PH_CONTROL = 4,
  PH_RENDER = 5, PH_CAND = 6, PH_REPORT = 7, NPH = 8;
static readonly string[] PHN = new string[]
  { "boot", "discover", "refresh detail", "measure", "control", "render", "candidate build",
    "report" };
int[] _phLast = new int[NPH], _phPeak = new int[NPH];
int[] _phWork = new int[NPH];          // items processed by that phase on its last run
int _tickLast, _tickPeak, _ctorCost, _mark;
// `last run` is nearly always a run that returned immediately: at Update10 with UpdateSeconds=1
// most invocations cost about 3 instructions and say nothing. The last COMPLETE tick is the
// figure worth reading, so both are kept.
int _fullLast;
string _peakWhat = "";                 // what the script was DOING on its most expensive run
bool _yielded;                          // the last chunked phase stopped on budget, not on work

void Mark() { _mark = Runtime.CurrentInstructionCount; }
void Spend(int ph, int work) {
  int d = Runtime.CurrentInstructionCount - _mark;
  if (d < 0) d = 0;
  _phLast[ph] = d; _phWork[ph] = work;
  if (d > _phPeak[ph]) _phPeak[ph] = d;
  _mark = Runtime.CurrentInstructionCount;
}
// THE BUDGET IS PER TICK, NOT PER PHASE.
//
// v0.1.18 gave every resumable phase the same soft ceiling and the production grid showed why
// that is not the same thing. On a RESCAN the model is already published, so discovery yielded
// correctly at 30,449 against a 30,000 budget - and then the REST of the tick ran, because
// protection cannot pause for twelve seconds while a rescan finishes. Detail top-up, Measure,
// Condition and Render cost another 16,878. PEAK RUN 47,327 of 50,000.
//
// Nothing was over its own budget. Every phase asked "am I over 60%?" and none asked "what
// still has to run after me?"
//
// A resumable phase now yields early enough to PAY FOR the work that must follow it, and it
// uses the measured peaks of those phases rather than a guess. The per-phase instrumentation
// added in v0.1.17 is what makes this possible: the script budgets from what it has actually
// been observed to cost, not from an estimate someone wrote down once.
//
// On a FIRST start the tick returns immediately after discovery, so there is no tail to
// reserve for and the full soft budget is available. That is why this never showed during boot.
int TailCost() {
  if (!_modelReady) return 0;
  return _phPeak[PH_DETAIL] + _phPeak[PH_MEASURE] + _phPeak[PH_CONTROL] + _phPeak[PH_RENDER];
}
// The ceiling a resumable phase must stop at. The safety margin is deliberately generous: a
// peak is the worst cost OBSERVED, not a proof of the worst cost possible.
int Ceiling() {
  int max = Runtime.MaxInstructionCount;
  int soft = max * _c.InstrBudgetPercent / 100;
  int room = max - TailCost() - max / 5;
  if (room < soft) soft = room;
  // A phase must always be able to make SOME progress, or a rescan can never complete and the
  // model goes stale forever - a quiet failure that looks exactly like a working script.
  int floor = max / 25;
  return soft < floor ? floor : soft;
}
bool Over() { return Runtime.CurrentInstructionCount > Ceiling(); }

MyIni _ini = new MyIni();
MyIni _bini = new MyIni();
StringBuilder _sb = new StringBuilder();

// STAGED DIAGNOSTIC REPORT.
//
// v0.1.17 removed the forced rediscovery from `scan` and the changelog claimed that made the
// command safe. It did not, and the production grid proved it. What remained was a detail
// top-up deliberately raised to 90% of the hard limit, an unbounded Measure(), and eight more
// full walks of the model with no budget check anywhere. On 1928 functional blocks that is a
// GUARANTEED termination, not an unlucky one - the 90% line spends 45,000 of 50,000 before the
// report emits its first character.
//
// The report is now built exactly the way discovery is: resumable, bounded by the same
// cooperative budget, and published only when it is complete. A half-written diagnostic is
// worse than none, because it looks like a finished one.
const int R_IDLE = 0, R_DETAIL = 1, R_HEAD = 2, R_CONSTRUCTS = 3, R_PRODUCERS = 4,
  R_BATTERIES = 5, R_TANKS = 6, R_DEFSCAN = 7, R_DEFEMIT = 8, R_UNKSCAN = 9, R_UNKEMIT = 10,
  R_UNKTAIL = 11, R_UNKHINT = 12, R_ALLBLOCKS = 13, R_BROWNOUT = 14, R_FAST = 15, R_HIST = 16,
  R_COST = 17, R_AUDIT = 18, R_SHEDSTATE = 19, R_EVENTS = 20, R_PUBLISH = 21, NRS = 22;
static readonly string[] RN = new string[]
  { "idle", "detail", "header", "constructs", "producers", "batteries", "tanks",
    "definitions", "definitions emit", "unknown scan", "unknown emit", "unknown tail",
    "unknown hint", "all blocks", "brownout", "fast ring", "history", "cost", "audit",
    "shed state", "events", "publish" };
int _rs = R_IDLE;
int _rIdx;                 // cursor inside the CURRENT section, so a section can resume
int _rTicks, _rDetail;     // ticks spent so far, blocks given a fresh detail read
bool _rAll;                // `scan all`
double _rStarted;
// The report iterates the model it STARTED with. Publication is a reference swap, so holding
// these keeps one coherent snapshot even if a rescan completes mid-report - the same invariant
// discovery relies on, for the same reason.
List<BI> _rBi; List<PI> _rProd; List<BAT> _rBats; List<CON> _rCons;
// NEVER _sb. Render() clears and reuses that one every single tick, and a report under
// construction must not be something another phase can wipe out from underneath it.
StringBuilder _rb = new StringBuilder();
Dictionary<string, int> _rCnt, _rCat, _rSrc, _rUTot, _rUShown;
Dictionary<string, double> _rMw, _rCur;
List<string> _rDefs = new List<string>();    // key order snapshot: emitting resumes by index
List<string> _rUDefs = new List<string>();
List<string> _rHint = new List<string>();
int _rUnknown, _rTankN, _rBrownN;
// A report that never finishes is a leak, not a diagnostic. At 1 Hz this is ~15 minutes.
const int R_TICK_CAP = 900;
double _lastMeasureAt = -1e9;
List<IMyTerminalBlock> _all = new List<IMyTerminalBlock>();
List<BI> _bi = new List<BI>();
List<PI> _prod = new List<PI>();
List<BAT> _bats = new List<BAT>();
List<CON> _cons = new List<CON>();
List<IMyShipConnector> _conn = new List<IMyShipConnector>();
List<IMyShipController> _ctrls = new List<IMyShipController>();
Dictionary<long, int> _gridCon = new Dictionary<long, int>();  // grid EntityId -> construct idx
Dictionary<long, BI> _prev = new Dictionary<long, BI>();       // last scan, to carry values
Dictionary<long, PI> _prodPrev = new Dictionary<long, PI>();   // last scan, to detect removal
Dictionary<long, double> _provenSaved = new Dictionary<long, double>();  // from Storage
Dictionary<long, string> _conName = new Dictionary<long, string>();
Dictionary<long, long> _gridConId = new Dictionary<long, long>();  // grid -> STABLE construct id
HashSet<long> _claimed = new HashSet<long>();
long _conSeq, _homeKey;
List<int> _newCons = new List<int>();
List<MyIniKey> _catKeys = new List<MyIniKey>();
HashSet<string> _unkDefs = new HashSet<string>(SCI);
Dictionary<long, SHED> _shed = new Dictionary<long, SHED>();
List<long> _shedOrder = new List<long>();  // restore is LIFO over this: last shed, first back
HashSet<long> _knownCon = new HashSet<long>();
List<string> _events = new List<string>();
List<SAMP> _hist = new List<SAMP>();
List<FAST> _fast = new List<FAST>();
double _lastFast;
List<IMyTextSurface> _screens = new List<IMyTextSurface>();
List<string> _screenPage = new List<string>();

int _role = R_STATION, _mode = M_NORM, _state = S_UNKNOWN, _cond = K_NORMAL;
bool _roleAuto = true, _stateAuto = true;
double _now, _lastScan = -1e9, _lastTick = -1e9, _lastHist = -1e9, _lastRestore = -1e9;
double _goodSince = -1e9;   // when reserve last rose above the restore threshold
double _lastSave;
bool _restoreBlocked;       // head of the restore queue is larger than the margin
double _genCur, _genMax, _demand, _reserve, _n1, _potential, _rechargeExposure;
double _genCred, _reserveNameplate, _credPeak, _credFlatSince;
double _battStressSince = -1e9, _battStoredAtStress, _battBar, _battDecline;
double _recoverSince = -1e9, _battRecBar;
// Actuator pacing. SP_* is the observable decision state, so UAT can tell "shed", "settling",
// "deficit persists, permitted again" and "improved, stopped" apart in the event log.
const int SP_IDLE = 0, SP_SETTLING = 1, SP_ARMED = 2, SP_STOPPED = 3;
static readonly string[] SPN = new string[] { "idle", "settling", "armed", "stopped-improved" };
double _shedSettleUntil, _battOutAtShed;
int _shedPhase, _shedActions;
bool _candAbort;
string _refuseWhy = "";
List<string> _refusals = new List<string>();
List<CAUD> _audit = new List<CAUD>();
double _auditAt = -1;
int _auditShed;
double _battExpected, _battNeedFall, _stressT;
int _brownout, _brownoutRaw;
bool _fuelAtCeiling, _stressed;
int _risk = K_NORMAL;
int _riskCand = K_NORMAL;   // debounce candidate - see Condition()
double _riskCandSince;
double _battStored, _battMaxStored, _battIn, _battOut, _battMaxIn, _largestProd, _genProven;
double _battNetOut;    // summed per-battery NET DISCHARGE, clamped at 0 - see Measure
double _battNetFlow;   // signed per-battery net flow. POSITIVE = DISCHARGING. One convention.
bool _countBatt;
string _largestProdName = "-";
int _known, _unknown, _scanCursor, _protCount;
string _lastAction = "none";
// STAGED BOOT. Everything the constructor used to do, spread over the first ticks.
const int B_TABLES = 0, B_CATALOG = 1, B_STORAGE = 2, B_CONFIG = 3, B_DONE = 4;
static readonly string[] BN = new string[] { "tables", "catalog", "storage", "config", "done" };
int _boot, _bootIdx;
string[] _bootLines;

// STAGED DISCOVERY. The staging model is only ever touched by DiscoverStep.
const int D_IDLE = 0, D_FETCH = 1, D_GRIDS = 2, D_CONSTRUCTS = 3, D_BLOCKS = 4, D_PUBLISH = 5;
static readonly string[] DN = new string[]
  { "idle", "fetch", "grids", "constructs", "blocks", "publish" };
int _ds, _dIdx, _dProcessed, _dTotal;
bool _modelReady;              // a complete model has been published at least once
List<IMyTerminalBlock> _sAll = new List<IMyTerminalBlock>();
List<BI> _sBi; List<PI> _sProd; List<BAT> _sBats;
List<IMyShipConnector> _sConn; List<IMyShipController> _sCtrls;
List<CON> _sCons; Dictionary<long, int> _sGridCon;
Dictionary<long, IMyTerminalBlock> _sReps; Dictionary<long, int> _sCounts;
List<IMyTerminalBlock> _sConRep; List<int> _sConBest; List<long> _sGridIds;
List<IMyTextSurface> _sScreens; List<string> _sScreenPage;

bool _dirty = true;
string _err = "";
long _runs;

// The constructor runs at RECOMPILE, before Main exists, so anything expensive here can
// terminate the script in a way no amount of chunking elsewhere can rescue. It therefore does
// the minimum and measures itself; tables, Storage and config are staged across the first
// ticks by BootStep.
public Program() {
  int t0 = Runtime.CurrentInstructionCount;
  Runtime.UpdateFrequency = UpdateFrequency.Update10;
  _ctorCost = Runtime.CurrentInstructionCount - t0;
  if (_ctorCost < 0) _ctorCost = 0;
}

// One boot step per tick, each bounded by the cooperative budget. Storage and the built-in
// catalog are the two that scale, and both resume exactly where they left off.
void BootStep() {
  if (_boot == B_TABLES) {
    BuildTables();                       // policy rows + hints only: small and fixed
    _boot = B_CATALOG; _bootIdx = 0;
    return;
  }
  if (_boot == B_CATALOG) {
    while (_bootIdx < CATALOG.Length) {
      var cp = CATALOG[_bootIdx++].Split('=');
      double cv;
      if (cp.Length == 2 && double.TryParse(cp[1], out cv)) _builtin[cp[0].Trim()] = cv;
      if (Over()) { _yielded = true; return; }
    }
    _boot = B_STORAGE; _bootIdx = 0;
    _bootLines = string.IsNullOrEmpty(Storage) ? new string[0] : Storage.Split('\n');
    _shed.Clear(); _shedOrder.Clear();
    return;
  }
  if (_boot == B_STORAGE) {
    while (_bootIdx < _bootLines.Length) {
      StorageLine(_bootLines[_bootIdx++]);
      if (Over()) { _yielded = true; return; }
    }
    _bootLines = null;
    _boot = B_CONFIG;
    return;
  }
  if (_boot == B_CONFIG) {
    ParseConfig();
    _boot = B_DONE;
    Ev("Power Control v" + VERSION + " started");
    // If Storage was cleared, blocks this script shed stay off and nothing here can know they
    // were ours - the safe failure, because the alternative is switching on equipment the
    // PLAYER turned off. Reported rather than silently assumed either way.
    if (_shedOrder.Count > 0)
      Ev("Recovered " + _shedOrder.Count + " shed entries from Storage");
    else
      Ev("No shed state in Storage: anything already off stays off");
  }
}

public void Save() {
  _sb.Clear();
  _sb.Append("V|").Append(VERSION).Append('\n');
  _sb.Append("M|").Append(MODEN[_mode]).Append('\n');
  if (!_stateAuto) _sb.Append("T|").Append(STATEN[_state]).Append('\n');
  for (int i = 0; i < _shedOrder.Count; i++) {
    SHED s;
    if (!_shed.TryGetValue(_shedOrder[i], out s)) continue;
    _sb.Append("S|").Append(s.Id).Append('|').Append(s.Kind).Append('|').Append(s.Prior)
      .Append('|').Append(Fx(s.Relief)).Append('|').Append(Clean(s.Name)).Append('\n');
  }
  // Witnessed producer output. Cheap to store, expensive to reacquire - it takes a base
  // actually running under load, which is exactly what a paste-deploy interrupts.
  int np = 0;
  for (int i = 0; i < _prod.Count; i++) {
    if (np++ >= 200) break;
    if (_prod[i].Proven <= 0) continue;
    _sb.Append("P|").Append(_prod[i].Id).Append('|').Append(Fx(_prod[i].Proven)).Append('\n');
  }
  // Learned rated loads are the expensive half of the model: they take a full scan of a live
  // base to acquire. Persisting them means a recompile does not blind the potential-demand
  // model. Bounded so Storage cannot grow without limit.
  int n = 0;
  foreach (var kv in _learned) {
    if (n++ >= 250) break;
    _sb.Append("L|").Append(Clean(kv.Key)).Append('|').Append(Fx(kv.Value)).Append('\n');
  }
  Storage = _sb.ToString();
  _sb.Clear();
}

// One Storage line. Split out so the boot phase can process it a few at a time - a base with
// a full learned catalog and a long shed list has hundreds of these.
void StorageLine(string line) {
  {
    var p = line.Split('|');
    if (p.Length < 2) return;
    if (p[0] == "M") { int m = IdxOf(MODEN, p[1]); if (m >= 0) _mode = m; }
    else if (p[0] == "T") {
      int t = IdxOf(STATEN, p[1]);
      if (t >= 0) { _state = t; _stateAuto = false; }
    } else if (p[0] == "S" && p.Length >= 6) {
      long id; int k, pr; double rel;
      if (!long.TryParse(p[1], out id) || !int.TryParse(p[2], out k)) return;
      int.TryParse(p[3], out pr); double.TryParse(p[4], out rel);
      if (_shed.ContainsKey(id)) return;
      _shed[id] = new SHED { Id = id, Kind = k, Prior = pr, Relief = rel, Name = p[5], At = 0 };
      _shedOrder.Add(id);
    } else if (p[0] == "P" && p.Length >= 3) {
      long pid; double pv;
      if (long.TryParse(p[1], out pid) && double.TryParse(p[2], out pv) && pv > 0)
        _provenSaved[pid] = pv;
    } else if (p[0] == "L" && p.Length >= 3) {
      double v;
      if (double.TryParse(p[2], out v) && v > 0) _learned[p[1]] = v;
    }
  }
}

// ---------------------------------------------------------------------------------------
// MAIN
// ---------------------------------------------------------------------------------------
public void Main(string argument, UpdateType src) {
  _runs++;
  _now += Runtime.TimeSinceLastRun.TotalSeconds;
  try {
    if (!string.IsNullOrEmpty(argument) &&
        (src & (UpdateType.Terminal | UpdateType.Trigger | UpdateType.Script)) != 0) {
      Command(argument.Trim());
      return;
    }
    if (_now - _lastTick < _c.UpdateSeconds) return;
    _lastTick = _now;
    Tick();
  } catch (Exception e) {
    // A power controller that throws must not also leave equipment switched off with no
    // explanation, so the exception is surfaced on the dashboard and in the log rather than
    // just aborting the run. Nothing is shed on a cycle that failed.
    _err = e.Message;
    Ev("SCRIPT ERROR " + e.Message);
    Echo("ERROR: " + e.Message);
  } finally {
    // RECORDED ON EVERY PATH, including the boot and discovery early returns and the command
    // path. It used to run only at the end of a complete tick, so during startup - the most
    // expensive runs in the whole session - the dashboard reported `peak 0`. The one number
    // the operator is asked to read back was blind to precisely the ticks under suspicion.
    _tickLast = Runtime.CurrentInstructionCount;
    if (_tickLast > _tickPeak) {
      _tickPeak = _tickLast;
      _peakWhat = _boot != B_DONE ? "boot " + BN[_boot]
                : _ds != D_IDLE ? "discover " + DN[_ds]
                : _rs != R_IDLE ? "report " + RN[_rs]
                : "tick";
    }
  }
}

void Tick() {
  Mark();
  _yielded = false;
  // BOOT. Nothing else may run until the tables, Storage and config exist.
  if (_boot != B_DONE) {
    BootStep();
    Spend(PH_BOOT, _bootIdx);
    EchoStarting();
    return;
  }
  // A new discovery pass starts only when the previous one has published. Config is re-read on
  // the same cadence as before.
  if (_ds == D_IDLE && (_dirty || _now - _lastScan >= _c.RescanSeconds)) {
    _dirty = false;
    ParseConfig();
    _ds = D_FETCH;
  }
  if (_ds != D_IDLE) Spend(PH_DISCOVER, DiscoverStep());
  // FIRST-START SAFETY. Until a complete model has been published there is nothing to reason
  // about, so nothing reasons. A rescan in progress does NOT reach this - the live model is
  // still published and still authoritative.
  if (!_modelReady) { EchoStarting(); return; }
  RefreshDetailChunk();
  Spend(PH_DETAIL, _c.ScanChunk);
  // Role first: Measure's Auto handling of battery discharge capacity depends on it, and a
  // one-cycle disagreement between the two is the kind of thing that only ever shows up as an
  // unexplained EMERGENCY on a ship's first tick.
  DetectRole();
  DetectState();
  // Measure is deliberately NOT chunked. It produces one coherent electrical snapshot, and a
  // snapshot assembled from two different ticks is not a snapshot - it is a number nobody can
  // defend. Its per-block cost is a few property reads, and it is instrumented so the claim
  // that this is affordable can be checked rather than assumed.
  Measure();
  _lastMeasureAt = _now;
  Spend(PH_MEASURE, _bi.Count);
  Condition();
  if (_c.AutoShed) ShedStep();
  RestoreStep();
  Spend(PH_CONTROL, _cand.Count);
  SampleFast();
  Sample();
  if (_now - _lastSave >= 60) { _lastSave = _now; Save(); }
  Render();
  Spend(PH_RENDER, _screens.Count);
  // The diagnostic report runs LAST and on whatever budget is left, because a diagnostic must
  // never compete with protection for instructions.
  if (_rs != R_IDLE) Spend(PH_REPORT, ReportStep());
  _fullLast = Runtime.CurrentInstructionCount;
  // ONE METRIC PER LINE. The previous layout packed five figures onto a line, which on a real
  // LCD either overflowed or relied on the surface wrapping it - and a wrapped number reads as
  // a different number. Blank lines group the four things being reported: what the script is,
  // the power balance, what it can see, and what it costs to run. Values and semantics are
  // unchanged; this is formatting only.
  Echo("Power Control v" + VERSION);
  Echo(ROLEN[_role] + "/" + MODEN[_mode]);
  Echo("cond " + CONDN[_cond]);
  Echo("");
  Echo("gen " + MW(_genCur));
  Echo("cred " + MW(_genCred));
  Echo("rated " + MW(_genMax));
  Echo("demand " + MW(_demand));
  Echo("reserve " + MW(_reserve));
  Echo("");
  Echo("blocks " + _bi.Count);
  Echo("discovery " + DN[_ds]);
  Echo("producers " + _prod.Count);
  Echo("batteries " + _bats.Count);
  Echo("grids " + _cons.Count);
  Echo("model coverage " + Pct(Coverage()));
  Echo("unknown " + _unknown);
  Echo("shed " + _shed.Count);
  if (_rs != R_IDLE) Echo("report " + RN[_rs] + " " + _rIdx);
  Echo("");
  Echo("instr " + Runtime.CurrentInstructionCount + "/" + Runtime.MaxInstructionCount);
  Echo("run " + Runtime.LastRunTimeMs.ToString("0.00") + "ms");
  if (_err.Length > 0) { Echo(""); Echo("last error: " + _err); }
}

// What the operator sees before there is a model. It says plainly that nothing will act.
void EchoStarting() {
  Echo("Power Control v" + VERSION);
  Echo(_boot != B_DONE ? "STARTING" : "DISCOVERING");
  Echo("");
  if (_boot != B_DONE) {
    Echo("boot " + BN[_boot]);
    if (_boot == B_CATALOG) Echo("catalog " + _bootIdx + "/" + CATALOG.Length);
    if (_boot == B_STORAGE)
      Echo("storage " + _bootIdx + "/" + (_bootLines == null ? 0 : _bootLines.Length));
  } else {
    Echo("stage " + DN[_ds]);
    Echo("blocks " + _dProcessed + "/" + _dTotal);
  }
  Echo("");
  Echo("model not published");
  Echo("NO ACTUATION");
  Echo("");
  Echo("instr " + Runtime.CurrentInstructionCount + "/" + Runtime.MaxInstructionCount);
  Echo("peak " + _tickPeak + " (" + _peakWhat + ")");
  Echo("ctor " + _ctorCost);
  if (_err.Length > 0) { Echo(""); Echo("last error: " + _err); }
}

void Command(string a) {
  string low = a.ToLowerInvariant();
  string rest = "";
  int sp = low.IndexOf(' ');
  string verb = low;
  if (sp > 0) { verb = low.Substring(0, sp); rest = low.Substring(sp + 1).Trim(); }
  if (verb == "mode") {
    int m = IdxOf(MODEN, rest);
    if (m < 0) { Echo("unknown mode: " + rest); return; }
    if (m != _mode) {
      Ev("Mode " + MODEN[_mode] + " -> " + MODEN[m]);
      _mode = m;
      // A mode change rewrites every tier, so any restore timer in flight is meaningless.
      _goodSince = -1e9;
    }
    Echo("mode " + MODEN[_mode]);
    Save();
  } else if (verb == "state") {
    if (rest == "auto") { _stateAuto = true; Ev("State back to auto"); }
    else {
      int t = IdxOf(STATEN, rest);
      if (t < 0) { Echo("unknown state: " + rest); return; }
      _stateAuto = false; _state = t; Ev("State forced " + STATEN[t]);
    }
    Save();
  } else if (verb == "scan") {
    Echo(StartReport(rest == "all"));
  } else if (verb == "status") {
    Tick();
  } else if (verb == "shed") {
    // Manual shed is one pass of the SAME engine with the same protection rules. It never
    // bypasses protection; it only bypasses the reserve threshold that would normally gate it.
    int n = DoShed(true);
    Echo(n > 0 ? ("shed " + n + " item(s)") : "nothing sheddable under current policy");
  } else if (verb == "recover") {
    int n = RestoreAll();
    Echo("restored " + n + " item(s)");
  } else if (verb == "rescan") {
    _dirty = true; Echo("rescan queued");
  } else if (verb == "help" || verb == "") {
    Echo("scan [all] (staged over ticks) | status |");
    Echo("mode economy|normal|redalert|emergency |");
    Echo("state auto|flight|docked|landed|construction | shed | recover | rescan");
  } else {
    Echo("unknown command: " + a + " (try help)");
  }
}

// ---------------------------------------------------------------------------------------
// TABLES AND CONFIGURATION
// ---------------------------------------------------------------------------------------
void BuildTables() {
  _builtin = new Dictionary<string, double>(SCI);
  _user = new Dictionary<string, double>(SCI);
  if (_learned == null) _learned = new Dictionary<string, double>(SCI);
  // CATALOG is parsed by the staged boot, not here - 338 entries in a constructor is exactly
  // the kind of fixed cost that terminates a script before Main ever runs.
  //
  // HINTS becomes parallel ARRAYS rather than a dictionary. Two reasons, and neither changes
  // an outcome: iteration order over an array is defined (the old code relied on Dictionary
  // enumeration happening to be insertion order, which is true in practice and guaranteed
  // nowhere), and each key gets a precomputed first-letter bit so most of the 88 candidates
  // are rejected by one bit test instead of a case-insensitive substring search.
  int nh = 0;
  for (int i = 0; i < HINTS.Length; i++) if (HINTS[i].IndexOf('=') > 0) nh++;
  _hintKey = new string[nh]; _hintCat = new int[nh]; _hintBit = new int[nh];
  nh = 0;
  for (int i = 0; i < HINTS.Length; i++) {
    var p = HINTS[i].Split('=');
    if (p.Length != 2) continue;
    int c = IdxOf(CATN, p[1]);
    if (c < 0) continue;
    string k = p[0].ToLowerInvariant();
    _hintKey[nh] = k; _hintCat[nh] = c;
    _hintBit[nh] = k.Length > 0 ? Bit(k[0]) : 0;
    nh++;
  }
  _hintN = nh;
  _gensupLower = new string[GENSUP.Length]; _gensupBit = new int[GENSUP.Length];
  for (int i = 0; i < GENSUP.Length; i++) {
    _gensupLower[i] = GENSUP[i].ToLowerInvariant();
    _gensupBit[i] = _gensupLower[i].Length > 0 ? Bit(_gensupLower[i][0]) : 0;
  }
  _tier = new int[8][];
  for (int i = 0; i < 8; i++) {
    _tier[i] = new int[NCAT];
    for (int c = 0; c < NCAT; c++) _tier[i][c] = T_NORM;
  }
  for (int i = 0; i < POLICY.Length; i++) {
    var row = POLICY[i].Split('|');
    if (row.Length != 3) continue;
    int r = IdxOf(ROLEN, row[0]), m = IdxOf(MODEN, row[1]);
    if (r < 0 || m < 0) continue;
    int[] t = _tier[r * 4 + m];
    var cells = row[2].Split(',');
    for (int j = 0; j < cells.Length; j++) {
      var kv = cells[j].Split('=');
      if (kv.Length != 2) continue;
      int c = IdxOf(CATN, kv[0].Trim());
      if (c < 0) continue;
      // Deliberately no 'P': PROTECTED is a control constraint, not a policy tier. A policy
      // row that could grant protection would make protection mode-dependent, and the whole
      // point of protection is that it is not.
      string v = kv[1].Trim().ToUpperInvariant();
      t[c] = v == "C" ? T_CRIT : v == "E" ? T_ESS : v == "N" ? T_NORM
           : v == "I" ? T_IND : v == "D" ? T_DISC : T_NORM;
    }
  }
}

void ParseConfig() {
  string cd = Me.CustomData == null ? "" : Me.CustomData;
  int cut = cd.IndexOf(RPTMARK, OIC);
  string head = cut >= 0 ? cd.Substring(0, cut) : cd;
  if (!_ini.TryParse(head) || !_ini.ContainsSection(SEC)) {
    WriteDefaultConfig();
    return;
  }
  // Player-supplied rated loads, re-read on every config parse so an edit takes effect on the
  // next rescan without a recompile. Rebuilt from the built-in table each time so a deleted
  // row actually disappears instead of lingering in the dictionary.
  _user.Clear();
  if (_ini.ContainsSection(SECCAT)) {
    _catKeys.Clear();
    _ini.GetKeys(SECCAT, _catKeys);
    for (int i = 0; i < _catKeys.Count; i++) {
      string k = _catKeys[i].Name;
      double v = _ini.Get(SECCAT, k).ToDouble(-1);
      if (v >= 0 && k.Length > 0) _user[k] = v;
    }
  }
  var c = new Cfg();
  c.RoleCfg = _ini.Get(SEC, "Role").ToString(c.RoleCfg);
  c.ModeCfg = _ini.Get(SEC, "Mode").ToString(c.ModeCfg);
  c.StateCfg = _ini.Get(SEC, "State").ToString(c.StateCfg);
  c.AutoShed = _ini.Get(SEC, "AutoShed").ToBoolean(c.AutoShed);
  c.ControlDocked = _ini.Get(SEC, "ControlDockedGrids").ToBoolean(c.ControlDocked);
  c.UpdateSeconds = _ini.Get(SEC, "UpdateSeconds").ToDouble(c.UpdateSeconds);
  c.RescanSeconds = _ini.Get(SEC, "RescanSeconds").ToDouble(c.RescanSeconds);
  c.CautionReserveMW = _ini.Get(SEC, "CautionReserveMW").ToDouble(c.CautionReserveMW);
  c.WarningReserveMW = _ini.Get(SEC, "WarningReserveMW").ToDouble(c.WarningReserveMW);
  c.CriticalReserveMW = _ini.Get(SEC, "CriticalReserveMW").ToDouble(c.CriticalReserveMW);
  c.CautionReservePct = _ini.Get(SEC, "CautionReservePercent").ToDouble(c.CautionReservePct);
  c.WarningReservePct = _ini.Get(SEC, "WarningReservePercent").ToDouble(c.WarningReservePct);
  c.CriticalReservePct = _ini.Get(SEC, "CriticalReservePercent").ToDouble(c.CriticalReservePct);
  c.ShedReserveMW = _ini.Get(SEC, "ShedReserveMW").ToDouble(c.ShedReserveMW);
  c.RestoreReserveMW = _ini.Get(SEC, "RestoreReserveMW").ToDouble(c.RestoreReserveMW);
  c.RecoverySeconds = _ini.Get(SEC, "RecoverySeconds").ToDouble(c.RecoverySeconds);
  c.RestoreStepSeconds = _ini.Get(SEC, "RestoreStepSeconds").ToDouble(c.RestoreStepSeconds);
  c.MaxShedPerCycle = _ini.Get(SEC, "MaxShedPerCycle").ToInt32(c.MaxShedPerCycle);
  c.RequireStress = _ini.Get(SEC, "RequireStressToShed").ToBoolean(c.RequireStress);
  c.StressHoldSeconds = _ini.Get(SEC, "StressHoldSeconds").ToDouble(c.StressHoldSeconds);
  c.RiskPromoteSeconds =
    _ini.Get(SEC, "CapacityRiskPromoteSeconds").ToDouble(c.RiskPromoteSeconds);
  c.RiskRecoverSeconds =
    _ini.Get(SEC, "CapacityRiskRecoverSeconds").ToDouble(c.RiskRecoverSeconds);
  c.BattStressMW = _ini.Get(SEC, "BattStressMW").ToDouble(c.BattStressMW);
  c.DeclineConsistency =
    _ini.Get(SEC, "DeclineConsistency").ToDouble(c.DeclineConsistency);
  c.MinDeclineMWh = _ini.Get(SEC, "MinDeclineMWh").ToDouble(c.MinDeclineMWh);
  c.BattRecoverFrac = _ini.Get(SEC, "BattRecoverFraction").ToDouble(c.BattRecoverFrac);
  c.StressRecoverSeconds =
    _ini.Get(SEC, "StressRecoverSeconds").ToDouble(c.StressRecoverSeconds);
  c.ShedSettleSeconds = _ini.Get(SEC, "ShedSettleSeconds").ToDouble(c.ShedSettleSeconds);
  c.CountBatteryDischarge =
    _ini.Get(SEC, "CountBatteryDischarge").ToString(c.CountBatteryDischarge);
  c.HistorySeconds = _ini.Get(SEC, "HistorySeconds").ToDouble(c.HistorySeconds);
  c.HistorySamples = _ini.Get(SEC, "HistorySamples").ToInt32(c.HistorySamples);
  c.EventLines = _ini.Get(SEC, "EventLines").ToInt32(c.EventLines);
  c.ScanChunk = _ini.Get(SEC, "ScanChunk").ToInt32(c.ScanChunk);
  c.InstrBudgetPercent = _ini.Get(SEC, "InstrBudgetPercent").ToInt32(c.InstrBudgetPercent);
  // The restore threshold must sit strictly above the shed threshold or the two decisions
  // share an edge and the load flaps on it. Widening it here is preferable to trusting a
  // hand-edited config, because the failure it prevents is the one the player will blame the
  // script for.
  if (c.RestoreReserveMW <= c.ShedReserveMW) c.RestoreReserveMW = c.ShedReserveMW + 5;
  if (c.HistorySamples < 10) c.HistorySamples = 10;
  if (c.HistorySamples > 720) c.HistorySamples = 720;
  if (c.MaxShedPerCycle < 1) c.MaxShedPerCycle = 1;
  if (c.ScanChunk < 5) c.ScanChunk = 5;
  if (c.UpdateSeconds < 0.5) c.UpdateSeconds = 0.5;
  _c = c;

  int r = IdxOf(ROLEN, c.RoleCfg);
  _roleAuto = r < 0;
  if (!_roleAuto) _role = r;
  int m = IdxOf(MODEN, c.ModeCfg);
  // Mode in Custom Data is the STARTUP mode only. A `mode` command during an incident is an
  // operator decision and must not be silently reverted by the next config reparse, so the
  // config mode is applied once, at construction, and never again.
  if (m >= 0 && _runs == 0) _mode = m;
  int st = IdxOf(STATEN, c.StateCfg);
  if (st >= 0 && _runs == 0) { _state = st; _stateAuto = false; }
}

void WriteDefaultConfig() {
  string cd = Me.CustomData == null ? "" : Me.CustomData;
  if (cd.IndexOf("[" + SEC + "]", OIC) >= 0) return; // malformed, not missing: do not clobber
  _sb.Clear();
  _sb.Append("[").Append(SEC).Append("]\n");
  _sb.Append("Role=Auto\nMode=Normal\nState=Auto\nAutoShed=true\nControlDockedGrids=true\n");
  _sb.Append("UpdateSeconds=1\nRescanSeconds=30\n");
  _sb.Append("CautionReserveMW=15\nWarningReserveMW=8\nCriticalReserveMW=0\n");
  _sb.Append("CautionReservePercent=20\nWarningReservePercent=10\nCriticalReservePercent=2\n");
  _sb.Append("ShedReserveMW=4\nRestoreReserveMW=20\nCountBatteryDischarge=Auto\n");
  _sb.Append("RequireStressToShed=true\nStressHoldSeconds=10\n");
  _sb.Append("BattStressMW=0.5\nDeclineConsistency=0.5\nMinDeclineMWh=0.002\n");
  _sb.Append("CapacityRiskPromoteSeconds=2\nCapacityRiskRecoverSeconds=15\n");
  _sb.Append("BattRecoverFraction=0.5\nStressRecoverSeconds=15\nShedSettleSeconds=5\n");
  _sb.Append("RecoverySeconds=30\nRestoreStepSeconds=10\nMaxShedPerCycle=4\n");
  _sb.Append("HistorySeconds=10\nHistorySamples=360\nEventLines=40\n");
  _sb.Append("ScanChunk=40\nInstrBudgetPercent=60\n");
  Me.CustomData = _sb.ToString() + (cd.Length > 0 ? "\n" + cd : "");
  _sb.Clear();
}

// ---------------------------------------------------------------------------------------
// DISCOVERY AND TOPOLOGY
// ---------------------------------------------------------------------------------------

// ---------------------------------------------------------------------------------------
// STAGED DISCOVERY. Resumable, budget-aware, and atomically published.
//
// The live model stays authoritative for the whole pass. Nothing outside these functions ever
// sees the _s* staging lists, and publication is a reference swap - so there is no instant at
// which a half-built _bi is the model anyone reads. On a first start there is simply no model,
// and BOTH actuators refuse to run until one exists.
//
// Stage order is dictated by data dependencies, not convenience: grids must be counted before
// constructs can be grouped, and constructs must exist before a block can be told which one it
// belongs to.
// ---------------------------------------------------------------------------------------
void StartDiscovery() {
  _sAll.Clear();
  GridTerminalSystem.GetBlocks(_sAll);
  _dTotal = _sAll.Count; _dIdx = 0; _dProcessed = 0;
  _sBi = new List<BI>(); _sProd = new List<PI>(); _sBats = new List<BAT>();
  _sConn = new List<IMyShipConnector>(); _sCtrls = new List<IMyShipController>();
  _sCons = new List<CON>(); _sGridCon = new Dictionary<long, int>();
  _sReps = new Dictionary<long, IMyTerminalBlock>(); _sCounts = new Dictionary<long, int>();
  _sConRep = new List<IMyTerminalBlock>(); _sConBest = new List<int>();
  _sGridIds = new List<long>();
  _sScreens = new List<IMyTextSurface>(); _sScreenPage = new List<string>();
  // Value carry-forward is taken from the LIVE model, which is still intact and still correct.
  _prev.Clear();
  for (int i = 0; i < _bi.Count; i++) _prev[_bi[i].Id] = _bi[i];
  _ds = D_GRIDS;
}

int DiscoverStep() {
  int did = 0;
  if (_ds == D_IDLE) return 0;
  if (_ds == D_FETCH) { StartDiscovery(); return 1; }

  if (_ds == D_GRIDS) {
    while (_dIdx < _sAll.Count) {
      var b = _sAll[_dIdx++]; did++;
      if (b == null || b.CubeGrid == null) continue;
      long g = b.CubeGrid.EntityId;
      int n;
      if (_sCounts.TryGetValue(g, out n)) _sCounts[g] = n + 1;
      else { _sReps[g] = b; _sCounts[g] = 1; _sGridIds.Add(g); }
      if (Over()) { _yielded = true; return did; }
    }
    _dIdx = 0; _ds = D_CONSTRUCTS;
    return did;
  }

  if (_ds == D_CONSTRUCTS) {
    // O(grids x constructs), and grids are few next to blocks - but a piston tower made 116
    // grids on the dev base alone, so it is chunked like everything else.
    while (_dIdx < _sGridIds.Count) {
      long gid = _sGridIds[_dIdx++]; did++;
      var rep = _sReps[gid];
      int idx = -1;
      for (int c = 0; c < _sConRep.Count; c++)
        if (rep.IsSameConstructAs(_sConRep[c])) { idx = c; break; }
      if (idx < 0) {
        idx = _sConRep.Count;
        _sConRep.Add(rep); _sConBest.Add(-1);
        _sCons.Add(new CON { Key = 0, Name = rep.CubeGrid.CustomName });
      }
      _sGridCon[gid] = idx;
      _sCons[idx].Grids++;
      // Named after its LARGEST grid: a rover docked to a station must not rename the station
      // because its rotor subgrid happened to be enumerated first.
      if (_sCounts[gid] > _sConBest[idx]) {
        _sConBest[idx] = _sCounts[gid];
        _sCons[idx].Name = rep.CubeGrid.CustomName;
      }
      if (Over()) { _yielded = true; return did; }
    }
    _dIdx = 0; _ds = D_BLOCKS;
    return did;
  }

  if (_ds == D_BLOCKS) {
    while (_dIdx < _sAll.Count) {
      var b = _sAll[_dIdx++]; did++; _dProcessed++;
      if (b == null || b.CubeGrid == null) continue;
      int con = SConOf(b.CubeGrid);
      if (con >= 0) _sCons[con].Blocks++;
      CollectScreen(b);
      var conn = b as IMyShipConnector;
      if (conn != null) _sConn.Add(conn);
      var ctl = b as IMyShipController;
      if (ctl != null) _sCtrls.Add(ctl);
      var bat = b as IMyBatteryBlock;
      if (bat != null) {
        _sBats.Add(new BAT { Bat = bat, Id = bat.EntityId, Con = con });
      } else {
        var pp = b as IMyPowerProducer;
        if (pp != null) {
          PI was;
          bool had = _prodPrev.TryGetValue(pp.EntityId, out was);
          string pdef = DefOf(pp);
          bool env = pp is IMySolarPanel || pdef.IndexOf("Solar", OIC) >= 0 ||
                     pdef.IndexOf("Wind", OIC) >= 0;
          _sProd.Add(new PI { P = pp, Id = pp.EntityId, Con = con, Name = pp.CustomName,
            WasWorking = had ? was.WasWorking : pp.IsWorking,
            LastMax = had ? was.LastMax : pp.MaxOutput,
            Proven = had ? was.Proven : SavedProven(pp.EntityId), Env = env });
        }
      }
    var f = b as IMyFunctionalBlock;
    if (f == null || !b.IsFunctional) continue;
    var r = new BI { B = b, F = f, Id = b.EntityId, GridId = b.CubeGrid.EntityId, Con = con };
    var d = b.BlockDefinition;
    r.Sub = d.SubtypeId == null ? "" : d.SubtypeId;
    r.Def = d.TypeId.ToString() + "/" + r.Sub;
    Classify(r);
    ReadBlockConfig(r);
    BI old;
    if (_prev.TryGetValue(r.Id, out old)) {
      // Brownout age carries across the rescan UNCONDITIONALLY - proving the block still
      // exists after a topology refresh is the entire point of keeping it.
      r.BrownSince = old.BrownSince;
      if (r.Src == SRC_NONE && old.Src != SRC_NONE) {
        r.MaxIn = old.MaxIn; r.CurIn = old.CurIn; r.Src = old.Src; r.HasCur = old.HasCur;
      }
    }
    if (r.Src == SRC_NONE) {
      // Seed from the catalogs at construction rather than waiting for the chunk cursor to
      // reach this block, so a definition seen before is known from its first cycle back.
      double sv;
      if (Lookup(_user, r, out sv)) { r.MaxIn = sv; r.Src = SRC_CATALOG; }
      else if (Lookup(_builtin, r, out sv)) { r.MaxIn = sv; r.Src = SRC_BUILTIN; }
      else if (_learned.TryGetValue(r.Def, out sv)) { r.MaxIn = sv; r.Src = SRC_LEARNED; }
    }
    if (bat != null) { r.Cat = C_BATT; r.MaxIn = bat.MaxInput; r.Src = SRC_IFACE; }
    else r.IsProducer = b is IMyPowerProducer;
    _sBi.Add(r);
      if (Over()) { _yielded = true; return did; }
    }
    _ds = D_PUBLISH;
    return did;
  }

  // PUBLISH. Everything that needs the whole model, then the swap.
  AssignConstructIdsStaged();
  MarkDockedStaged();
  if (_runs > 1) {
    var liveProd = new HashSet<long>();
    for (int i = 0; i < _sProd.Count; i++) liveProd.Add(_sProd[i].Id);
    foreach (var kv in _prodPrev)
      if (!liveProd.Contains(kv.Key))
        Ev("GENERATOR REMOVED " + Trim(kv.Value.Name) + " -" + MW(kv.Value.LastMax));
  }
  _prodPrev.Clear();
  for (int i = 0; i < _sProd.Count; i++) _prodPrev[_sProd[i].Id] = _sProd[i];
  _newCons.Clear();
  for (int i = 0; i < _sCons.Count; i++) {
    if (_knownCon.Contains(_sCons[i].Key)) continue;
    _sCons[i].IsNew = true;
    if (_modelReady && !_sCons[i].Home) _newCons.Add(i);
  }
  var live = new HashSet<long>();
  for (int i = 0; i < _sCons.Count; i++) {
    live.Add(_sCons[i].Key);
    _conName[_sCons[i].Key] = _sCons[i].Name;
  }
  foreach (var k in _knownCon) {
    if (live.Contains(k) || k == _homeKey) continue;
    string gn;
    Ev("Grid disconnected " + (_conName.TryGetValue(k, out gn) ? gn : "?"));
  }
  _knownCon = live;

  // THE SWAP. A reference assignment per collection: from the reader's point of view the model
  // changes between ticks, never during one.
  _all.Clear(); _all.AddRange(_sAll);
  _bi = _sBi; _prod = _sProd; _bats = _sBats; _conn = _sConn; _ctrls = _sCtrls;
  _cons = _sCons; _gridCon = _sGridCon;
  _screens = _sScreens; _screenPage = _sScreenPage;
  _scanCursor = 0;
  _lastScan = _now;
  _ds = D_IDLE;
  if (!_modelReady) {
    _modelReady = true;
    Ev("Model published: " + _bi.Count + " blocks, " + _cons.Count + " construct(s) - "
       + "control active");
  }
  return did;
}

int SConOf(IMyCubeGrid g) {
  if (g == null) return -1;
  int i;
  return _sGridCon.TryGetValue(g.EntityId, out i) ? i : -1;
}

// Identity by overlap with the previous PUBLISHED model - see the note on AssignConstructIds.
void AssignConstructIdsStaged() {
  int home = -1;
  for (int i = 0; i < _sAll.Count && home < 0; i++) {
    var b = _sAll[i];
    if (b != null && b.EntityId == Me.EntityId) home = SConOf(b.CubeGrid);
  }
  if (home < 0 && Me.CubeGrid != null) home = SConOf(Me.CubeGrid);
  if (home >= 0) _sCons[home].Home = true;
  _claimed.Clear();
  if (home >= 0) ClaimStaged(home);
  for (int i = 0; i < _sCons.Count; i++) if (_sCons[i].Key == 0) ClaimStaged(i);
  for (int i = 0; i < _sCons.Count; i++) {
    if (_sCons[i].Key != 0) continue;
    _sCons[i].Key = ++_conSeq;
    _claimed.Add(_sCons[i].Key);
  }
  _gridConId.Clear();
  foreach (var kv in _sGridCon) _gridConId[kv.Key] = _sCons[kv.Value].Key;
  if (home >= 0) _homeKey = _sCons[home].Key;
}

void ClaimStaged(int idx) {
  foreach (var kv in _sGridCon) {
    if (kv.Value != idx) continue;
    long prior;
    if (!_gridConId.TryGetValue(kv.Key, out prior) || _claimed.Contains(prior)) continue;
    _sCons[idx].Key = prior;
    _claimed.Add(prior);
    return;
  }
}

void MarkDockedStaged() {
  for (int i = 0; i < _sConn.Count; i++) {
    var c = _sConn[i];
    if (c.Status != MyShipConnectorStatus.Connected) continue;
    var o = c.OtherConnector;
    if (o == null || o.CubeGrid == null) continue;
    int a = SConOf(c.CubeGrid), b = SConOf(o.CubeGrid);
    if (a < 0 || b < 0 || a == b) continue;
    if (_sCons[a].Home) _sCons[b].Docked = true;
    else if (_sCons[b].Home) _sCons[a].Docked = true;
    else { _sCons[a].Docked = true; _sCons[b].Docked = true; }
  }
}

// Folded into the block pass. It used to be a SEPARATE full walk of every block after every
// discovery, doing one case-insensitive search each - an entire O(n) pass bought for nothing.
void CollectScreen(IMyTerminalBlock b) {
  if (b.EntityId == Me.EntityId) {
    var pbs = Me as IMyTextSurfaceProvider;
    if (pbs != null && pbs.SurfaceCount > 0) {
      var s0 = pbs.GetSurface(0);
      if (s0 != null) { PrepSurface(s0); _sScreens.Add(s0); _sScreenPage.Add("main"); }
    }
    return;
  }
  string nm = b.CustomName == null ? "" : b.CustomName;
  int at = nm.IndexOf(TAG_LCD_STEM, OIC);
  if (at < 0) return;
  string page = "main";
  int close = nm.IndexOf(']', at);
  if (close > at) {
    string tok = nm.Substring(at + 1, close - at - 1);
    int c3 = tok.IndexOf(':');
    if (c3 > 0) page = tok.Substring(c3 + 1).Trim().ToLowerInvariant();
  }
  var prov = b as IMyTextSurfaceProvider;
  if (prov == null) return;
  for (int k = 0; k < prov.SurfaceCount; k++) {
    var sf = prov.GetSurface(k);
    if (sf == null) continue;
    PrepSurface(sf);
    _sScreens.Add(sf); _sScreenPage.Add(page);
    if (!(b is IMyTextPanel)) break;
  }
}


// Identity by overlap with the previous scan, not by any property of the grids themselves.
// Home goes first: it is identified by containing Me, not by a key, so it can always reclaim
// its own id and a departing subgrid can never carry the base's identity off with it.
void AssignConstructIds(int home) {
  _claimed.Clear();
  if (home >= 0) ClaimId(home);
  for (int i = 0; i < _cons.Count; i++) if (_cons[i].Key == 0) ClaimId(i);
  for (int i = 0; i < _cons.Count; i++) {
    if (_cons[i].Key != 0) continue;
    _cons[i].Key = ++_conSeq;           // genuinely new: nothing it contains was here before
    _claimed.Add(_cons[i].Key);
  }
  _gridConId.Clear();
  foreach (var kv in _gridCon) _gridConId[kv.Key] = _cons[kv.Value].Key;
  if (home >= 0) _homeKey = _cons[home].Key;
}

void ClaimId(int idx) {
  foreach (var kv in _gridCon) {
    if (kv.Value != idx) continue;
    long prior;
    if (!_gridConId.TryGetValue(kv.Key, out prior) || _claimed.Contains(prior)) continue;
    _cons[idx].Key = prior;
    _claimed.Add(prior);
    return;
  }
}


double SavedProven(long id) {
  double v;
  return _provenSaved.TryGetValue(id, out v) ? v : 0;
}

int ConOf(IMyCubeGrid g) {
  if (g == null) return -1;
  int i;
  return _gridCon.TryGetValue(g.EntityId, out i) ? i : -1;
}

// ---------------------------------------------------------------------------------------
// CLASSIFICATION
// ---------------------------------------------------------------------------------------
// Interfaces first - they are exact and free. The substring table only ever sees blocks no
// interface claimed, which on an IO base is the IO machinery itself.
void Classify(BI r) {
  var b = r.B;
  // Generation support first. A steam well head presents as an oxygen generator and a steam
  // tank as an oxygen tank; taking the interface answer would file both under Fuel and leave
  // the plant that feeds a 50 MW turbine sheddable at Warning.
  // Lowercase once, then ORDINAL searches. A case-insensitive IndexOf is far more expensive
  // than an ordinal one, and with both sides lowercased the two are equivalent for the ASCII
  // subtype and block names this matches against. The letter mask then rejects most candidates
  // with a single bit test: a needle can only occur in a haystack that contains the needle's
  // first letter, so the prefilter is exact and cannot change an outcome.
  string lsub = r.Sub.ToLowerInvariant();
  int msub = Mask(lsub);
  for (int i = 0; i < _gensupLower.Length; i++) {
    if ((msub & _gensupBit[i]) == 0) continue;
    if (lsub.IndexOf(_gensupLower[i], StringComparison.Ordinal) < 0) continue;
    r.Cat = C_GEN;
    return;
  }
  if (b is IMyAirVent || b is IMyOxygenFarm || b is IMyMedicalRoom) r.Cat = C_LIFE;
  else if (b is IMyShipController || b is IMyProgrammableBlock || b is IMyTimerBlock) r.Cat = C_CTRL;
  else if (b is IMyThrust || b is IMyGyro || b is IMyMotorSuspension) r.Cat = C_PROP;
  else if (b is IMyBatteryBlock) r.Cat = C_BATT;
  else if (b is IMyPowerProducer) r.Cat = C_GEN;
  else if (b is IMyShipConnector || b is IMyLandingGear || b is IMyShipMergeBlock) r.Cat = C_DOCK;
  else if (b is IMyUserControllableGun) r.Cat = C_WEAP;
  else if (b is IMySensorBlock || b is IMyCameraBlock || b is IMyOreDetector) r.Cat = C_SENS;
  else if (b is IMyRadioAntenna || b is IMyLaserAntenna || b is IMyBeacon ||
           b is IMyBroadcastControllerBlock) r.Cat = C_COMM;
  else if (b is IMyJumpDrive) r.Cat = C_JUMP;
  else if (b is IMyRefinery || b is IMyAssembler) r.Cat = C_PROD;
  else if (b is IMyShipDrill || b is IMyShipWelder || b is IMyShipGrinder) r.Cat = C_IND;
  else if (b is IMyGasGenerator || b is IMyGasTank) r.Cat = C_FUEL;
  else if (b is IMyMotorBase || b is IMyPistonBase) r.Cat = C_MECH;
  else if (b is IMyConveyorSorter || b is IMyProjector || b is IMyGravityGeneratorBase) r.Cat = C_UTIL;
  else if (b is IMyLightingBlock || b is IMyDoor || b is IMyTextPanel) r.Cat = C_DECOR;
  else {
    // FIRST MATCH IN HINTS ORDER WINS - unchanged, but now guaranteed rather than inherited
    // from Dictionary enumeration happening to preserve insertion order. Several hints overlap
    // ("press" is a substring of "compressor"), so which one wins is a real decision and it
    // should not depend on hash layout.
    r.Cat = C_UNK;
    for (int i = 0; i < _hintN; i++) {
      if ((msub & _hintBit[i]) == 0) continue;
      if (lsub.IndexOf(_hintKey[i], StringComparison.Ordinal) < 0) continue;
      r.Cat = _hintCat[i];
      break;
    }
    if (r.Cat == C_UNK) {
      string lnm = (b.CustomName == null ? "" : b.CustomName).ToLowerInvariant();
      int mnm = Mask(lnm);
      for (int i = 0; i < _hintN; i++) {
        if ((mnm & _hintBit[i]) == 0) continue;
        if (lnm.IndexOf(_hintKey[i], StringComparison.Ordinal) < 0) continue;
        r.Cat = _hintCat[i];
        break;
      }
    }
  }
}

// Block-level override. Both a name tag and a [PowerControl] section in the block's Custom
// Data are accepted: the tag survives a Custom Data wipe, the INI section carries more than
// one setting, and supporting both costs one substring check.
void ReadBlockConfig(BI r) {
  string nm = r.B.CustomName == null ? "" : r.B.CustomName;
  if (nm.IndexOf(TAG_PROT, OIC) >= 0) r.CfgProt = true;
  if (nm.IndexOf(TAG_SHED, OIC) >= 0) r.CfgShed = true;
  if (nm.IndexOf(TAG_IGN, OIC) >= 0) r.Ignore = true;
  string cd = r.B.CustomData;
  if (string.IsNullOrEmpty(cd) || cd.IndexOf(SEC, OIC) < 0) return;
  if (!_bini.TryParse(cd) || !_bini.ContainsSection(SEC)) return;
  if (_bini.Get(SEC, "Protected").ToBoolean(false)) r.CfgProt = true;
  if (_bini.Get(SEC, "Sheddable").ToBoolean(false)) r.CfgShed = true;
  if (_bini.Get(SEC, "Ignore").ToBoolean(false)) r.Ignore = true;
  string cat = _bini.Get(SEC, "Category").ToString("");
  if (cat.Length > 0) {
    int c = IdxOf(CATN, cat);
    if (c >= 0) r.Cat = c;
  }
  double mw = _bini.Get(SEC, "MaxLoadMW").ToDouble(-1);
  if (mw >= 0) { r.MaxIn = mw; r.Src = SRC_CFG; }
}

// ---------------------------------------------------------------------------------------
// POTENTIAL-DEMAND MODEL. Tiers, best first. Nothing here guesses: a block that reaches the
// bottom is reported as unknown rather than given a plausible number.
// ---------------------------------------------------------------------------------------
void RefreshDetailChunk() {
  if (_bi.Count == 0) return;
  int budget = Ceiling();
  int done = 0;
  for (int n = 0; n < _bi.Count && done < _c.ScanChunk; n++) {
    // CHECKED FIRST. As the last statement in the body, the iteration that crossed the line was
    // still paid in full on top of the budget - and with the old scan override at 90% of the
    // hard limit, that overshoot is exactly how a diagnostic ran the script off the cliff.
    if (Runtime.CurrentInstructionCount > budget) break;
    if (_scanCursor >= _bi.Count) _scanCursor = 0;
    var r = _bi[_scanCursor++];
    done++;
    if (r.B == null) continue;
    var bat = r.B as IMyBatteryBlock;
    if (bat != null) { r.MaxIn = bat.MaxInput; r.CurIn = bat.CurrentInput; r.Src = SRC_IFACE; continue; }
    if (r.Src == SRC_CFG) continue;
    if (r.IsProducer) { r.MaxIn = 0; r.CurIn = 0; r.Src = SRC_IFACE; continue; }
    double cur, max;
    bool parsed = ParseDetail(r.B, out cur, out max);
    if (parsed) { r.CurIn = cur; r.HasCur = true; }
    // Overrides whatever the parse said, and whatever was there before it: off is off.
    if (r.F != null && !r.F.Enabled) { r.CurIn = 0; r.HasCur = true; }
    // Learning happens regardless of which tier ends up winning: a figure witnessed from a
    // working block is worth keeping for its idle siblings whatever else we know.
    if (parsed && max > 0) {
      double prev;
      if (!_learned.TryGetValue(r.Def, out prev) || max > prev) _learned[r.Def] = max;
    }
    // PRECEDENCE, in one place:
    //   user catalog  - measured deliberately by the operator, outranks everything
    //   DetailedInfo  - the running block describing itself, upgrades and damage included
    //   built-in      - the mod's definition-time rating, for blocks that publish nothing
    //   learned       - witnessed on a sibling of the same definition
    //   a parsed zero - the block published an Input line and it read zero: KNOWN, not unknown
    // A measured figure never LOWERS a rating: an idle machine publishing zero says what it
    // draws now, and MaxIn answers what it could draw.
    double best = 0;
    int bsrc = SRC_NONE;
    double v;
    if (Lookup(_user, r, out v)) { best = v; bsrc = SRC_CATALOG; }
    else if (parsed && max > 0) { best = max; bsrc = SRC_DETAIL; }
    else if (Lookup(_builtin, r, out v)) { best = v; bsrc = SRC_BUILTIN; }
    else if (_learned.TryGetValue(r.Def, out v)) { best = v; bsrc = SRC_LEARNED; }
    else if (parsed) { best = 0; bsrc = SRC_DETAIL; }
    r.MaxIn = best; r.Src = bsrc;
  }
}

// Catalog lookup by SubtypeId first, then by the full TypeId/SubtypeId. The empty-subtype
// guard is not theoretical: the live test grid has a MyObjectBuilder_OxygenTank/ with no
// subtype, and an empty key would match an empty catalog row and rate every such block.
bool Lookup(Dictionary<string, double> d, BI r, out double v) {
  v = 0;
  if (r.Sub.Length > 0 && d.TryGetValue(r.Sub, out v)) return true;
  return d.TryGetValue(r.Def, out v);
}

// DetailedInfo is a localized, free-form string. This parse is deliberately narrow: it looks
// for a line whose label mentions "input", takes the number and unit after the colon, and
// rejects anything measured in Wh (stored energy, not draw). If the server is not running in
// English this returns nothing, every block falls through to unknown, and the coverage figure
// says so - which is the correct failure, not a wrong number.
bool ParseDetail(IMyTerminalBlock b, out double cur, out double max) {
  cur = 0; max = 0;
  string di = b.DetailedInfo;
  if (string.IsNullOrEmpty(di) || di.IndexOf("Input", OIC) < 0) return false;
  bool any = false;
  var lines = di.Split('\n');
  for (int i = 0; i < lines.Length; i++) {
    string ln = lines[i];
    int c = ln.IndexOf(':');
    if (c <= 0 || c >= ln.Length - 1) continue;
    string label = ln.Substring(0, c);
    if (label.IndexOf("Input", OIC) < 0) continue;
    double v;
    if (!ParseMW(ln.Substring(c + 1), out v)) continue;
    any = true;
    if (label.IndexOf("Max", OIC) >= 0) { if (v > max) max = v; }
    else cur = v;
  }
  if (max <= 0 && cur > 0) max = cur;
  return any;
}

// "  1.20 MW" -> 1.20. Also kW, GW and bare W. "MWh" is rejected by the caller's unit test
// because stored energy is not a rate.
bool ParseMW(string s, out double mw) {
  mw = 0;
  s = s.Trim();
  if (s.Length == 0) return false;
  int i = 0;
  while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',' || s[i] == '-' ||
         s[i] == '+')) i++;
  if (i == 0) return false;
  string num = s.Substring(0, i).Replace(",", "");
  string unit = s.Substring(i).Trim();
  double v;
  if (!double.TryParse(num, out v)) return false;
  if (unit.IndexOf("Wh", OIC) >= 0) return false;
  double k = 1;
  if (unit.StartsWith("GW", OIC)) k = 1000;
  else if (unit.StartsWith("MW", OIC)) k = 1;
  else if (unit.StartsWith("kW", OIC)) k = 0.001;
  else if (unit.StartsWith("W", OIC)) k = 0.000001;
  else return false;
  mw = v * k;
  // A NEGATIVE figure is a sign convention for "consumes", not a negative draw - BuildInfo
  // shows a programmable block as "max power: -500 W". Nothing in the DetailedInfo this
  // script can actually read has been seen doing it, but a negative CurIn would drive a
  // negative per-grid load estimate and a negative relief, so the magnitude is taken and the
  // sign discarded rather than trusted either way.
  if (mw < 0) mw = -mw;
  return true;
}

// ---------------------------------------------------------------------------------------
// ROLE AND STATE
// ---------------------------------------------------------------------------------------
// Several weak signals combined, not one fragile heuristic. A static grid with no thrusters
// is unambiguously a station; a mobile grid with thrusters and a cockpit is unambiguously a
// ship; everything between is decided on the balance of evidence and can be overridden.
void DetectRole() {
  if (!_roleAuto) return;
  int score = 0;
  bool isStatic = Me.CubeGrid != null && Me.CubeGrid.IsStatic;
  if (isStatic) score -= 3;
  int thr = 0, gyr = 0, ctl = 0;
  for (int i = 0; i < _bi.Count; i++) {
    var r = _bi[i];
    if (r.Con < 0 || !_cons[r.Con].Home) continue;
    if (r.B is IMyThrust) thr++;
    else if (r.B is IMyGyro) gyr++;
    else if (r.B is IMyShipController) ctl++;
  }
  if (thr > 0) score += 2;
  if (thr >= 6) score += 1;
  if (gyr > 0) score += 2;
  if (ctl > 0) score += 1;
  if (!isStatic) score += 2;
  int want = score > 0 ? R_SHIP : R_STATION;
  if (want != _role) {
    Ev("Role " + ROLEN[_role] + " -> " + ROLEN[want] + " (auto)");
    _role = want;
  }
}

// A ship stays a ship when docked - role and state are different questions. CONSTRUCTION is
// not auto-detected: the PB API exposes no reliable "this grid is being built" signal, and
// inventing one would be worse than asking the operator to say so with `state construction`.
void DetectState() {
  if (!_stateAuto) return;
  int want = S_UNKNOWN;
  if (_role == R_STATION) want = S_STATIC;
  else {
    bool docked = false;
    for (int i = 0; i < _conn.Count; i++) {
      var c = _conn[i];
      if (c.Status != MyShipConnectorStatus.Connected) continue;
      int cc = ConOf(c.CubeGrid);
      if (cc >= 0 && _cons[cc].Home) { docked = true; break; }
    }
    double speed = 0;
    bool haveSpeed = false, underControl = false;
    for (int i = 0; i < _ctrls.Count; i++) {
      var c = _ctrls[i];
      int cc = ConOf(c.CubeGrid);
      if (cc < 0 || !_cons[cc].Home) continue;
      haveSpeed = true;
      double s = c.GetShipSpeed();
      if (s > speed) speed = s;
      if (c.IsUnderControl) underControl = true;
    }
    bool gearLocked = false;
    for (int i = 0; i < _bi.Count; i++) {
      var g = _bi[i].B as IMyLandingGear;
      if (g == null || _bi[i].Con < 0 || !_cons[_bi[i].Con].Home) continue;
      if (g.IsLocked) { gearLocked = true; break; }
    }
    if (docked) want = S_DOCKED;
    else if (haveSpeed && speed > 1.0) want = S_FLIGHT;
    else if (gearLocked) want = S_LANDED;
    else if (underControl) want = S_FLIGHT;
    else if (haveSpeed) want = S_LANDED;
  }
  if (want != _state) {
    Ev("State " + STATEN[_state] + " -> " + STATEN[want]);
    _state = want;
  }
}

// ---------------------------------------------------------------------------------------
// MEASUREMENT
// ---------------------------------------------------------------------------------------
double _prevDemand = -1;
double _demandRef;   // high-water reference for spike reporting, slowly decaying
void Measure() {
  for (int i = 0; i < _cons.Count; i++) {
    var c = _cons[i];
    c.GenCur = 0; c.GenMax = 0; c.LoadEst = 0; c.PotLoad = 0; c.BattCount = 0;
    c.BattIn = 0; c.BattMaxIn = 0; c.BattStored = 0; c.BattMaxStored = 0; c.BattOut = 0;
    c.Unknowns = 0;
  }
  _genCur = 0; _genMax = 0; _genProven = 0; _genCred = 0;
  _largestProd = 0; _largestProdName = "-"; _fuelAtCeiling = true;
  for (int i = 0; i < _prod.Count; i++) {
    var p = _prod[i];
    if (p.P == null) continue;
    bool working = p.P.IsWorking;
    // A generator that stopped is the leading indicator of the incident this script exists to
    // survive, so it is an event in its own right, with the capacity it took with it.
    if (working != p.WasWorking) {
      Ev(working ? ("Generator returned " + Trim(p.Name) + " +" + MW(p.P.MaxOutput))
                 : ("GENERATOR OFFLINE " + Trim(p.Name) + " -" + MW(p.LastMax)));
      // A producer that stopped and came back may have come back on a different fuel or steam
      // supply, so what it proved beforehand is no longer evidence about what it can do now.
      // Make it prove itself again - but ONLY if we witnessed the stop. On a world reload a
      // block can read not-working for a moment before it settles, and treating that as a
      // genuine stop would discard the figures just restored from Storage.
      if (working && p.SeenWorking) p.Proven = 0;
      p.WasWorking = working;
    }
    if (!working) continue;
    p.SeenWorking = true;
    double cur = p.P.CurrentOutput, mx = p.P.MaxOutput;
    p.LastMax = mx;
    if (cur > p.Proven) p.Proven = cur;
    // CREDIBLE: what there is EVIDENCE we can call on. Environmental producers are believed
    // at their MaxOutput because that figure is already a capability; everything else -
    // fuel-fed, or merely unrecognised - is credited only what it has been witnessed to
    // deliver. Treating an unrecognised producer as fuel-fed is the conservative reading.
    double cred = p.Env ? mx : (p.Proven > cur ? p.Proven : cur);
    if (cred > mx) cred = mx;              // never credit more than the block is rated for
    _genCur += cur; _genMax += mx; _genProven += p.Proven; _genCred += cred;
    // Is every FUEL-FED producer pinned at what it has demonstrated? Environmental producers
    // are excluded: a solar panel sitting at its current MaxOutput is normal, not a symptom.
    if (!p.Env && cur < cred - 0.1) _fuelAtCeiling = false;
    // N-1 asks what losing the largest CREDIBLE contribution would cost. Sizing that by
    // nameplate would subtract capacity the base never had.
    if (cred > _largestProd) { _largestProd = cred; _largestProdName = p.Name; }
    if (p.Con >= 0) { _cons[p.Con].GenCur += cur; _cons[p.Con].GenMax += mx; }
  }
  _battStored = 0; _battMaxStored = 0; _battIn = 0; _battOut = 0; _battMaxIn = 0;
  _battNetOut = 0; _battNetFlow = 0;
  for (int i = 0; i < _bats.Count; i++) {
    var b = _bats[i].Bat;
    if (b == null || !b.IsFunctional) continue;
    _battStored += b.CurrentStoredPower; _battMaxStored += b.MaxStoredPower;
    _battIn += b.CurrentInput; _battOut += b.CurrentOutput; _battMaxIn += b.MaxInput;
    // NET per battery, not netted across the bank: one battery charging while another
    // discharges is real power moving between them, not circulation inside one block.
    double bnet = b.CurrentOutput - b.CurrentInput;
    _battNetFlow += bnet;                  // signed: + discharging, - charging
    if (bnet > 0) _battNetOut += bnet;     // clamped per battery: what demand and stress use
    int ci = _bats[i].Con;
    if (ci >= 0) {
      var c = _cons[ci];
      c.BattCount++; c.BattStored += b.CurrentStoredPower; c.BattMaxStored += b.MaxStoredPower;
      c.BattIn += b.CurrentInput; c.BattOut += b.CurrentOutput; c.BattMaxIn += b.MaxInput;
    }
  }
  string cbd = _c.CountBatteryDischarge;
  _countBatt = string.Equals(cbd, "true", OIC) ||
    (!string.Equals(cbd, "false", OIC) && _role == R_SHIP);
  if (_countBatt) {
    for (int i = 0; i < _bats.Count; i++) {
      var b = _bats[i].Bat;
      if (b == null || !b.IsWorking || b.CurrentStoredPower <= 0) continue;
      // Discharge capacity is stored energy rather than generation, but it is immediately
      // callable, so where the operator has opted in it counts as credible too.
      _genMax += b.MaxOutput; _genCred += b.MaxOutput;
      // A battery counted as capacity is also a contingency: on a ship whose largest single
      // supply IS a battery, an N-1 that ignored it would be measuring the wrong loss.
      if (b.MaxOutput > _largestProd) { _largestProd = b.MaxOutput; _largestProdName = b.CustomName; }
      if (_bats[i].Con >= 0) _cons[_bats[i].Con].GenMax += b.MaxOutput;
    }
  }
  // CURRENT DEMAND. On one electrical network everything consumed is supplied by some
  // producer, and a discharging battery is a producer - so consumption is generator output
  // plus NET battery discharge. Battery CHARGING is a load and is already inside the producer
  // output that feeds it, so it is never added again. Using GROSS battery output here double
  // counted a battery that reports charging and discharging at the same instant. See the
  // block comment above Measure.
  _demand = _genCur + _battNetOut;
  // PROTECTION SPENDS CREDIBLE. The nameplate reserve is kept only so the dashboard can show
  // the gap between what the blocks are rated for and what is actually believed.
  _reserve = _genCred - _demand;
  _reserveNameplate = _genMax - _demand;
  _n1 = _genCred - _largestProd - _demand;
  // Credible capacity still RISING means the producers are demonstrating more, which is the
  // opposite of being stuck at a ceiling. This timer is what tells those two apart.
  if (_genCred > _credPeak + 0.01) { _credPeak = _genCred; _credFlatSince = _now; }
  Stress();
  _rechargeExposure = _battMaxIn;
  _potential = 0; _known = 0; _unknown = 0; _unkDefs.Clear(); _brownout = 0; _brownoutRaw = 0;
  for (int i = 0; i < _bi.Count; i++) {
    var r = _bi[i];
    if (r.Ignore || r.B == null) continue;
    // Interface, not category. A boiler or a steam well head is categorised Generation
    // because that is what it SUPPORTS, but it is a consumer and its load is real.
    if (r.IsProducer) continue;
    // BROWNOUT TELEMETRY, Phase A: counted and reported, never acted on. A block that is
    // switched on and undamaged but not working is, for most block types, a block that is not
    // getting the power it asked for - which would be a direct observation of unmet demand and
    // would work on a grid with no batteries at all. Before trusting it we need to see which
    // IO and vanilla blocks report IsWorking=false for reasons that have nothing to do with
    // power. Promoting it on the strength of the idea alone would repeat the exact mistake
    // that put a demand-trend heuristic into a live server.
    if (r.F.Enabled && r.B.IsFunctional && !r.B.IsWorking) {
      if (r.BrownSince < 0) r.BrownSince = _now;
      _brownoutRaw++;
      // Only counts once it has outlived a rescan, which a deleted block cannot do.
      if (_now - r.BrownSince >= _c.RescanSeconds) _brownout++;
    } else r.BrownSince = -1;
    bool known = r.Src != SRC_NONE;
    if (known) _known++; else { _unknown++; _unkDefs.Add(r.Def); }
    _potential += r.MaxIn;
    if (r.Con >= 0) {
      var c = _cons[r.Con];
      c.PotLoad += r.MaxIn;
      c.LoadEst += r.CurIn;
      if (!known) c.Unknowns++;
    }
  }
  for (int i = 0; i < _newCons.Count; i++) {
    int ci = _newCons[i];
    if (ci < 0 || ci >= _cons.Count) continue;
    var nc = _cons[ci];
    Ev("NEW GRID " + Trim(nc.Name, 24) + "  load " + MW(nc.LoadEst) + "  potential " +
      MW(nc.PotLoad) + "  recharge exposure " + MW(nc.BattMaxIn) +
      (nc.Unknowns > 0 ? "  (" + nc.Unknowns + " unknown)" : ""));
  }
  _newCons.Clear();
  // Protection is counted once per measurement, not once per rendered frame: it is a full
  // pass over every block and the dashboard redraws far more often than the state changes.
  _protCount = 0;
  for (int i = 0; i < _bi.Count; i++)
    if (_bi[i].CfgProt || IsProtectedNow(_bi[i].B)) _protCount++;
  if (_prevDemand >= 0) {
    if (_demand > _demandRef + 5 && _demand > _demandRef * 1.2) {
      Ev("LOAD SPIKE to " + MW(_demand) + " (+" + MW(_demand - _demandRef) + ")");
      _demandRef = _demand;
    } else if (_demand > _demandRef) {
      _demandRef = _demand;                       // silent new high, below the report bar
    } else {
      // Decay toward the current figure so a quiet period re-arms the detector. Slow on
      // purpose: fast decay would re-report every trough-to-peak of a cyclic load, which is
      // the exact behaviour being fixed.
      _demandRef -= (_demandRef - _demand) * 0.01;
    }
  } else {
    _demandRef = _demand;
  }
  _prevDemand = _demand;
}

// STRESS. Credible reserve alone must not authorise shedding: on a grid that has never been
// loaded hard, credible is low purely for want of evidence, because proven only grows when a
// producer is ASKED for more. Shedding on that would punish a base for being lightly used.
//
//   Stressed =  batteries are NET discharging above 0.1 MW
//            OR ( credible reserve is below the shed threshold
//                 AND every fuel-fed producer is pinned at what it has demonstrated
//                 AND credible capacity has not RISEN for StressHoldSeconds )
//
// The third clause is the discriminator, and it is why "at 99% of nameplate" was the wrong
// test: a 50 MW turbine fed 26 MW of steam never approaches 99% of its rating, so that rule
// would have failed on exactly the case this change exists to fix. Pinned at CREDIBLE with
// credible no longer growing is the observable difference between "demand is rising and being
// met" and "demand has reached the ceiling".
//
// STRUCTURAL LIMIT, stated rather than papered over: on a grid with NO batteries, Demand is
// defined as generator output plus net battery discharge, so demand can never exceed credible
// generation and reserve can never go negative. Such a grid can be seen to be AT its ceiling
// but never in deficit. The second clause is what makes a battery-less installation work at
// all, and it is deliberately the conservative reading - no buffer means nothing absorbs an
// overload, so acting at the ceiling is right. Consumer-side brownout detection (a production
// block that IsProducing while receiving far less than it requires) is the real answer and is
// left for a later version.
// The ONLY shedding authority in this build. All three of magnitude, duration and a real
// fall in stored energy must hold together; any one of them alone has already been shown to
// fire on a healthy station.
void Stress() {
  double bar = _genCred * 0.02;
  if (bar < _c.BattStressMW) bar = _c.BattStressMW;
  _battBar = bar;
  bool material = _battMaxStored > 0 && _battNetOut >= bar;
  _battRecBar = bar * _c.BattRecoverFrac;
  // Elapsed time for the energy integral. Taken from the accumulated clock rather than assumed
  // to be one second, and a long gap - a recompile, a paused server - integrates nothing
  // rather than inventing a large slab of energy that was never measured.
  double dt = _now - _stressT;
  _stressT = _now;
  if (dt < 0 || dt > 5) dt = 0;
  if (!_stressed) {
    // ---- ENTRY. Unchanged: it passed both the cyclic-factory negative case and the jump
    // drive positive case. The timer must be CONTINUOUS here - a battery oscillating across
    // the threshold is not draining, and an instantaneous test cannot tell the two apart.
    if (!material) { _battStressSince = -1e9; _battExpected = 0; }
    else {
      if (_battStressSince < -1e8) {
        _battStressSince = _now; _battStoredAtStress = _battStored; _battExpected = 0;
      }
      // Integrate the energy the measured discharge SHOULD have removed over this window.
      _battExpected += _battNetOut * dt / 3600.0;
    }
    bool sustained = material && _battStressSince > -1e8 &&
                     _now - _battStressSince >= _c.StressHoldSeconds;
    _battDecline = _battStressSince > -1e8 ? _battStoredAtStress - _battStored : 0;
    // Stored energy must have fallen CONSISTENTLY WITH THE MEASURED DISCHARGE. That is what
    // rejects the gross/net artefact - a bank reporting 11 MW out against 12 MW in moves no
    // net energy, so actual stays at zero while expected climbs and this never qualifies.
    _battNeedFall = _battExpected * _c.DeclineConsistency;
    if (_battNeedFall < _c.MinDeclineMWh) _battNeedFall = _c.MinDeclineMWh;
    if (sustained && _battDecline >= _battNeedFall) {
      _stressed = true;
      _recoverSince = -1e9;
      Ev("ELECTRICAL STRESS - batteries draining " + MW(_battNetOut) + " for " +
         Fx(_now - _battStressSince) + "s, stored down " + Fx(_battDecline) +
         " of " + Fx(_battExpected) + " MWh expected");
    }
  } else {
    // ---- EXIT. A DIFFERENT QUESTION, deliberately. Not "would this still qualify to start"
    // but "has the deficit actually gone". Keep reporting the cumulative drain since entry so
    // the scan shows how deep this episode has become, rather than resetting to zero.
    _battDecline = _battStoredAtStress - _battStored;
    if (_battNetOut >= _battRecBar) _recoverSince = -1e9;
    else if (_recoverSince < -1e8) _recoverSince = _now;
    if (_battNetOut < _battRecBar && _recoverSince > -1e8 &&
        _now - _recoverSince >= _c.StressRecoverSeconds) {
      _stressed = false;
      _battStressSince = -1e9;
      Ev("Electrical stress cleared - drain under " + MW(_battRecBar) + " for " +
         Fx(_now - _recoverSince) + "s");
    }
  }
}

double Coverage() {
  int t = _known + _unknown;
  return t <= 0 ? 1 : (double)_known / t;
}

// Condition is evaluated on BOTH an absolute MW reserve and a percentage of available
// generation, and the WORSE of the two wins. Percentage alone lies on a small ship (2 MW of
// 3 MW looks healthy right up to the moment a drill starts); MW alone lies on a large base.
void Condition() {
  int byMw = _reserve <= _c.CriticalReserveMW ? K_CRITICAL
           : _reserve <= _c.WarningReserveMW ? K_WARNING
           : _reserve <= _c.CautionReserveMW ? K_CAUTION : K_NORMAL;
  double pct = _genCred > 0 ? _reserve / _genCred * 100 : -100;
  int byPct = pct <= _c.CriticalReservePct ? K_CRITICAL
            : pct <= _c.WarningReservePct ? K_WARNING
            : pct <= _c.CautionReservePct ? K_CAUTION : K_NORMAL;
  int k = byMw > byPct ? byMw : byPct;
  // CAPACITY RISK is that ladder as it stands: it describes headroom, which is a contingency
  // question, not an electrical one. A negative N-1 is a risk in its own right however
  // comfortable the headroom looks.
  int rk = k;
  if (_n1 < 0 && rk < K_WARNING) rk = K_WARNING;
  // Any change of candidate restarts the clock, so a value oscillating across a threshold
  // never accumulates enough continuous time to commit.
  if (rk != _riskCand) { _riskCand = rk; _riskCandSince = _now; }
  if (rk != _risk) {
    double hold = rk > _risk ? _c.RiskPromoteSeconds : _c.RiskRecoverSeconds;
    if (_now - _riskCandSince >= hold) {
      // Logged ONLY on a committed change. The instantaneous crossings are what made this
      // unreadable in the first place and they are deliberately silent.
      Ev("Capacity risk " + CONDN[_risk] + " -> " + CONDN[rk] +
        " (credible reserve " + MW(_reserve) + ", N-1 " + MW(_n1) +
        ", held " + Fx(_now - _riskCandSince) + "s)");
      _risk = rk;
    }
  }
  // POWER CONDITION leaves NORMAL only when the system is observably strained. Thin headroom
  // on an idle base is a risk, not a condition, and reporting it as one teaches the operator
  // to ignore the field that is supposed to interrupt them.
  if (!_stressed) k = K_NORMAL;
  // EMERGENCY is not a worse WARNING: it means the network is already living off a reserve
  // that is running out, which is a different fact from "we are short of headroom".
  double soc = _battMaxStored > 0 ? _battStored / _battMaxStored : 1;
  // "No supply at all" and "supply is a tank that is nearly empty" are the two genuine
  // emergencies. A grid with no producers but full batteries is NEITHER, and saying it is
  // would shed a perfectly healthy battery ship down to life support.
  if (_genCred <= 0 && _demand > 0 && soc < 0.10) k = K_EMERG;
  else if (_reserve < 0 && soc < 0.10) k = K_EMERG;
  if (k != _cond) {
    Ev("Power condition " + CONDN[_cond] + " -> " + CONDN[k] +
      " (credible reserve " + MW(_reserve) + ")");
    _cond = k;
  }
}

// ---------------------------------------------------------------------------------------
// LOAD SHEDDING
// ---------------------------------------------------------------------------------------
// The deepest tier the current electrical condition is allowed to reach into. Tiers above
// this are not "expensive to shed" - they are simply not candidates.
int DeepestTier() {
  if (_cond >= K_CRITICAL) return T_NORM;
  if (_cond == K_WARNING) return T_IND;
  return T_DISC;
}

void ShedStep() {
  // A model that has never been published cannot justify switching anything off.
  if (!_modelReady) return;
  // EPISODE END. Whatever the alarm is still doing, the deficit this episode was opened for
  // has gone.
  if (_reserve >= _c.ShedReserveMW) {
    if (_shedPhase != SP_IDLE) {
      Ev("Shed episode ended after " + _shedActions + " action(s), reserve " + MW(_reserve));
      _shedPhase = SP_IDLE; _shedActions = 0;
      // Scoped to the episode, like the action count beside it. NOT _shedSettleUntil - that
      // survives the boundary by design so a fresh deficit is still paced.
      _battOutAtShed = 0;
    }
    return;
  }
  // Corroboration. Credible reserve is thin on a lightly used base simply for want of
  // evidence, and shedding on that alone would punish a base for never having been loaded.
  if (_c.RequireStress && !_stressed) return;
  // SETTLING. Announced at the moment of the shed, so nothing is logged here - this is the
  // interval during which the last action is allowed to take effect before anything is
  // measured or decided.
  if (_now < _shedSettleUntil) return;
  // LIVE DRAIN AUTHORISES EVERY ACTION. Stressed stays latched through the recovery hold by
  // design, and an episode end resets the action count - so gating this on "is this a
  // continuation" left the first action of the next episode authorised by an alarm that may
  // have stopped being true up to StressRecoverSeconds ago. Stateless rule, no exceptions.
  if (_battNetOut < _battBar) {
    if (_shedPhase != SP_STOPPED) {
      _shedPhase = SP_STOPPED;
      Ev("SHEDDING STOPPED - drain " + MW(_battNetOut) + " below bar " + MW(_battBar) +
         (_shedActions > 0 ? " after " + _shedActions + " action(s)" : ", no action taken") +
         "; stress latch stays for recovery hysteresis");
    }
    return;
  }
  if (_shedActions > 0 && _shedPhase != SP_ARMED) {
    _shedPhase = SP_ARMED;
    Ev("Deficit persists after settle: draining " + MW(_battNetOut) + ", was " +
       MW(_battOutAtShed) + " at the last action - further shedding permitted");
  }
  DoShed(false);
}

List<BI> _cand = new List<BI>();
int DoShed(bool manual) {
  double need = _c.ShedReserveMW - _reserve + 1.0;
  if (manual && need <= 0) need = 0.001;
  if (need <= 0) return 0;
  int deepest = DeepestTier();
  int[] tiers = _tier[_role * 4 + _mode];
  _cand.Clear();
  _audit.Clear(); _auditAt = _now; _auditShed = 0; _candAbort = false;
  for (int i = 0; i < _bi.Count; i++) {
    // A PARTIAL candidate list is worse than none: the block it failed to reach might be the
    // one that should have been shed, which is the exact class of defect the audit exists to
    // investigate. So the action is abandoned and retried next tick rather than acted on.
    if (Over()) { _candAbort = true; break; }
    var r = _bi[i];
    if (r.B == null || r.F == null || r.Ignore || r.CfgProt) continue;
    if (_shed.ContainsKey(r.Id)) continue;
    if (r.Cat == C_UNK) continue;             // never shed what we could not identify
    int t = tiers[r.Cat];
    if (r.CfgShed && t > T_PROT) t = T_DISC;  // explicit opt-in overrides policy, not protection
    // Identical tests in an identical order, with the outcome recorded instead of discarded.
    // Relief() is still only reached when the two tests before it pass, so nothing is called
    // that was not called before and no decision changes.
    int v; double rel = 0;
    if (t < deepest) v = CV_TIER;
    else if (!Controllable(r)) v = CV_CTRL;
    else { rel = Relief(r); v = rel <= 0.01 ? CV_RELIEF : CV_INCLUDED; }
    Audit(r, t, rel, v);
    if (v != CV_INCLUDED) continue;
    _cand.Add(r);
  }
  if (_candAbort) {
    Ev("Shed action deferred: instruction budget reached while building the candidate list ("
       + _cand.Count + " of " + _bi.Count + " examined) - retrying next tick");
    return 0;
  }
  if (_cand.Count == 0) return 0;
  _cand.Sort((x, y) => {
    int tx = tiers[x.Cat], ty = tiers[y.Cat];
    if (x.CfgShed) tx = T_DISC;
    if (y.CfgShed) ty = T_DISC;
    if (tx != ty) return ty - tx;                       // most expendable tier first
    double rx = Relief(x), ry = Relief(y);
    if (rx != ry) return ry > rx ? 1 : -1;              // biggest relief first within a tier
    return x.Id.CompareTo(y.Id);                        // stable
  });
  // Sorted position, matched back by EntityId. Only meaningful for included blocks, and it is
  // the other half of the question: excluded pre-sort, or included and mis-ordered?
  for (int i = 0; i < _cand.Count; i++)
    for (int j = 0; j < _audit.Count; j++)
      if (_audit[j].Id == _cand[i].Id) { _audit[j].Pos = i + 1; break; }
  double got = 0;
  int n = 0, nref = 0;
  _refusals.Clear();
  for (int i = 0; i < _cand.Count && n < _c.MaxShedPerCycle && got < need; i++) {
    var r = _cand[i];
    double rel = Relief(r);
    if (!ShedOne(r, rel)) {
      string line = Trim(r.B.CustomName, 24) + " [" + CATN[r.Cat] + "/" +
        TIERN[tiers[r.Cat]] + "] claimed " + MW(rel) + " REFUSED: " + _refuseWhy;
      if (_refusals.Count < 20) _refusals.Add(line);
      if (nref < 3) { Ev("  skipped " + line); nref++; }
      continue;
    }
    got += rel; n++;
  }
  _auditShed = n;
  if (n > 0) {
    _lastAction = "SHED " + n + " item(s), -" + MW(got);
    Ev("LOAD SHEDDING " + n + " item(s), " + MW(got) + " relieved");
    _shedActions += n;
    _battOutAtShed = _battNetOut;
    _shedSettleUntil = _now + _c.ShedSettleSeconds;
    _shedPhase = SP_SETTLING;
    Ev("  settling " + Fx(_c.ShedSettleSeconds) + "s before re-measuring");
    _goodSince = -1e9;
    Save();
  }
  return n;
}

// Records one block's decision using the values the filter ACTUALLY USED - all cached, which
// is the whole point. Nothing here is written back to the block record: a fresh reading taken
// during an audit must never become the state a later decision is made on, or the instrument
// changes what it is measuring.
void Audit(BI r, int tier, double rel, int v) {
  if (_audit.Count >= 40) return;
  var a = new CAUD { Id = r.Id, Name = Trim(r.B.CustomName, 24), Cat = r.Cat, Tier = tier,
    En = r.F.Enabled, HasCur = r.HasCur, Cur = r.CurIn, Max = r.MaxIn, Rel = rel, V = v };
  // For the one verdict we cannot currently explain, take a fresh read as EVIDENCE ABOUT THE
  // BLOCK - explicitly not evidence about what the filter saw, which was the cached value
  // above. If the fresh parse disagrees with the cache, the cache was stale; if it agrees at
  // zero, the block genuinely publishes no current-input figure and the chunk refresh is
  // innocent. Those are the two answers, and they need different fixes.
  if (v == CV_RELIEF) {
    double fc, fm;
    a.FreshOk = ParseDetail(r.B, out fc, out fm);
    a.FreshCur = fc; a.FreshMax = fm;
    string di = r.B.DetailedInfo;
    a.Detail = string.IsNullOrEmpty(di) ? "(empty)"
      : Trim(di.Replace("\r\n", "~").Replace('\n', '~').Replace('\r', '~'), 130);
  }
  _audit.Add(a);
}

// Relief is what switching this block off would actually give back NOW. A refinery rated at
// 8 MW that is currently idle returns nothing, and shedding it would be pure damage; where no
// current figure has been sampled yet the rated figure is the only estimate available.
double Relief(BI r) {
  var bat = r.B as IMyBatteryBlock;
  if (bat != null) return bat.ChargeMode == ChargeMode.Recharge ? bat.CurrentInput : 0;
  if (!r.F.Enabled) return 0;    // already off: nothing to gain, and not ours to claim
  return r.HasCur ? r.CurIn : r.MaxIn;
}

// May the script touch this block at all? Separate from priority on purpose: this answers a
// safety question, and a mode change must never be able to change the answer.
bool Controllable(BI r) {
  if (r.Con < 0) return false;
  if (!_cons[r.Con].Home) {
    // EXTERNAL CONSTRUCT. Section 22: observe everything, touch almost nothing. The category
    // whitelist is checked independently of the policy tier so no mode can widen it.
    if (!_c.ControlDocked) return false;
    bool ok = false;
    for (int i = 0; i < SAFE_EXTERNAL.Length; i++) if (SAFE_EXTERNAL[i] == r.Cat) { ok = true; break; }
    if (!ok) return false;
  }
  return !IsProtectedNow(r.B);
}

// RUNTIME PROTECTION, evaluated against live block state. This is called again immediately
// before the block is disabled, never read from the classification cache: a connector that
// locked one tick ago must be protected on this tick, not on the next scan.
//
// The connector rule is the important one. A docked ship commonly has its thrusters off; the
// connector is the only thing holding it. Switching that connector off to save a few hundred
// kilowatts releases a ship with no station-keeping, and the script would have caused a far
// worse accident than the brownout it was preventing.
bool IsProtectedNow(IMyTerminalBlock b) {
  if (b == null) return true;
  if (b.EntityId == Me.EntityId) return true;                 // never switch off the controller
  var conn = b as IMyShipConnector;
  if (conn != null) {
    return conn.Status == MyShipConnectorStatus.Connected ||
           conn.Status == MyShipConnectorStatus.Connectable;
  }
  var gear = b as IMyLandingGear;
  if (gear != null) return gear.IsLocked;
  var merge = b as IMyShipMergeBlock;
  if (merge != null) return merge.IsConnected;
  if (b is IMyShipController) return true;                    // cockpits and remote control
  if (b is IMyThrust || b is IMyGyro) return true;            // flight safety
  if (b is IMyAirVent || b is IMyOxygenFarm || b is IMyMedicalRoom) return true; // life support
  if (b is IMyMotorBase || b is IMyPistonBase) return true;   // structure-bearing mechanicals
  if (b is IMyProgrammableBlock) return true;                 // including other controllers
  return false;
}

bool ShedOne(BI r, double relief) {
  var b = r.B;
  _refuseWhy = "";
  // Re-validated here, against live state, in the same sequence that performs the mutation.
  if (b == null || !b.IsFunctional) { _refuseWhy = "not functional"; return false; }
  if (IsProtectedNow(b)) { _refuseWhy = "runtime-protected"; return false; }
  if (r.CfgProt) { _refuseWhy = "config-protected"; return false; }
  var bat = b as IMyBatteryBlock;
  if (bat != null) {
    if (bat.ChargeMode != ChargeMode.Recharge) {
      _refuseWhy = "battery not in Recharge"; return false;
    }
    int prior = (int)bat.ChargeMode;
    bat.ChargeMode = ChargeMode.Auto;
    Remember(r, 1, prior, relief);
    Ev("  battery off recharge " + Trim(b.CustomName) + " -" + MW(relief));
    return true;
  }
  if (r.F == null || !r.F.Enabled) { _refuseWhy = "already disabled"; return false; }
  // Fresh draw, not the cached chunk sample: a machine that finished its batch since the last
  // refresh is now idle, and switching it off would cost availability for no relief at all.
  // THIS is the guard suspected of silently reordering the shed sequence, so it now says so.
  double fcur, fmax;
  if (ParseDetail(b, out fcur, out fmax)) {
    r.CurIn = fcur; r.HasCur = true;
    if (fcur <= 0.01) {
      _refuseWhy = "live draw " + Fx(fcur) + "MW, cached said " + Fx(relief) + "MW - idle now";
      return false;
    }
    relief = fcur;
  }
  r.F.Enabled = false;
  Remember(r, 0, 0, relief);
  Ev("  shed " + Trim(b.CustomName) + " [" + CATN[r.Cat] + "] -" + MW(relief));
  return true;
}

void Remember(BI r, int kind, int prior, double relief) {
  var s = new SHED { Id = r.Id, Kind = kind, Prior = prior, Relief = relief,
    Name = r.B.CustomName == null ? "?" : r.B.CustomName, At = _now };
  _shed[r.Id] = s;
  _shedOrder.Add(r.Id);
}

// ---------------------------------------------------------------------------------------
// RECOVERY. Two separate timers: the reserve must have been healthy for RecoverySeconds
// before anything comes back at all, and then only one item per RestoreStepSeconds. Bringing
// a shed bank back in one step is how a base sheds again three seconds later, forever.
// ---------------------------------------------------------------------------------------
double RestoreThreshold() {
  double band = _genCred - _c.ShedReserveMW;
  double floor = _c.ShedReserveMW + 2;
  if (band < floor) band = floor;
  return _c.RestoreReserveMW < band ? _c.RestoreReserveMW : band;
}

void RestoreStep() {
  if (!_modelReady) return;
  if (_reserve >= RestoreThreshold()) {
    if (_goodSince < 0) _goodSince = _now;
  } else {
    _goodSince = -1e9;
    return;
  }
  if (_shedOrder.Count == 0) return;
  if (_now - _goodSince < _c.RecoverySeconds) return;
  if (_now - _lastRestore < _c.RestoreStepSeconds) return;
  SHED head;
  if (_shed.TryGetValue(_shedOrder[_shedOrder.Count - 1], out head)) {
    double margin = _reserve - _c.ShedReserveMW;
    if (head.Relief > margin) {
      if (!_restoreBlocked) {
        _restoreBlocked = true;
        Ev("Restore held: " + Trim(head.Name, 24) + " needs " + MW(head.Relief) +
          ", margin " + MW(margin));
      }
      return;
    }
  }
  _restoreBlocked = false;
  if (RestoreOne()) { _lastRestore = _now; Save(); }
}

// LIFO: the last thing shed was the most important thing shed, so it is the first thing back.
bool RestoreOne() {
  while (_shedOrder.Count > 0) {
    long id = _shedOrder[_shedOrder.Count - 1];
    _shedOrder.RemoveAt(_shedOrder.Count - 1);
    SHED s;
    if (!_shed.TryGetValue(id, out s)) continue;
    _shed.Remove(id);
    var b = GridTerminalSystem.GetBlockWithId(id);
    if (b == null) {
      // The block left with its grid. Forgetting it is correct: it is no longer ours.
      Ev("Restore skipped, block gone: " + Trim(s.Name));
      continue;
    }
    if (s.Kind == 1) {
      var bat = b as IMyBatteryBlock;
      if (bat != null) bat.ChargeMode = (ChargeMode)s.Prior;
    } else {
      var f = b as IMyFunctionalBlock;
      if (f != null) f.Enabled = true;
    }
    _lastAction = "RESTORE " + Trim(s.Name);
    Ev("RESTORED " + Trim(s.Name) + " (+" + MW(s.Relief) + " expected)");
    return true;
  }
  return false;
}

int RestoreAll() {
  int n = 0;
  while (_shedOrder.Count > 0 && n < 200) { if (RestoreOne()) n++; }
  if (n > 0) { _lastRestore = _now; Save(); Ev("Manual recover: " + n + " item(s)"); }
  return n;
}

// ---------------------------------------------------------------------------------------
// HISTORY AND EVENTS
// ---------------------------------------------------------------------------------------
// A flat ring of samples, held in memory only. It is deliberately NOT persisted to Storage:
// an hour of samples would dominate the Storage budget that the shed list and the learned
// catalog need, and losing a history graph across a recompile costs nothing, while losing
// the record of which blocks the script switched off costs a lot.
void SampleFast() {
  if (_now - _lastFast < 1.0) return;
  _lastFast = _now;
  _fast.Add(new FAST { T = (float)_now, Demand = (float)_demand, GenCur = (float)_genCur,
    GenCred = (float)_genCred, BattNet = (float)_battNetFlow,
    BattOut = (float)_battNetOut, Stored = (float)_battStored,
    Brown = (short)_brownout, Str = _stressed });
  while (_fast.Count > 60) _fast.RemoveAt(0);
}

void Sample() {
  if (_now - _lastHist < _c.HistorySeconds) return;
  _lastHist = _now;
  _hist.Add(new SAMP { T = (float)_now, GenCur = (float)_genCur, GenMax = (float)_genCred,
    Demand = (float)_demand, Reserve = (float)_reserve, Stored = (float)_battStored,
    Net = (float)_battNetFlow, Pot = (float)_potential, Cond = (byte)_cond });
  while (_hist.Count > _c.HistorySamples) _hist.RemoveAt(0);
}

void Ev(string s) {
  _events.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s);
  int cap = _c == null ? 40 : _c.EventLines;
  while (_events.Count > cap) _events.RemoveAt(0);
}

// ---------------------------------------------------------------------------------------
// DISPLAY
// ---------------------------------------------------------------------------------------
// The PB's own surface always shows the main page. Any other panel opts in by name tag,
// optionally choosing a page: [PC-LCD:grids], [PC-LCD:events], [PC-LCD:history].

void PrepSurface(IMyTextSurface s) {
  s.ContentType = ContentType.TEXT_AND_IMAGE;
  s.Font = "Monospace";
  s.Alignment = TextAlignment.LEFT;
}

void Render() {
  string main = null, grids = null, events = null, hist = null;
  for (int i = 0; i < _screens.Count; i++) {
    string p = _screenPage[i];
    string txt;
    if (p == "grids") { if (grids == null) grids = PageGrids(); txt = grids; }
    else if (p == "events") { if (events == null) events = PageEvents(); txt = events; }
    else if (p == "history") { if (hist == null) hist = PageHistory(); txt = hist; }
    else { if (main == null) main = PageMain(); txt = main; }
    _screens[i].WriteText(txt);
  }
}

const int W = 30;
void Row(string label, string val) {
  int pad = W - label.Length - val.Length;
  _sb.Append(label);
  for (int i = 0; i < pad; i++) _sb.Append(' ');
  if (pad <= 0) _sb.Append(' ');
  _sb.Append(val).Append('\n');
}

string PageMain() {
  _sb.Clear();
  _sb.Append("POWER CONTROL v").Append(VERSION).Append('\n');
  Row("Role", ROLEN[_role] + (_roleAuto ? "" : "*"));
  Row("State", STATEN[_state] + (_stateAuto ? "" : "*"));
  Row("Mode", MODEN[_mode]);
  Row("Condition", CONDN[_cond] + (_stressed ? " *" : ""));
  Row("Capacity Risk", CONDN[_risk] + (_genProven > _genCur + 0.1 ? "" : " ?"));
  _sb.Append("\nGENERATION\n");
  Row(" Current", MW(_genCur));
  // CREDIBLE is what protection spends; NAMEPLATE is what the blocks are rated for. On a
  // fuel-fed base the two diverge hard, and that gap is the thing worth looking at.
  Row(" Credible", MW(_genCred) + (_countBatt ? "+b" : ""));
  Row(" Nameplate", MW(_genMax));
  Row(" Utilization", _genCred > 0 ? Pct(_genCur / _genCred) : "-");
  _sb.Append("\nDEMAND\n");
  Row(" Current", MW(_demand));
  Row(" Potential", MW(_potential));
  Row(" Model Coverage", Pct(Coverage()));
  _sb.Append("\nRESERVE\n");
  Row(" Credible", MW(_reserve));
  Row(" vs Nameplate", MW(_reserveNameplate));
  Row(" N-1", MW(_n1));
  _sb.Append("  loses ").Append(Trim(_largestProdName, W - 9)).Append('\n');
  _sb.Append("\nBATTERY\n");
  Row(" Stored", Fx(_battStored) + "/" + Fx(_battMaxStored) + " MWh");
  Row(" Charge", _battMaxStored > 0 ? Pct(_battStored / _battMaxStored) : "-");
  Row(" Net Flow +out", MW(_battNetFlow));
  Row(" Recharge Exposure", MW(_rechargeExposure));
  int docked = 0, isnew = 0;
  for (int i = 0; i < _cons.Count; i++) {
    if (_cons[i].Docked && !_cons[i].Home) docked++;
    if (_cons[i].IsNew && !_cons[i].Home) isnew++;
  }
  _sb.Append("\nGRIDS\n");
  Row(" Home", "1");
  Row(" Docked", docked.ToString());
  Row(" New/Changed", isnew.ToString());
  _sb.Append("\nPROTECTION\n");
  Row(" Protected", _protCount.ToString());
  Row(" Currently Shed", _shed.Count.ToString());
  Row(" Unknown Consumers", _unknown.ToString());
  Row(" Unknown Types", _unkDefs.Count.ToString());
  Row(" Brownout Blocks", _brownout.ToString());
  Row(" Shed Authority", _stressed ? "batt-drain" : "none");
  Row(" Shed Phase", SPN[_shedPhase]);
  _sb.Append("\nLAST ACTION\n ").Append(Trim(_lastAction, W - 1)).Append('\n');
  // An operator who has switched AutoShed on is entitled to know that it is not going to do
  // anything, and why. Silence here would read as "armed and watching".
  if (_c.AutoShed && !_stressed)
    _sb.Append("\nOBSERVATION ONLY\n no trustworthy overload\n signal present; nothing\n")
      .Append(" will be shed\n");
  if (Coverage() < 0.98)
    _sb.Append("\nWARNING\n potential load model\n incomplete: ").Append(_unkDefs.Count)
      .Append(" type(s)\n run scan, then fill\n [PowerControl.Catalog]\n");
  if (_err.Length > 0) _sb.Append("\nERROR\n ").Append(Trim(_err, W - 1)).Append('\n');
  string s = _sb.ToString();
  _sb.Clear();
  return s;
}

string PageGrids() {
  _sb.Clear();
  _sb.Append("CONNECTED GRIDS\n\n");
  for (int i = 0; i < _cons.Count; i++) {
    var c = _cons[i];
    _sb.Append(Trim(c.Name, 20)).Append(c.Home ? "  HOME" : c.Docked ? "  DOCKED" : "  LINKED")
      .Append('\n');
    Row("  load est", MW(c.LoadEst));
    Row("  potential", MW(c.PotLoad));
    Row("  generation", MW(c.GenCur) + "/" + MW(c.GenMax));
    Row("  batteries", c.BattCount.ToString());
    if (c.BattCount > 0) {
      Row("  batt in", MW(c.BattIn) + "/" + MW(c.BattMaxIn));
      Row("  batt stored", Fx(c.BattStored) + " MWh");
    }
    if (c.Unknowns > 0) Row("  unknown", c.Unknowns.ToString());
    _sb.Append('\n');
  }
  _sb.Append("load est = sampled from\nDetailedInfo, not exact\n");
  string s = _sb.ToString();
  _sb.Clear();
  return s;
}

string PageEvents() {
  _sb.Clear();
  _sb.Append("EVENT LOG\n\n");
  for (int i = _events.Count - 1; i >= 0; i--) _sb.Append(_events[i]).Append('\n');
  string s = _sb.ToString();
  _sb.Clear();
  return s;
}

string PageHistory() {
  _sb.Clear();
  _sb.Append("HISTORY\n\n");
  if (_hist.Count == 0) { _sb.Append("no samples yet\n"); string e = _sb.ToString(); _sb.Clear(); return e; }
  double minR = 1e9, maxD = -1e9, minSoc = 1e9;
  for (int i = 0; i < _hist.Count; i++) {
    if (_hist[i].Reserve < minR) minR = _hist[i].Reserve;
    if (_hist[i].Demand > maxD) maxD = _hist[i].Demand;
    if (_hist[i].Stored < minSoc) minSoc = _hist[i].Stored;
  }
  Row("Samples", _hist.Count.ToString());
  Row("Window", Fx((_now - _hist[0].T) / 60) + " min");
  Row("Min reserve", MW(minR));
  Row("Peak demand", MW(maxD));
  Row("Min stored", Fx(minSoc) + " MWh");
  _sb.Append("\nRECENT (newest first)\n");
  _sb.Append("age   gen  dem  rsv cond\n");
  int shown = 0;
  for (int i = _hist.Count - 1; i >= 0 && shown < 20; i--, shown++) {
    var h = _hist[i];
    _sb.Append(PadL(Fx((_now - h.T) / 60) + "m", 5)).Append(' ')
      .Append(PadL(Fx(h.GenCur), 4)).Append(' ')
      .Append(PadL(Fx(h.Demand), 4)).Append(' ')
      .Append(PadL(Fx(h.Reserve), 4)).Append(' ')
      .Append(CONDN[h.Cond].Substring(0, 4)).Append('\n');
  }
  string s = _sb.ToString();
  _sb.Clear();
  return s;
}

// ---------------------------------------------------------------------------------------
// DIAGNOSTIC SCAN. This is not temporary debug code: it is the only way to find out how an
// Industrial Overhaul block actually presents itself to the PB API, and every catalog entry
// this script will ever gain has to come from it. Written to the PB's Custom Data BELOW the
// report marker, so the configuration above it is never touched.
// ---------------------------------------------------------------------------------------
// Queue a staged diagnostic report. The command returns immediately; the report is built
// across the following ticks and published in one piece when it is finished.
string StartReport(bool all) {
  if (!_modelReady)
    return "no model published yet - nothing to report on (wait for discovery to finish)";
  if (_rs != R_IDLE)
    return "scan already running (" + RN[_rs] + ", " + _rTicks + " ticks so far)";
  _rAll = all;
  _rBi = _bi; _rProd = _prod; _rBats = _bats; _rCons = _cons;
  _rb.Clear();
  _rCnt = new Dictionary<string, int>(SCI);
  _rCat = new Dictionary<string, int>(SCI);
  _rSrc = new Dictionary<string, int>(SCI);
  _rUTot = new Dictionary<string, int>(SCI);
  _rUShown = new Dictionary<string, int>(SCI);
  _rMw = new Dictionary<string, double>(SCI);
  _rCur = new Dictionary<string, double>(SCI);
  _rDefs.Clear(); _rUDefs.Clear(); _rHint.Clear();
  _rUnknown = 0; _rTankN = 0; _rBrownN = 0;
  _rTicks = 0; _rDetail = 0; _rStarted = _now;
  _scanCursor = 0;
  _rs = R_DETAIL; _rIdx = 0;
  return "scan queued - building across ticks, written to Custom Data when complete";
}

void NextSec(int s) { _rs = s; _rIdx = 0; }

// One slice of the report. Returns how many items it emitted, for the cost table.
int ReportStep() {
  _rTicks++;
  if (_rTicks > R_TICK_CAP) {
    Ev("Diagnostic scan abandoned after " + _rTicks + " ticks at stage " + RN[_rs]
       + " - nothing written. A report that cannot finish is a leak, not a diagnostic.");
    _rs = R_IDLE; _rBi = null; _rProd = null; _rBats = null; _rCons = null; _rb.Clear();
    return 0;
  }
  int work = 0;
  // EVERY section is re-entrant and EVERY loop inside one checks the budget. This is the loop
  // that the old WriteScan did not have.
  while (_rs != R_IDLE && !Over()) {
    switch (_rs) {
      case R_DETAIL:     work += RepDetail();     break;
      case R_HEAD:       work += RepHead();       break;
      case R_CONSTRUCTS: work += RepConstructs(); break;
      case R_PRODUCERS:  work += RepProducers();  break;
      case R_BATTERIES:  work += RepBatteries();  break;
      case R_TANKS:      work += RepTanks();      break;
      case R_DEFSCAN:    work += RepDefScan();    break;
      case R_DEFEMIT:    work += RepDefEmit();    break;
      case R_UNKSCAN:    work += RepUnkScan();    break;
      case R_UNKEMIT:    work += RepUnkEmit();    break;
      case R_UNKTAIL:    work += RepUnkTail();    break;
      case R_UNKHINT:    work += RepUnkHint();    break;
      case R_ALLBLOCKS:  work += RepAllBlocks();  break;
      case R_BROWNOUT:   work += RepBrownout();   break;
      case R_FAST:       work += RepFast();       break;
      case R_HIST:       work += RepHist();       break;
      case R_COST:       work += RepCost();       break;
      case R_AUDIT:      work += RepAudit();      break;
      case R_SHEDSTATE:  work += RepShedState();  break;
      case R_EVENTS:     work += RepEvents();     break;
      case R_PUBLISH:    work += RepPublish();    break;
      default:           _rs = R_IDLE;            break;
    }
  }
  if (_rs != R_IDLE) _yielded = true;
  return work;
}

// The detail top-up now runs on the NORMAL budget, spread across the report's own ticks. The
// 90% override is gone: a diagnostic does not get a licence to spend the tick it runs in, and
// raising the ceiling was never the same thing as having enough room.
int RepDetail() {
  int before = _rDetail;
  while (!Over() && _rDetail < _rBi.Count) {
    RefreshDetailChunk();
    _rDetail += _c.ScanChunk;
  }
  if (_rDetail >= _rBi.Count) { _rDetail = _rBi.Count; NextSec(R_HEAD); }
  return _rDetail - before;
}

int RepHead() {
  _rb.Append("POWER CONTROL SCAN  v").Append(VERSION).Append("  ")
    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
  _rb.Append("Role=").Append(ROLEN[_role]).Append(" State=").Append(STATEN[_state])
    .Append(" Mode=").Append(MODEN[_mode]).Append(" Condition=").Append(CONDN[_cond]).Append('\n');
  _rb.Append("Blocks=").Append(_all.Count).Append(" Functional=").Append(_rBi.Count)
    .Append(" Producers=").Append(_rProd.Count).Append(" Batteries=").Append(_rBats.Count)
    .Append(" Constructs=").Append(_rCons.Count).Append('\n');
  // The report PRINTS the last snapshot rather than taking its own. Measure() is an unbounded
  // walk of the whole model, and running it from a diagnostic command was part of what
  // terminated this script. The age is disclosed so the figures can be judged, not assumed.
  _rb.Append("SnapshotAge=").Append(Fx(_now - _lastMeasureAt))
    .Append("s  (this report prints the last measured snapshot; it does not take a new one)\n");
  _rb.Append("BattGrossOut=").Append(Fx(_battOut)).Append("MW BattGrossIn=").Append(Fx(_battIn))
    .Append("MW BattNetFlow=").Append(Fx(_battNetFlow)).Append("MW(+out)")
    .Append(" BattNetOut=").Append(Fx(_battNetOut))
    .Append("MW  (a large gross/net gap means the battery is reporting simultaneous charge "
      + "and discharge; only NET is used for demand)\n");
  _rb.Append("RestoreThreshold=").Append(Fx(RestoreThreshold()))
    .Append("MW  (configured ").Append(Fx(_c.RestoreReserveMW))
    .Append("MW, capped to stay reachable against credible capacity)\n");
  _rb.Append("HeadroomEvidence=")
    .Append(_genProven > _genCur + 0.1 ? "measured" : "NONE - no producer has yet been seen "
      + "to deliver more than it is delivering now, so zero headroom is unproven, not proven")
    .Append('\n');
  _rb.Append("Stressed=").Append(_stressed).Append(" CapacityRisk=").Append(CONDN[_risk])
    .Append(" FuelAtCeiling=").Append(_fuelAtCeiling)
    .Append(" CredFlatFor=").Append(Fx(_credFlatSince > 0 ? _now - _credFlatSince : 0))
    .Append("s\n");
  _rb.Append("BattRecoverBar=").Append(Fx(_battRecBar)).Append("MW  recoverHeld=")
    .Append(Fx(_recoverSince > -1e8 ? _now - _recoverSince : 0)).Append("/")
    .Append(Fx(_c.StressRecoverSeconds))
    .Append("s  (exit test; entry qualification is NOT required to stay latched)\n");
  _rb.Append("BattStressBar=").Append(Fx(_battBar)).Append("MW  held=")
    .Append(Fx(_battStressSince > -1e8 ? _now - _battStressSince : 0)).Append("/")
    .Append(Fx(_c.StressHoldSeconds)).Append("s  storedDecline=").Append(Fx(_battDecline))
    .Append("MWh actual\n");
  _rb.Append("DeclineExpected=").Append(Fx(_battExpected))
    .Append("MWh  required=").Append(Fx(_battNeedFall))
    .Append("MWh  (actual must reach ").Append(Fx(_c.DeclineConsistency))
    .Append(" x expected, floor ").Append(Fx(_c.MinDeclineMWh))
    .Append("MWh; scale-free - a 300MWh bank qualifies as fast as a 3MWh one)\n");
  _rb.Append("CapacityRisk=").Append(CONDN[_risk])
    .Append(" Candidate=").Append(CONDN[_riskCand])
    .Append(" CandidateHeld=").Append(Fx(_now - _riskCandSince)).Append("/")
    .Append(Fx(_riskCand > _risk ? _c.RiskPromoteSeconds
             : _riskCand < _risk ? _c.RiskRecoverSeconds : 0))
    .Append("s  (advisory only; debounced so ordinary production cycles do not chatter)\n");
  _rb.Append("ShedPhase=").Append(SPN[_shedPhase])
    .Append(" actionsThisEpisode=").Append(_shedActions)
    .Append(" settleRemaining=").Append(Fx(_shedSettleUntil > _now ? _shedSettleUntil - _now : 0))
    .Append("/").Append(Fx(_c.ShedSettleSeconds))
    .Append("s  drainNow=").Append(Fx(_battNetOut))
    .Append("MW atLastAction=").Append(Fx(_battOutAtShed))
    .Append("MW  (EVERY action is authorised by the LIVE drain against the bar, never by the "
      + "latched Stressed flag - including the first action of an episode)\n");
  _rb.Append("ShedAuthority=").Append(_stressed ? "battery-drain" : "NONE")
    .Append("  (battery drain is the only authority in this build; brownout is telemetry "
      + "only and does NOT authorise shedding)\n");
  _rb.Append("GenCredible=").Append(Fx(_genCred))
    .Append("MW  (protection spends THIS - nameplate is a rating, not a capability)\n");
  _rb.Append("GenProven=").Append(Fx(_genProven))
    .Append("MW  (highest total output ever witnessed; MaxOutput is a NAMEPLATE on a "
      + "fuel-fed producer and can exceed what it is able to deliver)\n");
  _rb.Append("GenCur=").Append(Fx(_genCur)).Append("MW GenMax=").Append(Fx(_genMax))
    .Append("MW Demand=").Append(Fx(_demand)).Append("MW Reserve=").Append(Fx(_reserve))
    .Append("MW N-1=").Append(Fx(_n1)).Append("MW\n");
  _rb.Append("Potential=").Append(Fx(_potential)).Append("MW Coverage=").Append(Pct(Coverage()))
    .Append(" Known=").Append(_known).Append(" Unknown=").Append(_unknown).Append('\n');
  NextSec(R_CONSTRUCTS);
  return 1;
}

int RepConstructs() {
  if (_rIdx == 0) _rb.Append("\n== CONSTRUCTS ==\n");
  int n = 0;
  while (_rIdx < _rCons.Count) {
    if (Over()) return n;
    var c = _rCons[_rIdx++];
    n++;
    _rb.Append(c.Home ? "HOME   " : c.Docked ? "DOCKED " : "LINKED ")
      .Append(c.Name).Append("  grids=").Append(c.Grids).Append(" blocks=").Append(c.Blocks)
      .Append(" gen=").Append(Fx(c.GenCur)).Append("/").Append(Fx(c.GenMax))
      .Append("MW loadEst=").Append(Fx(c.LoadEst)).Append("MW pot=").Append(Fx(c.PotLoad))
      .Append("MW batt=").Append(c.BattCount).Append(" battIn=").Append(Fx(c.BattIn))
      .Append("/").Append(Fx(c.BattMaxIn)).Append("MW stored=").Append(Fx(c.BattStored))
      .Append("/").Append(Fx(c.BattMaxStored)).Append("MWh unknown=").Append(c.Unknowns)
      .Append('\n');
  }
  NextSec(R_PRODUCERS);
  return n;
}

int RepProducers() {
  if (_rIdx == 0) _rb.Append("\n== PRODUCERS ==\n");
  int n = 0;
  while (_rIdx < _rProd.Count) {
    if (Over()) return n;
    int i = _rIdx++;
    var p = _rProd[i];
    n++;
    if (p.P == null) continue;
    _rb.Append(p.P.IsWorking ? "ON  " : "OFF ").Append(Trim(p.Name, 28)).Append("  ")
      .Append(DefOf(p.P)).Append("  out=").Append(Fx(p.P.CurrentOutput)).Append("/")
      .Append(Fx(p.P.MaxOutput)).Append("MW  proven=").Append(Fx(p.Proven))
      .Append("MW  ").Append(p.Env ? "env" : "fuel").Append("  grid=")
      .Append(p.P.CubeGrid == null ? "?" : p.P.CubeGrid.CustomName).Append('\n');
    if (i < 12) {
      string pdi = p.P.DetailedInfo;
      _rb.Append("      detail: ")
        .Append(string.IsNullOrEmpty(pdi) ? "(empty)"
          : Trim(pdi.Replace("\r\n", "~").Replace('\n', '~').Replace('\r', '~'), 140))
        .Append('\n');
    }
  }
  NextSec(R_BATTERIES);
  return n;
}

int RepBatteries() {
  if (_rIdx == 0) _rb.Append("\n== BATTERIES ==\n");
  int n = 0;
  while (_rIdx < _rBats.Count) {
    if (Over()) return n;
    var b = _rBats[_rIdx++].Bat;
    n++;
    if (b == null) continue;
    _rb.Append(Trim(b.CustomName, 28)).Append("  ").Append(b.ChargeMode.ToString())
      .Append(" stored=").Append(Fx(b.CurrentStoredPower)).Append("/")
      .Append(Fx(b.MaxStoredPower)).Append("MWh in=").Append(Fx(b.CurrentInput)).Append("/")
      .Append(Fx(b.MaxInput)).Append("MW out=").Append(Fx(b.CurrentOutput)).Append("/")
      .Append(Fx(b.MaxOutput)).Append("MW grid=")
      .Append(b.CubeGrid == null ? "?" : b.CubeGrid.CustomName).Append('\n');
  }
  NextSec(R_TANKS);
  return n;
}

int RepTanks() {
  if (_rIdx == 0) { _rb.Append("\n== GAS / STEAM TANKS ==\n"); _rTankN = 0; }
  int n = 0;
  while (_rIdx < _rBi.Count) {
    if (Over()) return n;
    var r = _rBi[_rIdx++];
    n++;
    var tk = r.B as IMyGasTank;
    if (tk == null) continue;
    _rTankN++;
    _rb.Append(Trim(tk.CustomName, 28)).Append("  ").Append(r.Def)
      .Append("  cat=").Append(CATN[r.Cat])
      .Append(" filled=").Append((tk.FilledRatio * 100).ToString("0.0")).Append("%")
      .Append(" stockpile=").Append(tk.Stockpile)
      .Append(" working=").Append(tk.IsWorking).Append('\n');
  }
  if (_rTankN == 0) _rb.Append("(none)\n");
  NextSec(R_DEFSCAN);
  return n;
}

// Per-DEFINITION rollup. This is the table that turns into CATALOG entries: one line per IO
// subtype, with the rated load the game actually reported and where it came from.
int RepDefScan() {
  int n = 0;
  while (_rIdx < _rBi.Count) {
    if (Over()) return n;
    var r = _rBi[_rIdx++];
    n++;
    if (r.Ignore || r.IsProducer) continue;
    int c;
    if (_rCnt.TryGetValue(r.Def, out c)) _rCnt[r.Def] = c + 1;
    else { _rCnt[r.Def] = 1; _rDefs.Add(r.Def); }   // key order snapshot, so emit can resume
    double v;
    if (!_rMw.TryGetValue(r.Def, out v) || r.MaxIn > v) _rMw[r.Def] = r.MaxIn;
    // Summed CURRENT draw alongside the rating. "Is this definition actually drawing anything
    // right now" is the question the shedding engine asks of every candidate, and it was not
    // answerable from this report at all - a machine rated 3 MW and drawing 0 is deliberately
    // never shed, so a report showing only the rating cannot explain what the engine did.
    _rCur[r.Def] = (_rCur.TryGetValue(r.Def, out v) ? v : 0) + r.CurIn;
    _rCat[r.Def] = r.Cat;
    int sc;
    if (!_rSrc.TryGetValue(r.Def, out sc) || r.Src > sc) _rSrc[r.Def] = r.Src;
  }
  NextSec(R_DEFEMIT);
  return n;
}

int RepDefEmit() {
  if (_rIdx == 0) _rb.Append("\n== CONSUMERS BY DEFINITION ==\n");
  int n = 0;
  while (_rIdx < _rDefs.Count) {
    if (Over()) return n;
    string k = _rDefs[_rIdx++];
    n++;
    _rb.Append(PadL(_rCnt[k].ToString(), 4)).Append("x ").Append(k)
      .Append("  cat=").Append(CATN[_rCat[k]]).Append(" max=").Append(Fx(_rMw[k]))
      .Append("MW cur=").Append(Fx(_rCur[k]))
      .Append("MW src=").Append(SRCN[_rSrc[k]]).Append('\n');
  }
  NextSec(R_UNKSCAN);
  return n;
}

int RepUnkScan() {
  int n = 0;
  while (_rIdx < _rBi.Count) {
    if (Over()) return n;
    var r = _rBi[_rIdx++];
    n++;
    if (r.Ignore || r.IsProducer || r.Src != SRC_NONE) continue;
    int t;
    if (_rUTot.TryGetValue(r.Def, out t)) _rUTot[r.Def] = t + 1;
    else { _rUTot[r.Def] = 1; _rUDefs.Add(r.Def); }
    _rUnknown++;
  }
  foreach (var d in _unkDefs) _rHint.Add(d);   // HashSet: snapshot so emitting can resume
  NextSec(R_UNKEMIT);
  return n;
}

// GROUPED BY DEFINITION, with a few examples each. Live evidence: a test grid with 115 pistons
// printed 115 identical unknown lines, and a real IO base has thousands of blocks. A report
// nobody can read is a report nobody acts on, and acting on it is the entire purpose - what
// matters is the DEFINITION and one specimen of its DetailedInfo, not the hundredth copy.
int RepUnkEmit() {
  if (_rIdx == 0) _rb.Append("\n== UNKNOWN CONSUMERS (no rated load found) ==\n");
  int n = 0;
  while (_rIdx < _rBi.Count) {
    if (Over()) return n;
    var r = _rBi[_rIdx++];
    n++;
    if (r.Ignore || r.IsProducer || r.Src != SRC_NONE) continue;
    int sh;
    _rUShown.TryGetValue(r.Def, out sh);
    if (sh >= 3) continue;
    _rUShown[r.Def] = sh + 1;
    _rb.Append(Trim(r.B.CustomName, 28)).Append("  ").Append(r.Def)
      .Append("  cat=").Append(CATN[r.Cat]).Append(" enabled=").Append(r.F.Enabled)
      .Append(" grid=").Append(r.B.CubeGrid == null ? "?" : r.B.CubeGrid.CustomName)
      .Append('\n');
    // The raw DetailedInfo is the evidence for WHY it is unknown: empty, or a format this
    // parser does not recognise. Without it the report cannot be acted on.
    string di = r.B.DetailedInfo;
    _rb.Append("      detail: ")
      .Append(string.IsNullOrEmpty(di) ? "(empty)"
        : Trim(di.Replace("\r\n", "~").Replace('\n', '~').Replace('\r', '~'), 100))
      .Append('\n');
  }
  NextSec(R_UNKTAIL);
  return n;
}

int RepUnkTail() {
  int n = 0;
  while (_rIdx < _rUDefs.Count) {
    if (Over()) return n;
    string k = _rUDefs[_rIdx++];
    n++;
    int sh;
    _rUShown.TryGetValue(k, out sh);
    if (_rUTot[k] > sh)
      _rb.Append("      ... and ").Append(_rUTot[k] - sh).Append(" more of ").Append(k)
        .Append('\n');
  }
  if (_rUnknown == 0) _rb.Append("(none)\n");
  else _rb.Append(_rUnknown).Append(" unknown block(s) in ").Append(_rUDefs.Count)
    .Append(" definition(s)\n");
  NextSec(R_UNKHINT);
  return n;
}

// Deliberately NOT paste-ready. An earlier build emitted these with "=0" already filled in,
// which invites asserting that these blocks draw nothing on no evidence at all - the one thing
// this model refuses to do everywhere else, because an invented value inflates the model AND
// the coverage figure that is supposed to disclose the gap. The line is left incomplete so it
// cannot be pasted without a number being supplied deliberately, and zero is only correct once
// something has established it.
int RepUnkHint() {
  if (_rHint.Count == 0) { NextSec(R_ALLBLOCKS); return 0; }
  if (_rIdx == 0) {
    _rb.Append("\n-- MEASURE each of these, then add it under [").Append(SECCAT)
      .Append("] above the report marker.\n");
    _rb.Append("-- Read the rated draw from an in-game block-info tool, or note network demand,\n");
    _rb.Append("-- switch the block on, and note it again. A value of 0 is a finding, not a default.\n");
  }
  int n = 0;
  while (_rIdx < _rHint.Count) {
    if (Over()) return n;
    _rb.Append("   ").Append(_rHint[_rIdx++]).Append(" = ?\n");
    n++;
  }
  NextSec(R_ALLBLOCKS);
  return n;
}

int RepAllBlocks() {
  if (!_rAll) { NextSec(R_BROWNOUT); return 0; }
  if (_rIdx == 0) _rb.Append("\n== ALL FUNCTIONAL BLOCKS ==\n");
  int n = 0;
  while (_rIdx < _rBi.Count && _rIdx < 500) {
    if (Over()) return n;
    var r = _rBi[_rIdx++];
    n++;
    _rb.Append(Trim(r.B.CustomName, 26)).Append(" | ").Append(r.Def)
      .Append(" | cat=").Append(CATN[r.Cat]).Append(" tier=")
      .Append(TIERN[_tier[_role * 4 + _mode][r.Cat]])
      .Append(" max=").Append(Fx(r.MaxIn)).Append("MW cur=").Append(Fx(r.CurIn))
      .Append("MW src=").Append(SRCN[r.Src])
      .Append(r.CfgProt ? " CFGPROT" : "").Append(IsProtectedNow(r.B) ? " RUNPROT" : "")
      .Append(Controllable(r) ? " CTRL" : " NOCTRL").Append('\n');
  }
  NextSec(R_BROWNOUT);
  return n;
}

int RepBrownout() {
  if (_rIdx == 0) {
    _rb.Append("\n== BROWNOUT (TELEMETRY ONLY - does NOT authorise shedding) ==\n");
    _rb.Append("enabled + functional + NOT working. Expected to be a direct sign of unmet\n");
    _rb.Append("demand, but not trusted until we know which blocks report this for other\n");
    _rb.Append("reasons. Anything listed here that is NOT short of power is a false positive.\n");
    _rBrownN = 0;
  }
  int n = 0;
  while (_rIdx < _rBi.Count && _rBrownN < 40) {
    if (Over()) return n;
    var r = _rBi[_rIdx++];
    n++;
    if (r.Ignore || r.IsProducer || r.B == null || r.F == null) continue;
    if (!r.F.Enabled || !r.B.IsFunctional || r.B.IsWorking) continue;
    _rBrownN++;
    _rb.Append(Trim(r.B.CustomName, 26)).Append("  ").Append(r.Def)
      .Append("  cat=").Append(CATN[r.Cat])
      .Append(r.CfgProt ? " CFGPROT" : "").Append(IsProtectedNow(r.B) ? " RUNPROT" : "")
      .Append(_shed.ContainsKey(r.Id) ? " SHED-BY-SCRIPT" : "")
      .Append(" for=").Append(Fx(r.BrownSince >= 0 ? _now - r.BrownSince : 0)).Append("s")
      .Append(_now - r.BrownSince >= _c.RescanSeconds ? " CONFIRMED" : " unconfirmed")
      .Append(" max=").Append(Fx(r.MaxIn)).Append("MW\n");
  }
  if (_rBrownN == 0) _rb.Append("(none)\n");
  _rb.Append("confirmed=").Append(_brownout).Append("  raw=").Append(_brownoutRaw)
    .Append("  (raw counts a block the instant it reads enabled-but-not-working; confirmed "
      + "requires it to outlive a rescan, which a DELETED block cannot do - a deleted jump "
      + "drive produced raw=1 for ~14s in the v0.1.9 UAT)\n");
  NextSec(R_FAST);
  return n;
}

int RepFast() {
  if (_rIdx == 0) {
    _rb.Append("\n== FAST RING (1 Hz, newest last) ==\n");
    _rb.Append("SIGN: bflow is POSITIVE WHEN DISCHARGING (negative = charging).\n");
    _rb.Append("bout is the clamped per-battery net discharge the stress detector tests.\n");
    _rb.Append("age    dem   gcur  gcred bflow bout  stored br s\n");
  }
  int n = 0;
  while (_rIdx < _fast.Count) {
    if (Over()) return n;
    var f = _fast[_rIdx++];
    n++;
    _rb.Append(PadL(Fx(_now - f.T), 5)).Append(' ').Append(PadL(Fx(f.Demand), 6)).Append(' ')
      .Append(PadL(Fx(f.GenCur), 5)).Append(' ').Append(PadL(Fx(f.GenCred), 5)).Append(' ')
      .Append(PadL(Fx(f.BattNet), 5)).Append(' ').Append(PadL(Fx(f.BattOut), 5)).Append(' ')
      .Append(PadL(Fx(f.Stored), 6)).Append(' ')
      .Append(PadL(f.Brown.ToString(), 2)).Append(' ').Append(f.Str ? "S" : "-").Append('\n');
  }
  if (_fast.Count == 0) _rb.Append("(no samples yet)\n");
  NextSec(R_HIST);
  return n;
}

int RepHist() {
  if (_rIdx == 0) {
    _rb.Append("\n== HISTORY (10 s ring, newest last, last 90) ==\n");
    _rb.Append("SIGN: bflow POSITIVE WHEN DISCHARGING, same convention as the fast ring.\n");
    _rb.Append("age    gcur  gcred dem   rsv   stored bflow cond\n");
    _rIdx = _hist.Count - 90;
    if (_rIdx < 0) _rIdx = 0;
    if (_rIdx == 0 && _hist.Count == 0) { _rb.Append("(no samples yet)\n"); NextSec(R_COST); return 0; }
  }
  int n = 0;
  while (_rIdx < _hist.Count) {
    if (Over()) return n;
    var h = _hist[_rIdx++];
    n++;
    _rb.Append(PadL(Fx((_now - h.T) / 60) + "m", 5)).Append(' ')
      .Append(PadL(Fx(h.GenCur), 5)).Append(' ').Append(PadL(Fx(h.GenMax), 5)).Append(' ')
      .Append(PadL(Fx(h.Demand), 5)).Append(' ').Append(PadL(Fx(h.Reserve), 5)).Append(' ')
      .Append(PadL(Fx(h.Stored), 6)).Append(' ').Append(PadL(Fx(h.Net), 5)).Append(' ')
      .Append(CONDN[h.Cond].Substring(0, 4)).Append('\n');
  }
  NextSec(R_COST);
  return n;
}

int RepCost() {
  _rb.Append("\n== RUNTIME COST ==\n");
  _rb.Append("hard limit ").Append(Runtime.MaxInstructionCount)
    .Append("  soft budget ").Append(Runtime.MaxInstructionCount * _c.InstrBudgetPercent / 100)
    .Append(" (").Append(_c.InstrBudgetPercent).Append("%)\n");
  _rb.Append("constructor ").Append(_ctorCost)
    .Append("   last run ").Append(_tickLast)
    .Append("   last FULL tick ").Append(_fullLast)
    .Append("   PEAK RUN ").Append(_tickPeak).Append("  during: ").Append(_peakWhat).Append('\n');
  _rb.Append("PEAK RUN is recorded on EVERY path, including the boot and discovery returns and\n");
  _rb.Append("the command path. Before v0.1.18 it was written only at the end of a complete\n");
  _rb.Append("tick, so startup - the most expensive part of the session - reported peak 0.\n");
  _rb.Append("phase ceiling ").Append(Ceiling())
    .Append("  = soft budget, capped by (hard limit - reserved tail - margin)\n");
  _rb.Append("reserved tail ").Append(TailCost())
    .Append("  = measured peak of detail + measure + control + render, the work that MUST\n");
  _rb.Append("still run after a resumable phase yields. v0.1.18 reserved nothing and reached\n");
  _rb.Append("47327 of 50000 on a rescan tick with every individual phase inside its budget.\n");
  _rb.Append("MEASURE IS NOT CHUNKED and is the largest fixed per-tick cost. It is linear in\n");
  _rb.Append("block count, so it is what bounds the grid size this script can protect.\n");
  _rb.Append("boot ").Append(BN[_boot]).Append("   discovery ").Append(DN[_ds])
    .Append("   modelPublished=").Append(_modelReady)
    .Append("   lastPhaseYieldedOnBudget=").Append(_yielded).Append('\n');
  _rb.Append("phase            last   peak   work\n");
  for (int i = 0; i < NPH; i++)
    _rb.Append(PadR(PHN[i], 16)).Append(PadL(_phLast[i].ToString(), 6)).Append(' ')
      .Append(PadL(_phPeak[i].ToString(), 6)).Append(' ')
      .Append(PadL(_phWork[i].ToString(), 6)).Append('\n');
  _rb.Append("discovery ").Append(_dProcessed).Append("/").Append(_dTotal)
    .Append("   PEAK RUN against the hard limit is the number that matters\n");
  _rb.Append("report built over ").Append(_rTicks).Append(" tick(s) in ")
    .Append(Fx(_now - _rStarted)).Append("s   detail refreshed ").Append(_rDetail)
    .Append("/").Append(_rBi.Count).Append(" block(s)\n");
  NextSec(R_AUDIT);
  return 1;
}

int RepAudit() {
  if (_rIdx == 0) {
    _rb.Append("\n== CANDIDATE AUDIT (last shed action) ==\n");
    if (_auditAt < 0) { _rb.Append("(no shed action has run yet)\n"); NextSec(R_SHEDSTATE); return 0; }
    _rb.Append("age=").Append(Fx(_now - _auditAt)).Append("s  considered=").Append(_audit.Count)
      .Append("  shed=").Append(_auditShed).Append('\n');
    _rb.Append("curIn/hasCur/relief are the CACHED values the filter used. 'fresh' lines are a\n");
    _rb.Append("read taken DURING the audit, after the decision - evidence about the block, not\n");
    _rb.Append("about what the filter saw. They are never written back.\n");
    _rb.Append("name                      cat         tier    en cur    curIn  maxIn  relief verdict\n");
  }
  int n = 0;
  while (_rIdx < _audit.Count) {
    if (Over()) return n;
    var a = _audit[_rIdx++];
    n++;
    _rb.Append(PadR(a.Name, 25)).Append(' ').Append(PadR(CATN[a.Cat], 11)).Append(' ')
      .Append(PadR(TIERN[a.Tier], 7)).Append(' ').Append(a.En ? "Y" : "n").Append(' ')
      .Append(a.HasCur ? "Y" : "n").Append(' ')
      .Append(PadL(Fx(a.Cur), 6)).Append(' ').Append(PadL(Fx(a.Max), 6)).Append(' ')
      .Append(PadL(Fx(a.Rel), 6)).Append(' ')
      .Append(a.V == CV_INCLUDED ? "included: sorted #" + a.Pos : CVN[a.V]).Append('\n');
    if (a.V == CV_RELIEF) {
      _rb.Append("    fresh parse: ").Append(a.FreshOk ? "ok" : "NO INPUT LINE FOUND")
        .Append(" cur=").Append(Fx(a.FreshCur)).Append(" max=").Append(Fx(a.FreshMax))
        .Append(a.FreshCur > 0.01 ? "   <-- CACHE WAS STALE" : "   <-- block really reads zero")
        .Append('\n');
      _rb.Append("    fresh detail: ").Append(a.Detail).Append('\n');
    }
  }
  NextSec(R_SHEDSTATE);
  return n;
}

int RepShedState() {
  if (_rIdx == 0) {
    _rb.Append("\n== CANDIDATES REFUSED AT THE LAST SHED ACTION ==\n");
    _rb.Append("A refused candidate used to fall through to the next one silently, which can\n");
    _rb.Append("reorder the whole shed sequence. If a high tier block appears here having been\n");
    _rb.Append("refused, THAT is why a lower-value block was shed instead.\n");
    for (int i = 0; i < _refusals.Count; i++) _rb.Append(_refusals[i]).Append('\n');
    if (_refusals.Count == 0) _rb.Append("(none at the last action)\n");
    _rb.Append("\n== SHED STATE ==\n");
  }
  int n = 0;
  while (_rIdx < _shedOrder.Count) {
    if (Over()) return n;
    SHED sh;
    long id = _shedOrder[_rIdx++];
    n++;
    if (!_shed.TryGetValue(id, out sh)) continue;
    _rb.Append(sh.Kind == 1 ? "BATT " : "BLOCK").Append(' ').Append(Trim(sh.Name, 30))
      .Append("  relief=").Append(Fx(sh.Relief)).Append("MW\n");
  }
  if (_shedOrder.Count == 0) _rb.Append("(nothing shed)\n");
  NextSec(R_EVENTS);
  return n;
}

int RepEvents() {
  if (_rIdx == 0) { _rb.Append("\n== EVENTS ==\n"); _rIdx = _events.Count; }
  int n = 0;
  while (_rIdx > 0) {
    if (Over()) return n;
    _rb.Append(_events[--_rIdx]).Append('\n');
    n++;
  }
  NextSec(R_PUBLISH);
  return n;
}

// ATOMIC. Custom Data is touched exactly once, with a complete report, at the very end. A
// partially written diagnostic is worse than none, because it reads like a finished one.
int RepPublish() {
  string rpt = _rb.ToString();
  if (rpt.Length > 60000) rpt = rpt.Substring(0, 60000) + "\n...report truncated...\n";
  string cd = Me.CustomData == null ? "" : Me.CustomData;
  int cut = cd.IndexOf(RPTMARK, OIC);
  string head = cut >= 0 ? cd.Substring(0, cut) : (cd.Length > 0 ? cd + "\n" : "");
  Me.CustomData = head + RPTMARK + "\n" + rpt;
  Ev("Diagnostic scan written (" + _rBi.Count + " blocks, " + _rUnknown + " unknown, "
     + _rTicks + " ticks)");
  _rb.Clear();
  _rBi = null; _rProd = null; _rBats = null; _rCons = null;
  _rs = R_IDLE; _rIdx = 0;
  Save();  // a scan is the largest single producer of learned values - do not lose them
  return 1;
}

// ---------------------------------------------------------------------------------------
// HELPERS
// ---------------------------------------------------------------------------------------
// Bit per ASCII letter present in the string. Only letters matter: every hint and GENSUP key
// begins with one.
static int Bit(char ch) {
  if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32);
  return (ch >= 'a' && ch <= 'z') ? 1 << (ch - 'a') : 0;
}
static int Mask(string s) {
  int m = 0;
  for (int i = 0; i < s.Length; i++) m |= Bit(s[i]);
  return m;
}
static int IdxOf(string[] a, string s) {
  if (s == null) return -1;
  s = s.Trim();
  for (int i = 0; i < a.Length; i++) if (string.Equals(a[i], s, OIC)) return i;
  return -1;
}
static string MW(double v) {
  double a = v < 0 ? -v : v;
  return (a < 10 ? v.ToString("0.00") : v.ToString("0.0")) + " MW";
}
static string Pct(double f) { return (f * 100).ToString("0.0") + "%"; }
static string Fx(double v) {
  double a = v < 0 ? -v : v;
  return a < 10 ? v.ToString("0.00") : v.ToString("0.0");
}
static string Trim(string s, int n = 28) {
  if (s == null) return "?";
  s = s.Replace('\r', ' ').Replace('\n', ' ');
  return s.Length <= n ? s : s.Substring(0, n - 1) + "~";
}
static string Clean(string s) {
  return s == null ? "" : s.Replace("|", "/").Replace("\n", " ").Replace("\r", "");
}
static string PadR(string s, int n) {
  var t = s == null ? "" : s;
  while (t.Length < n) t += " ";
  return t;
}
static string PadL(string s, int n) {
  if (s.Length >= n) return s;
  var t = s;
  for (int i = s.Length; i < n; i++) t = " " + t;
  return t;
}
static string DefOf(IMyTerminalBlock b) {
  var d = b.BlockDefinition;
  return d.TypeId.ToString() + "/" + (d.SubtypeId == null ? "" : d.SubtypeId);
}
