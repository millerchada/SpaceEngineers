# Space Engineers Programmable Block Scripts

In-game C# scripts for Space Engineers, built against **Industrial Overhaul
v1.7.7** and run on a live multiplayer server.

One folder per programmable block. Each folder has its own README covering what
that script does, how to set it up, and its block tags and configuration.

| Folder | What it is |
|---|---|
| [IO_Production_Manager](IO_Production_Manager/) | **IOPM** — warehouse sorting and balancing, production planning, stall recovery, dock-aware logistics, ship loadout servicing. The main project. |
| [IO_Item_Identity_Dump](IO_Item_Identity_Dump/) | Read-only. Lists every item's real `TypeId/SubtypeId` with totals. **Run this before trusting any item identity.** |
| [IO_Blueprint_Sniffer](IO_Blueprint_Sniffer/) | Read-only. Captures the real blueprint `MyDefinitionId`s the game actually uses. |
| [Endless_Drill](Endless_Drill/) | Endless Drill Mk1 — walking-drill controller (rotor / piston / welder cycle). |

## Deploying

The two read-only diagnostic scripts and the drill controllers are short enough
to paste straight into a block.

**IOPM is not.** Its `.cs` file is *source*, and the source is now larger than
the PB's own 100,000-character ceiling — it cannot be pasted into a block at
all. Build the deployment artifact first:

```bash
python build_pb.py IO_Production_Manager/IO_Production_Manager_v2.4.16.cs
```

That writes `IO_Production_Manager_v2.4.16.min.cs` next to the source, and
**that** is what goes into the block.

Comments and indentation cost ~20,000 characters and mean nothing at runtime,
but stripping them from the source would destroy the documentation that keeps
the script maintainable — so we keep both. `build_pb.py` refuses to write the
artifact unless every string literal survives byte-identical, code-context
`{}()[];` counts match the source, and the output is stable under a second
pass. It hard-errors on `@"..."` verbatim strings and `/* */` blocks, the two
constructs the transform cannot handle safely.

## Versioning convention

Each version is an **immutable file**. A new version is a copy plus a delta;
released files are never edited in place, so a regression can always be
isolated to a single delta. This has repeatedly paid off — see the changelogs.

The deployed version and the current candidate both sit in the script folder,
so there is always an immediate rollback target. Once a candidate is confirmed
working in-game, the version it replaced moves to that folder's `archive/` with
`git mv`, so `git log --follow` still traces it.

Generated `*.min.cs` artifacts are gitignored: they rebuild byte-for-byte from
the source plus the committed `build_pb.py`.
