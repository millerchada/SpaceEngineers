# A0a baseline — CREATIVE mode, IO 1.7.7 test station

Captured 2026-09-19 16:00:30, script v0.1.2, before the world was switched to survival.
This is the control half of the experiment for **limitation 6** (is `IMyPowerProducer.MaxOutput`
a nameplate, or the figure actually achievable?). In creative, producers have effectively
unlimited fuel/charge but keep their normal maximum output limits — so any producer whose
`max=` is *lower* than its nameplate here is being limited by something other than fuel.

Re-run `scan` on the same grid after the switch to survival, with nothing else changed, and
compare the `max=` column below.

## Totals

    Blocks=150 Functional=147 Producers=9 Batteries=1 Constructs=1 (116 grids)
    GenCur=0.00MW  GenMax=58.5MW  Demand=2.37MW  Reserve=56.1MW  N-1=6.10MW
    Potential=32.1MW  Coverage=13.0%  Known=18  Unknown=120

`GenCur=0.00` with a discharging battery is correct: SE dispatches stored/renewable supply
ahead of fuel-burning generation, so the turbine and engine idle while the battery covers the
2.37 MW load.

## Producers — the figures to compare

| Block | Definition | out= | **max=** | Nameplate |
|---|---|---|---|---|
| Steam Turbine - Mirrored | `MyObjectBuilder_HydrogenEngine/SteamTurbineMirrored` | 0.00 | **50.0** | 50 MW |
| Hydrogen Engine | `MyObjectBuilder_HydrogenEngine/LargeHydrogenEngine` | 0.00 | **5.00** | 5 MW |
| Large Wind Turbine | `MyObjectBuilder_WindTurbine/LargeWindTurbine` | 0.00 | 3.19 | varies |
| Solar Panel ×3 | `MyObjectBuilder_SolarPanel/LargeBlockSolarPanel` | 0.00 | 0.03 | varies |
| Solar Panel ×2 | `MyObjectBuilder_SolarPanel/LargeBlockSolarPanel` | 0.00 | 0.06 | varies |
| Solar Panel ×1 | `MyObjectBuilder_SolarPanel/LargeBlockSolarPanel` | 0.00 | 0.08 | varies |

**Already established by this capture:** solar and wind `MaxOutput` is dynamic — the six solar
panels report three different values simultaneously, tracking orientation, and the wind turbine
has moved between 3.19 / 4.35 / 5.46 MW across captures. For those block types `MaxOutput` is
the achievable figure, not a nameplate, so `Available Generation` is honest about them.

**Open:** the steam turbine and hydrogen engine both sit exactly on nameplate here, which is
expected under unlimited fuel and therefore proves nothing either way.

**Verdict rule for the survival re-run:**

* Steam turbine `max=` drops below 50.0 with an unfinished well → tracks availability,
  limitation 6 is cleared.
* Steam turbine `max=` stays at 50.0 while `out=` is capped lower → nameplate, and
  `Available Generation` / `Reserve` / `N-1` are upper bounds on a fuel-fed base.

## Batteries

    Warfare Lithium Battery  Auto  stored=3.00/3.00MWh  in=0.00/12.0MW  out=2.37/12.0MW

## Consumers by definition

    2x  MyProgrammableBlock/LargeProgrammableBlockReskin   Control       max=0.00   unknown
    1x  AirtightSlideDoor/LargeBlockSlideDoor              Decor         max=0.00   unknown
    1x  AirVent/AirVentFanFull                             LifeSupport   max=0.10   detail
    1x  BatteryBlock/LargeBlockBatteryBlockWarfare2        Battery       max=12.0   iface
    1x  OxygenTank/                                        Fuel          max=0.00   detail
    1x  OxygenGenerator/                                   Fuel          max=1.00   detail
    1x  OxygenGenerator/GeothermalWellHead                 Generation    max=0.50   detail
  115x  ExtendedPistonBase/LargePistonBase                 Mechanical    max=0.00   unknown
    1x  Door/SlidingHatchDoor                              Decor         max=0.00   unknown
    1x  OxygenTank/SteamTank                               Generation    max=0.00   detail
    1x  OxygenTank/LargeHydrogenTankSmall                  Fuel          max=0.00   detail
    1x  Refinery/LargeRefinery                             Production    max=3.00   detail
    1x  Refinery/Blast Furnace                             Production    max=2.00   detail
    1x  Assembler/Fabricator                               Production    max=1.00   detail
    1x  Assembler/LargeAssemblerNew                        Production    max=1.50   detail
    1x  Assembler/WireDrawer                               Production    max=1.50   detail
    1x  Assembler/Extruder                                 Production    max=1.50   detail
    1x  Assembler/PlateStamp                               Production    max=1.50   detail
    1x  Assembler/CementKiln                               Production    max=3.00   detail
    1x  Assembler/SiliconFuser                             Production    max=3.50   detail
    1x  Drill/LargeBlockGravDrill                          Industrial    max=0.00   detail
    1x  RadioAntenna/LargeBlockCompactRadioAntenna         Comms         max=0.02   detail
    1x  MedicalRoom/LargeMedicalRoom                       LifeSupport   max=0.00   unknown

Classification confirmed correct on every block present, including the two that are easy to get
wrong: the geothermal well head and steam tank are `Generation` (critical power-generation
support, never shed) rather than `Fuel`, while the hydrogen tank correctly stays `Fuel`.

## Unknown definitions — 120 blocks, 5 definitions

    MyProgrammableBlock/LargeProgrammableBlockReskin   detail: (empty)
    AirtightSlideDoor/LargeBlockSlideDoor              detail: (empty)
    Door/SlidingHatchDoor                              detail: (empty)
    MedicalRoom/LargeMedicalRoom                       detail: (empty)
    ExtendedPistonBase/LargePistonBase                 detail: Attached~Current position: 6.2m

None publish an input figure, so none can be modelled without a measured catalog row. Values
established out-of-band this session, from BuildInfo (which renders client-side and is NOT
visible to a programmable block):

    piston              2 kW  (max 2 kW)     -> 0.002
    sliding door       10 W   (max 1 kW)     -> 0.001   (use the MAX: doors draw while moving)
    sliding hatch door 30 W   (max 30 W)     -> 0.00003
    programmable block 500 W                 -> 0.0005
    medical room       not yet measured

Independent cross-check of the piston figure: switching all 39 pistons off moved demand
0.19 → 0.12 MW, i.e. ~1.79 kW each, against BuildInfo's 2 kW — agreement within the 0.01 MW
display rounding.
