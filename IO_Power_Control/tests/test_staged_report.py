"""Staged diagnostic-report regression test for IO Power Control.

    python IO_Power_Control/tests/test_staged_report.py <script.cs>

WHY IT EXISTS. v0.1.17 fixed startup and discovery, survived to DISCOVERING on the production
grid for the first time - and then died the moment `scan` was run. The changelog had claimed
the command was safe because the forced rediscovery had been removed from it. That removal was
real and it was not sufficient. What remained in WriteScan was:

  * a detail top-up deliberately raised to InstrBudgetPercent = 90, spending up to 45,000 of
    the 50,000 hard limit BEFORE the report emitted its first character, with the budget check
    as the LAST statement in the loop body so the crossing iteration was paid on top;
  * an unbounded Measure() over the whole model with what was left;
  * eight further full walks of the model, with string formatting, with no budget check at all.

On 1928 functional blocks that is a guaranteed termination, not an unlucky one. The claim that
the command was safe was made without doing this arithmetic, and no test covered `scan` - which
is why 31 assertions passed and the script still died.

WHAT THIS CAN AND CANNOT PROVE. It cannot reproduce Space Engineers' instruction counter. It
simulates instruction PRESSURE - the stub charges a fixed amount per read of
CurrentInstructionCount - and proves that the report yields, resumes, never exceeds the hard
limit on any single tick, never publishes a partial report, and survives a rescan completing
underneath it. The real 50,000 proof is the in-game RUNTIME COST section.
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

DRIVER = r'''
// ---------------------------------------------------------------------------------------
// TEST DRIVER - appended by tests/test_staged_report.py, not part of the shipped script.
// ---------------------------------------------------------------------------------------
int _tFail, _tRun;
void TA(string name, bool ok, string detail) {
  _tRun++;
  if (!ok) _tFail++;
  Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "   " + detail);
}

void TBoot() {
  int guard = 0;
  while (_boot != B_DONE && guard++ < 100000) BootStep();
}

int TRunDiscovery(int maxTicks) {
  int ticks = 0;
  while (_ds != D_IDLE && ticks < maxTicks) {
    Runtime.ResetCounter();
    DiscoverStep();
    ticks++;
  }
  return ticks;
}

public int RunTests() {
  Console.WriteLine("staged report - " + VERSION);
  var gts = new TestGTS();
  GridTerminalSystem = gts;
  var me = new TestBlock(1, "Programmable Block", "", "LargeProgrammableBlock");
  Me = me;
  gts.Blocks.Add(me);

  int N = 2000;
  for (int i = 0; i < N; i++) {
    string sub, nm;
    int k = i % 5;
    if (k == 0) { sub = "LargeRefinery"; nm = "Refinery " + i; }
    else if (k == 1) { sub = "AdvancedAssembler"; nm = "Assembler " + i; }
    else if (k == 2) { sub = "LargeJumpDrive"; nm = "Jump Drive " + i; }
    else if (k == 3) { sub = "SomeUnknownWidget"; nm = "Widget " + i; }
    else { sub = "CementKiln"; nm = "Kiln " + i; }
    gts.Blocks.Add(new PlainBlock(100 + i, nm, "Required Input: 1.00 MW", sub));
  }

  TBoot();

  // ---- A report must be refused before there is anything to report on. Publishing a
  // diagnostic built from no model would be indistinguishable from a grid with no blocks.
  string refusal = StartReport(false);
  TA("REPORT_refused_before_model_published", _rs == R_IDLE && refusal.Length > 0,
    "state=" + RN[_rs] + " msg=" + refusal);

  Runtime.Step = 40;                 // ~750 reads to reach a 60% budget of 50000
  _c.InstrBudgetPercent = 60;
  _ds = D_FETCH;
  TRunDiscovery(5000);
  TA("SETUP_model_published", _modelReady && _bi.Count == N + 1,
    "blocks=" + _bi.Count);

  // A distinctive cached figure. If the report recomputed the snapshot instead of printing it,
  // this value could not survive into the text - Measure() over doubles with no power interface
  // would overwrite it with zero. This is the assertion that pins "prints, does not measure".
  _demand = 42.5;
  _lastMeasureAt = _now - 3.0;

  string cdBefore = me.CustomData == null ? "" : me.CustomData;
  string queued = StartReport(false);
  TA("REPORT_queues_without_running", _rs != R_IDLE && _rIdx == 0
      && (me.CustomData == null ? "" : me.CustomData) == cdBefore,
    "state=" + RN[_rs] + " msg=" + queued);

  string busy = StartReport(true);
  TA("REPORT_second_scan_refused_while_running", _rs != R_IDLE && busy.IndexOf("already") >= 0,
    "msg=" + busy);

  // ---- drive it, watching the hard limit and Custom Data on every single tick
  int ticks = 0, overHard = 0, publishedEarly = 0, worstTick = 0;
  while (_rs != R_IDLE && ticks < 20000) {
    Runtime.ResetCounter();
    ReportStep();
    int spent = Runtime.CurrentInstructionCount;
    if (spent > worstTick) worstTick = spent;
    if (spent > Runtime.MaxInstructionCount) overHard++;
    ticks++;
    if (_rs != R_IDLE && (me.CustomData == null ? "" : me.CustomData) != cdBefore)
      publishedEarly++;
  }
  TA("REPORT_takes_multiple_ticks", ticks > 1, "ticks=" + ticks + " for " + (N + 1) + " blocks");
  TA("REPORT_never_exceeds_the_hard_limit_on_a_tick", overHard == 0,
    "ticksOverHardLimit=" + overHard + " worstTick=" + worstTick
    + "/" + Runtime.MaxInstructionCount);
  TA("REPORT_never_published_early", publishedEarly == 0, "earlyWrites=" + publishedEarly);
  TA("REPORT_completes", _rs == R_IDLE, "state=" + RN[_rs] + " ticks=" + ticks);

  string rpt = me.CustomData == null ? "" : me.CustomData;
  TA("REPORT_published_atomically", rpt != cdBefore && rpt.IndexOf(RPTMARK) >= 0,
    "len=" + rpt.Length);

  // ---- every section must be present. A report that yields and resumes but silently drops
  // sections is worse than one that dies: it looks complete.
  string[] want = new string[] {
    "POWER CONTROL SCAN", "SnapshotAge=", "== CONSTRUCTS ==", "== PRODUCERS ==",
    "== BATTERIES ==", "== GAS / STEAM TANKS ==", "== CONSUMERS BY DEFINITION ==",
    "== UNKNOWN CONSUMERS", "== BROWNOUT", "== FAST RING", "== HISTORY",
    "== RUNTIME COST ==", "== CANDIDATE AUDIT", "== CANDIDATES REFUSED",
    "== SHED STATE ==", "== EVENTS ==" };
  int missing = 0; string missMsg = "";
  for (int i = 0; i < want.Length; i++)
    if (rpt.IndexOf(want[i], StringComparison.Ordinal) < 0) {
      missing++;
      if (missMsg.Length == 0) missMsg = want[i];
    }
  TA("REPORT_contains_every_section", missing == 0,
    "missing=" + missing + " " + missMsg);

  TA("REPORT_prints_the_cached_snapshot_not_a_new_one",
    rpt.IndexOf("Demand=42.5MW", StringComparison.Ordinal) >= 0,
    "the cached demand figure must survive into the text (Measure() is not called from here)");

  TA("REPORT_detail_refresh_covered_the_model", _rDetail >= _bi.Count,
    "refreshed=" + _rDetail + "/" + _bi.Count);

  TA("REPORT_defaults_to_the_summary_not_every_block",
    rpt.IndexOf("== ALL FUNCTIONAL BLOCKS ==", StringComparison.Ordinal) < 0,
    "plain `scan` must not emit the per-block table");

  // ---- `scan all` does emit it
  cdBefore = me.CustomData;
  StartReport(true);
  int t2 = 0;
  while (_rs != R_IDLE && t2 < 20000) { Runtime.ResetCounter(); ReportStep(); t2++; }
  rpt = me.CustomData;
  TA("REPORT_all_emits_the_block_table",
    rpt.IndexOf("== ALL FUNCTIONAL BLOCKS ==", StringComparison.Ordinal) >= 0
      && rpt.Length > cdBefore.Length + 20000,
    "ticks=" + t2 + " len=" + rpt.Length + " vs plain " + cdBefore.Length);

  // ---- a rescan completing underneath the report must not corrupt it. The report holds the
  // model it started with, for the same reason discovery publishes by reference swap.
  cdBefore = me.CustomData;
  StartReport(false);
  var startedWith = _rBi;
  Runtime.ResetCounter(); ReportStep();          // get the report under way
  _ds = D_FETCH;
  TRunDiscovery(5000);                           // full rescan publishes a NEW _bi
  bool swapped = !object.ReferenceEquals(_bi, startedWith);
  int t3 = 0, held = 0;
  while (_rs != R_IDLE && t3 < 20000) {
    Runtime.ResetCounter();
    ReportStep();
    t3++;
    if (_rs != R_IDLE && !object.ReferenceEquals(_rBi, startedWith)) held++;
  }
  TA("REPORT_holds_its_own_snapshot_across_a_rescan", swapped && held == 0 && _rs == R_IDLE,
    "liveModelSwapped=" + swapped + " reportSnapshotChanged=" + held + " ticks=" + t3);
  TA("REPORT_still_publishes_after_a_rescan",
    me.CustomData != cdBefore && me.CustomData.IndexOf(RPTMARK) >= 0, "len="
    + me.CustomData.Length);

  // ---- a report that can never make progress must abandon and write NOTHING. A leaked
  // half-built report is a slow way to lose the diagnostic entirely.
  cdBefore = me.CustomData;
  StartReport(false);
  Runtime.Step = 60000;                          // every budget check trips immediately
  int t4 = 0;
  while (_rs != R_IDLE && t4 < R_TICK_CAP + 50) { Runtime.ResetCounter(); ReportStep(); t4++; }
  TA("REPORT_abandons_when_it_cannot_progress",
    _rs == R_IDLE && t4 <= R_TICK_CAP + 1 && me.CustomData == cdBefore,
    "ticks=" + t4 + " cap=" + R_TICK_CAP + " customDataUnchanged="
    + (me.CustomData == cdBefore));
  Runtime.Step = 40;

  // ---- the detail top-up itself must stop before the cliff, not one block after it
  Runtime.ResetCounter();
  _c.ScanChunk = 100000;                         // ask it for the whole model in one call
  RefreshDetailChunk();
  int after = Runtime.CurrentInstructionCount;
  TA("DETAIL_chunk_stops_within_the_hard_limit", after <= Runtime.MaxInstructionCount,
    "spent=" + after + "/" + Runtime.MaxInstructionCount
    + " (check is now the FIRST statement in the loop body)");
  _c.ScanChunk = 40;

  // ---- peak cost must be recorded on paths that are not a full tick. This is the defect the
  // production screenshot showed: `peak 0` while the same screen printed instr 30075.
  _tickPeak = 0; _peakWhat = "";
  Runtime.ResetCounter();
  Main("help", UpdateType.Terminal);
  TA("PEAK_recorded_on_the_command_path", _tickPeak > 0 && _peakWhat.Length > 0,
    "peak=" + _tickPeak + " during=" + _peakWhat);

  // ---- PER-TICK BUDGET. v0.1.18 gave every resumable phase the same soft ceiling, and the
  // production grid reached PEAK RUN 47327 of 50000 on a rescan tick with every individual
  // phase inside its budget: discovery yielded at 30449, then the tail that MUST still run -
  // detail top-up, Measure, Condition, Render - spent another 16878. Nothing was over its own
  // budget. Nothing asked what had to run after it.
  int max = Runtime.MaxInstructionCount;
  int margin = max / 5;
  int floorC = max / 25;
  int soft = max * _c.InstrBudgetPercent / 100;

  _modelReady = false;
  _phPeak[PH_DETAIL] = 900; _phPeak[PH_MEASURE] = 16400;
  _phPeak[PH_CONTROL] = 20; _phPeak[PH_RENDER] = 700;
  TA("BUDGET_first_start_gets_the_full_soft_budget", TailCost() == 0 && Ceiling() == soft,
    "tail=" + TailCost() + " ceiling=" + Ceiling() + " soft=" + soft);

  _modelReady = true;
  int tail = TailCost();
  int ceil = Ceiling();
  TA("BUDGET_reserves_the_measured_tail", tail == 18020 && ceil < soft,
    "tail=" + tail + " ceiling=" + ceil + " soft=" + soft);
  // The assertion that encodes the defect: a phase yielding at the ceiling, plus everything
  // that must follow it, has to fit inside the HARD limit with room to spare.
  TA("BUDGET_a_full_tick_fits_under_the_hard_limit", ceil + tail + margin <= max,
    "ceiling=" + ceil + " + tail=" + tail + " + margin=" + margin + " = "
    + (ceil + tail + margin) + " vs hard limit " + max);
  TA("BUDGET_reproduces_the_v0118_overrun_without_the_reservation",
    soft + tail > max - 2000,
    "the OLD ceiling " + soft + " plus the same tail " + tail + " = " + (soft + tail)
    + ", which is what reached 47327 in production");

  // A tail so expensive that nothing is affordable must still let a phase make progress, or a
  // rescan never completes and the model goes stale forever - a quiet failure that looks
  // exactly like a working script.
  _phPeak[PH_MEASURE] = 49000;
  TA("BUDGET_never_starves_a_phase_completely", Ceiling() == floorC,
    "ceiling=" + Ceiling() + " floor=" + floorC + " (progress guarantee, tail unaffordable)");
  _phPeak[PH_DETAIL] = 0; _phPeak[PH_MEASURE] = 0;
  _phPeak[PH_CONTROL] = 0; _phPeak[PH_RENDER] = 0;

  Console.WriteLine(_tRun + " run, " + _tFail + " failed");
  return _tFail;
}
'''

DOUBLES = r'''
public class TestGrid : IMyCubeGrid {
  public string CustomName { get; set; }
  public long EntityId { get; set; }
  public bool IsStatic { get { return true; } }
  public TestGrid() { CustomName = "Prod Grid"; EntityId = 1; }
}

public interface ITestSub { string Sub { get; } }

public class PlainBlock : IMyFunctionalBlock, ITestSub {
  static TestGrid _grid = new TestGrid();
  string _detail, _sub;
  public PlainBlock(long id, string name, string detail, string sub) {
    EntityId = id; CustomName = name; _detail = detail; _sub = sub;
    Enabled = true; CustomData = ""; CubeGrid = _grid;
  }
  public string Sub { get { return _sub; } }
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

public class TestBlock : IMyFunctionalBlock, IMyProgrammableBlock, ITestSub {
  static TestGrid _shared = new TestGrid();
  string _detail, _sub;
  public TestBlock(long id, string name, string detail, string sub) {
    EntityId = id; CustomName = name; _detail = detail; _sub = sub;
    Enabled = true; CustomData = ""; CubeGrid = _shared;
  }
  public string Sub { get { return _sub; } }
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
  public IMyTextSurface GetSurface(int index) { return null; }
  public int SurfaceCount { get { return 0; } }
}

public class TestGTS : IMyGridTerminalSystem {
  public List<IMyTerminalBlock> Blocks = new List<IMyTerminalBlock>();
  public void GetBlocks(List<IMyTerminalBlock> blocks) {
    blocks.Clear();
    for (int i = 0; i < Blocks.Count; i++) blocks.Add(Blocks[i]);
  }
  public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class {
    if (blocks != null) blocks.Clear();
  }
  public IMyTerminalBlock GetBlockWithName(string name) { return null; }
  public IMyTerminalBlock GetBlockWithId(long id) { return null; }
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
    if not os.path.exists(CSC):
        print('csc.exe not found at %s' % CSC)
        return 2

    src = src.replace('r.Sub = d.SubtypeId == null ? "" : d.SubtypeId;',
                      'r.Sub = (b is ITestSub) ? ((ITestSub)b).Sub : "";', 1)

    tmp = tempfile.mkdtemp(prefix='reporttest_')
    wrapped = os.path.join(tmp, 'wrapped.cs')
    io.open(wrapped, 'w', encoding='utf-8', newline='').write(
        PREAMBLE + src + DRIVER + '\n}\n' + DOUBLES)
    exe = os.path.join(tmp, 'test.exe')
    proc = subprocess.run([CSC, '-nologo', '-t:exe', '-main:TestEntry',
                           '-out:' + exe, wrapped, stubs],
                          capture_output=True, text=True)
    if proc.returncode != 0:
        print('--- TEST HARNESS FAILED TO COMPILE ---')
        offset = PREAMBLE.count('\n')
        for line in (proc.stdout + proc.stderr).splitlines():
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
    print('\nStaged report holds. The real instruction proof is the in-game RUNTIME COST '
          'section, not this.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
