"""Release gate. Run before every deploy; build_pb.py also invokes check_pb.py itself.

    python tests/run_release_gate.py IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

Three things must hold:

  1. The checker still DETECTS the two compile errors that historically reached the game.
     A checker that has silently stopped working is worse than none - this project has
     already shipped a version of check_pb.py that printed OK without compiling anything.
  2. The checker passes the script under release.
  3. build_pb.py's transform verification passes.

Exit code 0 only if all three hold.
"""
import io
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIX = os.path.join(ROOT, 'tests', 'fixtures')


def run(args):
    p = subprocess.run([sys.executable] + args, capture_output=True, text=True, cwd=ROOT)
    return p.returncode, p.stdout + p.stderr


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    target = sys.argv[1]
    failures = []

    print('=' * 70)
    print('1. NEGATIVE CONTROLS - the checker must still catch known-bad code')

    rc, out = run(['check_pb.py', os.path.join(FIX, 'bug_cs0136.cs')])
    ok = rc == 1 and 'CS0136' in out
    print('   pre-fix v2.4.33 (shadowed local) -> CS0136 : %s' % ('PASS' if ok else 'FAIL'))
    if ok:
        for line in out.splitlines():
            if 'CS0136' in line:
                print('     ' + line.strip()[-118:])
    else:
        failures.append('CS0136 fixture not detected (rc=%d)' % rc)

    rc, out = run(['check_pb.py', os.path.join(FIX, 'bug_whitelist.cs')])
    ok = rc == 1 and 'PB WHITELIST' in out
    print('   v2.4.3 Comparison<T> -> whitelist screen   : %s' % ('PASS' if ok else 'FAIL'))
    if not ok:
        failures.append('whitelist fixture not detected (rc=%d)' % rc)

    print()
    print('2. POSITIVE CONTROL - the script under release must compile clean')
    rc, out = run(['check_pb.py', target])
    ok = rc == 0 and 'OK:' in out
    print('   %s : %s' % (os.path.basename(target), 'PASS' if ok else 'FAIL'))
    if not ok:
        failures.append('target failed check_pb')
        print(out)

    print()
    print('3. TRANSFORM VERIFICATION - build_pb.py')
    rc, out = run(['build_pb.py', target])
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
