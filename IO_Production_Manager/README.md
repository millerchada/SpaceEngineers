# IO Production Manager (IOPM)

Warehouse sorting and balancing, production planning, stall recovery,
dock-aware logistics and ship loadout servicing, for **Industrial Overhaul
v1.7.7**. Interoperates with **GOAT / GSIM** sorting tags.

Version history: [CHANGELOG.md](CHANGELOG.md).

## Project layout

    IO_Production_Manager_v2.4.40.cs       current candidate source
    IO_Production_Manager_v2.4.40.min.cs   current candidate artifact - paste THIS
    IO_Production_Manager_v2.4.39.cs       last release ACCEPTED in live play
    IO_Production_Manager_v2.4.39.min.cs   its artifact
    README.md  CHANGELOG.md

Two versions live here at a time, and the distinction matters: **v2.4.39 is the
accepted runtime release** — it has passed the gate *and* a live smoke test.
**v2.4.40 has passed the gate but not yet live UAT** (`uat/v2.4.40/plan.md`).
Superseded pairs move to `archive/` only on promotion, so anything in `archive/`
is neither.

    archive/vX.Y.Z/    every superseded release, grouped by version
    tests/             audits, the release gate, the unit + invariant suites, fixtures
    tools/             gen_io_docs.py - regenerates docs/ from evidence/
    docs/              GENERATED; do not hand-edit
    evidence/machines/ primary live machine evidence, one JSON per block
    uat/               live Custom Data dumps captured from the game

Run everything from the repository root:

    python IO_Production_Manager/tests/run_release_gate.py            IO_Production_Manager/IO_Production_Manager_v2.4.40.cs

## Deploying — never paste the `.cs` file

The `.cs` is **source**. It is now larger than the PB's own 100,000-character
ceiling, so it cannot be pasted into a block at all. Build the artifact:

```bash
python ../../tools/build_pb.py IO_Production_Manager_v2.4.40.cs
# -> IO_Production_Manager_v2.4.40.min.cs   <-- paste THIS into the block
```

v2.4.40: source 189,483 -> artifact **89,097** chars (**10,903** headroom).
v2.4.39: source 151,049 -> artifact 78,188 chars (21,812 headroom).

Both artifacts shrank in 2.4.40 without a line of logic changing, because the
**transform** got better (see below). The v2.4.39 figure of 86,828 quoted in
older notes was correct for the old transform; rebuilding that unchanged source
today produces 78,188. The source is what is immutable, not the artifact — and
`build_pb.py` is deterministic, so either number is reproducible from its own
tooling.

Comments and indentation cost ~20,000 characters and mean nothing at runtime,
but stripping them from the source would destroy the documentation that keeps
this maintainable — so we keep both. `build_pb.py` refuses to write the
artifact unless every string literal survives byte-identical, code-context
`{}()[];` counts match the source, the output is stable under a second pass,
**and the artifact itself compiles**.

Four transforms run, in this order, each measured separately and reported on
every build:

| Pass | v2.4.40 saving | What it does |
|---|---|---|
| comments + indentation | ~74,000 | `//` comments, leading whitespace, blank lines |
| identifier shortening | 9,879 | our own `_fields` and method names |
| own-type member shortening | 3,396 | **direct member declarations** of types this file defines (2.4.40+) |
| space tightening | 6,517 | spaces that provably cannot separate two tokens (2.4.40+) |

Newlines are still kept. Packing them to a 200-character line width was measured
at **1,985 characters** and **not taken**: in-game compile errors are located by
line, and 1,985 characters is not worth making every one of them ambiguous. See
`tools/profile_pb.py` for the full character profile.

The two new passes have their own suite, `tests/tests_minify.py`, which is mostly
negative controls — the load-bearing one proves a framework member stays
unrenameable *even when one of our own classes declares the same name*.

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
| **Broadcast Controller** | Alert transport, selected by the **exact name** in `[Alerts] BroadcastController` — never by block type, never by tag. Its target, `UseAntenna` and chat Custom Name are read but **never written**. Two blocks sharing the name is an error, not a choice. |
| **Radio antenna** | Read only, and only to describe transport health: a count of antennas on this construct that are working *and* broadcasting. IOPM never selects, enables or reconfigures one. |

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
| Sorting instruction guard | `[Sorting] InstructionBudgetPercent`, default 75 (2.4.40+) |
| Observed peak (v2.4.21) | 25,250, phase `DockScan` (35 containers, 9 docked constructs, 160 unload sources) |

`Sorting` grows with **container count**; `DockScan` is independent of docked
construct count since v2.4.13. So the peak tracks the base you build, not other
players' traffic.

The script runs a **cooperative multi-tick phase architecture** — exactly one
phase per `Update10` tick. A single monolithic cycle exceeded the instruction
limit; do not re-collapse the phases.

## Invariants — do not break these

### A recipe never answers "is this a stock item"

Closed in all three places it had leaked, and verified against v2.4.29:

| Place | Was | Now |
|---|---|---|
| Seeding (`EnsureStockAliasesPresent`) | `_recipes` | stock-configurable ItemDefs (2.4.27) |
| Planning / counting | already `_cfg.StockTargets` | unchanged |
| LCD (`BuildStockText`) | `_recipes` | `_cfg.StockTargets` (2.4.29) |

Every surviving use of `_recipes` asks a manufacturing question — yield lookup,
queued-job ingredient demand, recursive expansion, `EnsureFeasible`'s
buildability gate, `ApplyPlan`, and the table's own population. **If you find
yourself reaching for `_recipes` to decide whether something belongs in stock,
in a count, or on a screen, you are reintroducing this bug.**

The distinction in one line: *"may the player set a target for this"* is
answered by `StockConfigurable`; *"can IOPM build this"* is answered by
`_recipes`. They are independent, and seven live products currently prove it by
being the first and not the second. `ArmoredPlate` proves the other half:
it gained a recipe in 2.4.30 with no change to its identity or its `[Stock]`
entry, because neither ever depended on one.

`[IOPM.StockDisplay] Rows` and `[IOPM.Production] StockItems` read the same
dictionary, so they always agree. A disagreement is a real defect — it is how
this bug was caught.


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

## What `[Sorting]` actually does — four separate passes

"Sorting" is an umbrella name, and the config keys under it control four
different passes that run in this order every cycle. They are frequently
confused, so:

| Pass | Key | What it does |
|---|---|---|
| **Route** | `Enabled` | Moves items **between** blocks — out of drills, transit cargo and mismatched containers, into the warehouse container tagged for that item's category. This is the pass that makes `[IOPM-Inventory] Ores` mean anything. |
| **Overflow drain** | *(always on)* | Pushes items **back out** of the `Overflow` container into their proper category once room exists there. |
| **Balance** | `Balance`, `BalanceTolerancePercent` | Evens fill levels **across** the containers of one category, so 18 Ores containers don't end up one full and seventeen empty. |
| **Organize** | `Organize`, `OrganizeSkip` | Reorders slots **within** a single container into alphabetical order, and merges split stacks of the same item. Moves nothing between blocks. |

So: **Route decides which container an item lives in. Organize decides which
slot it occupies inside that container.** Balance decides how much goes in
each of several containers of the same category. `MaxTransfersPerCycle` is a
single budget shared by all four.

Turning `Organize` off costs you slot tidiness **and stack merging** — the
merge pass lives inside it. It does not affect routing, categories, balancing
or production in any way.

### Slot order is not purely cosmetic

Ordering a container looks cosmetic but the conveyor system reads it. A
draining sorter takes the first matching item it finds, so pinning whichever
item sorts first into slot 0 starves everything further down the list when one
item vastly outnumbers the others.

Live case: `Bauxite` 29.1M vs `Silver` 142k in the same 18-container Ores pool.
`SilverIngot` sat at 157 for an entire session; with ordering off it went
157 -> 754 -> 2,659. The crusher gate had never been reaching the tail of an
alphabetised list.

Hence `OrganizeSkip` (2.4.25+): a comma list of categories exempted from slot
ordering while the rest stay tidy.

```
[Sorting]
Organize=true
OrganizeSkip=Ores
```

A container is skipped if **any** of its categories is in the list. Names not
matching a real category are silently ignored, so check the resolved set that
gets echoed back as `[IOPM.Organization] Skipped=` rather than trusting what
you typed.

## Capacity alerts (2.4.40+)

**Off by default.** `[Alerts] Enabled=false` until you deliberately turn it on.

IOPM watches the fill percentage of each warehouse category pool and the
Overflow pool, and sends a one-line chat message through a **Broadcast
Controller** when a pool changes alert state. That is the whole of v2.4.40:
capacity only. Production-blocked, resource-shortage and docked-loadout alerts
are deliberately *not* here — the transport and the state engine get proven in
live play before anything else is routed through them.

```ini
[Alerts]
Enabled=false
BroadcastController=Broadcast Controller IOPM
WarehouseWarningPercent=85
WarehouseCriticalPercent=95
CooldownSeconds=300
CapacityHysteresisPercent=2
AlertOnStartup=false
WakeAntennaForAlerts=false
AlertAntenna=
```

Nothing in that list duplicates a Broadcast Controller setting. **Target
(Owner / Faction / Everyone), `UseAntenna` and the chat Custom Name stay on the
block**, where you set them, and IOPM never writes any of them. The controller's
own Custom Name is what gives a message its visible source prefix — which is why
no site name is hardcoded anywhere in the script.

### The pipeline

    Observation  ->  Classification  ->  State transition  ->  Delivery
    PoolStats()      AlertLevel()        AlertStep()           AlertSend()

`AlertEvaluation` is its own phase, between `ApplyPlan` and `WriteDiagnostics`.
That placement is not cosmetic: alerts read the *settled* state of a cycle, and
sitting downstream of `ApplyPlan` makes it structurally impossible for an alert
to turn into a queue-management side effect. The phase is **observational** —
`tests/tests_invariants.py` rejects a build in which the alert region mutates a
queue, moves inventory, or writes a block setting.

Capacity comes from `PoolStats()`, the same function the `[IOPM.Warehouse.*]`
diagnostics and the LCDs already use. There is no second capacity calculation.

### Hysteresis — which way it applies

A band is entered the instant its threshold is crossed and left only once the
reading has fallen `CapacityHysteresisPercent` *below* it. With the defaults:

| From | Reading | Level | Message |
|---|---|---|---|
| Healthy | 84 | Healthy | — |
| Healthy | 91 | Warning | `WAREHOUSE WARNING \| Ingots 91%` |
| Warning | 92 | Warning | — |
| Warning | 96 | Critical | `WAREHOUSE CRITICAL \| Ingots 96%` |
| Critical | 94 | **Critical** | — (inside 95−2) |
| Critical | 92.9 | Warning | `WAREHOUSE WARNING \| Ingots 93%` |
| Warning | 84.9 | **Warning** | — (inside 85−2) |
| Warning | 82.9 | Healthy | `RECOVERED \| Ingots capacity back to 83%` |

So a pool hovering on 85.0 does not recover the moment it reads 84.9, and a pool
oscillating across a threshold produces one message, not one per cycle.

### Cooldown is a flap guard, not a reminder timer

**Time passing never creates a message.** There is no branch in the state engine
that emits anything because a duration elapsed, so "still critical" reminders are
not merely absent from this release — they are unreachable.

| Transition | Result |
|---|---|
| Healthy -> Warning | event |
| Warning -> Warning | silent |
| Warning -> Critical | event, **even inside the cooldown** |
| Critical -> Critical | silent |
| Critical -> Warning | event, but only once `CooldownSeconds` has passed since this pool last spoke |
| anything -> Healthy | recovery, **only if the problem was announced** |

Escalation is never suppressed: a warehouse crossing into Critical is the
message that matters most. A *downgrade* inside the cooldown is held, and if the
pool flaps back up before the cooldown expires the whole excursion is absorbed
silently, because the announced level never changed.

The engine tracks two levels per pool. `Lvl` is what the readings say; `Sent` is
what you have actually been told. Keeping them apart is what lets "this was
never announced, so there is nothing to recover from" be expressible at all.

### Observed is not announced — and it is the difference that matters

The engine keeps three things per pool, and conflating any two of them is a defect:

| | |
|---|---|
| `Lvl` | what the readings say now — the reference point hysteresis is measured against |
| `Ack` | the level already accounted for, either observed at baseline or announced |
| `Ann` | whether that level was **actually announced** — handed to the controller without an exception |

A startup baseline sets `Ack` **without** setting `Ann`. That is the whole
point: a warehouse already Critical before the script loaded is *observed*,
never *announced*, so its later return to Healthy emits **no** `RECOVERED` line
for a warning nobody ever received.

| Sequence | Result |
|---|---|
| start Healthy, stay Healthy | silent |
| start Warning, stay Warning | silent |
| start Warning → Healthy | **silent** — nothing was announced to recover from |
| start Critical → Healthy | **silent** |
| start Warning → Critical | **Critical is sent** — a genuine escalation past the baseline |
| start Critical → Warning | silent, if nothing had been announced |
| real Warning sent, later → Healthy | `RECOVERED` as normal |

This is not "suppress all recoveries". A recovery is sent exactly when the
problem it ends was itself announced.

### Startup, and switching alerting on

With `AlertOnStartup=false` (the default), the first evaluation after a PB
restart, a reload, or `Enabled` going `false -> true` takes that silent
baseline. Set `AlertOnStartup=true` to announce the conditions found at startup
instead.

**The baseline runs even when transport is down**, and that is deliberate. The
baseline records what was true when alerting *started* — a fact about time, not
about the controller. Deferring it until the controller came back would let a
condition that arose *while* the controller was offline be swallowed as "it was
already like that". A *sending* pass, by contrast, requires transport.

Alert state is **in memory only** — there is no `Storage` persistence and
`Save()` stays empty. A PB restart re-baselines rather than resuming, which is
the behaviour above and not a gap.

### Stable keys

State is keyed by the warehouse category IOPM already defines, never by display
text: `WH:ORES`, `WH:INGOTS`, `WH:COMPONENTS`, `WH:AMMO`, `WH:TOOLS`,
`WH:CONSUMABLES`, `WH:SEEDS`, `WH:MISC`, `WH:OVERFLOW`. Each key carries its own
independent state, so Ingots going critical says nothing about Ammo.

### Transport, and every way it can fail

The controller is found during the same same-construct enumeration everything
else uses, by the **configured exact name**, case-insensitively. Every match is
counted rather than the first one taken — which is what lets duplicates fail
closed instead of being resolved by enumeration order.

| `[IOPM.Alerts] Transport` | Meaning |
|---|---|
| `OK` | the only sendable state |
| `Disabled` | `Enabled=false` |
| `ConfigError` | Critical% not above Warning%, or an empty controller name |
| `ControllerNotFound` | no block on this construct has that name |
| `AmbiguousName` | **two or more blocks share it — fail closed, no guess** |
| `ControllerNotWorking` | found, but off / unpowered / damaged |
| `ComponentUnavailable` | the chat component would not resolve |

None of these throws, and none is silent: each is a value in `[IOPM.Alerts]`.
When transport is not `OK` the evaluation is skipped entirely, the state engine
is **not advanced**, and `Blocked` counts the cycle. A transition that could not
be sent stays pending and is re-derived next cycle, so it is still delivered
once the controller comes back.

### Antenna state is advisory, and deliberately so

IOPM reports two antenna facts and **acts on neither**:

```
UseAntennaAdvisory=true
AntennasBroadcastingAdvisory=1
```

An earlier draft of this release made `UseAntenna=true` with no broadcasting
antenna a transport state called `DegradedNoAntenna`. That was wrong in both
directions, and it is gone.

The script can see whether same-construct `IMyRadioAntenna` blocks are working
and broadcasting. It **cannot** see laser antennas, a player's suit antenna,
radio range, or the fact that `UseAntenna=false` still delivers to everyone on
the grid. Turning that single visible fact into a transport verdict would mean
*withholding a message from a player standing in the base* because a mast was
switched off — a false negative that costs an alert. Calling it `OK` regardless
would be a false positive. So it is neither: it is reported, and behaviour does
not depend on it.

`UseAntenna=false` is a valid native setting and was never an IOPM error. IOPM
will not switch controllers, enable an antenna, or widen `BroadcastTarget` to
`Everyone` to get a message out.

### Antenna wake (opt-in, off by default)

A radio antenna left powered down costs nothing and gives nothing away. If yours
normally sits off, IOPM can switch **one specifically named antenna** on for the
length of an alert and put it straight back.

```ini
WakeAntennaForAlerts=true
AlertAntenna=Compact Antenna Moon
```

Defaults are `false` and empty, so an existing installation is unaffected until
both are set by hand. There is **no default antenna name** — IOPM must never
guess which of your blocks to power up.

**The only block setting the alert subsystem may ever write is `Enabled`, on the
antenna you named.** Not `Radius`, not `EnableBroadcasting`, not any name, not
anything at all on the Broadcast Controller. `tests/tests_invariants.py` counts
the authorised writes and fails the build at four.

If `EnableBroadcasting=false` on that antenna, IOPM reports it
(`AlertAntennaBroadcasting=False`) and leaves it alone. Waking a powered-down
antenna is the feature; overriding every way an antenna can be configured is not.

#### When it applies — all six, or not at all

1. `[Alerts] Enabled=true`
2. Broadcast Controller transport is otherwise `OK`
3. the controller reports `UseAntenna=true`
4. `WakeAntennaForAlerts=true`
5. **exactly one** same-construct `IMyRadioAntenna` carries the configured name
6. **an alert message is actually pending**

Six is the one that matters. *A cycle merely running never moves a block.* No
pending alert, no antenna change — and with `UseAntenna=false` the antenna is
never touched at all, because the controller is not using it.

#### It is an asynchronous state machine, and that is deliberate

`Enabled = true` → `SendMessage` → `Enabled = false` inside a single execution is
**not** what this does. There is no evidence the game makes an antenna usable
synchronously within the tick that enabled it, and a send that quietly went
nowhere is exactly the failure this project keeps designing against.

So the antenna is enabled in one PB execution and the message is sent in a
**later** one. No blocking wait, no sleeping, no loop — just a state that
advances one evaluation at a time:

| State | Meaning |
|---|---|
| `Idle` | nothing outstanding |
| `Waking` | IOPM enabled the antenna; the send happens on a later execution |
| `Ready` | the antenna is up and at least one message has gone out |
| `Error` | the wake is configured but unusable — see `WakeNote` |

```
alert pending, antenna already on   -> send now, never touch the block
alert pending, antenna off          -> enable, mark ownership, send NOTHING
next evaluation, still pending      -> send the whole batch
nothing left pending                -> switch it back off, Idle
```

**Several alerts in one evaluation share one wake.** The messages are collected
first and sent as a batch, so three pools crossing a threshold together produce
one enable and one restore, not three. The buffer is bounded by the nine
warehouse categories — a fixed-size array, never a growing queue.

#### IOPM restores only what IOPM changed

An antenna that was **already on** is never switched off. Ownership is explicit:
`WakeOwned=True` means IOPM enabled it and still owes you a restore.

| What happens | What IOPM does |
|---|---|
| you switch the antenna off mid-sequence | **lets go** — your setting wins, no re-assert |
| the antenna is renamed or removed mid-sequence | restores the block it woke, if that reference still works |
| `WakeAntennaForAlerts` or `UseAntenna` switched off mid-wake | restores, then stands down |
| alerting switched off entirely mid-wake | restores first |
| **IOPM switched off entirely mid-wake** (`[General] Enabled=false`) | **`Main()` restores it** — no cycle will ever start again, so the alert phase cannot |
| transport goes down mid-wake | restores — nothing can be sent, so nothing needs the antenna |
| the restore itself throws | keeps ownership *and* the marker, and retries next evaluation |
| a send throws under our wake | the wake is **retired** — antenna back down, and any retry builds a fresh sequence |

If the wake is configured but unusable — name empty, antenna missing, or two
blocks sharing the name — IOPM **fails closed**: nothing is sent, nothing is
marked announced, the transition stays pending, `Blocked` increments and
`WakeState=Error` says which of the three it was. It does not fall back to some
other antenna, and it does not send hopefully into an antenna that is off.

A failed send never leaves the antenna up for an open-ended run of retries. The
next evaluation after a throwing send tears the wake down first — never in the
same execution as a successful send, which would race the very boundary the
wait exists to create — and a later retry starts a new sequence. A controller
that keeps throwing therefore leaves the antenna **off** most of the time
instead of powering it indefinitely.

#### Restart safety — the one thing that justified touching `Storage`

Alert state is in memory and dies with the script. If the PB is recompiled, the
world reloads, or the server restarts **between enabling the antenna and
switching it back off**, nothing in RAM remembers IOPM owes a restore — and your
antenna is left on, by IOPM, forever.

So `Storage` carries one number: the **`EntityId`** of the antenna IOPM
currently owns, and nothing else.

What that actually guarantees, stated accurately: `Storage` is persisted through
the game's **save** lifecycle, so it covers a PB recompile, a normal world save
and reload, and a normal persisted world restart. It is **not** a synchronous
flush to disk, and the PB API establishes no transactional durability — an abrupt
host or process crash before the world persists can lose the marker along with
everything else that session had not saved. That is an API boundary, not a defect
to route around; a second persistence mechanism would buy no stronger guarantee
and would create a second source of truth. Not the alert engine, not the state machine,
not history. The configured *name* selects the antenna in the first place; the
moment IOPM takes ownership it tracks the block by id, because a name can be
edited and an id cannot.

Recovery runs from **`Main()`**, on every execution, outside every gate there is
— not from the alert phase, and with **no one-shot latch**. Responsibility is
derived rather than cached: `_wkOwn != null` means this runtime owns an active
wake and the alert phase governs it; otherwise a non-empty `Storage` is a previous
owner's obligation to recover or retry. An earlier build cached "recovery already
ran" in a flag, which meant a wake taken *later* in the same runtime could never
be seen — and if `[General] Enabled=false` then stopped cycles, the antenna
stayed on forever with no restart involved at all.

`Main()` is also the backstop for an **active** wake whose phase has just been
configured away: if `[General] Enabled`, `[Alerts] Enabled` or
`WakeAntennaForAlerts` goes false while `WakeOwned=True`, `Main()` restores the
antenna itself. The reason is blunt: no cycle starts when
`[General] Enabled=false`, so recovery reached only through the alert phase could
never run on a base whose IOPM had been switched off, and an antenna IOPM powered
up would stay up indefinitely. Whether you have since disabled IOPM, disabled
alerts or disabled the wake feature has no bearing on IOPM's obligation to put
back a block it switched on.

**The one unavoidable boundary:** if the programmable block itself is off or not
executing, no script can restore anything. Recovery then happens on the first
execution after the PB runs again, which is the earliest moment any code of ours
exists.

**The marker is cleared only after the restore actually succeeds.** If the write
throws, the marker is kept, the failure is reported, and it is retried on a later
execution — and across another restart. Recovery is not one-shot when the restore
itself failed.

**An `EntityId` is durable ownership, and topology does not cancel it.** Recovery
does *not* check `IsSameConstructAs`: once IOPM knows exactly which block it
switched on, that block being detached onto another construct — ground off, split
at a merge block, a rotor coming apart — does not cancel the obligation.

| Marker state | Action |
|---|---|
| no marker | nothing to do |
| will not parse | **corrupt** — discarded with a diagnostic |
| resolves to an `IMyRadioAntenna` | **restore**, whatever construct it is on now |
| resolves to something else | **corrupt** — an impossible ownership state, discarded |
| does not resolve right now | **keep the marker and retry**, indefinitely |
| the restore throws | keep the marker and retry |

The null case is the one worth understanding. `GetBlockWithId` returning nothing
does **not** prove the block was destroyed — it is equally consistent with the
block being temporarily invisible to this terminal system, and the script cannot
tell those apart. So it does not choose. A permanently destroyed antenna leaves a
harmless stale number in `Storage` forever, which is the intended trade: silently
abandoning a real antenna IOPM powered on costs you a block stuck on, and a stale
number costs nothing.

The marker is written **before** `Enabled = true`, never after. An interruption
between the two then leaves a stale marker and an antenna that is still off,
which recovery simply clears. The other order strands the block. `Save()` remains
empty; this is a direct assignment, not a save hook, so it survives a crash that
never calls `Save()`.

Renaming the antenna during the outage used to defeat recovery; keying on
`EntityId` removes that limitation entirely. One honest limitation remains: if
*you* deliberately turned that antenna on during the outage, recovery turns it
off once. The window is load → first execution, and the alternative is a block
IOPM strands on forever.

#### Wake diagnostics

```
WakeAntennaForAlerts=True
AlertAntenna=Compact Antenna Moon
AlertAntennaFound=1
AlertAntennaEnabled=False
AlertAntennaBroadcasting=True
WakeState=Idle
WakeOwned=False
WakeNote=none
```

`AlertAntennaFound` is a count for the same reason `ControllersFound` is: two
blocks sharing the name has to be distinguishable from one, and it fails closed
either way.

### A transition is consumed only by a successful send

`AlertStep()` returns a message. It commits nothing. `AlertCommit()` — which
advances `Ack`, sets `Ann`, resets the cooldown clock and increments the counter
— runs **only after `SendMessage(string)` returned without throwing**.

If the send throws: `SendErrors` increments, nothing is marked announced,
`LastMessage` is unchanged, and the same transition is offered again next cycle
until it gets through. An escalation arriving while an earlier message is still
stuck simply supersedes it — the more severe message is the one that lands.

"Announced" here means **handed to the controller without an exception**, and
nothing more. `SendMessage(string)` returns `void`; there is no delivery
acknowledgement in the API, and the script cannot see whether anyone read the
line. Nothing in IOPM claims delivery.

### Diagnostics

```
[IOPM.Alerts]
Enabled=True
Controller=Broadcast Controller IOPM
ControllersFound=1
ControllerWorking=True
ComponentAvailable=True
Transport=OK
UseAntennaAdvisory=true
AntennasBroadcastingAdvisory=1
Alerts=3
Suppressed=1
Blocked=0
SendErrors=0
LastMessage=WAREHOUSE CRITICAL | Ingots 96%
States=WH:INGOTS=Critical
```

| Key | Means exactly |
|---|---|
| `Alerts` | messages successfully handed to the controller, no exception thrown |
| `Suppressed` | de-escalations held back by the cooldown flap guard |
| `Blocked` | evaluations skipped because transport was not `OK` |
| `SendErrors` | attempts where `SendMessage` threw |
| `LastMessage` | the last **successfully handed** message — never a failed attempt |

`States` lists only pools that are not Healthy-and-unannounced (`AllHealthy`
when there are none), and marks anything observed but never announced:
`WH:ORES=Critical/unannounced`. There is deliberately **no historical log**:
Custom Data is a fixed budget, and an append-only alert history is the one thing
guaranteed to exhaust it.

## What `Stalled=0` means — read before "fixing" stall detection

`[IOPM.Machines] Stalled` has read 0 for the entire life of this script. That
is expected, not a gap in coverage.

**The game already handles the common case.** A production block that cannot
complete the item at the head of its queue skips to the next item it *can*
build, provided it can pull the resources. When the missing resource arrives
it comes back to the skipped item on its own. IOPM never needs to intervene,
which is also why blocked ingredients get returned to the warehouse rather
than left sitting in machine inputs.

So a queue entry that is not advancing is usually NOT a stall. It is either
later in the queue than something the machine is currently building (machines
build one item at a time) or skipped because a resource is briefly absent.
Live example: `GoldWire Queued=1,500` sat unchanged across two dumps with
`GoldIngot` stock byte-identical, while `Superconductor` advanced 776 -> 823.
Both are made in the Wire Drawer; the wire jobs simply had not been reached.

**Detection is per machine, not per queue item.** `IsProducing` resets the
stall counter, so a machine busy on one item accrues no stall cycles however
long another queued item waits. That is deliberate: per-item stall tracking
would fire constantly on the skip behaviour above, and the remedy
(`EvacuateMachineInput`) would be wrong for it.

What stall recovery genuinely exists for is the case the skip behaviour cannot
escape: a machine with a queue, `IsProducing` false, and an input inventory
holding material it cannot use, unable to pull the right ingredients because
the input is full. Evacuating the input is the correct and only remedy there.

Rare trigger, correct design. Do not "improve" detection toward per-item
tracking without first re-reading this section.

## Four kinds of item knowledge — keep them separate

IOPM holds four independent facts about an item. Confusing them is the single
most expensive class of bug in this project's history, so they are named here.

| Knowledge | Where it lives | What it grants |
|---|---|---|
| **Physical identity** | `AddItemGroup` (ItemDef) or `AddLoadAlias` | A name resolves to a real `TypeId/SubtypeId` |
| **Blueprint identity** | `AddBlueprintGroup` | A job can be queued, IF a recipe also exists |
| **Recipe / yield** | `AddRecipes` | The planner may manufacture the item |
| **Preferred machine** | the recipe's machine field | Which block type gets the job |

A live inventory observation proves the **physical identity only**. It never
proves a blueprint id, a yield, or a machine. So an item can be fully
identity-known and still never produced — that is a correct state, not a gap.

Two consequences worth internalising:

- **A blueprint id is not an identity.** The blueprint table is consulted when
  queuing a job, never when resolving a loadout name. `Capacitor` had a
  blueprint id for several versions and still resolved nowhere (fixed 2.4.26).
- **An ingredient amount is not a recipe.** `Concrete` and `ArmoredPlate` appear
  in the Reactor and TokamakBlanket recipes. That proves those recipes consume
  them; it says nothing about how either is made.

### ItemDef vs loadout alias — which table to use

Adding an identity to the wrong table has real consequences:

- **ItemDef** (`AddItemGroup`) is a **stock-capable** alias. It credits
  `_onHand` and accepts a `[Stock]` target. Use it only for items IOPM is meant
  to account for and potentially manufacture.
- **Loadout alias** (`AddLoadAlias`) resolves a name and grants nothing else —
  no recipe, no ItemDef, never in `[Stock]` or `_onHand`. It also adds a row to
  the seeded loadout template menu.

Raw and refining-stage materials belong in the loadout alias table. As ItemDefs
they would pull refinery output into stock accounting, and refining is out of
IOPM's scope by design.

`AddLoadAlias` also registers the bare `SubtypeId`, which is **not unique across
TypeIds**. Where an ore and an ingot share a name, the bare name resolves to
whichever was registered; spell the other out in full
(`MyObjectBuilder_Ore/Uranium`) — loadout resolution accepts an explicit
`TypeId/SubtypeId` at priority D.

## `[Stock]` fills itself in (2.4.27+)

Every **stock-configurable** item is written into `[Stock]` automatically with a
target of `0`, so the whole configurable surface is visible and editable in one
place with no guessing at spelling.

**Nothing is ever destroyed.** Only absent keys are written. An existing value
is never overwritten, reordered or deleted — including a deliberate `0`, and
including a key you wrote under an alias (`Computer=500` correctly marks
`BasicComputer` as present and does not gain a duplicate row).

A target of `0` means "track it, never manufacture it". Delete a row and it
comes back at `0` on the next cycle; set it to a number to give it a floor.

`[Stock]` is kept sorted alphabetically and spelled canonically (2.4.31+, with
re-spelling added in 2.4.32). Write `Computer=500` and it becomes
`BasicComputer=500` — the name is normalised, the value is carried across as
raw text (`0.5` stays `0.5`). Keys IOPM does not recognise are left exactly as
you wrote them and sorted into place rather than dropped, so you can track your
own items there safely.

**If two keys mean the same item** — say `Computer=500` and
`BasicComputer=1000` — IOPM will **not** merge them, because that would throw
away one of your numbers, and it will **not** apply either one. The alias gets
no quota at all until you resolve it: it stops being a planner target, drops
out of `StockItems`, and disappears from the LCD. Both keys are kept exactly as
written and the conflict is reported under `[IOPM.ConfigError.*]`. Delete
whichever you do not want.

This holds even when the two values are **equal** (2.4.33+). The rule is about
the conflict, not the numbers — otherwise the same config would be valid or
invalid depending on a value you were about to change.

Everything in `[Stock]` appears on the `[IOPM-Stock]` LCD (2.4.29+), including
items IOPM cannot manufacture and keys you added by hand. `[IOPM.StockDisplay]
Rows` and `[IOPM.Production] StockItems` read the same dictionary, so they
always agree — if they ever disagree, something is genuinely wrong.

### What is stock-configurable, and what is not

| | Auto-listed? | Why |
|---|---|---|
| Manufactured components | **yes** | Things you may legitimately want a base floor for |
| `Polymer` | **yes** | Manufactured (`SyntheticPolymer`) despite carrying an Ingot TypeId — designated explicitly |
| Ingots | no | Refining output. Present only so recipe dependency resolution can price a component in raw material |
| Ores | no | Refining input, and IOPM does not manage refining |
| Ammo, tools, food, seeds | no | Identity only — they resolve in loadouts and never enter base stock accounting |

**Recipe availability does not gate the entry.** `Concrete` and `ArmoredPlate`
are listed even though IOPM cannot yet build them, because *"may I set a target
for this"* and *"can IOPM make this"* are different questions. Setting a
non-zero target on an item with no recipe is safe and reports `RawShortage`,
which correctly reads as "supply this yourself."

**Counting roots does not prove the catalog is complete.** "45 products / 45
recipes / 0 pending" only says every stock ROOT has a recipe; it says nothing
about the dependencies beneath them. Canvas shipped with a recipe in 2.4.35 and
was unbuildable on arrival because `SyntheticFabric` had none. Run
`python tests/audit_closure.py <source.cs>` (from this project root) — it walks the graph and classifies
every leaf as TERMINAL_PROCESS_BOUNDARY, MANUFACTURED_MISSING_RECIPE or UNKNOWN.

**TypeId is not evidence that something is terminal.** Leaves are classified
`TERMINAL_PROCESS_BOUNDARY` — *intentionally not manufactured by IOPM in the
current v2.4.x scope* — and membership is an explicit policy list, never derived
from an item's TypeId or inventory category. Two live counter-examples:
`Gunpowder` (`Ingot/Magnesium`) is crafted in a Munitions Factory, and `Polymer`
(`Ingot/Polymer`) is made from blueprint `SyntheticPolymer`. Both are Ingot-typed
and both are manufactured. TypeId says where an item is *sorted*, not whether it
can be *made*.

Currently one hole remains: `Gunpowder` (`Ingot/Magnesium`, referenced by
`Explosives`) is manufactured but has no recipe, so `Explosives` is blocked in
practice.

As of 2.4.35 every stock-configurable product has a validated recipe — 45
products, 46 recipes. The last eight were promoted one evidence
batch at a time: `ArmoredPlate` (2.4.30); `Capacitor`, `Girder`,
`RadioCommunication`, `SolarCell` (2.4.34); `Concrete`, `Explosives`, `Canvas`
(2.4.35).

**That does not merge the two ideas.** `StockConfigurable` and `_recipes` remain
independent, and the next product added will be identity-first again — listed in
`[Stock]` at `0`, reporting `RawShortage` if you set a target, until its recipe
is validated. The counts matching today is a coincidence of progress, not an
invariant. See "A recipe never answers *is this a stock item*" below.

### Display name ≠ alias ≠ SubtypeId

`Gunpowder` is the clearest case in the project: Industrial Overhaul reuses the
vanilla **Magnesium** subtype for the item its UI calls Gunpowder. A dump search
for "Gunpowder" finds nothing while the warehouse visibly holds a large stack.
If an item cannot be found by name, search the dump by quantity instead.

To make a further item stock-configurable, promote it to an ItemDef in
`AddItemGroup("MyObjectBuilder_Component", ...)`. Being a loadout alias is
deliberately not enough — that table also carries ammo and raw materials, so
membership there says only "this name resolves", never "this is a base
stockpile item".

## Configuration

All configuration lives in the programmable block's **Custom Data**. Sections
the script actually reads:

```
General  Sorting  Production  Display  Docking  Alerts  Stock  BlueprintOverrides
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
