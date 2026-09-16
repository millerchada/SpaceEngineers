"""Version immutability, enforced instead of merely asserted.

    python IO_Production_Manager/tests/tests_versions.py \
           IO_Production_Manager/IO_Production_Manager_vX.Y.Z.cs

The rule has been written down since early in this project:

    "Each version is an immutable file: a copy plus a delta, never edited in place, so a
     regression isolates to one delta."  (README)
    "A version that never compiled may be fixed in place."  (HANDOFF, invariant 13)

Every other invariant here has a gate and a negative control. This one had neither, and it
drifted for SEVEN commits without a single complaint: v2.4.40 was edited in place from
a0d861b through 5201a2c while it was being pasted into a live programmable block. The cost
was exactly what the rule predicts - two different builds reported `Version=2.4.40`, and
working out which one was running had to be done by spotting ABSENT diagnostic keys rather
than by reading the version. That information is not recoverable.

So this suite makes the rule mechanical:

  1. A version file's VERSION constant must match the version in its filename. Catches the
     copy-and-forget-to-bump, which is the easy half of the mistake.
  2. A file listed in version_lock.json must hash to the recorded value. Frozen means frozen.
     The candidate - the one version NOT in the lock - is free to change; everything else is
     a release someone may be running.

Freezing a version is a deliberate edit to version_lock.json, which is the point: the edit is
the review. There is no way to drift into it.
"""
import hashlib
import io
import json
import os
import re
import sys

PROJ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCK = os.path.join(PROJ, 'tests', 'version_lock.json')
NAME_RX = re.compile(r'^IO_Production_Manager_v(\d+\.\d+\.\d+)\.cs$')
CONST_RX = re.compile(r'const string VERSION = "([0-9.]+)"')


def digest(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def version_files():
    """(version, path, text) for every maintained version source, project root and archive.
    .min.cs artifacts are excluded: they are gitignored, regenerable, and not the file of
    record."""
    out = []
    for dirpath, _dirs, files in os.walk(PROJ):   # archive/ is under PROJ; walking both double-counts
        for f in files:
            m = NAME_RX.match(f)
            if not m:
                continue
            p = os.path.join(dirpath, f)
            out.append((m.group(1), p, io.open(p, encoding='utf-8').read()))
    out.sort(key=lambda r: [int(x) for x in r[0].split('.')])
    return out


def main():
    fails = []
    n = [0]

    def check(name, ok, detail=''):
        n[0] += 1
        print('  %-58s %s' % (name, 'PASS' if ok else 'FAIL'))
        if not ok:
            fails.append(name + ((' - ' + detail) if detail else ''))

    files = version_files()
    raw = json.load(io.open(LOCK, encoding='utf-8')) if os.path.exists(LOCK) else {}
    lock = raw.get('locked', {})
    # HISTORY IS FROZEN, INCLUDING ITS MISTAKES. Three archived releases carry a VERSION
    # constant that disagrees with their filename - the same copy-and-forget-to-bump this
    # suite exists to catch, committed years before it existed. Correcting them now would mean
    # editing frozen releases, which is precisely the thing being prevented. They are recorded
    # here instead, so they are acknowledged rather than silently tolerated, and so a NEW
    # mismatch still fails.
    known = raw.get('const_mismatch', {})

    print('=' * 70)
    print('1. NEGATIVE CONTROLS - the rules must reject known-bad inputs')
    # A locked file with a single byte changed. If this passes, the hash check is dead.
    if files and lock:
        ver, _p, text = next(((v, p, t) for v, p, t in files if v in lock), (None, None, None))
        if ver is None:
            check('a locked version exists to test against', False, 'lock file is empty')
        else:
            check('one changed byte in a frozen version is rejected',
                  digest(text + ' ') != lock[ver])
            check('  ... and the unmodified file is accepted', digest(text) == lock[ver])
    # A file whose VERSION constant disagrees with its name.
    bad = 'const string VERSION = "9.9.9";'
    check('a VERSION constant that contradicts the filename is detectable',
          CONST_RX.search(bad).group(1) == '9.9.9')

    print()
    print('2. EVERY VERSION FILE')
    check('version sources were found at all', len(files) >= 2, str(len(files)))
    for ver, path, text in files:
        m = CONST_RX.search(text)
        got = m.group(1) if m else None
        where = os.path.relpath(path, PROJ).replace(os.sep, '/')
        if ver in known:
            ok = got == known[ver]
            print('  %-58s %s' % ('%s: known historical mismatch (%s)' % (where, got),
                                  'noted' if ok else 'FAIL'))
            n[0] += 1
            if not ok:
                fails.append('%s changed its recorded mismatch: %r' % (where, got))
            continue
        check('%s declares VERSION "%s"' % (where, ver), got == ver, 'found %r' % got)

    print()
    print('3. FROZEN VERSIONS ARE UNCHANGED')
    if not lock:
        check('version_lock.json exists and is populated', False,
              'no lock file - immutability is unenforced')
    candidates = [v for v, _p, _t in files if v not in lock]
    for ver, path, text in files:
        where = os.path.relpath(path, PROJ).replace(os.sep, '/')
        if ver not in lock:
            print('  %-58s %s' % ('%s is the open candidate, not locked' % where, 'skip'))
            continue
        check('%s unchanged since it was frozen' % where, digest(text) == lock[ver],
              'sha256 %s, expected %s' % (digest(text)[:16], lock[ver][:16]))
    # Exactly one version may be open at a time. Two candidates means one of them is a release
    # someone could be running, with nothing stopping it from being edited underneath them.
    check('exactly one version is an open candidate', len(candidates) == 1, str(candidates))

    print()
    if fails:
        print('%d of %d VERSION CHECKS FAILED' % (len(fails), n[0]))
        for f in fails:
            print('  - ' + f)
        print()
        print('  To freeze the candidate deliberately, add its sha256 to')
        print('  tests/version_lock.json. To change a frozen version: do not. Copy it to a')
        print('  new version file and apply the delta there.')
        return 1
    print('ALL %d VERSION CHECKS PASSED (candidate: v%s)' % (n[0], candidates[0]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
