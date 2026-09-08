// IO Machine Dump v1.0.0 — READ-ONLY diagnostic.
// Lists every production block the grid terminal system can see - INCLUDING ones
// on other constructs - and, for each, every condition under which IOPM would
// exclude it from production management.
//
// Written to answer a specific live question: two identical Wire Drawers, only
// one ever receiving queued jobs, with no tags on either, both in assembly
// mode, and no warning from IOPM. Every remaining exclusion path in IOPM is
// silent — it produces no warning and no diagnostic entry — so the only way to
// tell them apart is to evaluate each one explicitly and print the result.
//
// *** RUN THIS ON A SCRATCH PROGRAMMABLE BLOCK — NOT the IOPM PB. ***
// It writes its report into its OWN Custom Data so the text can be copied out.
// A guard below refuses to run if this PB's Custom Data looks like IOPM config,
// so an accidental run on the IOPM PB cannot destroy that configuration.
//
// STRICTLY READ-ONLY. Never queues, never removes a queue entry, never moves an
// item, never toggles a block. It calls only property getters, GetQueue() and
// CanUseBlueprint(). There is deliberately no code path in this file that
// mutates anything at all.
//
// One-shot: press Run. Optional argument = a blueprint id to test with
// CanUseBlueprint on every machine, e.g.
//     MyObjectBuilder_BlueprintDefinition/GoldWire
// Without an argument the CanUse column reads "n/a" and everything else still
// reports. Pass the blueprint of the item that is failing to spread.

// Mirrors IOPM's IGNORE_TAGS exactly. Any of these on a production block's name
// makes it fully invisible to IOPM - including, non-obviously, [No Sorting].
static readonly string[] IGNORE_TAGS = new string[]
  { "[IOPM-Ignore]", "[Ignore]", "[Locked]", "!GSIM-Locked", "[No Sorting]", "!GSIM-NoSorting" };

List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();
List<MyProductionItem> _queue = new List<MyProductionItem>();

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.None; // manual Run only
}

bool CIC(string haystack, string needle)
{
    if (haystack == null || needle == null) return false;
    return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

string TagsOn(string name)
{
    string found = "";
    for (int i = 0; i < IGNORE_TAGS.Length; i++)
        if (CIC(name, IGNORE_TAGS[i])) found += (found.Length > 0 ? "+" : "") + IGNORE_TAGS[i];
    return found;
}

// Mirrors IOPM's IsManualBench.
bool IsManualBench(string machineText)
{
    return machineText.Contains("basicassemblingbench") || machineText.Contains("basic assembling bench")
        || machineText.Contains("assemblingbench") || machineText.Contains("assembling bench");
}

public void Main(string argument, UpdateType updateSource)
{
    // Refuse to clobber IOPM's control panel if run on the wrong PB.
    if (Me.CustomData.IndexOf("[IOPM", StringComparison.OrdinalIgnoreCase) >= 0 ||
        Me.CustomData.IndexOf("[Stock]", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        Echo("ABORTED: this PB's Custom Data looks like IOPM configuration.\n" +
             "Run this script on a separate scratch programmable block instead.");
        return;
    }

    string bpText = (argument ?? "").Trim();
    MyDefinitionId testBp = default(MyDefinitionId);
    bool haveBp = false;
    string bpNote = "no blueprint argument given";
    if (bpText.Length > 0)
    {
        haveBp = MyDefinitionId.TryParse(bpText, out testBp);
        bpNote = haveBp ? ("testing CanUseBlueprint(" + bpText + ")")
                        : ("ARGUMENT DID NOT PARSE AS A BLUEPRINT ID: " + bpText);
    }

    _blocks.Clear();
    GridTerminalSystem.GetBlocks(_blocks);

    StringBuilder sb = new StringBuilder();
    sb.Append("Machine Dump v1.0.0\n").Append(bpNote).Append('\n');

    int total = 0, candidates = 0;
    for (int b = 0; b < _blocks.Count; b++)
    {
        IMyProductionBlock pb = _blocks[b] as IMyProductionBlock;
        if (pb == null) continue;
        total++;
        // DELIBERATELY NOT FILTERED BY CONSTRUCT. IOPM manages production only on its own
        // construct, and IsSameConstructAs covers mechanical joins (rotor/piston/hinge) but
        // NOT connector docks. A base deliberately split into subgrids by connectors therefore
        // has production blocks IOPM cannot see at all - no tag, no warning, no diagnostic
        // entry. Filtering here would hide exactly the block worth finding.
        bool sameConstruct = false;
        try { sameConstruct = pb.IsSameConstructAs(Me); } catch { }

        string name = pb.CustomName ?? "";
        string subtype = pb.BlockDefinition.SubtypeId.ToString();
        // IOPM builds this same string and matches machine names against it.
        string machineText = (name + " " + subtype).ToLowerInvariant();

        // ---- read every property IOPM gates on, each guarded separately so one
        // ---- throwing getter cannot hide the rest.
        bool working = false, functional = false, enabled = false, producing = false;
        try { working = pb.IsWorking; } catch { }
        try { functional = pb.IsFunctional; } catch { }
        try { enabled = pb.Enabled; } catch { }
        try { producing = pb.IsProducing; } catch { }

        bool isRefinery = pb is IMyRefinery;
        IMyAssembler asm = pb as IMyAssembler;
        string mode = "n/a (not an assembler)";
        bool disassembly = false;
        if (asm != null)
        {
            try { disassembly = asm.Mode == MyAssemblerMode.Disassembly; } catch { }
            mode = disassembly ? "DISASSEMBLY" : "Assembly";
        }

        // QueueLoad is where IOPM sorts equal-ranked machines. A throwing
        // GetQueue makes IOPM score the machine 999999, which sorts it
        // permanently last and starves it silently - this column exists
        // specifically to catch that.
        int queueCount = -1;
        double queueLoad = -1;
        bool queueThrew = false;
        string queueDetail = "";
        _queue.Clear();
        try
        {
            pb.GetQueue(_queue);
            queueCount = _queue.Count;
            queueLoad = 0;
            for (int q = 0; q < _queue.Count; q++)
            {
                queueLoad += (double)_queue[q].Amount;
                if (q < 6)
                    queueDetail += (queueDetail.Length > 0 ? ", " : "")
                        + _queue[q].BlueprintId.SubtypeName + " x" + Math.Round((double)_queue[q].Amount, 2);
            }
            if (_queue.Count > 6) queueDetail += ", ...(" + (_queue.Count - 6) + " more)";
        }
        catch (Exception ex) { queueThrew = true; queueDetail = "GetQueue THREW: " + ex.Message; }

        string canUse = "n/a";
        if (haveBp)
        {
            try { canUse = pb.CanUseBlueprint(testBp) ? "yes" : "NO"; }
            catch (Exception ex) { canUse = "THREW: " + ex.Message; }
        }

        string tags = TagsOn(name);
        bool bench = IsManualBench(machineText);
        bool survival = machineText.Contains("survivalkit") || machineText.Contains("survival kit");

        // ---- the verdict: why IOPM would or would not consider this machine.
        string verdict;
        if (!sameConstruct)
            verdict = "INVISIBLE TO IOPM: different construct - connector-docked, not mechanically joined";
        else if (isRefinery) verdict = "EXCLUDED: refinery (outputs evacuated only, by design)";
        else if (tags.Length > 0) verdict = "EXCLUDED: ignore tag " + tags;
        else if (disassembly) verdict = "EXCLUDED: assembler in disassembly mode";
        else if (!working) verdict = "EXCLUDED: IsWorking false (power / enabled / damage)";
        else if (bench) verdict = "EXCLUDED: manual-only assembling bench";
        else if (survival) verdict = "EXCLUDED unless AllowSurvivalKitFallback=true: survival kit";
        else if (haveBp && canUse != "yes") verdict = "EXCLUDED for this blueprint: CanUseBlueprint " + canUse;
        else if (queueThrew) verdict = "STARVED: GetQueue throws, so IOPM scores load 999999 and sorts it last";
        else { verdict = "CANDIDATE"; candidates++; }

        sb.Append("\n[").Append(name).Append("]\n")
          .Append("Subtype=").Append(subtype).Append('\n')
          .Append("Grid=").Append(pb.CubeGrid.CustomName)
          .Append(" SameConstructAsPB=").Append(sameConstruct).Append('\n')
          .Append("Verdict=").Append(verdict).Append('\n')
          .Append("Working=").Append(working)
          .Append(" Functional=").Append(functional)
          .Append(" Enabled=").Append(enabled)
          .Append(" Producing=").Append(producing).Append('\n')
          .Append("AssemblerMode=").Append(mode).Append('\n')
          .Append("IgnoreTags=").Append(tags.Length > 0 ? tags : "none").Append('\n')
          .Append("CanUseBlueprint=").Append(canUse).Append('\n')
          .Append("QueueCount=").Append(queueCount)
          .Append(" QueueLoad=").Append(queueThrew ? "THREW" : Math.Round(queueLoad, 2).ToString()).Append('\n')
          .Append("Queue=").Append(queueDetail.Length > 0 ? queueDetail : "(empty)").Append('\n');
    }

    sb.Append("\nProductionBlocks=").Append(total)
      .Append(" Candidates=").Append(candidates).Append('\n')
      .Append("NOTE: run this on a scratch PB on the SAME construct as the IOPM PB, or\n")
      .Append("SameConstructAsPB is measured against the wrong reference block.\n");
    if (haveBp)
        sb.Append("Among candidates IOPM queues to ALL of them, splitting jobs evenly and\n")
          .Append("giving the remainder to the LOWEST QueueLoad first. An idle machine that\n")
          .Append("reads CANDIDATE with QueueLoad=0 should therefore be first in line.\n");

    string report = sb.ToString();
    Me.CustomData = report; // copy the full report out of Custom Data
    Echo(report.Length <= 8000 ? report
        : report.Substring(0, 8000) + "\n...(truncated in Echo; full report is in Custom Data)");
}
