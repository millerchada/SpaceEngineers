# IO Power Control

**IOPC v0.1.18** — power capacity, protection and automatic load shedding for Space Engineers,
built against **Industrial Overhaul v1.7.7**.

One script for both stations and ships. It answers four separate questions:

1. What are we producing right now?
2. What are we consuming right now?
3. What *could* the currently connected system draw if everything started?
4. What can safely be switched off when the answer to 3 exceeds the answer to 1?

It exists because of a specific failure: a station processing ore, a ship under construction
alongside it, enough of that ship coming online that its load joined the base, demand
exceeding generation, and the whole base going down.

## Tests

    python IO_Power_Control/tests/test_staged_discovery.py IO_Power_Control/IO_Power_Control_v0.1.18.cs
    python IO_Power_Control/tests/test_staged_report.py IO_Power_Control/IO_Power_Control_v0.1.18.cs

    python IO_Power_Control/tests/test_shed_authorization.py IO_Power_Control/IO_Power_Control_v0.1.18.cs

Runs the script rather than only compiling it: the shed-authorisation assertions construct the
state a live base would be in and call `ShedStep()` directly. Point it at an older version to
see an assertion fail — v0.1.12 fails `NEGATIVE_stale_latch_no_live_drain`, which is how that
defect was established before it was fixed.

## Deploying

    python tools/check_pb.py IO_Power_Control/IO_Power_Control_v0.1.18.cs
    python tools/build_pb.py IO_Power_Control/IO_Power_Control_v0.1.18.cs

Paste `IO_Power_Control_v0.1.18.min.cs` into a programmable block and recompile. The source is
~81 k characters, which is under the PB's 100 k ceiling, but the artifact is what gets pasted
— same as every other script in this repo.

On first run the script writes a default `[PowerControl]` section into the block's Custom Data
and starts on `Update10`.

---

## 1. Architecture

Single file, flat, no classes beyond small records — the usual PB shape.

**Two clocks.** Fast electrical values are read every `UpdateSeconds` (default 1 s) from
cached block lists. Topology and classification are rebuilt every `RescanSeconds` (default
30 s). Nothing rescans the grid per tick.

**A third, slower pass.** Per-block `DetailedInfo` is a string parse, far too expensive to run
over every block every cycle, so `ScanChunk` blocks (default 40) are refreshed per cycle with
a rolling cursor and an instruction-count budget (`InstrBudgetPercent`, default 60 %). Values
learned this way are carried across rescans and persisted, so coverage converges instead of
sawtoothing.

**Pipeline per tick:**

    [rescan due?] -> ParseConfig -> Discover -> BuildConstructs -> Classify
    RefreshDetailChunk -> DetectRole -> DetectState -> Measure -> Condition
      -> ShedStep -> RestoreStep -> Sample -> Render

`DetectRole` runs before `Measure` because the treatment of battery discharge capacity
depends on the role.

**Policy is a table, not code.** `POLICY[]` holds one row per role × mode mapping category →
tier. Changing what a station sheds in Red Alert is an edit to one string.

**Protection is not a tier.** It is a separate, absolute constraint, re-evaluated against live
block state in the same statement sequence that performs the mutation — never from cache.

---

## 2. Default configuration

Written to the PB Custom Data on first run:

```ini
[PowerControl]
Role=Auto                       ; Auto | Station | Ship
Mode=Normal                     ; startup mode only; `mode` command wins afterwards
State=Auto                      ; Auto | Flight | Docked | Landed | Construction | Static
AutoShed=true
ControlDockedGrids=true         ; and only ever the safe category set - see section 6
UpdateSeconds=1
RescanSeconds=30
CautionReserveMW=15
WarningReserveMW=8
CriticalReserveMW=0
CautionReservePercent=20
WarningReservePercent=10
CriticalReservePercent=2
ShedReserveMW=4                 ; shed below this reserve
RestoreReserveMW=20             ; restore only above this reserve
CountBatteryDischarge=Auto      ; Auto = yes on a SHIP, no on a STATION
RecoverySeconds=30              ; reserve must hold above restore threshold this long
RestoreStepSeconds=10           ; and this long between each restored item
MaxShedPerCycle=4
HistorySeconds=10
HistorySamples=360              ; 360 x 10 s = 60 minutes
EventLines=40
ScanChunk=40
InstrBudgetPercent=60
```

`RestoreReserveMW` is forced to at least `ShedReserveMW + 5` if a config sets them equal or
inverted — two decisions sharing an edge is how loads flap.

Everything below the line

    ; ==== POWER CONTROL GENERATED REPORT - EDITS BELOW THIS LINE ARE LOST ====

is owned by the script. Configuration is parsed from above it only, so a scan report can never
corrupt the configuration.

### Per-block overrides

By name tag (survives a Custom Data wipe):

    [PC-PROTECTED]   never shed, whatever the policy says
    [PC-SHED]        opt this block IN as discretionary, whatever the policy says
    [PC-IGNORE]      not controlled, and not counted as a consumer
    [PC-LCD]         render the dashboard here; [PC-LCD:grids|events|history] for other pages

By Custom Data on the block:

```ini
[PowerControl]
Protected=true
Sheddable=true
Ignore=false
Category=Industrial     ; any category name from section 5
MaxLoadMW=12.5          ; overrides the model for this block, source shown as "config"
```

`Protected` always beats `Sheddable`.

### Rated loads you measured yourself

Some blocks publish no electrical figure at all. On the live test grid, pistons, sliding doors
and the programmable block all report nothing usable. Rather than editing the source, add rows
to a second section in the PB Custom Data, **above** the report marker:

```ini
[PowerControl.Catalog]
MyObjectBuilder_ExtendedPistonBase/LargePistonBase=0.003
LargeBlockSlideDoor=0.0001
```

Keys are a bare `SubtypeId` or a full `TypeId/SubtypeId`; values are MW. Re-read on every
config parse, so an edit takes effect on the next rescan with no recompile. A catalog row
outranks a measured figure, because it is a number you measured deliberately.

`scan` prints this section for you, pre-populated with every unknown definition at `=0`. The
zeros are deliberate — filling one in is a measurement you take, not a number this script is
willing to invent on your behalf. To measure one: note network demand, switch the block on,
note it again.

---

## 3. Commands

| Argument | Effect |
|---|---|
| `scan` | Full diagnostic pass, report written to the PB Custom Data below the marker |
| `scan all` | As above plus a line for every functional block (capped at 500) |
| `status` | Force an immediate tick and Echo |
| `mode economy` / `normal` / `redalert` / `emergency` | Set operating mode. Persisted |
| `state auto` / `flight` / `docked` / `landed` / `construction` | Override the operational state |
| `shed` | Run one shedding pass now, ignoring the reserve threshold — **not** the protection rules |
| `recover` | Restore everything the script shed, immediately, in reverse shed order |
| `rescan` | Queue a topology rebuild on the next tick |
| `help` | List the above |

`shed` and `recover` go through the same engine as the automatic path. `shed` bypasses only
the reserve gate; every protection check, the external-grid whitelist and the unknown-block
rule still apply. `recover` is the operator override for "put it all back now" and does not
wait for the hysteresis timers.

---

## 4. Role, state, mode and condition

Four separate things, deliberately:

* **Role** — `STATION` or `SHIP`. Auto-detected from combined signals: grid static flag,
  thruster count, gyro presence, ship-controller presence. Not one fragile heuristic.
* **State** — `FLIGHT`, `DOCKED`, `LANDED`, `CONSTRUCTION`, `STATIC`, `UNKNOWN`. A ship stays
  a ship when docked. `CONSTRUCTION` is **not** auto-detected (see Limitations).
* **Mode** — `ECONOMY`, `NORMAL`, `REDALERT`, `EMERGENCY`. An operator decision, never
  inferred from an electrical condition.
* **Power Condition** — `NORMAL`, `CAUTION`, `WARNING`, `CRITICAL`, `EMERGENCY`. An electrical
  fact, never an operator decision. Leaves `NORMAL` only when the system is **observably
  stressed**, whatever the headroom arithmetic says. Shedding keys off this.
* **Capacity Risk** — the same ladder applied to credible headroom, plus a negative N-1.
  Debounced asymmetrically: a worse value promotes after `CapacityRiskPromoteSeconds` (2s), a
  better one must hold for `CapacityRiskRecoverSeconds` (15s), and any change of candidate
  restarts the clock — so a value oscillating across a threshold during ordinary production
  cycles settles at the worse of the two instead of chattering. Events are logged only on a
  committed change.
  Contingency exposure rather than an alarm: a lightly loaded base can legitimately sit at
  `Condition NORMAL / Capacity Risk WARNING`, and reporting that as a permanent WARNING would
  only teach the operator to ignore it.

Condition is evaluated on both an absolute MW reserve and a percentage of available
generation, and the **worse of the two wins**. Percentage alone lies on a small ship; MW alone
lies on a large base.

---

## 5. Categories and default policy

Categories: `LifeSupport, Control, Propulsion, Generation, Docking, Weapons, Sensors, Comms,
Battery, Jump, Industrial, Production, Shipyard, Fuel, Utility, Decor, Mechanical, Unknown`.

Tiers, most protected first: `Protected, Critical, Essential, Normal, Industrial,
Discretionary`. Shedding starts at Discretionary and works up, stopping at the depth the
current condition permits:

| Condition | Deepest tier reachable |
|---|---|
| Normal / Caution | Discretionary |
| Warning | Industrial |
| Critical / Emergency | Normal |

Selected rows from the table:

* **Station / Normal** — Critical: life support, control, generation, docking, mechanical,
  unknown. Essential: fuel, comms, sensors, propulsion. Industrial: production, industrial.
  Discretionary: shipyard, jump, decor, battery recharge.
* **Station / RedAlert** — weapons, sensors and comms rise to Critical; production,
  industrial, shipyard and jump drop to Discretionary. A jump drive does not outrank a turret
  because it is normally important.
* **Ship / Normal** — Critical: life support, control, propulsion, generation, docking,
  fuel, mechanical, unknown. Jump is Normal. Production and industrial are Industrial.
* **Ship / RedAlert** — weapons, sensors and comms rise to Critical; jump-drive charging,
  production, industrial and shipyard drop to Discretionary.

Two rules that are policy, not special cases:

* **Unknown is pinned to Critical in every row.** A block we could not classify is never shed.
* **Battery is Discretionary in every row.** The only battery action taken is
  `Recharge → Auto`, which is instantly reversible and costs nothing but charging time, so it
  is always the cheapest relief available and is always spent first.

---

## 6. Protection

Static protection: the `[PC-PROTECTED]` tag or `Protected=true`.

Runtime protection, evaluated live and re-checked immediately before any mutation:

* a connector that is `Connected` **or** `Connectable`
* a landing gear that is locked
* a merge block that is connected
* any ship controller (cockpit, remote control)
* any thruster or gyro
* air vents, oxygen farms, medical rooms
* rotors and pistons (they carry structure)
* any programmable block, including this one

The connector rule is the important one. A docked ship usually has its thrusters off and the
connector is the only thing holding it. Switching that connector off to save a few hundred
kilowatts releases a ship with no station-keeping — a far worse accident than the brownout
being prevented.

**External constructs.** On any connector-attached grid that is not the home construct, only
these categories may ever be touched: `Production, Industrial, Shipyard, Jump, Battery`. That
whitelist is checked independently of the policy tier, so no mode can widen it. Thrusters,
gyros, controllers, connectors and landing gear on someone else's ship are observed and never
controlled. Set `ControlDockedGrids=false` to observe only.

---

## 7. How demand is calculated — and where it cannot be

### Current demand is exact

Every connected grid shares one electrical network. Everything consumed is supplied by some
producer, and a discharging battery is a producer. So

    Current Demand = sum(non-battery producer CurrentOutput)
                   + sum over batteries of max(0, CurrentOutput - CurrentInput)

is the network's actual consumption, straight from `IMyPowerProducer.CurrentOutput`. No block
catalog is involved. Battery *charging* is a load and is already inside the producer output
feeding it, so it is not added again.

The battery term is **net, per battery, never gross**. A battery in Auto can report charging
and discharging at the same instant - observed live at `in=2.14MW, out=2.04MW` with its input
exactly equal to total generation. That circulation is not real power flow, and counting the
gross output reported 2.1 MW of load as 4.17 MW. Net is taken per battery rather than across
the bank, because one battery charging while another discharges *is* real power moving.

Note what this deliberately does **not** do: it does not net charging out of demand. A docked
ship set to Recharge shows `in=12, out=0`, so its net discharge is zero and demand stays at
generator output - which has risen to produce those 12 MW. Demand still rises, which is the
entire point. Subtracting charging from demand would hide the exact load this script exists
to catch.

    Current      = sum(CurrentOutput) of working producers              exact
    Credible     = sum(credible_i)    - what protection spends
    Nameplate    = sum(MaxOutput)     - a rating, NOT a capability
    Reserve      = Credible - Current Demand
    N-1 Reserve  = Credible - largest credible producer - Current Demand

    credible_i = MaxOutput_i               environmental (solar, wind)
               = max(proven_i, current_i)  fuel-fed, or unrecognised

**Nameplate is never spent as reserve.** A 50 MW IO steam turbine fed by a well good for 26 MW
of steam still advertises 50 MW; measured live, a base reported 44.1 MW of reserve while its
entire generation system had ever demonstrated 19.4 MW. Solar and wind are believed at their
`MaxOutput` because the game recomputes it from conditions; everything else is credited only
what it has been witnessed to deliver, and an unrecognised producer is treated as fuel-fed.

**Shedding additionally requires observed stress**, and in this build there is exactly one
qualifying signal: **sustained battery drain**, which must satisfy all three of

* net discharge >= `max(BattStressMW, 2% of credible)` — default 0.5 MW,
* held **continuously** for `StressHoldSeconds` (the timer restarts on any dip), and
* stored energy actually fallen **consistently with the measured discharge**:
  `actual >= max(MinDeclineMWh, DeclineConsistency x expected)`, where `expected` is the
  discharge integrated over the window.

That third leg is deliberately scale-free. Expressing it as a share of bank capacity — as it
was until v0.1.11 — qualified in 11 s on a 3 MWh bank and **18 minutes** on a 300 MWh one for
the same real deficit, because a bigger battery makes the same deficit a smaller fraction of
the bank without making it any less real.

Once stress is **established**, it latches. Exit is a different question from entry: the drain
must fall below `BattRecoverFraction` x the entry bar and stay there for `StressRecoverSeconds`.
Requiring the entry qualification to remain continuously true would mean ordinary variation
between a 2 MW and a 5 MW deficit clears an active alarm — which it did, on a live station,
while a 32 MW load was still connected and the battery was still draining.

With no qualifying signal, nothing is shed and the dashboard says `OBSERVATION ONLY` rather
than sitting silent.

Two earlier stress signals were removed after both fired on a healthy station. A demand-floor
heuristic followed troughs rather than trend, so ordinary cyclic IO production re-pinned the
floor on every idle period and the next processing cycle read as a new rising load. And an
instantaneous 0.1 MW net-discharge test could not distinguish a real drain from the control
oscillation of a bank reporting ~11 MW gross out against ~12 MW gross in — 0.22 MW for a few
seconds is 0.01% of a 3 MWh bank, below the resolution at which stored energy is displayed.

**Brownout detection** (`Enabled && IsFunctional && !IsWorking`) is collected as telemetry and
reported by `scan`, but does **not** authorise shedding. A block must stay browned out for
longer than a full rescan interval before it is even counted: the block list is rebuilt only by
`Discover()`, so a **deleted** block lingers in it for up to 30 s and will happily report
enabled-but-not-working — observed live when a deleted jump drive produced a phantom count for
~14 s. `scan` reports raw and confirmed counts separately, and the gap between them is the
argument for why this signal is not yet trusted. It is the most promising signal — it
observes unmet demand directly and would work on a battery-less grid — but it will not be
trusted until live evidence shows which blocks report `IsWorking=false` for non-power reasons.

Witnessed producer output is persisted in `Storage`, keyed by `EntityId`, so a paste-deploy does
not discard evidence that took a base under load to acquire. A rebuilt producer gets a new id
and correctly starts proving itself again.

With `CountBatteryDischarge` in effect, battery `MaxOutput` joins Available Generation and the
dashboard marks it `+b`. Auto means yes on a ship, no on a station: a battery ship genuinely
runs on stored power, and a base that counts a draining tank as capacity is deceiving itself.

### Potential demand is a model, and its coverage is published

There is **no** power-consumer interface in the PB API and **no** per-block consumption
property. The only per-block electrical figure the API exposes at all is the `DetailedInfo`
string. The model is therefore layered, and every block carries the tier its number came from:

Precedence, highest first:

    config  ->  catalog  ->  detail  ->  builtin  ->  learned  ->  unknown

The built-in table sits below `detail` on purpose: it is a definition-time rating, while a
running block describing itself accounts for upgrades and damage.

| Source | What it is |
|---|---|
| `config` | `MaxLoadMW=` on the block. Beats everything. |
| `detail` | Parsed from this block's own `DetailedInfo` "…Input: n MW" line. |
| `iface` | From an interface that really exposes it — today, battery `MaxInput`. |
| `catalog` | Your `[PowerControl.Catalog]` rows. Outranks everything: a number you measured on purpose. |
| `builtin` | The generated IO/vanilla subtype table, 338 rows. Sits **below** `detail` — see below. |
| `learned` | The largest figure ever observed for this same definition, persisted in `Storage`. |
| `unknown` | None of the above. Counted, listed by `scan`, and **never shed**. |

`Model Coverage` on the dashboard is `known / (known + unknown)` by block count. Below 98 %
the dashboard prints a warning. The number is never quietly rounded up.

Block count alone is misleading, so the dashboard also reports **Unknown Types**. The first
live run was 16 unknown blocks — but only 3 definitions, 13 of those blocks being the same
piston. One of those numbers looks like a crisis; the other is a short job with
`[PowerControl.Catalog]`.

**A measurement never lowers a rating.** A machine that publishes `Required Input: 0.00 MW`
while idle is telling you what it draws *now*; potential demand asks what it could draw, so an
idle reading cannot erase a catalog or learned figure. `Current Load` still uses the live
value, so shedding is never misled by a stale rating.

The learned tier matters more than it looks: a block that is switched off usually publishes
nothing useful, so the figure observed from a working sibling is what keeps an idle bank of
machines visible in the model.

**Potential demand includes battery recharge exposure**, which is also shown separately. A
docked ship with hundreds of batteries is harmless until someone sets them to Recharge; the
dashboard makes that latent load visible before it arrives.

### Where it cannot be calculated

* `DetailedInfo` is a localized, free-form string. This parser looks for a line whose label
  mentions "input" and takes the number and unit after the colon. **On a non-English server it
  will find nothing, every block will fall through to unknown, and coverage will say so.**
  That is the correct failure; the alternative is a confident wrong number.
* Thrusters expose thrust, not power. Their draw is whatever `DetailedInfo` reports, if
  anything, and otherwise unknown.
* Blocks under construction are not functional and are not counted. Potential demand on a ship
  being built therefore grows as the ship is welded, which is real, but it is not a forecast of
  the finished ship.

---

## 8. IO subtype definitions included

**338 subtypes, generated from the mod's own `.sbc` definitions.**

    python IO_Power_Control/tools/extract_io_catalog.py "<mod>/Data" "<game>/Content/Data"

Reads `RequiredPowerInput`, `OperationalPowerConsumption`, `PowerConsumptionMoving` and
`RequiredIdlePower`, largest wins, base game first and the mod overriding it. Nothing is
estimated: a block that declares no figure gets no row and stays unknown. Five independent
cross-checks against the live server agree exactly - `CementKiln` 3 MW, `LargeRefinery` 3 MW,
`GeothermalWellHead` 0.5 MW against their own `DetailedInfo`, and `LargePistonBase` 0.002 /
`LargeBlockSlideDoor` 0.001 against BuildInfo and a measured demand delta.

Regenerate it after a mod update; the table is marked GENERATED and should not be hand-edited.

**What it cannot cover:** many vanilla blocks - sliding hatch doors, medical rooms,
programmable blocks - declare no power tag in any `.sbc`, because their consumption is
hardcoded in the game's C# and applied through a runtime sink component. Adding the base
game's own data yields only five extra subtypes. Those blocks stay unknown until someone
measures them into `[PowerControl.Catalog]`.

Every entry would be a number invented off-server. An invented rated load is worse than a
disclosed unknown: it silently inflates the model *and* inflates the coverage figure that is
supposed to disclose the gap. The `scan` command exists to fill this table from the live
server; `CATALOG[]` in the source takes `"SubtypeId=MW"` rows and a catalog entry outranks a
learned one.

The classification **hint table** is populated, and is where IO machinery is actually
recognised — IO adds subtypes rather than interfaces, so ~90 substring rules map subtype and
display names onto categories (crusher, furnace, kiln, centrifuge, boiler, steam, dynamo,
gasoline, shipyard, …). That table is data and is extended the same way.

### Generation support is checked before the interface

IO models steam as a gas. Confirmed live: the geothermal well head is
`MyObjectBuilder_OxygenGenerator/GeothermalWellHead` and the steam tank is
`MyObjectBuilder_OxygenTank/SteamTank` — so both present as ordinary oxygen equipment and would
otherwise be filed under `Fuel`. A separate `GENSUP[]` table (steam, geothermal, wellhead,
boiler, condenser, superheat, feedwater, coolant) is matched against the subtype **before** the
interface fallback and files them as `Generation`, which is Critical in every policy row.

Shedding the well head that feeds a 50 MW steam turbine would be a load-shedding script causing
the blackout it was preventing, so this is a safety rule, not a cosmetic one. These blocks are
still counted as **consumers** — the well head really does draw 0.50 MW — because the
supply/demand test is the `IMyPowerProducer` interface, not the category name.

---

## 9. Diagnostics: testing unknown IO blocks

    run "scan"

Runs a full discovery, a complete off-budget `DetailedInfo` pass over every block, and writes
a report into this block's Custom Data below the marker. Sections:

* header — role, state, mode, condition, totals, coverage
* `== CONSTRUCTS ==` — one line per construct: home/docked, grids, blocks, generation,
  estimated load, potential load, battery count, battery in/max-in, stored, unknown count
* `== PRODUCERS ==` — on/off, name, `TypeId/SubtypeId`, current/max output, grid
* `== BATTERIES ==` — charge mode, stored/max, in/max-in, out/max-out, grid
* `== CONSUMERS BY DEFINITION ==` — **the table that becomes catalog entries**: count,
  `TypeId/SubtypeId`, category, max load found, and which tier produced it
* `== UNKNOWN CONSUMERS ==` — every block with no rated load, each followed by the raw first
  100 characters of its `DetailedInfo` so you can see *why* it is unknown (empty? different
  wording? different units?)
* `== ALL FUNCTIONAL BLOCKS ==` (with `scan all`) — per block: category, tier under the
  current role/mode, max, current, source, and the `CFGPROT` / `RUNPROT` / `CTRL` flags
* `== SHED STATE ==` and `== EVENTS ==`

Workflow for an unrecognised IO block:

1. `run "scan"`, open the PB Custom Data, find the block under `== UNKNOWN CONSUMERS ==`.
2. If its `detail:` line shows an input figure this parser missed, that is a parser bug —
   send the line.
3. If its `detail:` line is `(empty)`, the block does not publish its draw. Measure it: note
   network demand, switch the block on, note it again, and add
   `"<SubtypeId>=<MW>"` to `CATALOG[]` — or set `MaxLoadMW=` on that one block.
4. If its category is wrong, add a substring row to `HINTS[]` or set `Category=` on the block.

This is not temporary debug code. It is the only route from "IO 1.7.7 has a block we have
never seen" to "the model knows what it draws", and it stays in the script.

---

## 10. Dashboard

The PB's own first surface always shows the main page. Any other panel opts in with
`[PC-LCD]` in its name, optionally choosing a page: `[PC-LCD:grids]`, `[PC-LCD:events]`,
`[PC-LCD:history]`. The script sets `ContentType`, `Font=Monospace` and left alignment on a
tagged surface; font size stays yours.

Main page: role/state/mode/condition, generation (current, available, utilization), demand
(current, potential, model coverage), reserve (current, N-1, and what N-1 loses), battery
(stored, charge, net flow, recharge exposure), grids (home, docked, new), protection
(protected, currently shed, unknown consumers), last action, and the coverage warning.

Grids page: per construct, home/docked/linked, estimated load, potential load, generation,
battery count, battery input and stored. Per-grid load is labelled `est` because it is a
sampled `DetailedInfo` sum — network totals are exact, per-grid attribution is not.

---

## 11. Shedding, hysteresis and recovery

Shedding triggers below `ShedReserveMW` (default 4 MW) and targets getting back above it with
1 MW of margin, at most `MaxShedPerCycle` items per **action**.

**Actions are paced by `ShedSettleSeconds` (default 5 s), not by the tick rate.** Without that,
`ShedStep` runs at 1 Hz and the actuator acts again before its last action could take effect -
which cost five blocks and 47.5 MW on a 12 MW deficit, because two charging jump drives absorbed
every megawatt freed and the reserve figure never moved.

**Every** shed action is authorised by the **live** drain against the stress bar, never by the
latched `Stressed` flag — including the first action of an episode: the latch is deliberately slow to clear so the
alarm does not flicker, and that is the wrong thing to let authorise a new action every second.
The event log distinguishes shed / settling / deficit-persists / stopped-on-improvement, and
every refused candidate is recorded with its tier and the reason — a refusal used to fall
through to the next candidate silently, which can reorder the entire shed sequence.

Candidates are sorted by tier (most expendable first), then by the relief they would actually
give back *now*. A machine measured at zero draw gives zero relief and is not shed — switching
off an idle refinery for its 8 MW rating returns nothing and makes the engine shed the next
thing too. Only a block that has never been measured falls back to its rating. Immediately
before the mutation the block's `DetailedInfo` is re-read, so a machine that finished its batch
since the last chunk refresh is left alone.

Restoring requires reserve above `RestoreReserveMW` (default 20 MW) continuously for
`RecoverySeconds` (default 30 s), then one item per `RestoreStepSeconds` (default 10 s), in
LIFO order — the last thing shed was the most important thing shed, so it comes back first.
If the head of the queue would draw more than the current margin, restoration **waits** and
says so in the event log rather than re-opening the deficit.

**The script restores only what it shed.** Every action is recorded with the block's entity ID
and, for a battery, its previous charge mode, and the list is persisted to `Storage`. A block
the player switched off is never switched on: candidates that are already disabled are skipped
outright, so they never enter the list.

If `Storage` is lost (a full recompile with cleared storage), the script forgets what it shed:
those blocks stay off and must be restored by hand. That is the deliberate choice — the other
failure mode is switching on equipment the player deliberately disabled. The event log says
which case it is on startup.

---

## 12. Events and history

Event log (last `EventLines`, default 40): generator offline/returned/removed, grid connected
with its load, potential and recharge exposure, grid disconnected, load spike, power condition
change, mode change, state change, role change, shedding started, each block shed, restore
held, each block restored, scan written, script error.

History: one sample every `HistorySeconds` (default 10 s), `HistorySamples` deep (default 360
= 60 minutes), holding generation, available generation, demand, reserve, battery stored,
battery net flow, potential demand and condition. In memory only — persisting an hour of
samples would crowd out the shed list and the learned catalog in `Storage`, and losing a graph
across a recompile costs nothing while losing the record of what was switched off costs a lot.

---

## 13. Known limitations

1. **`DetailedInfo` parsing is English-dependent.** Non-English server → zero coverage, loudly
   disclosed, never a wrong number.
2. **The IO catalog is empty.** Coverage on a live IO base depends on what IO blocks publish
   in `DetailedInfo`. This is what the first UAT run is for.
3. **Per-grid load attribution is a sampled estimate**, refreshed `ScanChunk` blocks per cycle.
   Network totals are exact; per-grid figures are labelled `est`.
4. **`CONSTRUCTION` state is not auto-detected.** The PB API exposes no reliable "this grid is
   being built" signal. Set it with `run "state construction"`.
5. **Potential demand counts blocks that are already built.** A half-welded ship's future load
   is not forecast.
6. **Whether `MaxOutput` is achievable or only a nameplate is UNRESOLVED.** It was briefly
   recorded here as confirmed, on the strength of a steam turbine reporting `out=0.72/50.0MW`
   while the battery appeared to discharge 3.27 MW - the argument being that a capable turbine
   would have been used instead of draining the battery. **That argument was wrong**: 3.27 MW
   was the battery's *gross* output, and its net position at that moment was 1.06 MW of
   charging. The base was in surplus, so the turbine was idle by dispatch order, not starved.
   A later capture settles that much - the turbine reports `Filled: 100.0% (100L/100L)`, a full
   steam buffer, with `Current Output: 0 W`.

   Space Engineers dispatches renewable and stored supply ahead of fuel-burning generation, so
   a turbine sits at zero whenever wind, solar and batteries cover the load. **That makes the
   question untestable at rest.** It needs a load larger than everything else combined: if the
   turbine then ramps toward 50 MW, `MaxOutput` is honest; if it plateaus far below while the
   batteries go net-negative, it is a nameplate.

   Until that test is run, read `Available` as a rating and **Proven** - the highest output each
   producer has ever actually been witnessed to deliver - as a floor. The dashboard shows both.
   No PB API exposes a producer's currently achievable ceiling, so the gap cannot be closed by
   the script, only disclosed.
7. **Steam supply is visible but not modelled.** IO models steam as a gas, so the buffer
   between well head and turbine appears under `== GAS / STEAM TANKS ==` with its fill ratio.
   That is reported, never acted on. A falling steam buffer does not yet influence the power
   condition or trigger shedding.
8. **No steam-system modelling.** IO steam machinery that feeds turbines but produces no
   electricity is classified as `Generation` for policy purposes and contributes **zero** to
   electrical output — it is not counted as generation. Steam flow is future work.
9. **Batteries are only ever taken off `Recharge`**, never forced to `Discharge`. Forcing
   discharge is a bigger intervention than v0.1 should make unasked.
10. **Thruster power draw is not modelled** beyond whatever `DetailedInfo` reports.
11. **Restoring can be blocked indefinitely** if the head of the LIFO queue is larger than the
   available margin. Visible on the dashboard and in the log; `recover` overrides it.
12. **A shed block that the player then re-enables by hand** is still in the restore list and
    will be "restored" (a no-op) later.
13. **No IGC / multi-PB coordination.** Two copies of this script on one network will fight.
    Run one.
14. **Only partly exercised on the live server.** v0.1.1 has run on an IO 1.7.7 test station:
    producer discovery, `DetailedInfo` parsing, construct grouping, N-1 and the scan report are
    confirmed there. No IO *industrial* machinery (crushers, furnaces, refineries) has been
    seen yet, and no shedding, docking or Red Alert scenario has been run. See `uat/`.
