# Endless Drill Mk1

Controller for a walking drill rig — a rotor/piston/welder cycle that extends
its own rail as it drills, so it can advance indefinitely through a tunnel.

Unrelated to IOPM. Short enough to paste directly — no build step.

Design notes and defect history: [Endless_Drill_Mk1_Project_Notes.md](Endless_Drill_Mk1_Project_Notes.md).

## Two builds

| File | Build |
|---|---|
| `Endless_Drill_Mk1_Headless_v0_8_15.cs` | Full controller. Discovers and validates rotors, piston, welder, projectors, ejector and storage. "Headless": LCD discovery and rendering removed, so status comes from the PB detail panel. |
| `Endless_Drill_Mk1_Basic_v1_0_0.cs` | Minimal build for exactly five blocks — Advanced Rotor (Bottom), Advanced Rotor (Top), Piston (Drills), Welder (Drill), Drill Rail Projector. No storage/cargo/ejector monitoring, no multi-projector role system; the single projector is referenced directly. |

Both follow the same proven design for rotor sequencing, piston endpoint
handling, construction-progress gating, controlled stop, commissioning and
diagnostics. Pick Basic for a simple rig, Headless for one with cargo handling
and multiple projectors.

## Commands

Run the block with one of these as the argument:

| Command | Effect |
|---|---|
| `COMMISSION` | Full validation without moving anything. **Run this first on a new rig.** |
| `START` | Begin automatic cycling, with construction-assurance gating. |
| `PAUSE` | Stop piston and welder, preserving current physical state. |
| `RESUME` | Reconstruct state from hardware and continue. |
| `STOP` | Return to `SAFE_RETRACTED`, then stop. |
| `ESTOP` | Immediate piston/welder stop. Requires `RESET`. |
| `RESET` | Clear a latched fault or E-stop after fixing the machine. |
| `STATUS` | Print current state. |

## Operating principles

These are safety properties of the design, not incidental behaviour:

- **Never intentionally detaches both rotors** — the rig cannot drop itself.
- **Uses piston position feedback, not fixed delays**, so it stays correct when
  the server is running slowly.
- **Reconstructs state from hardware** after recompile or reload, and
  **requires `RESUME`** — it never resumes motion on its own.
- **Motion authorization is centralized** in `EvaluateSupervisor()`; that is the
  single place to look when the rig refuses to move.

## If it will not move

Run `STATUS` first. Motion is gated, so a stationary rig is usually a gate that
has not opened rather than a fault:

- A latched fault or E-stop needs `RESET`.
- After a recompile or reload it waits for `RESUME` by design.
- Construction-assurance gating holds motion until the projected rail section is
  actually welded up — check the projector and welder.
- `COMMISSION` re-runs validation and reports what it cannot find or verify.
