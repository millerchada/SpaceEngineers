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

## Next run (v0.1.10)

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
