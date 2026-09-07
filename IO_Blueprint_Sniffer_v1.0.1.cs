// IO Blueprint Sniffer v1.0.0 — standalone, READ-ONLY diagnostic utility.
// Purpose: observe live production queues and capture the real MyDefinitionId
// blueprint IDs Industrial Overhaul / Space Engineers actually use, so they
// can be copied manually into IO Production Manager's BlueprintOverrides.
//
// This script is completely separate from IO Production Manager. It does not
// modify, share code with, or read Custom Data from IOPM in any way.
//
// ABSOLUTE SAFETY INVARIANTS — do not weaken these:
//   * Only touches blocks that pass IsSameConstructAs(Me).
//   * Reads production queues via GetQueue() ONLY.
//   * Never calls AddQueueItem, ClearQueue, or any queue-mutating method.
//   * Never reorders or removes queue entries.
//   * Never sets/reads/changes .Enabled on any machine.
//   * Refineries are excluded entirely.
//   * Assemblers in disassembly mode are ignored.
//   * This script has no code path capable of writing production state.
//
// Storage format (this PB's Storage string, plain text, MyIni-compatible;
// this is the durable source of truth reloaded on recompile/restart):
//   [Seen.<n>]
//   Blueprint=<full MyDefinitionId>
//   Machine=<first CustomName observed with this blueprint>
//   MachineSubtype=<BlockDefinition SubtypeId>
//
//   [Captured.<Alias>]
//   Blueprint=<full MyDefinitionId>
//   Machine=<CustomName>
//   MachineSubtype=<SubtypeId>
//   Delta=<observed increase amount>
//
// On every successful capture, the same [Captured.<Alias>] data (all captures,
// not just the newest) is also written to this PB's own Custom Data, plus a
// [BlueprintOverrides] section of Alias=BlueprintId lines ready to paste into
// IO Production Manager. Custom Data is a player-facing export view only —
// Storage above remains authoritative.
//
// Commands (run argument):
//   capture <Alias>
//   capture <Alias> <machine-name-or-token>
//   cancel
//   status
//   clearseen        (clears [Seen.*] history only — never touches [Captured.*])
//
// No recipes, no stock management, no sorting, no dependency planning, no
// production control of any kind lives in this file. Keep it small.

const string VERSION = "1.0.1";

MyIni _ini = new MyIni();

enum SnifferState { PASSIVE, ARMED, AMBIGUOUS }
SnifferState _state = SnifferState.PASSIVE;

string _armedAlias = null;
string _armedMachineToken = null;

// Snapshot taken at arm-time: key = "MachineEntityId|BlueprintId" -> queued amount (as double).
Dictionary<string, double> _armSnapshot = new Dictionary<string, double>();
// Machine CustomName / SubtypeId by EntityId, captured at arm-time (for reporting).
Dictionary<long, string> _armMachineName = new Dictionary<long, string>();
Dictionary<long, string> _armMachineSubtype = new Dictionary<long, string>();

// Persisted "seen" blueprint records, keyed by BlueprintId string.
class SeenRecord
{
    public string Blueprint;
    public string Machine;
    public string MachineSubtype;
}
Dictionary<string, SeenRecord> _seen = new Dictionary<string, SeenRecord>(StringComparer.OrdinalIgnoreCase);

// Persisted captures, keyed by Alias.
class CaptureRecord
{
    public string Alias;
    public string Blueprint;
    public string Machine;
    public string MachineSubtype;
    public double Delta;
}
Dictionary<string, CaptureRecord> _captured = new Dictionary<string, CaptureRecord>(StringComparer.OrdinalIgnoreCase);

string _lastAmbiguousReport = null;

// Most recent successful capture, for on-screen persistence across Echo calls.
string _lastCapturedAlias = null;
string _lastCapturedBlueprint = null;

List<IMyProductionBlock> _machines = new List<IMyProductionBlock>();

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
    LoadState();
}

public void Save()
{
    Storage = BuildStorageString();
}

void LoadState()
{
    _seen.Clear();
    _captured.Clear();
    if (string.IsNullOrWhiteSpace(Storage)) return;

    MyIniParseResult result;
    if (!_ini.TryParse(Storage, out result)) return;

    List<string> sections = new List<string>();
    _ini.GetSections(sections);
    foreach (string section in sections)
    {
        if (section.StartsWith("Seen.", StringComparison.OrdinalIgnoreCase))
        {
            var rec = new SeenRecord
            {
                Blueprint = _ini.Get(section, "Blueprint").ToString(""),
                Machine = _ini.Get(section, "Machine").ToString(""),
                MachineSubtype = _ini.Get(section, "MachineSubtype").ToString("")
            };
            if (!string.IsNullOrEmpty(rec.Blueprint) && !_seen.ContainsKey(rec.Blueprint))
                _seen[rec.Blueprint] = rec;
        }
        else if (section.StartsWith("Captured.", StringComparison.OrdinalIgnoreCase))
        {
            string alias = section.Substring("Captured.".Length);
            var rec = new CaptureRecord
            {
                Alias = alias,
                Blueprint = _ini.Get(section, "Blueprint").ToString(""),
                Machine = _ini.Get(section, "Machine").ToString(""),
                MachineSubtype = _ini.Get(section, "MachineSubtype").ToString(""),
                Delta = _ini.Get(section, "Delta").ToDouble(0)
            };
            _captured[alias] = rec;
        }
    }
}

string BuildStorageString()
{
    MyIni outIni = new MyIni();
    int n = 1;
    foreach (var kv in _seen)
    {
        string section = "Seen." + n.ToString();
        outIni.Set(section, "Blueprint", kv.Value.Blueprint);
        outIni.Set(section, "Machine", kv.Value.Machine);
        outIni.Set(section, "MachineSubtype", kv.Value.MachineSubtype);
        n++;
    }
    foreach (var kv in _captured)
    {
        string section = "Captured." + kv.Key;
        outIni.Set(section, "Blueprint", kv.Value.Blueprint);
        outIni.Set(section, "Machine", kv.Value.Machine);
        outIni.Set(section, "MachineSubtype", kv.Value.MachineSubtype);
        outIni.Set(section, "Delta", kv.Value.Delta);
    }
    return outIni.ToString();
}

// Writes the user-visible/export representation of all captures to Me.CustomData.
// Storage remains the source of truth on reload; this is purely for the player
// to inspect/copy, and is rebuilt in full from _captured each time so existing
// captures are never erased when another one is added.
void WriteCapturesToCustomData()
{
    MyIni cdIni = new MyIni();
    foreach (var kv in _captured)
    {
        string section = "Captured." + kv.Key;
        cdIni.Set(section, "Blueprint", kv.Value.Blueprint);
        cdIni.Set(section, "Machine", kv.Value.Machine);
        cdIni.Set(section, "MachineSubtype", kv.Value.MachineSubtype);
        cdIni.Set(section, "Delta", kv.Value.Delta);
    }
    foreach (var kv in _captured)
    {
        cdIni.Set("BlueprintOverrides", kv.Key, kv.Value.Blueprint);
    }
    Me.CustomData = cdIni.ToString();
}

bool IsQualifyingMachine(IMyProductionBlock block)
{
    if (block == null) return false;
    if (!block.IsSameConstructAs(Me)) return false;
    if (block is IMyRefinery) return false;

    var assembler = block as IMyAssembler;
    if (assembler != null && assembler.Mode == MyAssemblerMode.Disassembly)
        return false;

    return true;
}

void DiscoverMachines()
{
    _machines.Clear();
    List<IMyProductionBlock> all = new List<IMyProductionBlock>();
    GridTerminalSystem.GetBlocksOfType(all, IsQualifyingMachine);
    _machines.AddRange(all);
}

// Snapshot of current queues: key "EntityId|BlueprintId" -> amount.
Dictionary<string, double> SnapshotQueues(Dictionary<long, string> nameByEntity, Dictionary<long, string> subtypeByEntity)
{
    var snap = new Dictionary<string, double>();
    List<MyProductionItem> queue = new List<MyProductionItem>();
    foreach (var machine in _machines)
    {
        queue.Clear();
        machine.GetQueue(queue);
        long entityId = machine.EntityId;
        if (nameByEntity != null) nameByEntity[entityId] = machine.CustomName;
        if (subtypeByEntity != null) subtypeByEntity[entityId] = machine.BlockDefinition.SubtypeId;

        foreach (var item in queue)
        {
            string bp = item.BlueprintId.ToString();
            string key = entityId.ToString() + "|" + bp;
            double amt;
            if (!snap.TryGetValue(key, out amt)) amt = 0;
            snap[key] = amt + (double)item.Amount;
        }
    }
    return snap;
}

void RecordSeen(string blueprint, string machineName, string subtype)
{
    if (_seen.ContainsKey(blueprint)) return;
    _seen[blueprint] = new SeenRecord { Blueprint = blueprint, Machine = machineName, MachineSubtype = subtype };
}

bool MachineMatchesToken(IMyProductionBlock m, string token)
{
    if (string.IsNullOrEmpty(token)) return true;
    return m.CustomName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
}

void HandleCommand(string arg)
{
    if (string.IsNullOrWhiteSpace(arg)) return;
    string trimmed = arg.Trim();
    string[] parts = trimmed.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;
    string cmd = parts[0].ToLowerInvariant();

    if (cmd == "status")
    {
        // Status is rendered every tick via Echo; nothing to do here.
        return;
    }
    else if (cmd == "cancel")
    {
        _state = SnifferState.PASSIVE;
        _armedAlias = null;
        _armedMachineToken = null;
        _armSnapshot.Clear();
        _armMachineName.Clear();
        _armMachineSubtype.Clear();
        _lastAmbiguousReport = null;
    }
    else if (cmd == "clearseen")
    {
        _seen.Clear();
        Save();
    }
    else if (cmd == "capture")
    {
        if (parts.Length < 2)
        {
            Echo("capture requires an Alias, e.g.: capture AluminumPlate");
            return;
        }
        string alias = parts[1];
        string token = parts.Length >= 3 ? string.Join(" ", parts, 2, parts.Length - 2) : null;

        DiscoverMachines();

        if (!string.IsNullOrEmpty(token))
        {
            int matchCount = 0;
            foreach (var m in _machines)
                if (MachineMatchesToken(m, token)) matchCount++;

            if (matchCount == 0)
            {
                Echo("No machine matches token: " + token);
                return;
            }
            if (matchCount > 1)
            {
                Echo("AMBIGUOUS machine token '" + token + "' matches " + matchCount + " machines. Use a more specific token.");
                return;
            }
        }

        _armedAlias = alias;
        _armedMachineToken = token;
        _armMachineName.Clear();
        _armMachineSubtype.Clear();
        _armSnapshot = SnapshotQueues(_armMachineName, _armMachineSubtype);
        _state = SnifferState.ARMED;
        _lastAmbiguousReport = null;
    }
    else
    {
        Echo("Unknown command: " + trimmed);
    }
}

void PollArmed()
{
    DiscoverMachines();

    var nameByEntity = new Dictionary<long, string>();
    var subtypeByEntity = new Dictionary<long, string>();
    var current = SnapshotQueues(nameByEntity, subtypeByEntity);

    // Passive "seen" bookkeeping happens regardless of arm state.
    RecordAllSeenFromSnapshot(current, nameByEntity, subtypeByEntity);

    if (_state != SnifferState.ARMED) return;

    // Find deltas: only additions/increases count. Restrict to armed machine token if set.
    List<string> deltaKeys = new List<string>();
    foreach (var kv in current)
    {
        string key = kv.Key;
        double newAmt = kv.Value;
        double oldAmt;
        if (!_armSnapshot.TryGetValue(key, out oldAmt)) oldAmt = 0;

        if (newAmt > oldAmt)
        {
            long entityId = ParseEntityIdFromKey(key);
            if (_armedMachineToken != null)
            {
                string mName;
                if (!nameByEntity.TryGetValue(entityId, out mName)) mName = "";
                if (mName.IndexOf(_armedMachineToken, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }
            deltaKeys.Add(key);
        }
    }

    if (deltaKeys.Count == 0) return;

    if (deltaKeys.Count > 1)
    {
        _state = SnifferState.AMBIGUOUS;
        var sb = new StringBuilder();
        sb.Append("CAPTURE AMBIGUOUS — multiple blueprint deltas observed:\n");
        foreach (var key in deltaKeys)
        {
            long entityId = ParseEntityIdFromKey(key);
            string bp = ParseBlueprintFromKey(key);
            string mName;
            nameByEntity.TryGetValue(entityId, out mName);
            sb.Append(" - ").Append(bp).Append(" @ ").Append(mName).Append("\n");
        }
        sb.Append("Retry the capture.");
        _lastAmbiguousReport = sb.ToString();
        return;
    }

    // Exactly one delta — commit the capture.
    string onlyKey = deltaKeys[0];
    long onlyEntityId = ParseEntityIdFromKey(onlyKey);
    string blueprint = ParseBlueprintFromKey(onlyKey);
    double oldOnly;
    if (!_armSnapshot.TryGetValue(onlyKey, out oldOnly)) oldOnly = 0;
    double delta = current[onlyKey] - oldOnly;

    string machineName;
    nameByEntity.TryGetValue(onlyEntityId, out machineName);
    string machineSubtype;
    subtypeByEntity.TryGetValue(onlyEntityId, out machineSubtype);

    _captured[_armedAlias] = new CaptureRecord
    {
        Alias = _armedAlias,
        Blueprint = blueprint,
        Machine = machineName ?? "",
        MachineSubtype = machineSubtype ?? "",
        Delta = delta
    };
    RecordSeen(blueprint, machineName ?? "", machineSubtype ?? "");
    Save();
    WriteCapturesToCustomData();

    _lastCapturedAlias = _armedAlias;
    _lastCapturedBlueprint = blueprint;

    _state = SnifferState.PASSIVE;
    _armedAlias = null;
    _armedMachineToken = null;
    _armSnapshot.Clear();
    _armMachineName.Clear();
    _armMachineSubtype.Clear();
}

void RecordAllSeenFromSnapshot(Dictionary<string, double> snapshot, Dictionary<long, string> nameByEntity, Dictionary<long, string> subtypeByEntity)
{
    bool changed = false;
    foreach (var kv in snapshot)
    {
        string bp = ParseBlueprintFromKey(kv.Key);
        if (_seen.ContainsKey(bp)) continue;
        long entityId = ParseEntityIdFromKey(kv.Key);
        string mName;
        nameByEntity.TryGetValue(entityId, out mName);
        string mSub;
        subtypeByEntity.TryGetValue(entityId, out mSub);
        RecordSeen(bp, mName ?? "", mSub ?? "");
        changed = true;
    }
    if (changed) Save();
}

long ParseEntityIdFromKey(string key)
{
    int idx = key.IndexOf('|');
    return long.Parse(key.Substring(0, idx));
}

string ParseBlueprintFromKey(string key)
{
    int idx = key.IndexOf('|');
    return key.Substring(idx + 1);
}

void RenderEcho()
{
    var sb = new StringBuilder();
    sb.Append("IO BLUEPRINT SNIFFER v").Append(VERSION).Append("\n");
    sb.Append("State: ").Append(_state.ToString()).Append("\n");
    if (_state == SnifferState.ARMED || _state == SnifferState.AMBIGUOUS)
    {
        sb.Append("Capture: ").Append(_armedAlias ?? "").Append("\n");
        sb.Append("Machine: ").Append(_armedMachineToken ?? "(any)").Append("\n");
    }
    sb.Append("Seen: ").Append(_seen.Count).Append("\n");
    sb.Append("Captured: ").Append(_captured.Count).Append("\n");

    if (_lastCapturedAlias != null)
    {
        sb.Append("\nLAST CAPTURE\n");
        sb.Append(_lastCapturedAlias).Append("=").Append(_lastCapturedBlueprint).Append("\n");
    }

    if (_state == SnifferState.AMBIGUOUS && _lastAmbiguousReport != null)
    {
        sb.Append("\n").Append(_lastAmbiguousReport).Append("\n");
    }

    Echo(sb.ToString());
}

public void Main(string argument, UpdateType updateSource)
{
    if (!string.IsNullOrEmpty(argument))
    {
        HandleCommand(argument);
    }

    DiscoverMachines();
    PollArmed();
    RenderEcho();
}
