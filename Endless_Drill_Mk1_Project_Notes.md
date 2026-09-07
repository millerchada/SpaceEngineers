# Endless Drill Mk1 — Project Notes

## What this is

A Space Engineers programmable-block (PB) script controlling an "Endless Drill" — a walking mining rig. The rig alternates two rotor anchors (top and bottom) while a piston extends and retracts, letting an attached drill head walk forward indefinitely along a recursively-projected rail, welding new rail segments into place as it goes.

Machine layout:

```
Top Rotor
  ↓
Piston (0.0–7.3 m travel)
  ↓
Bottom Rotor
  ↓
Moving drill platform
```

Current validated script: **`Endless_Drill_Mk1_Headless_v0_8_15.cs`** (headless build — no LCD rendering, Custom Data / Echo diagnostics only).

## Architecture — deliberate scope boundary

This script is **PB1, the motion controller**, and it was scoped narrowly on purpose after an earlier, broader "v1" attempt failed live testing (material-evacuation logic interacting badly with motion states, erratic behavior during stop, drills/welder disabled unexpectedly). That experience is why the current design draws a hard line:

**PB1 owns:**
- Piston motion and endpoint detection
- Top/bottom rotor attach/detach sequencing and attachment-stability confirmation
- Welder control tied to construction assurance
- Projector discovery, role classification, and **cycle-projector pinning** (evaluating the specific projector pinned to the current build cycle, not whichever projector a generic "active projector" selector currently favors)
- Construction-progress gating (a cycle must show real projected-block progress before anchors transfer)
- Storage-capacity hold/resume (see below)
- Motion safety interlocks (never both rotors detached, extend/retract only with the correct single rotor attached, etc.)
- Commissioning, pause/resume, emergency stop, controlled stop, reload-state reconstruction (reload never auto-resumes motion — always requires `RESUME`)
- Diagnostics: `STATUS` (on-screen) and `REPORT` (full Custom Data dump)

**PB1 explicitly does NOT own** (reserved for a separate, not-yet-built PB):
- Cargo management / general inventory balancing
- Factory production, assembler queues, component demand
- Logistics planning, conveyor/component supply
- Any automatic decision based on *why* a block didn't get welded (e.g., missing Motors) — PB1 only tracks aggregate confirmed-block progress, never inspects specific component shortages

This split was validated in practice: during a `ONCE` run, the projector's blueprint skipped some conveyor-tube blocks for lack of Motor components but still completed enough other blocks to satisfy the construction-assurance threshold. That's correct, expected behavior under this architecture — component supply is someone else's (future) problem.

## Commands

`COMMISSION`, `START`, `PAUSE`, `RESUME`, `STOP`, `ESTOP`, `RESET`, `STATUS`, `EJECTOR`, `PROJECTORS`, `REPORT`, `ONCE`. (`LCDS` is a no-op stub in this headless build.)

## Version history — key decisions and defects found

Each of these was found through live testing, reviewed, patched narrowly, and re-validated before moving on. This is the "why" behind the current code that the script's own header intentionally keeps brief.

- **v0.8.5** — `ExecuteControlledStop()` bug: the extend/retract driving cases never checked for reaching the physical endpoint before re-arming motion, so a `STOP` issued mid-motion drove the piston against its limit forever. Also fixed: bottom-rotor attach was firing at the wrong (retracted) position instead of at the extended endpoint.
- **v0.8.6** — `ExecuteControlledStop()` was forcing the welder off on every tick during the return leg, and again before top-attachment stability was confirmed — making it impossible to finish welding unfinished components during a controlled stop. Welder now stays on through the whole non-terminal recovery, off only once truly parked.
- **v0.8.7** — Construction assurance was evaluating whatever projector a generic "active projector" selector currently favored, rather than the specific projector pinned at cycle start. The moment the pinned projector *completed* (stopped projecting), it looked identical to "changed away," discarding real progress and holding motion forever. Fixed by resolving the pinned cycle projector directly by entity ID.
- **v0.8.8** — Added storage-capacity protection: motion pauses automatically when storage fills, resumes automatically when it clears (95% hold / 80% resume hysteresis), wired in as a supervisor-level hold — no fault, no `RESET`/`RESUME` needed.
- **v0.8.9** — The construction-progress timeout was still counting elapsed time *during* a storage hold, since the "is construction expected" check didn't know motion was intentionally paused. Fixed by freezing that timer during storage hold.
- **v0.8.10** — Live testing found aggregate storage utilization could stay low (lots of drill inventories acting as incidental buffer) even with actual cargo containers completely full. Added tracking of the worst single cargo container as a second hold trigger.
- **v0.8.11** — The supervisor's hold reason always blamed "aggregate utilization" even when a cargo container was the actual cause. Fixed to report whichever signal(s) actually triggered.
- **v0.8.12** — Pure size-reduction pass (script was approaching the 100,000-character PB limit). Condensed changelog comments, consolidated duplicated report-writing code, merged near-duplicate helper methods, shortened a handful of internal field names. No behavior change — validated against the full existing test matrix.
- **v0.8.13** — Two changes: retraction speed raised from 0.10 to 1.0 m/s (extension speed unchanged), and storage hold no longer blocks `Retract`/`AttachTop` — those are the non-ore-producing "return leg" of the cycle, so a storage hold now lets the machine finish returning to a safe, fully-parked state instead of getting stuck mid-transfer. The *next* cycle still can't start until storage clears.
- **v0.8.14** — Live testing found the *cargo-container* hold trigger was too eager: one lopsided cargo container could hit ~99% while overall storage was nowhere near full, stopping drilling unnecessarily. Replaced the control signal entirely: storage hold is now driven by the **single fullest individual drill inventory** (direct evidence of conveyor backpressure), not cargo-container fullness or aggregate volume — those are now diagnostic-only fields in the report.
- **v0.8.15** — Physical rig rebuild: the piston was reversed (now facing up instead of down), which changed the bottom rotor's correct mated angle from 180° to 0°. One-constant fix (`BOTTOM_EXPECTED_DEG`), confirmed piston position/velocity/endpoint logic is orientation-agnostic (it's all in the piston's own local frame, unaffected by physical mounting direction). Also required an in-game fix unrelated to the script: enabling "Share Inertia Tensor" on both rotors.

## Current status

`v0.8.15` has passed commissioning and a full `ONCE` cycle on the rebuilt rig (detach → extend → construction-assurance threshold met → bottom attach → top detach → retract → top attach → back to `SAFE_RETRACTED`, one full cycle counted, no faults). Continuous `START` testing is in progress as of this writing.

## Next planned feature (not yet implemented)

**Drill on/off control**, following the same pattern as the existing welder control:
- Drills **on** before extension begins (start of the `Extend` operation, before commanding piston motion).
- Drills **off** on a **delay** after the machine comes to rest — not instantly on stop.

Still to be decided before implementation: exactly what "coming to rest" should mean as the trigger (reaching `SAFE_RETRACTED` at full cycle end vs. any time the piston stops moving vs. specifically after retraction), and how long the off-delay should be.

## Working conventions established over this project

- Every functional change gets a new version file (`v0.8.N` → `v0.8.N+1`); the prior validated version is never modified in place.
- Changes are scoped as narrowly as possible per release, with an explicit list of what was *not* touched.
- Before implementing anything non-trivial: review current source, identify exact methods/fields affected, propose a diff plan, and wait for approval before writing code.
- Deliverables consistently include a change summary, exact modified methods, a character-count accounting (the script is close to the 100,000-character PB limit), and a regression checklist tied to previously-validated behavior.
- Safety invariants (never both rotors detached, endpoint-before-motion checks, controlled-stop behavior, fault-latching rules) are treated as non-negotiable and are called out explicitly whenever a change is anywhere near them.
