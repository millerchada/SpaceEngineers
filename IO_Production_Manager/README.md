# IO Production Manager (IOPM)

Warehouse sorting and balancing, production planning, stall recovery,
dock-aware logistics and ship loadout servicing, for **Industrial Overhaul
v1.7.7**. Interoperates with **GOAT / GSIM** sorting tags.

Version history: [CHANGELOG.md](CHANGELOG.md).

## Deploying — never paste the `.cs` file

The `.cs` is **source**. It is now larger than the PB's own 100,000-character
ceiling, so it cannot be pasted into a block at all. Build the artifact:

```bash
python ../build_pb.py IO_Production_Manager_v2.4.23.cs
# -> IO_Production_Manager_v2.4.23.min.cs   <-- paste THIS into the block
```

v2.4.23: source 117,084 -> artifact 89,673 chars (10,327 headroom).

Comments and indentation cost ~20,000 characters and mean nothing at runtime,
but stripping them from the source would destroy the documentation that keeps
this maintainable — so we keep both. `build_pb.py` refuses to write the
artifact unless every string literal survives byte-identical, code-context
`{}()[];` counts match the source, and the output is stable under a second
pass.

## Block tags

Put these in a block's **name**. Matching is case-insensitive substring.

### Warehouse containers (same construct as the PB, `IMyCargoContainer` only)

| Tag | Meaning |
|---|---|
| `Ores` `Ingots` `Components` `Ammo` `Tools` `Consumables` `Seeds` `Misc` | Category destination. A container may carry several. |
| `Overflow` | Spill-over pool. Exclusive — combining it with a category is ignored with a warning. |
| `[Ignore]` `[IOPM-Ignore]` `[Locked]` `!GSIM-Locked` | Container untouched entirely. |
| `[No Sorting]` `!GSIM-NoSorting` | Container untouched (same as above, for GOAT compatibility). |

Only an `IMyCargoContainer` can be a warehouse destination. Tagging anything
else — an O2/H2 generator, an assembler — does nothing. See **Block behaviour
by type and setting** below.

### Screens

| Tag | Meaning |
|---|---|
| `[StatusScreen]` `!StatusScreen` `!GSIM-StatusScreen` | Operational status panel. |
| `[Inventory]` `[IOPM-Inventory]` `[IOPM-Stock]` | Stock table (Item / Qty / Quota / Queued). |

The PB's own screen always shows a compact summary and needs no tag.

### Connectors — dock boundary policy

Checked on **either** side of a connected pair, and aggregated across every
connector reaching the same construct, so it never depends on block order.

| Tag | Meaning |
|---|---|
| `[No GOAT]` `!GSIM-NoGOAT` `[Ignore]` `[IOPM-Ignore]` | Skip that whole remote construct: no scan, no unload, no loadout servicing. **The only tag that reduces instruction cost.** |
| `[No Sorting]` `!GSIM-NoSorting` | Do not unload that construct's ordinary cargo, but **still service its `[Stock]` loadout containers.** Does not suppress the local connector's own inventory — that enables the *docking-T* pattern: one tagged and one plain connector on a berth, so the pilot chooses whether the ship gets unloaded by where they park. |

### Remote blocks on a docked construct

Precedence: **Locked/Ignore → `[Stock]` loadout → No-Sorting → ordinary unload.**

| Tag | Meaning |
|---|---|
| `[Ignore]` `[IOPM-Ignore]` `[Locked]` `!GSIM-Locked` | Untouchable: not unloaded, not sourced, not loadout-serviced. |
| `[Stock]` `!GSIM-Stock` | **Managed loadout container.** Contents maintained to quota while docked. Never unloaded like ordinary cargo. |

Ordinary unload only ever reads `IMyCargoContainer`, `IMyShipConnector` and
`IMyShipDrill`. Reactors, turrets, cockpits, O2/H2 generators and production
blocks are never drained.

## Block behaviour by type and setting

Not everything is driven by tags. Much of what IOPM does — or refuses to do —
follows from a block's **type** or its **own settings**, with nothing to type
into a name. There is no "Ejector" tag; there is an ejector *setting*.

| Block / setting | Behaviour |
|---|---|
| **Ejector** — any connector with `ThrowOut` **on** | **Left alone**, locally *and* on a docked ship. Its contents are never recovered into the warehouse. Throwing material away is the point of the block, and rescuing it fights that — on a docked ship these are commonly sorter-fed waste chutes. Still usable as a dock anchor. |
| **Connector** with `ThrowOut` off | Treated as transit cargo: contents are routed into the warehouse, and count as local on-hand stock while they wait. Never a balancing destination. |
| **Cargo container** | The only block type that can be a warehouse destination. |
| **Refinery** | Output inventory is evacuated. Its queue and inputs are **never** touched — refinery management is out of scope. |
| **Assembler in Disassembly mode** | Ignored entirely: no queue scanning, no production, no stall detection, no input evacuation. Grinding down is manual work. |
| **Basic Assembling Bench** | Never given work. Manual-only by design. |
| **Survival Kit** | Never given work unless `[Production] AllowSurvivalKitFallback=true`. |
| **Other production blocks** | Output is always a sorting source; input is staged and tracked for queue support. |
| **Welders, grinders, drills, reactors, turrets, cockpits, connectors** | Never a *local* sorting source or destination. On a **docked** construct, cargo containers, connectors and drills are unload sources; reactors, turrets, cockpits and O2/H2 generators are never drained. |
| **Text surface** on any block | Becomes a panel only if tagged. The PB's own screen always shows a compact summary with no tag. |

### Items with special handling

| Item | Behaviour |
|---|---|
| **`Heat`** (and any process resource) | Excluded from the warehouse system entirely — never counted, moved, or balanced. A container holding one is also skipped for slot ordering, so its slots are left untouched. |
| **`Grain`**, **`Algae`** | Routed to Consumables, overriding the broad `PhysicalObject` → Tools rule. Matched on the **full** `TypeId/SubtypeId` — `SeedItem/Grain` is a *different item* and correctly goes to Seeds. |
| **`SpaceCredit`** | Routed to Misc rather than Tools. |

## Loadout containers

A remote container tagged `[Stock]` keeps its contents at quota while docked.
Quotas live in that **container's** Custom Data, inside a bounded block:

```
@GOAT-Stock Definitions START
~~~~~~~~ Components ~~~~~~~~
SteelPlate=500
Motor=100M
GatlingAmmo=200L
Ice=All
@GOAT-Stock Definitions END
```

| Form | Meaning |
|---|---|
| `Item=100` | Exact. Top up to 100, remove anything above. |
| `Item=100M` | Minimum. Top up to 100, never remove. |
| `Item=100L` | Maximum. Never add, remove anything above 100. |
| `Item=All` | Fill from available stock, never remove on a quota basis. |
| trailing `P` | GOAT pinning metadata, ignored (`100MP` = `100M`). |

Items **not listed are never touched.** If a container's Custom Data is
completely empty, IOPM seeds an inert template listing every item it can
resolve, grouped by category, all at `0M` — edit a number to activate it.
Anything already in that Custom Data is never altered.

**`0M` is load-bearing.** A bare `Item=0` is an *exact* quota meaning "remove
everything above 0", so a template of bare zeros would strip the container.

## Base borrowing

A docked loadout may draw base stock down to, but never past, a floor:

```
minimumBaseReserve = StockTarget × (1 − LoadoutBorrowPercent/100)
```

Everything above the target is freely available, plus that percentage of the
target. Material already committed to an existing production queue is
subtracted first. Items with no `[Stock]` target have no floor. A shortage is
reported, never manufactured — a ship's demand does not create production
demand.

The floor limits what IOPM will **lend**. It does not stop you spending your
own stock below it.

## Versioning convention

Each version is an **immutable file**: a copy plus a delta, never edited in
place, so a regression isolates to one delta. The deployed version and the
current candidate both live here; superseded ones are in `archive/`, moved with
`git mv` so `git log --follow` still traces them.

Two read-only companion tools sit in sibling folders and are worth knowing
about:

- [`../IO_Item_Identity_Dump`](../IO_Item_Identity_Dump/) — verify an item's
  real `SubtypeId`. **Run it before trusting any item identity**; see the Glass
  incident below.
- [`../IO_Blueprint_Sniffer`](../IO_Blueprint_Sniffer/) — capture the real
  blueprint definition IDs the game uses.

## Hard constraints

| Limit | Value |
|---|---|
| PB source ceiling | 100,000 characters |
| Runtime instructions | 50,000 **per invocation** |
| Observed peak (v2.4.21) | 25,250, phase `DockScan` (35 containers, 9 docked constructs, 160 unload sources) |

`Sorting` grows with **container count**; `DockScan` is independent of docked
construct count since v2.4.13. So the peak tracks the base you build, not other
players' traffic.

The script runs a **cooperative multi-tick phase architecture** — exactly one
phase per `Update10` tick. A single monolithic cycle exceeded the instruction
limit; do not re-collapse the phases.

## Invariants — do not break these

- **`AddQueueItem()` is the only production-queue mutation.** No `ClearQueue`,
  no removal, no reduction, no reorder. Existing and manual queues are sacred.
- **Remote/docked inventory never enters base `_onHand` or `[Stock]`.** A ship
  docking must not change base stock numbers.
- **Config is snapshotted per cycle.** A Custom Data change mid-cycle is
  deferred to `PHASE_IDLE`, so `ApplyPlan` can never execute a plan built under
  different configuration.
- **Dock transfers happen before `BuildPlan`.** If docking moves local
  inventory, planning must see the post-transfer state.
- **The script owns only `[IOPM.*]` Custom Data sections.** User sections are
  preserved. GOAT container Custom Data is read-only.
- Refinery management is out of scope: outputs are evacuated, queues and inputs
  are never touched.

## Docking / loadout UAT status

Confirmed on a live multiplayer server, large base, v2.4.5 - v2.4.22.

| Behaviour | Status | Evidence |
|---|---|---|
| Loadout container discovery | PASS | LoadoutContainers=1 on a docked grid |
| GOAT bounded-section parse | PASS | hand-written `Ice=1000` honoured |
| Item resolution, observed-subtype fallback | PASS | `Ice` resolves via base inventory, not a table |
| Template seeding into EMPTY Custom Data | PASS | 25-line `0M` template, byte-exact |
| Existing Custom Data never altered | PASS | populated container left untouched |
| Unlisted items never swept | PASS | 1,000 Ice survived a components-only template |
| Exact quota fill | PASS | filled to exactly 1,000, stopped |
| `M` minimum (never removes) | PASS | 500M kept container contents |
| `L` maximum (removes, never adds) | PASS | excess returned to base, nothing added |
| Remote->base push (`PushExcess`) | PASS | base Motor 744 -> 1,201 |
| Remote inventory excluded from base stock | PASS | base read 921/750 while ship held Motors |
| Borrow formula + hard floor | PASS | stopped dead at 750 = 1000 x (1-25/100) |
| Natural replenishment (no faked demand) | PASS | Need=57 = 1000-921-22 |
| Recursive expansion arithmetic | PASS | 79 Motors -> +79 LargeSteelTube, +237 Electromagnet |
| Fractional ingredient math | PASS | one ElectronMatrix + one FSSolarCell consumed exactly 0.7 TantalumIngot (0.5 + 0.2) |
| Non-unity output yields | PASS | 30 Lightbulbs (x10 yield) consumed exactly 3 Glass |
| All 37 output subtypes verified | PASS | every managed item credited Stock once crafted |
| Shortage is diagnostic-only, no new root | PASS | Short=1 WaitingFor=Motor, zero jobs added |
| Over-commit recovery (queues are sacred) | PASS | CopperWire 5,105/5,000 + 2,262 queued, JobsAdded=0 |
| `All` modifier | NOT TESTED | lowest value; least likely modifier to be used |
| Recursive manual-queue protection vs loadout | PASS | 1,096 manually queued Superconductors derived 16,440 GoldWire / 3,288 Rubber / 11,892.6 GoldIngot exactly |
| Multiple loadout containers, no double-spend | PASS | two containers each wanting 500 Motors parked the base at exactly 750, both short |
| `[No Sorting]` connector tag | PASS | UnloadSources dropped while the construct stayed counted and its `[Stock]` container kept being serviced |
| `[No GOAT]` connector tag | NOT TESTED | would show as `ConnectedConstructs` dropping by one |
| Undock state cleanliness | NOT TESTED | |

Peak instructions with loadout work active: ~19,400 of 50,000, phase `DockScan`.

## Traps discovered the hard way

Each of these cost real time or resources. Details in CHANGELOG.md.

- **An item's `SubtypeId` is not its alias, and not its blueprint ID.** `Glass`
  is really `Component/BulletproofGlass`, `SensorCluster` is `Component/Detector`,
  `AluminumPlate` is `Component/InteriorPlate`. A wrong subtype fails *silently*:
  routing still works (it keys on `TypeId`), no warning fires, but the item never
  credits its alias, so Stock reads 0 forever and the planner reorders every
  cycle. This produced ~129,000 surplus BulletproofGlass before being caught.
  **Verify identities with the Item Identity Dump, never by assumption.**
- **A `SubtypeId` is not unique across `TypeId`s.** `PhysicalObject/Grain` (food)
  and `SeedItem/Grain` (seed) share the subtype `Grain`; so do Mushrooms and
  Vegetables. Any table keyed on subtype alone will conflate them — the category
  override did, and misrouted 151 seeds into Consumables until v2.4.23. Key on
  the full raw type wherever the TypeId is known.
- **`Comparison<T>` is not on the PB whitelist**, though `Func<T,TResult>` is.
  Naming that type in source is a compile error in-game. Use an inline lambda.
- **`MyIni` cannot remove a key.** `DeleteSection()` followed by `Set()` on the
  same section overlays the originally-parsed section and resurrects old keys.
  Never conditionally omit an `[IOPM.*]` key — blank it instead, or it keeps its
  last value forever and reads as current.
- **In-game compile error line numbers** refer to the `.min.cs` plus the PB's
  own ~32-line generated preamble. Map them through the artifact.
- **Repeated "Transfer failed … trying next container" warnings are a fault
  report, not noise — go find the damaged block.** When `CanItemsBeAdded` says
  yes but the transfer fails to *every* destination in the category **and** to
  an Overflow container sitting at 0% fill, the source has no conveyor path. A
  full warehouse cannot produce that pattern, because an empty Overflow would
  accept the item. Seen live as 37 warnings a cycle; the cause was a damaged
  conveyor junction on a drill, which IOPM flagged before anyone noticed it.
  Since v2.4.21 a stuck source is retried every 6th cycle, so the warning still
  appears — rate-limited rather than silenced, deliberately, so the signal
  survives.
- **LCDs taking ~90s to appear when you walk back into range is a Space
  Engineers engine behaviour, NOT a script fault.** PROVEN by controlled test:
  a plain unscripted LCD with hand-typed static text, placed beside two IOPM
  panels, reappeared at the same moment as both of them. The text is in the
  panel buffer the whole time (open the LCD text editor and it is there); only
  the rendered surface is stale. DO NOT try to fix this with a faster repaint
  loop - it cannot help, and it costs per-second network churn. The script's
  own render cadence tops out at one paint per logical cycle (~UpdateSeconds),
  which cannot produce a 90-second delay.

## Configuration

All configuration lives in the programmable block's **Custom Data**. Sections
the script actually reads:

```
General  Sorting  Production  Display  Docking  Stock  BlueprintOverrides
```

Anything else there is inert. `[BlueprintOverrides]` takes precedence over
built-in blueprint knowledge and exists as an escape hatch if a mod update
changes a blueprint ID.

Diagnostics are written back under `[IOPM.*]`. Those sections are script-owned
output, not settings — safe to delete; they regenerate within a cycle.

### Upgrading does not add new config keys

The default block is written **only when Custom Data is completely empty**. A
script upgrade never back-fills newly added keys into config you already have —
it reads them as absent and uses the coded default. So after taking a new
version, a new setting has to be typed in by hand.

This is deliberate: the alternative rewrites player-owned sections, and MyIni
cannot delete a key, so a bad automatic write could not be undone. But it means
"the setting isn't in my Custom Data" does **not** mean the script ignores it.
Check the default in `LoadConfig` before concluding anything.

Deleting a whole section is likewise harmless — every key falls back to its
default. Defaults in force are visible in `[IOPM.*]` (e.g. `BorrowPercent`
appears under `[IOPM.Docking]` whether or not `[Docking]` exists).

### A disabled phase clears its own diagnostics (2.4.24+)

Phases gated by config zero their `[IOPM.*]` counters on the skip path, not
just on entry. Before 2.4.24, `Organize=false` left `[IOPM.Organization]`
frozen on the last cycle that ran, which read as an active phase. `Enabled=`
is now emitted in that section so "ran and did nothing" is distinguishable
from "was not asked to run". Any new gated phase owes the same reset.
