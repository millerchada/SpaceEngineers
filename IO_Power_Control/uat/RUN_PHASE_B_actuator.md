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
