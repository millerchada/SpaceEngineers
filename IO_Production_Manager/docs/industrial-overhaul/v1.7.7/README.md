# Industrial Overhaul v1.7.7 — live production evidence

GENERATED from `evidence/machines/*.json` by `tools/gen_io_docs.py`.
**Do not hand-edit.** Edit the evidence JSON and re-run the generator — a
hand-edited doc becomes a second source of truth, which is the failure this whole
audit stack exists to prevent.

| File | Contents |
|---|---|
| `production-machines.md` | Every enumerated machine and its full recipe list |
| `production-catalog.md` | Every observed output, with IOPM reconciliation state |
| `production-dependency-notes.md` | Unresolved identities, conflicts, architecture gaps |

## Coverage roster

| Machine | Enumeration | Entries |
|---|---|---:|
| Advanced Assembler | COMPLETE | 17 |
| Assembler | COMPLETE | 16 |
| Auto Loom | COMPLETE | 2 |
| Cement Kiln | COMPLETE | 1 |
| Ceramics Furnace | COMPLETE | 3 |
| Extruder | COMPLETE | 3 |
| Fabricator | COMPLETE | 14 |
| Food Processor | COMPLETE | 24 |
| Microelectronics Factory | COMPLETE | 5 |
| Munitions Factory | COMPLETE | 26 |
| Nano-Assembler | COMPLETE | 13 |
| Nuclear Reprocessor | COMPLETE | 1 |
| Plate Stamp | COMPLETE | 5 |
| Survival Kit | DEFERRED | 0 |
| Synthetics Factory | COMPLETE | 4 |
| Wire Drawer | COMPLETE | 3 |
| **Total** | | **137** |

Scope: non-refinery production blocks currently built on the grid. Refinery and
ore-processing machines are outside this pass, and **every IO production block not
listed above is UNAUDITED, which is not the same as clean.**
