"""Compile-check a Space Engineers programmable-block script on this machine.

The game is otherwise the FIRST thing to see a compile error, which costs a paste-deploy
round trip and, worse, tempts a "it built fine" claim that only means build_pb.py's brace
counter was happy. build_pb.py verifies the TRANSFORM (literals survive, structure matches);
it has no idea whether the C# is valid. This checks that.

    python check_pb.py IO_Production_Manager/IO_Production_Manager_v2.4.33.cs

It wraps the script in the MyGridProgram shape the game uses, compiles it with csc against
se_stubs.cs, and reports errors with line numbers mapped back to the real source file.

WHAT IT CATCHES: syntax errors, shadowed locals (CS0136 - cost an in-game compile in 2.4.33),
duplicate locals (CS0128), unassigned locals, wrong argument counts, unknown members, missing
returns, unreachable code.

WHAT IT DOES NOT CATCH:
  - The PB WHITELIST. csc accepts Comparison<T>; the game rejects it (cost an in-game compile
    in 2.4.3). Screened separately below by name.
  - Stub drift. se_stubs.cs describes API SHAPES by hand. A member missing from the stubs is
    reported as an error in YOUR script, which is misleading - so unknown-member and
    unknown-type errors are listed under a separate heading as probable stub gaps. Add the
    member to se_stubs.cs rather than changing the script.
  - Behaviour of any kind. Nothing here runs.

A clean result means "the C# is valid and uses the API shapes correctly", not "this works".
"""
import io
import os
import re
import subprocess
import sys
import tempfile

CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# Constructs the PB whitelist rejects even though csc accepts them. Each entry is
# (regex, explanation). Kept short and specific: a false positive here blocks a deploy.
PROHIBITED = [
    (r'\bComparison\s*<', 'Comparison<T> is prohibited by the PB whitelist (cost an in-game '
                          'compile in 2.4.3). Use an inline lambda on List.Sort instead.'),
    (r'\bSystem\.Reflection\b', 'System.Reflection is prohibited by the PB whitelist.'),
    (r'\bSystem\.IO\b', 'System.IO is prohibited by the PB whitelist.'),
    (r'\bThread\b', 'Threading is prohibited by the PB whitelist.'),
]

PREAMBLE = '''using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using VRageMath;
// The class MUST be named Program: the script declares a constructor `public Program()`,
// which is only a constructor if the enclosing class shares that name. Naming the wrapper
// anything else turns it into a method with no return type (CS1520) - a phantom error in
// every script this tool checks.
public class Program : MyGridProgram {
'''


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    src = io.open(path, encoding='utf-8').read()
    here = os.path.dirname(os.path.abspath(__file__))
    stubs = os.path.join(here, 'se_stubs.cs')
    if not os.path.exists(stubs):
        print('se_stubs.cs not found next to check_pb.py'); return 2
    if not os.path.exists(CSC):
        print('csc.exe not found at %s - cannot compile-check' % CSC); return 2

    problems = 0

    # --- whitelist screen (csc cannot do this) ---
    for rx, why in PROHIBITED:
        for m in re.finditer(rx, src):
            line = src.count('\n', 0, m.start()) + 1
            print('%s:%d: PB WHITELIST: %s' % (path, line, why))
            problems += 1

    # --- compile ---
    tmp = tempfile.mkdtemp(prefix='pbcheck_')
    wrapped = os.path.join(tmp, 'wrapped.cs')
    offset = PREAMBLE.count('\n')   # lines added before the script body
    io.open(wrapped, 'w', encoding='utf-8', newline='').write(PREAMBLE + src + '\n}\n')
    proc = subprocess.run(
        [CSC, '-nologo', '-t:library',
         '-out:' + os.path.join(tmp, 'out.dll'), wrapped, stubs],
        capture_output=True, text=True)
    out = proc.stdout + proc.stderr

    real, stubgaps, unparsed = [], [], []
    for line in out.splitlines():
        m = re.match(r'.*wrapped\.cs\((\d+),(\d+)\): error (CS\d+): (.*)', line)
        if not m:
            # ANY error line without a file/line prefix is a COMPILER-LEVEL failure - a bad
            # flag, a missing reference, a crash. These must never be silently ignored: an
            # earlier version of this script matched only prefixed errors, so an invalid
            # -langversion aborted the compile and the run still printed OK. A checker that
            # reports success when it did not actually compile is worse than no checker.
            if ' error ' in line or line.lower().startswith('error'):
                unparsed.append(line.strip())
            continue
        ln, col, code, msg = int(m.group(1)) - offset, m.group(2), m.group(3), m.group(4)
        entry = '%s:%d:%s: %s: %s' % (path, ln, col, code, msg)
        # CS0246/CS0234 = unknown type, CS1061/CS0117 = unknown member: almost always a gap in
        # se_stubs.cs rather than a fault in the script.
        (stubgaps if code in ('CS0246', 'CS0234', 'CS1061', 'CS0117', 'CS1503', 'CS1501')
         else real).append(entry)

    if unparsed:
        print('\n--- COMPILER DID NOT RUN PROPERLY (%d) ---' % len(unparsed))
        for e in unparsed:
            print('  ' + e)
        print('  The check is INCONCLUSIVE - this is a check_pb.py/toolchain fault, not a '
              'verdict on the script. Fix this before trusting any result.')
        return 2
    if real:
        print('\n--- REAL ERRORS (%d) ---' % len(real))
        for e in real:
            print(e)
        problems += len(real)
    if stubgaps:
        print('\n--- probable se_stubs.cs gaps (%d) - add the member to the stubs, do NOT '
              'change the script ---' % len(stubgaps))
        for e in stubgaps[:25]:
            print(e)
        if len(stubgaps) > 25:
            print('  ... and %d more' % (len(stubgaps) - 25))

    if proc.returncode != 0 and not real and not stubgaps:
        print('\nCOMPILER EXITED %d with no errors parsed - INCONCLUSIVE. Do not treat this '
              'as a pass.' % proc.returncode)
        return 2
    if problems == 0 and not stubgaps:
        print('OK: %s compiles clean against the stubs, no prohibited constructs.' % path)
    elif problems == 0:
        print('\nNo script errors. Stub gaps above are a check_pb limitation, not a defect.')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
