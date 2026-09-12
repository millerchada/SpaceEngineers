# `ConfigChangePending` stays true after convergence — investigation

**Status: cause identified with one environmental assumption I cannot verify from
here. No code changed. Fix proposed, not applied.**

Observed on live v2.4.37: after raising `SolarCell` 200 → 250, IOPM planned,
produced and converged completely — yet `ConfigChangePending=true` persisted
across multiple later diagnostics snapshots with no further config edits.

## 1. Where it is set

One place only:

```csharp
// Main(), every tick
if (!string.Equals(_lastCustomDataSeen, Me.CustomData, StringComparison.Ordinal))
    _cfgDirty = true;
```

`ConfigChangePending` is a pure mirror of `_cfgDirty`, written in
`WriteDiagnostics`. It is never set anywhere else.

## 2. What is intended to clear it

```csharp
if (_cyclePhase == PHASE_IDLE && _cfgDirty) { LoadConfig(); _cfgDirty = false; }
```

The flag means *"a Custom Data change has been seen but not yet applied"*. It is
deliberately **not** applied mid-cycle — the config-snapshot invariant requires
every phase of a cycle to run against one immutable `Config`. So it is expected
to be true for the remainder of the cycle in which an edit lands, and false from
the next `PHASE_IDLE` onward.

`LoadConfig()` unconditionally re-syncs the baseline on its very first lines,
*before* the parse, so even an unparsable edit clears the flag:

```csharp
void LoadConfig() {
  _configErrors.Clear();
  _lastCustomDataSeen = Me.CustomData;   // before TryParse - always runs
  _bpReverse = null;
```

## 3. Can the phased flow latch it?

**No — not on its own.** Traced every path:

- `PHASE_IDLE` is reached every cycle, so the clear always runs.
- `LoadConfig` sets the baseline before any early return, so a parse failure
  cannot leave it set.
- `Me.CustomData` has exactly **two** writers: `WriteDefaultCustomData` (empty
  Custom Data only) and the `WriteDiagnostics` tail.

So for the flag to latch, `Me.CustomData` must persistently differ from
`_lastCustomDataSeen`. In isolation the logic converges.

## 4. The one gap — and it is in the write tail

```csharp
string finalText = CanonicalizeStock(ini, ini.ToString());
if (!string.Equals(finalText, _lastWrittenCustomData, StringComparison.Ordinal)) {
    Me.CustomData = finalText;
    _lastWrittenCustomData = finalText;
    _lastCustomDataSeen = Me.CustomData;   // RE-READ, not finalText
}
```

The re-read is deliberate (v2.4.31): if the engine normalises on write, storing
`finalText` would leave the baseline disagreeing with the block forever.

**But the re-read assumes the write is visible immediately.** If
`Me.CustomData` returns the *previous* value on the same tick it was assigned,
the baseline is set to stale text, and from the next tick onward
`_lastCustomDataSeen != Me.CustomData` — permanently, because IOPM keeps writing
the same steady-state text and keeps re-reading it one revision behind.

That produces exactly the reported symptom: `_cfgDirty` set every cycle, cleared
at every `PHASE_IDLE`, set again immediately, and observed `true` in every
snapshot taken mid-cycle.

**The environmental assumption I cannot verify from here** is whether this
server defers `Me.CustomData` read-back. It is plausible rather than proven:
this is a large multiplayer base, and this project has already documented severe
sync latency on it — tagged LCDs took roughly 90 seconds to refresh (v2.4.x
display investigation). A deferred Custom Data round trip is the same class of
behaviour.

Note the write is also **skipped** whenever the diagnostics text is unchanged,
which in steady state is common — their captures show `LastInstructions=161` and
`LastCycleSeconds=1.83` repeating. On the skip path the baseline is not
refreshed at all, so a single stale read is never self-corrected.

## 5. Is it diagnostics-only?

**No.** A latched flag makes `LoadConfig()` run at *every* `PHASE_IDLE` instead
of only on a real change. Per cycle that costs:

- a full `MyIni` re-parse of Custom Data,
- re-reading and re-validating every `[Stock]` key,
- `_bpReverse = null`, which forces `BlueprintReverseMap()` to rebuild — it
  iterates all 63 ItemDefs calling `TryGetBlueprint` on each.

**Correctness is unaffected.** The reload re-reads identical config, and the
snapshot invariant still holds because the reload still happens only at
`PHASE_IDLE`. This is wasted work and a misleading indicator, not a behavioural
fault. That matters for triage: it is not urgent, and it must not be bundled
with catalog work.

## 6. Proposed fix — one line, correct under both hypotheses

The baseline should accept **either** what we wrote **or** what we read back.
`_lastWrittenCustomData` already holds `finalText`, so no new state is needed:

```csharp
// Main()
string cd = Me.CustomData;
if (!string.Equals(_lastCustomDataSeen, cd, StringComparison.Ordinal) &&
    !string.Equals(_lastWrittenCustomData, cd, StringComparison.Ordinal))
    _cfgDirty = true;
```

- If the engine **normalises** on write, the re-read branch matches — the
  v2.4.31 behaviour is preserved.
- If the engine **defers** the read-back, the written-text branch matches once
  the write lands.
- A genuine player edit differs from **both** and still sets the flag.

It cannot mask a real edit, because a real edit produces text IOPM neither wrote
nor read.

## 7. Tests to add with the fix

1. **Unit-style, in `tests/`:** model the three cases — normalised read-back,
   deferred read-back, genuine edit — and assert `_cfgDirty` is false, false,
   true respectively. Today's single-comparison logic must fail case 2.
2. **A confirming live diagnostic, worth adding first.** A
   `[IOPM.Runtime] ConfigReloads` counter incremented in `LoadConfig()`
   distinguishes the hypotheses definitively: if it climbs by one per cycle on a
   converged base, the latch is real and continuous; if it stays flat while
   `ConfigChangePending=true`, my analysis is wrong and the flag is being read
   mid-cycle by design.

**Recommendation: add the counter first.** It is read-only, cannot regress
anything, and turns an assumption into a measurement — which is the standing
rule in this project after the `IsWorking` and stale-diagnostic episodes. Do not
ship the fix on the strength of this document alone.
