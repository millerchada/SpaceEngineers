# IOPM handoff — accepted state at v2.4.39

Checkpoint for a fresh conversation. Everything below is verified against the
repo at this commit, not recalled.

---

## Current release

| | |
|---|---|
| Version | **v2.4.39** |
| Release commit | **`0ec0259`** |
| Source | `IO_Production_Manager_v2.4.39.cs` |
| **Artifact to paste** | **`IO_Production_Manager_v2.4.39.min.cs`** — 86,828 chars, 13,172 headroom |
| Release gate | **PASSED** |
| Live status | Running, Sorting OK, Production OK, Warnings 0, 20 machines healthy |

Run before any paste, from the repository root:

```
python IO_Production_Manager/tests/run_release_gate.py \
       IO_Production_Manager/IO_Production_Manager_v2.4.39.cs
```

The gate runs two **negative controls first** — a checker that has silently
stopped working is worse than none, and this project has shipped a `check_pb.py`
that printed OK without compiling anything.

## Catalog counts (verified)

```
ItemDefs 63 | stock-configurable 45 | aliases 15 | loadout aliases 37 | recipes 47

catalog integrity   PASS       45 products / 47 recipes / 0 recipe-pending
dependency closure  COMPLETE   16 leaves, all explicit process boundaries
machine coverage    MANAGED 48 | KNOWN_IDENTITY_ONLY 12 | MISSING_FROM_IOPM 74
                    | UNKNOWN 3 | ingredient mismatches: none
```

**45 products and 47 recipes matching is not an invariant.** `StockConfigurable`
and `_recipes` are independent, and the next product added will be
identity-first again. Recipes exceed products because some are dependencies
(`Gunpowder`, `SyntheticFabric`) that are not stock roots.

## Gunpowder — fully proven, operational

Five facts, five methods, **none from a display name**:

| Fact | Value | Method |
|---|---|---|
| Physical identity | `MyObjectBuilder_Ingot/Magnesium` | Item Identity Dump, found **by quantity** |
| Blueprint | `MyObjectBuilder_BlueprintDefinition/Gunpowder` | IO_Blueprint_Sniffer v1.0.3 |
| Producer | Munitions Factory | machine enumeration |
| Inputs | 6 Niter · 2 Carbon · 2 Sulfur | machine enumeration |
| Output yield | **10** | one manual blueprint run, counted |
| Manual queue attribution | **working** | `audit_machine_coverage` |

Two of these are **actively misleading** from the display name. IO reuses the
vanilla `Magnesium` subtype, so a dump search for "Gunpowder" returns nothing at
all. And the UI displays **no yield**, which invites a default of 1 — that
default shipped in v2.4.37 and would have asked for ten jobs and ten times the
ingredients for the same demand.

`PotassiumNitrate → MyObjectBuilder_Ingot/Niter` was already in the catalog; the
dump confirmed it rather than discovering it.

## SolarCell — corrected, UAT PASS

`IronIngot:2 → 3`. The v2.4.34 recipe came from a superseded reading; the
consolidated IO v1.7.7 enumeration reads 3.

Proven live (`uat/v2.4.37/`) by planner arithmetic rather than by reading the
file: 47 units in flight produced support demand Glass 47, CopperWire 47,
SiliconWafer 235, **IronIngot 141**. Under the old recipe that line would read
94. Converged to Target 250 / Stock 250 / Satisfied with no overshoot.

**This was the first defect any audit found in a recipe that was already
running** — machine coverage caught it by comparing against live evidence, which
catalog integrity and closure structurally cannot do.

## SyntheticFabric / Canvas — operational

| | |
|---|---|
| `SyntheticFabric` | `Component/Fabric`, Auto Loom (observed), `Plastic:2`, **not** stock-configurable |
| `Canvas` | `Component/Canvas`, display name *Parachute*, Auto Loom, `SyntheticFabric:10` |

Live-confirmed working: Canvas waited on Synthetic Fabric and IOPM queued the
missing fabric unprompted. `SyntheticFabric` is the case that exposed the
closure gap — Canvas shipped with a recipe in v2.4.35 and was **unbuildable on
arrival** because its only ingredient had none.

## Machine evidence

**15 COMPLETE + Survival Kit DEFERRED**, 137 machine-recipe entries, in
`evidence/machines/` — one JSON per block, never merged.

Survival Kit is **deliberately deferred** (`AllowSurvivalKitFallback=false`),
recorded as such so a later pass cannot mistake it for an oversight. Every IO
production block **not** listed is **UNAUDITED, which is not the same as clean** —
the audit prints that on every run.

`docs/industrial-overhaul/v1.7.7/` is **generated** from that evidence by
`tools/gen_io_docs.py`. Do not hand-edit: a hand-edited doc becomes a second
source of truth.

## Outstanding unresolved identities and blueprint IDs

**Never resolve these by inference from a display name.**

| Item | Problem |
|---|---|
| `Acid Power Cell`, `Alkaline Power Cell`, `Asphalt` | blueprint id known, **no physical identity ever observed** — not present on the base |
| `Composite Plate` | live product; repo holds historical `CompositeArmor`. Equality **not proven** |
| `MR-8P Rifle Magazine` | repo subtype ends `_5rd`, live UI shows capacity **8**. Capacity is not proof of a different subtype |
| `S-20A Pistol Magazine` | no S-20A entry anywhere in the repo (only S-10/E/A) |
| Large/hand ammo variants | the UI exposes AP/DU/HE variants where the repo holds **one** alias — one `AutocannonClip` for two clips, one `LargeCalibreAmmo` for three shells, one `Missile200mm` for Rocket and HE Rocket. No 1:1 mapping is provable |
| Tool tiers | `AngleGrinderItem/2/3/4`, `HandDrillItem/2/3/4`, `WelderItem/2/3/4` line up suggestively with Grinder → Enhanced → Proficient → Elite. Mapping by numeric suffix is display-name inference in disguise |
| Food / harvesting | 24 outputs; three MealPack subtypes resolved, the rest unknown |

Resolved by the v2.4.38 dump and **recorded but not yet modelled**:
`GasContainerObject/HydrogenBottle`, `OxygenContainerObject/OxygenBottle`,
three MealPack subtypes, and both `MediumCalibreAmmo` and `MediumCalibreAmmoHE`
physically present (confirming two distinct assault-cannon subtypes without
proving which display name maps to which).

## `ConfigChangePending` — investigated, **no fix shipped**

Full analysis: `docs/investigations/configchangepending-latch.md`.

Observed latched `true` after full convergence with no config edits. Every path
traced: the phased reload **cannot latch on its own** — `PHASE_IDLE` is always
reached, `LoadConfig` re-syncs the baseline before any early return, and
`Me.CustomData` has only two writers. So the flag requires `Me.CustomData` to
persistently differ from `_lastCustomDataSeen`, which points at the deliberate
re-read in the `WriteDiagnostics` tail returning stale text on a server with
documented sync latency (LCDs took ~90s to refresh here).

**Not diagnostics-only**: a latched flag re-runs `LoadConfig()` every cycle,
re-parsing Custom Data and forcing `BlueprintReverseMap()` to rebuild across all
63 ItemDefs. **Correctness is unaffected** — the reload re-reads identical
config and still happens only at `PHASE_IDLE`.

**Why nothing shipped:** the environmental assumption is unverified. A one-line
fix is proposed (accept either the written text or the read-back;
`_lastWrittenCustomData` already holds the former), but the recommendation is to
land a **read-only `ConfigReloads` counter first** and turn the assumption into a
measurement. That is the standing rule here after the `IsWorking` and
stale-diagnostic episodes.

## Deferred UAT

**Gunpowder low-stock auto-production** — `uat/v2.4.39/smoke-test.txt`.
Unreachable, not unverified. Gunpowder has no `[Stock]` target, so it is only
produced as support demand for Explosives at 4 per unit; with ~196,894 Magnesium
on hand that needs Explosives demand above **~49,223** to trigger one job.

To reach it: drain the Magnesium stock, or give `Gunpowder` its own `[Stock]`
target above on-hand (making it a planner root). Watch for **`ceil(demand / 10)`
jobs, not one per unit** — one number confirming the yield correction and the
blueprint id together.

**Stall recovery** carries the same DEFERRED label for the life of the project.
The game skips a queue item it cannot build and resumes when the resource
arrives, so IOPM's recovery has a genuinely rare trigger. `Stalled=0` is
expected, not a coverage gap.

## Architectural invariants — must not regress

1. **`AddQueueItem()` is the only production-queue mutation.** No `ClearQueue`,
   no removal, no reduction, no reorder. Existing and manual queues are sacred.
2. **No refinery management.** Outputs are evacuated; queues and inputs are
   never touched.
3. **Config snapshot.** A Custom Data change applies only at `PHASE_IDLE`, so
   every phase of a cycle runs against one immutable `Config`.
4. **Cooperative multi-tick phases.** One phase per `Update10` tick. Do not
   re-collapse into a monolithic cycle. Runtime ceiling 50,000 instructions.
5. **Remote inventory never enters base `_onHand` or `[Stock]`.**
6. **Four kinds of item knowledge stay separate** — physical identity,
   blueprint identity, recipe/yield, preferred machine. A live observation
   proves identity **only**. A blueprint id is **not** an identity.
7. **A recipe never answers "is this a stock item."** `StockConfigurable` and
   `_recipes` are independent. This leaked into three places and took three
   versions to remove.
8. **TypeId is never evidence of terminality.** `Gunpowder` and `Polymer` are
   both Ingot-typed and both manufactured. The process boundary is a policy
   list, and `audit_closure.classify()` takes only the alias so a TypeId rule
   cannot be written there.
9. **An absent UI yield means UNKNOWN, never 1.** Two measured counter-examples:
   `Lightbulb|10` and `Gunpowder|10`.
10. **Never infer a physical subtype from a display name.**
11. **`[Stock]` is non-destructive**: absent keys only, values preserved as raw
    text, alias collisions **fail closed** (no quota applied) rather than
    picking one by iteration order.
12. **PB source ceiling 100,000 chars.** `Comparison<T>` is prohibited;
    `Func<>` is allowed. `MyIni` cannot remove a key — `DeleteSection()` +
    `Set()` resurrects the original section.
13. **Version immutability.** Each release is a copy plus a delta; superseded
    pairs move to `archive/vX.Y.Z/` on promotion. A version that never compiled
    may be fixed in place.

## Recommended next work — not started

1. **`ConfigReloads` counter** (read-only, smallest, unblocks a real fix).
2. **Scope decision on the 74 unmodelled products.** Per-group proposals are in
   the v2.4.37 changelog entry; nothing has been applied. Components and
   intermediates are the defensible first group; ammo should wait for the
   variant ambiguity.
3. **Promote ammo loadout aliases to ItemDefs** if manual-queue recognition for
   magazines matters — loadout aliases are not in `_items`, so
   `BlueprintReverseMap` can never attribute them.
4. **Producer tokens** for the 9 recipes whose machine token does not name the
   observed machine (`SyntheticFabric`, `Concrete`, `Girder`, `Explosives`, the
   five Nano-Assembler components). Cosmetic — token is a preference,
   `CanUseBlueprint` decides eligibility.
5. **Multi-output architecture** for Nuclear Reprocessing (3 simultaneous
   outputs). Needs coproduct accounting **and** a planning policy that does not
   re-run a recipe because only one output was counted.
6. **Idle Wire Drawer** — never diagnosed. `IO_Machine_Dump` is committed and
   ready; the standing hypothesis is that `GetBestMachines` gates on
   `IsWorking` (which for production blocks tracks *currently producing*) while
   the health pass short-circuits on an empty queue, making an idle machine both
   skipped and reported healthy.
7. **Dedicated bauxite line** — `Ores` sat at ~95% with 29M bauxite. Structural,
   not a config change.
