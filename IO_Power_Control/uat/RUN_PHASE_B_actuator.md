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
