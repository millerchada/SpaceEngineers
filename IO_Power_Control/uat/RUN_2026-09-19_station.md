# UAT run — station shedding and recovery

Script **v0.1.4**, checkpoint `b02fd6a`. IO 1.7.7 test station, survival mode.
Station only. **No docked ship in this run.**

## Why the deficit is made on the generation side

With the steam turbine online, `Available` reads the nameplate **59.9 MW** while the entire
drawable load of this base is about 30 MW (18.5 MW of rated production + 12 MW of battery
recharge). Reserve cannot get near the 4 MW shed threshold, so **automatic shedding could not
fire at all** — not because the engine is broken, but because the metric it tests is a rating
rather than a capability. That is limitation 6, and it is the single largest open risk for
running `AutoShed=true` on the production server.

Switching the steam turbine off makes `Available` collapse to what wind, solar and the hydrogen
engine can really deliver, which is a figure the base *can* exceed. It is also the exact
incident class this script exists for — a generator lost while industry is running — so the
test is more faithful this way, not less.

**No configuration changes are needed.** Every threshold stays at its default; only `AutoShed`
is toggled, which is what step 1 asks for. That matters: a test that only passes under
hand-tuned thresholds proves less than one that passes under the shipped ones.

## Baseline (from the 16:17:46 scan, v0.1.4)

    Available (nameplate)   59.9 MW     Steam turbine 50.0 + hydrogen 5.0 + wind ~3.8 + solar ~1.1
    Real steam capacity     25.9 MW     well at 1378.9 m: 0.024 x (1378.9 - 300) = 25.9
    Battery                 3.00 MWh, 12.0 MW max in/out
    Resting demand          ~2.0 MW     blast furnace 2.00 + antenna 0.02
    Production available    18.5 MW rated, idle

Rated production, from the live scan (all `src=detail`):

    SiliconFuser 3.5 | CementKiln 3.0 | LargeRefinery 3.0 | Blast Furnace 2.0
    LargeAssemblerNew 1.5 | WireDrawer 1.5 | Extruder 1.5 | PlateStamp 1.5 | Fabricator 1.0

Blocks that MUST NOT be shed at any point in this run, and why:

    Air Vent, Medical Room          LifeSupport  - Critical tier AND runtime-protected
    Programmable Block x2           Control      - Critical tier AND runtime-protected
    Geothermal Wellhead             Generation   - Critical; feeds the turbine
    Steam Buffer Tank 2             Generation   - Critical; feeds the turbine
    Small Hydrogen Tank             Fuel         - Essential
    Compact Radio Antenna           Comms        - Essential
    Sliding / Hatch doors           Decor        - Discretionary BUT zero measured draw,
                                                   so zero relief, so never a candidate

## Predicted behaviour, derived before the run

Derived from the policy table and the shipped thresholds, so a mismatch is a real finding
rather than a moved goalpost.

| Phase | Available | Demand | Reserve | Condition | Deepest tier | Expected |
|---|---|---|---|---|---|---|
| S2 baseline | 59.9 | ~2.0 | ~57.9 | NORMAL | Discretionary | nothing |
| S3 production on | 59.9 | ~10.5 | ~49.4 | NORMAL | Discretionary | nothing shed, `AutoShed=false` |
| S5 turbine off | ~9.3 | ~10.5 | ~-1.2 | **CRITICAL** | **Normal** | shed begins |
| S6 after shed | ~9.3 | ~4.0 | ~5.3 | WARNING | Industrial | shedding STOPS |
| S10 turbine on | 59.9 | ~4.0 | ~55.9 | NORMAL | Discretionary | recovery timer starts |

Shed arithmetic for S6, to check the engine against: target is
`ShedReserveMW - reserve + 1` = `4 - (-1.2) + 1` = **6.2 MW**. Candidates are all Production
(Industrial tier), sorted by relief descending, so SiliconFuser (3.5) then CementKiln (3.0)
= 6.5 MW clears it and the engine should stop at **two items**, not four.

Restore threshold is 20 MW, unreachable while the turbine is off (Available 9.3) — so recovery
must **hold** through S6-S9 and only begin once generation returns. That is deliberate: it
tests the hold as well as the release.

---

## Steps

Record for each: setup, expected, actual, dashboard values, event lines, PASS/FAIL, defect.

### S1 — AutoShed off

Set `AutoShed=false` in the PB Custom Data. Recompile is not needed; it is re-read on the next
rescan (≤30 s).

* **Expect:** dashboard unchanged, no shed activity possible.

### S2 — Metrics agree with the game

Compare dashboard against the game's own power panel and the terminal:

* `GENERATION / Current` vs the game's total output
* `Available` vs the sum of online producer maxima
* `DEMAND / Current` vs the game's consumption
* `BATTERY / Stored`, `Net Flow`, `Recharge Exposure` vs the battery terminal
* `Proven` — expect it to be at or just above Current, since it is a running high-water mark
* Run `scan`; confirm `Reserve = Available - Demand` and `N-1 = Available - largest - Demand`

* **Expect:** all agree within rounding. Coverage ~77 %, 5 unknown in 4 definitions.

### S3 — Production load rises sensibly

Start **SiliconFuser, CementKiln and Blast Furnace** (already running) with real work queued.

* **Expect:** demand climbs toward ~10.5 MW. Each machine shows `cur=` near its `max=` in
  `scan`'s `== CONSUMERS BY DEFINITION ==`. Possibly one `LOAD SPIKE` event — and **only one**,
  not a repeat per cycle.
* **Nothing is shed** — `AutoShed` is still false. This step proves the engine is inert when
  told to be.

### S4 — Enable AutoShed

Set `AutoShed=true`. Nothing should happen yet: reserve is ~49 MW.

* **Expect:** no shedding, no events. A script that sheds here has a threshold defect.

### S5 — Create the deficit

**Switch the steam turbine OFF** in the terminal.

* **Expect:** `GENERATOR OFFLINE Steam Turbine - Mirrored -50.0 MW` event. Available drops to
  ~9.3 MW. Reserve goes negative. Condition walks to **CRITICAL**. Battery begins net discharge.

### S6 — Shedding

* **Expect:** `LOAD SHEDDING 2 item(s)` with two `shed <name> [Production] -X MW` lines,
  **largest draw first** (SiliconFuser then CementKiln). Demand falls to ~4 MW, reserve to
  ~+5.3 MW, and **shedding then stops** — reserve is back above the 4 MW trigger.
* **FAIL if:** more than 4 items in one cycle; any non-Production block shed; shedding
  continues after reserve recovers; an idle machine shed for zero relief.

### S7 — Protection held

Run `scan all` and inspect.

* **Expect:** every block in the must-not-shed list above still `enabled=True`, and flagged
  `RUNPROT` where applicable. `Currently Shed` = 2.

### S8 — Reserve recovered

* **Expect:** `RESERVE / Current` positive (~5.3 MW), condition improved to WARNING,
  `LAST ACTION` naming the shed.

### S9 — Recovery holds while capacity is low

Wait 2 minutes with the turbine still off.

* **Expect:** **nothing is restored.** Reserve ~5.3 MW is below the 20 MW restore threshold.
  Possibly a `Restore held:` event. This is the hysteresis band doing its job.
* **FAIL if:** anything is restored while reserve is below the restore threshold.

### S10 — Restore generation

**Switch the steam turbine back ON.**

* **Expect:** `Generator returned` event. Available returns to 59.9 MW, reserve to ~55.9 MW.
  Recovery timer starts — and **nothing restores for 30 s**.

### S11 — Staged restoration

* **Expect:** one item restored, then the second **10 s later** — not both together. LIFO
  order, so CementKiln (shed last) comes back first. Two `RESTORED <name>` events, timestamps
  ~10 s apart.
* **FAIL if:** both restore in the same cycle, or restoration begins before 30 s have elapsed.

### S12 — Only script-shed blocks restored

Before S5, manually switch **one assembler off yourself** (e.g. WireDrawer) and note it.

* **Expect:** it is **never** switched on by the script, and never appears in `== SHED STATE ==`.
  `Currently Shed` returns to 0 with your block still off.
* **FAIL if:** the script enables a block it did not shed. This is the most important negative
  result in the run.

---

## Results

_To be filled in as each step is run._

| Step | Pass/Fail | Notes |
|---|---|---|
| S1 | | |
| S2 | | |
| S3 | | |
| S4 | | |
| S5 | | |
| S6 | | |
| S7 | | |
| S8 | | |
| S9 | | |
| S10 | | |
| S11 | | |
| S12 | | |

## Deferred to later runs

Locked-connector protection, docked-ship detection, docked battery recharge exposure,
docked-ship shedding, Red Alert reprioritisation, PB recompile with loads shed, and world
restart persistence. None require this run to pass first except the two that depend on shedding
working at all (docked shedding, and restart-with-loads-shed).
