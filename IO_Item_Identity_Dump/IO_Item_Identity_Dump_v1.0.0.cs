// IO Item Identity Dump v1.0.0 — READ-ONLY diagnostic.
// Lists every distinct physical item (TypeId/SubtypeId) with its total amount
// across every inventory on this PB's construct. Purpose: get ground truth for
// the actual SubtypeId an item carries in inventory, which is NOT necessarily
// the same string as the blueprint definition id that produced it.
//
// *** RUN THIS ON A SCRATCH PROGRAMMABLE BLOCK — NOT the IOPM PB. ***
// It writes its report into its OWN Custom Data so the text can be copied out.
// A guard below refuses to run if this PB's Custom Data looks like IOPM config,
// so an accidental run on the IOPM PB cannot destroy that configuration.
//
// Never moves, edits, or removes any item. No queue interaction whatsoever.
// One-shot: press Run. Optional argument = case-insensitive substring filter,
// e.g. "Component" or "Glass" or "MyObjectBuilder_AmmoMagazine".

Dictionary<string, double> _totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();
List<MyInventoryItem> _buf = new List<MyInventoryItem>();

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.None; // manual Run only
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

    string filter = (argument ?? "").Trim();
    _totals.Clear();
    _blocks.Clear();
    GridTerminalSystem.GetBlocks(_blocks);

    int blockCount = 0, invCount = 0;
    for (int b = 0; b < _blocks.Count; b++)
    {
        IMyTerminalBlock blk = _blocks[b];
        if (blk == null || !blk.IsSameConstructAs(Me) || !blk.HasInventory) continue;
        blockCount++;
        for (int i = 0; i < blk.InventoryCount; i++)
        {
            IMyInventory inv = blk.GetInventory(i);
            if (inv == null) continue;
            invCount++;
            _buf.Clear();
            inv.GetItems(_buf);
            for (int k = 0; k < _buf.Count; k++)
            {
                string key = _buf[k].Type.TypeId + "/" + _buf[k].Type.SubtypeId;
                if (filter.Length > 0 && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                double prev;
                _totals[key] = (_totals.TryGetValue(key, out prev) ? prev : 0) + (double)_buf[k].Amount;
            }
        }
    }

    List<string> keys = new List<string>(_totals.Keys);
    keys.Sort(StringComparer.OrdinalIgnoreCase);

    StringBuilder sb = new StringBuilder();
    sb.Append("Item Identity Dump v1.0.0\nBlocks=").Append(blockCount)
      .Append(" Inventories=").Append(invCount)
      .Append(" DistinctItems=").Append(keys.Count);
    if (filter.Length > 0) sb.Append(" Filter=").Append(filter);
    sb.Append('\n');
    for (int i = 0; i < keys.Count; i++)
        sb.Append(keys[i]).Append('=').Append(Math.Round(_totals[keys[i]], 2)).Append('\n');

    string report = sb.ToString();
    Me.CustomData = report; // copy the full report out of Custom Data
    Echo(report.Length <= 8000 ? report
        : report.Substring(0, 8000) + "\n...(truncated in Echo; full report is in Custom Data)");
}
