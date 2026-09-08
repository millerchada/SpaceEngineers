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

## 2.4.22
ALL 37 RECIPES NOW HAVE A VERIFIED OUTPUT SUBTYPE. ElectronMatrix and
FSSolarCell were the last two: both credited Stock=1 once crafted. Nothing in
the item registry is an assumption any more, which fully closes the class of bug
that cost 129k surplus glass.

The consumption arithmetic validated the transcribed recipes to the decimal:
  ArmorGlass    10 -> 8      1 each x 2 items
  LaserEmitter   1 -> 0      ElectronMatrix needs 1
  Polymer          -2        ElectronMatrix needs 2
  Plastic      504 -> 501    FSSolarCell needs 3
  TantalumIngot 6,775.2 -> 6,774.5 = 0.7 exactly = 0.5 + 0.2
That last line is FRACTIONAL ingredient math across two different recipes,
correct to one decimal place. A bonus check landed at the same time: Glass fell
3 while Lightbulb rose 470 -> 500, and Lightbulb has a x10 output yield needing
1 Glass per job - 30 bulbs = 3 jobs = 3 Glass, so non-unity yields are right too.

Partial evidence on undock: LoadoutContainers went 2 -> 1 while
ConnectedConstructs stayed 9, and base Motor moved 750 -> 780 - UP, not down by
the departed loadout's contents. Remote inventory never counted toward base
stock, so losing it changed nothing. Not a clean undock test, since the
construct count did not move.

DOCKING UAT ESSENTIALLY COMPLETE. Two final tests passed on live data:

MULTIPLE LOADOUT CONTAINERS / NO DOUBLE-SPEND - the last untested safety
property. Two [Stock] containers each demanding Motor=500 (combined 1,000)
against ~30 units of available headroom parked base Motor at EXACTLY 750, which
is 1000 x (1 - 25/100). Neither breached the floor: _dockSpent stopped them
spending the same headroom twice. LoadoutShortages=2 with both reporting
WaitingFor=Motor.

ITEM SUBTYPES ALL VERIFIED - ArmorGlass 10, SuperMagnet 1, TokamakBlanket 1 all
credited, so those last three ItemDefs were correct. EVERY ONE of the 37 managed
recipes now has a verified output subtype; no assumed identity remains anywhere
in the registry, which closes the class of bug that cost 129k glass.

The recipes verified themselves incidentally: Cryocooler 20 -> 19 (SuperMagnet
needs exactly 1) and Ceramic 2,873 -> 2,871 (TokamakBlanket needs exactly 2).

Still untested and deliberately low priority: the All quota modifier, the
[No GOAT] connector tag, and undock state cleanliness.

VALIDATED IN-GAME: UnloadSources 160 -> 156, exactly the four waste chutes
leaving the source list, and Warnings settled at 0 for real rather than by
backoff suppression. Organization resumed with Examined=34, Succeeded=2,
Failed=0 - confirming the 2.4.19 merge path is not silently no-opping, which was
the open risk there.

REMOTE ejectors are now skipped as unload sources. 2.4.20 fixed only LOCAL
connectors, in Discover; remote blocks on a docked construct go through
DockDiscover, which had its own unload-source check with no ThrowOut test. So
IOPM kept trying to pull material back out of ejectors on docked ships.

Found because 4 of 5 identically-named connectors on an orbital miner turned out
to be sorter-fed waste chutes. IOPM was fighting the ship's own waste handling,
which is what the Stone warnings had been reporting all along - and the shared
name is why they looked like one block failing repeatedly rather than four
different blocks.

NOTE FOR MAINTAINERS: connector handling exists in TWO places - Discover for
local connectors and DockDiscover for remote ones. A rule about connectors has
to be applied in both. 2.4.20 patched one and missed the other, and the symptom
survived two more versions before the cause surfaced.

## 2.4.21
VALIDATED IN-GAME: Warnings 27 -> 0 on the second cycle after deploy, with
ToBaseTransfers=3 confirming legitimate unloading was not blocked. The first
cycle after any recompile still warns in full, because the backoff only engages
once a source has failed a complete pass - do not read that as a failure.

Expect the count to OSCILLATE rather than sit still: near 0 for six cycles, then
one burst of ~20 when the stuck source is retried. That is the design - the
fault stays visible, at a sixth of the cost and noise.

STUCK-SOURCE BACKOFF. A source inventory whose items all fail to move is
now retried once every 6 cycles instead of every cycle.

Failing to route costs no transfer budget - tb only decrements on success - but
it costs a TryTransferItem binary search PER DESTINATION. Observed live: two
remote drills on a docked ship re-probed all 18 Ores containers plus Overflow
every single cycle, producing 37 warnings and drowning out real ones.

ROOT CAUSE, CONFIRMED: a DAMAGED CONVEYOR JUNCTION on the drill. There really
was no path from that block to base storage. (An intermediate theory that this
was a race against the game's own conveyor system - IOPM holding a stale
MyInventoryItem snapshot - was wrong for this incident. That failure mode is
possible in principle, but it is not what happened here.)

THE WARNINGS WERE NOT NOISE - THEY WERE A CORRECT FAULT REPORT. This pattern is
a reliable indicator of broken conveyor infrastructure on the source grid:

    CanItemsBeAdded says YES, the transfer fails anyway, to EVERY destination
    in the category AND to an Overflow container showing 0% fill

A genuinely full warehouse cannot produce that, because an empty Overflow would
accept the item. When you see it, go look for a damaged or disconnected block on
the source grid. IOPM found a damaged junction here before the player noticed it.

That is why the backoff RATE-LIMITS rather than silences: retrying every 6th
cycle still surfaces the warning periodically, so a real fault stays visible,
while the re-probing cost and the flood both drop about 6x. Suppressing it
entirely would have thrown away a working diagnostic. The backoff also covers a
genuinely full warehouse, and clears the moment a source succeeds - so repairing
the junction recovers within 6 cycles with no intervention. Keyed on the
inventory, cleared wholesale past 256 entries since docked ships come and go.

This is the third instance of the same shape this session: the ejector, the
loadout quota re-parse, and now this. IOPM had no memory of work that failed or
had not changed, so it repeated it every cycle. Worth checking for when
something feels expensive.

## 2.4.20
A local connector with ThrowOut enabled - an EJECTOR - is no longer treated as
transit cargo to recover.

An Ejector is an IMyShipConnector, so the "clean up local connector inventory"
behaviour from 2.4.1 tried to rescue its contents into the warehouse. That
fights the block's entire purpose, and because an ejector is usually
conveyor-isolated every attempt fails. Observed live: one "Base Ejector" holding
Stone produced 8 failed transfers and 9 warnings PER CYCLE, each costing a
TryTransferItem binary search, and drowning out real warnings. Overflow reported
"full/unreachable" while showing 0% fill, which is the signature of an
unreachable source rather than a full destination.

ThrowOut is the right test rather than a block subtype: it is exactly the
setting that says the player wants this material gone. The ejector remains a
dock anchor; only inventory routing is skipped. Wrapped in try/catch in case
ThrowOut is not available on every connector variant.

NEEDS IN-GAME CONFIRMATION that IMyShipConnector.ThrowOut is whitelisted. If it
is not, the guaranteed workaround is naming the block "[Ignore]", which takes it
out of routing via LOCK_TAGS.

DEFERRED FEATURE - EJECTOR MANAGEMENT. The original intent was for IOPM to drive
ejectors: keep a small buffer of Stone/Gravel and dump the excess so the
warehouse is not overrun. Deliberately NOT built. Ignoring ejectors is the
settled behaviour for now.

If it is ever picked up, the design worked out was: IOPM PUSHES excess into a
tagged ejector whose ThrowOut the player leaves permanently on - no block-state
writes, no cycling anything on and off, just a transfer decision, bounded by the
existing budget at the lowest priority. Config would be a CEILING, the inverse
of [Stock]'s floor:

    [Dump]
    Enabled=true
    Stone=50000
    Gravel=20000

with hard rails: only items explicitly listed are ever dumped, never below the
keep amount, only into tagged ejectors. This is the ONLY thing IOPM would ever
do that destroys material permanently, so it needs those rails and it needs
deliberate sign-off, not inference.

Open questions never answered: the real SubtypeIds for Stone and Gravel (the
live warning showed Stone classified as Ingots, so Ingot/Stone rather than the
vanilla Ore/Stone; Gravel unknown), the keep amounts, the tag name, and above
all whether a tagged ejector is even conveyor-reachable FROM the warehouse -
the observed one was isolated, which is why every rescue attempt failed. If it
is unreachable the push model cannot work at all.

## 2.4.19
Fixes stack FRAGMENTATION in alphabetical organization, reported in-game as
"duplicates of items in the components container".

ROOT CAUSE: the positional move must pass stackIfPossible:FALSE -

    ci.Inventory.TransferItemTo(ci.Inventory, sourceIndex, mismatchAt, false, amount)

- because that is the only way to land an item at an exact slot; with true the
game merges it wherever it likes and the sort cannot position anything. The
side effect is that an item arriving where its own type already sits becomes a
SEPARATE stack. Containers therefore fragment into several stacks of one item
over time. Nothing is lost or double-counted - Stock figures sum GetItems()
server-side and stay correct however many stacks the total is spread over - but
it looks like duplicated items, and it stops the ordering ever converging
(OutOfOrder never reached 0 across any live snapshot: 2, 4, 7, 2...).

FIX: before any positional move, if one item type occupies more than one slot,
merge those two stacks with stackIfPossible:TRUE and spend the container's one
action per cycle on that. Once no type is split, the sort has a fixed point to
reach. Diagnostics report it as "merge <Item>" in LastAttempt and MERGE in the
cycle log.

NEEDS IN-GAME CONFIRMATION: same-inventory merging via TransferItemTo with a
target index is not clearly documented. If the merge silently no-ops, the
symptom is _orgFailed climbing with OutOfOrder still never reaching 0 - in
which case turn organization off rather than letting it churn.

New [Sorting] Organize=true (default on). Set false to disable slot ordering
entirely; it is purely cosmetic, runs last, and only ever spends leftover
transfer budget.

A phantom stack that survives nothing - present in the terminal but not really
there - is more likely client display desync than fragmentation. Reconnecting
clears a desync; a real split stack survives.

## 2.4.18
PERFORMANCE ONLY. Adding a SECOND loadout container took PeakInstructions from
16,842 to 27,334 and moved the peak back to DockScan - about 10,000 per
container. Two separate costs, both introduced by making the seeded template a
full ~120-entry menu in 2.4.9-2.4.11:

1. ParseGoatStock re-parsed every container's Custom Data EVERY cycle, and each
   entry not found in a lookup table falls through to ObservedType(), which does
   a GetItems() per inventory. ~50 ore/tool/consumable entries x 35+ base
   inventories, per container, per cycle. Now the parsed quota table is cached
   against the Custom Data string it came from and only re-parsed when that
   string changes. This is the "do not repeatedly reparse unchanged Custom Data"
   rule from the original design constraints, which the full menu had broken.

2. ServiceLoadouts called InvAmount per quota entry - another GetItems() each,
   ~120 per container per cycle. The container is now snapshotted once into
   _loHave, making it O(items + entries).

CONSEQUENCE of the cache: a quota line that fails to resolve stays unresolved
until the container's Custom Data changes - which is what you would edit anyway
to fix a typo. The cache is keyed on block EntityId and cleared wholesale past
64 entries, since ships come and go.

_loHave reuses _itemsB, which is otherwise only used by BalancePools in the
Sorting phase - a different tick, so no buffer aliasing.

## 2.4.17
LaserEmitter and Cryocooler recipes, from live tooltips. Managed set 35 -> 37.

  LaserEmitter  Advanced Assembler  Glass 1, Lightbulb 3, SiliconWafer 2,
                                    SilverIngot 2, LithiumPaste 1, AluminumIngot 3
  Cryocooler    Assembler           CopperWire 2, LargeSteelTube 1, Motor 1,
                                    Thermocouple 1

These were the last two ingredient-only components. No new ItemDefs were needed
and both output subtypes were already dump-confirmed, so no verification
protocol applies. Every ingredient across all 37 recipes resolves.

Cryocooler's machine token is "Assembler", and MachineRank deliberately refuses
to match that token against an "advanced assembler" - so it prefers a plain
Assembler, matching where the blueprint actually appears. Eligibility is still
decided by CanUseBlueprint, so the token only affects ranking.

CHAINS NOW CLOSED, with one shared gate:
  Cryocooler -> SuperMagnet          genuinely producible; all inputs plentiful
  LaserEmitter -> ElectronMatrix     gated on ALUMINIUM
  ArmorGlass -> ElectronMatrix, FSSolarCell   gated on ALUMINIUM
AluminumIngot is refinery output (out of scope) and was ~18 on hand against
3 per ArmorGlass AND 3 per LaserEmitter. CrushedBauxite was observed entering
the warehouse, so aluminium is coming - until it does, those three report
RawShortage rather than producing.

WATCH SilverIngot: ~542 on hand and consumed by GravityGenerator (20 each),
LaserEmitter (2) and Reactor (5). It has no visible refining stream and is the
tightest non-refinery input.

## 2.4.16
OVERFLOW DRAIN-BACK VALIDATED on live data. With Ores and Ingots both at 100%
and Overflow absorbing the spill at 42.8%, adding containers (Ores 17->18,
Ingots 5->7) took Overflow to 0% and both warnings cleared. Material spilled
correctly under pressure and returned correctly once room existed - no manual
intervention.

WHAT SCALES WHAT, now that DockScan is fixed and Sorting is the peak phase:
  Sorting   grows with CONTAINER COUNT (and pending Organization work).
            Measured 10,747 -> 15,248 -> 16,842 across 32 -> 35 containers.
            Roughly linear, so ~2x containers implies ~2x this phase. Bounded
            in practice because Organization only gets leftover transfer budget.
  DockScan  since 2.4.13, independent of docked construct count.
Sorting therefore grows with the base you build, not with other players'
traffic - predictable, unlike the pre-2.4.13 DockScan behaviour.

VALIDATED IN-GAME. StockItems settled at 35 with plan records for all four new
items. Reactor credited Stock=78, confirming Component/Reactor. PeakInstructions
10,747-15,248 with PeakPhase back to Sorting - DockScan has dropped out of the
top spot entirely since 2.4.13.

EXPECT A ONE-CYCLE LAG whenever recipes are added. Adding a recipe makes
EnsureStockAliasesPresent write the new alias into [Stock] at 0 during
WriteDiagnostics - but LoadConfig for that cycle already ran against the older
[Stock], so StockItems reads low and the new items have no plan records for one
cycle. It self-corrects: the script's own write sets the config-dirty flag
(deliberately independent of the self-write guard, see 2.4.0), so the next Idle
reloads and the count settles. Not a bug; do not chase it.

STILL UNVERIFIED subtypes: ArmorGlass, SuperMagnet, TokamakBlanket. None exists
on the base. Craft one and run the Item Identity Dump before setting a nonzero
target.

FOUR NEW RECIPES from live in-game tooltips. Managed set 31 -> 35.

  ArmorGlass      Ceramics Furnace   AluminumIngot 3, PotassiumNitrate 1
  Reactor         Advanced Assembler TitaniumPlate 1, Concrete 3, Carbon 3,
                                     SilverIngot 5, Plastic 10
  SuperMagnet     Advanced Assembler TantalumIngot 1, TitaniumIngot 2,
                                     GoldWire 2, Cryocooler 1
  TokamakBlanket  Advanced Assembler Ceramic 2, LithiumPaste 2, ArmoredPlate 1,
                                     CopperIngot 2, Thermocouple 1

ArmorGlass completes the ElectronMatrix and FSSolarCell chains, which had been
sitting at RawShortage on it since 2.4.12.

New ItemDefs: PotassiumNitrate = Ingot/Niter (this is what the game calls
"Potassium Nitrate"; named for the display name, not the raw subtype, because
Friendly() splits camelCase and the player reads it in [Stock] and loadout
menus). Plus Reactor, SuperMagnet, TokamakBlanket as outputs and Concrete,
Cryocooler, ArmoredPlate as ingredients. Verified that every ingredient across
all 35 recipes resolves to an ItemDef.

The blueprint named "Superconducting Electromagnet" yields an ITEM called
"Superconducting Magnet", which the knowledge catalog calls SuperMagnet - three
different names for one thing, so do not "correct" any of them.

SUBTYPES: Reactor, Concrete, Cryocooler and ArmoredPlate are dump-confirmed.
SuperMagnet and TokamakBlanket are NOT - neither exists on the base yet, so
craft one and run the Item Identity Dump before setting a nonzero target.

INGREDIENT CEILINGS worth knowing before setting targets - these are not
manufacturable by IOPM and were near-empty on the live base:
  AluminumIngot ~18   (3 per ArmorGlass - refining is out of scope)
  Cryocooler    ~30   (1 per SuperMagnet, and it has no recipe yet)
  LaserEmitter   ~2   (1 per ElectronMatrix, and it has no recipe yet)
Targets on the items that consume these will report RawShortage rather than
produce, which is honest and fail-safe.

## 2.4.15
VALIDATED IN-GAME. The 2.4.13 DockDiscover rework delivered: PeakInstructions
36,056 -> 10,297 with the same 9 connected constructs and 116 unload sources -
3.5x, from 72% of the ceiling to 21%. Cost should now stay roughly flat as
ships come and go, since it no longer multiplies by construct count.

New recipes confirmed producing: Superconductor 40 -> 330 with 715 queued, and
the new ingredient rows (TantalumIngot, CobaltIngot, SilverIngot) tracked and
satisfied. Organization LastAttempt=none renders correctly on an idle cycle.

COST NOTE for whoever sets these targets: Superconductor is expensive in gold -
15 GoldWire (9 GoldIngot) each - so a 1,000 target commits ~15,000 GoldWire and
starves every other gold consumer until it drains. Observed live: 715 queued
Superconductors produced exactly 10,725 GoldWire of support demand, which took
GoldWire to 0 and blocked AdvancedComputer (507) and GravityGenerator (19).
Correct behaviour, but there is no priority mechanism beyond target values.

Tag vocabulary and PB screen, driven by real operational feedback.

1. DOCKING-T FIX. A [No Sorting] tag on a LOCAL connector used to suppress that
connector's OWN inventory as well, because [No Sorting] sits in IGNORE_TAGS.
Local connector inventory is now gated on LOCK_TAGS instead, so [No Sorting]
there means only "do not pull from whatever docks here". That enables the
intended pattern: a docking T with one tagged and one plain connector, so the
pilot chooses whether the ship gets unloaded by where it parks - while transit
cargo dropped in either connector is still cleaned into the warehouse.
[No Sorting] keeps its original meaning on local CARGO CONTAINERS.

2. [Ignore] is now accepted anywhere [IOPM-Ignore] is (IGNORE_TAGS, EXCL_TAGS,
LOCK_TAGS). Note "[IOPM-Ignore]" does not contain "[Ignore]" as a substring, so
both strings must be listed.

3. The stock DISPLAY tag moved off the word "Stock": [IOPM-Inventory] or the
short [Inventory]. "[Stock]" alone means a LOADOUT CONTAINER, and having the
display share that word was needless confusion. [IOPM-Stock] is still accepted
so existing panels keep working - it never actually collided, since
"[IOPM-Stock]" does not contain "[Stock]".

4. The PB's own screen gets its own COMPACT panel (BuildPbText) instead of the
wall-panel text, which just truncated mid-line on a surface that small. One
short line per fact: version, state, sort/prod flags, stock ready/total, dock
and loadout counts, cycle time, peak instructions, warning count. It is written
every cycle, never conditionally - see the 2.4.14 note on why.

## 2.4.14
The PB's own surface is now ALWAYS a status target, not merely a fallback.

BUG: Discover added Me.GetSurface(0) only when no [StatusScreen] block was
found. So on a base that started without one, the fallback wrote the PB screen,
and the moment a real [StatusScreen] was tagged the PB surface stopped being
written - and sat frozen on that last render FOREVER, including the now-false
"WARNING: no [StatusScreen] block found" line. Caught in-game from a photo of a
PB screen reporting Ores 8 / 19% while the live base reported 17 / 100%.

Nothing in the game clears a text surface for you: if a script stops writing a
surface, the last frame stays on it indefinitely. Never write a surface
conditionally unless something else is guaranteed to overwrite it.

The warning still fires when no tagged screen exists, but can no longer go
stale because the PB surface is refreshed every cycle either way.

## 2.4.13
PERFORMANCE ONLY, no behaviour change. DockDiscover walked every block once per
docked construct - O(constructs x blocks) - and `all` is the WHOLE
GridTerminalSystem, every block on the base and on every docked grid. On a live
multiplayer base with 9 connected constructs that put PeakInstructions at
36,056 of 50,000, and it grew with both base size and other players' traffic.

Now ONE pass, with construct membership cached per GRID by EntityId.
IsSameConstructAs is a grid-level property, so every block on a grid resolves
identically - answering it once per grid instead of once per block per construct
gives O(blocks + grids x constructs). Roughly 9 x 1000 construct comparisons
became ~20 x 9.

Deliberately NOT done by slicing constructs across ticks: that would have
deferred servicing and made the fix visible in behaviour. This keeps every
construct scanned every cycle.

CONTEXT worth keeping: this base uses CONNECTORS to split itself into subgrids
deliberately, to avoid one sprawling grid lagging the server. So the "docked
constructs" are mostly the base's own extensions and mine network, not visiting
ships - which is why [No GOAT] tagging was NOT an acceptable mitigation and the
cost had to be fixed structurally. On a multiplayer server the docked count is
not under our control at all.

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

SUBTYPES CONFIRMED IN-GAME after deploy: QuantumComputer credited Stock=564,
GravityGenerator 31, Thrust 3,427, Superconductor 40 - so Component/<Name> was
right for all of them. Still unverified: ElectronMatrix, FSSolarCell, ArmorGlass
(none exists on the base yet).

RECURSIVE MANUAL-QUEUE SUPPORT VALIDATED on live data by accident. A manual
queue of 1,096 Superconductors was invisible before 2.4.12 taught IOPM the
recipe; once visible, the planner derived every layer exactly:
    GoldWire  1,096 x 15  = 16,440   reported 16,440
    Rubber    1,096 x 3   =  3,288   reported  3,288
    GoldIngot 19,821 x 0.6 = 11,892.6 reported 11,892.6
That drained GoldWire to 1 and blocked AdvancedComputer - correct behaviour
supporting a queue IOPM may never cancel, not a runaway.

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
