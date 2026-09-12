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
               'try { a.Enabled = false; _wkErr = ')           # restart recovery
    check('every Enabled write is one of the three authorised ones',
          all(any(t.startswith(a) or a in t for a in allowed) for t in ens), str(ens))
    check('there are exactly three of them', len(ens) == 3, str(ens))
    check('IOPM only ever switches the antenna OFF via the block it woke',
          bool(m) and '_wkOwn = _wkAnt;' in m.group(0))
    check('a restore failure keeps ownership so it is retried',
          bool(m) and '_wkState = WK_ERR; _wkErr = "restore failed"; return;' in m.group(0))
    check('the wake marker is written BEFORE the antenna is enabled',
          bool(m) and m.group(0).index('Storage = _cfg.AlertAntenna;')
                      < m.group(0).index('_wkAnt.Enabled = true;'))
    check('an alert must be pending before any wake decision can move a block',
          bool(m) and 'if (!pending) return WA_NONE;' in m.group(0))

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

    report('-- the extraction markers tests_alert_engine.py depends on')
    for name in ('alert-engine', 'alert-transport', 'alert-wake'):
        check('marker pair <%s> present' % name,
              ('// <%s>' % name) in src and ('// </%s>' % name) in src)
    return fails, n[0]


def quiet(*a):
    pass


# Each mutant is a (name, substitution) that breaks exactly one invariant. The rules must
# reject every one of them; if a mutant passes, the corresponding rule is dead.
ALERT_MUTANT = 3  # index of the first mutant that edits alert code

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
     lambda s: s.replace('    Storage = _cfg.AlertAntenna;' + chr(10) + '    _wkAnt.Enabled = true;',
                         '    _wkAnt.Enabled = true;' + chr(10) + '    Storage = _cfg.AlertAntenna;', 1)),
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
