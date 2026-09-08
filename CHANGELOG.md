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

## 2.4.12
SIX NEW RECIPES, managed set 25 -> 31. Transcribed from live in-game blueprint
tooltips (Industrial Overhaul v1.7.7), not guessed:

  Superconductor    Wire Drawer     Rubber 3, GoldWire 15
  GravityGenerator  Nano-Assembler  TantalumIngot 5, GoldWire 30, CobaltIngot 25,
                                    SilverIngot 20, Electromagnet 15
  Thrust            Nano-Assembler  Electromagnet 6, CobaltIngot 10, GoldWire 3,
                                    PlatinumIngot 0.5
  QuantumComputer   Nano-Assembler  TantalumIngot 0.1, PlatinumIngot 0.2,
                                    GoldWire 6, AdvancedComputer 2, AluminumPlate 2
  ElectronMatrix    Nano-Assembler  ArmorGlass 1, TantalumIngot 0.5, LaserEmitter 1,
                                    GoldWire 2, Polymer 2
  FSSolarCell       Nano-Assembler  ArmorGlass 1, TantalumIngot 0.2, GoldWire 1,
                                    SiliconWafer 5, Plastic 3, TitaniumIngot 0.5

New ItemDefs: TantalumIngot=Tantalum, PlatinumIngot=Platinum (both dump-confirmed)
and 8 components. Machine tokens only RANK candidates - eligibility is decided by
pb.CanUseBlueprint() - so an approximate machine name still works.

OUTPUT SUBTYPES: Superconductor, GravityGenerator, Thrust and LaserEmitter are
CONFIRMED against a live Item Identity Dump. ArmorGlass, ElectronMatrix,
FSSolarCell and QuantumComputer are NOT - none is physically present on the base,
so their Component/<Name> subtype is an assumption of exactly the kind that caused
the 129k glass over-production.

WHY THAT IS STILL SAFE TO SHIP: a new recipe auto-populates [Stock] at 0, and none
of these four is an ingredient of anything else, so there is no support demand and
NOTHING is produced until a target is set by hand. Before setting a nonzero target
for any of those four: craft one manually, run the Item Identity Dump, confirm the
real SubtypeId. The failure signature is Stock stuck at 0 while production runs.

ElectronMatrix and FSSolarCell additionally need ArmorGlass and LaserEmitter, which
have no recipes yet, so they will report RawShortage until those are added or the
ingredients are stocked by hand. Honest and fail-safe, not a bug.

## 2.4.11
The seeded template is grouped by the SAME 9 warehouse categories used
everywhere else in the script (Ores/Ingots/Components/Ammo/Tools/Consumables/
Seeds/Misc), emitted in CATS order, instead of a hand-rolled TypeId chain that
dumped tools, consumables and bottles into one "Other" heap. Roughly 120
entries now arrive under the same headings you already read on the status
panel.

New CategoryForRaw() applies the same precedence as CategoryForItem but from a
raw "TypeId/SubtypeId" string - subtype override first (Grain->Consumables,
SpaceCredit->Misc), then the broad TypeId map - so a static table entry with no
live MyItemType still classifies correctly. This REPLACED a five-branch
StartsWith chain, so the grouping logic got smaller, not larger.

WHY NOT JUST MIRROR THE PB'S [Stock] LIST: [Stock] is what IOPM MANUFACTURES
(25 recipes). A loadout is what a ship CARRIES, and they barely intersect. Of
118 item types observed on the live base, the loadout-relevant ones are mostly
NOT manufactured: MealPack_KelpCrisp (2,059), NATO_25x184mm (1,228), Ice
(1.86M), welders/grinders/drills, Medkit/Powerkit/RadiationKit, Hydrogen and
Oxygen bottles. Mirroring [Stock] is what 2.4.6 did, and it is exactly why a
greenhouse container never listed its own Ice. Production knowledge, base stock
targets and loadout item identity are three separate domains - keep them so.

## 2.4.10
The seeded template also lists every item type observed in the BASE, not just
in the container being seeded.

WHY 2.4.9 WAS WRONG: a container is seeded exactly when its Custom Data is
empty, which is usually also when it holds nothing - so observing only the
container listed almost nothing beyond the static tables. Reported case: a
greenhouse [Stock] container produced no Ores group at all, because the
greenhouse had consumed its Ice before the seed ran. The base inventory is the
real "what exists in this world" vocabulary, and it is what makes ORES and
MODDED items reachable from the menu.

Cost is paid only on a seed, which requires empty Custom Data and so happens
about once per container ever - not per cycle.

## 2.4.9
The seeded template now lists EVERY item IOPM can resolve by name, so the
player picks from a full menu instead of a curated subset: recipe aliases, the
loadout alias table, and whatever the container holds. ~62 entries, grouped
Components / Ingots / Ammo / Ores / Other, each still at "0M".

Deduped by RESOLVED RAW TYPE, one line per real item, with an IOPM alias
beating a loadout-table name for the same item - so "SensorCluster" is listed
and "Detector" is not, "Glass" and not "BulletproofGlass". Without that dedup
the same physical item would appear twice under different names and two quota
lines would fight over it.

GROUP TITLES ARE PARSER-SENSITIVE. IsMetaSection skips any heading containing
Container/Setting/Modifier/Explanation/Pinned/Note. Components, Ingots, Ammo,
Ores and Other are all verified clear. NEVER title a group anything containing
"Container" - the entire group would be silently ignored.

## 2.4.8
The seeded template now lists the 25 managed components PLUS whatever the
container already holds, so ores, ammo and modded items appear instead of only
recipe items. Reported case: a greenhouse [Stock] container holding Ice got a
components-only template, so the Ice it actually carried was never listed.

An observed item is named by its IOPM alias when it has one, otherwise by its
bare SubtypeId - which resolves through the observed-subtype path proven with
Ice. Process resources (Heat) are excluded; they must never enter a quota
table. Heading changed "Components" -> "Items" (verified against
IsMetaSection: "Items" matches none of Container/Setting/Modifier/Explanation/
Pinned/Note, so the parser still reads the entries). Everything still seeds at
"0M" - see the 2.4.6 warning about why that is load-bearing.

Uses a LOCAL item list rather than a shared scratch buffer: this runs inside
DockDiscover and must not alias a buffer another pass is holding.

MILESTONE / OPERATIONAL NOTE: source has passed the PB ceiling (100,733 at
2.4.8, 102,079 at 2.4.9). The .cs can no longer be pasted into a programmable
block at all. build_pb.py is no longer an optimisation, it is mandatory.
Never try to deploy the source.

## 2.4.7
QUOTA MODIFIERS COMPLETE (except All). "Motor=100L" removed the container's
excess and added nothing: base Motor 744 -> 1,201, LoadoutShortages=0. That was
the first remote->base loadout transfer, so PushExcess is now exercised too.

Over-commit recovery confirmed at the same time, and it is worth understanding
because it looks wrong: 2,262 CopperWire stayed queued for Motors the base no
longer needed, while CopperWire reported 5,105/5,000 Satisfied with
JobsAdded=0. IOPM never cancels a queued job - AddQueueItem is the only queue
mutation - so it over-commits when demand vanishes mid-flight and recovers by
simply not queueing more until stock drains. Expected, not a runaway.

VALIDATED IN-GAME. The restored key made the whole bottleneck chain readable
at a glance: Motor Blocked=257 BlockedBy=Electromagnet, Electromagnet
Action=Queued Blocked=745 BlockedBy=CopperWire (exactly the partially-blocked
Queued row 2.4.5 had hidden), CopperWire Queued=3,026 in flight.

NOTE for reading these numbers: base stock CAN sit below the borrow floor.
Observed Motor Stock=738 against a 750 floor with ToLoadoutTransfers=0 - the
missing units were consumed outside IOPM (welding/manual use). The floor limits
what IOPM will LEND to a loadout, not what the player can spend. A real leak
would look like the number falling while a loadout is short AND
ToLoadoutTransfers is climbing.

Fixes a diagnostic regression introduced in 2.4.5. BlockedBy is now emitted
whenever it is set, not only when Action is one of the fully-blocked states.

2.4.5 gated the write on IsBlockedAction(r.Action), which excludes "Queued".
But a Queued row can be PARTIALLY ingredient-limited - Action=Queued with
Blocked=45 - and that is exactly when you need to know which ingredient is the
limiter. The gate was a workaround for stale values; that root cause is now
properly fixed by always emitting the key, so the gate bought nothing and cost
real information. Observed live: Motor showed Blocked=45 with BlockedBy blank
during the loadout-borrow UAT.

BORROW CHAIN VALIDATED IN-GAME (2.4.6): a Motor quota on a docked [Stock]
container moved ~79 Motors out of the base. Base stock fell 1,000 -> 921 with
the 750 floor intact, remote inventory stayed out of base accounting, and the
planner saw Need=57 (1000 - 921 - 22 queued) and began replenishing. The
recursive expansion is arithmetically exact: 79 Motors in flight raised the
LargeSteelTube target by 79 (1 per Motor) and the Electromagnet target by 237
(3 per Motor), with ingot support reserved underneath. Sections I, H and J of
the docking spec are now confirmed on live data.

## 2.4.6
VALIDATED IN-GAME: emptying a remote [Stock] container's Custom Data produced
the 25-line template byte-for-byte on the next scan, and the container's
existing 1,000 Ice was left untouched - unlisted items are never swept, since
ServiceLoadouts only iterates the quota table.

Seeds a starting quota template into a [Stock] loadout container whose Custom
Data is EMPTY, so quotas can be filled in on the block instead of typed from
scratch. New [Docking] SeedLoadoutTemplate=true (default on).

Guarded by string.IsNullOrWhiteSpace(b.CustomData): anything already present -
GOAT's own section, or the player's - is never read past, never altered, never
appended to. This is the same rule the PB's own WriteDefaultCustomData follows.
Written in GOAT's bounded format so the existing parser reads it back and GOAT
interop is preserved for free. New diagnostic key: LoadoutsSeeded.

THE "0M" IN THE TEMPLATE IS LOAD-BEARING - DO NOT "SIMPLIFY" IT TO 0.
A bare "Item=0" is an EXACT quota, and ServiceLoadouts reads that as "remove
everything above 0":

    if (have > q.Amt + 0.0001 && q.Mod != 'M') PushExcess(...)

Seeding bare zeros would therefore STRIP a docked container of every item the
template lists - on a greenhouse, it would drain the Ice. "0M" (minimum 0) is
inert in both directions: never adds, because stock is never below 0, and never
removes, because the push branch skips M. The template does nothing whatsoever
until a real quantity is edited in.

LOADOUT PATH VALIDATED IN-GAME (2.4.5): a hand-written "Ice=1000" block in a
remote [Stock] container filled it to exactly 1,000 and stopped. That exercised
the layer-C fallback resolver (Ice is neither a managed recipe nor in the
hardcoded alias table, so it could only resolve by matching an observed
SubtypeId in base inventory - the mechanism modded items rely on), zero-target
generic accounting, and base->remote transfer with base stock unaffected.

## 2.4.5
VALIDATED IN-GAME. First version deployed as a build_pb.py artifact rather
than raw source: the stripped .min.cs compiled and runs, so comment and
indentation stripping is proven safe and ~16,000 characters of headroom are
permanent. PeakInstructions 18,792 - identical to 2.4.4, confirming the strip
has no runtime effect. Every Satisfied row now reports BlockedBy= blank and
the five legacy IOPM.Organization keys are gone, confirming both the MyIni
diagnosis and the always-emit-the-key fix.

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
is stale.

PROVEN by controlled test (2.4.5 era): a plain UNSCRIPTED LCD with hand-typed
static text was placed beside two IOPM panels. All three reappeared at the same
moment, ~90 seconds after coming back into range. Nothing in a PB script can
influence this. A cached-text repaint loop was designed and then DISCARDED on
this evidence - it would have added source, instructions and per-second network
churn for zero benefit. Do not propose it again.

Separately, the script's data cadence IS tunable: rendering happens in the last
two phases of a 13-phase cycle, gated by [General] UpdateSeconds. Because the
architecture runs exactly one phase per Update10 tick, lowering UpdateSeconds
does NOT raise peak instructions per invocation - it only runs cycles closer
together. UpdateSeconds=2 makes the ~2.2s cycle itself the limiter.

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
