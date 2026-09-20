# UAT — Phase B, the actuator

Baseline: **`IO_Power_Control-v0.1.11`**, the accepted Phase A detector.

Phase A proved the script can tell a real deficit from a busy factory. Phase B asks a different
question: **when it acts, does it act correctly?** Nothing in the actuator path has ever
executed — candidate selection, live protection re-validation, hysteresis, staged restore. The
single exception is the accidental v0.1.5 shed of the Ore Purifier, which was a defect rather
than a test.

The detector is deliberately not being touched again unless Phase B exposes a defect in it.

---

## B0 — Restore path, in isolation. `AutoShed=false`

The Ore Purifier has been in `SHED STATE` since v0.1.5, surviving five recompiles. It has
served its purpose as persistence evidence and is now the subject of the first restore test.

    run "recover"

* **Expect:** the Ore Purifier is re-enabled, one `RESTORED` event names it, `Currently Shed`
  goes to 0, and `== SHED STATE ==` reads `(nothing shed)`.
* **Check afterwards:** recompile the PB and confirm the shed list stays empty — persistence
  must work in both directions, and an entry that comes back from the dead would be a defect.
* **FAIL if:** anything other than the Ore Purifier changes state.

This must pass before anything is armed. It is the only way to test restore without first
having to trust shedding.

## B1 — Arming, with the smallest possible blast radius

    AutoShed=true
    MaxShedPerCycle=1

One block per cycle. The first actuation should be small enough to read in full: what was
picked, why, and what it was worth.

**Re-enable the battery first.** Battery drain is the only shedding authority — with the
battery disabled, `AutoShed=true` can never fire and the test would silently prove nothing.
Expect the bank to draw up to 12 MW recharging from ~2.60 MWh, which is itself worth watching
on the dashboard as recharge exposure becoming real load.

Before the overload, capture a baseline `scan` and note which production blocks are running and
what each is drawing — the engine sorts candidates by *current* draw, so what it picks is
predictable from that list and should be checked against it.

## B2 — First shed

Install one jump drive. Same overload as Phase A.

* **Expect:** detector fires as before, then a single `shed <name> [Production] -X MW`.
* **The block chosen should be the largest current draw** among sheddable Production/Industrial
  candidates — not the largest *rated*, since an idle machine yields nothing.
* Reserve recovers by roughly the shed block's draw.
* **Shedding then STOPS.** One block per cycle, and it should not continue once credible
  reserve is back above `ShedReserveMW`.
* **FAIL if:** more than one block per cycle; anything outside Production/Industrial;
  an idle machine shed for zero relief; shedding continues after reserve recovers.

## B3 — Protection, under a real shed

While shed, `scan all`. Every one of these must still be `enabled=True`:

    Air Vent, Medical Room          LifeSupport, runtime-protected
    Programmable Block x2           Control, runtime-protected
    Geothermal Wellhead             Generation - feeds the turbine
    Steam Buffer Tank               Generation
    Small Hydrogen Tank             Fuel
    Compact Radio Antenna           Comms

Shedding the well head would starve the steam turbine — the script causing the blackout it
exists to prevent. This is the single most important negative result in Phase B.

## B4 — Staged restore

Remove the jump drive.

* **Expect:** stress clears after the 15 s recovery hold, then restoration waits
  `RecoverySeconds` (30 s) before the first item and `RestoreStepSeconds` (10 s) between each.
* With one block shed there is only one restore, so **run B2 again with two or three blocks
  shed** (raise `MaxShedPerCycle` to 2 once the single case is clean) to see the staging.
* **FAIL if:** everything restores in one cycle, or restoration begins before the delay.

## B5 — Only what the script shed

Before B2, manually switch one assembler off yourself and note which.

* **Expect:** never enabled by the script, never in `== SHED STATE ==`, and `Currently Shed`
  returns to 0 with your block still off.

## B6 — Persistence across a recompile, with loads shed

While blocks are shed, recompile the PB.

* **Expect:** startup event reports N shed entries recovered from Storage; the same blocks —
  and only those — restore later.

---

## Known gaps Phase B does not close

* **One battery only.** Every battery figure has been exercised against a single 3 MWh / 12 MW
  unit. The per-battery net clamping exists for a mixed bank where one battery charges while
  another discharges; that case is still untested.
* **Brownout freshness is unproven positively** — no transient `raw=1` has been reproduced.
* Docking, Red Alert, ship role: Phase C.
* No steam/H2 sustainable-capacity model.

## B2 FIRST ARMED RUN (v0.1.11) — FAIL, fixed in v0.1.12

`AutoShed=true`, `MaxShedPerCycle=1`. Two empty jump drives charging at 32 MW each.

A **12 MW deficit cost five blocks, 47.5 MW of load**, shed one per second. Two defects:

1. `MaxShedPerCycle` caps one `DoShed` call and `ShedStep` runs every tick, so the cap was one
   block per *second*. No settling interval.
2. Reserve never improved between actions - the fast ring shows demand pinned at 71.7 MW and
   `bout` at 12.0 MW throughout, because the charging jump drives absorbed every megawatt
   freed. Four of the five sheds achieved nothing measurable.

Also observed: `Jump Drive 9` is tier Discretionary and should sort **first** - and did in the
earlier single-drive episode at 20:29:13. Here it went last. Suspected cause is `ShedOne`
refusing it on a near-zero live `DetailedInfo` read and the loop silently falling through.
**Instrumented in v0.1.12 rather than fixed by assumption.**

Restoration behaved correctly and was not changed:
`Restore held: Jump Drive 9 needs 32.0 MW, margin 18.1 MW` is the per-block fit check working.

## B2r — RE-RUN ON v0.1.12

Same setup: `AutoShed=true`, `MaxShedPerCycle=1`, `ShedSettleSeconds=5`, two empty jump drives.

**First clear the shed state** from the failed run - `run "recover"` - so the episode starts
with an empty list.

Expected event sequence, which is the whole point of the change:

    LOAD SHEDDING 1 item(s), X MW relieved
      settling 5.0s before re-measuring
    Deficit persists after settle: draining X, was Y at the last action
    ... or ...
    SHEDDING STOPPED - drain X below bar Y after N action(s)
    Shed episode ended after N action(s), reserve X

| # | Check | Expected |
|---|---|---|
| 1 | Actions are >= 5 s apart | timestamps in the event log |
| 2 | Total blocks shed is far fewer than five | |
| 3 | The ordering question is answered | `== CANDIDATES REFUSED ==` either names Jump Drive 9 with a near-zero live draw, or it does not and the cause is something else |
| 4 | Post-settle decision is explicit | `Deficit persists` or `SHEDDING STOPPED` appears, not silence |
| 5 | Shedding stops on improvement | once `bout` < bar, no further sheds **even though `Stressed` stays latched** |
| 6 | Elastic load is visible | if the jump drives keep absorbing freed power, `drainNow` vs `atLastAction` shows it |
| 7 | Protection held | B3 list still enabled |
| 8 | Restoration unchanged | staged, per-block fit check still holds oversized items |

If check 3 shows Jump Drive 9 refused for a near-zero live read while it was demonstrably
drawing 32 MW, that is a **new defect in the fresh-read guard** and should be reported before
any further shedding work - it would mean the guard is misreading a block class rather than
protecting against idle machines.

## B2r RESULT (v0.1.12) and v0.1.13 LIVE REGRESSION — PASS, ACCEPTED

`AutoShed=true`, `MaxShedPerCycle=1`, `ShedSettleSeconds=5`, two fresh empty jump drives.

    20:59:56  ELECTRICAL STRESS - batteries draining 12.0 MW for 10.5s
    20:59:56  shed Jump Drive 10 [Jump] -24.6 MW
    20:59:56    settling 5.00s before re-measuring
    20:59:57  Shed episode ended after 1 action(s), reserve 12.0 MW
    21:00:12  Electrical stress cleared - drain under 0.59 MW for 15.2s

| # | Check | Result |
|---|---|---|
| 1 | Actions paced, not per-tick | **PASS** - only one action occurred |
| 2 | Far fewer blocks than five | **PASS** - one block, 24.6 MW vs five blocks, 47.5 MW |
| 3 | Ordering question | **NOT REPRODUCED** - jump drive selected first, refusals empty |
| 4 | Post-settle decision explicit | **PASS** - settle notice then episode end |
| 5 | No second action while `Stressed` latched | **PASS** - drain 12 -> 0, nothing further shed |
| 6 | No production shed | **PASS** |
| 7 | Refused candidates | **PASS** - none |

**v0.1.13 live regression: PASS.** Same behaviour with the stateless live-drain rule in place.

**The actuator authorisation defect is accepted as fixed.**

### Still unexplained

The v0.1.11 five-block ordering anomaly did **not** reproduce. Jump Drive 10 was selected first,
exactly as the sort predicts, and `== CANDIDATES REFUSED ==` was empty. So the cause of
production being chosen ahead of a tier-Discretionary jump drive in that run remains unknown -
it was not demonstrated to be the `ShedOne` fresh-read guard. The instrumentation stays in
place. Watch for it during B4, which sheds several blocks.

### Known telemetry scoping wrinkle (cosmetic, not blocking)

`atLastAction` is **not** scoped to the episode, while `actionsThisEpisode` is, so an idle
scan reads:

    ShedPhase=idle actionsThisEpisode=0 ... drainNow=0.00MW atLastAction=12.0MW

which invites reading "12.0 MW at the last action" as belonging to an episode that reports zero
actions. `_battOutAtShed` is display-only and safe to scope to the episode. The settle timer
must **not** be reset alongside it - it deliberately survives the episode boundary so a new
deficit seconds later is still paced.

The episode-end event could also say that it supersedes a pending settle, which is what made
the 20:59:56/57 sequence look wrong at first reading.

## Phase B remaining

B2/B3 are covered for the jump-drive case only. Still outstanding, and none of them have run:

* **B0** restore in isolation - `run "recover"`. Jump Drive 10 is still shed.
* **B3** protection under a shed that actually reaches production tiers.
* **B4** staged restore with several blocks shed.
* **B5** only script-shed blocks restored.
* **B6** persistence across a recompile with loads shed.

Note on B0/B4 timing: Jump Drive 10 claims 24.6 MW of relief, and restoration requires the
block to *fit* - `margin = reserve - ShedReserveMW`. At 53.5 MW demand the reserve is ~5 MW, so
it will correctly report `Restore held` until the factory idles and reserve climbs past roughly
28.6 MW. That is the per-block fit check working, not a stall.

## B6 — PERSISTENCE ACROSS A RECOMPILE, USING THE v0.1.15 UPGRADE ITSELF

The upgrade is the test. `Jump Drive 13` is currently script-shed and must stay that way across
the recompile: its claimed relief is 28.5 MW and restoration requires the block to *fit*
(`margin = reserve - ShedReserveMW`), so with reserve around 13 MW it cannot come back yet.
That makes this a real persistence test rather than a contrived one - the correct outcome is
"nothing happens", which is the hardest kind to verify without evidence.

Paste `IO_Power_Control_v0.1.15.min.cs`, recompile, then `scan`.

| # | Expected | Where to look |
|---|---|---|
| 1 | Shed ownership restored from Storage | startup event `Recovered 1 shed entries from Storage`, NOT `No shed state in Storage` |
| 2 | `Jump Drive 13` still disabled and listed | `== SHED STATE ==` shows it with `relief=28.5MW` |
| 3 | Not accidentally restored | no `RESTORED` event; `Restore held: Jump Drive 13 needs 28.5 MW, margin ...` is the correct outcome |
| 4 | Its draw reports 0, not the stale pre-shed figure | `1x .../LargeJumpDrive ... cur=` should drop by ~28.5 MW versus v0.1.13, and `loadEst` should fall toward real demand |
| 5 | `atLastAction` scoped for an idle episode | `ShedPhase=idle actionsThisEpisode=0 ... atLastAction=0.00MW` |
| 6 | No other block changes state | compare the enabled set before and after |

**Note on check 1.** The startup line distinguishes the two cases explicitly, and this is the
first time that branch will have been exercised with a non-empty list - every prior recompile
in this project reported `No shed state in Storage`. If it reports that again while
`Jump Drive 13` is still off, persistence is **not** working and the block has been orphaned:
the script would no longer know it owns it, and would never restore it. That is a FAIL and
should stop Phase B.

**Note on check 4.** The other jump drive is still charging, so the rollup will not read zero -
it should read roughly one drive's draw instead of two.

## B6 RESULT (v0.1.15) — PASS

Immediately before the recompile the script owned three shed blocks:

    Advanced Assembler 2
    Ceramics Furnace
    Jump Drive 15

After recompiling the same v0.1.15 build, startup reported **`Recovered 3 shed entries from
Storage`** and `== SHED STATE ==` still contained all three.

| # | Check | Result |
|---|---|---|
| 1 | Shed ownership restored from Storage | **PASS** - 3 entries, not `No shed state in Storage` |
| 2 | Blocks still disabled and listed | **PASS** |
| 3 | Not accidentally restored | **PASS** - no `RESTORED` on startup |
| 4 | Disabled block reports 0 draw | **PASS** - `2x JumpDrive cur=32.0MW`, was ~60 MW with one shed |
| 5 | `atLastAction` scoped | **PASS** - `ShedPhase=idle actionsThisEpisode=0 atLastAction=0.00MW` |
| 6 | No other block changed state | **PASS** |

This is the first recompile in the project to exercise the non-empty branch of shed-state
recovery. Both v0.1.15 presentation fixes are confirmed live.

`recover` has **not** been run; those three blocks remain script-owned and shed.

### v0.1.15 cadence — PASS

    21:22:49  shed Advanced Assembler 2 [Production] -4.00 MW
    21:22:55  shed Ceramics Furnace     [Production] -3.50 MW
    21:23:01  shed Jump Drive 15        [Jump]      -30.6 MW

One action at a time, 5-6 s apart, `Deficit persists after settle: draining 12.0 MW, was
12.0 MW at the last action` between each, drain to 0 after the jump drive, episode ended after
3 actions, stress cleared after the normal 15.2 s hysteresis. Settle pacing and live-drain
re-authorisation both behaved exactly as designed.

## BLOCKING: candidate ordering anomaly reproduced, root cause NOT yet proven

Jump is Discretionary (tier 5), Production is Industrial (tier 4), so an actively charging jump
drive should be selected first. It was selected **third**, and
`== CANDIDATES REFUSED ==` was **empty**.

The v0.1.9 hypothesis - that `ShedOne` refused it on a fresh near-zero read - is **disproved**
by that empty list. See the review recorded in CHANGELOG under v0.1.16 for where the ordering
is actually lost. In short: `Relief(r) <= 0.01` excludes a block **before** `_cand.Add`, so an
excluded block never reaches `ShedOne` and no refusal can exist for it. The refusal telemetry
is downstream of the exclusion and structurally cannot observe this.

**No behavioural change until the candidate audit proves the mechanism.**

## Results

| Step | Pass/Fail | Notes |
|---|---|---|
| B0 restore in isolation | | |
| B1 arming | | |
| B2 first shed | | |
| B3 protection held | | |
| B4 staged restore | | |
| B5 only script-shed restored | | |
| B6 persistence with loads shed | | |
