# Live UAT evidence

Durable, versioned records of behaviour observed on the live multiplayer server.
One directory per release; files inside are captured verbatim from in-game
diagnostics and are **never edited afterwards**.

    uat/
      README.md
      live_custom_data.txt     the ONE mutable file - see below
      vX.Y.Z/                  immutable evidence for that release

## Why versioned rather than one rolling file

A single mutable dump answers "what is the base doing right now" and nothing
else. It cannot answer "did this release actually do what we claimed", because
the moment the next test runs the evidence for the previous one is gone. Claims
made in the CHANGELOG then rest on conversation history, which is not a record.

Each `vX.Y.Z/` directory is the evidence for a claim. If a later release
regresses something, the earlier capture is still there to compare against.

## `live_custom_data.txt` is deliberately the exception

It is a **test fixture**, not evidence: `tests/tests_canonicalize_stock.py`
reads it as the realistic input for the 52-check canonicalisation suite. It
tracks whatever the current base looks like, on purpose, so the suite keeps
being exercised against a real config rather than a synthetic one.

It is kept here rather than in `tests/fixtures/` because it is a genuine live
capture; `tests/fixtures/` holds *constructed* inputs (the two deliberately
broken compile fixtures).

## Capture conventions

- Verbatim. No reformatting, no trimming, no correcting.
- Each file states what was being tested and what the expected outcome was, in a
  header comment, so the numbers can be checked rather than taken on trust.
- Prefer two captures per test — the *planning* state that proves the intent and
  the *converged* state that proves the outcome. A single end-state screenshot
  cannot distinguish "worked" from "never ran".

## Index

| Release | Evidence | Result |
|---|---|---|
| v2.4.37 | `solarcell-planning.txt`, `solarcell-complete.txt` | **PASS** — corrected `SolarCell` recipe proven live |
| v2.4.38 | `gunpowder-yield.txt`, `item-identity-dump.txt` | **PASS** — output yield 10 measured, not assumed |
| v2.4.39 | `gunpowder-blueprint.txt`, `smoke-test.txt` | **PASS** — Gunpowder operational; one item DEFERRED |

## `PASS` and `DEFERRED` are different words on purpose

A deferred item is one that could not be reached, not one that was skipped. The
v2.4.39 smoke test records a full IOPM-created Gunpowder job as DEFERRED because
the base holds ~196,894 Magnesium and Gunpowder has no `[Stock]` target, so
support demand cannot bite without an Explosives target near 49,000. The
capability is proven; the *unprompted decision* has not been observed.

Stall recovery carries the same label for the same reason, across the whole life
of the project. Write down which one a result is — "we tested it and it worked"
and "we could not get it to run" are both useful, and confusing them is how a
gap becomes invisible.
