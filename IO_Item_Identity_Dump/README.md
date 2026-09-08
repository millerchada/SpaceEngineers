# IO Item Identity Dump

Read-only diagnostic. Lists every distinct physical item on the construct as
its **real** `TypeId/SubtypeId`, with totals.

Short enough to paste directly — no build step.

## Why this exists

**An item's in-game display name is not its `SubtypeId`, and its `SubtypeId` is
not its blueprint ID.** All three can differ, and getting the item identity
wrong fails *silently*:

- `Glass` is really `MyObjectBuilder_Component/BulletproofGlass`
- `SensorCluster` is really `MyObjectBuilder_Component/Detector`
- `AluminumPlate` is really `MyObjectBuilder_Component/InteriorPlate`

When IOPM had those wrong, category routing still worked (it keys on `TypeId`),
so no warning ever fired — but the items never credited their alias. Stock read
0 forever, demand never fell, and the planner re-queued every cycle. That
produced roughly **129,000 surplus BulletproofGlass** across several play
sessions before anyone noticed.

The failure signature is: *stock stuck at 0 while production visibly runs.*

**Run this before adding any item or recipe to IOPM, and before setting a stock
target for an item whose subtype has never been confirmed.**

The programmable block API cannot read blueprint *ingredient* lists, so recipes
still have to be transcribed from the in-game blueprint tooltip. This tool
answers the identity question only.

## Usage

1. Paste into a **scratch programmable block — not the IOPM block.**
2. Optionally set an argument to filter, e.g. `Component`, `Ingot`, `Ore`,
   `Glass`, or a full `MyObjectBuilder_AmmoMagazine`.
3. Press **Run**.
4. Copy the report out of **that block's Custom Data**. The PB detail panel also
   shows it, truncated at 8,000 characters.

It runs once per Run press (`UpdateFrequency.None`) and does nothing otherwise.

## Safety

- Never moves, adds, or removes an item. `GetItems` only.
- Never touches a production queue.
- Only inspects blocks passing `IsSameConstructAs(Me)`.
- **Refuses to run** if the block's Custom Data looks like IOPM configuration
  (contains `[IOPM` or `[Stock]`), so an accidental run on the IOPM block cannot
  destroy that configuration.

It writes its report into its **own** Custom Data, which is why it must go on a
scratch block.

## Reading the output

```
Item Identity Dump v1.0.0
Blocks=186 Inventories=222 DistinctItems=118 Filter=Component
MyObjectBuilder_Component/BulletproofGlass=129787
MyObjectBuilder_Component/Detector=768
MyObjectBuilder_Component/InteriorPlate=11028
```

`Blocks`/`Inventories` count what was scanned. Everything after is
`TypeId/SubtypeId=total`, sorted.

A note on construct scope: this only sees the construct the block sits on. On a
base split into subgrids joined by **connectors**, each side is a separate
construct — so a dump from one subgrid will not show the others. Mechanically
attached subgrids (rotor/hinge/piston) *are* the same construct and do appear.
