// IO Production Manager v1.0.11
// Space Engineers Programmable Block script
// Designed for Industrial Overhaul v1.7.7-style production chains.
//
// Core safety rule:
//   READ existing queues. APPEND shortages. NEVER clear/remove/reorder queues.
//
// This script does NOT manage refineries, ores, oil crackers, or chemical refineries.
// It only manages manufactured-item production and reports terminal/raw shortages.

MyIni _ini = new MyIni();
MyCommandLine _cmd = new MyCommandLine();

const string VERSION = "1.0.11";

string _componentTag = "Components";
string _ingotTag = "Ingots";
bool _autoProduction = false;
bool _allowSurvivalKitFallback = false;
bool _requireConveyorAccess = true;
string[] _manualTags = new string[] { "[IOPM-Manual]", "!IOPM-Manual" };

Dictionary<string, ItemDef> _items = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _builtinItemAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _learnedItemTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, Recipe> _recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _builtinBlueprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _trustedBuiltinBlueprintAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _learnedBlueprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _learnedYields = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _legacyStorageBlueprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _overrideBlueprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _stockTargets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
List<string> _configErrors = new List<string>();

List<IMyProductionBlock> _productionBlocks = new List<IMyProductionBlock>();
List<IMyInventory> _stockInventories = new List<IMyInventory>();
List<IMyInventory> _outputInventories = new List<IMyInventory>();
List<IMyInventory> _inputInventories = new List<IMyInventory>();

Dictionary<string, double> _onHand = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _stagedInputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _queuedOutput = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
// Existing queue jobs are tracked separately from expected output so their
// ingredient requirements can be supported without touching the queue itself.
Dictionary<string, double> _queuedJobs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _queueSupportDemand = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _demand = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _plannedOutput = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, int> _plannedJobs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _rawShortages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _unknownBlueprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _noMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _cycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _planOrderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
List<string> _planOrder = new List<string>();
List<string> _cycleLog = new List<string>();

// Bulk learning session state (v1.0.11).
// Capture: eligible machines are temporarily disabled while the player queues
// training items. Observe: exactly ONE production machine / blueprint is allowed
// to advance at a time. The active machine remains ON continuously while its
// target job runs; it is frozen immediately after queue progress is detected.
// Existing queues are never edited.
int _learnPhase = 0; // 0=off, 1=capture, 2=observe
DateTime _learnCaptureDeadline;
int _learnCaptureSeconds = 60;
Dictionary<long, BulkLearnMachine> _bulkLearnMachines = new Dictionary<long, BulkLearnMachine>();
List<string> _bulkLearnLog = new List<string>();
List<string> _bulkLearnMissed = new List<string>();
List<string> _bulkLearnKnownSkipped = new List<string>();
const string LEARN_LOCK_TAG = " [Locked]";
const int LEARN_RETRY_SECONDS = 15;
const int LEARN_MAX_BLOCKED_RETRIES = 2;

// Serialized observation state.
long _learnActiveMachineId = 0;
string _learnActiveBlueprint = "";
int _learnActiveMode = 0; // 0=none, 1=draining work ahead, 2=learning target, 3=waiting for output delta
double _learnActiveStartJobs = 0;
double _learnActiveCompletedJobs = 0;
Dictionary<string, double> _learnActiveOutputBaseline = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
DateTime _learnActiveSince;
DateTime _learnLastProgress;
DateTime _learnOutputWaitSince;
double _learnActiveLastQueueTotal = -1;
const int LEARN_BLOCKED_SECONDS = 30;
const int LEARN_OUTPUT_WAIT_SECONDS = 10;

public Program()
{
    BuildKnowledgeBase();
    CaptureBuiltinItemAliases();

    // v1.0.2 and earlier stored learned blueprint mappings in PB Storage.
    // v1.0.3 migrates them once into human-readable Custom Data.
    LoadLegacyStorage();

    if (string.IsNullOrWhiteSpace(Me.CustomData))
        WriteDefaultCustomData();

    LoadConfig();
    MigrateLegacyStorageToCustomData();
    LoadConfig();

    UpdateRuntimeFrequency();
}

public void Save()
{
    // Learned blueprint mappings are persisted in Custom Data, not hidden Storage.
}

public void Main(string argument, UpdateType updateSource)
{
    if ((updateSource & (UpdateType.Trigger | UpdateType.Terminal | UpdateType.Script)) != 0 && !string.IsNullOrWhiteSpace(argument))
    {
        HandleCommand(argument);
        return;
    }

    if (_learnPhase != 0 && (updateSource & (UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0)
    {
        TickBulkLearning();
        return;
    }

    if ((updateSource & UpdateType.Update100) != 0 && _autoProduction)
    {
        RunCycle(true, true);
    }
}

void HandleCommand(string argument)
{
    if (!_cmd.TryParse(argument))
    {
        Echo("Could not parse command.");
        return;
    }

    string verb = (_cmd.Argument(0) ?? "").ToLowerInvariant();

    if (verb == "status" || verb == "stock")
    {
        RunCycle(false, true);
        return;
    }

    if (verb == "run")
    {
        RunCycle(true, true);
        return;
    }

    if (verb == "plan")
    {
        RunCycle(false, false);
        ShowPlan();
        return;
    }

    if (verb == "machines")
    {
        DiscoverGrid();
        ShowMachines();
        return;
    }

    if (verb == "diagnose")
    {
        RunCycle(false, false);
        ShowDiagnostics();
        return;
    }

    if (verb == "production")
    {
        string arg = (_cmd.Argument(1) ?? "").ToLowerInvariant();
        if (arg == "on") SetAutoProduction(true);
        else if (arg == "off") SetAutoProduction(false);
        else Echo("Usage: production on | production off");
        return;
    }

    if (verb == "learn")
    {
        string arg1 = (_cmd.Argument(1) ?? "").ToLowerInvariant();
        if (arg1 == "start")
        {
            int seconds = 60;
            string secText = _cmd.Argument(2);
            int parsed;
            if (!string.IsNullOrWhiteSpace(secText) && int.TryParse(secText, out parsed)) seconds = Math.Max(10, Math.Min(parsed, 600));
            StartBulkLearning(seconds);
            return;
        }
        if (arg1 == "finish") { FinishLearningCapture(); return; }
        if (arg1 == "status") { ShowBulkLearningStatus(); return; }
        if (arg1 == "cancel") { CancelBulkLearning(); return; }

        string name = _cmd.Argument(1);
        if (string.IsNullOrWhiteSpace(name))
        {
            Echo("Usage:\nlearn <ItemName>\nlearn start [seconds]\nlearn finish\nlearn status\nlearn cancel");
            return;
        }
        LearnBlueprint(name);
        return;
    }

    if (verb == "learned")
    {
        ShowLearned();
        return;
    }

    if (verb == "export")
    {
        ExportLearned();
        return;
    }

    if (verb == "forget")
    {
        string name = _cmd.Argument(1);
        if (string.IsNullOrWhiteSpace(name))
        {
            Echo("Usage: forget <ItemName>");
            return;
        }
        ForgetLearned(name);
        return;
    }

    if (verb == "repair")
    {
        RepairTrustedLearnedMappings();
        return;
    }

    if (verb == "reload")
    {
        LoadConfig();
        UpdateRuntimeFrequency();
        Echo("Configuration reloaded.");
        return;
    }

    if (verb == "help")
    {
        ShowHelp();
        return;
    }

    Echo("Unknown command: " + verb + "\nRun: help");
}

void RunCycle(bool applyQueues, bool showStatus)
{
    _cycleLog.Clear();
    _configErrors.Clear();

    LoadConfig();
    DiscoverGrid();
    ScanInventory();
    ScanQueues();
    BuildPlan();

    if (applyQueues)
        ApplyPlan();

    if (showStatus)
        ShowStatus();
}

void BuildPlan()
{
    _demand.Clear();
    _queueSupportDemand.Clear();
    _plannedOutput.Clear();
    _plannedJobs.Clear();
    _rawShortages.Clear();
    _unknownBlueprints.Clear();
    _noMachines.Clear();
    _cycles.Clear();
    _planOrderSet.Clear();
    _planOrder.Clear();

    // Permanent stock targets are one source of demand.
    foreach (var kv in _stockTargets)
        AddTo(_demand, kv.Key, kv.Value);

    // v1.0.5: every existing assembly queue is sacred, but known queued jobs
    // also create demand for their ingredients. This lets a manually queued
    // Motor job automatically request Electromagnets/Wire/Tubes without ever
    // clearing, reducing, reordering, or replacing the Motor queue itself.
    List<string> queueRoots = new List<string>();
    foreach (var kv in _queuedJobs)
    {
        Recipe queuedRecipe;
        if (!_recipes.TryGetValue(kv.Key, out queuedRecipe)) continue;
        if (kv.Value <= 0.0001) continue;

        for (int i = 0; i < queuedRecipe.Inputs.Count; i++)
        {
            Ingredient ing = queuedRecipe.Inputs[i];
            string dep = Canon(ing.Item);
            double amount = ing.Amount * kv.Value;
            AddTo(_demand, dep, amount);
            AddTo(_queueSupportDemand, dep, amount);
            if (_recipes.ContainsKey(dep) && !queueRoots.Contains(dep))
                queueRoots.Add(dep);
        }
    }

    // First resolve dependencies required by work that is ALREADY queued.
    // This does not alter those existing queue entries; it only fills gaps
    // underneath them.
    for (int i = 0; i < queueRoots.Count; i++)
    {
        HashSet<string> stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureItem(queueRoots[i], stack, 0, true);
    }

    // Then resolve configured stock targets. Existing queued output still
    // counts toward the target, so only the positive remainder is appended.
    foreach (var kv in _stockTargets)
    {
        HashSet<string> stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureItem(kv.Key, stack, 0, false);
    }

    // Anything with demand but no production recipe is a terminal/raw requirement.
    foreach (var kv in _demand)
    {
        if (_recipes.ContainsKey(kv.Key)) continue;
        double supportDemand = Get(_queueSupportDemand, kv.Key);
        double stagedCredit = Math.Min(Get(_stagedInputs, kv.Key), supportDemand);
        double effectiveDemand = Math.Max(0, kv.Value - stagedCredit);
        double shortBy = effectiveDemand - Get(_onHand, kv.Key);
        if (shortBy > 0.0001)
            _rawShortages[kv.Key] = shortBy;
    }
}

bool EnsureItem(string alias, HashSet<string> stack, int depth, bool queueSupportBranch)
{
    alias = Canon(alias);
    if (depth > 20)
    {
        _cycles.Add(alias);
        return false;
    }

    if (stack.Contains(alias))
    {
        _cycles.Add(alias);
        return false;
    }

    double totalDemand = Get(_demand, alias);
    // Staged machine inputs only credit dependency demand created by existing
    // queues. They do NOT count toward permanent [Stock] reserves.
    double supportDemand = Get(_queueSupportDemand, alias);
    double stagedCredit = Math.Min(Get(_stagedInputs, alias), supportDemand);
    double effectiveDemand = Math.Max(0, totalDemand - stagedCredit);
    double available = Get(_onHand, alias) + Get(_queuedOutput, alias) + Get(_plannedOutput, alias);
    double shortage = effectiveDemand - available;
    if (shortage <= 0.0001)
        return true;

    Recipe recipe;
    if (!_recipes.TryGetValue(alias, out recipe))
    {
        // Terminal/raw/refined material. We never operate refineries for it.
        return true;
    }

    MyDefinitionId blueprint;
    if (!TryGetBlueprint(alias, out blueprint))
    {
        _unknownBlueprints.Add(alias);
        return false;
    }

    List<IMyProductionBlock> machines = GetBestMachines(alias, blueprint, recipe);
    if (machines.Count == 0)
    {
        _noMachines.Add(alias);
        return false;
    }

    double recipeOutput = GetRecipeOutput(alias, recipe);
    int jobs = (int)Math.Ceiling(shortage / recipeOutput);
    if (jobs <= 0) return true;

    stack.Add(alias);
    bool dependenciesReady = true;

    for (int i = 0; i < recipe.Inputs.Count; i++)
    {
        Ingredient ing = recipe.Inputs[i];
        string dep = Canon(ing.Item);
        double depDemand = ing.Amount * jobs;
        AddTo(_demand, dep, depDemand);
        if (queueSupportBranch) AddTo(_queueSupportDemand, dep, depDemand);

        if (_recipes.ContainsKey(dep))
        {
            if (!EnsureItem(dep, stack, depth + 1, queueSupportBranch))
                dependenciesReady = false;
        }
    }

    stack.Remove(alias);

    // Do not append a parent job if a required manufactured dependency cannot
    // itself be planned. Raw/refined shortages do NOT block queue creation;
    // they are reported for the player to handle.
    if (!dependenciesReady)
        return false;

    AddTo(_plannedOutput, alias, jobs * recipeOutput);
    AddTo(_plannedJobs, alias, jobs);

    if (!_planOrderSet.Contains(alias))
    {
        _planOrderSet.Add(alias);
        _planOrder.Add(alias);
    }

    return true;
}

void ApplyPlan()
{
    for (int p = 0; p < _planOrder.Count; p++)
    {
        string alias = _planOrder[p];
        int jobs = GetInt(_plannedJobs, alias);
        if (jobs <= 0) continue;

        Recipe recipe;
        if (!_recipes.TryGetValue(alias, out recipe)) continue;

        MyDefinitionId bp;
        if (!TryGetBlueprint(alias, out bp)) continue;

        List<IMyProductionBlock> machines = GetBestMachines(alias, bp, recipe);
        if (machines.Count == 0)
        {
            _noMachines.Add(alias);
            continue;
        }

        // Spread appended work across equally preferred compatible machines.
        int baseJobs = jobs / machines.Count;
        int remainder = jobs % machines.Count;

        for (int i = 0; i < machines.Count; i++)
        {
            int add = baseJobs + (i < remainder ? 1 : 0);
            if (add <= 0) continue;

            try
            {
                machines[i].AddQueueItem(bp, (MyFixedPoint)add);
                _cycleLog.Add("+" + add + " " + Friendly(alias) + " -> " + machines[i].CustomName);
            }
            catch (Exception ex)
            {
                _cycleLog.Add("QUEUE ERROR " + Friendly(alias) + ": " + ex.Message);
            }
        }
    }
}

void DiscoverGrid()
{
    _productionBlocks.Clear();
    _stockInventories.Clear();
    _outputInventories.Clear();
    _inputInventories.Clear();

    GridTerminalSystem.GetBlocksOfType<IMyProductionBlock>(_productionBlocks, b => b != null && b.IsSameConstructAs(Me));

    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocks(blocks);

    for (int i = 0; i < blocks.Count; i++)
    {
        IMyTerminalBlock b = blocks[i];
        if (b == null || !b.IsSameConstructAs(Me) || !b.HasInventory) continue;

        bool source = ContainsIgnoreCase(b.CustomName, _componentTag) || ContainsIgnoreCase(b.CustomName, _ingotTag);
        if (source)
        {
            IMyInventory inv = b.GetInventory(0);
            if (inv != null) _stockInventories.Add(inv);
        }
    }

    for (int i = 0; i < _productionBlocks.Count; i++)
    {
        IMyProductionBlock pb = _productionBlocks[i];
        if (pb == null) continue;
        IMyInventory outInv = pb.OutputInventory;
        if (outInv != null) _outputInventories.Add(outInv);

        // Production inputs are tracked separately. They can satisfy dependencies
        // for work already queued, but are never counted as base stock reserves.
        if (!(pb is IMyRefinery))
        {
            IMyAssembler asm = pb as IMyAssembler;
            if (asm == null || asm.Mode != MyAssemblerMode.Disassembly)
            {
                IMyInventory inInv = pb.InputInventory;
                if (inInv != null) _inputInventories.Add(inInv);
            }
        }
    }
}

void ScanInventory()
{
    _onHand.Clear();
    _stagedInputs.Clear();

    for (int i = 0; i < _stockInventories.Count; i++)
        CountInventory(_stockInventories[i], _onHand);

    // Count finished items waiting in machine outputs so we do not queue duplicates
    // while GOAT (or the player) is waiting to move them to stock containers.
    for (int i = 0; i < _outputInventories.Count; i++)
        CountInventory(_outputInventories[i], _onHand);

    // Inputs already staged inside production machines support existing queues.
    // Keep them separate so they never falsely satisfy a permanent stock target.
    for (int i = 0; i < _inputInventories.Count; i++)
        CountInventory(_inputInventories[i], _stagedInputs);
}

void CountInventory(IMyInventory inv, Dictionary<string, double> totals)
{
    if (inv == null) return;
    List<MyInventoryItem> items = new List<MyInventoryItem>();
    inv.GetItems(items);

    for (int i = 0; i < items.Count; i++)
    {
        string alias = AliasFromType(items[i].Type);
        if (alias == null) continue;
        AddTo(totals, alias, (double)items[i].Amount);
    }
}

void ScanQueues()
{
    _queuedOutput.Clear();
    _queuedJobs.Clear();

    Dictionary<string, string> reverse = BuildBlueprintReverseMap();
    List<MyProductionItem> queue = new List<MyProductionItem>();

    for (int i = 0; i < _productionBlocks.Count; i++)
    {
        IMyProductionBlock pb = _productionBlocks[i];
        if (pb == null || pb is IMyRefinery) continue;

        IMyAssembler asm = pb as IMyAssembler;
        if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;

        queue.Clear();
        pb.GetQueue(queue);

        for (int q = 0; q < queue.Count; q++)
        {
            string key = queue[q].BlueprintId.ToString();
            string alias;
            if (!reverse.TryGetValue(key, out alias)) continue;

            double jobs = (double)queue[q].Amount;
            double yield = 1.0;
            Recipe r;
            if (_recipes.TryGetValue(alias, out r)) yield = GetRecipeOutput(alias, r);

            AddTo(_queuedJobs, alias, jobs);
            AddTo(_queuedOutput, alias, jobs * yield);
        }
    }
}

Dictionary<string, string> BuildBlueprintReverseMap()
{
    Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var kv in _items)
    {
        MyDefinitionId bp;
        if (!TryGetBlueprint(kv.Key, out bp)) continue;
        string key = bp.ToString();
        if (!map.ContainsKey(key)) map.Add(key, kv.Key);
    }
    return map;
}

List<IMyProductionBlock> GetBestMachines(string alias, MyDefinitionId bp, Recipe recipe)
{
    List<MachineChoice> choices = new List<MachineChoice>();

    for (int i = 0; i < _productionBlocks.Count; i++)
    {
        IMyProductionBlock pb = _productionBlocks[i];
        if (pb == null) continue;
        if (pb is IMyRefinery) continue;
        if (!pb.IsWorking) continue;
        if (HasManualTag(pb.CustomName)) continue;

        IMyAssembler asm = pb as IMyAssembler;
        if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;

        string machineText = MachineText(pb);
        if (IsManualBench(machineText)) continue;

        bool survival = machineText.Contains("survivalkit") || machineText.Contains("survival kit");
        if (survival && !_allowSurvivalKitFallback) continue;

        bool canUse = false;
        try { canUse = pb.CanUseBlueprint(bp); }
        catch { canUse = false; }
        if (!canUse) continue;

        if (_requireConveyorAccess && !HasConveyorPath(pb, recipe))
            continue;

        int rank = MachineRank(machineText, recipe, survival);
        choices.Add(new MachineChoice(pb, rank, QueueLoad(pb)));
    }

    if (choices.Count == 0) return new List<IMyProductionBlock>();

    int bestRank = int.MaxValue;
    for (int i = 0; i < choices.Count; i++)
        if (choices[i].Rank < bestRank) bestRank = choices[i].Rank;

    List<MachineChoice> best = new List<MachineChoice>();
    for (int i = 0; i < choices.Count; i++)
        if (choices[i].Rank == bestRank) best.Add(choices[i]);

    best.Sort((a, b) => a.QueueLoad.CompareTo(b.QueueLoad));

    List<IMyProductionBlock> result = new List<IMyProductionBlock>();
    for (int i = 0; i < best.Count; i++) result.Add(best[i].Block);
    return result;
}

bool HasConveyorPath(IMyProductionBlock pb, Recipe recipe)
{
    if (_stockInventories.Count == 0) return true;
    if (recipe.Inputs.Count == 0) return true;

    ItemDef inputDef;
    string firstInput = Canon(recipe.Inputs[0].Item);
    if (!_items.TryGetValue(firstInput, out inputDef)) return true;

    MyItemType type;
    try { type = MyItemType.Parse(inputDef.Type); }
    catch { return true; }

    for (int i = 0; i < _stockInventories.Count; i++)
    {
        try
        {
            if (_stockInventories[i].CanTransferItemTo(pb.InputInventory, type))
                return true;
        }
        catch { }
    }
    return false;
}

int MachineRank(string machineText, Recipe recipe, bool survival)
{
    if (survival) return 9000;

    for (int i = 0; i < recipe.PreferredMachines.Count; i++)
    {
        string token = recipe.PreferredMachines[i].ToLowerInvariant();
        // A normal Assembler is preferred for generic assembler work; do not
        // let the token accidentally rank an Advanced Assembler equally.
        if (token == "assembler" && machineText.Contains("advanced assembler")) continue;
        if (machineText.Contains(token)) return i * 100;
    }

    return 5000;
}

double QueueLoad(IMyProductionBlock pb)
{
    List<MyProductionItem> q = new List<MyProductionItem>();
    try { pb.GetQueue(q); }
    catch { return 999999; }

    double total = 0;
    for (int i = 0; i < q.Count; i++) total += (double)q[i].Amount;
    return total;
}

bool IsManualBench(string machineText)
{
    // Industrial Overhaul benches are deliberately excluded from unattended
    // production. They may technically support blueprints but are poor targets
    // for base automation, especially the manually-fed Basic Assembling Bench.
    if (machineText.Contains("basicassemblingbench")) return true;
    if (machineText.Contains("basic assembling bench")) return true;
    if (machineText.Contains("assemblingbench")) return true;
    if (machineText.Contains("assembling bench")) return true;
    return false;
}

bool HasManualTag(string name)
{
    for (int i = 0; i < _manualTags.Length; i++)
    {
        string t = _manualTags[i];
        if (!string.IsNullOrWhiteSpace(t) && ContainsIgnoreCase(name, t)) return true;
    }
    return false;
}

string MachineText(IMyProductionBlock pb)
{
    return ((pb.CustomName ?? "") + " " + pb.BlockDefinition.SubtypeId.ToString()).ToLowerInvariant();
}

void LoadConfig()
{
    _configErrors.Clear();
    _stockTargets.Clear();
    _overrideBlueprints.Clear();
    _learnedBlueprints.Clear();
    _learnedYields.Clear();
    RemoveDynamicLearnedItems();

    MyIniParseResult result;
    if (!_ini.TryParse(Me.CustomData, out result))
    {
        _configErrors.Add("Custom Data parse error: " + result.ToString());
        return;
    }

    _componentTag = _ini.Get("Config", "ComponentTag").ToString("Components");
    _ingotTag = _ini.Get("Config", "IngotTag").ToString("Ingots");
    _autoProduction = _ini.Get("Config", "AutoProduction").ToBoolean(false);
    _allowSurvivalKitFallback = _ini.Get("Config", "AllowSurvivalKitFallback").ToBoolean(false);
    _requireConveyorAccess = _ini.Get("Config", "RequireConveyorAccess").ToBoolean(true);

    // IMPORTANT: GOAT uses [Manual]/!GSIM-Manual on IO machines.
    // Those tags are intentionally NOT honored here; otherwise IOPM would
    // exclude the exact machines it is meant to automate. IOPM has its own
    // independent opt-out tags.
    string tags = _ini.Get("Config", "OptOutTags").ToString("[IOPM-Manual],!IOPM-Manual");
    _manualTags = tags.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < _manualTags.Length; i++) _manualTags[i] = _manualTags[i].Trim();

    List<MyIniKey> keys = new List<MyIniKey>();

    // v1.0.8+: load self-discovered item types BEFORE Stock/Blueprint sections.
    // This makes a learned IO item a first-class inventory item after reload,
    // even when it was never present in the built-in catalog.
    _ini.GetKeys("LearnedItems", keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string rawAlias = keys[i].Name;
        string itemType = _ini.Get("LearnedItems", rawAlias).ToString("").Trim();
        if (itemType == "") continue;
        RegisterLearnedItemFromConfig(rawAlias, itemType);
    }

    keys.Clear();
    _ini.GetKeys("Stock", keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string rawName = keys[i].Name;
        string alias = Canon(rawName);
        if (!_items.ContainsKey(alias))
        {
            _configErrors.Add("Unknown [Stock] item: " + rawName);
            continue;
        }

        double value;
        if (!double.TryParse(_ini.Get("Stock", rawName).ToString(), out value) || value < 0)
        {
            _configErrors.Add("Invalid stock target: " + rawName);
            continue;
        }
        _stockTargets[alias] = value;
    }

    keys.Clear();
    _ini.GetKeys("BlueprintOverrides", keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string alias = Canon(keys[i].Name);
        string bp = _ini.Get("BlueprintOverrides", keys[i].Name).ToString("").Trim();
        if (_items.ContainsKey(alias) && bp != "")
            _overrideBlueprints[alias] = bp;
    }

    // Learned mappings are intentionally stored in Custom Data so they can be
    // copied to another PB, another base, or sent back for future script builds.
    keys.Clear();
    _ini.GetKeys("LearnedBlueprints", keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string rawName = keys[i].Name;
        string alias = Canon(rawName);
        string bp = _ini.Get("LearnedBlueprints", rawName).ToString("").Trim();
        if (bp == "") continue;
        if (!_items.ContainsKey(alias))
        {
            _configErrors.Add("Unknown [LearnedBlueprints] item: " + rawName + " (missing [LearnedItems] entry?)");
            continue;
        }

        MyDefinitionId parsed;
        if (!MyDefinitionId.TryParse(bp, out parsed))
        {
            _configErrors.Add("Invalid learned blueprint: " + rawName);
            continue;
        }
        _learnedBlueprints[alias] = bp;
    }

    keys.Clear();
    _ini.GetKeys("LearnedYields", keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string rawName = keys[i].Name;
        string alias = Canon(rawName);
        double value;
        if (!_items.ContainsKey(alias)) continue;
        if (double.TryParse(_ini.Get("LearnedYields", rawName).ToString(), out value) && value > 0)
            _learnedYields[alias] = value;
    }
}

void WriteDefaultCustomData()
{
    MyIni ini = new MyIni();
    ini.Set("Config", "ComponentTag", "Components");
    ini.Set("Config", "IngotTag", "Ingots");
    ini.Set("Config", "AutoProduction", false);
    ini.Set("Config", "AllowSurvivalKitFallback", false);
    ini.Set("Config", "RequireConveyorAccess", true);
    ini.Set("Config", "OptOutTags", "[IOPM-Manual],!IOPM-Manual");

    ini.Set("Stock", "Motor", 500);
    ini.Set("Stock", "SteelPlate", 10000);

    ini.Set("BlueprintOverrides", "ExampleItem", "");
    Me.CustomData = ini.ToString();
}

void SetAutoProduction(bool enabled)
{
    MyIniParseResult result;
    if (!_ini.TryParse(Me.CustomData, out result))
    {
        Echo("Cannot update Custom Data: " + result.ToString());
        return;
    }
    _ini.Set("Config", "AutoProduction", enabled);
    Me.CustomData = _ini.ToString();
    LoadConfig();
    UpdateRuntimeFrequency();
    Echo("AutoProduction = " + (_autoProduction ? "ON" : "OFF"));
}


void UpdateRuntimeFrequency()
{
    if (_learnPhase == 1) Runtime.UpdateFrequency = UpdateFrequency.Update10;
    else if (_learnPhase == 2) Runtime.UpdateFrequency = UpdateFrequency.Update1;
    else Runtime.UpdateFrequency = _autoProduction ? UpdateFrequency.Update100 : UpdateFrequency.None;
}

double GetRecipeOutput(string alias, Recipe recipe)
{
    alias = Canon(alias);
    double learned;
    if (_learnedYields.TryGetValue(alias, out learned) && learned > 0) return learned;

    // Learned blueprint + no explicit yield means the learner observed the
    // normal 1-job -> 1-item case. Exceptional yields (Lightbulb=10) are
    // persisted explicitly. This keeps Custom Data clean without falling back
    // to a stale built-in batch yield.
    if (_learnedBlueprints.ContainsKey(alias)) return 1.0;

    return recipe != null && recipe.Output > 0 ? recipe.Output : 1.0;
}

void StartBulkLearning(int seconds)
{
    if (_learnPhase != 0)
    {
        Echo("A learning session is already active.\nUse: learn status | learn finish | learn cancel");
        return;
    }

    DiscoverGrid();
    _bulkLearnMachines.Clear();
    _bulkLearnLog.Clear();
    _bulkLearnMissed.Clear();
    _bulkLearnKnownSkipped.Clear();
    ResetActiveLearning();
    _learnCaptureSeconds = seconds;
    _learnCaptureDeadline = DateTime.UtcNow.AddSeconds(seconds);

    for (int i = 0; i < _productionBlocks.Count; i++)
    {
        IMyProductionBlock pb = _productionBlocks[i];
        if (!EligibleForBulkLearning(pb)) continue;

        BulkLearnMachine st = new BulkLearnMachine(pb);
        st.BaselineQueue = SnapshotQueueTotals(pb);
        st.LastQueue = CopyTotals(st.BaselineQueue);
        st.LastOutput = SnapshotRawInventory(pb.OutputInventory);
        st.Status = "CAPTURE";
        _bulkLearnMachines[pb.EntityId] = st;

        // [Locked] is GOAT's inventory-ignore tag. Keep output in the machine
        // while we observe it, then restore the exact original name.
        if (!ContainsIgnoreCase(pb.CustomName, "[Locked]"))
            pb.CustomName = pb.CustomName + LEARN_LOCK_TAG;

        IMyFunctionalBlock fb = pb as IMyFunctionalBlock;
        if (fb != null) fb.Enabled = false;
    }

    if (_bulkLearnMachines.Count == 0)
    {
        Echo("BULK LEARN\nNo eligible WORKING production machines found.\nEnable any machine you want included, then try again.");
        return;
    }

    _learnPhase = 1;
    UpdateRuntimeFrequency();
    ShowBulkLearningStatus();
}

bool EligibleForBulkLearning(IMyProductionBlock pb)
{
    if (pb == null || pb is IMyRefinery) return false;
    if (HasManualTag(pb.CustomName)) return false;
    if (!pb.IsFunctional) return false;

    // Respect machines the player intentionally had disabled before learning.
    IMyFunctionalBlock fb = pb as IMyFunctionalBlock;
    if (fb != null && !fb.Enabled) return false;

    IMyAssembler asm = pb as IMyAssembler;
    if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) return false;
    string text = MachineText(pb);
    if (IsManualBench(text)) return false;
    bool survival = text.Contains("survivalkit") || text.Contains("survival kit");
    if (survival && !_allowSurvivalKitFallback) return false;
    return true;
}

void TickBulkLearning()
{
    if (_learnPhase == 1)
    {
        if (DateTime.UtcNow >= _learnCaptureDeadline)
            FinishLearningCapture();
        else
            ShowBulkLearningStatus();
        return;
    }

    if (_learnPhase == 2)
    {
        ObserveBulkLearning();
        return;
    }
}

void FinishLearningCapture()
{
    if (_learnPhase == 0)
    {
        Echo("No bulk learning session is active.");
        return;
    }
    if (_learnPhase == 2)
    {
        ShowBulkLearningStatus();
        return;
    }

    int trainingBlueprints = 0;
    foreach (var kv in _bulkLearnMachines)
    {
        BulkLearnMachine st = kv.Value;
        IMyProductionBlock pb = st.Block;
        if (pb == null) continue;

        List<MyProductionItem> ordered = SnapshotQueueOrdered(pb);
        Dictionary<string, double> now = QueueTotals(ordered);
        st.TrainingBlueprints.Clear();
        st.TrainingOrder.Clear();

        // Determine the quantity ADDED during capture, then preserve the queue's
        // actual order so we can learn the new blueprints without reordering it.
        Dictionary<string, double> addedByBlueprint = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in now)
        {
            double before = GetRaw(st.BaselineQueue, q.Key);
            double added = q.Value - before;
            if (added > 0.0001) addedByBlueprint[q.Key] = added;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            string bp = ordered[i].BlueprintId.ToString();
            double added = GetRaw(addedByBlueprint, bp);
            if (added <= 0.0001) continue;

            // v1.0.10: bulk learning is for UNKNOWN blueprint mappings only.
            // If this blueprint already resolves to an item through an operator
            // override, trusted built-in, or learned mapping, leave the queue
            // exactly as-is but do not spend observation time relearning it.
            string knownOwner;
            if (TryGetKnownBlueprintOwner(bp, out knownOwner))
            {
                st.KnownSkipped++;
                _bulkLearnKnownSkipped.Add(st.OriginalName + " | " + BlueprintShortName(bp) +
                    " | known as " + Friendly(knownOwner));
                continue;
            }

            if (!st.TrainingBlueprints.ContainsKey(bp))
            {
                st.TrainingBlueprints[bp] = added;
                st.TrainingOrder.Add(bp);
                trainingBlueprints++;
            }
        }

        st.LastQueue = CopyTotals(now);
        st.LastOutput = SnapshotRawInventory(pb.OutputInventory);
        st.Status = st.TrainingOrder.Count > 0 ? "WAITING" : "COMPLETE";

        // IMPORTANT: every learning machine remains OFF here. Observation is
        // serialized globally; only the active machine is enabled later.
        IMyFunctionalBlock fb = pb as IMyFunctionalBlock;
        if (fb != null) fb.Enabled = false;
    }

    if (trainingBlueprints == 0)
    {
        RestoreBulkLearningMachines();
        _learnPhase = 0;
        UpdateRuntimeFrequency();
        Echo("BULK LEARN\nNo UNKNOWN blueprint work was detected." +
            (_bulkLearnKnownSkipped.Count > 0 ? "\nKnown blueprints skipped: " + _bulkLearnKnownSkipped.Count : "") +
            "\nMachines restored. Queues were not changed.");
        return;
    }

    ResetActiveLearning();
    _learnPhase = 2;
    UpdateRuntimeFrequency();
    ShowBulkLearningStatus();
}

void ObserveBulkLearning()
{
    if (_learnActiveMachineId == 0)
    {
        BulkLearnMachine next = FindNextLearningMachine();
        if (next == null)
        {
            // No machine is eligible THIS tick. If pending work remains, every
            // remaining machine is temporarily parked/blocked. Keep the session
            // alive and report that state instead of letting one machine monopolize
            // the learner or incorrectly declaring completion.
            if (HasPendingLearningWork())
            {
                ShowBulkLearningStatus();
                return;
            }

            CompleteBulkLearning();
            return;
        }
        BeginNextLearningTarget(next);
    }

    BulkLearnMachine st;
    if (!_bulkLearnMachines.TryGetValue(_learnActiveMachineId, out st) || st == null || st.Block == null)
    {
        ResetActiveLearning();
        return;
    }

    if (_learnActiveMode == 1) TickDrainToTrainingTarget(st);
    else if (_learnActiveMode == 2) TickLearnTrainingTarget(st);
    else if (_learnActiveMode == 3) TickCaptureTrainingOutput(st);
    else ResetActiveLearning();

    ShowBulkLearningStatus();
}

BulkLearnMachine FindNextLearningMachine()
{
    DateTime now = DateTime.UtcNow;
    foreach (var kv in _bulkLearnMachines)
    {
        BulkLearnMachine st = kv.Value;
        if (st == null || st.Block == null) continue;
        if (st.TrainingOrder.Count <= 0) continue;
        if (st.RetryAfterUtc > now) continue;
        return st;
    }
    return null;
}

bool HasPendingLearningWork()
{
    foreach (var kv in _bulkLearnMachines)
    {
        BulkLearnMachine st = kv.Value;
        if (st != null && st.Block != null && st.TrainingOrder.Count > 0) return true;
    }
    return false;
}

bool TryGetKnownBlueprintOwner(string blueprint, out string alias)
{
    alias = null;
    if (string.IsNullOrWhiteSpace(blueprint)) return false;

    // BuildBlueprintReverseMap uses the same production precedence as normal
    // operation (override -> trusted built-in -> learned -> other built-in).
    Dictionary<string, string> reverse = BuildBlueprintReverseMap();
    return reverse.TryGetValue(blueprint, out alias);
}

void DeferActiveLearning(BulkLearnMachine st, string reason)
{
    if (st == null)
    {
        ResetActiveLearning();
        return;
    }

    SetMachineEnabled(st.Block, false);
    st.BlockedAttempts++;

    // Do not retry forever. A genuinely unavailable recipe should not keep the
    // entire learning session alive indefinitely. Two parked retries are allowed;
    // the third blocked observation is recorded as a miss and learning continues.
    if (st.BlockedAttempts > LEARN_MAX_BLOCKED_RETRIES)
    {
        MissActiveLearning(st, reason + " after " + st.BlockedAttempts + " blocked attempts");
        return;
    }

    st.BlockedReason = reason;
    st.RetryAfterUtc = DateTime.UtcNow.AddSeconds(LEARN_RETRY_SECONDS);
    st.Status = "BLOCKED - " + reason + " (parked; retry " + st.BlockedAttempts + "/" + LEARN_MAX_BLOCKED_RETRIES + ")";
    ResetActiveLearning();
}

void BeginNextLearningTarget(BulkLearnMachine st)
{
    if (st == null || st.Block == null || st.TrainingOrder.Count == 0) return;

    _learnActiveMachineId = st.Block.EntityId;
    _learnActiveBlueprint = st.TrainingOrder[0];
    st.RetryAfterUtc = DateTime.MinValue;
    st.BlockedReason = "";
    _learnActiveSince = DateTime.UtcNow;
    _learnLastProgress = DateTime.UtcNow;
    _learnActiveLastQueueTotal = QueueTotal(st.Block);
    st.Status = "WAITING";

    List<MyProductionItem> q = SnapshotQueueOrdered(st.Block);
    if (q.Count == 0 || GetQueueBlueprintAmount(q, _learnActiveBlueprint) <= 0.0001)
    {
        MissActiveLearning(st, "training blueprint no longer in queue");
        return;
    }

    string head = q[0].BlueprintId.ToString();
    if (head.Equals(_learnActiveBlueprint, StringComparison.OrdinalIgnoreCase))
    {
        ArmActiveLearning(st, q);
        return;
    }

    // There is intentional work ahead of the training item. Let ONLY this
    // machine run until the training blueprint reaches the head of its queue.
    _learnActiveMode = 1;
    st.Status = "WAITING - existing jobs ahead";
    SetMachineEnabled(st.Block, true);
}

void TickDrainToTrainingTarget(BulkLearnMachine st)
{
    List<MyProductionItem> q = SnapshotQueueOrdered(st.Block);
    if (q.Count == 0 || GetQueueBlueprintAmount(q, _learnActiveBlueprint) <= 0.0001)
    {
        SetMachineEnabled(st.Block, false);
        MissActiveLearning(st, "training blueprint disappeared before observation");
        return;
    }

    double queueTotal = QueueTotal(q);
    if (_learnActiveLastQueueTotal < 0 || Math.Abs(queueTotal - _learnActiveLastQueueTotal) > 0.0001)
    {
        _learnActiveLastQueueTotal = queueTotal;
        _learnLastProgress = DateTime.UtcNow;
    }

    string head = q[0].BlueprintId.ToString();
    if (head.Equals(_learnActiveBlueprint, StringComparison.OrdinalIgnoreCase))
    {
        // Stop at the boundary BEFORE intentionally observing this blueprint.
        SetMachineEnabled(st.Block, false);
        ArmActiveLearning(st, q);
        return;
    }

    if ((DateTime.UtcNow - _learnLastProgress).TotalSeconds >= LEARN_BLOCKED_SECONDS)
    {
        DeferActiveLearning(st, "existing job ahead not progressing");
        return;
    }

    st.Status = "WAITING - existing jobs ahead";
    SetMachineEnabled(st.Block, true);
}

void ArmActiveLearning(BulkLearnMachine st, List<MyProductionItem> q)
{
    SetMachineEnabled(st.Block, false);
    _learnActiveMode = 2;
    _learnActiveStartJobs = GetQueueBlueprintAmount(q, _learnActiveBlueprint);
    _learnActiveCompletedJobs = 0;
    _learnActiveOutputBaseline = SnapshotRawInventory(st.Block.OutputInventory);
    _learnActiveSince = DateTime.UtcNow;
    _learnLastProgress = DateTime.UtcNow;
    _learnActiveLastQueueTotal = QueueTotal(q);
    st.Status = "LEARNING - " + BlueprintShortName(_learnActiveBlueprint);
    SetMachineEnabled(st.Block, true);
}

void TickLearnTrainingTarget(BulkLearnMachine st)
{
    // v1.0.11: keep the active IO machine ON continuously while the target job
    // runs. Earlier versions toggled OFF/ON every Update1, which can prevent IO
    // machines from accumulating enough simulation time to make progress.
    SetMachineEnabled(st.Block, true);

    List<MyProductionItem> q = SnapshotQueueOrdered(st.Block);
    double nowTarget = GetQueueBlueprintAmount(q, _learnActiveBlueprint);
    double completedJobs = _learnActiveStartJobs - nowTarget;

    if (completedJobs > 0.0001)
    {
        // Freeze as soon as queue progress is visible. Mapping validation remains
        // additive-only, so a fast next job can cause a miss but cannot corrupt an
        // existing trusted mapping.
        SetMachineEnabled(st.Block, false);
        _learnActiveCompletedJobs = completedJobs;
        _learnOutputWaitSince = DateTime.UtcNow;
        _learnActiveMode = 3;
        st.Status = "LEARNING - capturing target output";
        TickCaptureTrainingOutput(st);
        return;
    }

    double queueTotal = QueueTotal(q);
    if (_learnActiveLastQueueTotal < 0 || Math.Abs(queueTotal - _learnActiveLastQueueTotal) > 0.0001)
    {
        _learnActiveLastQueueTotal = queueTotal;
        _learnLastProgress = DateTime.UtcNow;
    }

    if ((DateTime.UtcNow - _learnLastProgress).TotalSeconds >= LEARN_BLOCKED_SECONDS)
    {
        DeferActiveLearning(st, "target not progressing (check inputs/power)");
        return;
    }

    st.Status = "LEARNING - " + BlueprintShortName(_learnActiveBlueprint);
    // Deliberately leave the machine ON until a later Update1 observes progress.
}

void TickCaptureTrainingOutput(BulkLearnMachine st)
{
    // Always inspect while frozen after target queue progress. The machine is
    // intentionally NOT pulsed here; the target already had uninterrupted run
    // time in mode 2, and allowing more production now could advance the next job.
    SetMachineEnabled(st.Block, false);
    Dictionary<string, double> nowOutput = SnapshotRawInventory(st.Block.OutputInventory);

    string outputItemType = null;
    double outputDelta = 0;
    int positiveTypes = 0;
    foreach (var outNow in nowOutput)
    {
        double delta = outNow.Value - GetRaw(_learnActiveOutputBaseline, outNow.Key);
        if (delta > 0.0001)
        {
            positiveTypes++;
            outputItemType = outNow.Key;
            outputDelta = delta;
        }
    }

    if (positiveTypes > 1)
    {
        MissActiveLearning(st, "ambiguous output (multiple raw item types changed during target run)");
        return;
    }

    if (positiveTypes == 1 && outputItemType != null)
    {
        double completed = _learnActiveCompletedJobs;
        if (completed <= 0.0001) completed = 1.0;
        double yield = outputDelta / completed;
        if (yield <= 0) yield = 1;

        bool discovered;
        string outputAlias = ResolveOrDiscoverItemAlias(outputItemType, out discovered);
        if (!string.IsNullOrWhiteSpace(outputAlias))
        {
            string conflictReason;
            if (!BulkLearningMappingIsSafe(outputAlias, outputItemType, _learnActiveBlueprint, out conflictReason))
            {
                MissActiveLearning(st, conflictReason);
                return;
            }

            bool wroteNew;
            if (PersistLearnedDiscoveryAdditive(outputAlias, outputItemType, _learnActiveBlueprint, yield, out wroteNew))
            {
                if (wroteNew)
                {
                    _learnedBlueprints[outputAlias] = _learnActiveBlueprint;
                    if (Math.Abs(yield - 1.0) > 0.0001) _learnedYields[outputAlias] = yield;
                    else _learnedYields.Remove(outputAlias);
                }

                string prefix = discovered ? "DISCOVERED " : (wroteNew ? "LEARNED " : "VERIFIED ");
                _bulkLearnLog.Add(prefix + Friendly(outputAlias) + " = " + _learnActiveBlueprint +
                    (Math.Abs(yield - 1.0) > 0.0001 ? " (yield " + F(yield) + ")" : ""));
                CompleteActiveLearning(st);
                return;
            }

            MissActiveLearning(st, "output identified but Custom Data save failed");
            return;
        }
    }

    if ((DateTime.UtcNow - _learnOutputWaitSince).TotalSeconds >= LEARN_OUTPUT_WAIT_SECONDS)
    {
        MissActiveLearning(st, "no output delta observed after target queue advanced");
        return;
    }

    st.Status = "LEARNING - waiting for target output";
    // Remain OFF while waiting. If no output appears within the timeout, fail
    // conservatively rather than allowing the next queued blueprint to run.
}

bool BulkLearningMappingIsSafe(string alias, string itemType, string blueprint, out string reason)
{
    alias = Canon(alias);
    reason = "";

    // Bulk learning is intentionally ADDITIVE ONLY. It may fill missing
    // knowledge, but it is never allowed to replace a mapping we already trust.
    string existing;
    if (_learnedBlueprints.TryGetValue(alias, out existing) && !existing.Equals(blueprint, StringComparison.OrdinalIgnoreCase))
    {
        reason = "CONFLICT - preserved existing mapping for " + Friendly(alias) + " (" + BlueprintShortName(existing) + ")";
        return false;
    }

    string builtin;
    if (_trustedBuiltinBlueprintAliases.Contains(alias) && _builtinBlueprints.TryGetValue(alias, out builtin) &&
        !builtin.Equals(blueprint, StringComparison.OrdinalIgnoreCase))
    {
        reason = "CONFLICT - observed " + BlueprintShortName(blueprint) + " for trusted " + Friendly(alias) +
            "; kept " + BlueprintShortName(builtin);
        return false;
    }

    // For previously unknown/untrusted mappings, require the blueprint name and
    // output item name to look related. This is deliberately conservative: a
    // strange legitimate recipe may be skipped and relearned manually, but a
    // cross-item race (for example AlkalinePowerCell -> Electromagnet) must not
    // become persistent knowledge.
    if (!_trustedBuiltinBlueprintAliases.Contains(alias) && !_learnedBlueprints.ContainsKey(alias) &&
        !BlueprintLooksRelatedToOutput(alias, itemType, blueprint))
    {
        reason = "LOW CONFIDENCE - blueprint/output names do not match: " + BlueprintShortName(blueprint) +
            " -> " + Friendly(alias);
        return false;
    }

    // Also refuse to claim a blueprint that already resolves to a DIFFERENT
    // trusted/known item. This catches cross-item races instead of corrupting
    // both aliases with one blueprint.
    Dictionary<string, string> reverse = BuildBlueprintReverseMap();
    string mappedAlias;
    if (reverse.TryGetValue(blueprint, out mappedAlias) && !Canon(mappedAlias).Equals(alias, StringComparison.OrdinalIgnoreCase))
    {
        reason = "CONFLICT - blueprint already belongs to " + Friendly(mappedAlias) + "; observed output " + Friendly(alias);
        return false;
    }

    return true;
}

bool BlueprintLooksRelatedToOutput(string alias, string itemType, string blueprint)
{
    string a = LearningNameKey(alias);
    string bp = LearningNameKey(BlueprintShortName(blueprint));

    string subtype = itemType ?? "";
    int slash = subtype.LastIndexOf('/');
    if (slash >= 0 && slash + 1 < subtype.Length) subtype = subtype.Substring(slash + 1);
    string it = LearningNameKey(subtype);

    if (NamesRelated(a, bp)) return true;
    if (NamesRelated(it, bp)) return true;
    return false;
}

bool NamesRelated(string a, string b)
{
    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
    if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
    if (a.Length >= 4 && b.Length >= 4 && (a.Contains(b) || b.Contains(a))) return true;
    return false;
}

string LearningNameKey(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return "";
    string s = value.ToLowerInvariant();
    string clean = "";
    for (int i = 0; i < s.Length; i++)
        if (char.IsLetterOrDigit(s[i])) clean += s[i];

    if (clean.StartsWith("po") && clean.Length > 4) clean = clean.Substring(2);
    clean = clean.Replace("myobjectbuilder", "");
    clean = clean.Replace("blueprintdefinition", "");
    clean = clean.Replace("component", "");
    clean = clean.Replace("magazine", "");

    // IO names the Small Steel Tube item "SmallTube" in several blueprint IDs.
    clean = clean.Replace("steel", "");
    return clean;
}

bool PersistLearnedDiscoveryAdditive(string alias, string itemType, string blueprint, double yield, out bool wroteNew)
{
    wroteNew = false;
    alias = Canon(alias);
    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result)) return false;

    string existing = ini.Get("LearnedBlueprints", alias).ToString("").Trim();
    if (existing != "")
    {
        // Existing knowledge is sacred during BULK learning. Verification is
        // allowed, replacement is not. Manual `learn <item>` remains the
        // deliberate way to change a mapping.
        return existing.Equals(blueprint, StringComparison.OrdinalIgnoreCase);
    }

    // Trusted built-ins need no duplicate learned entry. Matching observations
    // are considered verification only.
    string builtin;
    if (_trustedBuiltinBlueprintAliases.Contains(alias) && _builtinBlueprints.TryGetValue(alias, out builtin) &&
        builtin.Equals(blueprint, StringComparison.OrdinalIgnoreCase))
        return true;

    if (!_builtinItemAliases.Contains(alias))
        ini.Set("LearnedItems", alias, itemType);

    ini.Set("LearnedBlueprints", alias, blueprint);
    if (yield > 0 && Math.Abs(yield - 1.0) > 0.0001) ini.Set("LearnedYields", alias, yield);

    Me.CustomData = ini.ToString();
    wroteNew = true;
    return true;
}

void CompleteActiveLearning(BulkLearnMachine st)
{
    string bp = _learnActiveBlueprint;
    if (st != null)
    {
        st.TrainingBlueprints.Remove(bp);
        RemoveFirstString(st.TrainingOrder, bp);
        st.RetryAfterUtc = DateTime.MinValue;
        st.BlockedReason = "";
        st.BlockedAttempts = 0;
        st.Status = st.TrainingOrder.Count > 0 ? "WAITING" : "COMPLETE";
        SetMachineEnabled(st.Block, false);
    }
    ResetActiveLearning();
}

void MissActiveLearning(BulkLearnMachine st, string reason)
{
    string bp = _learnActiveBlueprint;
    string machine = st != null ? st.OriginalName : "Unknown machine";
    _bulkLearnMissed.Add(machine + " | " + BlueprintShortName(bp) + " | " + reason);
    if (st != null)
    {
        st.TrainingBlueprints.Remove(bp);
        RemoveFirstString(st.TrainingOrder, bp);
        st.RetryAfterUtc = DateTime.MinValue;
        st.BlockedReason = "";
        st.BlockedAttempts = 0;
        st.Status = st.TrainingOrder.Count > 0 ? "WAITING" : "COMPLETE";
        SetMachineEnabled(st.Block, false);
    }
    ResetActiveLearning();
}

void CompleteBulkLearning()
{
    int learnedCount = _bulkLearnLog.Count;
    int missedCount = _bulkLearnMissed.Count;
    RestoreBulkLearningMachines();
    _learnPhase = 0;
    ResetActiveLearning();
    LoadConfig();
    UpdateRuntimeFrequency();

    Echo("BULK LEARN COMPLETE\n" + learnedCount + " mapping(s) learned." +
        (_bulkLearnKnownSkipped.Count > 0 ? "\n" + _bulkLearnKnownSkipped.Count + " known blueprint(s) skipped." : "") +
        (missedCount > 0 ? "\n" + missedCount + " item(s) could not be identified." : "") +
        "\nSaved to Custom Data [LearnedItems] / [LearnedBlueprints] / [LearnedYields].\n\nMachines restored. Queues were NOT changed.");
    int start = Math.Max(0, _bulkLearnLog.Count - 8);
    for (int i = start; i < _bulkLearnLog.Count; i++) Echo("- " + _bulkLearnLog[i]);
    if (missedCount > 0)
    {
        Echo("\nMISSED:");
        int mstart = Math.Max(0, missedCount - 5);
        for (int i = mstart; i < missedCount; i++) Echo("- " + _bulkLearnMissed[i]);
    }
}

void CancelBulkLearning()
{
    if (_learnPhase == 0)
    {
        Echo("No bulk learning session is active.");
        return;
    }

    RestoreBulkLearningMachines();
    _learnPhase = 0;
    ResetActiveLearning();
    UpdateRuntimeFrequency();
    Echo("BULK LEARN CANCELLED\nMachines restored to original names/states.\nEverything you queued was left untouched.");
}

void RestoreBulkLearningMachines()
{
    foreach (var kv in _bulkLearnMachines)
    {
        BulkLearnMachine st = kv.Value;
        IMyProductionBlock pb = st.Block;
        if (pb == null) continue;
        pb.CustomName = st.OriginalName;
        IMyFunctionalBlock fb = pb as IMyFunctionalBlock;
        if (fb != null) fb.Enabled = st.OriginalEnabled;
    }
}

void ResetActiveLearning()
{
    _learnActiveMachineId = 0;
    _learnActiveBlueprint = "";
    _learnActiveMode = 0;
    _learnActiveStartJobs = 0;
    _learnActiveCompletedJobs = 0;
    _learnActiveOutputBaseline.Clear();
    _learnActiveLastQueueTotal = -1;
}

void ShowBulkLearningStatus()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION + "\nBULK LEARNING\n");
    if (_learnPhase == 0)
    {
        Echo("Not active.\nUse: learn start [seconds]");
        return;
    }

    if (_learnPhase == 1)
    {
        int left = (int)Math.Ceiling((_learnCaptureDeadline - DateTime.UtcNow).TotalSeconds);
        if (left < 0) left = 0;
        Echo("CAPTURE: " + left + " sec remaining");
        Echo("Machines temporarily OFF + [Locked].");
        Echo("Queue ONE of each item you want learned.\n");
        Echo("Machines: " + _bulkLearnMachines.Count);
        Echo("Run 'learn finish' when done early.");
        Echo("Queues will never be cleared/reordered.");
        return;
    }

    int totalPending = 0;
    Echo("OBSERVE: SERIALIZED (one machine/job at a time)");
    if (_learnActiveMachineId != 0)
    {
        BulkLearnMachine active;
        if (_bulkLearnMachines.TryGetValue(_learnActiveMachineId, out active) && active != null)
        {
            Echo("ACTIVE: " + active.OriginalName);
            Echo("  " + active.Status);
            Echo("  blueprint: " + BlueprintShortName(_learnActiveBlueprint));
        }
    }
    Echo("");

    int shown = 0;
    foreach (var kv in _bulkLearnMachines)
    {
        BulkLearnMachine st = kv.Value;
        if (st.TrainingOrder.Count <= 0) continue;
        totalPending += st.TrainingOrder.Count;
        if (shown < 8)
        {
            string retry = "";
            if (st.RetryAfterUtc > DateTime.UtcNow)
            {
                int sec = (int)Math.Ceiling((st.RetryAfterUtc - DateTime.UtcNow).TotalSeconds);
                if (sec < 0) sec = 0;
                retry = " | retry: " + sec + "s";
            }
            Echo(st.OriginalName + "\n  " + st.Status + " | pending: " + st.TrainingOrder.Count + retry);
            shown++;
        }
    }
    if (shown >= 8) Echo("...");

    Echo("\nPending blueprints: " + totalPending);
    Echo("Learned this session: " + _bulkLearnLog.Count);
    if (_bulkLearnKnownSkipped.Count > 0) Echo("Known blueprints skipped: " + _bulkLearnKnownSkipped.Count);
    if (_bulkLearnMissed.Count > 0) Echo("Missed/ambiguous: " + _bulkLearnMissed.Count);

    if (_learnActiveMachineId == 0 && totalPending > 0)
        Echo("All remaining work is temporarily BLOCKED/PARKED; learner will retry without stalling other machines.");

    Echo("Outputs held by temporary [Locked] tag.");
    Echo("Use 'learn cancel' to restore machines without touching queues.");
}

List<MyProductionItem> SnapshotQueueOrdered(IMyProductionBlock pb)
{
    List<MyProductionItem> q = new List<MyProductionItem>();
    if (pb == null) return q;
    try { pb.GetQueue(q); } catch { q.Clear(); }
    return q;
}

Dictionary<string, double> QueueTotals(List<MyProductionItem> q)
{
    Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (q == null) return result;
    for (int i = 0; i < q.Count; i++)
    {
        string key = q[i].BlueprintId.ToString();
        double amount = (double)q[i].Amount;
        double old;
        if (result.TryGetValue(key, out old)) result[key] = old + amount;
        else result[key] = amount;
    }
    return result;
}

double GetQueueBlueprintAmount(List<MyProductionItem> q, string blueprint)
{
    double total = 0;
    if (q == null) return total;
    for (int i = 0; i < q.Count; i++)
        if (q[i].BlueprintId.ToString().Equals(blueprint, StringComparison.OrdinalIgnoreCase))
            total += (double)q[i].Amount;
    return total;
}

double QueueTotal(List<MyProductionItem> q)
{
    double total = 0;
    if (q == null) return total;
    for (int i = 0; i < q.Count; i++) total += (double)q[i].Amount;
    return total;
}

double QueueTotal(IMyProductionBlock pb)
{
    return QueueTotal(SnapshotQueueOrdered(pb));
}

void SetMachineEnabled(IMyProductionBlock pb, bool enabled)
{
    IMyFunctionalBlock fb = pb as IMyFunctionalBlock;
    if (fb != null) fb.Enabled = enabled;
}

void RemoveFirstString(List<string> list, string value)
{
    if (list == null) return;
    for (int i = 0; i < list.Count; i++)
    {
        if (list[i].Equals(value, StringComparison.OrdinalIgnoreCase))
        {
            list.RemoveAt(i);
            return;
        }
    }
}

string BlueprintShortName(string bp)
{
    if (string.IsNullOrWhiteSpace(bp)) return "";
    int slash = bp.LastIndexOf('/');
    return slash >= 0 && slash + 1 < bp.Length ? bp.Substring(slash + 1) : bp;
}

Dictionary<string, double> SnapshotQueueTotals(IMyProductionBlock pb)
{
    Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    List<MyProductionItem> q = new List<MyProductionItem>();
    try { pb.GetQueue(q); } catch { return result; }
    for (int i = 0; i < q.Count; i++)
    {
        string key = q[i].BlueprintId.ToString();
        double amount = (double)q[i].Amount;
        double old;
        if (result.TryGetValue(key, out old)) result[key] = old + amount;
        else result[key] = amount;
    }
    return result;
}

Dictionary<string, double> SnapshotRawInventory(IMyInventory inv)
{
    // v1.0.8 deliberately snapshots RAW MyItemType strings, not known aliases.
    // That is what lets bulk learning discover outputs absent from _items.
    Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (inv == null) return result;
    List<MyInventoryItem> items = new List<MyInventoryItem>();
    inv.GetItems(items);
    for (int i = 0; i < items.Count; i++)
    {
        string itemType = RawItemType(items[i].Type);
        double amount = (double)items[i].Amount;
        double old;
        if (result.TryGetValue(itemType, out old)) result[itemType] = old + amount;
        else result[itemType] = amount;
    }
    return result;
}

Dictionary<string, double> CopyTotals(Dictionary<string, double> src)
{
    Dictionary<string, double> dst = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in src) dst[kv.Key] = kv.Value;
    return dst;
}

double GetRaw(Dictionary<string, double> d, string key)
{
    double v;
    return d != null && d.TryGetValue(key, out v) ? v : 0;
}

void LearnBlueprint(string requested)
{
    string alias = Canon(requested);
    if (!_items.ContainsKey(alias))
    {
        Echo("Unknown item: " + requested);
        return;
    }

    // Learning is allowed even when a built-in blueprint exists. This lets an
    // IO-specific learned mapping override a stale/vanilla bootstrap mapping.
    DiscoverGrid();

    Recipe recipe;
    _recipes.TryGetValue(alias, out recipe);

    List<LearnCandidate> candidates = new List<LearnCandidate>();
    List<MyProductionItem> q = new List<MyProductionItem>();

    for (int i = 0; i < _productionBlocks.Count; i++)
    {
        IMyProductionBlock pb = _productionBlocks[i];
        if (pb == null || pb is IMyRefinery) continue;

        IMyAssembler asm = pb as IMyAssembler;
        if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;

        q.Clear();
        pb.GetQueue(q);
        if (q.Count != 1) continue;

        int rank = 5000;
        if (recipe != null)
            rank = MachineRank(MachineText(pb), recipe, false);

        candidates.Add(new LearnCandidate(pb, q[0].BlueprintId, rank));
    }

    if (candidates.Count == 0)
    {
        Echo("LEARN " + Friendly(alias) + "\n\nQueue ONLY this item on one appropriate machine, leaving that machine with exactly one queue line, then run:\nlearn " + alias);
        return;
    }

    int bestRank = int.MaxValue;
    for (int i = 0; i < candidates.Count; i++)
        if (candidates[i].Rank < bestRank) bestRank = candidates[i].Rank;

    string unique = null;
    IMyProductionBlock uniqueBlock = null;
    bool ambiguous = false;

    for (int i = 0; i < candidates.Count; i++)
    {
        if (candidates[i].Rank != bestRank) continue;
        string bp = candidates[i].Blueprint.ToString();
        if (unique == null)
        {
            unique = bp;
            uniqueBlock = candidates[i].Block;
        }
        else if (!unique.Equals(bp, StringComparison.OrdinalIgnoreCase))
        {
            ambiguous = true;
            break;
        }
    }

    if (ambiguous || unique == null)
    {
        Echo("LEARN AMBIGUOUS for " + Friendly(alias) + "\n\nMore than one different single-line queue exists on equally preferred machines. Leave one appropriate machine with a single queue line for this item, then try again.\n\nNo queues were changed.");
        return;
    }

    if (!PersistLearnedBlueprint(alias, unique))
    {
        Echo("LEARN FAILED TO SAVE\n" + Friendly(alias) + "\n" + unique + "\n\nBlueprint was identified, but Custom Data could not be updated. Queue was NOT changed.");
        return;
    }

    _learnedBlueprints[alias] = unique;
    Echo("LEARNED\n" + Friendly(alias) + "\n" + unique + "\nfrom " + uniqueBlock.CustomName + "\n\nSaved to Custom Data [LearnedBlueprints].\nQueue was NOT changed.");
}

void ForgetLearned(string requested)
{
    string alias = Canon(requested);
    if (!_learnedBlueprints.ContainsKey(alias))
    {
        Echo("No learned blueprint stored for " + requested);
        return;
    }

    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result))
    {
        Echo("Cannot update Custom Data: " + result.ToString());
        return;
    }

    ini.Delete("LearnedBlueprints", alias);
    ini.Delete("LearnedYields", alias);
    Me.CustomData = ini.ToString();
    _learnedBlueprints.Remove(alias);
    _learnedYields.Remove(alias);
    Echo("Forgot learned blueprint for " + Friendly(alias) + "\nCustom Data updated.");
}

void ShowLearned()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION + "\nLEARNED BLUEPRINTS\n");
    if (_learnedBlueprints.Count == 0)
    {
        Echo("(none)\n\nUse: learn <ItemName>");
        return;
    }

    foreach (var kv in _learnedBlueprints)
    {
        double y = Get(_learnedYields, kv.Key);
        string itemType;
        string discoveredType = _learnedItemTypes.TryGetValue(kv.Key, out itemType) ? "\n  item: " + itemType : "";
        Echo(Friendly(kv.Key) + discoveredType + "\n  " + kv.Value + (y > 0 ? "\n  yield: " + F(y) : ""));
    }

    Echo("\nCopyable source: PB Custom Data -> [LearnedItems] / [LearnedBlueprints] / [LearnedYields]");
}

void ExportLearned()
{
    if (!SyncAllLearnedToCustomData())
    {
        Echo("EXPORT FAILED\nCustom Data could not be updated.");
        return;
    }

    Echo("EXPORT READY\n" + _learnedBlueprints.Count + " learned blueprint mapping(s) are in PB Custom Data under:\n\n[LearnedItems]\n[LearnedBlueprints]\n[LearnedYields]\n\nOpen Custom Data and copy those sections.");
}

bool PersistLearnedBlueprint(string alias, string blueprint)
{
    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result)) return false;

    ini.Set("LearnedBlueprints", alias, blueprint);
    Me.CustomData = ini.ToString();
    return true;
}

bool SyncAllLearnedToCustomData()
{
    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result)) return false;

    foreach (var kv in _learnedItemTypes)
        ini.Set("LearnedItems", kv.Key, kv.Value);
    foreach (var kv in _learnedBlueprints)
        ini.Set("LearnedBlueprints", kv.Key, kv.Value);
    foreach (var kv in _learnedYields)
        ini.Set("LearnedYields", kv.Key, kv.Value);

    Me.CustomData = ini.ToString();
    return true;
}

void LoadLegacyStorage()
{
    _legacyStorageBlueprints.Clear();
    if (string.IsNullOrWhiteSpace(Storage)) return;

    string[] lines = Storage.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < lines.Length; i++)
    {
        if (!lines[i].StartsWith("BP|")) continue;
        string[] p = lines[i].Split('|');
        if (p.Length != 3) continue;
        string alias = Canon(p[1]);
        MyDefinitionId parsed;
        if (_items.ContainsKey(alias) && !string.IsNullOrWhiteSpace(p[2]) && MyDefinitionId.TryParse(p[2], out parsed))
            _legacyStorageBlueprints[alias] = p[2];
    }
}

void MigrateLegacyStorageToCustomData()
{
    if (_legacyStorageBlueprints.Count == 0) return;

    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result)) return;

    bool changed = false;
    foreach (var kv in _legacyStorageBlueprints)
    {
        string current = ini.Get("LearnedBlueprints", kv.Key).ToString("").Trim();
        if (current != "") continue;
        ini.Set("LearnedBlueprints", kv.Key, kv.Value);
        changed = true;
    }

    if (changed) Me.CustomData = ini.ToString();

    // Migration is one-way. Custom Data is now the source of truth.
    Storage = "";
    _legacyStorageBlueprints.Clear();
}

void RepairTrustedLearnedMappings()
{
    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result))
    {
        Echo("REPAIR FAILED\nCustom Data parse error: " + result.ToString());
        return;
    }

    int conflicts = 0;
    int redundant = 0;
    foreach (string alias in _trustedBuiltinBlueprintAliases)
    {
        string learned = ini.Get("LearnedBlueprints", alias).ToString("").Trim();
        if (learned == "") continue;

        string trusted;
        if (!_builtinBlueprints.TryGetValue(alias, out trusted)) continue;
        if (learned.Equals(trusted, StringComparison.OrdinalIgnoreCase)) redundant++;
        else conflicts++;

        // Remove the learned copy so the trusted built-in is the source of truth.
        // BlueprintOverrides remains available for an intentional operator override.
        ini.Delete("LearnedBlueprints", alias);
        ini.Delete("LearnedYields", alias);
    }

    Me.CustomData = ini.ToString();
    LoadConfig();
    UpdateRuntimeFrequency();
    Echo("REPAIR COMPLETE\nRemoved " + conflicts + " conflicting and " + redundant +
        " redundant learned mapping(s) for trusted IO items.\nTrusted built-ins are active. [LearnedItems] discoveries were preserved.\nQueues were NOT changed.");
}

bool TryGetBlueprint(string alias, out MyDefinitionId bp)
{
    alias = Canon(alias);
    string text = null;

    // Explicit operator override always wins. For mappings validated in-game and
    // promoted into the trusted built-in library, a stray bulk-learning result
    // is NOT allowed to supersede that known-good blueprint.
    if (_overrideBlueprints.ContainsKey(alias)) text = _overrideBlueprints[alias];
    else if (_trustedBuiltinBlueprintAliases.Contains(alias) && _builtinBlueprints.ContainsKey(alias)) text = _builtinBlueprints[alias];
    else if (_learnedBlueprints.ContainsKey(alias)) text = _learnedBlueprints[alias];
    else if (_builtinBlueprints.ContainsKey(alias)) text = _builtinBlueprints[alias];

    if (string.IsNullOrWhiteSpace(text))
    {
        bp = default(MyDefinitionId);
        return false;
    }

    return MyDefinitionId.TryParse(text, out bp);
}

void ShowStatus()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION);
    Echo("Production: " + (_autoProduction ? "ON" : "OFF"));
    Echo("Stock inventories: " + _stockInventories.Count + " | Production blocks: " + _productionBlocks.Count);
    Echo("Existing queues: PRESERVED + SUPPORTED");
    if (_learnPhase != 0) Echo("Learning session: " + (_learnPhase == 1 ? "CAPTURE" : "OBSERVE"));
    Echo("");

    if (_configErrors.Count > 0)
    {
        Echo("CONFIG PROBLEMS:");
        for (int i = 0; i < _configErrors.Count; i++) Echo("- " + _configErrors[i]);
        Echo("");
    }

    if (_stockTargets.Count == 0)
    {
        Echo("No [Stock] targets configured.");
    }
    else
    {
        foreach (var kv in _stockTargets)
        {
            string a = kv.Key;
            double stock = Get(_onHand, a);
            double queued = Get(_queuedOutput, a);
            double accounted = stock + queued;
            double adding = Get(_plannedOutput, a);
            double need = Math.Max(0, kv.Value - accounted);
            string state = stock >= kv.Value ? "READY" : (accounted >= kv.Value ? "QUEUED" : (adding > 0 ? "ADDING" : "SHORT"));

            Echo(Friendly(a) + "  " + state);
            Echo("  STOCK: " + F(stock));
            if (queued > 0.0001) Echo("  QUEUED: " + F(queued));
            Echo("  ACCOUNTED: " + F(accounted) + " / " + F(kv.Value));
            if (need > 0.0001) Echo("  NEED: " + F(need));
            if (adding > 0.0001) Echo("  PLAN ADD: " + F(adding));
        }
    }

    if (_unknownBlueprints.Count > 0)
    {
        Echo("\nUNKNOWN BLUEPRINTS:");
        foreach (string a in _unknownBlueprints) Echo("- " + Friendly(a) + "  (learn " + a + ")");
    }

    if (_noMachines.Count > 0)
    {
        Echo("\nNO USABLE MACHINE:");
        foreach (string a in _noMachines) Echo("- " + Friendly(a));
    }

    if (_rawShortages.Count > 0)
    {
        Echo("\nRAW / REFINED SHORTAGES:");
        foreach (var kv in _rawShortages) Echo("- " + Friendly(kv.Key) + ": " + F(kv.Value));
    }

    if (_cycleLog.Count > 0)
    {
        Echo("\nLAST APPENDS:");
        int start = Math.Max(0, _cycleLog.Count - 12);
        for (int i = start; i < _cycleLog.Count; i++) Echo(_cycleLog[i]);
    }
}

void ShowPlan()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION + "\nPRODUCTION PLAN\n");

    if (_configErrors.Count > 0)
    {
        Echo("CONFIG PROBLEMS:");
        for (int i = 0; i < _configErrors.Count; i++) Echo("- " + _configErrors[i]);
        Echo("\nNO QUEUES HAVE BEEN CHANGED.");
        return;
    }

    bool any = false;

    if (_queuedJobs.Count > 0)
    {
        Echo("EXISTING QUEUES: preserved + dependency-supported");
        int shown = 0;
        foreach (var kv in _queuedJobs)
        {
            if (!_recipes.ContainsKey(kv.Key) || kv.Value <= 0.0001) continue;
            Echo("- " + Friendly(kv.Key) + ": " + F(kv.Value) + " job" + (Math.Abs(kv.Value - 1) < 0.0001 ? "" : "s"));
            shown++;
            if (shown >= 8) { Echo("- ..."); break; }
        }
        if (shown > 0) Echo("");
    }

    // _planOrder is dependency-first, so the display mirrors the order in
    // which a live run would append work.
    for (int i = 0; i < _planOrder.Count; i++)
    {
        string alias = _planOrder[i];
        int jobs = GetInt(_plannedJobs, alias);
        double output = Get(_plannedOutput, alias);
        if (jobs <= 0 || output <= 0.0001) continue;

        any = true;
        Echo(Friendly(alias) + "  +" + F(output) + "  (" + jobs + " job" + (jobs == 1 ? "" : "s") + ")");

        Recipe recipe;
        MyDefinitionId bp;
        if (_recipes.TryGetValue(alias, out recipe) && TryGetBlueprint(alias, out bp))
        {
            List<IMyProductionBlock> machines = GetBestMachines(alias, bp, recipe);
            if (machines.Count > 0)
            {
                string names = "";
                int limit = Math.Min(machines.Count, 3);
                for (int m = 0; m < limit; m++)
                {
                    if (m > 0) names += ", ";
                    names += machines[m].CustomName;
                }
                if (machines.Count > limit) names += " +" + (machines.Count - limit) + " more";
                Echo("  -> " + names);
            }
        }
    }

    if (!any) Echo("Nothing needs to be appended.");

    if (_unknownBlueprints.Count > 0)
    {
        Echo("\nUNKNOWN BLUEPRINTS:");
        foreach (string a in _unknownBlueprints) Echo("- " + Friendly(a) + "  (learn " + a + ")");
    }

    if (_noMachines.Count > 0)
    {
        Echo("\nNO USABLE MACHINE:");
        foreach (string a in _noMachines) Echo("- " + Friendly(a));
    }

    if (_rawShortages.Count > 0)
    {
        Echo("\nRAW / REFINED SHORTAGES:");
        foreach (var kv in _rawShortages) Echo("- " + Friendly(kv.Key) + ": " + F(kv.Value));
    }

    Echo("\nNO QUEUES HAVE BEEN CHANGED.");
}

void ShowDiagnostics()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION + "\nDIAGNOSTICS\n");

    if (_configErrors.Count > 0)
    {
        Echo("CONFIG:");
        for (int i = 0; i < _configErrors.Count; i++) Echo("- " + _configErrors[i]);
    }
    else Echo("Config: OK");

    Echo("Stock inventories: " + _stockInventories.Count);
    Echo("Production blocks: " + _productionBlocks.Count);
    Echo("Stock targets: " + _stockTargets.Count);

    Echo("\nUnknown blueprints: " + _unknownBlueprints.Count);
    foreach (string a in _unknownBlueprints) Echo("- " + Friendly(a));

    Echo("\nNo usable machine: " + _noMachines.Count);
    foreach (string a in _noMachines) Echo("- " + Friendly(a));

    Echo("\nRaw/refined shortages: " + _rawShortages.Count);
    foreach (var kv in _rawShortages) Echo("- " + Friendly(kv.Key) + " " + F(kv.Value));

    if (_cycles.Count > 0)
    {
        Echo("\nDEPENDENCY CYCLE:");
        foreach (string a in _cycles) Echo("- " + Friendly(a));
    }
}

void ShowMachines()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION + "\nMACHINES\n");

    for (int i = 0; i < _productionBlocks.Count; i++)
    {
        IMyProductionBlock pb = _productionBlocks[i];
        string state = "AUTO";
        string text = MachineText(pb);

        if (pb is IMyRefinery) state = "OBSERVE ONLY (refinery)";
        else if (HasManualTag(pb.CustomName)) state = "EXCLUDED (IOPM opt-out)";
        else
        {
            IMyAssembler asm = pb as IMyAssembler;
            if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) state = "EXCLUDED (disassembly)";
            else if (IsManualBench(text)) state = "EXCLUDED (assembling bench)";
            else if ((text.Contains("survivalkit") || text.Contains("survival kit")) && !_allowSurvivalKitFallback) state = "FALLBACK DISABLED";
            else if (!pb.IsWorking) state = "NOT WORKING";
        }

        Echo(pb.CustomName + "\n  " + state);
    }
}

void ShowHelp()
{
    Echo("IO PRODUCTION MANAGER v" + VERSION + "\n");
    Echo("status           show STOCK / QUEUED / ACCOUNTED / TARGET");
    Echo("plan             dry-run: show every append without changing queues");
    Echo("run              append shortages now");
    Echo("machines         show discovered production blocks");
    Echo("diagnose         show blockers / raw shortages");
    Echo("production on    enable automatic Update100 runs");
    Echo("production off   disable automatic runs");
    Echo("learn <item>     learn one queued blueprint safely");
    Echo("learn start [s]  bulk learn UNKNOWN blueprints (known ones skipped)");
    Echo("learn finish     end capture; begin serialized observation");
    Echo("learn status     show bulk learning progress");
    Echo("learn cancel     restore machines; leave queues untouched");
    Echo("learned          show learned blueprint mappings");
    Echo("export           sync learned mappings/yields to Custom Data");
    Echo("forget <item>    forget a learned mapping/yield");
    Echo("repair           remove conflicting learned copies of trusted IO mappings");
    Echo("reload           reload Custom Data");
    Echo("help             this screen");
    Echo("\nSAFETY: existing queues are never cleared/removed/reordered; their dependencies are supported.");
}

void BuildKnowledgeBase()
{
    // Actual IO / Space Engineers item types. Friendly aliases are added below.
    AddItem("SteelPlate", "Steel Plate", "MyObjectBuilder_Component/SteelPlate");
    AddItem("AluminumPlate", "Aluminum Plate", "MyObjectBuilder_Component/AluminumPlate");
    AddItem("TitaniumPlate", "Titanium Plate", "MyObjectBuilder_Component/TitaniumPlate");
    AddItem("CopperWire", "Copper Wire", "MyObjectBuilder_Component/CopperWire");
    AddItem("GoldWire", "Gold Wire", "MyObjectBuilder_Component/GoldWire");
    AddItem("LargeSteelTube", "Large Steel Tube", "MyObjectBuilder_Component/LargeTube");
    AddItem("SmallSteelTube", "Small Steel Tube", "MyObjectBuilder_Component/SmallTube");
    AddItem("Construction", "Construction Component", "MyObjectBuilder_Component/Construction");
    AddItem("MetalGrid", "Metal Grid", "MyObjectBuilder_Component/MetalGrid");
    AddItem("Electromagnet", "Electromagnet", "MyObjectBuilder_Component/Electromagnet");
    AddItem("Motor", "Motor", "MyObjectBuilder_Component/Motor");
    AddItem("BasicComputer", "Basic Computer", "MyObjectBuilder_Component/Computer");
    AddItem("AdvancedComputer", "Advanced Computer", "MyObjectBuilder_Component/AdvancedComputer");
    AddItem("SensorCluster", "Sensor Cluster", "MyObjectBuilder_Component/SensorCluster");
    AddItem("Display", "Display", "MyObjectBuilder_Component/Display");
    AddItem("Thermocouple", "Thermocouple", "MyObjectBuilder_Component/Thermocouple");
    AddItem("Ceramic", "Ceramic", "MyObjectBuilder_Component/Ceramic");
    AddItem("Glass", "Glass", "MyObjectBuilder_Component/Glass");
    AddItem("HeatingElement", "Heating Element", "MyObjectBuilder_Component/HeatingElement");
    AddItem("Lightbulb", "Lightbulb", "MyObjectBuilder_Component/Lightbulb");
    AddItem("MedicalComponent", "Medical Component", "MyObjectBuilder_Component/Medical");
    AddItem("LithiumPowerCell", "Lithium Power Cell", "MyObjectBuilder_Component/PowerCell");
    AddItem("Plastic", "Plastic", "MyObjectBuilder_Component/Plastic");
    AddItem("Rubber", "Rubber", "MyObjectBuilder_Component/Rubber");

    // Industrial Overhaul uses MyObjectBuilder_Ingot for several refined/intermediate items.
    AddItem("Polymer", "Polymer", "MyObjectBuilder_Ingot/Polymer");
    AddItem("IronIngot", "Iron Ingot", "MyObjectBuilder_Ingot/Iron");
    AddItem("NickelIngot", "Nickel Ingot", "MyObjectBuilder_Ingot/Nickel");
    AddItem("CobaltIngot", "Cobalt Ingot", "MyObjectBuilder_Ingot/Cobalt");
    AddItem("CopperIngot", "Copper Ingot", "MyObjectBuilder_Ingot/Copper");
    AddItem("GoldIngot", "Gold Ingot", "MyObjectBuilder_Ingot/Gold");
    AddItem("AluminumIngot", "Aluminum Ingot", "MyObjectBuilder_Ingot/Aluminum");
    AddItem("TitaniumIngot", "Titanium Ingot", "MyObjectBuilder_Ingot/Titanium");
    AddItem("SilverIngot", "Silver Ingot", "MyObjectBuilder_Ingot/Silver");
    AddItem("SiliconWafer", "Silicon Wafer", "MyObjectBuilder_Ingot/Silicon");
    AddItem("Carbon", "Carbon", "MyObjectBuilder_Ingot/Carbon");
    AddItem("Sulfur", "Sulfur", "MyObjectBuilder_Ingot/Sulfur");
    AddItem("LithiumPaste", "Lithium Paste", "MyObjectBuilder_Ingot/Lithium");

    AddAlias("ConstructionComp", "Construction");
    AddAlias("ConstructionComponent", "Construction");
    AddAlias("LargeTube", "LargeSteelTube");
    AddAlias("SmallTube", "SmallSteelTube");
    AddAlias("Computer", "BasicComputer");
    AddAlias("Medical", "MedicalComponent");
    AddAlias("PowerCell", "LithiumPowerCell");
    AddAlias("SiliconIngot", "SiliconWafer");
    AddAlias("CarbonIngot", "Carbon");
    AddAlias("SulfurIngot", "Sulfur");
    AddAlias("LithiumIngot", "LithiumPaste");
    AddAlias("PolymerIngot", "Polymer");

    // Industrial Overhaul v1.7.7 blueprint IDs validated in-game by the
    // user's bulk-learning session. These intentionally replace several
    // vanilla/GOAT mappings that looked plausible but were wrong for IO.
    AddTrustedBuiltinBlueprint("Electromagnet", "MyObjectBuilder_BlueprintDefinition/Electromagnet");
    AddTrustedBuiltinBlueprint("CopperWire", "MyObjectBuilder_BlueprintDefinition/CopperWire");
    AddTrustedBuiltinBlueprint("Motor", "MyObjectBuilder_BlueprintDefinition/POMotorComponent");
    AddTrustedBuiltinBlueprint("HeatingElement", "MyObjectBuilder_BlueprintDefinition/HeatingElement");
    AddTrustedBuiltinBlueprint("SteelPlate", "MyObjectBuilder_BlueprintDefinition/POSteelPlate");
    AddTrustedBuiltinBlueprint("SmallSteelTube", "MyObjectBuilder_BlueprintDefinition/POSmallTube");
    AddTrustedBuiltinBlueprint("AdvancedComputer", "MyObjectBuilder_BlueprintDefinition/AdvancedComputer");
    AddTrustedBuiltinBlueprint("Plastic", "MyObjectBuilder_BlueprintDefinition/PolymerToPlastic");
    AddTrustedBuiltinBlueprint("Construction", "MyObjectBuilder_BlueprintDefinition/POConstructionComponent");
    AddTrustedBuiltinBlueprint("LargeSteelTube", "MyObjectBuilder_BlueprintDefinition/POLargeTube");
    AddTrustedBuiltinBlueprint("BasicComputer", "MyObjectBuilder_BlueprintDefinition/POComputerComponent");
    AddTrustedBuiltinBlueprint("Rubber", "MyObjectBuilder_BlueprintDefinition/Rubber");
    AddTrustedBuiltinBlueprint("TitaniumPlate", "MyObjectBuilder_BlueprintDefinition/TitaniumPlate");
    AddTrustedBuiltinBlueprint("Ceramic", "MyObjectBuilder_BlueprintDefinition/Ceramic");
    AddTrustedBuiltinBlueprint("Polymer", "MyObjectBuilder_BlueprintDefinition/SyntheticPolymer");
    AddTrustedBuiltinBlueprint("Lightbulb", "MyObjectBuilder_BlueprintDefinition/Lightbulb");
    AddTrustedBuiltinBlueprint("Display", "MyObjectBuilder_BlueprintDefinition/PODisplay");
    AddTrustedBuiltinBlueprint("MedicalComponent", "MyObjectBuilder_BlueprintDefinition/POMedicalComponent");
    AddTrustedBuiltinBlueprint("Thermocouple", "MyObjectBuilder_BlueprintDefinition/Thermocouple");
    AddTrustedBuiltinBlueprint("LithiumPowerCell", "MyObjectBuilder_BlueprintDefinition/POPowerCell");

    // Still unvalidated in IO and therefore intentionally NOT hard-coded:
    // AluminumPlate, GoldWire, MetalGrid, SensorCluster, Glass.
    // Let the learner capture the game's real IDs rather than guessing.

    // IO v1.7.7 production recipes captured from the user's in-game tooltips.
    AddRecipe("SteelPlate", 1, new string[] { "Plate Stamp" },
        I("IronIngot", 20));
    AddRecipe("AluminumPlate", 1, new string[] { "Plate Stamp" },
        I("AluminumIngot", 5));
    AddRecipe("TitaniumPlate", 1, new string[] { "Plate Stamp" },
        I("TitaniumIngot", 12));

    AddRecipe("CopperWire", 1, new string[] { "Wire Drawer" },
        I("CopperIngot", 1));
    AddRecipe("GoldWire", 1, new string[] { "Wire Drawer" },
        I("GoldIngot", 0.6));

    AddRecipe("LargeSteelTube", 1, new string[] { "Extruder" },
        I("IronIngot", 4));
    AddRecipe("SmallSteelTube", 1, new string[] { "Extruder" },
        I("IronIngot", 2));

    AddRecipe("Construction", 1, new string[] { "Fabricator" },
        I("IronIngot", 5));
    AddRecipe("MetalGrid", 1, new string[] { "Fabricator" },
        I("IronIngot", 3), I("NickelIngot", 3), I("CobaltIngot", 3));
    AddRecipe("Electromagnet", 1, new string[] { "Fabricator" },
        I("IronIngot", 1.5), I("NickelIngot", 1), I("CopperWire", 3));
    AddRecipe("HeatingElement", 1, new string[] { "Fabricator" },
        I("NickelIngot", 5), I("CopperIngot", 5));
    AddRecipe("Lightbulb", 10, new string[] { "Fabricator" },
        I("Glass", 1), I("CopperWire", 10));

    AddRecipe("Motor", 1, new string[] { "Assembler" },
        I("Electromagnet", 3), I("LargeSteelTube", 1), I("CopperWire", 3));
    AddRecipe("MedicalComponent", 1, new string[] { "Assembler" },
        I("IronIngot", 12), I("NickelIngot", 8), I("SilverIngot", 8));

    AddRecipe("BasicComputer", 1, new string[] { "Microelectronics Factory", "Fabricator" },
        I("CopperWire", 3), I("SiliconWafer", 2));
    AddRecipe("AdvancedComputer", 1, new string[] { "Microelectronics Factory" },
        I("GoldWire", 3), I("SiliconWafer", 2), I("Plastic", 2));
    AddRecipe("SensorCluster", 1, new string[] { "Microelectronics Factory" },
        I("Glass", 1), I("BasicComputer", 2), I("CopperWire", 3), I("SiliconWafer", 3), I("NickelIngot", 3));
    AddRecipe("Display", 1, new string[] { "Microelectronics Factory" },
        I("CopperWire", 2), I("SiliconWafer", 3), I("Plastic", 2), I("SilverIngot", 0.1), I("BasicComputer", 1), I("NickelIngot", 1));
    AddRecipe("Thermocouple", 1, new string[] { "Microelectronics Factory" },
        I("SiliconWafer", 2), I("CopperWire", 3), I("Plastic", 1), I("AluminumIngot", 2));

    AddRecipe("Ceramic", 1, new string[] { "Ceramics Furnace" },
        I("SiliconWafer", 3), I("Carbon", 2));
    AddRecipe("Glass", 1, new string[] { "Ceramics Furnace" },
        I("SiliconWafer", 3));

    AddRecipe("Polymer", 1, new string[] { "Synthetics Factory" },
        I("SiliconWafer", 1), I("Carbon", 1.5), I("Sulfur", 0.5));
    AddRecipe("Plastic", 1, new string[] { "Synthetics Factory" },
        I("Polymer", 1.5));
    AddRecipe("Rubber", 1, new string[] { "Synthetics Factory" },
        I("Polymer", 2));

    AddRecipe("LithiumPowerCell", 1, new string[] { "Advanced Assembler" },
        I("AluminumPlate", 1), I("CopperWire", 4), I("LithiumPaste", 10), I("Rubber", 2), I("Carbon", 3));
}

void AddTrustedBuiltinBlueprint(string alias, string blueprint)
{
    alias = Canon(alias);
    _builtinBlueprints[alias] = blueprint;
    _trustedBuiltinBlueprintAliases.Add(alias);
}

void CaptureBuiltinItemAliases()
{
    _builtinItemAliases.Clear();
    foreach (var kv in _items) _builtinItemAliases.Add(kv.Key);
}

void RemoveDynamicLearnedItems()
{
    if (_learnedItemTypes.Count == 0) return;

    List<string> removeItems = new List<string>();
    foreach (var kv in _learnedItemTypes)
        if (!_builtinItemAliases.Contains(kv.Key)) removeItems.Add(kv.Key);

    for (int i = 0; i < removeItems.Count; i++)
        _items.Remove(removeItems[i]);

    List<string> removeAliases = new List<string>();
    foreach (var kv in _aliases)
    {
        for (int i = 0; i < removeItems.Count; i++)
        {
            if (kv.Value.Equals(removeItems[i], StringComparison.OrdinalIgnoreCase))
            {
                removeAliases.Add(kv.Key);
                break;
            }
        }
    }
    for (int i = 0; i < removeAliases.Count; i++) _aliases.Remove(removeAliases[i]);

    _learnedItemTypes.Clear();
}

bool RegisterLearnedItemFromConfig(string rawAlias, string rawType)
{
    string alias = (rawAlias ?? "").Trim();
    string itemType = NormalizeItemTypeText(rawType);
    if (alias == "" || !IsValidItemTypeText(itemType))
    {
        _configErrors.Add("Invalid [LearnedItems] entry: " + rawAlias);
        return false;
    }

    string canonical;
    if (_aliases.TryGetValue(alias, out canonical))
    {
        ItemDef existing;
        if (_items.TryGetValue(canonical, out existing) && existing.Type.Equals(itemType, StringComparison.OrdinalIgnoreCase))
            return true;

        _configErrors.Add("[LearnedItems] alias conflicts with existing item: " + alias);
        return false;
    }

    ItemDef direct;
    if (_items.TryGetValue(alias, out direct))
    {
        if (direct.Type.Equals(itemType, StringComparison.OrdinalIgnoreCase)) return true;
        _configErrors.Add("[LearnedItems] alias conflicts with existing item: " + alias);
        return false;
    }

    AddItem(alias, PrettyAlias(alias), itemType);
    _learnedItemTypes[alias] = itemType;
    return true;
}

string ResolveOrDiscoverItemAlias(string rawItemType, out bool discovered)
{
    discovered = false;
    string itemType = NormalizeItemTypeText(rawItemType);
    string known = AliasFromRawType(itemType);
    if (known != null) return known;

    if (!IsValidItemTypeText(itemType)) return null;

    string alias = GenerateDiscoveredAlias(itemType);
    if (string.IsNullOrWhiteSpace(alias)) return null;

    AddItem(alias, PrettyAlias(alias), itemType);
    _learnedItemTypes[alias] = itemType;
    discovered = true;
    return alias;
}

string GenerateDiscoveredAlias(string itemType)
{
    int slash = itemType.IndexOf('/');
    string typeId = slash > 0 ? itemType.Substring(0, slash) : "Item";
    string subtype = slash >= 0 && slash + 1 < itemType.Length ? itemType.Substring(slash + 1) : "Item";
    string baseAlias = SanitizeAlias(subtype);
    if (baseAlias == "") baseAlias = "Item";

    if (AliasNameAvailable(baseAlias)) return baseAlias;

    string category = typeId;
    const string prefix = "MyObjectBuilder_";
    if (category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) category = category.Substring(prefix.Length);
    category = SanitizeAlias(category);
    if (category == "") category = "Item";

    string candidate = baseAlias + "_" + category;
    if (AliasNameAvailable(candidate)) return candidate;

    int n = 2;
    while (!AliasNameAvailable(candidate + n)) n++;
    return candidate + n;
}

bool AliasNameAvailable(string alias)
{
    return !_items.ContainsKey(alias) && !_aliases.ContainsKey(alias);
}

string SanitizeAlias(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return "";
    string result = "";
    for (int i = 0; i < value.Length; i++)
    {
        char c = value[i];
        if (char.IsLetterOrDigit(c) || c == '_') result += c;
        else if (result.Length > 0 && result[result.Length - 1] != '_') result += "_";
    }
    return result.Trim('_');
}

string PrettyAlias(string alias)
{
    // Keep discovered names deterministic and compact. The IO subtype is
    // generally already human-readable enough for PB status output.
    return alias;
}

string NormalizeItemTypeText(string value)
{
    return (value ?? "").Trim();
}

bool IsValidItemTypeText(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    int slash = value.IndexOf('/');
    if (slash <= 0 || slash >= value.Length - 1) return false;
    return value.StartsWith("MyObjectBuilder_", StringComparison.OrdinalIgnoreCase);
}

string RawItemType(MyItemType type)
{
    return type.TypeId + "/" + type.SubtypeId;
}

string AliasFromRawType(string full)
{
    foreach (var kv in _items)
    {
        if (kv.Value.Type.Equals(full, StringComparison.OrdinalIgnoreCase))
            return kv.Key;
    }
    return null;
}

void AddItem(string alias, string friendly, string type)
{
    _items[alias] = new ItemDef(alias, friendly, type);
    _aliases[alias] = alias;

    // Also allow exact subtype as a config alias when it differs from our friendly key.
    int slash = type.IndexOf('/');
    if (slash >= 0 && slash + 1 < type.Length)
    {
        string subtype = type.Substring(slash + 1);
        if (!_aliases.ContainsKey(subtype)) _aliases[subtype] = alias;
    }
}

void AddAlias(string alias, string canonical)
{
    _aliases[alias] = canonical;
}

Ingredient I(string item, double amount)
{
    return new Ingredient(Canon(item), amount);
}

void AddRecipe(string item, double output, string[] preferred, params Ingredient[] inputs)
{
    Recipe r = new Recipe(Canon(item), output);
    for (int i = 0; i < preferred.Length; i++) r.PreferredMachines.Add(preferred[i]);
    for (int i = 0; i < inputs.Length; i++) r.Inputs.Add(inputs[i]);
    _recipes[r.Item] = r;
}

string Canon(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return "";
    value = value.Trim();
    string c;
    if (_aliases.TryGetValue(value, out c)) return c;
    return value;
}

string Friendly(string alias)
{
    alias = Canon(alias);
    ItemDef d;
    if (_items.TryGetValue(alias, out d)) return d.Friendly;
    return alias;
}

string AliasFromType(MyItemType type)
{
    return AliasFromRawType(RawItemType(type));
}

void AddTo(Dictionary<string, double> d, string key, double amount)
{
    key = Canon(key);
    double old;
    if (d.TryGetValue(key, out old)) d[key] = old + amount;
    else d[key] = amount;
}

void AddTo(Dictionary<string, int> d, string key, int amount)
{
    key = Canon(key);
    int old;
    if (d.TryGetValue(key, out old)) d[key] = old + amount;
    else d[key] = amount;
}

double Get(Dictionary<string, double> d, string key)
{
    double v;
    return d.TryGetValue(Canon(key), out v) ? v : 0;
}

int GetInt(Dictionary<string, int> d, string key)
{
    int v;
    return d.TryGetValue(Canon(key), out v) ? v : 0;
}

bool ContainsIgnoreCase(string haystack, string needle)
{
    if (haystack == null || needle == null) return false;
    return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

string F(double v)
{
    if (Math.Abs(v - Math.Round(v)) < 0.0001) return Math.Round(v).ToString("N0");
    if (Math.Abs(v) >= 1000) return v.ToString("N1");
    return v.ToString("0.###");
}

class ItemDef
{
    public string Alias;
    public string Friendly;
    public string Type;
    public ItemDef(string alias, string friendly, string type)
    {
        Alias = alias; Friendly = friendly; Type = type;
    }
}

class Ingredient
{
    public string Item;
    public double Amount;
    public Ingredient(string item, double amount)
    {
        Item = item; Amount = amount;
    }
}

class Recipe
{
    public string Item;
    public double Output;
    public List<Ingredient> Inputs = new List<Ingredient>();
    public List<string> PreferredMachines = new List<string>();
    public Recipe(string item, double output)
    {
        Item = item; Output = output;
    }
}

class MachineChoice
{
    public IMyProductionBlock Block;
    public int Rank;
    public double QueueLoad;
    public MachineChoice(IMyProductionBlock block, int rank, double queueLoad)
    {
        Block = block; Rank = rank; QueueLoad = queueLoad;
    }
}

class BulkLearnMachine
{
    public IMyProductionBlock Block;
    public string OriginalName;
    public bool OriginalEnabled;
    public string Status = "WAITING";
    public Dictionary<string, double> BaselineQueue = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> LastQueue = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> LastOutput = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> TrainingBlueprints = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public List<string> TrainingOrder = new List<string>();
    public int KnownSkipped = 0;
    public DateTime RetryAfterUtc = DateTime.MinValue;
    public string BlockedReason = "";
    public int BlockedAttempts = 0;

    public BulkLearnMachine(IMyProductionBlock block)
    {
        Block = block;
        OriginalName = block != null ? block.CustomName : "";
        IMyFunctionalBlock fb = block as IMyFunctionalBlock;
        OriginalEnabled = fb != null ? fb.Enabled : true;
    }
}

class LearnCandidate
{
    public IMyProductionBlock Block;
    public MyDefinitionId Blueprint;
    public int Rank;
    public LearnCandidate(IMyProductionBlock block, MyDefinitionId blueprint, int rank)
    {
        Block = block; Blueprint = blueprint; Rank = rank;
    }
}
