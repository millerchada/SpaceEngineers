# IO Production Manager — Changelog

Full version-history detail lives here instead of in the source header, to
keep the in-file comment compact against the 100,000-character PB limit.

## Build step (from 2.4.5 onward) — READ THIS BEFORE DEPLOYING

The .cs files are now SOURCE, not the deployment artifact. Comments and
indentation cost ~16,000 of the PB's 100,000 characters and have no runtime
meaning, but deleting them from the source would destroy the documentation
that keeps this script maintainable. So we keep both:

    python build_pb.py IO_Production_Manager_v2.4.5.cs
        -> IO_Production_Manager_v2.4.5.min.cs      <-- paste THIS into the PB

2.4.5: source 97,955 -> artifact 81,750 (saved 16,205; 18,250 headroom).

build_pb.py strips // comments (string-aware), indentation, trailing space and
redundant spaces outside string literals. It KEEPS newlines deliberately, so
in-game compile errors stay locatable. It refuses to write the artifact unless
all 531 string literals survive byte-identical, code-context {}()[]; counts
match the source exactly, and the output is stable under a second pass.

The transform is only valid while the source contains NO verbatim @"..."
strings (leading whitespace is significant inside those) and no /* */ blocks.
build_pb.py hard-errors on both rather than silently corrupting them.

CAVEAT: in-game error line numbers now refer to the .min.cs, plus the PB's own
~32-line generated preamble. Map them back through the artifact, not the source.

## 2.4.5
Display only. No logic, phase, planning or docking change.
- New `FK()` formatter for the stock table: values >=10,000 collapse to floored
  k (129,775 -> "129k", 218,205.4 -> "218k"), >=1,000,000 to floored M. ALWAYS
  floors, never rounds up, so a displayed figure is never optimistic. Values
  under 10,000 keep full comma precision. Diagnostics deliberately keep the
  full-precision `F()` — never use `FK()` there.
- Stock columns: name 22->24 (fits "Construction Component", which was exactly
  22 and bumped its own row right by one), Qty/Quota 10->7. Net 4 chars
  narrower, so the Queued column stops clipping off the panel edge.
- Diagnostic keys are now emitted UNCONDITIONALLY (`BlockedBy`,
  `SupportDemand`, `SupportReserved`, `Organization/LastError`) — blanked
  rather than skipped.

ROOT CAUSE, confirmed in-game: MyIni cannot remove a key. `DeleteSection()`
followed by `Set()` on the same section in one pass overlays the ORIGINALLY
PARSED section, resurrecting every old key. Proven by controlled test: 2.4.4
stopped writing `BlockedBy` for Satisfied rows (`IsBlockedAction()` excludes
"Satisfied"), yet all seven stale values persisted unchanged. A section that
is deleted and NOT re-created does vanish (an `IOPM.Machine.*` section did),
which is what makes the overlay explanation fit. CONSEQUENCE FOR MAINTAINERS:
never conditionally omit an `[IOPM.*]` key — it will keep its last value
forever and read as current. Existing residue needs one manual deletion of
the `[IOPM.*]` block; it regenerates clean.

## 2.4.4
Display only. No logic, phase, planning or docking change.
- New [Display] section. `ManageFonts=true` (default) keeps current behaviour;
  `ManageFonts=false` makes the script set only ContentType and never touch
  Font/FontSize, so the panel's own settings stick. Previously WriteSurfaces
  forced Monospace/0.8 on EVERY render, silently reverting any manual change
  within one cycle. NOTE: the tables are space-padded, so a proportional font
  will misalign the columns — keep Monospace and change only the size.
- `StatusFontSize` / `StockFontSize` (default 0.8 each).
- `StockRowsPerPage=0` (default, all rows on one page). When >0 the stock
  display pages, advancing ONE page per rendered cycle — so page dwell is
  UpdateSeconds and cannot go below it (rendering happens once per logical
  cycle, in the final phases). Header shows "IOPM STOCK n/m" when paging.
- `BlockedBy` is now written only when the row's Action is actually blocked;
  it was lingering on Satisfied rows and reading as a live blocker.
- `[IOPM.Organization] LastAttempt` reports "none" instead of formatting
  uninitialised state as a failure on cycles where nothing was attempted.

NOT A BUG, recorded so it isn't re-investigated: LCD panels showing "Online"
after you fly out of range and back is Space Engineers' own surface render
streaming, not a script fault. The text stays in the panel buffer the whole
time (open the LCD's text editor and it is there); only the rendered texture
is stale, until the next WriteText repaints it. Because IOPM renders once per
logical cycle in the final phases, worst case is one cycle (~UpdateSeconds).
Caching the built text and re-writing it every tick would cut that to ~0.17s
at the cost of a per-tick WriteText; deliberately not done while source size
is over budget.

## 2.4.3
PB-whitelist compile fix + live-verified item identities. No logic, phase,
budget or feature change.

FIRST REAL IN-GAME COMPILE of the 2.4.x line failed:
`Error: The type or member 'Comparison<CI>' is prohibited`. The 2.4.0 refactor
had replaced 2.3.4's inline sort lambdas with a cached comparator field to avoid
per-call closure allocation. Naming that generic delegate type in source is not
on the PB whitelist. Reverted to inline lambdas that call the existing named
static comparator: the body still does no dictionary/friendly-name lookups
inside a sort (the 2.3.1 crash-safety property), and because the lambdas capture
nothing the compiler caches the delegate, so no per-call allocation returns.
NOTE: `Func<T,TResult>` IS whitelisted — 2.3.4 shipped one for days. Do not
assume the two are treated alike. In-game error line numbers are offset by ~32
from file lines (the PB wraps the script in a generated preamble).

Also: `SpaceCredit` -> Misc. It rode the broad `PhysicalObject` -> Tools rule,
putting 4,544 credits on the Tools shelf. Same override class as Grain/Algae.

Also: `InteriorPlate`/`Detector`/`BulletproofGlass` added as config aliases, so
the real SubtypeIds resolve in [Stock] and loadouts, matching the existing
LargeTube/Computer/Medical/PowerCell precedent.


Three managed aliases pointed at physical item SubtypeIds that do not exist,
confirmed against a live in-game inventory dump (Item Identity Dump v1.0.0):
- `AluminumPlate` -> `MyObjectBuilder_Component/InteriorPlate` (was `/AluminumPlate`; 11,028 on hand read as 0)
- `SensorCluster` -> `MyObjectBuilder_Component/Detector` (was `/SensorCluster`; 768 on hand read as 0)
- `Glass` -> `MyObjectBuilder_Component/BulletproofGlass` (was `/Glass`; 129,307 on hand read as 0)

A wrong subtype here fails SILENTLY and expensively: category routing still
works (it keys on TypeId), so no warning is ever raised, but the item never
credits its alias. Stock reads 0 forever, Need never falls, and the planner
tops the queue up every cycle. Under 2.3.4 this produced ~129k surplus
BulletproofGlass over several play sessions before being caught. It also
propagates: SensorCluster reported `Blocked by Glass` while 129k Glass sat
on the base, and AluminumPlate reported a false `AluminumIngot` shortage.

LIVE UAT CONFIRMED (first successful in-game run of the 2.4.x line): all three
identity fixes credited immediately — Glass 129,759/500, SensorCluster 768/500,
AluminumPlate 10,967/2,000, all Satisfied; the runaway Glass queue stopped and
the false AluminumIngot shortage disappeared. PeakInstructions fell from 44,654
(2.3.4, phase=Sorting) to 18,772 — the 2.4.0 refactor's real payoff. New peak
phase is DockScan, exactly as predicted. Docking discovered 5 constructs /
64 unload sources with 0 blocked transfers.

All 13 MyObjectBuilder_Ingot identities were verified CORRECT by the same
dump and must not be "fixed": Iron, Nickel, Cobalt, Copper, Gold, Aluminum,
Titanium, Silver, Silicon, Lithium, Polymer, Carbon, Sulfur. In particular
`AluminumIngot=Aluminum` is right — its 0 stock was a genuine shortage
(18.08 on hand), and that demand vanishes once AluminumPlate can see its
own 11,028 InteriorPlate.

These three were exactly the items 2.3.4's own source comment listed as
blueprint-validated-but-item-unvalidated. `GoldWire` and `MetalGrid`, from
that same unvalidated list, were confirmed CORRECT by the dump. Blueprint
definition IDs were already right in every case — a blueprint's definition
ID and its output item's SubtypeId are independent strings.

## 2.4.2
Corrective pass on the 2.4.1 docking feature.
- Connector state now uses the real `IMyShipConnector.Status == MyShipConnectorStatus.Connected`
  instead of a non-existent `.Connected` bool.
- GOAT [Stock] loadout items no longer have to be IOPM production items. A separate loadout
  identity layer (IOPM alias -> explicit raw type -> compact GOAT ammo/component table ->
  observed SubtypeId) resolves ammo, non-managed and modded items. Nothing in that layer
  becomes manufacturable or appears in [Stock]/_onHand.
- Loadout availability is now measured physically across approved local base sources for ANY
  raw item type, not only items in the production registry.
- GOAT Custom Data parser is subsection-aware: `~` heading/help lines, Container List, Settings
  (method/rebalancePercentage/name), Modifier Explanation and Pinned Items are no longer
  mistaken for item quotas.
- Remote ignore/lock precedence: [IOPM-Ignore]/[Locked]/!GSIM-Locked on a remote block excludes
  it entirely; [Stock] loadouts are serviced even under a [No Sorting] boundary; [No GOAT] or
  [IOPM-Ignore] on either connector excludes the whole construct.
- Connector-boundary policy is aggregated over ALL local pairs reaching a construct before its
  record is built, so exclusion is no longer GetBlocks() enumeration-order dependent.
- Existing/manual production queues are now protected RECURSIVELY: a queue needing new
  Electromagnets also commits the Iron/Nickel/CopperWire behind them against loadout borrowing.
- DockService has its own transfer budget in its own phase; with nothing docked, sorting
  capacity is exactly 2.4.0's again.
- Sorting order now evacuates production machine outputs first, before cosmetic work.
- UnloadConnectorInventory is honoured for local and remote connector inventory; the connector
  still works as a dock anchor when it is false.
- `Item=All` no longer reports satisfaction when nothing was available to move.

## 2.3.3
Converted the heavy cycle (Discover -> Sorting -> ScanInventory -> StallRecovery
-> BuildPlan -> ApplyPlan -> WriteDiagnostics -> StatusRender -> StockRender)
into a cooperative multi-tick state machine: at most one phase executes per
Update10 invocation, so a mid-cycle instruction-limit termination can no
longer leave Custom Data stranded on a stale version (VERSION and
[IOPM.Runtime] used to be written only at the very end of the old
single-invocation RunCycle()). Added Echo("IOPM v... | Phase=...") every
tick, before any heavy phase runs, so version/phase can be verified even if
that tick's phase later throws. Runtime diagnostics now track current
phase, last phase's instruction count, session peak instructions, peak
phase name, and PB max instructions.

## 2.3.2
Fixed a PB instruction-limit crash: status and stock LCD text rebuilds no
longer run every Update10 tick or share an invocation with the heavy
accounting cycle. (Superseded by 2.3.3 — this fix assumed the heavy cycle
itself fit in one invocation, which UAT proved false on larger grids.)

## 2.3.1
Fixed a PB crash in the [IOPM-Stock] display: the alphabetical sort
comparator called Friendly()/dictionary lookups inline, which could throw
mid-sort. Rows are now precomputed once with per-row error isolation,
matching the OrganizeInventories() defensive-sort pattern.

## 2.3.0
Added a live [IOPM-Stock] LCD display (Item/Qty/Quota/Queued), read-only,
auto-populating missing [Stock] aliases at 0 without overwriting existing
user quotas.

## 2.2.0
Fixed ReserveSupportBudget over-reserving physical stock already covered by
queued/planned output.

## Prior releases (1.0.11 - 2.1.4)
See individual versioned .cs files for full history.
