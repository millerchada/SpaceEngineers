"""Release gate. Run before every deploy; build_pb.py also invokes check_pb.py itself.

    python IO_Production_Manager/tests/run_release_gate.py \
           IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

Five things must hold:

  1. The checker still DETECTS the two compile errors that historically reached the game.
     A checker that has silently stopped working is worse than none - this project has
     already shipped a version of check_pb.py that printed OK without compiling anything.
  2. The checker passes the script under release.
  3. Every tests_*.py suite beside this file passes against the script under release.
  4. build_pb.py's transform verification passes.

Exit code 0 only if all three hold.
"""
import io
import os
import subprocess
import sys

# PROJ is the IOPM project; REPO is the repository root. Shared PB tooling (build_pb,
# check_pb) lives at REPO/tools because every PB script in this repo uses it; the audits and
# fixtures are IOPM-specific and live beside this file.
PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.dirname(PROJ)
FIX = os.path.join(PROJ, 'tests', 'fixtures')
TOOLS = os.path.join(REPO, 'tools')
TESTS = os.path.join(PROJ, 'tests')


def run(args):
    p = subprocess.run([sys.executable] + args, capture_output=True, text=True, cwd=REPO)
    return p.returncode, p.stdout + p.stderr


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    target = sys.argv[1]
    failures = []

    print('=' * 70)
    print('1. NEGATIVE CONTROLS - the checker must still catch known-bad code')

    rc, out = run([os.path.join(TOOLS, 'check_pb.py'), os.path.join(FIX, 'bug_cs0136.cs')])
    ok = rc == 1 and 'CS0136' in out
    print('   pre-fix v2.4.33 (shadowed local) -> CS0136 : %s' % ('PASS' if ok else 'FAIL'))
    if ok:
        for line in out.splitlines():
            if 'CS0136' in line:
                print('     ' + line.strip()[-118:])
    else:
        failures.append('CS0136 fixture not detected (rc=%d)' % rc)

    rc, out = run([os.path.join(TOOLS, 'check_pb.py'), os.path.join(FIX, 'bug_whitelist.cs')])
    ok = rc == 1 and 'PB WHITELIST' in out
    print('   v2.4.3 Comparison<T> -> whitelist screen   : %s' % ('PASS' if ok else 'FAIL'))
    if not ok:
        failures.append('whitelist fixture not detected (rc=%d)' % rc)

    print()
    print('2. POSITIVE CONTROL - the script under release must compile clean')
    rc, out = run([os.path.join(TOOLS, 'check_pb.py'), target])
    ok = rc == 0 and 'OK:' in out
    print('   %s : %s' % (os.path.basename(target), 'PASS' if ok else 'FAIL'))
    if not ok:
        failures.append('target failed check_pb')
        print(out)

    print()
    print('3. CATALOG + CLOSURE AUDITS')
    rc, out = run([os.path.join(TESTS, 'audit_catalog.py'), target])
    print('   catalog integrity : %s' % ('PASS' if rc == 0 else 'FAIL'))
    if rc != 0:
        failures.append('catalog audit failed')
        print(out)
    # Closure is reported but NOT fatal: a manufactured dependency with no recipe is a real
    # hole, yet it does not make the build unsafe to deploy - the affected root simply reports
    # Blocked. Surfacing it every build is the point; blocking the deploy would mean a known,
    # documented gap stops unrelated fixes from shipping.
    rc, out = run([os.path.join(TESTS, 'audit_closure.py'), target])
    tail = [l for l in out.splitlines() if l.startswith('CLOSURE')]
    print('   dependency closure: %s' % (tail[0] if tail else 'no verdict'))
    for line in out.splitlines():
        if line.startswith('MANUFACTURED_MISSING_RECIPE (') or line.startswith('UNKNOWN ('):
            print('     ' + line)

    print()
    print('4. UNIT + INVARIANT SUITES')
    # Every tests_*.py beside this file, discovered rather than listed: a suite that is added
    # and then forgotten is a suite that proves nothing. Each is handed the release target, so
    # they test the script under release rather than whatever version they defaulted to.
    suites = sorted(f for f in os.listdir(TESTS)
                    if f.startswith('tests_') and f.endswith('.py'))
    if not suites:
        failures.append('no tests_*.py suites found - the gate would pass vacuously')
        print('   NO SUITES FOUND')
    for f in suites:
        rc, out = run([os.path.join(TESTS, f), target])
        verdict = [l for l in out.splitlines()
                   if 'CHECKS PASSED' in l or 'CHECKS FAILED' in l or 'FAILED' in l]
        print('   %-28s : %s' % (f, 'PASS' if rc == 0 else 'FAIL'))
        if verdict:
            print('     ' + verdict[-1].strip())
        if rc != 0:
            failures.append('%s failed' % f)
            print(out)

    print()
    print('5. TRANSFORM VERIFICATION - build_pb.py')
    rc, out = run([os.path.join(TOOLS, 'build_pb.py'), target])
    ok = rc == 0 and 'verification passed' in out
    print('   %s : %s' % (os.path.basename(target), 'PASS' if ok else 'FAIL'))
    for line in out.splitlines():
        if 'artifact' in line or 'headroom' in line:
            print('     ' + line.strip())
    if not ok:
        failures.append('build_pb verification failed')

    print()
    print('=' * 70)
    if failures:
        print('RELEASE GATE FAILED')
        for f in failures:
            print('  - ' + f)
        return 1
    print('RELEASE GATE PASSED - safe to paste the .min.cs')
    return 0


if __name__ == '__main__':
    sys.exit(main())
