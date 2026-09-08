# IO Machine Dump

Read-only diagnostic. Lists every production block the grid terminal system can
see and, for each one, every condition under which IOPM would exclude it from
production management.

## Deploying

Paste `IO_Machine_Dump_v1.0.0.cs` directly — it is small, so there is no build
step and no `.min.cs`.

**Run it on a scratch programmable block, not the IOPM PB.** It writes its
report into its own Custom Data so the text can be copied out. A guard refuses
to run if the PB's Custom Data contains `[IOPM` or `[Stock]`, so an accidental
run on the IOPM PB cannot destroy that configuration.

Put the scratch PB on the **same construct** as the IOPM PB, or the
`SameConstructAsPB` column is measured against the wrong reference block.

Press Run. Optional argument is a blueprint id to test with `CanUseBlueprint`
on every machine:

```
MyObjectBuilder_BlueprintDefinition/GoldWire
```

Without an argument the `CanUseBlueprint` column reads `n/a` and everything
else still reports.

## Why it exists

Every path by which IOPM excludes a production block is **silent** — no
warning, no `[IOPM.*]` diagnostic entry, nothing in game to look at. So a
machine that is quietly never used is indistinguishable from one that is
working fine but idle.

Written for a live case: two identical Wire Drawers on the same grid, one
accumulating nine queue entries and the other permanently empty, with no tags
on either, both in assemble mode, both powered, manual queueing working fine on
both, and `[IOPM.Machines]` reporting `Healthy=18 NotWorking=0 Warnings=0`.

## What it reads, and what each column rules out

| Column | Rules out |
|---|---|
| `Grid` / `SameConstructAsPB` | A block on another construct. `IsSameConstructAs` covers mechanical joins (rotor, piston, hinge) but **not connector docks**, so a base split into subgrids by connectors has production blocks IOPM cannot see. |
| `IgnoreTags` | `[IOPM-Ignore]`, `[Ignore]`, `[Locked]`, `!GSIM-Locked`, `[No Sorting]`, `!GSIM-NoSorting`. Note that `[No Sorting]` on a production block makes it fully invisible to IOPM, which is not what the tag says. |
| `AssemblerMode` | An assembler flipped to disassembly is dropped from `_mach`. |
| `Working` / `Functional` / `Enabled` / `Producing` | Printed side by side deliberately. For production blocks `IsWorking` tends to track *currently producing*, not *powered and switched on* — so an idle machine can read `Working=false` while `Functional=true Enabled=true`. |
| `QueueCount` / `QueueLoad` | A throwing `GetQueue` makes IOPM score the machine `999999`, sorting it permanently last. |
| `CanUseBlueprint` | The machine's blueprint class genuinely not containing the item. |
| `Queue` | The first six entries with amounts, so per-cycle job additions can be seen accumulating. |

`Verdict` collapses all of the above into one line per machine: either
`CANDIDATE` or the specific reason IOPM skips it.

## Guarantees

Strictly read-only. It calls only property getters, `GetQueue()` and
`CanUseBlueprint()`. It never queues, never removes a queue entry, never moves
an item, never toggles a block. There is deliberately no code path in the file
that mutates anything except its own Custom Data.

## Interpreting the result

Among machines that read `CANDIDATE`, IOPM queues to **all** of them, splitting
each batch of jobs evenly and giving the remainder to the lowest `QueueLoad`
first. So an idle machine reading `CANDIDATE` with `QueueLoad=0` should be
first in line for work.

If a batch of N jobs appears **intact** in one machine's queue rather than
split, then that machine was the only candidate at the time — which points at
an exclusion, not at a scheduling preference.
