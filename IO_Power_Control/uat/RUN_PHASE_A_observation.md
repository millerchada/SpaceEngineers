# UAT — Phase A, observation only

Script **v0.1.7**. `AutoShed=false` throughout. **Nothing is being tested for actuation here.**

This run exists because two stress detectors in a row fired on a healthy station, and both were
designed from reasoning rather than from data. The purpose now is to collect the data first.

## Rules for this run

* `AutoShed=false`. Do not enable it, whatever the dashboard shows.
* Do not change `BattStressMW`, `StoredDeclinePercent` or `StressHoldSeconds`. Their current
  values are a starting proposal and the point of the run is to find out whether they are right.
* Let the factory run its **normal cycles**. Do not stage an overload. A detector that fires on
  ordinary work is the failure mode being hunted.
* Leave the Ore Purifier in `SHED STATE`. It is evidence that shed state survived two recompiles.

## What should be true if Phase A is correct

| | Expected |
|---|---|
| `Stressed` | **False, continuously**, through every production cycle |
| `ShedAuthority` | `NONE` |
| Dashboard | `OBSERVATION ONLY` panel present (because `AutoShed` is false, this should *not* appear — see below) |
| Event log | **no** `ELECTRICAL STRESS` entries at all |
| `Condition` | `NORMAL` |
| `Capacity Risk` | `CRITICAL ?` or `WARNING ?` — with the `?`, meaning headroom is unproven |

Note the `OBSERVATION ONLY` panel only renders when `AutoShed=true`; with it false the panel is
absent and that is correct.

**Any `ELECTRICAL STRESS` event during ordinary cycling is a FAIL** and means the battery
thresholds are still wrong.

## Steps

### P1 — Deploy and let it settle

Paste v0.1.7, recompile, wait 2 minutes so the fast ring fills (60 samples at 1 Hz).

### P2 — Capture during normal cycling

Run `scan` while the factory is mid-cycle. Capture the whole report. The sections that matter:

    == FAST RING ==       60 seconds at 1 Hz
    == HISTORY ==         up to 15 minutes at 10 s
    == BROWNOUT ==        the telemetry, and whether anything is listed
    BattStressBar / held / storedDecline / ShedAuthority

### P3 — Capture again after several minutes

At least 5 minutes later, `scan` again. We want the history ring to span multiple full
production cycles, including at least one trough where WireDrawer and CementKiln are idle.

### P4 — What we are reading the data for

1. **Does `Stressed` stay False?** The primary result.
2. **How large and how long are the real battery excursions?** From the fast ring's `bnet`
   column. This is what decides whether 0.5 MW / 10 s / 0.5% are the right numbers, or whether
   they are still too tight or needlessly loose.
3. **Does `stored` actually fall during those excursions?** The fast ring shows both, so for
   the first time the question is answerable rather than arguable.
4. **What does the brownout column show during normal operation?** It should be **0** on a
   healthy base. Anything persistently non-zero is a block reporting `IsWorking=false` for a
   non-power reason, and each one has to be identified by name before this signal can ever be
   promoted to a shedding authority.
5. **Does `GenCredible` grow** as the factory demands more, and does it persist across the
   recompile this deployment causes? Check `GenProven` is not back at `GenCur` after the paste —
   that would mean the `P|` Storage records are not working.

## RESULT: PASS (v0.1.8, 2026-09-19)

Normal and heavy cyclic factory operation, roughly **5 MW through repeated 28 MW demand**,
including sustained 19-28 MW cycling.

| Check | Result |
|---|---|
| `Stressed` held False throughout | **PASS** |
| `ELECTRICAL STRESS` events | **none** |
| Brownout count | **0 throughout** |
| Battery | stayed full; heavy-load period showed `bflow=0.00 MW` |
| Generation followed demand | yes, without battery support |

The detector had nothing to fire on during the heavy period - generation tracked demand and the
battery never had to support the load - and it correctly fired on nothing. Earlier in the run
the same build saw repeated ~4.54 MW support pulses with no stress, no brownout and no
stored-energy decline; the v0.1.6 instantaneous 0.1 MW test would have triggered on every one.

**One defect found, advisory telemetry only:** `CapacityRisk` chattered `WARNING <-> CRITICAL`
every few seconds as instantaneous credible reserve crossed thresholds during ordinary cycles.
Every crossing was individually true, but the sequence was noise. Fixed in **v0.1.9** with an
asymmetric debounce; no protection behaviour was involved or changed.

**Also found and fixed in v0.1.8:** battery flow was reported in two opposite sign conventions,
so the same support pulse read +4.54 in the fast ring and -4.54 in the history of one scan.

## Next run

`AutoShed=false` still. Two objectives, in order:

1. **Confirm the chatter is gone** under unchanged factory cycling. `scan` now prints
   `CapacityRisk / Candidate / CandidateHeld` so the debounce can be read directly rather than
   inferred from the event log.
2. **Then force a real overload** - enough sustained load to drive genuine battery discharge -
   and watch the battery stress transition: `bout` above the bar, `held` climbing toward
   `StressHoldSeconds`, and `storedDecline` reaching what is required. This is the first time
   any stress signal will be asked to fire *positively*, and the first evidence about whether
   0.5 MW / 10 s / 0.5% are the right numbers.

## FULL PHASE A RESULT (v0.1.9, 2026-09-19) — AutoShed=false throughout

| # | Scenario | Result |
|---|---|---|
| 1 | CapacityRisk debounce under normal cycling | **PASS** |
| 2 | Normal cyclic production, incl. ~4.54 MW battery transients | **PASS** |
| 3 | Heavy real production ~27-32 MW at zero reserve | **PASS** |
| 4 | Controlled overload - 32 MW jump drive, 60-63 MW demand | **PASS** |
| 5 | Recovery after jump drive removal | **PASS** |
| 6 | Established stress during sustained deficit | **FAIL** → fixed v0.1.10 |
| 7 | Brownout after block deletion | **FAIL** → fixed v0.1.10 |

**1. Debounce.** One committed transition only:
`18:24:55 Capacity risk NORMAL -> WARNING, credible reserve 11.3 MW, N-1 -9.99 MW, held 2.33s`.
Held WARNING under normal cycling thereafter. No spam.

**3. Zero reserve is not stress.** Demand 32.0, GenCur 32.0, GenCredible 32.0, Reserve 0.00,
BattNetOut 0.00, Stored 3.00, `Stressed=False`, `Brownout=0`. Steam turbine ~25.9 MW, H2 ~2.6 MW.
The controller correctly waited for a demonstrated deficit rather than acting on arithmetic.

**4. First true positive.** Jump drive drew 32.0 MW, total demand 60-63 MW against ~58.3 MW of
saturated generation (steam 50.0, H2 5.0, wind+solar ~3.3). Battery supplied the deficit.

    Demand 60.0  GenCur 58.3  GenCredible 58.3  Reserve -1.72
    BattNetOut 1.72  BattStressBar 1.17  held 26.5/10.0s  storedDecline 0.02/0.02 MWh
    ShedAuthority=battery-drain  Brownout=0

    18:59:44  LOAD SPIKE to 60.0 MW (+29.0 MW)
    18:59:47  Capacity risk WARNING -> CRITICAL
    19:00:04  ELECTRICAL STRESS - batteries draining 1.71 MW for 19.8s, stored down 0.02 MWh
    19:00:04  Power condition NORMAL -> CRITICAL

Fast ring showed sustained 1.7-4.7 MW discharge, stored 3.00 -> 2.98 MWh.

**Latency finding:** ~20 s, not 10 s. The hold was satisfied first; the **stored-decline term
was the gating condition**. 0.5% of 3.00 MWh = 0.015 MWh, which at 0.5 MW takes 108 s and at
1.5 MW takes 36 s. Thresholds NOT retuned - one negative and one positive case is directional
evidence, not a distribution.

**5. Recovery.** Jump drive deleted, demand fell to ~28-31 MW, BattNetOut 0,
`19:03:32 Electrical stress cleared` / `Power condition CRITICAL -> NORMAL`. Battery finished at
2.78/3.00 MWh and was deliberately disabled afterwards to preserve the evidence.

**6. The defect.** See CHANGELOG v0.1.10 for the full diagnosis. In short: no latch, and a
single sub-bar tick reset both the hold timer and the stored-decline baseline.

**7. Brownout stale topology.** `br=1` for ~14 s after the jump drive was deleted. Confirmed
cause: `_bi` is rebuilt only by `Discover()` on the `RescanSeconds` boundary, so a deleted block
persists in the cache for up to 30 s. Good evidence that brownout must not become authority
from a single cached observation.

## v0.1.10 RE-TEST — PASS (2026-09-19, AutoShed=false)

Battery re-enabled from the preserved 2.78 MWh state, same temporary 32 MW jump drive
installed, overload allowed to run ~90 s, then the jump drive deleted.

| # | Check | Result |
|---|---|---|
| 1 | Positive detection | **PASS** |
| 2 | Stress latch through a sustained, varying deficit | **PASS** — the v0.1.9 failure |
| 3 | Recovery hold | **PASS** |
| 4 | Deleted-block brownout guard | **PASS** (see caveat) |

**Positive detection.** Demand ~60-63 MW against GenCur/GenCredible ~55.3-55.4 MW, battery
deficit ~4.2-7.7 MW.

    19:29:21  LOAD SPIKE to 60.0 MW (+29.0 MW)
    19:29:23  Capacity risk WARNING -> CRITICAL
    19:29:31  ELECTRICAL STRESS - batteries draining 4.71 MW for 10.5s, stored down 0.02 MWh
    19:29:31  Power condition NORMAL -> CRITICAL

**Latch.** ~90 s of sustained overload, fast-ring discharge varying 4.2-7.7 MW, **every sample
`S`**, no spurious clear/reassert cycle. Under v0.1.9 this same variation cleared the alarm.

**Recovery.** Jump drive deleted, load back to ~27-31 MW, discharge to zero:

    19:31:09  Power condition CRITICAL -> NORMAL
    19:31:25  Electrical stress cleared - drain under 0.55 MW for 15.2s
    19:31:25  Capacity risk CRITICAL -> WARNING ... held 15.2s

`Condition` recovered **immediately** with electrical reserve while `Stressed` stayed latched
for the full 15 s confirmation. That separation is the intended design: reserve is an instant
fact, recovery is a claim that has to hold.

Final: `Stressed=False`, `BattNetOut=0.00`, `BattRecoverBar=0.55`, `recoverHeld=26.8/15.0s`,
`ShedAuthority=NONE`. Battery ended ~2.60/3.00 MWh, then deliberately disabled to preserve the
post-UAT state. Base remains power-positive without it (7.22 MW required / 55.82 MW available).

**Brownout.** `confirmed=0, raw=0`. The transient `raw=1` was not reproduced on this deletion,
so the guard is proven only in the negative direction - nothing became confirmed. That is the
outcome that matters, but it is not a positive demonstration that the freshness rule fires.

---

## PHASE A: COMPLETE

Detection is proven in **both** directions on a battery-equipped station:

    negative   cyclic factory, ~4.54 MW transients, heavy production at zero reserve
    positive   32 MW step load, detected in ~10 s
    latch      held through 90 s of 4.2-7.7 MW varying deficit
    recovery   cleared 15 s after the deficit ended, not before

No known defects outstanding in the detection path.

### What Phase A did NOT prove

1. **Nothing has ever been shed.** The entire actuator path - candidate selection, live
   protection re-validation, hysteresis, staged restore - has never executed, except the
   accidental v0.1.5 shed of the Ore Purifier.
2. **One battery only.** Every battery figure has been exercised against a single 3 MWh /
   12 MW unit. The per-battery net clamping exists precisely for a mixed bank where one
   battery charges while another discharges, and that case is entirely untested.
3. ~~**The stored-decline term does not scale.**~~ **FIXED in v0.1.11.** The leg was
   `(StoredDeclinePercent/100 x MaxStored) / deficit` - ~11 s on this 3 MWh bank at 4.71 MW,
   but **18 minutes** on a 300 MWh bank at 5 MW. Replaced with a scale-free consistency test:
   the observed decline must reach `max(MinDeclineMWh, DeclineConsistency x expected)`, where
   expected is the measured discharge integrated over the window. Computed timings: 10.0 s for
   the UAT case (was 11.5 s), 10.0 s for a 300 MWh bank at 5 MW (was 1080 s).
   **Needs re-verification in game** - see below.
4. **Brownout freshness fires correctly** - unproven in the positive direction.
5. Docking, Red Alert, ship role, restart-with-loads-shed: untouched.

### Outstanding state

The Ore Purifier is still in `SHED STATE` from the v0.1.5 false shed. It has now served its
purpose as persistence evidence across four recompiles and should be cleared with
`run "recover"` before any armed test, so the shed list starts empty.

## v0.1.11 DETECTOR RE-VERIFICATION — PASS (2026-09-19, AutoShed=false)

Two jump drives installed, giving a far deeper deficit than any previous run.

**Entry.** Demand 71.0 MW against GenCredible 59.0 MW, `BattNetOut` 12.0 MW, Reserve -12.0 MW.

    20:19:40  ELECTRICAL STRESS - batteries draining 12.0 MW for 10.5s,
              stored down 0.04 of 0.04 MWh expected
    DeclineExpected=0.04 MWh   required=0.02 MWh   actual exceeded required

Every figure reconciles against the new semantics: 12.0 MW x 10.5 s = 0.0350 MWh expected
(displays as 0.04), required 0.5 x that = 0.0175 (displays as 0.02), and the bar was
max(0.5, 0.02 x 59.0) = 1.18 MW with a recovery bar of 0.59 MW - exactly as reported.

**The gating term has moved, which is the point of the change.** At 12 MW the *hold* was the
binding constraint at 10.5 s, not the decline - the first run in which that is true. Under the
old capacity-share rule the decline was always the bottleneck. The leg now confirms the drain
rather than rate-limiting the detector.

**Latch.** Held continuously through the sustained 12 MW deficit. No false clear/reassert.

**Recovery.** One jump drive removed, one left installed. Discharge fell immediately to zero
while stress stayed latched for the full confirmation:

    20:20:58  Electrical stress cleared - drain under 0.59 MW for 15.2s
    20:20:58  Capacity risk CRITICAL -> WARNING
    20:20:58  Power condition WARNING -> NORMAL

Final: `Stressed=False`, `BattNetOut=0.00`, `Condition=NORMAL`, `CapacityRisk=WARNING`,
brownout `confirmed=0 raw=0`. Condition/CapacityRisk separation correct again - the remaining
jump drive leaves real headroom thin, which is a risk, not a condition.

---

# PHASE A DETECTOR: ACCEPTED

**Tagged `IO_Power_Control-v0.1.11` as the accepted detector baseline.**

Proven across three independent overload runs and extended cyclic operation:

| | |
|---|---|
| False-positive resistance | cyclic factory, ~4.54 MW transients, heavy production at zero reserve |
| Positive detection | 32 MW step (10.5 s), 32 MW repeat, 12 MW sustained deficit (10.5 s) |
| Latch | held through 90 s at 4.2-7.7 MW, and through a sustained 12 MW deficit |
| Recovery | cleared 15 s after the deficit ended, never before |
| Scale independence | decline leg no longer the gating term at a large deficit |
| Brownout | never a confirmed false positive, including across a block deletion |

No known defects in the detection path.

## Next: Phase B, the actuator

See `RUN_PHASE_B_actuator.md`. Nothing in the actuator path has ever executed.

## Next run (v0.1.11) — re-verify the detector, still AutoShed=false

The v0.1.10 evidence was gathered against the old third leg. The computed timing says the test
base should behave identically (10.0 s vs a measured 10.5 s), but that is a calculation, not an
observation, and the entry path has changed.

Re-run the jump-drive overload once more and confirm:

* entry still fires at roughly the same moment, ~10 s after the drain starts;
* `DeclineExpected`, `required` and the actual decline are all visible in `scan` and move as
  expected during the window;
* the latch still holds through the varying deficit;
* recovery still clears ~15 s after removal.

Only then is the detector worth checkpointing as the basis for arming the actuator.

## Superseded run notes (v0.1.10)

`AutoShed=false` still. Re-run scenario 4 - install the jump drive again - and confirm:

* stress **latches** through the whole deficit with no spurious clear, however the deficit varies;
* `recoverHeld` appears in `scan` and counts only while the drain is genuinely low;
* stress clears ~15 s after the jump drive is removed, not instantly - the delay is the latch
  working, not a fault;
* brownout `raw` may briefly show 1 on deletion, but `confirmed` must stay **0**.

## Results



| Capture | Time | Stressed seen? | Brownout count | Max bnet / duration | stored fell? | Notes |
|---|---|---|---|---|---|---|
| P2 | | | | | | |
| P3 | | | | | | |

## Decision this run feeds

Only after the above:

* whether the battery thresholds move, and to what, **from the observed distribution**;
* whether brownout is trustworthy enough to become a shedding authority in Phase B;
* whether a battery-less grid can be given any authority at all, or must stay observation-only.

Nothing is promoted on the strength of an argument this time.
