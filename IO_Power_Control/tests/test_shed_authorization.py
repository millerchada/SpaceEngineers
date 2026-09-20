"""Behavioural regression test for IO Power Control's shed authorisation.

This is the first test in this repo that RUNS a programmable-block script rather than merely
compiling it. It wraps the script in the same MyGridProgram shape check_pb.py uses, appends a
driver inside the class so it can reach private state, compiles the lot to an exe with csc, and
executes it.

    python IO_Power_Control/tests/test_shed_authorization.py <script.cs>

WHY IT EXISTS. The actuator is authorised by a combination of credible reserve, a latched
Stressed flag and live battery drain, and this project has now found the same defect shape
three separate times in game: something that is deliberately slow to clear being allowed to
authorise an action that should require live evidence. Each time it was found by a person
watching a live base, which is an expensive way to find it.

THE CASE THIS PINS DOWN. After a shed episode ends, _shedActions resets to 0, and the live
drain re-authorisation is gated on `_shedActions > 0` - so it is skipped for the first action
of the NEXT episode, which is authorised by Stressed instead. Stressed stays latched for
StressRecoverSeconds (15s) after the drain stops. Reserve between 0 and ShedReserveMW needs no
battery drain at all to occur. So within that window a shed can be authorised by a stale alarm
with zero live drain.

Run it against v0.1.12 and NEGATIVE_stale_latch fails. That failure is the point.
"""
import io
import os
import re
import subprocess
import sys
import tempfile

CSC = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

PREAMBLE = '''using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;
using VRageMath;
public class Program : MyGridProgram {
'''

# Appended INSIDE the Program class, so it can set the private fields the scenarios need.
DRIVER = r'''
// ---------------------------------------------------------------------------------------
// TEST DRIVER. Not part of the shipped script - appended by tests/test_shed_authorization.py.
// ---------------------------------------------------------------------------------------
int _tFail, _tRun;

void TAssert(string name, bool ok, string detail) {
  _tRun++;
  if (!ok) _tFail++;
  Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "   " + detail);
}

// Build the minimum world ShedStep needs: one home construct and one sheddable production
// block that is genuinely drawing power.
void TScenario(double reserve, bool stressed, int actions, double settleUntil,
               double now, double drain, double bar, int cond) {
  _shed.Clear(); _shedOrder.Clear(); _events.Clear();
  _cons.Clear();
  _cons.Add(new CON { Key = 1, Name = "Test", Home = true });
  _bi.Clear();
  var blk = new TestBlock(2, "Test Refinery", "Required Input: 5.00 MW");
  _bi.Add(new BI { B = blk, F = blk, Id = 2, GridId = 1, Con = 0, Cat = C_PROD,
                   Def = "MyObjectBuilder_Refinery/Test", Sub = "Test",
                   MaxIn = 5.0, CurIn = 5.0, HasCur = true, Src = SRC_DETAIL });
  _reserve = reserve; _stressed = stressed; _shedActions = actions;
  _shedSettleUntil = settleUntil; _now = now;
  _battNetOut = drain; _battBar = bar; _battMaxStored = 3.0; _battStored = 2.0;
  _cond = cond; _shedPhase = actions > 0 ? SP_ARMED : SP_IDLE;
}

public int RunTests() {
  Console.WriteLine("shed authorisation regression - " + VERSION);

  // THE DEFECT. Previous episode ended so _shedActions is 0; Stressed is still latched inside
  // its 15s recovery hold; the settle interval has expired; reserve has dipped below the shed
  // threshold WITHOUT any battery drain. Nothing about the live electrical system justifies an
  // action here - the only thing authorising it is an alarm that has not finished clearing.
  TScenario(2.0, true, 0, 0.0, 100.0, 0.00, 1.17, K_CRITICAL);
  ShedStep();
  TAssert("NEGATIVE_stale_latch_no_live_drain", _shed.Count == 0,
    "shed=" + _shed.Count + " (expected 0; drain 0.00 < bar 1.17, latch is stale)");

  // The positive case must survive the fix. At the moment stress qualifies, drain is >= bar by
  // construction - it is the `material` term of the entry test, held continuously for 10s - so
  // a genuine first action still sheds.
  TScenario(2.0, true, 0, 0.0, 100.0, 5.00, 1.17, K_CRITICAL);
  ShedStep();
  TAssert("POSITIVE_first_action_with_live_drain", _shed.Count == 1,
    "shed=" + _shed.Count + " (expected 1; drain 5.00 >= bar 1.17)");

  // Continuation with the drain gone: already correct in v0.1.12, asserted so a future change
  // cannot quietly undo it.
  TScenario(2.0, true, 1, 0.0, 100.0, 0.00, 1.17, K_CRITICAL);
  ShedStep();
  TAssert("NEGATIVE_continuation_drain_below_bar", _shed.Count == 0,
    "shed=" + _shed.Count + " (expected 0; continuation must re-qualify)");

  // Settle timing is NOT being changed by this fix, so it is pinned here too.
  TScenario(2.0, true, 1, 200.0, 100.0, 5.00, 1.17, K_CRITICAL);
  ShedStep();
  TAssert("NEGATIVE_inside_settle_interval", _shed.Count == 0,
    "shed=" + _shed.Count + " (expected 0; 100s is inside a settle running to 200s)");

  // Episode-end semantics are likewise unchanged.
  TScenario(10.0, true, 1, 0.0, 100.0, 5.00, 1.17, K_CRITICAL);
  ShedStep();
  TAssert("NEGATIVE_reserve_recovered_ends_episode", _shed.Count == 0 && _shedPhase == SP_IDLE,
    "shed=" + _shed.Count + " phase=" + SPN[_shedPhase] + " (expected 0 / idle)");

  // Stress absent entirely: the oldest rule in the actuator, still holding.
  TScenario(2.0, false, 0, 0.0, 100.0, 5.00, 1.17, K_CRITICAL);
  ShedStep();
  TAssert("NEGATIVE_not_stressed", _shed.Count == 0,
    "shed=" + _shed.Count + " (expected 0; RequireStressToShed)");

  Console.WriteLine(_tRun + " run, " + _tFail + " failed");
  return _tFail;
}
'''

# Appended AFTER the Program class. Only needs to satisfy the interfaces ShedStep touches.
DOUBLES = r'''
public class TestGrid : IMyCubeGrid {
  public string CustomName { get; set; }
  public long EntityId { get; set; }
  public bool IsStatic { get { return true; } }
  public TestGrid() { CustomName = "Test Grid"; EntityId = 1; }
}

public class TestBlock : IMyFunctionalBlock {
  string _detail;
  public TestBlock(long id, string name, string detail) {
    EntityId = id; CustomName = name; _detail = detail; Enabled = true;
    CustomData = ""; CubeGrid = new TestGrid();
  }
  public IMyComponentContainer Components { get { return null; } }
  public string CustomName { get; set; }
  public string CustomData { get; set; }
  public string DetailedInfo { get { return _detail; } }
  public long EntityId { get; set; }
  public bool IsWorking { get { return Enabled; } }
  public bool IsFunctional { get { return true; } }
  public bool Enabled { get; set; }
  public bool HasInventory { get { return false; } }
  public int InventoryCount { get { return 0; } }
  public IMyCubeGrid CubeGrid { get; set; }
  public MyDefinitionId BlockDefinition { get { return default(MyDefinitionId); } }
  public IMyInventory GetInventory(int index) { return null; }
  public bool IsSameConstructAs(IMyTerminalBlock other) { return true; }
  public void RequestEnable(bool enable) { Enabled = enable; }
}

public static class TestEntry {
  public static int Main() { return new Program().RunTests(); }
}
'''


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    src = io.open(path, encoding='utf-8').read()
    here = os.path.dirname(os.path.abspath(__file__))
    stubs = os.path.join(here, '..', '..', 'tools', 'se_stubs.cs')
    if not os.path.exists(stubs):
        print('se_stubs.cs not found at ' + stubs)
        return 2
    if not os.path.exists(CSC):
        print('csc.exe not found at %s' % CSC)
        return 2

    tmp = tempfile.mkdtemp(prefix='shedtest_')
    wrapped = os.path.join(tmp, 'wrapped.cs')
    io.open(wrapped, 'w', encoding='utf-8', newline='').write(
        PREAMBLE + src + DRIVER + '\n}\n' + DOUBLES)
    exe = os.path.join(tmp, 'test.exe')
    proc = subprocess.run([CSC, '-nologo', '-t:exe', '-main:TestEntry',
                           '-out:' + exe, wrapped, stubs],
                          capture_output=True, text=True)
    out = proc.stdout + proc.stderr
    if proc.returncode != 0:
        print('--- TEST HARNESS FAILED TO COMPILE ---')
        offset = PREAMBLE.count('\n')
        for line in out.splitlines():
            m = re.match(r'.*wrapped\.cs\((\d+),(\d+)\): (error .*)', line)
            if m:
                print('%s:%d: %s' % (path, int(m.group(1)) - offset, m.group(3)))
            elif ' error ' in line:
                print(line.strip())
        print('The test could not run, which is NOT a verdict on the script.')
        return 2

    run = subprocess.run([exe], capture_output=True, text=True)
    sys.stdout.write(run.stdout)
    if run.stderr.strip():
        sys.stderr.write(run.stderr)
    if run.returncode != 0:
        print('\n%d assertion(s) FAILED.' % run.returncode)
        return 1
    print('\nAll shed-authorisation assertions hold.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
