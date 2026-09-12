# v2.4.40 live UAT plan — Broadcast Controller transport + capacity alerts

Status: **NOT YET RUN.** v2.4.40 has passed the full release gate (compile,
catalog, closure, 4 suites, transform verification) but nothing below has been
observed in the game. Until this file is answered, **v2.4.39 remains the
accepted runtime release.**

This plan matches the **corrected** state semantics. An earlier draft of both
the code and this file disagreed about `DegradedNoAntenna`; that state no longer
exists — see probe 1.11 and §*Antenna state is advisory* in the README.

Blocks this uses, both already built:

| Role | Block name |
|---|---|
| Broadcast Controller | `Broadcast Controller IOPM` |
| Radio antenna | `Compact Antenna Moon` — also the antenna-wake target, probe 4 |

Capture evidence into this directory, verbatim, per `uat/README.md` — prefer
two captures per test (the state that proves intent, the state that proves the
outcome). Name files after the probe: `probe-1-discovery.txt`,
`capacity-warning.txt`, and so on.

---

## The three words this plan turns on

| | |
|---|---|
| **observed** | IOPM has seen the level. Costs nothing and tells nobody. |
| **announced** | the message was handed to the controller and `SendMessage` did not throw. |
| **delivered** | **unknowable.** The API has no acknowledgement. Nothing here tests it, and nothing in IOPM claims it. |

A recovery is only ever sent for a problem that was *announced*. That is what
probes 2.12 and 2.13 exist to prove.

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

Paste `IO_Production_Manager_v2.4.40.min.cs` (96,801 chars) into the PB.

**Do not start this UAT yet.** At 96,801 chars the candidate leaves only 3,199
characters of PB headroom, and that is a decision to take deliberately rather
than discover at the keyboard. See the v2.4.40 antenna-wake CHANGELOG entry.

Leave `[Alerts] Enabled=false` for the first recompile and confirm the script
still runs exactly as v2.4.39 did — `[IOPM.Status] State=Running`, sorting and
production unchanged, `[IOPM.Alerts] Transport=Disabled`.

Then add / set:

```ini
[Alerts]
Enabled=true
BroadcastController=Broadcast Controller IOPM
WakeAntennaForAlerts=true
AlertAntenna=Compact Antenna Moon
WarehouseWarningPercent=85
WarehouseCriticalPercent=95
CooldownSeconds=300
CapacityHysteresisPercent=2
AlertOnStartup=false
```

This is the real configuration to test, antenna wake included. Run probe 1
first with `WakeAntennaForAlerts=false` so transport is proven on its own before
a second moving part is added.

Remember the upgrade rule: the default block is written only when Custom Data is
**completely empty**, so on an existing PB the `[Alerts]` section has to be typed
in by hand.

| # | Check | Expected in `[IOPM.Alerts]` |
|---|---|---|
| 1.1 | in-game compile accepts `Components.TryGet` | script compiles; no red text |
| 1.2 | exact-name discovery | `ControllersFound=1`, `ControllerWorking=True` |
| 1.3 | component resolves | `ComponentAvailable=True` |
| 1.4 | transport healthy | `Transport=OK` |
| 1.5 | antenna seen (advisory) | `AntennasBroadcastingAdvisory=1` with `Compact Antenna Moon` on and broadcasting |
| 1.6 | `UseAntenna` read back (advisory) | `UseAntennaAdvisory=` matches the block's own terminal |
| 1.7 | silent startup | `Alerts=0`, `LastMessage=none` |

**1.7 is the one to read carefully.** If any pool is already above 85% when
alerting is switched on, the correct behaviour is *silence* — `States` will show
e.g. `WH:ORES=Warning/unannounced` with `Alerts=0`. The `/unannounced` suffix is
the point: the level was **observed**, not announced. That is
`AlertOnStartup=false` working, not a failure to alert.

### 1.8 — does `SendMessage(string)` actually reach chat?

The quickest forcing function without waiting on a warehouse: temporarily set
`WarehouseWarningPercent` to just under a pool's current fill (e.g. `Ores` sits
around 95%, so set `WarehouseWarningPercent=50`, `WarehouseCriticalPercent=99`).

Careful — the threshold change does **not** re-baseline. The pool was already
observed at `Warning/unannounced`; lowering the threshold does not change its
level, so no message appears. To force a real transition, set
`WarehouseWarningPercent=50` **and** `WarehouseCriticalPercent=90`, which moves
`Ores` from Warning to Critical — a genuine escalation past the baseline, which
**does** send.

Capture the chat line **and** `[IOPM.Alerts]` in the same file. Confirm `Alerts`
went to `1` and `LastMessage` shows that exact line.

### 1.9 — native settings are the player's

On the controller block itself, cycle `BroadcastTarget` through Owner → Faction
→ Everyone and toggle `UseAntenna`. After each change, confirm IOPM has **not**
written any of them back: the terminal still shows what you set, and
`UseAntennaAdvisory` merely reports it.

With `UseAntenna=false` the expected transport is `OK`, **not** an error — that
is a valid grid-local configuration.

### 1.10 — range

With `UseAntenna=true` and `Compact Antenna Moon` broadcasting, walk out past the
antenna's radius and trigger another alert. Record where reception stops.

This measures the *game's* antenna behaviour, not IOPM's. `Alerts` will still
increment out of range, because it counts messages **handed to the controller**.
That is not a bug — the script cannot see your suit antenna or your range, and
it deliberately does not pretend to.

### 1.11 — antenna off is NOT a transport failure

Run this with `WakeAntennaForAlerts=false`, or the wake feature will simply turn
the antenna back on and you will be testing probe 4 instead.

Turn `Compact Antenna Moon` **off** while `UseAntenna=true`, then force a
transition as in 1.8.

Expected: `Transport=OK`, `AntennasBroadcastingAdvisory=0`, and the message **is
still sent** (`Alerts` increments, `Blocked` does not).

That is deliberate. IOPM can see one antenna fact and cannot see laser antennas,
suit antennas, range, or that `UseAntenna=false` still reaches everyone on the
grid. Refusing to send on that basis would withhold an alert from a player
standing in the base because a mast was switched off. The fact is reported;
behaviour does not depend on it.

### 1.12 — real failure modes are non-fatal and do not consume alerts

Do these one at a time, letting a full cycle run between each, and confirm the
script never throws and keeps running:

| Do | Expected `Transport` |
|---|---|
| turn `Broadcast Controller IOPM` **off** | `ControllerNotWorking` |
| rename it to something else | `ControllerNotFound` |
| rename a **second** block to `Broadcast Controller IOPM` | `AmbiguousName`, `ControllersFound=2` |

While transport is not `OK`, `Blocked` climbs and `Alerts` does **not**.

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
| 2.4 | let several cycles run at 96–98% | **no repeat**, no "still critical"; `Alerts` unchanged |
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

### 2.12 — restart with a problem already present emits NO recovery

**This is the regression test for the defect review found in the first
v2.4.40 build.** In that build a startup baseline was recorded as if it had been
announced, so returning to Healthy produced a `RECOVERED` line for a warning the
player never received.

1. Get a pool into Warning or Critical and let its alert be sent normally, or
   simply find one already high.
2. With `AlertOnStartup=false`, **recompile the PB** (or toggle it off and on).
3. Confirm silence, and that `[IOPM.Alerts] States` shows the pool with the
   **`/unannounced`** suffix — e.g. `WH:AMMO=Critical/unannounced` — and
   `Alerts=0` for the new session.
4. Now bring that pool **directly back to Healthy**, below the hysteresis exit.

**Expected: NO `RECOVERED` message at all.** `Alerts` stays `0`.

Then move the pool up again into Warning and confirm alerting resumes normally —
the baseline must not have disabled anything, only declined to invent history.

### 2.13 — escalation past a baseline still alerts

With the pool sitting at `Warning/unannounced` after a restart, push it past
95%. **Expected: `WAREHOUSE CRITICAL` is sent.** A baseline suppresses replay of
what was already true; it must not suppress something genuinely getting worse.

### 2.14 — transport unavailable during the first baseline

**Regression test for the second review defect.**

1. Set `Enabled=false`. Turn `Broadcast Controller IOPM` **off** (or rename it).
2. Get a pool clearly **Healthy** (well below 85%).
3. Set `Enabled=true`. Confirm `Transport=ControllerNotWorking` (or
   `ControllerNotFound`) and that `Blocked` is climbing while `Alerts=0`.
4. **While transport is still down**, fill that pool past 95%.
5. Restore the controller.

**Expected: `WAREHOUSE CRITICAL` is sent once transport returns.** The condition
arose while the controller was offline and must not have been silently absorbed
into the baseline.

Then the complementary case, which should be silent:

1. With transport down, enable alerts while a pool is **already** Critical.
2. Restore the controller without changing the pool.

**Expected: silence** — that condition predates alerting, exactly as if the
controller had been available the whole time. `States` shows `/unannounced`.

### 2.15 — a send that fails does not lose the alert

Harder to force deliberately, so treat it as opportunistic: if `SendErrors` is
ever non-zero, confirm that `Alerts` did **not** increment for that attempt,
`LastMessage` did not change to the failed line, and the message appears on a
later cycle once the controller is healthy.

The nearest reliable proxy is 1.12 + 2.14: a transition that exists while
transport is not `OK` must still be delivered after transport recovers.

---

## Probe 4 — antenna wake, with `Compact Antenna Moon`

Only start this once probe 1 and probe 2 have passed with
`WakeAntennaForAlerts=false`. Transport and the state engine must be known-good
before a block-moving feature is layered on top of them.

Now set:

```ini
WakeAntennaForAlerts=true
AlertAntenna=Compact Antenna Moon
```

and make sure the Broadcast Controller has `UseAntenna=true` — with
`UseAntenna=false` the antenna is never touched and none of this applies.

Read `[IOPM.Alerts]` every step. The keys that matter here are
`AlertAntennaFound`, `AlertAntennaEnabled`, `WakeState`, `WakeOwned`,
`WakeNote`, `Alerts` and `Blocked`.

### 4.1 — antenna already ON

Leave `Compact Antenna Moon` switched **on**. Force a transition (the 1.8
threshold trick).

Expected:

- the message is sent, `Alerts` +1
- the antenna **stays on** throughout
- `WakeOwned=False` at every point — IOPM never claims a block it did not switch
- `WakeState` never leaves `Idle`

The failure to watch for is IOPM switching the antenna off afterwards. It did
not turn it on, so it must never turn it off.

### 4.2 — antenna initially OFF (the whole point of the feature)

Switch `Compact Antenna Moon` **off**. Confirm `AlertAntennaEnabled=False` and
`WakeState=Idle`. Force a transition.

Expected, in this order — and the ordering is the test:

| # | Expect |
|---|---|
| 1 | the antenna turns **on** |
| 2 | **no message is committed in that same update** — `Alerts` unchanged, `WakeState=Waking`, `WakeOwned=True` |
| 3 | a **later** update sends the alert — `Alerts` +1, `WakeState=Ready` |
| 4 | the antenna returns to **off** |
| 5 | `Alerts` incremented **exactly once** for that transition |
| 6 | `WakeState=Idle`, `WakeOwned=False` |

Capture at least two files: one during `Waking` (which proves nothing was sent
in the enabling update) and one after the restore. A single end-state capture
cannot tell "enabled then sent later" from "enabled and sent immediately", and
that distinction is the reason this is a state machine at all.

### 4.3 — several alerts share one wake

With the antenna off, arrange for **two or more** pools to cross a threshold in
the same evaluation (lowering `WarehouseWarningPercent` sharply is the easy way).

Expected: **one** enable, all the messages, **one** restore. `Alerts` increases
by the number of messages; the antenna is toggled once, not once per alert.

### 4.4 — failed transport during the sequence

Start 4.2 and, while `WakeState=Waking`, turn `Broadcast Controller IOPM`
**off**.

Expected:

- no announcement is committed — `Alerts` unchanged
- `Compact Antenna Moon` is put **back to off** (IOPM restores what it changed
  even though the send never happened)
- `Blocked` increments, `Transport=ControllerNotWorking`
- the alert transition is **still pending**

Then switch the controller back on. The alert must still arrive — and the wake
sequence must run again from the start to deliver it.

### 4.5 — the player wins

While `WakeOwned=True`, switch `Compact Antenna Moon` **off yourself**.

Expected: IOPM **lets go** — `WakeOwned` becomes `False` — and does not
immediately force it back on in that same update. Your setting is the setting.
(It may wake again on a later evaluation if the alert is still pending; what it
must not do is fight you inside the sequence.)

### 4.6 — fail closed on a bad wake configuration

One at a time, with an alert pending:

| Do | Expected |
|---|---|
| `AlertAntenna=` (empty) with wake on | `WakeState=Error`, `WakeNote` says so, `Blocked` climbs, `Alerts` does **not** |
| `AlertAntenna=Nonexistent Antenna` | `AlertAntennaFound=0`, `WakeState=Error`, nothing sent |
| rename a second antenna to `Compact Antenna Moon` | `AlertAntennaFound=2`, `WakeState=Error`, nothing sent — **no arbitrary pick** |

In every case the transition must still be pending afterwards: fix the config
and the alert should then arrive.

### 4.7 — `EnableBroadcasting=false` is yours, not IOPM's

Set `EnableBroadcasting=false` on `Compact Antenna Moon` and leave it powered
off. Trigger an alert.

Expected: IOPM switches `Enabled` on and back off as usual, reports
`AlertAntennaBroadcasting=False`, and **never writes `EnableBroadcasting`**.
Confirm on the block's own terminal that the setting is untouched afterwards.

### 4.8 — restart interruption **cannot strand the antenna on**

This is the edge case the `Storage` marker exists for, and it is worth being
deliberate about.

1. Switch `Compact Antenna Moon` **off**.
2. Force a transition and watch for `WakeState=Waking` / `WakeOwned=True` — the
   antenna is now on **because IOPM turned it on**.
3. **While in that state**, recompile the programmable block (edit and re-save
   the script, or use Recompile). That destroys all in-memory alert state.

Expected: within a cycle or two of the restart, `Compact Antenna Moon` is
switched back **off** by itself, and `WakeNote` reads
`restored Compact Antenna Moon after an interrupted wake`.

Then confirm the marker is not sticky: let a cycle run and check `WakeNote`
clears on the next wake, and that a normal alert still works.

Worth trying the harsher version too if you can: save and reload the world while
`WakeOwned=True`. Same expected outcome — the marker is written to `Storage`
*before* the antenna is enabled, so it survives anything that does not call
`Save()`.

**If the antenna is ever left on after a restart with no alert pending, that is a
defect — record it and stop.** Stranding a player's block is the one outcome
this feature is not allowed to have.

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
