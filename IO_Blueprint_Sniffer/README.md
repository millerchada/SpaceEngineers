# IO Blueprint Sniffer

Read-only diagnostic. Watches live production queues and captures the real
`MyDefinitionId` blueprint IDs that Industrial Overhaul / Space Engineers
actually use, so they can be copied into IOPM.

Current: `IO_Blueprint_Sniffer_v1.0.3.cs`. Earlier versions in `archive/`.
Short enough to paste directly — no build step.

## Why this exists

A blueprint's definition ID is not derivable from the item name. Industrial
Overhaul prefixes many of its own with `PO`, and the mapping is not consistent:

```
AluminumPlate  -> MyObjectBuilder_BlueprintDefinition/POInteriorPlate
Glass          -> MyObjectBuilder_BlueprintDefinition/POBulletproofGlass
GoldWire       -> MyObjectBuilder_BlueprintDefinition/GoldWire
```

Guessing wrong is *fail-safe* — IOPM reports `UnknownBlueprint` or `NoMachine`
and queues nothing — but it does mean the item silently never gets produced.
This tool gets the real value.

## How it works

You arm it for an item alias, then queue that item **by hand** in a machine. The
sniffer observes the queue growing, captures the blueprint ID responsible, and
records it.

Captures persist in this PB's `Storage` (plain text, `MyIni`-compatible), so
they survive recompile and restart:

```
[Seen.<n>]
Blueprint=<full MyDefinitionId>
Machine=<first CustomName observed with this blueprint>
MachineSubtype=<BlockDefinition SubtypeId>

[Captured.<Alias>]
Blueprint=<full MyDefinitionId>
Machine=<CustomName>
MachineSubtype=<SubtypeId>
Delta=<observed increase amount>
```

Captured values go into IOPM's `[BlueprintOverrides]`, or get promoted into its
built-in catalog once confirmed.

## Safety invariants

Stated in the script header and not to be weakened:

- Only touches blocks passing `IsSameConstructAs(Me)`.
- Reads production queues via `GetQueue()` **only**.
- Never calls `AddQueueItem`, `ClearQueue`, or any queue-mutating method.
- Never reorders or removes queue entries.
- Never reads or changes `.Enabled` on any machine.
- Refineries excluded entirely; assemblers in disassembly mode ignored.
- No code path is capable of writing production state.

Completely independent of IOPM: shares no code and never reads or writes IOPM's
Custom Data.

## What it cannot do

**It cannot read a blueprint's ingredient list.** The programmable block API
does not expose blueprint prerequisites. Recipes must be transcribed by hand
from the in-game blueprint tooltip (hover the item in the machine's production
list), which shows build time, required materials and amounts.

For the separate question of an item's real `TypeId/SubtypeId`, use
[`../IO_Item_Identity_Dump`](../IO_Item_Identity_Dump/).
