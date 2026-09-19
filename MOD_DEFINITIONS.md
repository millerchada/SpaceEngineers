# The mod's own definition files are on disk, and they are readable

Discovered 2026-09-19 while building IO_Power_Control. This is a repo-level note because it
changes how *every* script in this repo should acquire game data, not just that one.

## Where

    Industrial Overhaul v1.7.7
      D:\SteamLibrary\steamapps\workshop\content\244850\2344068716\

    Base game (loaded first; the mod overrides it)
      D:\SteamLibrary\steamapps\common\SpaceEngineers\Content\Data\

Both are plain XML (`.sbc`) plus C# (`.cs`), readable with ordinary file tools. No game, no
server, no deploy cycle.

## What they answer

    Data/Blueprints_IO.sbc              108 blueprints
    Data/Blueprints_Vanilla.sbc          84 blueprints (mod's overrides of vanilla recipes)
    Data/Blueprints_Incinerator.sbc     102 blueprints
      -> blueprint SubtypeId, Prerequisites (item + amount), Result (item + AMOUNT),
         BaseProductionTimeInSeconds, IsPrimary

    Data/BlueprintClasses_IO.sbc         46 classes
    Data/Cubeblocks_IO/CubeBlocks_Production.sbc   44 machine -> class links
      -> which machine can actually run which blueprint

    Data/PhysicalItems_IO.sbc            56 items
    Data/PhysicalItems_Vanilla.sbc       43 items
      -> authoritative TypeId/SubtypeId for every item

    Data/Cubeblocks_IO/*.sbc, CubeBlocks_Vanilla/*, CubeBlocks_DLC/*
      -> RequiredPowerInput, OperationalPowerConsumption, PowerConsumptionMoving,
         RequiredIdlePower, MaxPowerOutput, FuelCapacity, IceToGasRatio, inventory volumes

    Data/Scripts/*.cs
      -> the mod's actual behavioural logic (GeothermalWell.cs, Reactors/, Animations/)

## What this cost us before we knew

IOPM discovered this domain empirically, over many releases: the recipe-pending set closing at
v2.4.35, a dependency-closure audit at v2.4.36, the SolarCell ingredient conflict at v2.4.37,
Gunpowder's output yield at v2.4.38 ("live-measured"), its blueprint id at v2.4.39.

The Gunpowder yield, in `Blueprints_Vanilla.sbc`, in full:

```xml
<Blueprint>
  <Id><TypeId>BlueprintDefinition</TypeId><SubtypeId>Gunpowder</SubtypeId></Id>
  <Prerequisites>
    <Item Amount="6" TypeId="Ingot" SubtypeId="Niter" />
    <Item Amount="2" TypeId="Ingot" SubtypeId="Carbon" />
    <Item Amount="2" TypeId="Ingot" SubtypeId="Sulfur" />
  </Prerequisites>
  <Result Amount="10" TypeId="Ingot" SubtypeId="Magnesium" />
  <BaseProductionTimeInSeconds>2</BaseProductionTimeInSeconds>
</Blueprint>
```

Blueprint id, prerequisites, result, yield and time - the whole of what took several releases
to establish. Note also that a blueprint named `Gunpowder` yields `Ingot/Magnesium`: exactly
the identity trap `IO_Blueprint_Sniffer` and `IO_Item_Identity_Dump` exist to catch.

## The rule this does NOT change

**A definition file is the DECLARED state. The game is the ACTUAL state.** They can differ:
mod load order, other mods, a version drift between the workshop copy on this machine and what
the server runs, and runtime logic that overrides a declaration.

So the correct use is **generate from files, then verify against the game** - never "read the
file and believe it". The live dump scripts keep their value as verification; what changes is
that they stop being the tool of first discovery.

Nothing that is purely runtime is in these files at all: conveyor reachability, stalls, actual
inventory, instruction budgets, dock topology. The engineering half of IOPM would not have been
helped by any of this.

## The pattern to follow

`IO_Power_Control/tools/extract_io_catalog.py` is the worked example. It reads the base game
directory first and lets the mod override it - the order the game loads them - and emits a
generated table straight into the script source, marked GENERATED with the regenerate command
in a comment beside it.

Its governing rule is worth copying verbatim: **a block that declares no figure gets no row.**
Nothing is estimated to fill a gap, because an invented value is worse than a disclosed unknown
- it inflates the model *and* the coverage figure that is supposed to disclose the gap.

### Result so far (IO_Power_Control)

338 subtypes extracted. Five independent cross-checks against the live server agree exactly:
`CementKiln` 3 MW, `LargeRefinery` 3 MW and `GeothermalWellHead` 0.5 MW against their own
`DetailedInfo`; `LargePistonBase` 0.002 and `LargeBlockSlideDoor` 0.001 against BuildInfo and a
measured demand delta.

### A limit found the same way

Adding the base game's Data directory to that scan yields only **five** extra subtypes. Many
vanilla blocks - sliding hatch doors, medical rooms, programmable blocks - declare no power tag
in any `.sbc`, because their consumption is hardcoded in the game's C# and applied through a
runtime resource-sink component. Tools that read the live component (BuildInfo) can show those
figures; definition files cannot. Expect a similar floor in any other domain.

## Reading the mod's C# pays too

`Data/Scripts/GeothermalWell.cs` settled two contradictory in-game tooltips and produced a
formula that then matched the game to four significant figures. It also exposed two defects in
the mod's own documentation - see `IO_Power_Control/CHANGELOG.md` for the full working. Where a
mod's described behaviour and its observed behaviour disagree, its source is the arbiter.

## Proposed next: IO_Production_Manager

Not started; recorded here so it can be picked up in its own session.

Generate IOPM's recipe and item-identity tables from the `.sbc` files, then **diff them against
the evidence IOPM already holds** in `IO_Production_Manager/evidence/` and `docs/`. Three
outcomes, all worth having:

1. recipes IOPM has **wrong** - silent production defects
2. recipes IOPM is **missing** - capability it does not know it has
3. places where the game **disagreed** with the declarations - the most interesting class, and
   the one that retroactively justifies the live-measurement work

It is a read-only diff against a mature, working script: it risks nothing and changes no
runtime behaviour until someone decides a specific discrepancy is worth acting on.
