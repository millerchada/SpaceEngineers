# IO Power Control v0.1.4 — UAT checklist

Run on the live **Industrial Overhaul v1.7.7** server.

**Already observed** on an IO 1.7.7 test station (2026-09-19, sections A1-A12 partly, B1, and
the producer/consumer discovery of A8-A11): generic producer discovery including the IO steam
turbine and wind turbine, `DetailedInfo` parsing across IO production machinery, construct
grouping over 116 grids, generator-removal detection, N-1, the scan report, and the
`[PowerControl.Catalog]` workflow. Four defects were found this way and are in the changelog.

**Not yet observed at all:** every shedding, recovery, docking, Red Alert and ship scenario -
sections C, D, E, F, G, I and J below. Those are the substance of the release and none of them
have run.

Record the result of every step. Where a step says *capture*, paste the PB Custom Data (below
the report marker) into `uat/` as evidence — the scan output is the primary artefact this
whole release is trying to obtain.

## Environment caveat: creative vs survival

**Record which mode each run was in.** In creative, power-producing blocks have effectively
unlimited fuel or charge but keep their normal maximum output limits. That makes a creative
run useless for one specific question and fine for everything else:

* **Cannot be answered in creative:** whether `IMyPowerProducer.MaxOutput` is a nameplate or
  the figure actually achievable. A turbine that always has steam always reports 50 MW, so a
  creative observation can neither confirm nor clear limitation 6. Step A11a below needs
  survival, or at least a deliberately starved well.
* **Unaffected by creative:** block discovery, classification, construct grouping, the
  `DetailedInfo` consumer model, coverage, protection, shedding order, hysteresis and restore.
  Those read the same API values either way.

**A turbine reading `out=0.00` is not evidence of anything being wrong.** Space Engineers
dispatches stored and renewable supply ahead of fuel-burning generation, so a turbine sits at
zero output whenever the batteries can cover the load - which is the normal resting state of a
base with any charge in it. `Current Generation` and `Utilization` reading 0 alongside a
discharging battery is correct behaviour, not a fault and not a steam-supply symptom.

To make generation actually engage for a test, give the base a load the batteries cannot
absorb - setting the batteries to Recharge is the cleanest way, and it doubles as step C1.

### The controlled experiment for limitation 6

World game mode lives in the world/server config, so it is an edit plus a restart, not a live
toggle. Per-player `Alt+F10` -> Admin Tools -> Creative Mode Tools keeps instant build and
spawning while the WORLD stays survival - which is the right rig for this: fast to build, but
power, production and inventories all behave.

Switching an existing creative world to survival makes every producer need real fuel at once.
A base whose only generation is an unfinished steam well and an unfuelled hydrogen engine will
run on its batteries and then go dark. Stage fuel before flipping, or the first survival run
is a blackout recovery rather than a controlled test.

| # | Step | Pass criteria |
|---|---|---|
| A0a | **Before switching**, capture a full `scan` in creative. | Baseline: same grid, unlimited fuel. |
| A0b | Switch the world to survival, reload, capture a `scan` on the same grid with nothing else changed. | The diff isolates exactly what fuel availability changes. If `MaxOutput` on the steam turbine and hydrogen engine is identical in both, the figure is a NAMEPLATE and limitation 6 stands. If either falls, it tracks real availability and the limitation is cleared. |
| A11a | **In survival**, with a well too shallow to supply 50 Steam/sec, read the turbine's `out=` and `max=` in `== PRODUCERS ==`. | If `max=` falls below the 50 MW nameplate, Available Generation is honest and limitation 6 is cleared. If `max=` stays pinned at 50 MW while `out=` is capped lower, Available Generation is an **upper bound** on a steam base and the limitation stands. |
| A11b | Read `== GAS / STEAM TANKS ==` while the turbine is loaded. | The steam buffer's fill ratio tracks supply against draw, and falls when the well cannot keep up. |

IO 1.7.7 steam figures, from the mod's own block descriptions:

    Steam Turbine        consumes 50 Steam/sec -> 50 MW   (1 MW per Steam/sec)
    Geothermal Well Tip  ~7.5 Steam/sec per 100m below 150m; min depth 150m  -> 50/sec at ~817m
    Geothermal Wellhead  ~6 Steam/sec per 200m past first 300m               -> 50/sec at ~1967m
    Well separation      250m optimal; efficiency falls off linearly if closer

The two formulas disagree. Which one the code uses is itself a UAT observation: if a well
starts carrying the turbine at roughly 820 m, it is the Well Tip rule.

---

**Before starting:** build the artifact, paste it, recompile, confirm the `[PowerControl]`
section appears in Custom Data and the Echo panel shows a version line and non-zero block
counts.

---

## A. Station, at rest

| # | Step | Pass criteria |
|---|---|---|
| A1 | Place on an IO station. Recompile. | Role reads `STATION`, State `STATIC`, Mode `NORMAL`. |
| A2 | Read Echo. | Instruction count well under the ceiling; run time stable over a minute. |
| A3 | Compare `Current Generation` against the in-game power tab. | Within rounding. |
| A4 | Compare `Available Generation` against the sum of online generator capacities. | Within rounding. |
| A5 | Compare `Current Demand` against the in-game total consumption. | Within rounding. |
| A6 | Check `Reserve` = Available − Demand, and `N-1` names a real generator. | Arithmetic holds; the named generator is the largest online one. |
| A7 | Check the BATTERY block against the terminal. | Stored, charge %, net flow and recharge exposure all match. |
| A8 | **`run "scan"` — capture.** | Report written below the marker; config above it untouched. |
| A9 | In the report, read `Model Coverage` and the `== UNKNOWN CONSUMERS ==` section. | **This is the point of the release.** Record coverage and every unknown subtype with its raw `detail:` line. |
| A10 | Check `== CONSUMERS BY DEFINITION ==`. | Every IO machine type present, with a category that makes sense. Note any miscategorised subtype. |
| A11 | Check `== PRODUCERS ==` for IO generators (steam turbines, gasoline/hydrogen engines, RTGs, solar, wind). | Each appears with a real `TypeId/SubtypeId` and a plausible max output. |
| A12 | Check that IO **steam-producing** machinery that makes no electricity is **not** in `== PRODUCERS ==`. | Absent from producers; contributes zero to generation. |

## B. Grid topology and docking

| # | Step | Pass criteria |
|---|---|---|
| B1 | Tag a panel `[PC-LCD:grids]`. | Grids page renders; home construct listed as `HOME`. |
| B2 | Dock a ship. | Within `RescanSeconds`: a `NEW GRID` event naming it, with load, potential load and recharge exposure; grids page shows it as `DOCKED`. |
| B3 | Check the docked ship's own subgrids (rotor turrets, drills). | They belong to the *ship's* construct, not the station's, and the construct is named after the ship's largest grid — not a subgrid. |
| B4 | Undock. | `Grid disconnected` event naming the grid; it leaves the grids page. |
| B5 | Dock a ship with a large battery bank, batteries in `Auto`. | `Recharge Exposure` rises by that bank's max input **before** anyone sets it to Recharge. |

## C. Sudden battery recharge load — the latent-exposure case

| # | Step | Pass criteria |
|---|---|---|
| C1 | With the ship of B5 docked, set its batteries to `Recharge`. | Demand rises; reserve falls; a `LOAD SPIKE` event appears. |
| C2 | Watch the shedding engine. | Batteries are the **first** thing given up (Discretionary in every mode): event `battery off recharge`, charge mode goes `Recharge → Auto`. |
| C3 | Check `Currently Shed` on the dashboard. | Count matches the number of batteries switched. |
| C4 | Wait for recovery. | After reserve is above `RestoreReserveMW` for `RecoverySeconds`, batteries return to `Recharge` **one per `RestoreStepSeconds`**, not all at once. |

## D. Active ore processing and industrial shedding

| # | Step | Pass criteria |
|---|---|---|
| D1 | Start crushers/refineries until reserve drops below `ShedReserveMW`. | Condition moves through `CAUTION` → `WARNING`; events logged for each transition. |
| D2 | Observe shedding. | Industrial/production blocks shed **only** at `WARNING` or worse; shipyard, jump and decor go first. |
| D3 | Check what was shed. | Each shed block is logged with its category and the relief claimed. Nothing in life support, control, docking, generation or mechanical was touched. |
| D4 | Check an **idle** refinery that was running earlier. | It is **not** shed — measured zero draw means zero relief. |
| D5 | Stop the ore feed so reserve recovers. | Restoration is progressive and LIFO; no more than one item per `RestoreStepSeconds`. |
| D6 | Watch for 5 minutes around the threshold. | **No flapping.** Nothing sheds and restores repeatedly. |

## E. Protection

| # | Step | Pass criteria |
|---|---|---|
| E1 | Lock a connector to a docked ship, then force shedding with `run "shed"` repeatedly. | The connector is **never** disabled. It appears with `RUNPROT` in `scan all`. |
| E2 | Lock a landing gear; repeat. | Never disabled. |
| E3 | Tag a refinery `[PC-PROTECTED]`; force shedding. | Never shed. Shows `CFGPROT` in `scan all`. |
| E4 | Put `[PowerControl]` + `Protected=true` in a block's Custom Data; force shedding. | Never shed. |
| E5 | Check the PB itself and any other programmable block. | Never shed. |
| E6 | Check a block the script classified `Unknown`. | Never shed, under any mode or condition. |
| E7 | On the **docked ship**, force shedding hard. | Its thrusters, gyros, cockpit, connectors and landing gear are untouched. Only production/industrial/shipyard/jump/battery on that ship can be affected. |
| E8 | Set `ControlDockedGrids=false`; repeat. | Nothing on the docked ship is touched at all. |

## F. Ship

| # | Step | Pass criteria |
|---|---|---|
| F1 | Paste the same artifact into a mobile ship's PB. | Role auto-detects `SHIP`. |
| F2 | Ship running on batteries only, no reactors. | Condition is **not** `EMERGENCY` while batteries are charged. `Available` shows `+b`. |
| F3 | Fly. | State `FLIGHT`. |
| F4 | Land on gear. | State `LANDED`. |
| F5 | Dock. | State `DOCKED`, and the role is still `SHIP`. |
| F6 | Drain batteries below 10 % with a deficit. | Condition reaches `EMERGENCY`; thrusters, gyros, cockpit and life support are never shed. |
| F7 | `run "state construction"`. | State reads `CONSTRUCTION` and stays there across recompiles. |

## G. Ship under construction — the motivating incident

| # | Step | Pass criteria |
|---|---|---|
| G1 | With the station script running, weld a new ship in a shipyard / from a projector. | As blocks complete, `Potential` demand on the station dashboard rises. |
| G2 | When the new grid becomes a separate construct. | `NEW GRID` event with its current load, potential load and recharge exposure — *before* the load is drawn. |
| G3 | Bring the new ship's systems online until demand exceeds generation. | Shedding preserves the base: shipyard and industrial load go before anything in the critical set. |
| G4 | Confirm the base stays up. | No brownout; life support, control and locked connectors still powered. |

## H. Generator loss and N-1

| # | Step | Pass criteria |
|---|---|---|
| H1 | Note the `N-1` figure and the generator it names. | Matches the largest online producer. |
| H2 | Switch that generator off. | `GENERATOR OFFLINE` event with the capacity lost; reserve falls by roughly the N-1 prediction. |
| H3 | Grind that generator away entirely. | `GENERATOR REMOVED` event — loss detected by absence, not by a property. |
| H4 | Switch it back on / rebuild. | `Generator returned` event. |

## I. Modes

| # | Step | Pass criteria |
|---|---|---|
| I1 | `run "mode redalert"` on the station. | Mode changes; event logged. |
| I2 | `run "scan all"` and read the tier column. | Weapons, sensors and comms are now `Critical`; production, industrial, shipyard and jump are `Discretionary`. A jump drive does **not** outrank a turret. |
| I3 | Force a deficit in Red Alert. | Production/ore processing sheds; weapons and defences do not. |
| I4 | `run "mode economy"`, then `normal`, then `emergency`. | Each takes effect; tiers change accordingly; each is logged. |
| I5 | Recompile the PB. | Mode survives (persisted to `Storage`). |

## J. State memory and recovery

| # | Step | Pass criteria |
|---|---|---|
| J1 | Manually switch off an assembler **before** any shedding. | The script never switches it back on. |
| J2 | Let the script shed several blocks, then recompile the PB. | Startup event reports N shed entries recovered from `Storage`; the same blocks are restored later, and only those. |
| J3 | `run "recover"`. | Everything the script shed comes back immediately, in reverse shed order. `Currently Shed` goes to 0. |
| J4 | Shed something, then grind it away, then recover. | `Restore skipped, block gone` — no exception. |

## K. Performance and endurance

| # | Step | Pass criteria |
|---|---|---|
| K1 | Run on the largest IO base available for 60 minutes. | No `SCRIPT ERROR` events; instruction count stays well under the ceiling on every cycle including rescans and `scan`. |
| K2 | Tag `[PC-LCD:history]`. | 60 minutes of samples accumulate; window, min reserve and peak demand look right. |
| K3 | Tag `[PC-LCD:events]`. | Event log renders newest first and stays within `EventLines`. |
| K4 | Watch `Model Coverage` over an hour. | It **converges upward** and does not sawtooth at each rescan. |
| K5 | Check `Storage` size indirectly (recompile repeatedly). | No growth without limit; learned catalog capped at 250 entries. |

---

## What a failed run should produce

Not a patch — evidence. For anything that fails, capture:

* the PB Custom Data report below the marker,
* the event log page,
* the exact block name and `TypeId/SubtypeId` involved,
* for a classification or model failure, the raw `detail:` line for that block.

Coverage below expectation on step A9 is **not** a failure of the release. It is the measurement
the release was built to take, and it is what fills `CATALOG[]` in v0.2.
