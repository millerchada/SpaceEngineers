# UAT run — station shedding and recovery

Script **v0.1.5** (`bedfa87`). Pre-UAT checkpoint `b02fd6a`. IO 1.7.7 test station, survival.
Station only — **no docked ship in this run.**

Supersedes the first draft of this sheet, which created the deficit by switching the steam
turbine off. That is no longer necessary: v0.1.5 measures reserve against **credible** capacity,
so the base can reach its own real ceiling under load and the test is faithful to the incident.

## Baseline, and what v0.1.5 should now report

Live figures before the ramp (from the 16:xx capture that triggered the v0.1.5 fix):

    GenCurrent     14.5 MW
    GenCredible    19.4 MW    wind 3.63 + solar ~0.9 at MaxOutput; steam 13.5 + H2 1.35 proven
    GenNameplate   58.6 MW
    Demand         14.5 MW
    Credible Reserve  4.9 MW  (v0.1.4 reported 44.1 MW against nameplate)
    N-1              -8.6 MW  (19.4 - 13.5 largest credible - 14.5)

Predicted dashboard at rest:

    Condition        NORMAL       not stressed: batteries net 0, reserve 4.9 >= shed 4
    Capacity Risk    WARNING      reserve <= 8 MW, and N-1 negative
    RestoreThreshold 15.4 MW      capped from 20 to stay reachable (19.4 - 4)

**A permanent WARNING on Condition would be a FAIL.** That separation is the whole point of the
second half of the fix.

## Known caveat to watch for — `proven` can capture a burst

The steam turbine holds a 100 L steam buffer. Under a sudden load it can briefly deliver more
than the well sustains, and `proven` is a high-water mark, so it would record the burst and
credit it permanently. **Credible may therefore overstate a fuel-buffered producer after a
transient.**

Watch for this at step 5: if the turbine peaks well above ~26 MW and then settles back, record
both figures. It does not invalidate the fix — credible is still far below nameplate — but it
is a real limit of "witnessed output" as evidence, and it decides whether a future version needs
a sustained-output measure rather than a peak.

## Derived expectations

Steam ceiling from the mod source (MOD_DEFINITIONS.md), well at 1378.9 m, no interference:

    sustained MW = 0.024 x (1378.9 - 300) = 25.9 MW

So credible capacity should converge on roughly:

    wind ~3.6 + solar ~1.0 + steam ~25.9 + hydrogen up to 5.0   =  ~35.5 MW

Available load on this base:

    production 18.5 MW rated + resting ~2 MW + battery recharge 12 MW  =  ~32.5 MW

**32.5 MW of load against ~35.5 MW of credible capacity is not quite a deficit.** If the ramp
tops out without stress appearing, switch the **hydrogen engine** off to drop credible by ~5 MW.
That is the planned escalation, not an improvisation.

---

## Steps

Record for each: setup, expected, actual, dashboard values, event lines, PASS/FAIL, defect.

### R0 — Control for the "only restores what it shed" test

Before anything else, **manually switch one assembler off yourself** (WireDrawer suggested) and
note which. It must never be touched by the script.

### R1 — AutoShed off, agreement check

Set `AutoShed=false`. Confirm dashboard against the game's power panel:

* `Current` vs the game's total output
* `Nameplate` vs the sum of online producer maxima — expect ~58.6 MW
* `Credible` — expect ~19.4 MW, **and this is the number protection now spends**
* `Reserve Credible` vs `vs Nameplate` — expect ~4.9 and ~44.1; the gap is the defect that was fixed
* `Condition NORMAL`, `Capacity Risk WARNING`
* Run `scan`: check `Stressed=False`, `FuelAtCeiling`, `CredFlatFor`, `RestoreThreshold`

### R2 — Ramp demand, in steps, pausing 30 s

Start production progressively — roughly 3 MW at a time. Suggested order: Blast Furnace,
CementKiln, SiliconFuser, LargeRefinery, then the smaller assemblers.

* **Expect:** as each machine starts, the steam turbine is asked for more, its `proven` rises,
  and **`Credible` rises with it**. Reserve should stay roughly flat rather than collapsing.
* **This is the core proof that the model grows with demonstrated output** and does not shed a
  base merely for being lightly used.
* **Expect no shedding** — `AutoShed` is still false, and `Stressed` should remain False while
  credible keeps rising.

### R3 — Enable AutoShed partway up

With perhaps half the production running, set `AutoShed=true`.

* **Expect:** nothing happens. Credible is still rising, so `Stressed=False`.
* **FAIL if:** anything sheds while credible capacity is still growing.

### R4 — Push through the steam ceiling

Continue starting production. If needed, set the battery to **Recharge** (+12 MW), then switch
off the **hydrogen engine** (−5 MW credible).

* **Expect:** the steam turbine plateaus near ~26 MW, `proven` stops rising, `Credible` goes
  flat, `CredFlatFor` starts counting.

### R5 — Stress becomes true

* **Expect:** `ELECTRICAL STRESS` event, naming either "batteries carrying X MW" or "generation
  at demonstrated ceiling". `Condition` leaves NORMAL. Battery goes net-discharging.
* Record the **credible capacity and demand at the moment stress appears.**

### R6 — Shedding operates at the real limit

* **Expect:** shedding begins with demand somewhere near **30–35 MW**, not near the 58.6 MW
  nameplate. Production sheds largest-draw-first, at most 4 per cycle, and stops once credible
  reserve is back above 4 MW.
* **This is the headline result of the whole fix.** Under v0.1.4 nothing would have shed at all.
* **FAIL if:** shedding only begins near nameplate, or never begins.

### R7 — Protection held

`scan all`. Every one of these still `enabled=True`:

    Air Vent, Medical Room                LifeSupport, runtime-protected
    Programmable Block x2                 Control, runtime-protected
    Geothermal Wellhead, Steam Buffer Tank  Generation - feeds the turbine
    Small Hydrogen Tank                   Fuel
    Compact Radio Antenna                 Comms
    WireDrawer (your R0 block)            must remain off, and absent from SHED STATE

### R8 — Recovery holds

Leave it shed for 2 minutes.

* **Expect:** nothing restored while credible reserve is below `RestoreThreshold` (printed by
  `scan`). A `Restore held:` event is acceptable and informative.

### R9 — Release the overload

Take the battery off Recharge, and/or restart the hydrogen engine.

* **Expect:** `Generator returned` if the engine comes back, **and its `proven` resets to 0** —
  it must re-prove itself. Credible dips then recovers as it delivers again.

### R10 — Staged restoration

* **Expect:** 30 s of healthy reserve, then **one item per 10 s**, LIFO so the last shed comes
  back first. Two or more `RESTORED` events with ~10 s spacing.
* **FAIL if:** everything restores in one cycle, or restoration starts before the delay.

### R11 — Only script-shed blocks restored

* **Expect:** `Currently Shed` returns to 0 with your R0 assembler **still off**, and never
  named in any `RESTORED` event.

---

## Results

| Step | Pass/Fail | Notes |
|---|---|---|
| R0 | | |
| R1 | | |
| R2 | | |
| R3 | | |
| R4 | | |
| R5 | | |
| R6 | | |
| R7 | | |
| R8 | | |
| R9 | | |
| R10 | | |
| R11 | | |

## Deferred to later runs

Locked-connector protection, docked-ship detection, docked battery recharge exposure,
docked-ship shedding, Red Alert reprioritisation, PB recompile with loads shed, and world
restart persistence.
