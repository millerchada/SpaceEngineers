# Machine evidence

Primary evidence, one JSON file per production block, captured by hand from the
in-game blueprint list.

This directory records **what the mod exposes**. It is kept separate from what
IOPM chooses to model so the two can be *compared* rather than conflated —
`tests/audit_machine_coverage.py` reads these files and reports the delta.

## Why this exists

The other two audits both start from what IOPM already knows:

| Audit | Question |
|---|---|
| `audit_catalog.py` | Are the products and recipes IOPM knows internally consistent? |
| `audit_closure.py` | Do the recipe graphs IOPM knows terminate at an intentional boundary? |
| `audit_machine_coverage.py` | **Does IOPM know everything the mod exposes?** |

A product absent from IOPM entirely passes the first two **silently**. It is not
a stock root, so catalog integrity never looks at it; no known recipe references
it, so it never appears as a leaf in the closure graph. Absence is invisible to
any audit that starts from what you already have. This one starts from outside
and compares inward.

## Enumeration states

```
COMPLETE    every visible blueprint category on the block was inspected
PARTIAL     some categories inspected; observed_recipes is a LOWER BOUND
(absent)    machine not yet audited at all
```

**A machine with no file here is UNAUDITED, not clean.** The audit prints that
warning on every run. Do not read a passing coverage report as global coverage.

## Transcription rules

- **Quantities exactly as shown.** No rounding, no normalising.
- **Output yield is not recorded unless the UI stated one.** A single result
  icon is not evidence of yield — `Lightbulb|10` is a measured non-unity yield
  that no icon would have revealed.
- **Physical subtype ids are never inferred from display names.** Where identity
  is unresolved, `identity_hint` is `null` and reconciliation is left to the
  audit. `identity_confidence` records doubt explicitly, including outright
  conflicts with historical repo knowledge.

## Current state

| Machine | Enumeration | Observed recipes |
|---|---|---|
| Advanced Assembler | COMPLETE | 17 |
| Assembler | COMPLETE | 16 |

Every other Industrial Overhaul production block is unaudited.

**One file per block, never merged.** Several outputs appear on more than one
machine — Hydrogen Bottle, Oxygen Bottle, and the MR-20/MR-8P magazines are on
both the Assembler and the Advanced Assembler with identical requirements. That
is an observation about the mod, not a reason to share a file: enumerating one
machine is not evidence about another.
