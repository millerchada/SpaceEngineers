# IO Power Control — changelog

## v0.1.11 — the stored-decline leg is now scale-free (2026-09-19)

**Detector change only.** The material-discharge threshold, the 10 s sustained hold, the
v0.1.10 stress latch and 15 s recovery, and brownout behaviour are all unchanged. No actuator
changes. No steam/H2 model.

### The defect

The third entry leg was expressed as a share of bank capacity:

    required decline = StoredDeclinePercent/100 x MaxStored
    time to qualify  = required / deficit

      3 MWh bank, 4.71 MW deficit  ->  0.015 MWh  ->  ~11 s   (UAT measured 10.5 s)
    300 MWh bank, 5.00 MW deficit  ->  1.500 MWh  ->  ~18 MINUTES

A bigger battery does not make a deficit less real; it only makes the same deficit a smaller
fraction of the bank. Tying the qualification to capacity made the detector arbitrarily slow on
exactly the installations that matter most.

### What the leg is actually for

Rejecting the gross/net telemetry artefact - a bank reporting ~11 MW out against ~12 MW in is
moving no net energy, so nothing falls. The honest test of that is not "a fixed share of the
bank disappeared" but **"stored energy is falling consistently with the discharge we are
measuring"**.

### New qualification semantics

    expected += battNetOut x dt              integrated over the entry window, MW x s -> MWh
    actual    = storedAtWindowStart - storedNow
    qualify  when sustained AND actual >= max(MinDeclineMWh, DeclineConsistency x expected)

    DeclineConsistency  0.5      fraction of the integrated expected energy
    MinDeclineMWh       0.002    absolute floor, an ENERGY not a share

Both terms are scale-free. The ratio is deliberately loose: SE reports simultaneous gross
charge and discharge, so demanding equality would fail on the very artefact this is meant to
tolerate. The floor exists only to reject "stored did not move at all", and being absolute is
what keeps it from reintroducing the scaling problem.

`dt` comes from the accumulated clock rather than an assumed 1 Hz tick, and a gap longer than
5 s - a recompile, a paused server - integrates **nothing** rather than inventing a slab of
energy that was never measured.

### Verified timings, computed rather than asserted

| Case | v0.1.11 | previous rule |
|---|---|---|
| UAT: 4.71 MW into a 3 MWh bank | **10.0 s** | 11.5 s (measured 10.5 s) |
| 5 MW into a **300 MWh** bank | **10.0 s** | **1080 s** |
| 5 MW into a 3 MWh bank | 10.0 s | 10.8 s |
| 0.5 MW, at the bar floor | 15.0 s (floor dominates) | 108 s |

So behaviour on the test base is essentially unchanged - which matters, because the v0.1.10
UAT evidence was gathered against it - while a large bank now qualifies on the same timescale
instead of eighteen minutes later.

The rejection case still works by construction: during the gross/net oscillation `actual` stays
near zero while `expected` climbs, so the ratio is never met.

### Config

`StoredDeclinePercent` is **removed**, not left inert - a key that silently does nothing is
worse than one that is gone. `DeclineConsistency` and `MinDeclineMWh` replace it. An existing
Custom Data still carrying the old key is harmless; it is simply ignored.

`scan` now prints `DeclineExpected`, `required` and the actual decline side by side.

## v0.1.10 — stress latch, and a brownout freshness guard (2026-09-19)

Two defects from the full v0.1.9 Phase A UAT. Entry thresholds and all passing v0.1.9
behaviour are untouched.

### DEFECT: established stress cleared during a sustained real deficit

Live at 19:02:24, with a 32 MW jump drive still installed and the battery still discharging:

    19:02:24  Electrical stress cleared
    19:02:24  Power condition CRITICAL -> NORMAL
    19:02:54  ELECTRICAL STRESS - batteries draining 2.54 MW for 16.3s, stored down 0.02 MWh

History across the gap: `bflow` 4.80, 4.92, 2.20, 2.45, 5.69 MW with stored falling
2.86 -> 2.82 MWh. **Nothing had recovered.**

**Cause: there was no latch.** `now` was the ENTRY test recomputed every cycle and assigned
straight to `_stressed`. One tick below the bar reset *both* accumulated qualifications:
`if (!material) _battStressSince = -1e9;` restarted the hold timer, and the next material tick
re-captured `_battStoredAtStress` at the by-then-lower stored level, so the 0.015 MWh decline
also had to be re-earned from a fresh baseline. A momentary dip - demand touching generation
during a production trough, invisible between 10-second history samples - discarded twenty
seconds of accumulated evidence, and the re-assert 30 s later is the system earning the whole
qualification again from zero.

**Fix: entry and exit are different questions.** Entry asks "is there a deficit worth acting
on"; exit asks "has it actually recovered". Requiring the entry qualification to remain
continuously true in order to stay latched conflates the two, and ordinary variation between a
2 MW and a 5 MW deficit is not a recovery.

    ENTRY  unchanged: material discharge, held continuously for StressHoldSeconds,
           and a confirmed stored-energy decline
    EXIT   drain below BattRecoverFraction x the entry bar (default 0.5, so ~0.59 MW on the
           test base) held continuously for StressRecoverSeconds (default 15s)

While latched, the reported decline is the cumulative drain since entry rather than being reset
to zero, so the scan shows how deep the episode has become.

### DEFECT: brownout counted a deleted block

`br=1` for roughly 14 seconds immediately after the temporary jump drive was deleted, then 0
with an empty list at the next scan. **Confirmed from the code:** `_bi` is rebuilt only inside
`Discover()`, which runs on the `RescanSeconds` boundary, so a deleted block stays in the
cached list for up to 30 seconds and can keep reporting
`Enabled && IsFunctional && !IsWorking`.

**Fix: survive a topology refresh.** A block only counts once it has been continuously browned
out for longer than a full rescan interval, which it can only manage if `Discover()` found it
again. A deleted block is not in the rebuilt list, so its age never accrues. Both figures are
reported - `raw` is what a naive detector would have seen, and the gap between raw and confirmed
is itself the argument for why this signal is not yet allowed to authorise anything.

This is good evidence for the existing decision: **brownout stays telemetry only.**

### v0.1.9 Phase A UAT results

| Scenario | Result |
|---|---|
| CapacityRisk debounce under normal cycling | **PASS** - one committed transition, held 2.33s, no spam |
| Normal cyclic production, incl. 4.54 MW transients | **PASS** - no stress, no brownout, no decline |
| Heavy real production, ~27-32 MW, zero reserve | **PASS** - zero reserve alone did not trigger stress |
| Controlled overload: 32 MW jump drive, 60-63 MW demand | **PASS** - first true positive detection |
| Recovery after the jump drive was removed | **PASS** |
| Established stress during sustained deficit | **FAIL** - fixed above |
| Brownout after block deletion | **FAIL** - fixed above |

The positive detection captured at the transition:

    Demand 60.0  GenCur 58.3  GenCredible 58.3  Reserve -1.72
    BattNetOut 1.72  BattStressBar 1.17  held 26.5/10.0s  storedDecline 0.02/0.02 MWh
    ShedAuthority=battery-drain

**Detection latency was ~20 s, not 10 s**, and the stored-decline term rather than the hold
timer was the gating condition. The operator's arithmetic corrects an earlier claim of mine:
0.5% of a 3.00 MWh bank is 0.015 MWh, which at a 0.5 MW deficit takes **108 seconds**, not the
~36 s I stated - 36 s corresponds to 1.5 MW. Thresholds are deliberately **not** being retuned
on one negative and one positive case.

### Re-test result: both fixes PASS

Same 32 MW jump drive, battery re-enabled from the preserved 2.78 MWh state. Detected in 10.5 s
at a 4.71 MW drain; **latched through ~90 s of deficit varying between 4.2 and 7.7 MW with every
fast-ring sample `S`** - the exact variation that cleared the alarm under v0.1.9. On removal,
`Condition` recovered immediately with electrical reserve while `Stressed` stayed latched for
the full 15 s confirmation, then cleared. Brownout `confirmed=0, raw=0`; the deleted jump drive
never became a confirmed brownout.

**Phase A detection is complete.** Proven negative (cyclic factory, transients, zero reserve)
and positive (32 MW step), with a working latch and recovery hold. No known defects outstanding
in the detection path.

### Known scaling limit, recorded before arming

Detection latency is `(StoredDeclinePercent/100 x MaxStored) / deficit`. On the 3 MWh test bank
at 4.71 MW that is ~11 s, matching the measured 10.5 s. On a **300 MWh** bank at a 5 MW deficit
it would be **18 minutes**. The stored-decline term is expressed as a fraction of capacity, and
what it actually needs to verify is that stored energy is falling *consistently with the
reported drain* - a scale-free test. Harmless on the test base; must be fixed before AutoShed
is armed on a large production base.

### Unchanged

Entry thresholds, capacity model, shedding engine, `Potential`, catalog, CapacityRisk debounce.
No steam/H2 sustainable-capacity model.

## v0.1.9 — Capacity Risk debounce (2026-09-19)

**Telemetry-only fix.** Nothing in this change touches stress detection, the battery stress
thresholds, brownout logic, shedding, the credible-generation math or restore behaviour.

### The Phase A observation run PASSED

Normal and heavy cyclic factory operation, roughly 5 MW through repeated 28 MW demand:

* `Stressed=False` throughout
* no `ELECTRICAL STRESS` events
* brownout count stayed **0**
* battery stayed full; the heavy-load period showed `bflow=0.00 MW`
* generation followed demand without battery support

That is the first stress detector in this project to survive real cyclic industry. Notably the
battery never had to support the load at all during the heavy period, so what was rejected was
not marginal: the detector had nothing to fire on and correctly fired on nothing. The 0.1 MW
instantaneous test in v0.1.6 would have triggered on the earlier 4.54 MW support pulses; the
three-part test did not.

### The one defect: Capacity Risk chattered

The event log flipped `WARNING <-> CRITICAL` every few seconds as instantaneous credible
reserve moved between ~0 and a few MW across ordinary production cycles. Every individual
crossing was true. The sequence was still noise, and an advisory field that shouts several
times a minute stops being read - the same failure mode as the permanent WARNING fixed in
v0.1.5, arriving from the other direction.

**Asymmetric debounce, deliberately:**

    candidate WORSE than current    promote after CapacityRiskPromoteSeconds   default 2s
    candidate BETTER than current   clear after  CapacityRiskRecoverSeconds    default 15s
    candidate CHANGES               the clock restarts

Getting worse is news and should arrive quickly; getting better is a claim that has to hold up,
because a momentary trough in demand is not a recovery. Restarting the clock on any change of
candidate is what stops a value oscillating across a threshold from ever committing - during
cycling the field settles at the **worse** of the two, which is the correct bias for a risk
indicator.

Timing comes from the accumulated clock, not a count of invocations, so it does not depend on
the tick landing at exactly 1 Hz.

Events are logged **only on a committed change**, and carry how long the new value was held.
The instantaneous crossings are deliberately silent - they are what made the log unreadable.

`scan` exposes `CapacityRisk`, `Candidate` and `CandidateHeld=<held>/<required>s` so the
debounce can be diagnosed rather than guessed at.

### Unchanged

Everything else, explicitly. No steam/H2 sustainable-capacity model - that remains the next
modelling step, after the battery stress transition has been proven under a deliberate overload.

## v0.1.8 — one battery-flow sign convention (2026-09-19)

Found during the Phase A observation run: the same 4.54 MW battery-support pulse read **+4.54
in the fast ring and -4.54 in the history of the same scan**, both columns labelled "net".

Not intentional. Three places reported battery flow in two opposite conventions:

    fast ring  BattNet = _battNetOut       Sum(max(0, out-in))   discharge POSITIVE, clamped
    history    Net     = _battIn - _battOut                      charge POSITIVE
    dashboard  Net Flow= _battIn - _battOut                      charge POSITIVE

Since `Sum(in) - Sum(out)` is exactly `-Sum(out - in)`, those were negatives of one another and
neither label said which way round it was. `_battNetOut` was added for the demand-accounting fix
in v0.1.4 and the older displays were never revisited.

**One convention from here: positive means power LEAVING the batteries** - discharge - matching
the direction protection cares about. Every display now states it in the label.

Two related quantities remain, and they are **not** the same on a mixed bank, so both are shown
rather than one being inferred:

    NetFlow = Sum(out_i - in_i)          signed; negative while charging
    NetOut  = Sum(max(0, out_i - in_i))  clamped per battery; what demand and the stress
                                         detector use, because one battery discharging while
                                         another charges really is supplying the grid

On a single-battery grid they coincide whenever the bank is discharging, which is precisely why
the discrepancy presented as a clean sign flip rather than as two different measurements.

The fast ring now carries `bflow` and `bout` side by side; both ring headers state the sign
convention in as many words.

**One deliberate data change:** history samples recorded before this build hold the opposite
sign, so a scan captured across the upgrade will show the flip mid-ring. Nothing else is
affected - no protection decision ever used the history or dashboard figure.

### Phase A observation, interim

No stress events, no brownouts, no stored-energy decline, across repeated ~4.54 MW
battery-support pulses. The three-part battery test is rejecting exactly what it was built to
reject. Run continues.

## v0.1.7 — Phase A: both false-positive paths removed, evidence collected instead (2026-09-19)

**Defect fix.** v0.1.6 still declared electrical stress on a healthy, naturally cycling IO
factory. Live capture:

    Condition=CRITICAL  Stressed=True  CredFlatFor=222.6s
    Demand=12.0MW  DemandFloor=7.70MW  DemandRise=4.30MW
    GenCur=12.0MW  GenCredible=12.0MW  Reserve=0.00MW  BattNetOut=0.00MW

Active production at the snapshot: WireDrawer 1.5 MW, CementKiln 3.0 MW, RockCrusher x2 5.0 MW.
Ordinary cyclic work, no overload.

### The demand-floor clause is deleted, not tuned

It followed troughs rather than trend: the floor dropped **instantly** to any lower demand and
crept back at 2% per cycle. IO production cycles between active and idle, so every trough
re-pinned the floor and the next ordinary processing cycle read as a new rising load.
`DemandRise 4.30` was the entire gap `12.00 - 7.70`, meaning the floor had been reset within
the previous sample.

From a trough, `gap(n) = 4.30 x 0.98^n` at 1 Hz, so `rise >= 4 MW` holds for **the first ~3.6
seconds after every trough**. With `reserve == 0` and `FuelAtCeiling == true` both cold-start
tautologies and `CredFlatFor` long past, **every production cycle asserted stress.** No
threshold fixes that. A trough-follower structurally cannot distinguish cyclic industry from a
new load, so the clause is gone and is not replaced with another demand-baseline heuristic.

### Instantaneous battery net output is no longer stress

The second false-positive path, from the event log: `ELECTRICAL STRESS - batteries carrying
0.22 MW`. That is 0.0003 MWh against a 3.00 MWh bank - **0.01%**, an order of magnitude below
the resolution at which stored energy is even displayed, and indistinguishable from the control
oscillation of a bank reporting ~11 MW gross out against ~12 MW gross in.

Battery stress now requires all three, together:

    material   net discharge >= max(BattStressMW, 2% of credible generation)   default 0.5 MW
    sustained  held CONTINUOUSLY for StressHoldSeconds - the timer restarts on any dip
    draining   stored energy actually fell >= StoredDeclinePercent of capacity  default 0.5%

The energy-decline term is what rejects the oscillation: a bank moving no net energy cannot
make stored energy fall, however its gross figures read.

### Brownout detection added as TELEMETRY ONLY

    Enabled && IsFunctional && !IsWorking

For most block types that is a block switched on, undamaged, and not getting the power it asked
for - a direct observation of unmet demand that needs no demand trend and no battery, and so
would work on a battery-less grid, which nothing currently can. `scan` reports the count, and
per block the name, definition, category and whether it is protected or was shed by the script.

**It does not authorise shedding.** Promoting it on the strength of the idea would repeat the
exact mistake this commit exists to correct. First we need live evidence of which IO and
vanilla blocks report `IsWorking=false` for reasons unrelated to power; anything listed that is
not short of power is a false positive, and the Phase A run exists to find them.

### Instrumentation: the evidence did not exist at any resolution we recorded

A three-second battery excursion was being mistaken for stress, and the only history was
sampled every ten seconds. `scan` now dumps:

* a **1 Hz, 60-sample fast ring**: age, Demand, GenCurrent, GenCredible, BattNetOut,
  BatteryStored, brownout count, Stressed
* the existing **10-second history** ring, last 90 samples

plus `BattStressBar`, how long the drain has been held against the requirement, the stored
decline against the decline needed, and `ShedAuthority`.

### Fail conservative, and say so

Sustained battery drain is the **only** shedding authority in this build. With no qualifying
signal, `Stressed=False` and `AutoShed` does nothing - and the dashboard says
`OBSERVATION ONLY / no trustworthy overload signal present; nothing will be shed` rather than
sitting silent, because an operator who has switched AutoShed on is entitled to know it is not
going to act.

### Unchanged

Capacity model, shedding engine, `Potential`, catalog. No steam/H2 sustainable-capacity model.
The Ore Purifier left in `SHED STATE` from the v0.1.5 false shed is deliberately untouched: it
is evidence that per-block shed state survived two recompiles.

## v0.1.6 — the stress gate was two tautologies and a timer (2026-09-19)

**Defect fix, caught at UAT step R1 before any load was ramped.** A healthy, steady station
reported:

    Condition=NORMAL  Stressed=False  CapacityRisk=CRITICAL
    FuelAtCeiling=True  CredFlatFor=1.80s
    GenCredible=16.5MW  GenProven=16.5MW  GenCur=16.5MW  Demand=16.5MW  Reserve=0.00MW

**That station was 8.2 seconds away from shedding 5 MW of perfectly good production.** At
`CredFlatFor >= StressHoldSeconds` the ceiling clause would have gone true, Condition would
have followed reserve to CRITICAL, `DeepestTier` would have reached Normal, and `DoShed` would
have targeted `4 - 0 + 1 = 5 MW`. `AutoShed` was off on the server, which is the only reason it
did not happen.

### Why it was degenerate

For a fuel-fed producer `credible = max(proven, current)`. At a cold start proven EQUALS
current, so:

    cur < cred - 0.1  is false for every producer  ->  _fuelAtCeiling TRUE by construction
    reserve = SUM(cred_i - cur_i) - battNetOut     ->  0.00       by construction

Two of the three ceiling conditions were satisfied by arithmetic rather than evidence, and the
third was a hold timer. The gate was never testing anything.

The rule as requested always included **evidence of sustained or rising demand**. v0.1.5
substituted "credible capacity is flat" for that, which is not the same claim - a stable base
has flat everything. That substitution was the defect.

### DEMAND PRESSURE is now an explicit term

    _demandFloor drops instantly to any lower demand, and creeps up toward a higher one at
    2% per cycle (about a one-minute time constant).
    rise = Demand - _demandFloor
    the ceiling clause additionally requires rise >= ShedReserveMW

A step increase shows as a gap; a steady load closes that gap within about a minute and
accumulates no pressure at all. On the baseline above, `rise` converges to 0 and the clause can
never fire. On a sudden 12 MW load it reads ~9.8 MW at the ten-second mark, which is pressure.

Consequence worth knowing: on a battery-less grid, ceiling stress lapses roughly a minute after
a step even if the overload persists, because the floor catches up. With batteries the discharge
clause carries it indefinitely. This is the same structural limit recorded in v0.1.5 - demand
cannot exceed credible generation on a battery-less grid by construction - and consumer-side
brownout detection remains the real answer.

### Witnessed capacity now survives a recompile

v0.1.4 had witnessed ~19.4 MW. Installing v0.1.5 restarted `proven` at the then-current
16.5 MW, which is precisely what produced `credible == current == demand` and a reserve of
exactly zero. Evidence that takes a working base under load to acquire must not be discarded by
a paste-deploy.

Per-producer proven output is now persisted in `Storage` (`P|entityId|MW`, capped at 200) beside
the learned consumer catalog and the shed list, and `Save()` is called every 60 s - until now it
ran only on a mode change, shed, restore or scan, so a base that never shed checkpointed
nothing.

Keyed by `EntityId`, which is the right granularity in both directions: it survives a recompile
and a world reload, while a producer that is ground down and rebuilt gets a new id and correctly
starts proving itself again.

The offline->online reset introduced in v0.1.5 now fires only for a stop this session actually
**witnessed**. On a world reload a block can read not-working for a moment before it settles,
and treating that as a genuine stop would have discarded the figures just restored from Storage.

### Capacity Risk is labelled when there is no evidence

Zero credible headroom is indistinguishable from headroom that has never been measured, and
CRITICAL asserts knowledge we do not have. The arithmetic is unchanged and still shown - the
separation of Power Condition from Capacity Risk is working as designed - but the field now
carries a `?` while no producer has been seen to deliver more than it is delivering now, and
`scan` prints `HeadroomEvidence=measured|NONE` in as many words.

### Unchanged

The capacity model, the shedding engine, `Potential`, the catalog. No fuel/steam sustainable
model: that remains the next modelling improvement, after basic protection behaviour is proven
safe.

## v0.1.5 — credible capacity: protection stops spending nameplate (2026-09-19)

**Defect fix. Found by the first live reading of the pre-UAT baseline, before any shedding was
attempted.** Protection decisions were taken against NAMEPLATE generation.

    GenNameplate  58.6 MW      GenCurrent  14.5 MW      Reserve reported  44.1 MW
    GenProven     19.4 MW      steam turbine delivering 9.92 MW of a 50 MW rating

The protection engine believed it had 44 MW of headroom on a base whose entire generation
system had ever demonstrated 19.4 MW, and whose sustainable ceiling is about 26 MW. The
incident this script exists to prevent is demand exceeding *sustainable* generation - and on a
fuel-fed producer that happens long before demand approaches the rating. A 50 MW steam turbine
fed by a well good for 26 MW of steam will never read near 50 MW. **Nameplate is a rating, not
a capability, and it must not be spent as reserve.**

### Three figures

    GenCurrent    what is flowing now                    exact
    GenCredible   what there is EVIDENCE we can call on  protection spends this
    GenNameplate  the sum of ratings                     display and theoretical capacity

    credible_i = MaxOutput_i               environmental (solar, wind)
               = max(proven_i, current_i)  fuel-fed, or unrecognised

    Reserve = GenCredible - Demand
    N-1     = GenCredible - largest credible_i - Demand

Environmental producers are believed at their `MaxOutput` because that figure is already a
capability: six solar panels on the test base reported three different values simultaneously by
orientation, and the wind turbine moved between 3.19 and 5.46 MW across captures. Everything
else is credited only what it has been witnessed to deliver, and an *unrecognised* producer is
treated as fuel-fed because that is the conservative reading.

`proven` is reset on a genuine offline->online transition: a producer that stopped and came
back may have come back on a different fuel or steam supply, so what it proved beforehand is no
longer evidence about what it can do now.

### Stress: why credible reserve alone cannot authorise shedding

On a grid that has never been loaded hard, credible is low purely for want of evidence -
`proven` only grows when a producer is *asked* for more. Shedding on that would punish a base
for being lightly used. So shedding now needs corroboration:

    Stressed =  batteries are NET discharging above 0.1 MW
             OR ( credible reserve is below the shed threshold
                  AND every fuel-fed producer is pinned at what it has demonstrated
                  AND credible capacity has not RISEN for StressHoldSeconds )

The third clause is the discriminator. An earlier draft used "fuel-fed producer at >=99% of
MaxOutput", which **would have failed on exactly the case being fixed** - a 50 MW turbine fed
26 MW of steam never approaches 99% of its rating. Pinned at CREDIBLE, with credible no longer
growing, is the observable difference between "demand is rising and being met" and "demand has
reached the ceiling".

`RequireStressToShed=true` by default; `StressHoldSeconds=10`.

**Structural limit, stated rather than papered over:** on a grid with NO batteries, Demand is
generator output plus net battery discharge, so demand can never exceed credible generation and
reserve can never go negative. Such a grid can be seen to be AT its ceiling but never in
deficit. The second stress clause is what makes a battery-less installation work without the
escape hatch, and it is deliberately the conservative reading - no buffer means nothing absorbs
an overload, so acting at the ceiling is right. Consumer-side brownout detection (a production
block that `IsProducing` while receiving far less than it requires) is the real answer and is
left for a later version.

### Power Condition and Capacity Risk are now separate

Merging them meant a base with thin demonstrated headroom reported WARNING permanently while
being electrically fine, which teaches an operator to ignore the field that is supposed to
interrupt them.

    Power Condition   observed electrical strain. NORMAL unless actually stressed,
                      whatever the headroom arithmetic says. Shedding keys off this.
    Capacity Risk     contingency exposure: thin credible headroom, negative N-1.
                      True of a lightly loaded base, worth showing, not an alarm.

The test base now reads `Condition NORMAL` / `Capacity Risk WARNING`, which is the honest
description of it. The ad-hoc "rated X but only Y ever delivered" banner is removed - Capacity
Risk carries that message as a rated field, so the banner said it twice.

### Restore threshold had to be capped, or staged restoration would never fire

Moving protection onto credible capacity shrinks the scale every absolute MW threshold is
measured against. Reserve's ceiling is now credible capacity itself, so a base with 19.4 MW
credible **can never reach a 20 MW restore threshold at any demand** - restoration would have
failed silently, which is worse than restoring slightly early because nothing in the log says
it is happening.

The cap is derived rather than invented: restoration must be reachable while still leaving the
shed margin intact, so it is at most `credible - ShedReserveMW`, never below `ShedReserveMW + 2`
so the hysteresis band always exists. The configured figure still applies wherever it is
reachable, so a large base behaves exactly as before. `scan` prints both.

### Deliberately unchanged

`Potential` demand, the catalog, and the shedding engine itself. This is a capacity-model
defect fix, not a feature change.

### Left open

An IO-specific *sustainable* capacity model - deriving the steam turbine's real ceiling from
well depth via the formula in MOD_DEFINITIONS.md rather than waiting to witness it - is now
possible and is the obvious next step. Not done here: it would replace evidence with
inference, and the evidence-based model needs to be proven in game first.

## v0.1.4b — the catalog is no longer empty (2026-09-19)

The Industrial Overhaul mod files were located on disk, which turns Tier 3 of the
potential-demand model from an empty promise into 338 rows read from the game's own
definitions. `IO_Power_Control/tools/extract_io_catalog.py` generates `CATALOG[]` from
`RequiredPowerInput`, `OperationalPowerConsumption`, `PowerConsumptionMoving` and
`RequiredIdlePower` (largest wins - Potential asks for the ceiling), reading the base game's
Data directory first and letting the mod override it, the same order the game loads them.

Nothing here is estimated. A block that declares no figure gets no row and keeps being
reported as unknown. Validated against the live server, where five independent cross-checks
agree exactly:

    CementKiln          3        vs DetailedInfo  3.00 MW
    LargeRefinery       3        vs DetailedInfo  3.00 MW
    GeothermalWellHead  0.5      vs DetailedInfo  0.50 MW
    LargePistonBase     0.002    vs BuildInfo 2 kW, and a measured demand delta of 1.79 kW
    LargeBlockSlideDoor 0.001    vs BuildInfo max 1 kW

**Precedence matters and is now explicit**, in one place in the code:

    user catalog  ->  DetailedInfo  ->  built-in  ->  learned  ->  unknown

The built-in table sits BELOW `DetailedInfo` deliberately: it is a definition-time rating,
while a running block describing itself accounts for upgrades and damage. The operator's own
`[PowerControl.Catalog]` rows outrank everything, because those are measurements taken on
purpose. A previous build merged both into one dictionary that outranked `DetailedInfo`, which
would have let a stale definition override a live block.

**What the catalog cannot reach.** Adding the base game's Data directory to the scan yields
only five extra subtypes. Many vanilla blocks - sliding hatch doors, medical rooms and
programmable blocks among them - declare no power tag in any `.sbc` at all, because their
consumption is hardcoded in the game's C# and applied through a resource sink component at
runtime. Tools that read the live component can show those figures; definition files cannot.
Those blocks stay unknown by necessity, and the only route to a number is a measurement typed
into `[PowerControl.Catalog]`.

## IO 1.7.7 geothermal, solved from source

`Data/Scripts/GeothermalWell.cs` settles the two contradictory tooltips, and the result was
confirmed in game to four significant figures (a well at 1378.9 m reported `Max gen: 20.71
heat/s`; the formula gives 20.715).

    heat/s       = 6 x 0.8 x (0.8 x (Depth - 300) / Wells within 250m) / 200
    steam/s      = heat/s x 1.25          (Wellhead IceToGasRatio)
    sustained MW = steam/s                (SteamTurbine: MaxPowerOutput 50, 50 Steam/sec)

    => MW = 0.024 x (Depth - 300) / Wells

So one well needs **~2,383 m** to sustain a single 50 MW turbine, and 900 m yields 14.4 MW.
`EfficiencyReduction` counts `GeothermalWellTip` blocks within a 250 m sphere *including
itself*, so wells spaced beyond 250 m do not interfere - four wells at 900 m beat one well at
2.4 km comfortably.

**Two documentation defects in the mod**, worth knowing when reading its tooltips:

* The Well Tip's "Minimum depth: 150 m / ~7.5 Steam/sec per 100 m below 150 m" matches the
  *commented-out* previous formula. The live code gates at 300 m and divides by 200.
* The Wellhead's "~6 Steam/sec per 200 m" is 1.25x the real figure of 4.8. The live line
  applies `BaseGeneration` (0.8) twice; drop the outer one and the tooltip becomes exactly
  right. The superseded line above it applies it once. That reads like an editing accident
  rather than an undocumented 20% nerf, though intent cannot be read from source.

## v0.1.4 — demand accounting, proven output (2026-09-19)

> **Correction, same day.** This entry originally claimed the creative/survival A/B had proved
> `MaxOutput` to be a nameplate. It had not, and the claim is retracted below. The reasoning
> rested on a battery "discharging 3.27 MW" - a *gross* figure, whose net position was 1.06 MW
> of charging. It was the same artefact this very release fixes, used as evidence one hour
> before it was understood. The **Proven** metric added here is still worth having, but it was
> added on a false premise and the premise is corrected rather than quietly dropped.

**`IMyPowerProducer.MaxOutput`: still unresolved.** Same grid, same blocks, only the world's
game mode changed:

| Producer | creative `out=/max=` | survival `out=/max=` |
|---|---|---|
| Steam Turbine - Mirrored | 0.00 / **50.0** | 0.72 / **50.0** |
| Hydrogen Engine | 0.00 / **5.00** | 0.07 / **5.00** |

The turbine delivered 0.72 MW while advertising 50.0 MW, which looked decisive. It was not.
The supporting argument was that the batteries were discharging 3.27 MW at that instant, so a
capable turbine would have been used instead - but 3.27 MW was **gross** output against 4.33 MW
of gross input, a net *charge* of 1.06 MW. The base was in surplus and the turbine was idle by
dispatch order.

A later capture confirms the turbine is not starved at all: `Filled: 100.0% (100L/100L)` with
`Current Output: 0 W`. Space Engineers dispatches renewable and stored supply ahead of
fuel-burning generation, so a turbine reads zero whenever wind, solar and batteries cover the
load - which makes the question **untestable at rest**. It needs a load larger than everything
else combined, and then: ramps toward 50 MW means honest; plateaus low while batteries go
net-negative means nameplate.

Solar and wind are **not** affected: six solar panels report three different values
simultaneously by orientation and the wind turbine has moved between 3.19 and 5.46 MW across
captures. For those, `MaxOutput` is the achievable figure.

**PROVEN OUTPUT** is kept regardless: the highest output each producer has ever actually been
witnessed to deliver, summed. It is a hard LOWER bound - a producer may be capable of more and
simply never have been asked - and it costs nothing to carry. The dashboard shows it beside
Available, and warns when the rated figure is more than double the witnessed one *while
batteries are net discharging*, which is the combination worth looking at. The script never
claims to know the true ceiling; it shows what is rated and what has been seen, and the gap
between them is for the operator to interpret.

(The warning's test was itself corrected before release: it compared against gross battery
output, which is never zero on a battery in Auto, and would have latched on permanently.)

**Demand double-counted circular battery flow.** A single battery reported `in=2.14MW` and
`out=2.04MW` *simultaneously*, its input exactly equal to total generation - charging from
generation while discharging to load, a round trip that does not exist physically and is an
artefact of how the game reports a battery in Auto. Demand was `GenCur + gross battery output`,
so the circulating power was counted twice: **about 2.1 MW of real load reported as 4.17 MW**.
The correct accounting is net discharge per battery:

    Demand = GenCur + SUM over batteries of max(0, output - input)

Total supply is generator output plus net battery discharge; total consumption is non-battery
load plus net battery charging; they balance, so consumption has no gross term in it anywhere.
Net is taken **per battery**, not across the bank - one battery charging while another
discharges is real power moving between them, not circulation inside one block.

The case that rules out the tempting shortcut: a **docked ship set to Recharge** draws 12 MW
with `in=12, out=0`, so net discharge is 0 and Demand stays `GenCur` - which has risen to
generate that 12 MW. Demand still rises, correctly. Netting charging *out* of demand, which
looks equally reasonable, would have hidden the exact load this script was written to catch.

This artefact was also the root cause of the spike spam below: the oscillation being reported
was the accounting, not the base.

**LOAD SPIKE spam.** The detector compared against the previous SAMPLE, so a cyclic load
re-reported the same spike forever: a piston drill tower oscillating between 2 and 7.6 MW
logged nine identical `+5.53 MW` events inside one minute and pushed everything else out of a
40-line log. An event that repeats is not an event. A spike is now a new high-water mark
against a slowly decaying reference, so the first cycle of an oscillation reports and the rest
do not, while a genuinely larger spike later still gets through.

Also in this build (was v0.1.3, never deployed, folded in here):

* `DetailedInfo` uses `\r\n` and the report only stripped `\n`, splitting every `detail:`
  line in two. Parsing was unaffected; the report is the point of that line.
* Unknown consumers are **grouped by definition**, three specimens each plus a
  `... and N more` tail. 115 pistons printed 115 identical lines; a real IO base has thousands
  of blocks and the report would have been both unreadable and over the size cap.
* `== CONSUMERS BY DEFINITION ==` gained `cur=`, the summed current draw. "Is this actually
  drawing anything right now" is what the shedding engine asks of every candidate, and the
  report could not answer it.
* Raw `DetailedInfo` dumped for every producer, and a `== GAS / STEAM TANKS ==` section using
  `IMyGasTank.FilledRatio`. IO models steam as a gas, so the buffer between well head and
  turbine reads directly.
* `ParseMW` takes the magnitude of a negative figure. BuildInfo shows a programmable block as
  `max power: -500 W`, so the sign convention exists; a negative would have produced a negative
  per-grid load estimate and a negative shed relief.

IO 1.7.7 steam figures, captured from the mod's own block descriptions:

    Steam Turbine        consumes 50 Steam/sec -> 50 MW   (1 MW per Steam/sec)
    Geothermal Well Tip  ~7.5 Steam/sec per 100m below 150m; min depth 150m -> 50/sec at ~817m
    Geothermal Wellhead  ~6 Steam/sec per 200m past the first 300m          -> 50/sec at ~1967m
    Well separation      250m optimal; efficiency falls off linearly if closer

The two production formulas disagree; which one the code uses is itself an open observation.

## v0.1.3 — report legibility (2026-09-19, folded into v0.1.4, never deployed)

`DetailedInfo` uses `
`, and the scan report only stripped `
`. The leftover carriage
return split every `detail:` line across two lines in the report - which matters because that
line is the entire evidence for *why* a block is unknown, and it is the thing that gets pasted
back for analysis. Parsing was never affected: `Trim()` on the value already dropped the `
`.

Two diagnostics added for IO steam generation, both visibility only, no behaviour change:

* **Raw `DetailedInfo` for every producer** (first 12, 140 chars). The open question on an IO
  steam base is whether `MaxOutput` is the nameplate or the figure actually achievable now.
  The live turbine reports `out=0.00/50.0MW` with no steam supply; if that 50 MW is nameplate,
  then Available Generation, Reserve and N-1 all overstate the base by 50 MW, which is exactly
  the self-deception this script exists to prevent. **This is unresolved and is recorded as a
  limitation until the well is producing and the two can be compared.**
* **`== GAS / STEAM TANKS ==`** with `IMyGasTank.FilledRatio`. IO models steam as a gas, so the
  buffer between well head and turbine is an ordinary gas tank and its fill reads directly.
  Section 11 of the spec puts steam-flow modelling out of scope *unless the API makes it
  trivial*; a fill ratio is trivial, and a falling steam buffer is the earliest possible
  warning that a steam turbine is about to stop carrying the base.

IO 1.7.7 steam figures captured from the mod's own block descriptions (evidence, not inference):

    Steam Turbine        consumes 50 Steam/sec -> 50 MW   (1 MW per Steam/sec)
    Geothermal Well Tip  ~7.5 Steam/sec per 100m below 150m; min depth 150m
    Geothermal Wellhead  ~6 Steam/sec per 200m past the first 300m
    Well separation      250m optimal; efficiency falls off linearly if closer

The two production formulas disagree - the Well Tip rule saturates one turbine at ~817 m, the
Wellhead rule at ~1,967 m. Not resolved here; it is recorded because the answer determines
whether a single well can carry a 50 MW turbine at all.

The unknown-consumer section is now **grouped by definition**, with at most three specimens
each and a `... and N more of <definition>` tail. Live evidence: a test grid grown to 115
pistons printed 115 identical unknown lines, and a real IO base has thousands of blocks. What
the report is for is the DEFINITION and one specimen of its raw `DetailedInfo`; the hundredth
copy of the same line only makes the report unreadable, and a report nobody reads is a report
nobody acts on.

Hardening from the same session: a negative power figure is a sign convention for "consumes"
(BuildInfo shows a programmable block as `max power: -500 W`). No readable `DetailedInfo` has
been seen doing it, but a negative parse would have produced a negative per-grid load estimate
and a negative shed relief, so `ParseMW` now takes the magnitude.

**Established this run (not a code change, but the reason the catalog exists):**

The BuildInfo mod displays `Input Power: 2 kW (max: 2 kW)` for a piston in the terminal, but
that text is **not** in the `DetailedInfo` a programmable block can read - the raw dump is only
`Attached / Current position: 10.0m`. BuildInfo renders client-side. So a piston genuinely
publishes nothing to scripts, and no parser change can reach it. The parser would have handled
the line format had it been there.

That makes BuildInfo the right instrument for filling `[PowerControl.Catalog]`: it reports the
rated figure directly, and its `max:` value is the one `Potential` wants.

Cross-check of the two independent methods, which agree: the demand-delta measurement gave
1.79 kW per piston (39 pistons, demand 0.19 → 0.12 MW) and BuildInfo reports 2 kW. 39 × 2 kW
= 0.078 MW, and 0.194 − 0.078 = 0.116, which displays as 0.12. The discrepancy is entirely the
0.01 MW display rounding, which is a useful confirmation that the exact-demand metric is sound.

## v0.1.2 — construct identity (2026-09-19)

Second live scan, same IO 1.7.7 test station grown to 69 blocks / 40 grids, with IO production
machinery added. One defect, and the first real confirmation that the potential-demand model
works on IO industry.

**Construct identity was not stable.** A construct was keyed by the smallest grid EntityId it
contained. Grinding off the subgrid holding that id changes the key, so the construct reads as
a different one - live evidence is two spurious `Grid disconnected Static Grid 5281` events
naming the HOME base while pistons were being built and removed.

That matters well beyond a noisy log. New-grid detection is the headline feature, and it runs
entirely off this key. A key that changes when a subgrid is added or removed means false
NEW GRID events on a base under construction - exactly the situation the feature exists for,
and exactly when the operator most needs to trust it.

Identity is now **overlap with the previous scan**: a construct sharing any grid with a
construct seen last time *is* that construct, whatever grids it has since gained or lost. The
home construct claims its identity first, because it is identified by containing `Me` rather
than by a key, so a ship separating from the base can never carry the base's identity away.
A construct whose grids were all absent last scan is genuinely new. Belt and braces: the
construct holding this programmable block can never raise a disconnect event about itself.

**Confirmed: the model works on IO industrial machinery.** Every IO production block published
a usable figure through `DetailedInfo`, all `src=detail`, no catalog entry needed:

| Definition | Rated |
|---|---|
| `MyObjectBuilder_Refinery/LargeRefinery` | 3.00 MW |
| `MyObjectBuilder_Refinery/Blast Furnace` | 2.00 MW |
| `MyObjectBuilder_Assembler/LargeAssemblerNew` | 1.50 MW |
| `MyObjectBuilder_Assembler/Fabricator` | 1.00 MW |
| `MyObjectBuilder_Assembler/WireDrawer` | 1.50 MW |
| `MyObjectBuilder_Assembler/Extruder` | 1.50 MW |
| `MyObjectBuilder_Assembler/PlateStamp` | 1.50 MW |
| `MyObjectBuilder_Assembler/CementKiln` | 3.00 MW |
| `MyObjectBuilder_Assembler/SiliconFuser` | 3.50 MW |

IO implements its machinery as `Refinery` and `Assembler` subtypes, so the interface
classifier catches all of them as `Production` before the hint table is reached. Production and
Industrial carry the same tier in every policy row, so this is correct as well as convenient -
and it means the built-in catalog can stay empty for IO industry.

Also confirmed this run: `GENERATOR REMOVED Solar Panel 6` fired correctly (loss by absence);
solar panels at night report `MaxOutput=0.00` and are therefore excluded from available
generation without any special case; N-1 of 10.3 MW against a 50 MW steam turbine out of
60.5 MW available is right.

**Still open:** the 42 unknown consumers are 39 pistons plus one door, one sliding door and one
programmable block - four definitions, none of which publish any draw. They need measured
`[PowerControl.Catalog]` rows. No shedding, docking or Red Alert scenario has been run yet.

## v0.1.1 — first live evidence (2026-09-19)

First `scan` on an IO 1.7.7 test station (26 blocks, 14 grids, 1 construct, wind turbine +
steam turbine + one battery). Coverage came back at 30.4 % with 16 unknown consumers, and the
report exposed three defects that a synthetic test could not have.

**1. A measured zero was erasing a known rating (the serious one).**
The grav drill publishes `Required Input: 0.00 MW` while idle, with no `Max Required Input`
line. That parsed as max = 0 and **overwrote** the block's catalog or learned rating with
zero. Potential demand answers "what could start"; modelling every idle machine at zero
defeats the entire purpose of the script, because the incident it exists to prevent *is* idle
machines starting. `MaxIn` is now the best potential figure from any tier and a measurement
can never lower it. `CurIn` still carries the live draw, so shedding decisions are unaffected.

**2. The `Generation` category was silently deleting demand.**
`Measure` skipped every block categorised `Generation` on the grounds that producers are
supply, not demand. But the hint table maps boiler, steam, condenser and dynamo to
`Generation`, and those IO blocks are *consumers that support generation* — so their load was
dropped from potential demand entirely. The test is now what the block **is**
(`IMyPowerProducer`), not what it is called.

**3. IO steam plant was classified as ordinary Fuel.**
Live: the geothermal well head is `MyObjectBuilder_OxygenGenerator/GeothermalWellHead` and the
steam tank is `MyObjectBuilder_OxygenTank/SteamTank`. IO models steam as a gas, so both matched
`IMyGasGenerator`/`IMyGasTank` and landed on `Fuel` before the subtype hints ever ran. `Fuel`
is Essential, so they survived a Warning — but shedding the well head that feeds a 50 MW steam
turbine is how a load-shedding script causes the blackout it was preventing. A `GENSUP[]` table
(steam, geothermal, wellhead, boiler, condenser, superheat, feedwater, coolant) is now checked
**ahead of** the interface fallback and files these as `Generation`, which is Critical in every
policy row.

**Also in this build**

* `[PowerControl.Catalog]` section in the PB Custom Data: `SubtypeId=MW` or
  `TypeId/SubtypeId=MW` rows, re-read on every config parse, no recompile needed. Catalog
  outranks a measured figure, because a catalog row is a value the player measured deliberately.
* `scan` now prints a **ready-to-paste** `[PowerControl.Catalog]` block listing every unknown
  definition with `=0`. The zeros are deliberate: filling one in is a measurement the player
  takes, not a number this script will invent on their behalf.
* Dashboard reports **Unknown Types** alongside Unknown Consumers. The live run is 16 unknown
  blocks but only **3 definitions** (piston, sliding door, programmable block) — one is a
  crisis-shaped number, the other is a 30-second job.
* Empty-subtype guard on catalog lookup. Not theoretical: the test grid carries a
  `MyObjectBuilder_OxygenTank/` with no subtype at all, and an empty key would have matched an
  empty catalog row and rated every such block wrongly.

**Confirmed working against the live server**

* `DetailedInfo` parsing: air vent 0.10 MW, oxygen generator 1.00 MW, geothermal well head
  0.50 MW, all `src=detail`. Tier 1 of the model works on an English server.
* Generic producer discovery: the IO steam turbine is
  `MyObjectBuilder_HydrogenEngine/SteamTurbineMirrored` and was found with no subtype-specific
  code at all, at its true 50 MW capacity. The vanilla-name-list approach would have missed it.
* Steam *storage* and the well head are correctly **absent** from the producer list — no
  steam-producing block is counted as electrical generation.
* Construct grouping: 14 grids (13 piston subgrids) collapsed to 1 construct, named after the
  largest grid rather than a subgrid.
* N-1: 51.5 MW available − 50.0 MW steam turbine − 0.13 MW demand = 1.37 MW. A single
  generator failure leaves this base with 1.37 MW, which is exactly the contingency the metric
  is for.
* Cost: 868 of 50,000 instructions.

**Still open after this run**

* The 3 unknown definitions publish no draw at all (`detail: (empty)`, or pistons reporting
  only `Attached / Current position`). They need measured catalog rows; the script will not
  guess them.
* No IO *industrial* machinery (crushers, furnaces, refineries) was on the test grid, so the
  hint table and the learned tier are still unexercised against the blocks that matter most.

## v0.1.0 — first build (2026-09-19)

Milestones A–D of the v0.1 specification, in one pass. Compile-verified against
`tools/se_stubs.cs`; **not yet run on the live IO 1.7.7 server.**

**A — Discovery / diagnostics**
* Role auto-detection from combined signals (static flag, thrusters, gyros, controllers),
  overridable by config.
* Construct topology via `IsSameConstructAs`, grouped per grid rather than per block; home
  construct vs connector-attached constructs; constructs named after their largest grid.
* Producer, battery, connector, controller and consumer discovery, all generic through
  `IMyPowerProducer` / `IMyBatteryBlock` — no vanilla block-name list.
* Classification: interfaces first, then a ~90-row subtype/name hint table for IO machinery.
* `scan` / `scan all` diagnostic report written to the PB Custom Data below an owned marker,
  including a per-definition rollup and the raw `DetailedInfo` of every unknown block.

**B — Monitoring**
* Exact current generation, available generation, utilization, current demand and reserve from
  producer output; N-1 reserve against the largest single producer.
* Layered potential-demand model (config / detail / iface / catalog / learned / unknown) with
  published coverage. The IO catalog ships **empty** on purpose.
* Full battery accounting including recharge exposure, per construct and in total.
* Per-construct attribution, dashboard with four pages, 60-minute rolling history, event log.

**C — Policy**
* Role × mode policy table as data; categories and tiers separated from protection.
* Static protection by tag and by block Custom Data; runtime protection for locked connectors,
  locked gear, connected merge blocks, controllers, thrusters, gyros, life support, structural
  mechanicals and programmable blocks, re-validated immediately before every mutation.

**D — Load shedding**
* Tier-ordered shedding bounded by the current power condition, relief-ordered within a tier,
  `MaxShedPerCycle` per cycle.
* Hysteresis band, recovery delay, progressive one-at-a-time restoration in LIFO order, and a
  restore hold when the next item is bigger than the available margin.
* Shed state recorded per block with prior battery charge mode and persisted to `Storage`.
  Only blocks this script shed are ever switched back on.

### Defects found and fixed during the build

These were all caught in review before the first artifact; they are recorded because each one
would have been a live incident rather than a cosmetic bug.

1. **Rebuilding the block model every rescan discarded every measured value.** Coverage
   sawtoothed to zero every 30 s and, on a base large enough that one chunk cycle does not
   finish inside `RescanSeconds`, would never have converged at all. Values are now carried
   across the rescan and seeded from the catalogs at construction.
2. **A block measured at zero draw was treated as unmeasured** and fell back to its rated
   load. The engine would have switched off an idle refinery for a claimed 8 MW, received
   nothing, and immediately shed the next thing — over-shedding by construction. A measured
   zero is now a real zero, and only never-measured blocks fall back to a rating.
3. **A block reporting an Input line of zero was counted as an unknown consumer**, burying the
   blocks that actually are unknown in the coverage figure.
4. **Non-functional blocks were skipped before producer discovery**, so a destroyed or
   stripped generator simply vanished from the list — and a vanished generator raises no
   offline event. Producer identity now survives the rescan and removal is reported by absence.
5. **A battery-powered ship sat permanently in EMERGENCY** and would have shed itself down to
   life support, because a grid with no producers has zero available generation. Battery
   discharge capacity now counts as available generation on a ship (`CountBatteryDischarge`,
   `Auto` = yes on ships, no on stations) and "no producers" is only an emergency when the
   batteries are also nearly empty.
6. **New-grid events carried no numbers**, because per-construct aggregates do not exist until
   after `Measure`. An event saying "a grid appeared" without saying what it can draw is the
   report that lets the incident happen anyway. Events are now queued and emitted with load,
   potential load and recharge exposure.
7. **Restoration could re-open the deficit** by bringing back an item larger than the margin,
   which is the flap the hysteresis band exists to prevent. Restoration now holds and says so.
8. **`_storageLost` was never assigned**, so the warning it guarded could never fire — a flag
   that is structurally always false reads like a safeguard and is worse than none. Replaced
   with a startup event reporting what is actually knowable.
9. Shed candidates used a chunk-cycle-old current draw; the block's `DetailedInfo` is now
   re-read immediately before the mutation.
10. The LCD page tag was matched on `[PC-LCD]` exactly, so `[PC-LCD:grids]` never matched, and
    two contradictory page parsers ran in sequence.

### v0.1.0a - in-game compile fix

The first paste into a programmable block failed to compile. `MyDefinitionId.TypeId` is a
`VRage.ObjectBuilders.MyObjectBuilderType` **struct**, not a string: it is never null, has no
implicit string conversion, and `d.TypeId == null ? "" : d.TypeId` is both an ambiguous
operator and an impossible conditional. Two sites, both building the `TypeId/SubtypeId`
definition key. Corrected to `d.TypeId.ToString()`.

This is stub drift, not a script fault - `tools/se_stubs.cs` described `TypeId` as a string,
so `csc` accepted it and the GAME was the first thing to see the error, which is the exact
round trip `check_pb.py` exists to prevent. The stub now returns an opaque
`MyObjectBuilderType` struct that cannot be compared to null, and a negative control confirms
the old expression is rejected locally with CS0019. `MyItemType.TypeId` really is a string and
is unchanged - the two are different types.

### Tooling

* `tools/se_stubs.cs` gained the power API — `IMyPowerProducer`, `IMyBatteryBlock`,
  `ChargeMode`, `IMyJumpDrive`, `IMyShipController`, `IMyThrust`, `IMyGyro`,
  `IMyShipMergeBlock`, `IMyAirVent`, `IMyGasTank` and ~20 more block interfaces —
  `IMyTerminalBlock.DetailedInfo`, `IMyCubeGrid.IsStatic`, and a
  `SpaceEngineers.Game.ModAPI.Ingame` namespace for `IMyLandingGear` / `IMyOxygenFarm`.
  `IMyReactor` and `IMyCockpit` were widened to their real bases.
* `tools/check_pb.py` preamble gained `using SpaceEngineers.Game.ModAPI.Ingame;`, matching the
  game's own wrapper. IOPM v2.4.41 re-checks clean against the updated stubs.
