"""Staged-discovery regression test for IO Power Control.

    python IO_Power_Control/tests/test_staged_discovery.py <script.cs>

WHY IT EXISTS. v0.1.16 terminated on its first execution on the production grid with the hard
50,000-instruction error. `Discover()` walked every block on the construct in one invocation
and only `RefreshDetailChunk` was ever budget-aware. The fix makes initialisation and discovery
resumable and publishes the model atomically - which introduces a new and worse failure mode if
it is got wrong: a PARTIALLY BUILT model being treated as authoritative. That is what these
assertions are for.

WHAT THIS CAN AND CANNOT PROVE. It cannot reproduce Space Engineers' instruction counter, so it
cannot tell you the real cost of anything. What it can do is simulate instruction PRESSURE - the
stub charges a fixed amount per read of CurrentInstructionCount - and then prove that the
chunked phases yield, resume, skip nothing, duplicate nothing, and never publish early. The
real 50,000 proof has to come from the in-game RUNTIME COST section.

It also pins CLASSIFICATION PARITY: the optimised classifier (lowercase + first-letter bitmask
+ ordinal search) must return exactly what the original (case-insensitive search over the hint
list in order) returned, for every hint key and a spread of unmatched blocks. An optimisation
that quietly recategorises a block would be far worse than the cost it saved.
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
// TEST DRIVER - appended by tests/test_staged_discovery.py, not part of the shipped script.
// ---------------------------------------------------------------------------------------
int _tFail, _tRun;
void TA(string name, bool ok, string detail) {
  _tRun++;
  if (!ok) _tFail++;
  Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + "   " + detail);
}

// The original classifier, reimplemented here rather than kept in the shipped script: a parity
// test has to compare against the OLD algorithm, and the old algorithm has no business
// surviving in production code just so a test can call it.
int LegacyHintCat(string sub, string name) {
  for (int i = 0; i < HINTS.Length; i++) {
    var p = HINTS[i].Split('=');
    if (p.Length != 2) continue;
    int c = IdxOf(CATN, p[1]);
    if (c < 0) continue;
    if (sub.IndexOf(p[0], StringComparison.OrdinalIgnoreCase) >= 0) return c;
  }
  for (int i = 0; i < HINTS.Length; i++) {
    var p = HINTS[i].Split('=');
    if (p.Length != 2) continue;
    int c = IdxOf(CATN, p[1]);
    if (c < 0) continue;
    if (name.IndexOf(p[0], StringComparison.OrdinalIgnoreCase) >= 0) return c;
  }
  return C_UNK;
}

void TBoot() {
  int guard = 0;
  while (_boot != B_DONE && guard++ < 100000) BootStep();
}

// Drives ticks of discovery under simulated instruction pressure, resetting the counter each
// tick exactly as the game does.
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
  Console.WriteLine("staged discovery - " + VERSION);
  var gts = new TestGTS();
  GridTerminalSystem = gts;
  var me = new TestBlock(1, "Programmable Block", "", "LargeProgrammableBlock");
  Me = me;
  gts.Blocks.Add(me);

  // A production-scale spread: refineries, assemblers, jump drives, batteries, and a large tail
  // of blocks that no interface claims, which is the population that used to cost up to ~176
  // case-insensitive searches each.
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
  TA("BOOT_completes", _boot == B_DONE && _hintN > 0 && _builtin.Count > 300,
    "boot=" + BN[_boot] + " hints=" + _hintN + " catalog=" + _builtin.Count);

  // ---- staged discovery under pressure
  Runtime.Step = 40;            // ~750 reads to reach a 60% budget of 50000
  _c.InstrBudgetPercent = 60;
  _ds = D_FETCH;
  int midChecks = 0, publishedEarly = 0, liveDuring = 0;
  int ticks = 0;
  while (_ds != D_IDLE && ticks < 5000) {
    Runtime.ResetCounter();
    DiscoverStep();
    ticks++;
    if (_ds != D_IDLE) {
      midChecks++;
      if (_modelReady) publishedEarly++;       // must never publish before the pass completes
      liveDuring += _bi.Count;                 // live model must stay empty on a FIRST start
    }
  }
  TA("DISCOVERY_takes_multiple_ticks", ticks > 1, "ticks=" + ticks + " for " + N + "+1 blocks");
  TA("DISCOVERY_yielded_on_budget", _yielded, "yielded=" + _yielded);
  TA("DISCOVERY_never_published_early", publishedEarly == 0,
    "earlyPublishes=" + publishedEarly + " over " + midChecks + " mid-pass checks");
  TA("DISCOVERY_live_model_empty_until_publish_on_first_start", liveDuring == 0,
    "sum of live _bi.Count during the pass = " + liveDuring);
  TA("DISCOVERY_publishes", _modelReady && _ds == D_IDLE,
    "modelReady=" + _modelReady + " state=" + DN[_ds]);

  // ---- no block skipped, none counted twice
  var seen = new HashSet<long>();
  int dupes = 0;
  for (int i = 0; i < _bi.Count; i++) if (!seen.Add(_bi[i].Id)) dupes++;
  TA("DISCOVERY_no_duplicates_across_chunks", dupes == 0, "duplicates=" + dupes);
  TA("DISCOVERY_no_blocks_skipped", _bi.Count == N + 1,
    "modelled=" + _bi.Count + " expected=" + (N + 1));
  TA("DISCOVERY_processed_every_block", _dProcessed == N + 1,
    "processed=" + _dProcessed + "/" + _dTotal);

  // ---- classification parity with the pre-optimisation algorithm
  int mismatches = 0, compared = 0; string firstBad = "";
  for (int i = 0; i < _bi.Count; i++) {
    var r = _bi[i];
    if (!(r.B is PlainBlock)) continue;           // only blocks that reach the hint path
    compared++;
    int legacy = LegacyHintCat(r.Sub, r.B.CustomName);
    if (legacy != r.Cat) {
      mismatches++;
      if (firstBad.Length == 0)
        firstBad = r.Sub + " new=" + CATN[r.Cat] + " legacy=" + CATN[legacy];
    }
  }
  TA("CLASSIFY_parity_on_modelled_blocks", mismatches == 0 && compared > 1000,
    "compared=" + compared + " mismatches=" + mismatches + " " + firstBad);

  // Every hint key must still resolve to its own category through the optimised path.
  int keyBad = 0; string keyMsg = "";
  for (int i = 0; i < _hintN; i++) {
    string probe = "xx" + _hintKey[i] + "zz";
    int legacy = LegacyHintCat(probe, "");
    var blk = new PlainBlock(900000 + i, "probe", "", probe);
    var rr = new BI { B = blk, F = blk, Id = 900000 + i, Sub = probe, Def = probe };
    Classify(rr);
    if (rr.Cat != legacy) {
      keyBad++;
      if (keyMsg.Length == 0) keyMsg = probe + " new=" + CATN[rr.Cat] + " legacy=" + CATN[legacy];
    }
  }
  TA("CLASSIFY_parity_over_every_hint_key", keyBad == 0,
    "checked=" + _hintN + " mismatches=" + keyBad + " " + keyMsg);

  // ---- rescan keeps the working model live
  var before = _bi;
  int beforeCount = _bi.Count;
  _ds = D_FETCH;
  int rescanTicks = 0, swappedEarly = 0, changedDuring = 0;
  while (_ds != D_IDLE && rescanTicks < 5000) {
    Runtime.ResetCounter();
    DiscoverStep();
    rescanTicks++;
    if (_ds != D_IDLE) {
      if (!object.ReferenceEquals(_bi, before)) swappedEarly++;
      if (_bi.Count != beforeCount) changedDuring++;
    }
  }
  TA("RESCAN_takes_multiple_ticks", rescanTicks > 1, "ticks=" + rescanTicks);
  TA("RESCAN_live_model_not_swapped_until_complete", swappedEarly == 0,
    "earlySwaps=" + swappedEarly);
  TA("RESCAN_live_model_unchanged_during_pass", changedDuring == 0,
    "changes=" + changedDuring + " (live count stayed " + beforeCount + ")");
  TA("RESCAN_publishes_a_new_model", !object.ReferenceEquals(_bi, before) && _bi.Count == N + 1,
    "swapped=" + (!object.ReferenceEquals(_bi, before)) + " count=" + _bi.Count);

  // ---- blocks vanishing mid-pass must not corrupt the published model
  _ds = D_FETCH;
  Runtime.ResetCounter();
  DiscoverStep();                                  // D_FETCH snapshots the block list
  gts.Blocks.RemoveRange(gts.Blocks.Count - 200, 200);
  int t2 = TRunDiscovery(5000);
  TA("DISCOVERY_survives_blocks_removed_mid_pass", _modelReady && _ds == D_IDLE && _bi.Count > 0,
    "ticks=" + t2 + " modelled=" + _bi.Count + " (snapshot taken before the removal)");

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

// Both doubles expose their subtype through this, so the harness can inject it without the
// script having to know anything about tests.
public interface ITestSub { string Sub { get; } }

// No interface the classifier tests for - this is the population that must reach the hint path.
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

    # The script derives a block's subtype from BlockDefinition, which a stub cannot populate.
    # Rewriting that one line for the harness keeps the test honest about everything else
    # rather than weakening the script to be testable.
    src = src.replace('r.Sub = d.SubtypeId == null ? "" : d.SubtypeId;',
                      'r.Sub = (b is ITestSub) ? ((ITestSub)b).Sub : "";', 1)

    tmp = tempfile.mkdtemp(prefix='stagedtest_')
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
    print('\nStaged discovery holds. The real instruction proof is the in-game RUNTIME COST '
          'section, not this.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
