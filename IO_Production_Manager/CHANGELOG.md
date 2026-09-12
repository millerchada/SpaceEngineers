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

## 2.4.36 — closure audit; the "complete catalog" claim was wrong

Artifact 86,732; headroom 13,268. Managed recipes 45 -> 46.

### The flawed definition

v2.4.35 reported "45 stock-configurable products / 45 recipes / 0 recipe-pending"
and called the catalog complete. THAT COUNT ONLY PROVED EVERY STOCK ROOT HAD A
RECIPE. It said nothing about the dependencies beneath those roots, and a root
whose ingredient has no recipe is still unbuildable - the planner reports
`RawShortage` on the ingredient and the root sits `Blocked` forever.

Canvas was the proof: shipped with a recipe in v2.4.35, and unbuildable on
arrival, because `SyntheticFabric` had an ItemDef and no recipe. Gunpowder is a
second, independent instance that the same count also missed.

### tests/audit_closure.py

Walks every ingredient of every recipe transitively from the stock roots and
classifies each LEAF - an ingredient with no active recipe:

    TERMINAL_RAW                 intentionally outside production control
    MANUFACTURED_MISSING_RECIPE  craftable, recipe not implemented - a real hole
    UNKNOWN                      insufficient evidence; resolve by observation

HAVING AN ItemDef DOES NOT MAKE SOMETHING TERMINAL, and that is the whole point.
Gunpowder carries an INGOT TypeId and would pass any "is it an ingot, therefore
refinery output" heuristic - yet it is crafted in a Munitions Factory. TypeId
describes where an item is SORTED, not whether it can be MADE. Classification
therefore reads positive evidence and falls back to UNKNOWN rather than guessing.

### Two faults in the audit itself, both found by disbelieving its output

Both made it UNDER-report, which is the dangerous direction for a completeness
check:

1. `AddBlueprintGroup` keys through `Canon()` at runtime, so `Fabric=Fabric` is
   stored under the alias `SyntheticFabric`. The audit parsed the raw key and so
   found no blueprint.
2. `_aliases` is populated from TWO places - `AddAliasGroup`, and `AddItem`
   registering each ItemDef's bare SubtypeId. The audit modelled only the first,
   so nothing connected the name `Fabric` to the alias `SyntheticFabric`.

With both fixed, SyntheticFabric reclassified from UNKNOWN to
MANUFACTURED_MISSING_RECIPE - which is what it always was.

### Closure before and after

    v2.4.35   18 leaves   16 TERMINAL_RAW   2 MISSING (Gunpowder, SyntheticFabric)   0 UNKNOWN
    v2.4.36   17 leaves   16 TERMINAL_RAW   1 MISSING (Gunpowder)                    0 UNKNOWN

Added: `SyntheticFabric | 1 | Assembler | Plastic:2`. Machine token unverified -
a PREFERENCE that orders already-eligible machines, with `CanUseBlueprint`
deciding eligibility. The Auto Loom that makes Canvas is a plausible producer of
the fabric too, but plausible is not observed, so it is not named.

### STILL OPEN - the catalog is NOT complete

    Gunpowder   MyObjectBuilder_Ingot/Magnesium   referenced by Explosives
                crafted by Munitions Factory (Large)   MANUFACTURED_MISSING_RECIPE

The machine is known; the INGREDIENT LIST has never been captured. Until it is,
`Explosives` is queueable in principle and blocked in practice. This is the one
remaining hole and it is recorded here rather than rounded away.

### Gate

Both audits now run in `tests/run_release_gate.py`. Catalog integrity is FATAL.
Closure is REPORTED BUT NOT FATAL: a known, documented dependency gap should not
stop unrelated fixes from shipping, and the affected root degrades to `Blocked`
rather than misbehaving. Surfacing it on every build is the point.

## 2.4.35 — the recipe-pending set is empty

Artifact 86,691; headroom 13,309. Managed recipes 42 -> 45.
Stock-configurable products 45, unchanged. Recipe-pending 3 -> **0**.

    Concrete   | 1 | Assembler                           | Gravel:25
    Explosives | 1 | Assembler                           | IronIngot:1, Gunpowder:4
    Canvas     | 1 | Auto Loom;Mod Compatibility Assembler| SyntheticFabric:10

Canvas is the game's Parachute recipe, and its machine token is the ONLY one in
the whole catalog backed by direct observation of the producing blocks rather
than inference - so it names both. Output 1 for all three: nothing in the repo
or the tests ever established a non-unity yield, and inventory stack size is not
evidence of yield.

### The dependency identities

    Gravel          -> MyObjectBuilder_Ingot/Stone
    Gunpowder       -> MyObjectBuilder_Ingot/Magnesium
    SyntheticFabric -> MyObjectBuilder_Component/Fabric

GUNPOWDER IS WORTH REMEMBERING. Industrial Overhaul reuses the VANILLA Magnesium
subtype for the item the UI calls Gunpowder. That is why searching a dump for
"Gunpowder" returned nothing while the ingot warehouse visibly held a large
stack - the dump reported `MyObjectBuilder_Ingot/Magnesium=196,886`. An item's
display name, its alias and its SubtypeId are three different things, and this
is the first case in the project where the mod's own UI name and the engine
subtype disagree outright.

All three are recipe INPUTS, not products, so they are declared
`stockConfigurable=false` and `[Stock]` stays at 45. The ingot pair rides the
existing ingot group, which is already false; SyntheticFabric needs its own call
because the component group is `true` and would have pushed `[Stock]` to 46.

ONE ALIAS PER IDENTITY. `Magnesium` already existed as a LOADOUT alias from
v2.4.26. Rather than leave two independent names for one physical identity, the
bare `Magnesium=Magnesium` loadout entry was removed: `AddItem` registers the
bare SubtypeId automatically, so a loadout line `Magnesium` still resolves
through `Canon -> Gunpowder -> MyObjectBuilder_Ingot/Magnesium`. `_typeAlias`
therefore maps the identity to exactly one alias and `ScanQueues` / `_onHand`
attribution stays deterministic.

ORDERING DEPENDENCY, easy to break later: the `SyntheticFabric=Fabric` ItemDef
must be declared BEFORE `AddBlueprintGroup`. The knowledge table's existing
`Fabric=Fabric` entry is keyed through `Canon`, so with the ItemDef in place
first the blueprint re-keys itself onto `SyntheticFabric` - which is what lets
`BlueprintReverseMap` attribute a MANUALLY queued Fabric. Moving that call after
the blueprint groups would break it silently. No new blueprint id was added.

### tests/audit_catalog.py

New. Checks what no compiler can see: every ingredient resolves, every recipe
output is an ItemDef, no alias declared twice, and - the one that matters here -
**raw physical identity -> alias is ONE-TO-ONE**, because two aliases for one
`TypeId/SubtypeId` would make `_onHand` crediting and `ScanQueues` attribution
depend on declaration order.

It caught something on its first run, though not a defect: seven loadout aliases
pointing at identities ItemDefs already own. Sharpening the check separated two
different things -

  - `Detector` and `BulletproofGlass` are BARE SUBTYPES of identities ItemDefs
    own (SensorCluster, Glass) and were registered as aliases twice over, once
    automatically by `AddItem` and once explicitly in `AddAliasGroup`. Dead
    weight, removed, same reasoning as Magnesium.
  - `RadioCommComponent`, `ReactorComponent`, `ThrustComponent`,
    `GravityGenComponent`, `DetectorComponent` are ALTERNATE SPELLINGS that
    cannot be derived from a subtype. They earn their place and are kept.

Neither kind ever affected attribution - `AddLoadAlias` does not populate
`_typeAlias`. The first version of the check conflated "a second name usable in
a loadout" with "a second alias for attribution", which would have left a
permanently red check, and a check that is always red teaches people to ignore
it.

### Full gate

    catalog integrity (6 invariants)              PASS
    stock-configurable 45 / recipes 45 / pending 0
    canonical-stock tests (52)                    PASS
    compile negative controls (CS0136, whitelist) PASS
    positive compile                              PASS
    minifier round-trip (inverse == original)     PASS
    artifact compiles                             PASS
    literal + structure integrity                 PASS
    single AddQueueItem site, no Clear/Remove/Move PASS
    86,691 / 100,000, headroom 13,309

## 2.4.34

Source 139,630 -> 143,171 (artifact 86,505; 13,495 headroom).
Managed recipe set 38 -> 42. `[Stock]` unchanged at 45.

Four of the seven recipe-pending products promoted on live IO 1.7.7 evidence:

    Capacitor          | 1 | Assembler | Rubber:1, AluminumIngot:1, Ceramic:1
    Girder             | 1 | Assembler | IronIngot:2
    RadioCommunication | 1 | Assembler | SmallSteelTube:6, CopperWire:12
    SolarCell          | 1 | Assembler | Glass:1, CopperWire:1, SiliconWafer:5, IronIngot:2

Nothing new was added to make these queueable. Every blueprint id was already in
the table (`Capacitor`, `POGirderComponent`, `PORadioCommunicationComponent`,
`POSolarCell`) and no alternate alias was created. `SolarCell` uses the existing
`SiliconWafer` alias for `MyObjectBuilder_Ingot/Silicon` rather than introducing
a second name for the same item. Verified: every ingredient across all 42
recipes resolves to an existing ItemDef or alias.

YIELD 1 for all four - ordinary single-result components with no evidence of a
multi-output recipe. `Lightbulb|10` is not a precedent to generalise from; it is
a measured non-unity yield (30 Lightbulbs consumed exactly 3 Glass, UAT-proven).

MACHINE TOKEN: the producing machine was not observed for any of the four, so
the generic `Assembler` token is used. That is a PREFERENCE, not a gate - it
orders machines already eligible, and eligibility comes from
`CanUseBlueprint()`. A machine the token does not name still receives the job
when it is the only one that can build it, since `MachineRank` falls back to
5000 for everything unmatched. A wrong preference costs ordering, never
correctness.

### THREE REMAIN PENDING, and not for want of recipe knowledge

    Concrete    Gravel:25
    Explosives  IronIngot:1, Gunpowder:4
    Canvas      SyntheticFabric:10

The RECIPES are known. What is missing is the physical identity of `Gravel`,
`Gunpowder` and `SyntheticFabric` - no `TypeId/SubtypeId` for any of them exists
anywhere in this repo. The changelog already records "Gravel unknown" from the
deferred ejector design, and greps for Gunpowder and SyntheticFabric return
nothing at all.

A guessed identity fails SILENTLY, which is why these were not added anyway.
A wrong INGREDIENT subtype is less destructive than the wrong OUTPUT subtype
that cost ~129k surplus BulletproofGlass in v2.3.4 - it under-produces rather
than over-produces - but it still leaves the parent permanently `Blocked` with
nothing explaining why, and no warning fires because routing keys on TypeId.

RESOLUTION IS ONE TOOL RUN, NOT A SCREENSHOT: run `IO_Item_Identity_Dump` on a
scratch PB. It prints the exact `TypeId/SubtypeId` for every item physically
present. With those three lines the recipes are a one-line change each, exactly
like the four above. `SiliconWafer` resolving cleanly to `Ingot/Silicon` is the
worked example of why this matters - it was already known, so `SolarCell` needed
no new identity at all.

### Also in this release

`build_pb.py` now pins UTF-8 for reads and writes. Sources contain non-ASCII
(em dashes in comments) and were written as UTF-8, but the locale default here
is cp1252, so an unpinned read decoded them differently from `check_pb.py`,
which always reads UTF-8. Harmless today only because comments never reach the
artifact - pinned so it stays that way rather than depending on it. Found while
cross-checking literal integrity by hand; the mismatch first showed up as a
false "literals differ" result from the ad-hoc check, not from the build.

### Release gate

    canonical-stock tests (52)                    PASS
    compile negative controls (CS0136, whitelist) PASS
    corrected-source positive compile             PASS
    minifier round-trip (inverse == original)     PASS
    transformed artifact compiles                 PASS
    literal integrity 639 -> 639 identical        PASS
    structure counts identical                    PASS
    single AddQueueItem call site                 PASS
    no ClearQueue / Remove / Move                 PASS
    BlueprintReverseMap still iterates ALL ItemDefs so manual queues are
      recognised, including for the three still-pending products  PASS
    size 86,505 / 100,000, headroom 13,495        PASS

## Size-reclamation pass (no version bump - behaviour is byte-identical)

    artifact 94,878 -> 86,287     headroom 5,122 -> 13,713

Target was <= 90,000, preferably <= 88,000. Reached without touching one line of
planner, sorting, docking or queue logic - no expression rewritten, no branch
merged, nothing inlined, no diagnostic string shortened.

### Where the characters actually were

Measured rather than guessed:

    identifiers   44,648 chars (47.1%)
    string data   14,097 chars (14.9%)
    newlines       2,226 chars

Most identifier cost is framework names (Dictionary, List, IMyInventory) that
cannot be renamed. What we own - private fields and our own method names - was
worth ~10,000.

### minify_names.py

Renames only our own private fields and method names in the ARTIFACT. Readable
names stay in the Git source, which is the only place anyone reads them.

Excluded, each for a reason:
  - anything ever reached through a dot. Our method `Get` and MyIni's `ini.Get`
    share a name; a bare-token rename would rewrite the API call.
  - anything inside a string literal. Item aliases, [Stock] keys, GOAT tokens
    and diagnostics text are DATA - rewriting one changes behaviour.
  - locals, which are the riskiest (shadowing can rebind rather than error) and
    the least valuable.

Verified three ways: the INVERSE mapping must reproduce the original byte for
byte (proving the transform is a bijection over tokens that touched nothing
else); build_pb re-verifies all 635 string literals and the code-structure
counts against the ORIGINAL source; and check_pb compiles the artifact.

NEWLINES WERE DELIBERATELY KEPT. Removing them would save 2,226 more characters
but destroy in-game line numbers for runtime errors, and renaming alone already
cleared the target with room to spare. Compile errors are caught locally now,
but runtime ones still surface in the game.

THE COMPILER CAUGHT THE PASS GETTING IT WRONG, which is the whole argument for
the gate: `QueueLoad` and `ServiceLoadouts` are each BOTH one of our method
names AND a field on a class. The dotted uses were correctly skipped but the
bare declarations were renamed, desyncing them (CS1061/CS1660). Fixed by
excluding any name that is ever dotted anywhere in the file.

### The knowledge-only blueprint table: proven NOT removable

The obvious saving was the 691-character table commented "KNOWLEDGE ONLY - no
Recipe and no ItemDef, so never planned, queued or put in [Stock]". THE COMMENT
IS FALSE, and deleting on the strength of it would have broken production.
Audit of all 25 entries:

    13  live via TryGetBlueprint() in EnsureFeasible/ApplyPlan - they gained
        recipes in v2.4.16, v2.4.17 and v2.4.30 and the label was never updated
     7  live via BlueprintReverseMap(), which iterates _items - ALL ItemDefs,
        not just recipes - so ScanQueues can attribute a MANUALLY queued job to
        an alias. Deleting these would silently stop IOPM recognising a
        hand-queued Girder, Capacitor, Concrete, Explosives, Canvas,
        RadioCommunication or SolarCell.
     5  genuinely unreachable: AcidPowerCell, AlkalinePowerCell, Asphalt,
        CompositeArmor, Fabric - worth 123 characters.

KEPT, including the five. 123 characters against 13,713 headroom is not worth a
change, and they are the blueprint ids a future promotion needs - exactly as
ArmoredPlate's was in v2.4.30, when it was still sitting in this "unused" table.
The comment has been replaced with the audit.

This is why the instruction was to prove before removing. The label was three
versions stale and pointed at the opposite of the truth.

### Release gate

`python tests/run_release_gate.py <source.cs>` is now required before any paste,
and `build_pb.py` refuses to emit an artifact that does not compile - deleting
it rather than leaving a broken file on disk. The gate runs two NEGATIVE
CONTROLS first, because a checker that has silently stopped working is worse
than none:

    pre-fix v2.4.33 (shadowed local) -> CS0136 reported   PASS
    v2.4.3 Comparison<T>             -> whitelist screen  PASS
    script under release             -> compiles clean    PASS
    build_pb transform verification                       PASS

## 2.4.33 — in-game compile fix, and a local compile checker

The first paste of v2.4.33 failed to compile in game:

    Program(2194,157): Error: A local or parameter named 'raw' cannot be
    declared in this scope because that name is used in an enclosing local
    scope to define a local or parameter

Introduced in v2.4.32, in `CanonicalizeStock`: a loop-local `string raw` for the
per-key value and a method-level `string[] raw = text.Split()` further down the
same method. C# refuses a method-scope local that collides with an earlier
block-scope local EVEN WHEN the method-scope declaration appears later in the
text. Renamed to `rawVal`. Fixed in place rather than as a new version, since
v2.4.33 never compiled and so was never an immutable released version - the same
precedent as the `Comparison<CI>` fix in v2.4.3.

### check_pb.py — the game should not be the first compiler to see the script

Two of this project's compile errors were found only by pasting into the game.
`build_pb.py` verifies the comment-stripping TRANSFORM, not the C#: passing it
never meant the script compiles, and saying "build verified" about it invited
exactly that misreading.

`check_pb.py` wraps a script in the `MyGridProgram` shape and compiles it with
`csc` against `se_stubs.cs`, hand-written shapes for the PB API (12 types were
enough to start; a few more were added as the compiler named them).

Proven against both historical failures, as fixtures:

    bug_cs0136    -> CS0136 reported at the exact source line   (v2.4.33)
    bug_whitelist -> Comparison<T> reported by the name screen  (v2.4.3)

and all three repo scripts compile clean.

THREE FAULTS IN THE CHECKER ITSELF, found while building it, each of which would
have made it worthless or misleading:

1. `-langversion:6` is invalid for csc 4.8, so the compile aborted with CS1617
   before compiling anything - and the error parser only matched errors carrying
   a `file(line,col)` prefix, so the run printed **OK**. A checker that reports
   success when it did not compile is worse than no checker. Any unprefixed
   error line, and any non-zero exit with nothing parsed, is now reported as
   INCONCLUSIVE with a distinct exit code.
2. The wrapper class was named `__PBScript`. The script declares
   `public Program()`, which is only a constructor if the class is named
   `Program`; otherwise it is a method with no return type, so every script
   checked reported a phantom CS1520.
3. `MyFixedPoint` had no arithmetic operators and `ContentType` was typed as a
   string, producing a wall of phantom CS0019/CS0103 that buried the one real
   error.

WHAT IT STILL DOES NOT CATCH: behaviour, and any API shape `se_stubs.cs` gets
wrong. Unknown-type and unknown-member errors are listed separately as probable
stub gaps - add the member to the stubs, do not change the script.

## 2.4.33

Source 137,328 -> 139,341 (artifact 94,872; 5,128 headroom). Recipes 38 -> 38.

v2.4.32 protected the TEXT but not the PARSE. `CanonicalizeStock` kept both
conflicting lines and warned, while `LoadConfig` still did:

    string alias = Canon(rawName);
    ...
    c.StockTargets[alias] = value;   // last GetKeys() iteration wins

So `Computer=500` alongside `BasicComputer=2000` still collapsed into ONE
active quota, chosen by dictionary iteration order - a number the player never
picked, driving real production, while the diagnostics said the conflict was
unresolved. The warning was true and the behaviour was not.

### Fail-closed

A colliding alias now gets NO entry in `StockTargets` at all. It is not a
planner root, generates no demand, is not counted in `StockItems`, and does not
appear on the LCD. A conflicting quota is not a quota. All source keys stay in
the file verbatim and the conflict is reported:

    [Stock] alias collision on BasicComputer: BasicComputer, Computer all mean
    BasicComputer. NO quota is applied for BasicComputer until this is
    resolved - all keys are kept verbatim and none renamed. Delete whichever
    you do not want.

EQUAL VALUES COLLIDE IDENTICALLY. `Computer=500` with `BasicComputer=500` is
refused too. The rule is about the conflict, not the numbers: accepting equal
values would make the same configuration valid or invalid depending on a value
the player may be about to edit, and would leave a latent trap that only fires
later. Explicitly tested.

Seeding is unaffected and was already correct - `EnsureStockAliasesPresent`
tests presence against the `[Stock]` KEYS, not against `StockTargets`, so a
collided alias still counts as present and no extra key is stacked on top of
the conflict. Also explicitly tested.

### One grouping, two consumers

`GroupStockKeys` and `StockCollisionMessage` are now shared by `LoadConfig` and
`DetectStockCollisions`. Two independent implementations could have disagreed
about what counts as a collision - one failing closed while the other renamed -
which is the worst of both. Both are pure functions of the key SET: groups and
their contents are sorted, so no result depends on iteration order anywhere.

Detection stays in BOTH places deliberately, rather than being computed once in
`LoadConfig` and reused. The text pass must judge the ini it is ABOUT TO
REWRITE: config applies only at Idle, so a collision created by a mid-cycle
hand edit would not yet be in `_cfg`, and a text pass trusting a stale set
would rename the new key and collapse the pair - destroying the value in the
same cycle it was typed.

### Tests

`tests_canonicalize_stock.py` gains a model of `LoadConfig`'s `[Stock]` loop
and 20 new checks. Full suite now 52, all passing:

    DIFFERENT values (Computer=500 vs BasicComputer=1000)
      no active quota, reported once, unrelated quotas unaffected,
      both source keys verbatim, text pass idempotent, no extra key seeded
    EQUAL values (Computer=1000 vs BasicComputer=1000)
      identical outcome - refused, not quietly accepted
    regression guard
      clean config still yields 45 active quotas, no collision reported,
      BasicComputer applies normally at 1000 when it is alone
    iteration order
      same targets and same errors with the key list reversed

The reversed-key-order check is the direct regression test for the original
defect: under the old code it would have produced a different quota.

## 2.4.32

Source 133,535 -> 137,328 (artifact 94,256; **5,744 headroom** - see the note at
the end). Recipes 38 -> 38, ItemDefs unchanged, seeding unchanged.

`[Stock]` keys are now RE-SPELLED to their canonical alias, not merely sorted as
if they were. `Computer=500` is rewritten as `BasicComputer=500`, carrying the
value across as raw text. v2.4.31 sorted an alias key under its canonical name
but left the spelling alone, which made the section read as though it were
mis-sorted; the invariant is now actually true rather than approximately true.

### Collisions are detected, never resolved

Two keys can mean the same item - `Computer=500` alongside `BasicComputer=1000`.
Re-spelling both would collapse them and silently destroy one player-entered
number. So a COLLIDING GROUP IS NEVER RENAMED: every key in it is emitted
verbatim, and the conflict is reported through `[IOPM.ConfigError.*]`:

    [Stock] alias collision on BasicComputer: BasicComputer, Computer all mean
    BasicComputer. All kept verbatim and NONE renamed - delete whichever you do
    not want. Until then the config load order decides which value wins.

The player decides which to keep, because only the player knows which number
they meant. Seeding cannot create a collision - it adds a key only when nothing
already canonicalizes to it - so this fires solely on hand-edited config.

`DetectStockCollisions` runs immediately after seeding and before the `IOPM.*`
sections are written, so a collision reaches the diagnostics in the same cycle
it is found.

### A message that churned, caught by the test

The first implementation named the colliding keys in DISCOVERY order. Because
this same pass reorders the section, the message read "Computer and
BasicComputer" on the cycle that found the collision and "BasicComputer and
Computer" on the next - the `[Stock]` section was byte-stable, but the
diagnostic wobbled for one extra cycle. `tests_canonicalize_stock.py` caught it
("collision still reported next cycle" FAILED). Fixed by gathering full groups
and sorting both the group names and the names within each group, making the
message a pure function of the key SET rather than of its order.

### Verified

`tests_canonicalize_stock.py` is committed alongside the source, with
`live_custom_data.txt` as its fixture, and ports `DetectStockCollisions` and
`CanonicalizeStock` line for line. 32 checks, all passing:

    LIVE CONFIG (45 canonical keys)
      idempotent over 3 passes, no collisions, 45 -> 45, no key lost,
      no value changed, ZERO VISIBLE DIFF against v2.4.31 ordering,
      [General] [Sorting] [Production] [Display] untouched
    RE-SPELLING
      Computer -> BasicComputer, value 500 carried across, sorted correctly,
      idempotent, no collision reported for a single key
    COLLISION
      both keys survive, neither renamed, nothing lost, reported once,
      message identical on the following cycle, idempotent
    UNKNOWN KEYS / FIDELITY / LINE ENDINGS
      unknown keys survive un-renamed and interleave alphabetically,
      0.5 and 1e3 preserved verbatim, CRLF stays CRLF, LF stays LF
    STRUCTURAL
      sparse one-key section seeds to 45 and is idempotent,
      [Stock] as the final section is idempotent

As predicted, the change is a NO-OP on the current base: the live 45 keys are
already canonical, so today it only makes the invariant correct for future
hand edits.

### PB headroom is trending down and needs watching

    2.4.29  8,669      2.4.31  7,191
    2.4.30  8,610      2.4.32  5,744

Roughly 2,900 characters consumed across two versions. The artifact is 94,256
of the PB's 100,000. This is not urgent, but the next few features cannot all
be additive. When it gets tight the cheapest real saving is the knowledge-only
blueprint table, which costs ~1,100 characters and earns its place only when a
pending recipe is promoted.

## 2.4.31

Source 129,718 -> 133,535 (artifact 92,809; 7,191 headroom). Recipes 38 -> 38,
ItemDefs unchanged, seeding unchanged.

`[Stock]` is now rewritten alphabetically by canonical alias.

### Why it is spliced as TEXT and not done through MyIni

This is the constraint that shapes the whole implementation. MyIni CANNOT
reorder or remove a key:

- `Set()` appends a new key AFTER the existing ones, in original parse order.
- `DeleteSection()` followed by `Set()` on the same section overlays the
  ORIGINALLY PARSED section and resurrects every old key - proven in-game in
  v2.4.4-v2.4.5 and documented above.

So ordering has to be imposed on the serialized string afterwards.
`CanonicalizeStock` runs on `ini.ToString()` and replaces only the lines
between the `[Stock]` header and the next section header. `DeleteSection` is
still used for `IOPM.*` only, where deleting WITHOUT re-creating does work.

### What is preserved, exactly

- **Values, as RAW TEXT.** `500` stays `500`, `0.5` stays `0.5`, `1e3` stays
  `1e3`. The value is never parsed to a double and re-formatted, so no rounding
  or culture formatting can touch it.
- **Every existing key, including unrecognised ones.** An unknown key sorts
  into place under its own literal name and is never dropped. A player may be
  tracking something IOPM knows nothing about; deleting it would be data loss.
- **Key spelling.** A key written as an alias (`Computer=500`) keeps that
  spelling and is merely SORTED as its canonical alias (BasicComputer).
  Renaming would rewrite player input, and `Canon()` already resolves it at
  load time, so there is nothing to gain. CONSEQUENCE, worth knowing: an alias
  key appears next to its canonical sibling rather than under its own letter -
  `BasicComputer, Computer, Canvas` reads oddly but is correct. Seeded configs
  are all canonical, so this only shows up if a player types an alias by hand.
- **Every other section**, byte for byte.

### Idempotence

The output is a pure function of the key/value set, so cycle two produces a
byte-identical string, `finalText == _lastWrittenCustomData` is true, and the
write is skipped entirely. Line endings are normalised to whatever the incoming
text already uses, so the pass cannot oscillate between LF and CRLF forms.

No config-change loop: `_lastCustomDataSeen = Me.CustomData` (a RE-READ, not
`finalText`) already ran after every write and now covers the spliced section
too, so `Main()` sees no change and `_cfgDirty` stays false.

### Verified before shipping

The C# was ported line for line to Python and run against the real live Custom
Data plus adversarial inputs:

    live data, 3 passes ......... byte-identical from pass 1
    keys 45 -> 45, none lost, no value changed, none added
    ordering matches sort-by-canonical-alias
    [General] [Sorting] [Production] [Display] .... untouched
    unknown keys (ZzUserThing, AaaCustom, WeirdValue) .. survive, interleaved
    alias key Computer=500 ...... spelling and value kept, sorts as BasicComputer
    BasicComputer=1000 AND Computer=500 present ... BOTH survive, neither deleted
    raw values 0.5 and 1e3 ...... preserved verbatim
    CRLF input .................. every newline stays CRLF, idempotent
    LF input .................... stays LF
    sparse [Stock] with one key . seeded to 45, existing value kept, idempotent
    [Stock] as the LAST section . idempotent

Two assertions in the first test run reported failures that were faults in the
TEST, not the code: a "duplicate BasicComputer" check whose adversarial input
already contained both keys (both must survive, and did), and a malformed CRLF
expression. Both were re-checked properly and pass.

### Unchanged

One AddQueueItem site, 38 recipes, one guarded `ini.Set("Stock", ...)`,
`DeleteSection` still `IOPM.*` only. No planner, recipe, sorting, docking or
queue behaviour was touched.

## 2.4.30

Source 128,695 -> 129,718 (artifact 91,390; 8,610 headroom). Managed recipe set
37 -> 38.

    ArmoredPlate | 1 | Plate Stamp | SteelPlate:1, TitaniumPlate:1

LIVE-VALIDATED against Industrial Overhaul v1.7.7. The first promotion out of
the eight identity-known / recipe-pending products catalogued across
v2.4.26-v2.4.28. Seven remain pending: Canvas, Capacitor, Concrete, Explosives,
Girder, RadioCommunication, SolarCell.

Nothing else moved. The functional diff is two lines - the version constant and
the recipe. The physical identity `MyObjectBuilder_Component/ArmoredPlate` and
the existing `[Stock]` value are untouched; only the recipe is new. Nothing had
to be added to make it queueable: the blueprint id `ArmoredPlate` has been in
the knowledge-only table since v2.4.25, and the machine token `Plate Stamp`
already backs the SteelPlate and AluminumPlate recipes.

This is the v2.4.27 principle running in the intended direction for the first
time. Identity came first and stood on its own; the recipe arrived later and
independently, and the `[Stock]` entry never depended on it. `ArmoredPlate`
should now move from `RawShortage` to `Queued` or `Blocked` depending on
ingredient availability, with no configuration change on the player's side.

### Downstream demand, so a large plate order is not mistaken for a runaway

`ArmoredPlate` is now a PARENT of both plates, so recursive expansion
propagates an ArmoredPlate target into plate demand and on into ingots. At the
current `ArmoredPlate=20000` target the full chain is:

    20,000 ArmoredPlate
      -> 20,000 SteelPlate     -> 400,000 IronIngot     (20 per plate)
      -> 20,000 TitaniumPlate  -> 240,000 TitaniumIngot (12 per plate)

Iron is not a concern at ~2.3M on hand. TITANIUM IS THE BINDING CONSTRAINT and
should be expected to be: TitaniumPlate stock was 622 at last reading. IOPM will
report the honest partial - `Feasible` limited by ingots actually present,
`Blocked` for the rest with `BlockedBy=TitaniumPlate` or `TitaniumIngot` - and
will queue only what it can support, one cycle at a time. That is correct
behaviour, not a stall, and it is the same shape as the Electromagnet ->
CopperWire chain that has been running all along.

## 2.4.29 — PROMOTED, live baseline

Confirmed on the production server. v2.4.28 archived; v2.4.29 is the only live
source in the folder.

The [Stock]/recipe conflation is closed in all three places: seeding (2.4.27),
planning and counting (already correct), and LCD rendering (2.4.29). Audited
against v2.4.29 - every remaining `_recipes` reference asks a manufacturing
question, none asks whether an item belongs in stock. Recorded as an invariant
in the README.

PARKED by decision, not oversight: the stale `_stockRowCount` on the
no-screen skip path. Real, in the same family as 2.4.14 and 2.4.24, invisible
on a base that has a stock screen. To be picked up the next time there is a
reason to touch diagnostics.

## 2.4.29

Source 127,764 -> 128,695 (artifact 91,331; 8,669 headroom). Recipes 37 -> 37,
ItemDefs unchanged, seeding implementation unchanged.

`BuildStockText` iterated `_recipes`. That was the last surviving instance of
the conflation v2.4.27 set out to remove: the LCD answered "can IOPM build
this" when the question on screen is "what am I stocking".

Proven live on the production server rather than argued:

    IOPM.Production.StockItems  = 45
    IOPM.StockDisplay.Rows      = 37

The eight recipe-less products were physically counted, held `LastPlan`
records, and were still invisible on the panel.

### The fix

    - foreach (var kv in _recipes) {
    + foreach (var kv in _cfg.StockTargets) {

`_cfg.StockTargets` is the SAME dictionary `[IOPM.Production] StockItems`
counts, so `Rows` and `StockItems` are now equal BY CONSTRUCTION and cannot
drift apart again. The fix is structural, not a second list kept in sync.

A consequence worth stating: a key the player adds by hand now appears on the
panel even when IOPM cannot manufacture it. That is correct. The panel reports
what is being stocked, and a quota with no way to reach it is exactly the thing
worth seeing.

`row.Quota` also drops its redundant `ContainsKey` + indexer lookup and reads
`kv.Value` directly, which is the same value by definition.

### Unchanged

Planning, queue behaviour, recipes, sorting, docking and the StockConfigurable
seeding implementation are untouched - the functional diff is three lines, one
of them the version constant. Pagination is untouched and still driven by
`rows.Count`, so 45 rows paginate exactly as 37 did: one page when
`StockRowsPerPage` is 0 or >= the row count, otherwise ceil(total/per) pages
advancing one page per rendered cycle.

### Known, deliberately not fixed here

`RenderStockScreens` returns early when no screen carries `[IOPM-Stock]`, which
leaves `_stockRowCount` holding its last value instead of 0. That is the same
stale-diagnostic family as 2.4.14 and 2.4.24, and it violates the rule recorded
in 2.4.24 that a skipped phase owes its diagnostics a reset on the skip path.
It is invisible on the current base, which has one stock screen, and fixing it
was outside the narrow scope of this version. Worth doing in its own change.

## 2.4.28

Source 126,765 -> 127,764 (artifact 91,381; 8,619 headroom). Managed recipe set
37 -> 37, unchanged. Component ItemDefs 38 -> 44.

Six live-observed manufactured component PRODUCTS were still classified as
loadout-only identities and are now native stock-configurable ItemDefs:

    Capacitor  Explosives  Girder  RadioCommunication  SolarCell  Canvas

They are manufactured products physically observed on the production server, so
they are exactly as stock-configurable as Concrete and ArmoredPlate - the player
may want a base floor for any of them. The loadout alias table could never
confer that status, because it also carries ammo and raw materials; membership
there says only "this name resolves", never "this is a base stockpile item".

RECIPE-LESS BY DESIGN. No IO 1.7.7 recipe for any of the six is validated, so
none is in AddRecipes and none can be manufactured. This is the v2.4.27
principle applied consistently: a [Stock] entry answers "may I set a target for
this", never "can IOPM build this". A non-zero target on any of them reports
RawShortage, which correctly reads as supply-this-yourself.

### Live migration: 37 -> 45

Verified by parsing the BUILT ARTIFACT, not the source, so no comment can
pollute the measurement:

    ADDED (8, all at 0): ArmoredPlate, Canvas, Capacitor, Concrete,
                         Explosives, Girder, RadioCommunication, SolarCell
    recipes among added: 0
    ingot-TypeId items configurable: Polymer only
    duplicate aliases in the component group: none
    every other key: untouched

The functional diff against 2.4.27 is two lines - the version constant and the
component list. Audits: one AddQueueItem call site, no ClearQueue/Remove/Move,
one ini.Set("Stock") guarded by !present.Contains, 37 recipes.

### The six keep their loadout alias entries

Not removed, deliberately. ResolveLoadoutType checks ItemDefs at priority A,
before the alias table, so the ItemDef now wins and resolves to the identical
raw type - the entries are redundant but harmless. They also carry EXTRA alias
spellings that remain useful (RadioCommComponent, ReactorComponent,
ThrustComponent, GravityGenComponent, DetectorComponent), and LoadoutTemplate
de-duplicates by resolved raw type with _items first, so the seeded menu gains
no duplicate rows.

### Tooling note

A comment placed between two concatenated string literals originally contained
the quoted phrase "supply this yourself". C# ignores it and build_pb verified
all 614 literals byte-identical, but naive literal-extraction tooling - my own
verification script included - concatenated the comment text into the item list
and reported a phantom alias. The quotes were removed and a warning left in
place. Verification now reads the artifact, where comments no longer exist.

## 2.4.27

Source 122,848 -> 126,765 (artifact 91,312; 8,688 headroom). Managed recipe set
37 -> 37, unchanged.

`[Stock]` auto-population is now driven by STOCK-CONFIGURABLE IDENTITY instead
of recipe availability.

### The defect

`EnsureStockAliasesPresent` iterated `_recipes`:

    foreach (var kv in _recipes)
      if (!present.Contains(kv.Key)) ini.Set("Stock", kv.Key, 0);

So a configuration entry appeared only once IOPM had learned to BUILD the item.
That conflates two independent facts. "May the player set a target for this?"
and "can IOPM manufacture this?" are different questions, and answering the
first with the second hid Concrete and ArmoredPlate - real components, live
observed on the base in the v2.4.26 dump - from the configuration purely
because their IO 1.7.7 recipes are not validated.

### The rule

`ItemDef` gains `StockConfigurable`, an EXPLICIT designation:

- manufactured components -> stock-configurable, auto-listed at 0
- ingots -> NOT stock-configurable. They are refining output, present only so
  recipe dependency resolution can price a component in raw material.
  Auto-listing them would invite [Stock] targets for materials IOPM must not
  manage; refining is out of scope by design.
- loadout-only identities (ammo, ores, tools, food, and components carrying
  identity but no ItemDef - Capacitor, Girder, SolarCell, Explosives, Canvas,
  RadioCommunication) are excluded automatically by not being ItemDefs at all.

Effect on the live base: `[Stock]` 37 -> 39. `ArmoredPlate=0` and `Concrete=0`
added, nothing else written.

### Polymer: why the designation is per-item, not per-TypeId

Caught before shipping. Polymer is MANUFACTURED - blueprint `SyntheticPolymer`,
one of the 37 managed recipes - but IO 1.7.7 gives it an INGOT TypeId, so it
lives in the ingot ItemDef group. A TypeId-group rule alone would have silently
stopped auto-listing it. The live base would not have noticed, because the pass
never deletes and `Polymer=500` was already there, but a FRESH deployment would
have lost the entry.

`MarkStockConfigurable("Polymer")` designates it explicitly, which keeps the
designation independent of recipe availability - the whole point of this
version. A rule of "component group OR has a recipe" was rejected for exactly
that reason: it would have made recipes control existence again through the
back door.

### Non-destructive guarantees

- Only ABSENT keys are written. An existing user value is never read,
  overwritten, reordered or deleted - including a deliberate 0.
- Presence is tested through `Canon`, so a key written under an alias
  (`Computer=500`) correctly marks its canonical item (BasicComputer) present
  and does not gain a duplicate row.
- The pass runs inside `WriteDiagnostics`, which already rewrites Custom Data
  and then re-reads it into `_lastCustomDataSeen`, so added keys cannot
  self-trigger the config-change detector. The next cycle finds them present
  and writes nothing: idempotent, settles in one cycle.
- `DeleteSection` still touches `IOPM.*` only. Verified by audit: the single
  `ini.Set("Stock", ...)` in the file is guarded by `!present.Contains`.

### A [Stock] target on a recipe-less item is safe

Verified against `EnsureFeasible`. At target 0 the shortage is <= 0 and the row
reports `Satisfied`, queuing nothing. At a non-zero target it reports
`RawShortage` with `BlockedBy` set to itself, which correctly says "supply this
yourself" rather than pretending the item does not exist. No crash, no bogus
queueing, no unknown-blueprint warning, because the recipe lookup is reached
only when a shortage exists.

## 2.4.26

Source 120,051 -> 122,848 (artifact 90,807; 9,193 headroom). Managed recipe set
37 -> 37, UNCHANGED. Identity consolidation from a live Item Identity Dump.

The entire functional diff is four data lines. No planner, sorting, docking,
queue, budget, stall or phase code was touched.

### Ledger: live Item Identity Dump

- Source: live Item Identity Dump, production Industrial Overhaul 1.7.7 server
- Confidence: live observed / high
- Scope of proof: a physical observation proves the PHYSICAL ITEM IDENTITY and
  nothing else. It does not prove a blueprint id, a yield, or a preferred
  machine. Those remain separately validated facts.

#### The eight observed components absent from managed [Stock]

| Alias | MyItemType | Was it resolvable in 2.4.25? | Action | Recipe |
|---|---|---|---|---|
| ArmoredPlate | Component/ArmoredPlate | yes - ItemDef (TokamakBlanket ingredient) | none needed | PENDING |
| Capacitor | Component/Capacitor | **NO** - blueprint id only | added to loadout identity table | PENDING |
| Concrete | Component/Concrete | yes - ItemDef (Reactor ingredient) | none needed | PENDING |
| Explosives | Component/Explosives | yes - loadout alias | none needed | PENDING |
| Girder | Component/Girder | yes - loadout alias | none needed | PENDING |
| RadioCommunication | Component/RadioCommunication | yes - loadout alias | none needed | PENDING |
| SolarCell | Component/SolarCell | yes - loadout alias | none needed | PENDING |
| Canvas | Component/Canvas | yes - loadout alias | none needed | PENDING |

Only ONE of the eight was genuinely unresolvable. Capacitor had an entry in the
knowledge-only blueprint table, and A BLUEPRINT ID IS NOT AN IDENTITY - the
blueprint table is consulted when queuing a job, never when resolving a loadout
name. The other seven already resolved, two as ItemDefs and five as loadout
aliases, so re-listing them would have been dead weight.

NONE of the eight has a validated IO 1.7.7 recipe in durable project knowledge.
Checked explicitly: every "SolarCell" hit in this changelog is FSSolarCell, a
different item with a different recipe. Concrete and ArmoredPlate appear only as
INGREDIENTS of the Reactor and TokamakBlanket recipes - an ingredient amount
proves consumption, never the recipe that produces the ingredient. So all eight
are identity-known / recipe-pending, no [Stock] target was created, and the
2022-era IO quantities in the old external catalog were deliberately not
resurrected.

#### Previously inferred aliases, now live-observed

These eight were already coded exactly right. Nothing changed but the
confidence, so the implementation was not churned.

| Alias | MyItemType |
|---|---|
| AluminumPlate | Component/InteriorPlate |
| BasicComputer | Component/Computer |
| SensorCluster | Component/**Detector** |
| Glass | Component/**BulletproofGlass** |
| LargeSteelTube | Component/LargeTube |
| SmallSteelTube | Component/SmallTube |
| MedicalComponent | Component/Medical |
| LithiumPowerCell | Component/PowerCell |

SensorCluster and Glass are the significant pair. `BuildKnowledgeBase` carried a
"PENDING LIVE UAT" caveat saying only the blueprint ids PODetectorComponent and
POBulletproofGlass had been validated and that the RESULT subtypes were still
inferred. Both are now physically observed. That caveat is retired - it was the
last remaining inferred identity in the managed set, and it was the exact
failure mode that cost ~129k surplus BulletproofGlass in v2.3.4-v2.4.2.

#### Ammo identities added

    MyObjectBuilder_AmmoMagazine/InteriorTurret_Mag_50rd
    MyObjectBuilder_AmmoMagazine/MediumCalibreAmmoHE

Self-mapped, because no authoritative GOAT alias is known for either. The exact
SubtypeId resolves; no friendly name was invented. All existing ammo mappings
preserved untouched.

#### Ingot identities added

Diffed the 22 live-proven Ingot subtypes against the ItemDef group. Sixteen were
already known. Six were not:

    DepletedUranium  FuelOil  Magnesium  PrototechScrap  SpentFuel  Uranium

These went into the loadout identity table, deliberately NOT the Ingot ItemDef
group. An ItemDef is a STOCK-CAPABLE alias: it credits `_onHand` and accepts a
`[Stock]` target. None of these six is consumed by any managed recipe, so as
ItemDefs they would add stock accounting for materials IOPM must not manage.
Refining stays out of scope: no [Stock] targets, no refining recipes, no
refinery input management, no inferred processing routes.

COLLISION WARNING carried in the source: `AddLoadAlias` also registers the bare
SubtypeId, so a loadout line `Uranium` resolves to the INGOT. A SubtypeId is not
unique across TypeIds - proven live by PhysicalObject/Grain vs SeedItem/Grain in
v2.4.23 - so where an ore of the same name also exists (Uranium and Magnesium
are the likely pair) a loadout wanting the ore must spell out
`MyObjectBuilder_Ore/Uranium`. Resolution priority D already accepts that form.

#### Deliberately NOT added

The dump also observed foods, seeds, tools, bottles, datapads and weapons. None
was hard-coded. Broad categories already classify by TypeId, and the loadout
resolver has exact observed-subtype fallback, so a hard-coded identity earns its
place only when it is a managed-production item, a production dependency, a
known GOAT alias, or an exact subtype that must resolve even when not currently
observed. IOPM is not an item database.

## 2.4.25

Source 120,051 -> artifact 90,513 (saved 29,538, 24.6%; 9,487 headroom).

`[Sorting] OrganizeSkip=<categories>` exempts named warehouse categories from
slot ordering while ordering stays on everywhere else.

WHY THIS IS NOT COSMETIC. Alphabetical slot ordering is cosmetic to a reader
but not to the conveyor system: a draining sorter takes the first matching
item it finds, so ordering a container pins whichever item sorts first into
slot 0 permanently. When one item in a pool vastly outnumbers the rest, every
downstream gate drains that one and never reaches the tail of the list.

Live evidence, base with 18 Ores containers:

    Bauxite      29,107,507      <- sorts first
    Silver          142,504      <- sorts last
    CrushedBauxite  129,514      <- crusher had processed this
    CrushedSilver     1,789      <- and almost none of this

`SilverIngot` sat at 157 for an entire session against 142k of unprocessed
silver ore. With `Organize=false`: 157 -> 754 -> 2,659, and the item went from
`RawShortage` to `Satisfied`. The chain was never broken; the gate was being
fed nothing but bauxite.

Three wrong diagnoses preceded the right one, worth recording because each was
consistent with the data available at the time:

1. "No purification stage for silver" - refuted by `CrushedSilver` existing.
2. "Refineries choose ore by their own priority" - the mechanism is the SORTER
   draining an ordered container, not the refinery.
3. "Ore alphabetisation is a non-issue, 2.1M aluminium proves the chain works"
   - it does work, for the ore that sorts first. Abundance of the winning
   item is not evidence that the losing item is being served.

- `OrganizeSkip` parses through the existing `ParseCategories`, which matches
  whole alphanumeric words against `CATS` - a comma list needs no new parsing
  code and an unrecognised name is dropped rather than trusted.
- A container is skipped if ANY of its categories is exempt. A multi-category
  container including an exempt one is exempt, since ordering it would pin
  slot 0 for the exempt category as well.
- `[IOPM.Organization]` gains `Skipped=` (the RESOLVED set, so a typo is
  visible rather than echoed back as typed) and `SkippedContainers=`.
- Default in a freshly written config block is `OrganizeSkip=Ores`. Existing
  config blocks are NOT back-filled - see 2.4.24 - so this must be typed in
  by hand on the deployed block.

Note that turning `Organize` off entirely also disables the stack-merge pass
from 2.4.19, which lives inside `OrganizeInventories`. `OrganizeSkip` exists
so ore containers can be exempted WITHOUT losing merging elsewhere.

CONFIRMED LIVE with `OrganizeSkip=Ores`:

    [IOPM.Organization]
    Enabled=true  Skipped=Ores  SkippedContainers=18  Examined=16

Container arithmetic checks out exactly: 35 warehouse containers total, minus
18 Ores exempted by the new key, minus 1 Overflow exempted by the pre-existing
`OVF` check (which is not counted in `SkippedContainers`), leaves 16 examined.
`OutOfOrder=0` on all 16, so nothing had fragmented during the interval when
`Organize` was off - consistent with routing using the stacking overload and
only the positional move (`stackIfPossible:false`) ever splitting a stack.

## 2.4.24

Source 118,023 -> artifact 89,937 (saved 28,086, 23.8%; 10,063 headroom).

A skipped phase must not leave its diagnostics reporting the last cycle that
ran. `OrganizeInventories` zeroes its counters on ENTRY, so gating the call on
`Organize=false` left `[IOPM.Organization]` frozen on the final cycle before
the toggle was saved -- counters, `LastAttempt`, container name and all. Found
live: the section still read

    Examined=34 OutOfOrder=2 Attempts=2 Succeeded=2
    LastAttempt=... Ores E | PurifiedBauxite | slot 3->2 | ok

with `Organize=false` correctly set and honoured, which reads as an active
phase and made the toggle look broken. The organizer was in fact off.

This is the same failure class as the frozen status screen in 2.4.14: a stale
display presented as current state. It is worse than a missing display,
because it invites a wrong conclusion instead of prompting a question.

- `ResetOrganizeDiag()` extracted; called on the skip path as well as on entry
  to `OrganizeInventories`. It also clears the string/index state
  (`_orgLastContainer`, `_orgLastItem`, the slot indexes, `_orgLastError`),
  which the old on-entry reset never touched at all -- so `LastAttempt` was
  stale even across normal cycles that examined nothing.
- `[IOPM.Organization] Enabled=` is now emitted unconditionally, so a reader
  can distinguish "ran and did nothing" from "was not asked to run" without
  cross-checking `[Sorting] Organize` by hand.

RULE FOR FUTURE PHASES: any phase gated behind a config flag owes its
diagnostics a reset on the skip path. Resetting on entry only is not enough.

Also worth recording, since it cost time twice: `WriteDefaultCustomData` fires
ONLY when Custom Data is empty. New config keys are therefore never
back-filled into an existing `[IOPM]` config block -- an upgraded script reads
them as absent and falls back to the coded default. That is correct behaviour
(the alternative rewrites player-owned sections, and MyIni cannot delete a key
to undo a mistake) but it means a new setting must be typed in by hand on any
block that already has config. Live: 2.4.23 with `Organize` absent, defaulting
true, and the whole `[Docking]` section deleted while all its defaults applied.

## 2.4.23
CATEGORY OVERRIDES NOW MATCH ON FULL TypeId/SubtypeId, NOT SubtypeId ALONE.

A SubtypeId is NOT unique across TypeIds. Caught by an item dump the moment
seeds appeared on the base:

    MyObjectBuilder_PhysicalObject/Grain =   9   (food)
    MyObjectBuilder_SeedItem/Grain       = 151   (seed)

Both share the SubtypeId "Grain". The override table was keyed on SubtypeId and
is consulted BEFORE the TypeId map, so both matched "Grain=Consumables" and 151
seeds were routed out of Seeds into Consumables. Mushrooms and Vegetables have
the identical food/seed name collision and routed correctly only because they
happen not to be overridden - the bug was latent for every one of them.

Keys are now full raw types where the TypeId is confirmed
(PhysicalObject/Grain, PhysicalObject/SpaceCredit). Lookup tries the full raw
type first, then the bare SubtypeId as a fallback so an unconfirmed entry still
works: Algae keeps a bare key because it has never appeared on the base, and a
guessed TypeId would silently stop its override working.

SILVER MYSTERY SOLVED, and it was never the ore ordering. The same dump showed
Ore/Silver 142,504 and Ore/CrushedSilver 1,789 against Ingot/Silver 156.92,
while bauxite beside it ran Bauxite 29M -> Crushed 129k -> Purified 72 ->
Aluminum 2.1M. Refineries are demonstrably working and silver ore is abundant -
the silver chain is simply missing its PURIFICATION stage in-game. The
OrganizeSkip=Ores change that had been proposed would have been built on a wrong
hypothesis; it is not needed and was not written.

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

NOT an undock test - corrected. LoadoutContainers went 2 -> 1 because a [Stock]
container was REMOVED, not because a ship undocked; ConnectedConstructs stayed
at 9 throughout. What it does show is that dock state drops a removed loadout
container cleanly, and that the borrow accounting is symmetric: base Motor moved
750 -> 780, and 30 is exactly what had been lent to that container. Removing the
block spills its contents into the conveyor network, and IOPM routed the
borrowed Motors back to base. Undock cleanliness itself remains untested.

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
