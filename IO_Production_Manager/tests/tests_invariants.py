"""Textual invariant gate for an IOPM source, with its own negative controls.

    python IO_Production_Manager/tests/tests_invariants.py \
           IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

Some of this project's load-bearing rules are not expressible to a compiler and not reachable
from a unit test, because they are statements about what the file does NOT contain:

  - AddQueueItem() is the only production-queue mutation that exists
  - remote/docked inventory never reaches base _onHand
  - the alert subsystem may write exactly ONE block setting - Enabled, on the configured alert
    antenna - and must not mutate a queue, an inventory, or any other native block setting

A comment asserting an invariant is not a check. This is the check. It is deliberately crude -
it reads the source as text - because the alternative, trusting review, is what let a second
capacity calculation, a silently-picked colliding alias and a wrong yield default all ship in
earlier versions of this project.

NEGATIVE CONTROLS RUN FIRST, for the same reason run_release_gate.py runs its own first: a
textual check that has quietly stopped matching anything passes everything. Known-bad mutants
of the real source are fed through the same rules and must be REJECTED before the real source
is allowed to pass.

Every rule is "these call sites, and no others". A legitimate new call site means editing this
file on purpose - the edit is the review.
"""
import io
import os
import re
import sys

PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

ALERT_REGION = r'^// ===== ALERTS.*?^// The script owns \[IOPM\.\*\] ONLY'

# The queue and _onHand rules apply to every version of this script. The alert rules cannot
# apply before the alert subsystem exists, and the release gate is still routinely run against
# the ACCEPTED release rather than only the candidate - so those rules are gated on the
# source's own VERSION constant. A source at or above ALERTS_FROM with no alert region is
# still a hard failure; that is a deleted feature, not an old file.
ALERTS_FROM = (2, 4, 40)
# The sorting instruction guard and balance reserve arrive in 2.4.41. Gated for the same
# reason the alert rules are: an archived release must still be provable against the gate,
# and a rule that silently passes on an old file because the feature was never there proves
# nothing either way.
SORTING_FROM = (2, 4, 41)


def source_version(src):
    m = re.search(r'const string VERSION = "([0-9.]+)"', src)
    return tuple(int(x) for x in m.group(1).split('.')) if m else None


def code_lines(src):
    """Lines that are not pure // comments. Comments describe intent; only code can violate an
    invariant, and a comment that names a forbidden API - as the file header does - must not
    read as a violation."""
    return [l for l in src.split(chr(10)) if l.strip() and not l.strip().startswith('//')]


def run(src, report):
    """Apply every rule. Returns the list of failures. `report` prints, or swallows."""
    lines = code_lines(src)
    fails = []
    n = [0]

    def check(name, ok, detail=''):
        n[0] += 1
        report('  %-58s %s' % (name, 'PASS' if ok else 'FAIL'))
        if not ok:
            fails.append(name + ((' - ' + detail) if detail else ''))

    def hits(pattern, pool=None):
        rx = re.compile(pattern)
        return [t.strip() for t in (lines if pool is None else pool) if rx.search(t)]

    report('-- production queue mutation contract')
    adds = hits(r'\.AddQueueItem\s*\(')
    check('AddQueueItem() has exactly one call site', len(adds) == 1, str(adds))
    for banned in ('ClearQueue', 'RemoveQueueItem', 'MoveQueueItem', 'SwapQueueItem',
                   'InsertQueueItem'):
        found = hits(r'\.' + banned + r'\s*\(')
        check('no %s() anywhere' % banned, not found, str(found))
    check('the sole AddQueueItem call adds a positive amount',
          bool(adds) and 'add' in adds[0], str(adds))

    report('-- remote/docked inventory never enters base _onHand')
    feeds = hits(r'AddTo\(\s*_onHand\s*,')
    allowed = ('_whStock', '_ovStock', '_moStock')
    named = re.findall(r'foreach \(var kv in (_\w+)\) AddTo\(_onHand, kv\.Key, kv\.Value\);', src)
    check('_onHand is fed by exactly 3 statements', len(feeds) == 3, 'found %d' % len(feeds))
    check('_onHand feeders are _whStock/_ovStock/_moStock only',
          sorted(named) == sorted(allowed), str(named))

    ver = source_version(src)
    if ver is not None and ver < ALERTS_FROM:
        report('-- alert rules: NOT APPLICABLE (v%s predates the alert subsystem)'
               % '.'.join(map(str, ver)))
        return fails, n[0]

    report('-- the AlertEvaluation phase is observational')
    m = re.search(ALERT_REGION, src, re.S | re.M)
    check('the alert region is present and delimited', m is not None)
    region = code_lines(m.group(0)) if m else []
    for banned, why in (
            (r'AddQueueItem', 'mutates a production queue'),
            (r'TransferItemTo', 'moves inventory'),
            (r'\.UseAntenna\s*=[^=]', 'overwrites a native Broadcast Controller setting'),
            (r'\.BroadcastTarget\s*=[^=]', 'overwrites Owner/Faction/Everyone targeting'),
            (r'\.CustomName\s*=[^=]', 'renames a block'),
            (r'\.CustomData\s*=[^=]', 'writes Custom Data outside WriteDiagnostics'),
            (r'\.EnableBroadcasting\s*=[^=]', 'reconfigures an antenna'),
            (r'\.Radius\s*=[^=]', 'reconfigures an antenna')):
        found = hits(banned, region)
        check('alert code never %s' % why, not found, str(found))
    check('the alert phase calls PoolStats rather than recomputing fill',
          bool(m) and 'PoolStats(pool, out healthy, out pct)' in m.group(0))

    # THE OBSERVATIONAL RULE, NARROWED RATHER THAN DROPPED (2.4.40 antenna wake). The alert
    # subsystem is now allowed to write exactly one setting on exactly one block. Enumerating
    # the three authorised writes - and requiring the count to be exactly three - keeps a
    # fourth from appearing quietly, which a bare "no .Enabled =" rule could no longer do.
    report('-- Enabled may be written on the configured alert antenna, and nowhere else')
    ens = hits(r'\.Enabled\s*=[^=]', region)
    allowed = ('_wkAnt.Enabled = true;',      # wake
               'if (_wkOwn != null) _wkOwn.Enabled = false;',  # restore what we woke
               'try { a.Enabled = false; }')                  # restart recovery
    check('every Enabled write is one of the three authorised ones',
          all(any(t.startswith(a) or a in t for a in allowed) for t in ens), str(ens))
    check('there are exactly three of them', len(ens) == 3, str(ens))
    check('IOPM only ever switches the antenna OFF via the block it woke',
          bool(m) and '_wkOwn = _wkAnt;' in m.group(0))
    check('a restore failure keeps ownership so it is retried',
          bool(m) and '_wkState = WK_ERR; _wkErr = "restore failed"; return;' in m.group(0))
    check('the wake marker is written BEFORE the antenna is enabled',
          bool(m) and 'Storage = _wkAnt.EntityId.ToString();' in m.group(0)
          and m.group(0).find('Storage = _wkAnt.EntityId.ToString();')
              < m.group(0).find('_wkAnt.Enabled = true;'))
    check('an alert must be pending before any wake decision can move a block',
          bool(m) and 'if (!pending) return WA_NONE;' in m.group(0))
    check('a failed send retires the wake instead of camping on the antenna',
          bool(m) and 'if (retire) return WA_REST;' in m.group(0)
          and 'if (failed) _wkRetire = true;' in m.group(0))

    # RESTART RECOVERY. Three separate rules, because the three ways it went wrong are
    # independent: it was unreachable, it gave up too early, and it keyed on a mutable name.
    report('-- interrupted-wake recovery')
    mm = re.search(r'public void Main\(.*?\n}', src, re.S)
    mainbody = mm.group(0) if mm else ''
    check('recovery is called from Main(), not only from the alert phase',
          'WakeService();' in mainbody)
    # AFTER LoadConfig (so it sees a setting that has just removed the alert phase) and BEFORE
    # the GeneralEnabled gate that decides whether a cycle starts at all.
    check('  ... after the config snapshot applies, before the cycle gate',
          0 <= mainbody.find('LoadConfig();') < mainbody.find('WakeService();')
          < (mainbody.find('_cfg.GeneralEnabled') if '_cfg.GeneralEnabled' in mainbody else -1))
    check('no one-shot latch can strand a wake taken later in the runtime',
          '_wkDone' not in src)
    check('Main restores an active wake once its phase can no longer run',
          bool(m) and 'case WS_RESTORE: WakeOff(); break;' in m.group(0)
          and 'return (generalEnabled && alertsEnabled && wakeCfg) ? WS_NONE : WS_RESTORE;'
              in m.group(0))
    check('an active wake is never treated as an interrupted one',
          bool(m) and 'if (!owned) return WS_RECOVER;' in m.group(0))
    check('a throwing enable does NOT clear the recovery marker',
          bool(m) and '_wkOwn = null; _wkState = WK_ERR; _wkErr = "enable failed; marker '
                      'retained for recovery";' in m.group(0))
    check('recovery has exactly one call site', len(hits(r'WakeRecover\(\);', lines)) == 1)
    check('the Main backstop has exactly one call site',
          len(hits(r'WakeService\(\);', lines)) == 1)
    check('a failed restore KEEPS the marker rather than clearing it',
          bool(m) and '_wkErr = "interrupted wake: restore of " + id + " failed, marker '
                      'retained";' in m.group(0).replace(chr(10) + '      ', ' '))
    check('recovery resolves by EntityId, which a rename cannot defeat',
          bool(m) and 'GridTerminalSystem.GetBlockWithId(id)' in m.group(0)
          and 'Storage = _wkAnt.EntityId.ToString();' in m.group(0))
    # `region` is CODE ONLY - the comment above WakeRecover explains that construct membership
    # is deliberately not checked, and a raw text search would match that explanation.
    check('recovery restores by id and does NOT require the same construct',
          bool(m) and 'WakeRecoverAction(has, parsed, b != null, a != null)' in m.group(0)
          and not hits(r'IsSameConstructAs', region))
    check('an unresolved id is retried, never treated as nothing to restore',
          bool(m) and 'if (!resolved) return WR_RETRY;' in m.group(0))
    check('  ... and the RETRY branch never clears the marker',
          bool(m) and 'case WR_RETRY:' in m.group(0)
          and 'Storage' not in m.group(0).split('case WR_RETRY:')[1].split('break;')[0])
    check('only a corrupt marker is discarded',
          bool(m) and 'if (!parsed) return WR_CORRUPT;' in m.group(0)
          and 'if (!isAntenna) return WR_CORRUPT;' in m.group(0))

    # The rules below are the ones the v2.4.40 review found broken. Each names an EXACT line,
    # because each defect was wrong by a single token and a looser pattern would have matched
    # the broken version just as happily.
    report('-- announcement is earned, not assumed')
    body = m.group(0) if m else ''
    check('a startup baseline never marks a level announced',
          'if (silent) { st.Ack = lvl; st.Ann = false; return ""; }' in body)
    check('an alert is committed ONLY when AlertSend returned true',
          'if (AlertSend(_alPendM[i])) AlertCommit(_alPendK[i], _alPendM[i]);' in body)
    check('AlertCommit has exactly one call site', len(hits(r'AlertCommit\(_al', region)) == 1)
    check('AlertSend reports success rather than returning void',
          'bool AlertSend(string msg) {' in body)
    check('antenna state is not an input to the transport verdict',
          'static string AlertTransport(bool enabled, string cfgError, int found, '
          'bool working, bool component) {' in body)
    check('no transport state claims a send it cannot make',
          'DegradedNoAntenna' not in body)
    check('Storage carries the wake marker and nothing else',
          len(hits(r'Storage\s*=', lines)) == len(hits(r'Storage\s*=', region))
          and len(hits(r'Storage\s*=', region)) == 5)
    check('Save() still persists nothing', 'public void Save() { }' in src)

    # THE SORTING PHASE MUST BE ABLE TO STOP ITSELF. Bounding transfers was never the same as
    # bounding work: on a live 52-container base the phase reached 46,685 of 50,000 instructions
    # with nothing watching, and raising MaxTransfersPerCycle terminated the script outright.
    # Every loop that enumerates containers owes a guard, and this counts them so one cannot be
    # quietly dropped again.
    if ver is not None and ver < SORTING_FROM:
        report('-- sorting budget rules: NOT APPLICABLE (v%s predates the guard)'
               % '.'.join(map(str, ver)))
        report('-- the extraction markers tests_alert_engine.py depends on')
        for name in ('alert-engine', 'alert-transport', 'alert-wake'):
            check('marker pair <%s> present' % name,
                  ('// <%s>' % name) in src and ('// </%s>' % name) in src)
        return fails, n[0]

    report('-- sorting cannot exceed the instruction ceiling')
    guards = hits(r'if \(!InstrOk\(\)\) break;', lines)
    check('every container-enumerating loop is guarded', len(guards) == 6, str(len(guards)))
    check('the guard samples the live counter',
          'int cur = Runtime.CurrentInstructionCount;' in src
          and 'InstrOver(cur, Runtime.MaxInstructionCount, _cfg.InstrBudgetPercent)' in src)
    check('the peak is sampled INSIDE the phase, not only at the end of Main',
          'if (cur > _peakInstructions) { _peakInstructions = cur; _peakPhaseName = PN[PHASE_SORTING]; }'
          in src)
    check('the budget percent is clamped below 100',
          'Math.Max(10, Math.Min(95, _ini.Get("Sorting", "InstructionBudgetPercent")' in src)
    report('-- balance gets a reserve routing cannot spend')
    check('the reserve is withheld before routing runs',
          'int reserve = _cfg.BalanceEnabled ? BalanceReserve(tb, _cfg.BalanceReservePercent) : 0;' in src
          and 'int rb = tb - reserve;' in src)
    check('  ... and rejoins the allowance afterwards', 'tb = rb + reserve;' in src)
    check('the reserve can never exceed half the allowance',
          'return Math.Min(tb / 2, Math.Max(1, (int)(tb * (pct / 100.0))));' in src)
    check('machine-output evacuation still runs first and unguarded',
          'rb = RouteSources(_mOut, rb, false);' in src)
    check('a skipped Organize clears its diagnostics for ANY reason',
          'if (!_cfg.OrganizeEnabled || tb <= 0 || !InstrOk()) ResetOrganizeDiag();' in src)

    report('-- the extraction markers tests_alert_engine.py depends on')
    for name in ('alert-engine', 'alert-transport', 'alert-wake', 'sorting-budget'):
        check('marker pair <%s> present' % name,
              ('// <%s>' % name) in src and ('// </%s>' % name) in src)
    return fails, n[0]


def quiet(*a):
    pass


# Each mutant is a (name, substitution) that breaks exactly one invariant. The rules must
# reject every one of them; if a mutant passes, the corresponding rule is dead.
ALERT_MUTANT = 3  # index of the first mutant that edits alert code
SORT_MUTANTS = ('an unguarded container loop in the sorting phase',
                'the balance reserve handed straight back to routing',
                'an instruction budget that allows the full ceiling',
                'Organize left stale when it runs out of budget')

MUTANTS = [
    ('a second AddQueueItem call site',
     lambda s: s.replace('void WriteDiagnostics() {',
                         'void WriteDiagnostics() {\n  _mach[0].AddQueueItem(default(MyDefinitionId), (MyFixedPoint)1);', 1)),
    ('a ClearQueue call',
     lambda s: s.replace('void WriteDiagnostics() {',
                         'void WriteDiagnostics() {\n  _mach[0].ClearQueue();', 1)),
    ('docked stock feeding _onHand',
     lambda s: s.replace('foreach (var kv in _moStock) AddTo(_onHand, kv.Key, kv.Value);',
                         'foreach (var kv in _loHave) AddTo(_onHand, kv.Key, kv.Value);', 1)),
    ('the alert phase overwriting UseAntenna',
     lambda s: s.replace('    } catch { _bcChat = null; _bcComponent = false; }',
                         '      ch.UseAntenna = true;\n    } catch { _bcChat = null; _bcComponent = false; }', 1)),
    ('the alert phase renaming the controller',
     lambda s: s.replace('bool AlertSend(string msg) {',
                         'bool AlertSend(string msg) {\n  _bcBlock.CustomName = "IOPM";', 1)),
    ('a startup baseline that fabricates an announcement',
     lambda s: s.replace('if (silent) { st.Ack = lvl; st.Ann = false; return ""; }',
                         'if (silent) { st.Ack = lvl; st.Ann = true; return ""; }', 1)),
    ('committing an alert without a successful send',
     lambda s: s.replace('if (AlertSend(_alPendM[i])) AlertCommit(_alPendK[i], _alPendM[i]);',
                         'AlertSend(_alPendM[i]); AlertCommit(_alPendK[i], _alPendM[i]);', 1)),
    ('antenna state smuggled back into the transport verdict',
     lambda s: s.replace('static bool AlertCanSend(string transport) { return transport == "OK"; }',
                         'static bool AlertCanSend(string transport) { return transport == "OK" '
                         '|| transport == "DegradedNoAntenna"; }', 1)),
    ('the alert phase switching the CONTROLLER off',
     lambda s: s.replace('void WakeDrop() {',
                         'void WakeDrop() {' + chr(10) + '  _bcBlock.Enabled = false;', 1)),
    ('the alert phase reconfiguring the antenna it woke',
     lambda s: s.replace('    _wkAnt.Enabled = true;',
                         '    _wkAnt.Enabled = true;' + chr(10) + '    _wkAnt.EnableBroadcasting = true;', 1)),
    ('waking the antenna with no alert pending',
     lambda s: s.replace('  if (!pending) return WA_NONE;',
                         '  if (!pending) return antEnabled ? WA_NONE : WA_WAKE;', 1)),
    ('enabling the antenna before recording that IOPM owns it',
     lambda s: s.replace('    Storage = _wkAnt.EntityId.ToString();' + chr(10) + '    _wkAnt.Enabled = true;',
                         '    _wkAnt.Enabled = true;' + chr(10) + '    Storage = _wkAnt.EntityId.ToString();', 1)),
    ('the Main backstop removed, leaving recovery to the alert phase',
     lambda s: s.replace('  WakeService();' + chr(10), '', 1)),
    ('recovery clearing the marker BEFORE the restore succeeds',
     lambda s: s.replace('  try { a.Enabled = false; }',
                         '  Storage = ""; _wkDone = true;' + chr(10) + '  try { a.Enabled = false; }', 1)),
    ('a failed restore giving up on the marker',
     lambda s: s.replace('    _wkErr = "interrupted wake: restore of " + id + " failed, marker retained";',
                         '    Storage = ""; _wkDone = true;', 1)),
    ('recovery abandoning an antenna it merely cannot see right now',
     lambda s: s.replace('  if (!resolved) return WR_RETRY;',
                         '  if (!resolved) return WR_CORRUPT;', 1)),
    ('recovery giving up when the antenna moved construct',
     lambda s: s.replace('  if (!isAntenna) return WR_CORRUPT;',
                         '  if (!isAntenna || true) return WR_CORRUPT;', 1)),
    ('a throwing enable destroying the recovery marker',
     lambda s: s.replace('    _wkOwn = null; _wkState = WK_ERR; _wkErr = "enable failed; marker retained for recovery";',
                         '    _wkOwn = null; Storage = ""; _wkState = WK_ERR; _wkErr = "enable failed";', 1)),
    ('a one-shot latch reintroduced in front of recovery',
     lambda s: s.replace('void WakeRecover() {',
                         'bool _wkDone;' + chr(10) + 'void WakeRecover() {' + chr(10) +
                         '  if (_wkDone) return;' + chr(10) + '  _wkDone = true;', 1)),
    ('Main never restoring a wake whose phase was disabled',
     lambda s: s.replace('  return (generalEnabled && alertsEnabled && wakeCfg) ? WS_NONE : WS_RESTORE;',
                         '  return WS_NONE;', 1)),
    ('an active wake mistaken for an interrupted one',
     lambda s: s.replace('  if (!owned) return WS_RECOVER;', '  return WS_RECOVER;', 1)),
    ('a failed send camping on the antenna',
     lambda s: s.replace('    if (retire) return WA_REST;', '', 1)),
    ('an unguarded container loop in the sorting phase',
     lambda s: s.replace('    if (!InstrOk()) break; // stop cleanly; the rest of this pass resumes next cycle' + chr(10), '', 1)),
    ('the balance reserve handed straight back to routing',
     lambda s: s.replace('  int rb = tb - reserve;', '  int rb = tb;', 1)),
    ('an instruction budget that allows the full ceiling',
     lambda s: s.replace('Math.Max(10, Math.Min(95, _ini.Get("Sorting", "InstructionBudgetPercent")',
                         'Math.Max(10, Math.Min(100, _ini.Get("Sorting", "InstructionBudgetPercent")', 1)),
    ('Organize left stale when it runs out of budget',
     lambda s: s.replace('if (!_cfg.OrganizeEnabled || tb <= 0 || !InstrOk()) ResetOrganizeDiag();',
                         'if (!_cfg.OrganizeEnabled) ResetOrganizeDiag();', 1)),
    ('a deleted extraction marker',
     lambda s: s.replace('// </alert-engine>', '', 1)),
]


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    target = sys.argv[1]
    src = io.open(target, encoding='utf-8').read()
    problems = []

    print('=' * 70)
    print('1. NEGATIVE CONTROLS - each mutant must be REJECTED')
    ver = source_version(src)
    mutants = MUTANTS if (ver is None or ver >= ALERTS_FROM) else MUTANTS[:ALERT_MUTANT]
    if ver is not None and ver < SORTING_FROM:
        mutants = [m for m in mutants if m[0] not in SORT_MUTANTS]
    for name, mutate in mutants:
        mutated = mutate(src)
        if mutated == src:
            print('   %-52s INCONCLUSIVE' % name)
            problems.append('mutant "%s" did not apply - it no longer matches the source, so '
                            'nothing was tested' % name)
            continue
        fails, _ = run(mutated, quiet)
        print('   %-52s %s' % (name, 'rejected' if fails else 'NOT DETECTED'))
        if not fails:
            problems.append('mutant "%s" passed every rule - that rule is dead' % name)

    print()
    print('2. THE REAL SOURCE - %s' % os.path.basename(target))
    fails, checks = run(src, print)
    problems += fails

    print()
    if problems:
        print('INVARIANT GATE FAILED')
        for p in problems:
            print('  - ' + p)
        return 1
    print('ALL %d INVARIANT CHECKS PASSED (+%d negative controls)' % (checks, len(mutants)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
