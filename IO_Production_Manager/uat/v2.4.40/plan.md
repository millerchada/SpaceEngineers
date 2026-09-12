# v2.4.40 live UAT plan — Broadcast Controller transport + capacity alerts

Status: **NOT YET RUN.** v2.4.40 has passed the full release gate (compile,
catalog, closure, 4 suites, transform verification) but nothing below has been
observed in the game. Until this file is answered, **v2.4.39 remains the
accepted runtime release.**

Blocks this uses, both already built:

| Role | Block name |
|---|---|
| Broadcast Controller | `Broadcast Controller IOPM` |
| Radio antenna | `Compact Antenna Moon` |

Capture evidence into this directory, verbatim, per `uat/README.md` — prefer
two captures per test (the state that proves intent, the state that proves the
outcome). Name files after the probe: `probe-1-discovery.txt`,
`capacity-warning.txt`, and so on.

---

## Why probe 1 exists at all

Everything about the API shape was verified against the shipped game assemblies
on this machine — `Sandbox.Common.dll` really does declare
`IMyChatBroadcastControllerComponent` with `SendMessage(string)`, and
`IMyComponentContainer.TryGet<T>(out T)` really is the way to reach it. See the
v2.4.40 CHANGELOG entry for the exact members.

**One thing could not be verified offline: the PB whitelist.** The whitelist is
registered in game code, not in a data file, so whether the in-game script
compiler *accepts* `block.Components.TryGet(out chat)` is only answerable by
pasting the script into a programmable block. `check_pb.py` compiles against
hand-written stubs and explicitly does not model the whitelist. So probe 1 is
not a formality — it is the one gate this release genuinely cannot close from
the repo.

If the in-game compile rejects it, **stop**: do not work around it by guessing
at terminal properties. Capture the exact compiler message into
`probe-1-discovery.txt` and treat it as an API finding.

---

## Probe 1 — transport, before any threshold is touched

Paste `IO_Production_Manager_v2.4.40.min.cs` (92,968 chars) into the PB.

Leave `[Alerts] Enabled=false` for the first recompile and confirm the script
still runs exactly as v2.4.39 did — `[IOPM.Status] State=Running`, sorting and
production unchanged, `[IOPM.Alerts] Transport=Disabled`.

Then add / set:

```ini
[Alerts]
Enabled=true
BroadcastController=Broadcast Controller IOPM
WarehouseWarningPercent=85
WarehouseCriticalPercent=95
CooldownSeconds=300
CapacityHysteresisPercent=2
AlertOnStartup=false
```

Remember the upgrade rule: the default block is written only when Custom Data is
**completely empty**, so on an existing PB the `[Alerts]` section has to be typed
in by hand.

| # | Check | Expected in `[IOPM.Alerts]` |
|---|---|---|
| 1.1 | in-game compile accepts `Components.TryGet` | script compiles; no red text |
| 1.2 | exact-name discovery | `ControllersFound=1`, `ControllerWorking=True` |
| 1.3 | component resolves | `ComponentAvailable=True` |
| 1.4 | transport healthy | `Transport=OK` |
| 1.5 | antenna seen | `AntennasBroadcasting=1` (with `Compact Antenna Moon` on and broadcasting) |
| 1.6 | `UseAntenna` read back | `UseAntenna=` matches what the block's own terminal shows |
| 1.7 | silent startup | `Alerts=0`, `LastMessage=none`, `States=AllHealthy` (assuming no pool is over 85%) |

**1.7 is the one to read carefully.** If any pool is already above 85% when
alerting is switched on, the correct behaviour is *silence* — `States` will show
e.g. `WH:ORES=Warning` with `Alerts=0`, because the baseline adopted the
condition rather than announcing it. That is `AlertOnStartup=false` working, not
a failure to alert.

### 1.8 — does `SendMessage(string)` actually reach chat?

The quickest forcing function without waiting on a warehouse: temporarily set
`WarehouseWarningPercent` to just under a pool's current fill (e.g. `Ores` sits
around 95%, so set `WarehouseWarningPercent=50`, `WarehouseCriticalPercent=99`).
Next cycle should produce exactly one `WAREHOUSE WARNING | Ores nn%` in chat.

Capture the chat line **and** `[IOPM.Alerts]` in the same file. Then put the
thresholds back and note that the restore produces a `RECOVERED` line.

### 1.9 — native settings are the player's

On the controller block itself, cycle `BroadcastTarget` through Owner → Faction
→ Everyone and toggle `UseAntenna`. After each change, confirm IOPM has **not**
written any of them back: the terminal still shows what you set, and
`[IOPM.Alerts] UseAntenna` merely reports it.

With `UseAntenna=false` the expected transport is `OK`, **not** an error — that
is a valid grid-local configuration.

### 1.10 — range

With `UseAntenna=true` and `Compact Antenna Moon` broadcasting, walk out past the
antenna's radius and trigger another alert (repeat the 1.8 trick). Record where
reception stops. This measures the *game's* antenna behaviour, not IOPM's: the
script cannot see your suit antenna or your range, and never claims it can.

### 1.11 — failure modes are non-fatal

Do these one at a time, letting a full cycle run between each, and confirm the
script never throws and keeps running:

| Do | Expected `Transport` |
|---|---|
| turn `Broadcast Controller IOPM` **off** | `ControllerNotWorking` |
| rename it to something else | `ControllerNotFound` |
| rename a **second** block to `Broadcast Controller IOPM` | `AmbiguousName` — and `ControllersFound=2` |
| restore the name, turn off `Compact Antenna Moon`, with `UseAntenna=true` | `DegradedNoAntenna` |

While transport is down, `Blocked` should climb and `Alerts` should **not**.
Restore the controller and confirm the next genuine transition still speaks —
that is the proof the state engine was not advanced against messages nobody got.

---

## Probe 2 — capacity state machine, live

Pick one pool and move real material. `Ammo`, `Tools` or `Seeds` are usually the
easiest to swing because they are small. Record the `[IOPM.Warehouse.<cat>]
FillPercent` at each step so the numbers can be checked rather than taken on
trust.

| # | Do | Expected |
|---|---|---|
| 2.1 | bring the pool from below 85% to ~91% | **exactly one** `WAREHOUSE WARNING \| <cat> 91%`; `Alerts` +1 |
| 2.2 | push it to ~92%, let several cycles run | **no further message**; `Alerts` unchanged |
| 2.3 | push past 95% to ~96% | `WAREHOUSE CRITICAL \| <cat> 96%`; `Alerts` +1 |
| 2.4 | let several cycles run at 96–98% | **no repeat**, no "still critical" — `Alerts` unchanged |
| 2.5 | drop to ~94% | **no message** — inside the 95−2 hysteresis band |
| 2.6 | drop to ~92% within 5 min of 2.3 | **no message**; `Suppressed` +1 |
| 2.7 | wait out `CooldownSeconds`, still ~92% | `WAREHOUSE WARNING \| <cat> 92%` |
| 2.8 | drop to ~84.9% | **no message** — inside the 85−2 band |
| 2.9 | drop below ~83% | `RECOVERED \| <cat> capacity back to nn%` |
| 2.10 | drop further, to ~50% | **no message** — already recovered |

2.2, 2.4 and 2.5 are the tests that matter. Anything that produces a message
there is alert spam, and the release should be rejected rather than tuned.

### 2.11 — Overflow uses its own prefix

Fill the Overflow pool past 95% and confirm the message reads
`OVERFLOW CRITICAL | Overflow nn%`, not `WAREHOUSE`.

### 2.12 — a restart does not blast

With the pool sitting in Warning or Critical, recompile the PB (or toggle it off
and on). Expected: **silence**, and `[IOPM.Alerts] States` showing the pool at
its current level with `Alerts=0` for the new session. Then move the pool and
confirm alerting resumes normally.

---

## Probe 3 — nothing else changed

The whole point of putting alerts in their own read-only phase is that the rest
of IOPM is untouched. Confirm against the v2.4.39 smoke test
(`uat/v2.4.39/smoke-test.txt`):

- `[IOPM.Status]` — `State=Running`, `Sorting=OK`, `Production=OK`, `Warnings=0`
- `[IOPM.Machines]` — 20 machines healthy, `Stalled=0`
- `[IOPM.Runtime] PeakInstructions` — note the new value. A 14th phase was added,
  so `CurrentPhase` will now cycle through `AlertEvaluation`; the peak itself
  should not move much, because the alert phase does no enumeration of its own.
- production converges exactly as before; no queue is touched by alerting

Capture `[IOPM.Runtime]` specifically. The instruction ceiling is 50,000 per
invocation and the observed peak has historically been ~25,000 in `DockScan`; if
`AlertEvaluation` shows up anywhere near that, say so.

---

## Known-deferred, carried forward from v2.4.39

Neither is in scope here, and neither should be marked resolved by this UAT:

- **Gunpowder low-stock auto-production** — unreachable without draining the
  ~196,894 Magnesium or giving `Gunpowder` its own `[Stock]` target.
- **Stall recovery** — genuinely rare trigger; `Stalled=0` is expected.
