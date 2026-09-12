# Production machines — IO v1.7.7

GENERATED from `evidence/machines/*.json`. Do not hand-edit.

Build times are reproduced verbatim as displayed, including ranges.

## Advanced Assembler

IOPM machine token: `Advanced Assembler` · 17 entries · Components 5, Proficient Tools 8, Advanced Ammo 4

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Laser Emitter | `4.2s` | Glass `1`; Lightbulb `3`; SiliconWafer `2`; SilverIngot `2`; LithiumPaste `1`; AluminumIngot `3` |  |
| Lithium Power Cell | `2.5s` | AluminumPlate `1`; CopperWire `4`; LithiumPaste `10`; Rubber `2`; Carbon `3` |  |
| Reactor Comp. | `3.3s` | TitaniumPlate `1`; Concrete `3`; Carbon `3`; SilverIngot `5`; Plastic `10` |  |
| Superconducting Electromagnet | `4.2s` | TantalumIngot `1`; TitaniumIngot `2`; GoldWire `2`; Cryocooler `1` | result info identifies the product as 'Superconducting Magnet' |
| Tokamak Blanket Plate | `4.2s` | Ceramic `2`; LithiumPaste `2`; ArmoredPlate `1`; CopperIngot `2`; Thermocouple `1` |  |

### Proficient Tools

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Proficient Grinder | `4.2s` | Ceramic `1`; GoldWire `1`; Motor `1`; TitaniumPlate `1` |  |
| Proficient Hand Drill | `4.2s` | Ceramic `1`; LargeSteelTube `1`; GoldWire `2`; Motor `1`; TitaniumPlate `1` |  |
| Proficient Welder | `4.2s` | GoldWire `2`; Capacitor `2`; SmallSteelTube `1`; Plastic `1`; AluminumPlate `1` | UI ingredient 'Capacitor Cell' reconciles to the canonical Capacitor alias |
| RO-1 Rocket Launcher | `6.2s` | TitaniumIngot `6`; LargeSteelTube `4`; Plastic `4`; GoldWire `3` | uses magazine: Rocket |
| S-20A Pistol | `4.2s` | AluminumIngot `2`; Plastic `1`; SmallSteelTube `1` | uses magazine: S-20A Pistol Magazine |
| MR-50A Rifle | `4.2s` | TitaniumIngot `1`; Plastic `2`; SmallSteelTube `2`; CobaltIngot `1` | uses magazines: MR-50A Rifle Magazine; 5.56x45mm NATO magazine (shown NOT craftable) |
| Hydrogen Bottle | `3.3s ~ 16.7s` | LargeSteelTube `5`; NickelIngot `15` | capacity 400 L H2 shown; identical recipe on Fabricator and Assembler |
| Oxygen Bottle | `3.3s ~ 16.7s` | LargeSteelTube `5`; NickelIngot `15` | capacity 40 L O2 shown; identical recipe on Fabricator and Assembler |

### Advanced Ammo

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| MR-20 Rifle Magazine | `3.3s ~ 4.2s` | IronIngot `0.8`; CopperIngot `0.2`; Gunpowder `0.15` | capacity 20 |
| MR-8P Rifle Magazine | `3.3s ~ 4.2s` | IronIngot `0.8`; CopperIngot `0.2`; Gunpowder `0.15` | capacity 8 |
| MR-50A Rifle Magazine | `4.2s` | IronIngot `2`; CopperIngot `0.5`; Gunpowder `0.4` | capacity 50 |
| S-20A Pistol Magazine | `2.5s` | IronIngot `0.5`; CopperIngot `0.1`; Gunpowder `0.1` | capacity 20 |

## Assembler

IOPM machine token: `Assembler` · 16 entries · Components 7, Enhanced Tools 7, Light Ammo 2

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Acid Power Cell | `2.7s` | SteelPlate `1`; CopperIngot `3`; CopperWire `2`; Sulfur `5`; Ice `3` | ingredient 'Ice' is marked Not Craftable in the UI - mined, not manufactured |
| Capacitor Cell | `1s` | Rubber `1`; AluminumIngot `1`; Ceramic `1` |  |
| Cryogenic Cooler | `3.3s` | CopperWire `2`; LargeSteelTube `1`; Motor `1`; Thermocouple `1` |  |
| Medical Comp. | `2s` | IronIngot `12`; NickelIngot `8`; SilverIngot `8` |  |
| Motor | `1s ~ 3.3s` | Electromagnet `3`; LargeSteelTube `1`; CopperWire `3` |  |
| Radio-Comm Comp. | `1.3s ~ 4.4s` | SmallSteelTube `6`; CopperWire `12` |  |
| Solar Cell | `1s` | Glass `1`; CopperWire `1`; SiliconWafer `5`; IronIngot `3` | SUPERSEDES the earlier IronIngot:2 reading. The consolidated IO v1.7.7 handoff confirms IronIngot 3 and is authoritative. The v2.4.34 recipe was built from the superseded reading and is corrected in v2.4.37. |

### Enhanced Tools

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Enhanced Grinder | `3.3s` | Ceramic `1`; CopperWire `1`; Motor `1`; IronIngot `3` | used to craft Proficient Grinder (Advanced Assembler) and itself |
| Enhanced Hand Drill | `3.3s` | LargeSteelTube `1`; CopperWire `2`; Motor `1`; IronIngot `3` | used to craft Proficient Hand Drill (Advanced Assembler) and itself |
| Enhanced Welder | `3.3s` | CopperWire `2`; SmallSteelTube `1`; AluminumPlate `1` | used to craft Proficient Welder (Advanced Assembler) and itself |
| MR-20 Rifle | `3.3s` | IronIngot `2`; SmallSteelTube `2`; Plastic `2`; CobaltIngot `1` | uses MR-20 Rifle Magazine; also lists 5.56x45mm NATO magazine (Not Craftable) |
| MR-8P Rifle | `3.3s` | IronIngot `2`; SmallSteelTube `2`; Plastic `2`; SiliconWafer `1` | uses MR-8P Rifle Magazine; also lists 5.56x45mm NATO magazine (Not Craftable) |
| Hydrogen Bottle | `3.3s ~ 16.7s` | LargeSteelTube `5`; NickelIngot `15` | identical recipe on the Advanced Assembler |
| Oxygen Bottle | `3.3s ~ 16.7s` | LargeSteelTube `5`; NickelIngot `15` | identical recipe on the Advanced Assembler |

### Light Ammo

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| MR-20 Rifle Magazine | `3.3s ~ 4.2s` | IronIngot `0.8`; CopperIngot `0.2`; Gunpowder `0.15` | capacity 20 |
| MR-8P Rifle Magazine | `3.3s ~ 4.2s` | IronIngot `0.8`; CopperIngot `0.2`; Gunpowder `0.15` | capacity 8 |

## Auto Loom

IOPM machine token: `Auto Loom` · 2 entries · Components 2

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Synthetic Fabric | `1.3s` | Plastic `2` | producer now DIRECTLY OBSERVED as Auto Loom; identity previously live-observed |
| Parachute | `1.7s` | SyntheticFabric `10` | display name Parachute, physical output Canvas; producer directly observed |

## Cement Kiln

IOPM machine token: `Cement Kiln` · 1 entries · Components 1

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Concrete | `0.2s` | Gravel `25` | producer directly observed as Cement Kiln |

## Ceramics Furnace

IOPM machine token: `Ceramics Furnace` · 3 entries · Components 3

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Armor Glass | `2.3s` | AluminumIngot `3`; PotassiumNitrate `1` |  |
| Ceramic | `1.7s` | SiliconWafer `3`; Carbon `2` |  |
| Glass | `0.3s` | SiliconWafer `3` |  |

## Extruder

IOPM machine token: `Extruder` · 3 entries · Components 3

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Girder | `0.3s` | IronIngot `2` |  |
| Large Steel Tube | `0.7s ~ 2.2s` | IronIngot `4` |  |
| Small Steel Tube | `0.3s ~ 1.1s` | IronIngot `2` |  |

## Fabricator

IOPM machine token: `Fabricator` · 14 entries · Components 7, Basic Tools 6, Basic Ammo 1

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Alkaline Power Cell | `2s ~ 6.7s` | SteelPlate `1`; CopperIngot `1`; CopperWire `1`; NickelIngot `1`; Ice `1` | Not Craftable: Ice |
| Electromagnet | `0.7s ~ 3.3s` | IronIngot `1.5`; NickelIngot `1`; CopperWire `3` |  |
| Heating Element | `0.7s ~ 2.2s` | NickelIngot `5`; CopperIngot `5` |  |
| 10x Lightbulb | `1.7s ~ 5.6s` | Glass `1`; CopperWire `10` | **output x10** · EXPLICIT displayed output quantity 10 - the one proven non-unity yield |
| Basic Computer | `0.3s ~ 1.7s` | CopperWire `3`; SiliconWafer `2` |  |
| Construction Comp. | `0.3s ~ 1.7s` | IronIngot `5` |  |
| Metal Grid | `0.7s` | IronIngot `3`; NickelIngot `3`; CobaltIngot `3` |  |

### Basic Tools

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Grinder | `3.3s ~ 16.7s` | NickelIngot `1`; CopperWire `1`; IronIngot `3` | feeds Enhanced Grinder on the Assembler |
| Hand Drill | `3.3s ~ 16.7s` | LargeSteelTube `1`; CopperWire `1`; IronIngot `3` | feeds Enhanced Hand Drill on the Assembler |
| Hydrogen Bottle | `3.3s ~ 16.7s` | LargeSteelTube `5`; NickelIngot `15` | identical recipe also on Assembler and Advanced Assembler |
| Oxygen Bottle | `3.3s ~ 16.7s` | LargeSteelTube `5`; NickelIngot `15` | identical recipe also on Assembler and Advanced Assembler |
| Basic Pistol | `2.7s ~ 13.3s` | IronIngot `1`; NickelIngot `0.3`; SmallSteelTube `1` | uses Basic Pistol Magazine |
| Welder | `3.3s ~ 16.7s` | CopperWire `1`; SmallSteelTube `1`; IronIngot `3` | feeds Enhanced Welder on the Assembler |

### Basic Ammo

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Basic Pistol Magazine | `1s ~ 5s` | Gravel `10` | capacity 10 |

## Food Processor

IOPM machine token: `Food Processor` · 24 entries · Food Items 20, Harvesting 4

### Food Items

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Cooked Mammal Meat | `2.7s` | MammalMeatRaw `1` | Not Craftable: MammalMeatRaw |
| Cooked Insect Meat | `2.7s` | InsectMeatRaw `1` | Not Craftable: InsectMeatRaw |
| Meal Pack (Kelp Crisp) | `2.7s` | Algae `4` | Not Craftable: Algae |
| Meal Pack (Fruit Bar) | `2.7s` | Grain `1`; Fruit `1` | Not Craftable: Grain, Fruit |
| Meal Pack (Garden Slaw) | `2.7s` | Vegetables `2`; Fruit `1` | Not Craftable: Vegetables, Fruit |
| Meal Pack (Red Pellets) | `2.7s` | Fruit `2`; Vegetables `1` | Not Craftable: Fruit, Vegetables |
| Meal Pack (Chili) | `2.7s` | Vegetables `3`; Fruit `1` | Not Craftable: Vegetables, Fruit |
| Meal Pack (Flatbread) | `2.7s` | Grain `1`; Vegetables `1`; Fruit `1` | Not Craftable: Grain, Vegetables, Fruit |
| Meal Pack (Ramen) | `2.7s` | Grain `1`; Vegetables `1`; Algae `2` | Not Craftable: Grain, Vegetables, Algae |
| Meal Pack (Fruit Pastry) | `2.7s` | Grain `2`; Fruit `2` | Not Craftable: Grain, Fruit |
| Meal Pack (Veggie Burger) | `2.7s` | Grain `1`; Vegetables `2`; Mushrooms `1` | Not Craftable: Grain, Vegetables, Mushrooms |
| Meal Pack (Curry) | `2.7s` | Grain `1`; Vegetables `1`; Fruit `2` | Not Craftable: Grain, Vegetables, Fruit |
| Meal Pack (Green Pellets) | `2.7s` | Vegetables `1`; Algae `2`; CookedInsectMeat `1` |  |
| Meal Pack (Dumplings) | `2.7s` | Grain `2`; Vegetables `1`; Mushrooms `1` |  |
| Meal Pack (Spaghetti) | `2.7s` | Grain `3`; Vegetables `1`; Mushrooms `1` |  |
| Meal Pack (Lasagna) | `2.7s` | Grain `2`; Vegetables `1`; CookedMammalMeat `1` |  |
| Meal Pack (Burrito) | `2.7s` | Grain `2`; Vegetables `2`; CookedMammalMeat `1` |  |
| Meal Pack (Frontier Stew) | `2.7s` | Grain `1`; Mushrooms `2`; Algae `2`; CookedInsectMeat `1` |  |
| Meal Pack (Seared Sabiroid) | `2.7s` | Grain `1`; Vegetables `1`; Mushrooms `1`; Algae `1`; CookedInsectMeat `1` |  |
| Meal Pack (Steak Dinner) | `2.7s` | Grain `1`; Vegetables `1`; Mushrooms `1`; Fruit `1`; CookedMammalMeat `1` |  |

### Harvesting

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Harvest Fruit Seeds | `2.7s` | Fruit `1` | Not Craftable: Fruit |
| Harvest Grain Seeds | `2.7s` | Grain `1` | Not Craftable: Grain |
| Harvest Vegetable Seeds | `2.7s` | Vegetables `1` | Not Craftable: Vegetables |
| Harvest Mushroom Spores | `2.7s` | Mushrooms `1` | Not Craftable: Mushrooms |

## Microelectronics Factory

IOPM machine token: `Microelectronics Factory` · 5 entries · Components 5

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Advanced Computer | `0.7s` | GoldWire `3`; SiliconWafer `2`; Plastic `2` |  |
| Basic Computer | `0.3s ~ 1.7s` | CopperWire `3`; SiliconWafer `2` |  |
| Sensor Cluster | `1.3s` | Glass `1`; BasicComputer `2`; CopperWire `3`; SiliconWafer `3`; NickelIngot `3` |  |
| Display | `1.3s` | CopperWire `2`; SiliconWafer `3`; Plastic `2`; SilverIngot `0.1`; BasicComputer `1`; NickelIngot `1` |  |
| Thermocouple | `1.3s` | SiliconWafer `2`; CopperWire `3`; Plastic `1`; AluminumIngot `2` |  |

## Munitions Factory

IOPM machine token: `Munitions Factory` · 26 entries · Large Ammo 19, Hand Ammo 7

### Large Ammo

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Gunpowder | `0.8s` | PotassiumNitrate `6`; Carbon `2`; Sulfur `2` | **output x10** · CLOSES the manufactured-missing leaf. OUTPUT YIELD 10, live-proven: one manual blueprint execution placed 10 Gunpowder in the machine output. The UI does not display a yield for this recipe - absence of a displayed yield is NOT evidence of yield 1. Identity live-observed as MyObjectBuilder_Ingot/Magnesium; PotassiumNitrate live-confirmed as MyObjectBuilder_Ingot/Niter in the same dump. |
| Explosives | `1.7s` | IronIngot `1`; Gunpowder `4` | producer directly observed as Munitions Factory |
| AP Autocannon Clip | `2.5s` | IronIngot `20`; CopperIngot `6`; Gunpowder `8` | capacity 16 |
| DU Autocannon Clip | `2.5s` | IronIngot `10`; DepletedUranium `10`; CopperIngot `6`; Gunpowder `8` | capacity 16 |
| DU Gatling Ammo Box | `2.1s` | IronIngot `20`; DepletedUranium `10`; CopperIngot `10`; Gunpowder `10` | capacity 140 |
| AP Artillery Shell | `3.3s` | IronIngot `8`; CopperIngot `3`; Gunpowder `3.5` |  |
| DUAP Artillery Shell | `3.3s` | IronIngot `4`; DepletedUranium `4`; CopperIngot `3`; Gunpowder `3.5` |  |
| HE Artillery Shell | `3.3s` | IronIngot `2`; CopperIngot `3`; Gunpowder `10` |  |
| Large AP Railgun Sabot | `5.8s` | IronIngot `10`; CopperIngot `5`; AluminumIngot `10`; TitaniumIngot `10` |  |
| Large DUAP Railgun Sabot | `5.8s` | IronIngot `10`; CopperIngot `5`; AluminumIngot `10`; DepletedUranium `10` |  |
| AP Assault Cannon Shell | `1.7s` | IronIngot `4`; CopperIngot `1.5`; Gunpowder `1.5` |  |
| DUAP Assault Cannon Shell | `1.7s` | IronIngot `2`; DepletedUranium `2`; CopperIngot `1.5`; Gunpowder `1.5` |  |
| HE Assault Cannon Shell | `1.7s` | IronIngot `1`; CopperIngot `1.5`; Gunpowder `4` |  |
| HE Rocket | `2.1s` | LargeSteelTube `5`; CopperWire `5`; Gunpowder `30` |  |
| Light Turret Box | `1.2s` | IronIngot `15`; CopperIngot `5`; Gunpowder `5` | capacity 50 |
| Rocket | `2.1s` | LargeSteelTube `5`; CopperWire `5`; Gunpowder `20` |  |
| Gatling Ammo Box | `2.1s` | IronIngot `30`; CopperIngot `10`; Gunpowder `10` | capacity 140 |
| Small AP Railgun Sabot | `5.8s` | IronIngot `2`; CopperIngot `1`; AluminumIngot `2`; TitaniumIngot `2` |  |
| Small DUAP Railgun Sabot | `5.8s` | IronIngot `2`; CopperIngot `1`; AluminumIngot `2`; DepletedUranium `2` |  |

### Hand Ammo

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| MR-20 Rifle Magazine | `3.3s ~ 4.2s` | IronIngot `0.8`; CopperIngot `0.2`; Gunpowder `0.15` | capacity 20 |
| S-10E Pistol Magazine | `2.1s` | IronIngot `0.3`; CopperIngot `0.1`; Gunpowder `0.1` | capacity 10 |
| S-20A Pistol Magazine | `2.5s` | IronIngot `0.5`; CopperIngot `0.1`; Gunpowder `0.1` | capacity 20 |
| MR-8P Rifle Magazine | `3.3s ~ 4.2s` | IronIngot `0.8`; CopperIngot `0.2`; Gunpowder `0.15` | capacity 8 |
| MR-50A Rifle Magazine | `4.2s` | IronIngot `2`; CopperIngot `0.5`; Gunpowder `0.4` | capacity 50 |
| Basic Pistol Magazine | `1s ~ 5s` | Gravel `10` | capacity 10 |
| MR-30E Rifle Magazine | `4.2s` | IronIngot `1.2`; CopperIngot `0.4`; Gunpowder `0.25` | capacity 30 |

## Nano-Assembler

IOPM machine token: `NanoAssembler;Nano-Assembler` · 13 entries · Components 5, Elite Tools 6, High-Tech Ammo 2

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Electron-Matrix Cell | `6.2s` | ArmorGlass `1`; TantalumIngot `0.5`; LaserEmitter `1`; GoldWire `2`; Polymer `2` |  |
| Full-Spectrum Solar Cell | `2.5s` | ArmorGlass `1`; TantalumIngot `0.2`; GoldWire `1`; SiliconWafer `5`; Plastic `3`; TitaniumIngot `0.5` |  |
| Gravity Comp. | `6.2s` | TantalumIngot `5`; GoldWire `30`; CobaltIngot `25`; SilverIngot `20`; Electromagnet `15` |  |
| Thruster Comp. | `2.5s` | Electromagnet `6`; CobaltIngot `10`; GoldWire `3`; PlatinumIngot `0.5` |  |
| Quantum Computer | `1.2s` | TantalumIngot `0.1`; PlatinumIngot `0.2`; GoldWire `6`; AdvancedComputer `2`; AluminumPlate `2` |  |

### Elite Tools

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| PRO-1 Rocket Launcher | `8.3s` | TitaniumIngot `7`; SiliconWafer `2`; LargeSteelTube `4`; Plastic `4`; GoldWire `3` | uses Rocket |
| Elite Grinder | `4.2s` | LaserEmitter `1`; TantalumIngot `0.25`; GoldWire `2`; Motor `1`; TitaniumPlate `1` |  |
| S-10E Pistol | `5s` | AluminumIngot `2`; Plastic `1`; SmallSteelTube `1`; Glass `1` | uses S-10E Pistol Magazine |
| Elite Hand Drill | `4.2s` | TantalumIngot `0.5`; LargeSteelTube `1`; PlatinumIngot `1`; GoldWire `4`; Motor `1`; TitaniumPlate `1` |  |
| MR-30E Rifle | `4.2s` | TitaniumIngot `1`; Plastic `2`; SmallSteelTube `2`; TantalumIngot `0.1` | uses MR-30E Rifle Magazine; vanilla 5.56 magazine shown Not Craftable |
| Elite Welder | `4.2s` | GoldWire `2`; PlatinumIngot `0.5`; Capacitor `2`; LaserEmitter `1`; AluminumPlate `1` | UI ingredient "Capacitor Cell" reconciles to the canonical Capacitor alias |

### High-Tech Ammo

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| S-10E Pistol Magazine | `2.1s` | IronIngot `0.3`; CopperIngot `0.1`; Gunpowder `0.1` | capacity 10 · also observed on Munitions Factory |
| MR-30E Rifle Magazine | `4.2s` | IronIngot `1.2`; CopperIngot `0.4`; Gunpowder `0.25` | capacity 30 · also observed on Munitions Factory |

## Nuclear Reprocessor

IOPM machine token: `Nuclear Reprocessor` · 1 entries · Reprocessing 1

### Reprocessing

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Uranium Reprocessing | `3.3s` | SpentFuel `10`; Ice `5`; Sulfur `2`; PotassiumNitrate `3` | Not Craftable: SpentFuel, Ice · **MULTI-OUTPUT**: Nuclear Fuel x3; Depleted Uranium x7; Nuclear Waste x10 · ARCHITECTURE GAP: three simultaneous outputs. IOPM models one blueprint -> one product, so this cannot be represented without coproduct accounting. Do NOT force it into the one-output model. |

## Plate Stamp

IOPM machine token: `Plate Stamp` · 5 entries · Components 5

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Armored Plate | `1s` | SteelPlate `1`; TitaniumPlate `1` |  |
| Composite Plate | `1.7s` | Ceramic `0.5`; TantalumIngot `0.09`; DepletedUranium `0.1`; ArmoredPlate `0.75` |  |
| Aluminum Plate | `0.3s` | AluminumIngot `5` |  |
| Steel Plate | `0.3s ~ 1.7s` | IronIngot `20` |  |
| Titanium Plate | `0.3s` | TitaniumIngot `12` |  |

## Survival Kit

**DEFERRED** — AllowSurvivalKitFallback=false, so IOPM never queues to it. Deliberately deferred, NOT unaudited - do not let a later pass mistake this for an oversight.

## Synthetics Factory

IOPM machine token: `Synthetics Factory` · 4 entries · Components 4

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Asphalt | `0.2s` | Gravel `12`; OilSand `3` | Not Craftable: OilSand |
| Plastic | `0.5s` | Polymer `1.5` |  |
| Rubber | `0.7s` | Polymer `2` |  |
| Synthetic Polymer | `1.7s` | SiliconWafer `1`; Carbon `1.5`; Sulfur `0.5` | blueprint displayed as Synthetic Polymer; result item explicitly shown as Polymer |

## Wire Drawer

IOPM machine token: `Wire Drawer` · 3 entries · Components 3

### Components

| Output | Build time | Inputs | Notes |
|---|---|---|---|
| Copper Wire | `0.3s ~ 1.7s` | CopperIngot `1` |  |
| Gold Wire | `0.3s` | GoldIngot `0.6` |  |
| Superconductor | `1.7s` | Rubber `3`; GoldWire `15` |  |
