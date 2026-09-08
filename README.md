# Space Engineers Programmable Block Scripts

In-game C# scripts for Space Engineers, built against **Industrial Overhaul v1.7.7**
and interoperating with **GOAT / GSIM** sorting.

The main project is **IOPM** (IO Production Manager): warehouse sorting and
balancing, production planning, stall recovery, dock-aware logistics, and
Custom Data diagnostics.

## Deploying — do not paste the `.cs` file

The `.cs` files are **source**, not the deployment artifact. Comments and
indentation cost ~16,000 of the PB's 100,000-character budget and mean nothing
at runtime, but stripping them from the source would destroy the documentation
that makes this script maintainable. So we keep both:

```bash
python build_pb.py IO_Production_Manager_v2.4.5.cs
# -> IO_Production_Manager_v2.4.5.min.cs   <-- paste THIS into the block
```

`build_pb.py` refuses to write the artifact unless every string literal
survives byte-identical, code-context `{}()[];` counts match the source, and
the output is stable under a second pass. It hard-errors on `@"..."` verbatim
strings and `/* */` blocks, the two constructs the transform cannot handle
safely.

v2.4.13: source 105,260 -> artifact 85,143 chars (14,857 headroom).

The source is now LARGER than the 100,000-character PB ceiling, so it cannot be
pasted into a block at all. The build step is mandatory, not an optimisation.

## Layout

Only the **live working set** sits at the repo root. Superseded versions move to
`archive/`, where they stay readable but out of the way.

```
IO_Production_Manager_v2.4.9.cs      deployed in-game (validated)
IO_Production_Manager_v2.4.13.cs     candidate - DockScan single-pass (perf)
IO_Blueprint_Sniffer_v1.0.3.cs       current
IO_Item_Identity_Dump_v1.0.0.cs      current
Endless_Drill_Mk1_Basic_v1_0_0.cs    separate script (not an IOPM version)
Endless_Drill_Mk1_Headless_v0_8_15.cs
build_pb.py / README.md / CHANGELOG.md
archive/production_manager/          v1.0.11 - v2.4.8, v2.4.10 - v2.4.11
archive/blueprint_sniffer/           v1.0.0 - v1.0.2
```

The deployed version and the candidate are both kept at root so there is always
an immediate rollback target. Once a candidate is confirmed working in-game, the
version it replaced moves to `archive/`.

## Versioning convention

Each version is an **immutable file**. A new version is a copy plus a delta;
released files are never edited in place. This exists so a regression can be
isolated to a specific delta, and it has repeatedly paid off — see CHANGELOG.md.
Archived versions were moved with `git mv`, so `git log --follow` still works
across the move.

Supporting tools:
- `IO_Blueprint_Sniffer_v1.0.3.cs` — discovers blueprint definition IDs from
  machine queues.
- `IO_Item_Identity_Dump_v1.0.0.cs` — read-only; dumps every physical item's
  real `TypeId/SubtypeId` with totals across the construct. **Run this before
  trusting any item identity.** See the Glass incident below.

## Hard constraints

| Limit | Value |
|---|---|
| PB source ceiling | 100,000 characters |
| Runtime instructions | 50,000 **per invocation** |
| Observed peak (v2.4.12) | 36,056, phase `DockScan` (9 constructs) - v2.4.13 addresses this |

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

Confirmed on a live multiplayer server, large base, v2.4.5 - v2.4.7.

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
| Shortage is diagnostic-only, no new root | PASS | Short=1 WaitingFor=Motor, zero jobs added |
| Over-commit recovery (queues are sacred) | PASS | CopperWire 5,105/5,000 + 2,262 queued, JobsAdded=0 |
| `All` modifier | NOT TESTED | |
| Recursive manual-queue protection vs loadout | NOT TESTED | needs a manual queue competing for the same input |
| Multiple docked ships sharing the budget | NOT TESTED | 5 constructs docked but only 1 loadout container |
| `[No Sorting]` / `[No GOAT]` connector tags | NOT TESTED | |
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
- **`Comparison<T>` is not on the PB whitelist**, though `Func<T,TResult>` is.
  Naming that type in source is a compile error in-game. Use an inline lambda.
- **`MyIni` cannot remove a key.** `DeleteSection()` followed by `Set()` on the
  same section overlays the originally-parsed section and resurrects old keys.
  Never conditionally omit an `[IOPM.*]` key — blank it instead, or it keeps its
  last value forever and reads as current.
- **In-game compile error line numbers** refer to the `.min.cs` plus the PB's
  own ~32-line generated preamble. Map them through the artifact.
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
