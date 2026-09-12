// IO Production Manager v2.1.2
// Space Engineers Programmable Block script.
// Warehouse sorting/balancing/cosmetic ordering, inventory accounting,
// production queue accounting, dependency planning, feasibility-gated
// manufactured production, stalled-machine input recovery, status display,
// Custom-Data diagnostics.
//
// ABSOLUTE INVARIANT: the only PRODUCTION-QUEUE mutation call anywhere in
// this file is AddQueueItem(). No ClearQueue, no removal, no reordering of
// the production queue. (Inventory slot reordering below is unrelated —
// TransferItemTo repositions physical item stacks, never touches a queue.)
//
// Custom Data is the control panel. No PB argument required for normal
// operation. Diagnostics live under [IOPM.*] sections.
//
// v2.1.1: process items (e.g. Heat) excluded from warehouse classification
// via PROCESS_ITEM_SUBTYPES — never routed/Overflowed/balanced/reordered/
// counted/warned. Owning block untouched; only the item is excluded.
//
// v2.1.2: diagnostic-only — [IOPM.Organization] counters in
// OrganizeInventories() distinguish detection failure vs. a false API
// result vs. a true-but-no-op result. No algorithm/behavior change.

const string VERSION = "2.1.2";

// ---------------------------------------------------------------------------
// GOAT-compatible category vocabulary
// ---------------------------------------------------------------------------
static readonly string[] CATEGORY_WORDS = new string[]
{
    "Ores", "Ingots", "Components", "Ammo", "Tools", "Consumables", "Misc", "Seeds", "Overflow"
};

// Canonical display/packing order (Overflow is our extension, listed last).
static readonly string[] CATEGORY_ORDER = new string[]
{
    "Ores", "Ingots", "Components", "Ammo", "Tools", "Consumables", "Seeds", "Misc", "Overflow"
};

static readonly string[] IGNORE_TAGS = new string[]
{
    "[IOPM-Ignore]", "[Locked]", "!GSIM-Locked", "[No Sorting]", "!GSIM-NoSorting"
};

static readonly string[] STATUS_TAGS = new string[]
{
    "[StatusScreen]", "!StatusScreen", "!GSIM-StatusScreen"
};

const string OVERFLOW_CATEGORY = "Overflow";

// ---------------------------------------------------------------------------
// Config (Custom Data is the source of truth; re-read on change / interval)
// ---------------------------------------------------------------------------
class Config
{
    public bool GeneralEnabled = true;
    public double UpdateSeconds = 5;

    public bool SortingEnabled = true;
    public bool BalanceEnabled = true;
    public double BalanceTolerancePercent = 5;
    public int MaxTransfersPerCycle = 25;
    public string OverflowPolicy = "Overflow";
    // "Overflow" is a fixed compatibility token, not configurable.

    public bool ProductionEnabled = false;
    public bool AllowSurvivalKitFallback = false;
    public int StallDetectionCycles = 5;
    public bool InputRecoveryEnabled = true;
    public int RecoveryCooldownCycles = 6;

    public Dictionary<string, double> StockTargets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BlueprintOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

MyIni _ini = new MyIni();
Config _cfg = new Config();
List<string> _configErrors = new List<string>();
string _lastCustomDataSeen = null;
double _elapsedSinceCycle = 999;

// ---------------------------------------------------------------------------
// Item / recipe knowledge base (imported/ported from v1.0.11)
// ---------------------------------------------------------------------------
Dictionary<string, ItemDef> _items = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, Recipe> _recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, string> _builtinBlueprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// Broad GOAT-compatible TypeId -> category classification.
Dictionary<string, string> _typeCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// ---------------------------------------------------------------------------
// Discovery results (rebuilt every cycle)
// ---------------------------------------------------------------------------
class ContainerInfo
{
    public IMyTerminalBlock Block;
    public IMyInventory Inventory;
    public HashSet<string> Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool IsSingleCategory { get { return Categories.Count == 1; } }
}

Dictionary<string, List<ContainerInfo>> _categoryPools = new Dictionary<string, List<ContainerInfo>>(StringComparer.OrdinalIgnoreCase);
// Deduped list of every category-tagged container, once each. Inventory
// accounting MUST iterate this, never _categoryPools, or a multi-tag
// container would be counted once per tag.
List<ContainerInfo> _allContainers = new List<ContainerInfo>();
List<IMyInventory> _plainCargoInventories = new List<IMyInventory>();
HashSet<IMyInventory> _ignoredInventories = new HashSet<IMyInventory>();
List<IMyTextSurface> _statusScreens = new List<IMyTextSurface>();
bool _usingFallbackStatusScreen = false;

List<IMyProductionBlock> _managedMachines = new List<IMyProductionBlock>();
List<IMyInventory> _machineOutputInventories = new List<IMyInventory>();
List<IMyInventory> _machineInputInventories = new List<IMyInventory>();
List<IMyInventory> _refineryOutputInventories = new List<IMyInventory>();
// Diagnostic-only: inventory -> owning block name, for every inventory IOPM
// may move items FROM, so a failed transfer can name its actual source.
Dictionary<IMyInventory, string> _inventoryOwnerName = new Dictionary<IMyInventory, string>();

// ---------------------------------------------------------------------------
// Inventory accounting (rebuilt every cycle)
// ---------------------------------------------------------------------------
Dictionary<string, double> _warehouseStock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _overflowStock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _machineOutputStock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _stagedInputStock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _onHand = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); // Warehouse+Overflow+MachineOutput

Dictionary<string, double> _queuedJobs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _queuedOutput = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

// ---------------------------------------------------------------------------
// Planning state (rebuilt every cycle)
// ---------------------------------------------------------------------------
Dictionary<string, double> _demand = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _queueSupportDemand = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _budget = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
// Physical stock already reserved for existing-queue support this cycle;
// a later automatic [Stock] branch can never spend the same material.
Dictionary<string, double> _supportReserved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, double> _plannedOutput = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
Dictionary<string, int> _plannedJobs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _planOrderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
List<string> _planOrder = new List<string>();

class PlanRecord
{
    public string Alias;
    public double Target;
    public double Stock;
    public double Queued;
    public double Need;
    public double Feasible;
    public double Blocked;
    public string BlockedBy = "";
    public string Action = "None";
    public int JobsAdded = 0;
}
Dictionary<string, PlanRecord> _planRecords = new Dictionary<string, PlanRecord>(StringComparer.OrdinalIgnoreCase);

HashSet<string> _unknownBlueprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
HashSet<string> _noMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
List<UnknownRecord> _unknownItems = new List<UnknownRecord>();
class UnknownRecord { public string ItemType; public string Reason; public string SeenIn; }

List<string> _warnings = new List<string>();
List<string> _cycleLog = new List<string>();
double _lastCycleSeconds = 0;

// [IOPM.Organization] diagnostics — reset each time OrganizeInventories() runs.
int _orgExamined, _orgOutOfOrder, _orgAttempts, _orgSucceeded, _orgFailed;
string _orgLastContainer = "", _orgLastItem = "";
int _orgLastSourceIndex = -1, _orgLastTargetIndex = -1;
bool _orgLastResult = false;

// ---------------------------------------------------------------------------
// Stall detection / recovery state (persists across cycles, in-memory only;
// resets on PB recompile, which is an acceptable/known limitation)
// ---------------------------------------------------------------------------
class MachineHealth
{
    public long EntityId;
    public string Name;
    public string LastQueueSignature = "";
    public int StallCycles = 0;
    public int CooldownRemaining = 0;
    public bool Recovering = false;
    public string LastRecoveryResult = "";
    public double LastInputFillPercent = 0;
    public double LastItemsEvacuated = 0;
    public double LastItemsBlocked = 0;
    public bool NotWorking = false; // disabled/unpowered/damaged — evacuation is never a plausible fix for this
}
Dictionary<long, MachineHealth> _machineHealth = new Dictionary<long, MachineHealth>();

// ---------------------------------------------------------------------------
// Entry points
// ---------------------------------------------------------------------------
public Program()
{
    BuildKnowledgeBase();
    BuildTypeCategoryMap();

    if (string.IsNullOrWhiteSpace(Me.CustomData))
        WriteDefaultCustomData();

    LoadConfig();
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
}

public void Save()
{
    // Machine-health tracking is intentionally in-memory only; resets on recompile.
}

public void Main(string argument, UpdateType updateSource)
{
    if ((updateSource & UpdateType.Update10) == 0) return;

    double dt = Runtime.TimeSinceLastRun.TotalSeconds;
    if (dt <= 0) dt = 0.1667;
    _elapsedSinceCycle += dt;

    bool customDataChanged = !string.Equals(_lastCustomDataSeen, Me.CustomData, StringComparison.Ordinal);
    if (customDataChanged) LoadConfig();

    if (_cfg.GeneralEnabled && _elapsedSinceCycle >= Math.Max(1, _cfg.UpdateSeconds))
    {
        DateTime start = DateTime.UtcNow;
        RunCycle();
        _lastCycleSeconds = (DateTime.UtcNow - start).TotalSeconds;
        _elapsedSinceCycle = 0;
    }

    RenderStatusScreens();
}

void RunCycle()
{
    _cycleLog.Clear();
    _warnings.Clear();
    _unknownBlueprints.Clear();
    _noMachines.Clear();
    _unknownItems.Clear();

    // 1. DISCOVER
    Discover();

    // 2. SORT/BALANCE. Recovery gets a reserved budget slice so sorting can't starve it.
    int recoveryReserve = Math.Min(_cfg.MaxTransfersPerCycle, Math.Max(2, _cfg.MaxTransfersPerCycle / 4));
    int sortingBudget = Math.Max(0, _cfg.MaxTransfersPerCycle - recoveryReserve);
    if (_cfg.SortingEnabled)
        sortingBudget = RunSorting(sortingBudget);

    // 3. SCAN INVENTORY
    ScanInventory();

    // 4. SCAN EXISTING QUEUES
    ScanQueues();

    // 5. STALL DETECTION / RECOVERY (may move items; rescan after)
    if (_cfg.ProductionEnabled && _cfg.InputRecoveryEnabled)
    {
        int recoveryBudget = recoveryReserve + sortingBudget; // reserve + sorting's unused leftover
        bool anyRecovery = RunStallRecovery(recoveryBudget);
        if (anyRecovery) ScanInventory();
    }

    // 6-8. CALCULATE DEMAND / BUILD BUDGET / PLAN FEASIBLE PRODUCTION
    if (_cfg.ProductionEnabled)
        BuildPlan();

    // 9. APPEND JOBS ONLY
    if (_cfg.ProductionEnabled)
        ApplyPlan();

    // 10. WRITE DIAGNOSTICS
    WriteDiagnostics();

    // 11. status is rendered every Update10 tick in Main(), not here.
}

// ---------------------------------------------------------------------------
// Config load / defaults
// ---------------------------------------------------------------------------
void LoadConfig()
{
    _configErrors.Clear();
    _lastCustomDataSeen = Me.CustomData;

    MyIniParseResult result;
    if (!_ini.TryParse(Me.CustomData, out result))
    {
        _configErrors.Add("Custom Data parse error: " + result.ToString());
        return;
    }

    Config c = new Config();
    c.GeneralEnabled = _ini.Get("General", "Enabled").ToBoolean(true);
    c.UpdateSeconds = _ini.Get("General", "UpdateSeconds").ToDouble(5);

    c.SortingEnabled = _ini.Get("Sorting", "Enabled").ToBoolean(true);
    c.BalanceEnabled = _ini.Get("Sorting", "Balance").ToBoolean(true);
    c.BalanceTolerancePercent = _ini.Get("Sorting", "BalanceTolerancePercent").ToDouble(5);
    c.MaxTransfersPerCycle = _ini.Get("Sorting", "MaxTransfersPerCycle").ToInt32(25);
    c.OverflowPolicy = _ini.Get("Sorting", "OverflowPolicy").ToString("Overflow");

    // Defaults to false when absent — safe fresh-install default.
    c.ProductionEnabled = _ini.Get("Production", "Enabled").ToBoolean(false);
    c.AllowSurvivalKitFallback = _ini.Get("Production", "AllowSurvivalKitFallback").ToBoolean(false);
    c.StallDetectionCycles = _ini.Get("Production", "StallDetectionCycles").ToInt32(5);
    c.InputRecoveryEnabled = _ini.Get("Production", "InputRecoveryEnabled").ToBoolean(true);
    c.RecoveryCooldownCycles = _ini.Get("Production", "RecoveryCooldownCycles").ToInt32(6);

    List<MyIniKey> keys = new List<MyIniKey>();
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
        c.StockTargets[alias] = value;
    }

    keys.Clear();
    _ini.GetKeys("BlueprintOverrides", keys);
    for (int i = 0; i < keys.Count; i++)
    {
        string alias = Canon(keys[i].Name);
        string bp = _ini.Get("BlueprintOverrides", keys[i].Name).ToString("").Trim();
        if (_items.ContainsKey(alias) && bp != "") c.BlueprintOverrides[alias] = bp;
    }

    _cfg = c;
}

void WriteDefaultCustomData()
{
    MyIni ini = new MyIni();
    ini.Set("General", "Enabled", true);
    ini.Set("General", "UpdateSeconds", 5);

    ini.Set("Sorting", "Enabled", true);
    ini.Set("Sorting", "Balance", true);
    ini.Set("Sorting", "BalanceTolerancePercent", 5);
    ini.Set("Sorting", "MaxTransfersPerCycle", 25);
    ini.Set("Sorting", "OverflowPolicy", "Overflow");

    // Production starts OFF; player must validate warehouse tags first.
    ini.Set("Production", "Enabled", false);
    ini.Set("Production", "AllowSurvivalKitFallback", false);
    ini.Set("Production", "StallDetectionCycles", 5);
    ini.Set("Production", "InputRecoveryEnabled", true);
    ini.Set("Production", "RecoveryCooldownCycles", 6);

    ini.Set("Stock", "SteelPlate", 10000);
    ini.Set("Stock", "Construction", 5000);
    ini.Set("Stock", "Motor", 1000);

    ini.Set("BlueprintOverrides", "ExampleItem", "");

    Me.CustomData = ini.ToString();
}

// ---------------------------------------------------------------------------
// Tag helpers
// ---------------------------------------------------------------------------
bool ContainsIgnoreCase(string haystack, string needle)
{
    if (haystack == null || needle == null) return false;
    return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

bool HasIgnoreTag(string name)
{
    for (int i = 0; i < IGNORE_TAGS.Length; i++)
        if (ContainsIgnoreCase(name, IGNORE_TAGS[i])) return true;
    return false;
}

bool HasStatusTag(string name)
{
    for (int i = 0; i < STATUS_TAGS.Length; i++)
        if (ContainsIgnoreCase(name, STATUS_TAGS[i])) return true;
    return false;
}

List<string> Tokenize(string name)
{
    List<string> tokens = new List<string>();
    if (string.IsNullOrEmpty(name)) return tokens;
    string cur = "";
    for (int i = 0; i < name.Length; i++)
    {
        char c = name[i];
        if (char.IsLetterOrDigit(c)) cur += c;
        else
        {
            if (cur.Length > 0) { tokens.Add(cur); cur = ""; }
        }
    }
    if (cur.Length > 0) tokens.Add(cur);
    return tokens;
}

HashSet<string> ParseCategories(string name)
{
    HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    List<string> tokens = Tokenize(name);
    for (int i = 0; i < tokens.Count; i++)
    {
        for (int w = 0; w < CATEGORY_WORDS.Length; w++)
        {
            if (tokens[i].Equals(CATEGORY_WORDS[w], StringComparison.OrdinalIgnoreCase))
            {
                found.Add(CATEGORY_WORDS[w]);
                break;
            }
        }
    }
    return found;
}

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------
bool IsProtectedFromSorting(IMyTerminalBlock b)
{
    // These block classes are never sorting sources/destinations.
    if (b is IMyShipWelder) return true;
    if (b is IMyShipGrinder) return true;
    if (b is IMyShipDrill) return true;
    if (b is IMyReactor) return true;
    if (b is IMyUserControllableGun) return true;
    if (b is IMyCockpit) return true;
    if (b is IMyShipConnector) return true;
    return false;
}

void Discover()
{
    _categoryPools.Clear();
    _allContainers.Clear();
    _plainCargoInventories.Clear();
    _ignoredInventories.Clear();
    _statusScreens.Clear();
    _usingFallbackStatusScreen = false;
    _managedMachines.Clear();
    _machineOutputInventories.Clear();
    _machineInputInventories.Clear();
    _refineryOutputInventories.Clear();
    _inventoryOwnerName.Clear();

    for (int i = 0; i < CATEGORY_ORDER.Length; i++)
        _categoryPools[CATEGORY_ORDER[i]] = new List<ContainerInfo>();

    List<IMyTerminalBlock> all = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocks(all);

    for (int i = 0; i < all.Count; i++)
    {
        IMyTerminalBlock b = all[i];
        if (b == null || !b.IsSameConstructAs(Me)) continue;

        IMyTextSurfaceProvider tsp = b as IMyTextSurfaceProvider;
        if (tsp != null && tsp.SurfaceCount > 0 && HasStatusTag(b.CustomName))
            _statusScreens.Add(tsp.GetSurface(0));

        if (!b.HasInventory) continue;

        IMyProductionBlock pb = b as IMyProductionBlock;
        if (pb != null)
        {
            ClassifyProductionBlock(pb);
            continue;
        }

        if (IsProtectedFromSorting(b)) continue;

        // Warehouse discovery is restricted to actual cargo containers —
        // O2/H2 generators etc. are never touched even if tagged.
        if (!(b is IMyCargoContainer)) continue;

        IMyInventory inv = b.GetInventory(0);
        if (inv == null) continue;
        _inventoryOwnerName[inv] = b.CustomName;

        if (HasIgnoreTag(b.CustomName))
        {
            _ignoredInventories.Add(inv);
            continue;
        }

        HashSet<string> categories = ParseCategories(b.CustomName);
        if (categories.Count == 0)
        {
            _plainCargoInventories.Add(inv);
            continue;
        }

        // Overflow is exclusive, not an ordinary multi-tag category — a
        // container tagged "Ores Overflow" is treated as Overflow-only.
        if (categories.Contains(OVERFLOW_CATEGORY) && categories.Count > 1)
        {
            List<string> otherTags = new List<string>();
            foreach (string cat in categories)
                if (!cat.Equals(OVERFLOW_CATEGORY, StringComparison.OrdinalIgnoreCase)) otherTags.Add(cat);
            Warn("InvalidContainerTags: " + b.CustomName + " combines Overflow with " + string.Join(",", otherTags) + " — treated as Overflow only");
            categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OVERFLOW_CATEGORY };
        }

        ContainerInfo ci = new ContainerInfo { Block = b, Inventory = inv, Categories = categories };
        _allContainers.Add(ci); // exactly once per physical container
        foreach (string cat in categories)
        {
            List<ContainerInfo> pool;
            if (!_categoryPools.TryGetValue(cat, out pool))
            {
                pool = new List<ContainerInfo>();
                _categoryPools[cat] = pool;
            }
            pool.Add(ci); // same ContainerInfo instance referenced by every tag's pool
        }
    }

    if (_statusScreens.Count == 0)
    {
        _statusScreens.Add(Me.GetSurface(0));
        _usingFallbackStatusScreen = true;
    }
}

void ClassifyProductionBlock(IMyProductionBlock pb)
{
    IMyInventory outInv = pb.OutputInventory;
    IMyInventory inInv = pb.InputInventory;

    if (pb is IMyRefinery)
    {
        // Refinery output may be evacuated as ordinary sorting; queue/input never touched.
        if (outInv != null) { _refineryOutputInventories.Add(outInv); _inventoryOwnerName[outInv] = pb.CustomName; }
        return;
    }

    if (HasIgnoreTag(pb.CustomName))
    {
        // [IOPM-Ignore]: fully invisible to IOPM.
        return;
    }

    if (outInv != null) { _machineOutputInventories.Add(outInv); _inventoryOwnerName[outInv] = pb.CustomName; } // always a sorting source

    IMyAssembler asm = pb as IMyAssembler;
    bool disassembling = asm != null && asm.Mode == MyAssemblerMode.Disassembly;
    if (disassembling)
    {
        // Disassembly is manual player work: excluded from queue-support
        // scanning, production, stall detection, and input evacuation.
        return;
    }

    _managedMachines.Add(pb);
    if (inInv != null) { _machineInputInventories.Add(inInv); _inventoryOwnerName[inInv] = pb.CustomName; }
}

// ---------------------------------------------------------------------------
// Item classification (category) — GOAT-compatible broad TypeId mapping
// ---------------------------------------------------------------------------
void BuildTypeCategoryMap()
{
    _typeCategory.Clear();
    _typeCategory["MyObjectBuilder_Ore"] = "Ores";
    _typeCategory["MyObjectBuilder_Ingot"] = "Ingots";
    _typeCategory["MyObjectBuilder_Component"] = "Components";
    _typeCategory["MyObjectBuilder_AmmoMagazine"] = "Ammo";
    _typeCategory["MyObjectBuilder_OxygenContainerObject"] = "Tools";
    _typeCategory["MyObjectBuilder_GasContainerObject"] = "Tools";
    _typeCategory["MyObjectBuilder_PhysicalGunObject"] = "Tools";
    _typeCategory["MyObjectBuilder_PhysicalObject"] = "Tools";
    _typeCategory["MyObjectBuilder_ConsumableItem"] = "Consumables";
    _typeCategory["MyObjectBuilder_Datapad"] = "Misc";
    _typeCategory["MyObjectBuilder_SeedItem"] = "Seeds";
    _typeCategory["MyObjectBuilder_Package"] = "Misc";
}

// Non-warehouse/process resources: SubtypeIds that must stay entirely
// outside IOPM's warehouse system (no routing, no Overflow, no balancing,
// no cosmetic ordering, no warnings) regardless of their generic TypeId.
// Extend this list as more IO process items are identified.
static readonly string[] PROCESS_ITEM_SUBTYPES = new string[] { "Heat" };

bool IsProcessItem(MyItemType type)
{
    for (int i = 0; i < PROCESS_ITEM_SUBTYPES.Length; i++)
        if (string.Equals(type.SubtypeId, PROCESS_ITEM_SUBTYPES[i], StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

// Classifies an item's category, first excluding process items (returns
// null via isProcessItem=true) before falling back to the generic TypeId
// map — so a process resource never gets treated as "unknown" either.
string ClassifyItem(MyItemType type, out bool isProcessItem)
{
    isProcessItem = IsProcessItem(type);
    if (isProcessItem) return null;
    return CategoryForType(type.TypeId);
}

string CategoryForType(string typeIdWithPrefix)
{
    string cat;
    if (_typeCategory.TryGetValue(typeIdWithPrefix, out cat)) return cat;
    return null;
}

string RawItemType(MyItemType type)
{
    return type.TypeId + "/" + type.SubtypeId;
}

// Diagnostic-only helpers for identifying a failed transfer's real source.
string OwnerName(IMyInventory inv)
{
    string name;
    return inv != null && _inventoryOwnerName.TryGetValue(inv, out name) ? name : "Unknown";
}

string ItemDisplayName(MyItemType type)
{
    string alias = AliasFromType(type);
    return alias != null ? Friendly(alias) : type.SubtypeId.ToString();
}

string AliasFromRawType(string full)
{
    foreach (var kv in _items)
        if (kv.Value.Type.Equals(full, StringComparison.OrdinalIgnoreCase)) return kv.Key;
    return null;
}

string AliasFromType(MyItemType type)
{
    return AliasFromRawType(RawItemType(type));
}

// ---------------------------------------------------------------------------
// Sorting / balancing / overflow engine
// ---------------------------------------------------------------------------
List<ContainerInfo> PoolFor(string category)
{
    List<ContainerInfo> pool;
    if (_categoryPools.TryGetValue(category, out pool)) return pool;
    return new List<ContainerInfo>();
}

double FreeVolume(IMyInventory inv)
{
    double free = (double)(inv.MaxVolume - inv.CurrentVolume);
    return free > 0 ? free : 0;
}

// TransferItemTo's clamping to available destination space isn't a
// documented contract, so CanItemsBeAdded (a documented capacity check) is
// binary-searched for the largest amount confirmed to fit before requesting
// it. hadRoom distinguishes "genuinely full" (try next candidate silently)
// from "capacity said yes but transfer still failed" (real unreachable
// destination, worth reporting by name).
bool TryTransferItem(IMyInventory src, IMyInventory dest, MyInventoryItem item, MyFixedPoint requested, out MyFixedPoint moved, out bool hadRoom)
{
    moved = (MyFixedPoint)0;
    hadRoom = false;
    if (requested <= 0) return false;
    if (ReferenceEquals(src, dest)) return false; // never transfer an inventory to itself

    MyFixedPoint safe = requested;
    bool fullyFits = false;
    try { fullyFits = dest.CanItemsBeAdded(requested, item.Type); } catch { fullyFits = false; }

    if (!fullyFits)
    {
        double lo = 0, hi = (double)requested;
        for (int iter = 0; iter < 30 && hi - lo > 0.0005; iter++)
        {
            double mid = (lo + hi) / 2.0;
            bool canFit = false;
            try { canFit = dest.CanItemsBeAdded((MyFixedPoint)mid, item.Type); } catch { canFit = false; }
            if (canFit) lo = mid; else hi = mid;
        }
        safe = (MyFixedPoint)lo;
    }

    if (safe <= 0) return false;
    hadRoom = true;

    bool ok = false;
    try { ok = src.TransferItemTo(dest, item, safe); } catch { ok = false; }
    if (ok) moved = safe;
    return ok;
}

// Ranked destinations for a category: single-category containers first
// (most free volume first), then multi-category. Full ranked list lets
// callers fail over to the next candidate if the top pick is unreachable.
List<ContainerInfo> BestDestinationCandidates(string category, out bool anyCandidateExists)
{
    anyCandidateExists = false;
    List<ContainerInfo> pool = PoolFor(category);
    List<ContainerInfo> singles = new List<ContainerInfo>();
    List<ContainerInfo> multis = new List<ContainerInfo>();

    for (int i = 0; i < pool.Count; i++)
    {
        ContainerInfo ci = pool[i];
        if (_ignoredInventories.Contains(ci.Inventory)) continue;
        anyCandidateExists = true;
        if (FreeVolume(ci.Inventory) <= 0.0001) continue;
        if (ci.IsSingleCategory) singles.Add(ci); else multis.Add(ci);
    }

    singles.Sort((a, b) => FreeVolume(b.Inventory).CompareTo(FreeVolume(a.Inventory)));
    multis.Sort((a, b) => FreeVolume(b.Inventory).CompareTo(FreeVolume(a.Inventory)));

    List<ContainerInfo> result = new List<ContainerInfo>(singles.Count + multis.Count);
    result.AddRange(singles);
    result.AddRange(multis);
    return result;
}

// Tries every ranked candidate for `category` until one accepts the item.
// categoryExists/categoryHasRoom reported separately (never merged with
// Overflow's own state) so callers distinguish "no container" from
// "exists but full/unreachable" precisely.
bool TryRouteToCategory(IMyInventory src, MyInventoryItem item, string category, out MyFixedPoint moved, out ContainerInfo usedDest, out bool categoryExists, out bool categoryHasRoom)
{
    moved = (MyFixedPoint)0;
    usedDest = null;
    List<ContainerInfo> candidates = BestDestinationCandidates(category, out categoryExists);
    categoryHasRoom = candidates.Count > 0;

    for (int i = 0; i < candidates.Count; i++)
    {
        ContainerInfo dest = candidates[i];
        if (ReferenceEquals(dest.Inventory, src)) continue; // never route Overflow back into itself

        MyFixedPoint m; bool hadRoom;
        bool ok = TryTransferItem(src, dest.Inventory, item, item.Amount, out m, out hadRoom);
        if (ok && m > 0) { moved = m; usedDest = dest; return true; }
        if (hadRoom)
            Warn("Transfer failed: " + ItemDisplayName(item.Type) + " | From=" + OwnerName(src) +
                " | To=" + dest.Block.CustomName + " | Category=" + category + " — trying next container");
    }
    return false;
}

// Full routing decision: correct category first, Overflow second, else
// leave in place. Reports precise, non-overwriting diagnostics.
bool RouteItem(IMyInventory src, MyInventoryItem item, string category, out MyFixedPoint moved)
{
    bool categoryExists, categoryHasRoom;
    ContainerInfo usedDest;
    if (TryRouteToCategory(src, item, category, out moved, out usedDest, out categoryExists, out categoryHasRoom))
        return true;

    bool overflowExists = false, overflowHasRoom = false, overflowOk = false;
    if (string.Equals(_cfg.OverflowPolicy, "Overflow", StringComparison.OrdinalIgnoreCase) &&
        !category.Equals(OVERFLOW_CATEGORY, StringComparison.OrdinalIgnoreCase))
    {
        ContainerInfo overflowDest;
        overflowOk = TryRouteToCategory(src, item, OVERFLOW_CATEGORY, out moved, out overflowDest, out overflowExists, out overflowHasRoom);
    }

    if (overflowOk)
    {
        Warn(!categoryExists ? "No " + category + " container — routed to Overflow" : category + " pool full/unreachable — using Overflow");
        return true;
    }

    if (!categoryExists) Warn("No " + category + " container");
    else if (!overflowExists) Warn(category + " pool full/unreachable, and no Overflow container — item left in place");
    else Warn(category + " pool full/unreachable, and Overflow full/unreachable — item left in place");
    return false;
}

int RunSorting(int transferBudget)
{
    if (transferBudget <= 0) return 0;

    // Step A: route misplaced items out of plain cargo/category containers/outputs.
    transferBudget = RouteSources(_plainCargoInventories, transferBudget, null);

    // Built from deduped _allContainers so each physical container is
    // processed once. Overflow excluded — DrainOverflow() is the only
    // normal path that removes items from Overflow.
    List<IMyInventory> allCategoryInvs = new List<IMyInventory>();
    for (int i = 0; i < _allContainers.Count; i++)
    {
        ContainerInfo ci = _allContainers[i];
        if (_ignoredInventories.Contains(ci.Inventory)) continue;
        if (ci.Categories.Contains(OVERFLOW_CATEGORY)) continue;
        allCategoryInvs.Add(ci.Inventory);
    }
    transferBudget = RouteSources(allCategoryInvs, transferBudget, LookupContainerCategories);

    transferBudget = RouteSources(_machineOutputInventories, transferBudget, null);
    transferBudget = RouteSources(_refineryOutputInventories, transferBudget, null);

    // Step B: balance within each category pool.
    if (_cfg.BalanceEnabled && transferBudget > 0)
        transferBudget = BalancePools(transferBudget);

    // Step C: drain Overflow back to proper category once room exists.
    if (transferBudget > 0)
        transferBudget = DrainOverflow(transferBudget);

    // Step D: cosmetic slot ordering — lowest priority, only runs on
    // whatever budget routing/balancing/draining left over, never its own
    // reserved slice, so it can never delay or crowd out safety work.
    if (transferBudget > 0)
        transferBudget = OrganizeInventories(transferBudget);

    return transferBudget;
}

// Deterministic physical stack ordering (spec v2.1.0): single-category
// containers sort alphabetically by friendly item name; multi-category
// containers sort by canonical category order first, then alphabetically
// within each category. Overflow is exempt. One corrective move per
// container per cycle — after a move, slot indexes are stale, so this
// container is left for the next cycle rather than continuing against them.
int OrganizeInventories(int transferBudget)
{
    _orgExamined = 0; _orgOutOfOrder = 0; _orgAttempts = 0; _orgSucceeded = 0; _orgFailed = 0;

    for (int c = 0; c < _allContainers.Count && transferBudget > 0; c++)
    {
        ContainerInfo ci = _allContainers[c];
        if (_ignoredInventories.Contains(ci.Inventory)) continue;
        if (ci.Categories.Contains(OVERFLOW_CATEGORY)) continue; // Overflow: no cosmetic ordering

        List<MyInventoryItem> items = new List<MyInventoryItem>();
        ci.Inventory.GetItems(items);
        if (items.Count < 2) continue;

        bool hasProcessItem = false;
        for (int k = 0; k < items.Count; k++)
            if (IsProcessItem(items[k].Type)) { hasProcessItem = true; break; }
        if (hasProcessItem) continue; // process resource present — leave this container's slots untouched

        _orgExamined++;
        bool multi = !ci.IsSingleCategory;
        List<int> order = new List<int>();
        for (int k = 0; k < items.Count; k++) order.Add(k);
        order.Sort((a, b) =>
        {
            int catA = multi ? CategoryRank(items[a].Type.TypeId) : 0;
            int catB = multi ? CategoryRank(items[b].Type.TypeId) : 0;
            if (catA != catB) return catA.CompareTo(catB);
            int nameCmp = string.Compare(ItemDisplayName(items[a].Type), ItemDisplayName(items[b].Type), StringComparison.OrdinalIgnoreCase);
            if (nameCmp != 0) return nameCmp;
            return a.CompareTo(b); // stable: duplicates keep their relative order
        });

        int mismatchAt = -1;
        for (int k = 0; k < order.Count; k++)
            if (order[k] != k) { mismatchAt = k; break; }
        if (mismatchAt < 0) continue; // already correctly ordered
        _orgOutOfOrder++;

        int sourceIndex = order[mismatchAt];
        bool ok = false;
        try { ok = ci.Inventory.TransferItemTo(ci.Inventory, sourceIndex, mismatchAt, false, items[sourceIndex].Amount); }
        catch { ok = false; }

        _orgAttempts++;
        if (ok) _orgSucceeded++; else _orgFailed++;
        _orgLastContainer = ci.Block.CustomName;
        _orgLastItem = ItemDisplayName(items[sourceIndex].Type);
        _orgLastSourceIndex = sourceIndex;
        _orgLastTargetIndex = mismatchAt;
        _orgLastResult = ok;

        if (ok)
        {
            transferBudget--;
            _cycleLog.Add("ORGANIZE " + ItemDisplayName(items[sourceIndex].Type) + " in " + ci.Block.CustomName);
        }
        // Whether it succeeded or not, indexes are now stale for this
        // container — move on rather than re-reading mid-loop this cycle.
    }
    return transferBudget;
}

int CategoryRank(string typeIdWithPrefix)
{
    string cat = CategoryForType(typeIdWithPrefix);
    if (cat == null) return CATEGORY_ORDER.Length;
    int idx = Array.IndexOf(CATEGORY_ORDER, cat);
    return idx < 0 ? CATEGORY_ORDER.Length : idx;
}

HashSet<string> LookupContainerCategories(IMyInventory inv)
{
    foreach (var kv in _categoryPools)
        for (int i = 0; i < kv.Value.Count; i++)
            if (kv.Value[i].Inventory == inv) return kv.Value[i].Categories;
    return null;
}

// ownCategoriesLookup: null for non-category sources; for category
// containers, lets items already in one of their own categories be left alone.
int RouteSources(List<IMyInventory> sources, int transferBudget, Func<IMyInventory, HashSet<string>> ownCategoriesLookup)
{
    for (int s = 0; s < sources.Count && transferBudget > 0; s++)
    {
        IMyInventory src = sources[s];
        if (src == null || _ignoredInventories.Contains(src)) continue;

        HashSet<string> ownCats = ownCategoriesLookup != null ? ownCategoriesLookup(src) : null;

        bool movedAny = true;
        while (movedAny && transferBudget > 0)
        {
            movedAny = false;
            List<MyInventoryItem> items = new List<MyInventoryItem>();
            src.GetItems(items);

            for (int i = 0; i < items.Count; i++)
            {
                string typeKey = RawItemType(items[i].Type);
                bool isProcessItem;
                string category = ClassifyItem(items[i].Type, out isProcessItem);
                string alias = AliasFromRawType(typeKey);

                if (isProcessItem) continue; // IO process resource — stays under IO's control, never warehouse stock

                if (category == null)
                {
                    RecordUnknownItem(typeKey, "UnknownItemType", src);
                    continue;
                }

                if (ownCats != null && ownCats.Contains(category)) continue; // already correctly placed

                MyFixedPoint moved;
                bool ok = RouteItem(src, items[i], category, out moved);

                if (ok)
                {
                    transferBudget--;
                    movedAny = true;
                    string qualifier = moved < items[i].Amount ? " (partial, remainder stays/routes next pass)" : "";
                    _cycleLog.Add("SORT " + (alias ?? typeKey) + qualifier);
                    break; // items list is now stale; refetch
                }
            }
        }
    }
    return transferBudget;
}

// Containers balance only against others with the exact same category set
// (Ores<->Ores, {Ores,Ingots}<->{Ores,Ingots}, never mixed) so a multi-tag
// container's capacity is never counted into two pools at once.
string GroupKey(HashSet<string> categories)
{
    List<string> sorted = new List<string>(categories);
    sorted.Sort(StringComparer.OrdinalIgnoreCase);
    return string.Join("+", sorted);
}

int BalancePools(int transferBudget)
{
    Dictionary<string, List<ContainerInfo>> groups = new Dictionary<string, List<ContainerInfo>>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < _allContainers.Count; i++)
    {
        ContainerInfo ci = _allContainers[i];
        if (_ignoredInventories.Contains(ci.Inventory)) continue;
        string key = GroupKey(ci.Categories);
        List<ContainerInfo> list;
        if (!groups.TryGetValue(key, out list)) { list = new List<ContainerInfo>(); groups[key] = list; }
        list.Add(ci);
    }

    List<string> groupKeys = new List<string>(groups.Keys);
    groupKeys.Sort(StringComparer.OrdinalIgnoreCase); // deterministic pass order

    for (int g = 0; g < groupKeys.Count && transferBudget > 0; g++)
    {
        List<ContainerInfo> eligible = groups[groupKeys[g]];
        if (eligible.Count < 2) continue;

        HashSet<string> groupCategories = eligible[0].Categories; // identical across the group by construction

        double totalCapacity = 0;
        for (int i = 0; i < eligible.Count; i++) totalCapacity += (double)eligible[i].Inventory.MaxVolume;
        if (totalCapacity <= 0) continue;

        // Only items whose category matches the group are aggregated; a
        // stray misplaced item is left for the routing pass, never balanced.
        Dictionary<string, double> perItemTotal = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Dictionary<int, double>> perItemPerContainer = new Dictionary<string, Dictionary<int, double>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < eligible.Count; i++)
        {
            List<MyInventoryItem> items = new List<MyInventoryItem>();
            eligible[i].Inventory.GetItems(items);
            for (int k = 0; k < items.Count; k++)
            {
                if (IsProcessItem(items[k].Type)) continue; // IO process resource — never balanced
                string itemCategory = CategoryForType(items[k].Type.TypeId);
                if (itemCategory == null || !groupCategories.Contains(itemCategory)) continue;

                string key = RawItemType(items[k].Type);
                double amt = (double)items[k].Amount;
                double old;
                perItemTotal[key] = (perItemTotal.TryGetValue(key, out old) ? old : 0) + amt;

                Dictionary<int, double> perContainer;
                if (!perItemPerContainer.TryGetValue(key, out perContainer))
                {
                    perContainer = new Dictionary<int, double>();
                    perItemPerContainer[key] = perContainer;
                }
                double oldc;
                perContainer[i] = (perContainer.TryGetValue(i, out oldc) ? oldc : 0) + amt;
            }
        }

        foreach (var itemKv in perItemTotal)
        {
            if (transferBudget <= 0) break;
            string typeKey = itemKv.Key;
            double total = itemKv.Value;
            if (total <= 0.0001) continue;

            Dictionary<int, double> perContainer = perItemPerContainer[typeKey];

            int overIdx = -1; double overExcess = 0;
            // Every under-target container is a candidate, not just the
            // most-deficient one, so a failed pick can fail over to the next.
            List<KeyValuePair<int, double>> underCandidates = new List<KeyValuePair<int, double>>();

            for (int i = 0; i < eligible.Count; i++)
            {
                double have;
                perContainer.TryGetValue(i, out have);
                double targetShare = total * ((double)eligible[i].Inventory.MaxVolume / totalCapacity);
                double tolerance = targetShare * (_cfg.BalanceTolerancePercent / 100.0);
                double diff = have - targetShare;

                if (diff > tolerance && diff > overExcess) { overExcess = diff; overIdx = i; }
                if (-diff > tolerance) underCandidates.Add(new KeyValuePair<int, double>(i, -diff));
            }

            if (overIdx < 0 || underCandidates.Count == 0) continue;
            underCandidates.RemoveAll(kv => kv.Key == overIdx);
            if (underCandidates.Count == 0) continue;
            underCandidates.Sort((a, b) => b.Value.CompareTo(a.Value)); // most-deficient first

            List<MyInventoryItem> srcItems = new List<MyInventoryItem>();
            eligible[overIdx].Inventory.GetItems(srcItems);
            int srcItemIdx = -1;
            for (int k = 0; k < srcItems.Count; k++)
                if (RawItemType(srcItems[k].Type).Equals(typeKey, StringComparison.OrdinalIgnoreCase)) { srcItemIdx = k; break; }
            if (srcItemIdx < 0) continue;

            for (int u = 0; u < underCandidates.Count; u++)
            {
                int underIdx = underCandidates[u].Key;
                double underDeficit = underCandidates[u].Value;
                double moveAmount = Math.Min(overExcess, underDeficit);
                if (moveAmount <= 0.0001) continue;

                MyFixedPoint amt = (MyFixedPoint)Math.Min((double)srcItems[srcItemIdx].Amount, moveAmount);
                MyFixedPoint moved; bool hadRoom;
                bool ok = TryTransferItem(eligible[overIdx].Inventory, eligible[underIdx].Inventory, srcItems[srcItemIdx], amt, out moved, out hadRoom);
                if (ok)
                {
                    transferBudget--;
                    _cycleLog.Add("BALANCE " + typeKey + " " + eligible[overIdx].Block.CustomName + " -> " + eligible[underIdx].Block.CustomName);
                    break;
                }
                if (hadRoom)
                    Warn("Transfer failed: " + ItemDisplayName(srcItems[srcItemIdx].Type) + " | From=" + eligible[overIdx].Block.CustomName +
                        " | To=" + eligible[underIdx].Block.CustomName + " | Category=" + CategoryForType(srcItems[srcItemIdx].Type.TypeId) + " — trying next container");
            }
        }
    }
    return transferBudget;
}

int DrainOverflow(int transferBudget)
{
    List<ContainerInfo> overflowPool = PoolFor(OVERFLOW_CATEGORY);
    for (int i = 0; i < overflowPool.Count && transferBudget > 0; i++)
    {
        ContainerInfo of = overflowPool[i];
        if (_ignoredInventories.Contains(of.Inventory)) continue;

        List<MyInventoryItem> items = new List<MyInventoryItem>();
        of.Inventory.GetItems(items);
        for (int k = 0; k < items.Count && transferBudget > 0; k++)
        {
            if (IsProcessItem(items[k].Type)) continue; // IO process resource — never drained/routed
            string category = CategoryForType(items[k].Type.TypeId);
            if (category == null || category.Equals(OVERFLOW_CATEGORY, StringComparison.OrdinalIgnoreCase)) continue;

            MyFixedPoint moved; ContainerInfo dest; bool exists, hasRoom;
            bool ok = TryRouteToCategory(of.Inventory, items[k], category, out moved, out dest, out exists, out hasRoom);
            if (ok)
            {
                transferBudget--;
                _cycleLog.Add("DRAIN-OVERFLOW " + RawItemType(items[k].Type) + " -> " + dest.Block.CustomName);
                break; // list stale, restart this container next outer pass
            }
        }
    }
    return transferBudget;
}

void RecordUnknownItem(string typeKey, string reason, IMyInventory seenIn)
{
    for (int i = 0; i < _unknownItems.Count; i++)
        if (_unknownItems[i].ItemType == typeKey) return; // dedupe per cycle
    string seenName = "unknown";
    // Best-effort: we don't retain a reverse inv->block map; leave generic.
    _unknownItems.Add(new UnknownRecord { ItemType = typeKey, Reason = reason, SeenIn = seenName });
}

void Warn(string message)
{
    if (!_warnings.Contains(message)) _warnings.Add(message);
}

// ---------------------------------------------------------------------------
// Inventory accounting (spec #11)
// ---------------------------------------------------------------------------
void CountInventory(IMyInventory inv, Dictionary<string, double> totals)
{
    if (inv == null) return;
    List<MyInventoryItem> items = new List<MyInventoryItem>();
    inv.GetItems(items);
    for (int i = 0; i < items.Count; i++)
    {
        string alias = AliasFromType(items[i].Type);
        if (alias == null) continue; // unknown items are reported during sorting, not accounting
        AddTo(totals, alias, (double)items[i].Amount);
    }
}

void ScanInventory()
{
    _warehouseStock.Clear();
    _overflowStock.Clear();
    _machineOutputStock.Clear();
    _stagedInputStock.Clear();
    _onHand.Clear();

    // Iterate the DEDUPED container list once per physical inventory —
    // counting via _categoryPools directly would credit multi-tag contents twice.
    for (int i = 0; i < _allContainers.Count; i++)
    {
        ContainerInfo ci = _allContainers[i];
        if (_ignoredInventories.Contains(ci.Inventory)) continue;
        bool isOverflow = ci.Categories.Contains(OVERFLOW_CATEGORY);
        CountInventory(ci.Inventory, isOverflow ? _overflowStock : _warehouseStock);
    }
    for (int i = 0; i < _plainCargoInventories.Count; i++)
        CountInventory(_plainCargoInventories[i], _warehouseStock);

    for (int i = 0; i < _machineOutputInventories.Count; i++)
        CountInventory(_machineOutputInventories[i], _machineOutputStock);

    for (int i = 0; i < _machineInputInventories.Count; i++)
        CountInventory(_machineInputInventories[i], _stagedInputStock);

    // Warehouse + Overflow + Machine Output count as finished inventory. Staged inputs never satisfy [Stock].
    foreach (var kv in _warehouseStock) AddTo(_onHand, kv.Key, kv.Value);
    foreach (var kv in _overflowStock) AddTo(_onHand, kv.Key, kv.Value);
    foreach (var kv in _machineOutputStock) AddTo(_onHand, kv.Key, kv.Value);
}

// ---------------------------------------------------------------------------
// Queue scan (read-only)
// ---------------------------------------------------------------------------
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

void ScanQueues()
{
    _queuedJobs.Clear();
    _queuedOutput.Clear();

    Dictionary<string, string> reverse = BuildBlueprintReverseMap();
    List<MyProductionItem> queue = new List<MyProductionItem>();

    for (int i = 0; i < _managedMachines.Count; i++)
    {
        IMyProductionBlock pb = _managedMachines[i];
        IMyAssembler asm = pb as IMyAssembler;
        if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;

        queue.Clear();
        pb.GetQueue(queue); // READ ONLY — never mutated here

        for (int q = 0; q < queue.Count; q++)
        {
            string key = queue[q].BlueprintId.ToString();
            string alias;
            if (!reverse.TryGetValue(key, out alias)) continue;

            double jobs = (double)queue[q].Amount;
            double yield = 1.0;
            Recipe r;
            if (_recipes.TryGetValue(alias, out r)) yield = r.Output;

            AddTo(_queuedJobs, alias, jobs);
            AddTo(_queuedOutput, alias, jobs * yield);
        }
    }
}

// ---------------------------------------------------------------------------
// Stall detection / recovery (spec #19) — never touches the queue itself.
// ---------------------------------------------------------------------------
string QueueSignature(List<MyProductionItem> q)
{
    if (q.Count == 0) return "";
    return q[0].BlueprintId.ToString() + "|" + ((double)q[0].Amount).ToString("0.####");
}

bool RunStallRecovery(int transferBudget)
{
    bool anyRecovery = false;
    List<MyProductionItem> q = new List<MyProductionItem>();

    for (int i = 0; i < _managedMachines.Count; i++)
    {
        IMyProductionBlock pb = _managedMachines[i];
        long id = pb.EntityId;
        MachineHealth h;
        if (!_machineHealth.TryGetValue(id, out h))
        {
            h = new MachineHealth { EntityId = id, Name = pb.CustomName };
            _machineHealth[id] = h;
        }
        h.Name = pb.CustomName;

        if (h.CooldownRemaining > 0)
        {
            h.CooldownRemaining--;
            if (h.CooldownRemaining == 0)
            {
                // Cooldown expired: clear Recovering so this machine can be
                // retried, but StallCycles resets to 0 (via `continue` below,
                // not rechecked this tick) — a fresh StallDetectionCycles
                // window is required before another attempt fires.
                h.Recovering = false;
                h.StallCycles = 0;
            }
            continue;
        }

        q.Clear();
        try { pb.GetQueue(q); } catch { q.Clear(); }

        bool functional = pb.IsFunctional && pb.IsWorking;
        if (q.Count == 0)
        {
            h.StallCycles = 0;
            h.Recovering = false;
            h.NotWorking = false;
            h.LastQueueSignature = QueueSignature(q);
            continue;
        }

        if (!functional)
        {
            // Non-working (disabled/unpowered/damaged) is never an input-blockage stall.
            h.StallCycles = 0;
            h.Recovering = false;
            h.NotWorking = true;
            h.LastQueueSignature = QueueSignature(q);
            Warn("Machine not working (check power/enabled/damage): " + pb.CustomName);
            continue;
        }
        h.NotWorking = false;

        string sig = QueueSignature(q);
        // IsProducing means actively working right now — a long recipe can run
        // many cycles before queue-head decrements, so don't treat that as a stall.
        bool activelyProducing = false;
        try { activelyProducing = pb.IsProducing; } catch { activelyProducing = false; }

        if (sig != h.LastQueueSignature || activelyProducing)
        {
            h.StallCycles = 0;
            h.Recovering = false;
        }
        else
        {
            h.StallCycles++;
        }
        h.LastQueueSignature = sig;

        IMyInventory inInv = pb.InputInventory;
        if (inInv != null)
            h.LastInputFillPercent = inInv.MaxVolume > 0 ? ((double)inInv.CurrentVolume / (double)inInv.MaxVolume) * 100.0 : 0;

        if (h.StallCycles >= _cfg.StallDetectionCycles && !h.Recovering)
        {
            // A zero transfer budget is not an attempt — don't burn a cooldown on it.
            if (transferBudget <= 0)
            {
                h.LastRecoveryResult = "deferred: no transfer budget available this cycle";
                Warn("Stall recovery deferred (no transfer budget): " + pb.CustomName);
            }
            else
            {
                transferBudget = EvacuateMachineInput(pb, h, transferBudget);
                h.Recovering = true;
                h.CooldownRemaining = _cfg.RecoveryCooldownCycles;
                h.StallCycles = 0;
                anyRecovery = true;
            }
        }
    }
    return anyRecovery;
}

int EvacuateMachineInput(IMyProductionBlock pb, MachineHealth h, int transferBudget)
{
    IMyInventory inInv = pb.InputInventory;
    h.LastItemsEvacuated = 0;
    h.LastItemsBlocked = 0;

    if (inInv == null)
    {
        h.LastRecoveryResult = "no input inventory";
        return transferBudget;
    }

    List<MyInventoryItem> items = new List<MyInventoryItem>();
    inInv.GetItems(items);
    double evacuated = 0, blocked = 0;

    for (int k = 0; k < items.Count; k++)
    {
        if (IsProcessItem(items[k].Type)) continue; // IO process resource — left alone, not evacuated, not counted

        if (transferBudget <= 0) { blocked += (double)items[k].Amount; continue; }

        string category = CategoryForType(items[k].Type.TypeId);
        if (category == null) { blocked += (double)items[k].Amount; continue; }

        MyFixedPoint moved;
        bool ok = RouteItem(inInv, items[k], category, out moved);

        if (ok) { evacuated += (double)moved; blocked += (double)(items[k].Amount - moved); transferBudget--; }
        else blocked += (double)items[k].Amount;
    }

    h.LastItemsEvacuated = evacuated;
    h.LastItemsBlocked = blocked;
    h.LastRecoveryResult = "evacuated=" + F(evacuated) + " blocked=" + F(blocked);
    _cycleLog.Add("RECOVERY " + pb.CustomName + " " + h.LastRecoveryResult);

    // Never report success when nothing actually moved.
    if (evacuated > 0.0001)
        Warn("Stalled machine input evacuated: " + pb.CustomName + (blocked > 0.0001 ? " (partial, " + F(blocked) + " left blocked)" : ""));
    else
        Warn("Stall recovery blocked (no destination/budget available): " + pb.CustomName);
    return transferBudget;
}

// ---------------------------------------------------------------------------
// Production planning (spec #13-#17) — feasibility-gated, budget-limited.
// ---------------------------------------------------------------------------
void BuildPlan()
{
    _demand.Clear();
    _queueSupportDemand.Clear();
    _budget.Clear();
    _supportReserved.Clear();
    _plannedOutput.Clear();
    _plannedJobs.Clear();
    _planOrderSet.Clear();
    _planOrder.Clear();
    _planRecords.Clear();

    // Budget = physical on-hand surplus above each item's own [Stock] floor.

    // Priority 1: existing/manual queue jobs create dependency demand for
    // their ingredients. The queued item itself is untouched.
    List<string> queueRoots = new List<string>();
    foreach (var kv in _queuedJobs) queueRoots.Add(kv.Key);
    queueRoots.Sort(StringComparer.OrdinalIgnoreCase);

    for (int r = 0; r < queueRoots.Count; r++)
    {
        string parentAlias = queueRoots[r];
        double jobs = Get(_queuedJobs, parentAlias);
        Recipe recipe;
        if (jobs <= 0.0001 || !_recipes.TryGetValue(parentAlias, out recipe)) continue;

        for (int i = 0; i < recipe.Inputs.Count; i++)
        {
            Ingredient ing = recipe.Inputs[i];
            string dep = Canon(ing.Item);
            double amount = ing.Amount * jobs;
            // Do not AddTo(_queueSupportDemand, ...) here — EnsureFeasible
            // already does this when isSupportBranch is true; doing both double-counts.
            HashSet<string> stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EnsureFeasible(dep, amount, true, stack, 0);

            // Reserve this direct ingredient's on-hand consumption only here
            // (once per direct ingredient), never recursively deeper — that
            // is what keeps Motor->Electromagnet->Iron from self-blocking:
            // Iron's budget stays untouched until Electromagnet's own
            // feasibility gate (the standard commit-decrement) has its turn.
            ReserveSupportBudget(dep, EffectiveSupportTarget(dep));
        }
    }

    // Priority 2: [Stock] targets, furthest-below-target-by-percent first, alpha tie-break.
    List<string> stockRoots = new List<string>(_cfg.StockTargets.Keys);
    stockRoots.Sort((a, b) =>
    {
        double pctA = DeficitPercent(a);
        double pctB = DeficitPercent(b);
        int cmp = pctB.CompareTo(pctA); // descending deficit
        if (cmp != 0) return cmp;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    });

    for (int i = 0; i < stockRoots.Count; i++)
    {
        HashSet<string> stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnsureFeasible(stockRoots[i], _cfg.StockTargets[stockRoots[i]], false, stack, 0);
    }
}

double DeficitPercent(string alias)
{
    double target = Get(_cfg.StockTargets, alias);
    if (target <= 0) return -1;
    double have = Get(_onHand, alias) + Get(_queuedOutput, alias);
    double deficit = target - have;
    return Math.Max(0, deficit) / target * 100.0;
}

double Get(Dictionary<string, double> d, string key)
{
    double v;
    return d != null && d.TryGetValue(Canon(key), out v) ? v : 0;
}

double GetBudget(string alias)
{
    double v;
    if (_budget.TryGetValue(alias, out v)) return v;
    double floor = Get(_cfg.StockTargets, alias);
    double physical = Get(_onHand, alias);
    v = Math.Max(0, physical - floor);
    _budget[alias] = v;
    return v;
}

// Mirrors EnsureFeasible's effectiveTarget calc so BuildPlan can reserve
// exactly what was just computed, after the fact.
double EffectiveSupportTarget(string alias)
{
    double target = Get(_demand, alias);
    double stagedCredit = Math.Min(Get(_stagedInputStock, alias), Get(_queueSupportDemand, alias));
    return Math.Max(0, target - stagedCredit);
}

// Reserves the on-hand stock needed to cover cumulative existing-queue
// support demand. Idempotent per alias via _supportReserved: a second call
// (e.g. another parent sharing the ingredient) only reserves the new delta.
void ReserveSupportBudget(string alias, double effectiveSupportTarget)
{
    double physical = Get(_onHand, alias);
    double neededFromPhysical = Math.Min(physical, effectiveSupportTarget);
    double alreadyReserved = Get(_supportReserved, alias);
    double additional = neededFromPhysical - alreadyReserved;
    if (additional <= 0.0001) return;

    double budgetNow = GetBudget(alias);
    double actual = Math.Min(additional, budgetNow);
    if (actual <= 0) return;

    _budget[alias] = budgetNow - actual;
    AddTo(_supportReserved, alias, actual);
}

// Mutates global demand/budget/plannedOutput/planRecords. Recurses for the
// FULL aspirational amount so deep shortages surface for future cycles, but
// gates what's committed THIS cycle by a shared budget ledger so an
// irreversible AddQueueItem is never issued for an infeasible quantity.
void EnsureFeasible(string alias, double demandIncrement, bool isSupportBranch, HashSet<string> stack, int depth)
{
    alias = Canon(alias);
    if (depth > 20 || stack.Contains(alias)) return; // dependency cycle guard

    AddTo(_demand, alias, demandIncrement);
    if (isSupportBranch) AddTo(_queueSupportDemand, alias, demandIncrement);

    double target = Get(_demand, alias);
    double stagedCredit = Math.Min(Get(_stagedInputStock, alias), Get(_queueSupportDemand, alias));
    double effectiveTarget = Math.Max(0, target - stagedCredit);

    // For a support-branch demand, on-hand stock inside this alias's OWN
    // [Stock] floor isn't available to satisfy it (e.g. CopperWire=5000
    // floor, physical=5000: a manual queue needing 300 must trigger +300
    // production, not be waved through as satisfied). A plain [Stock] root
    // has no such adjustment — its own demand IS the floor.
    double onHandPhysical = Get(_onHand, alias);
    double availablePhysical = isSupportBranch ? Math.Max(0, onHandPhysical - Get(_cfg.StockTargets, alias)) : onHandPhysical;
    double already = availablePhysical + Get(_queuedOutput, alias) + Get(_plannedOutput, alias);
    double shortage = effectiveTarget - already;

    // NOTE: support-demand budget reservation happens ONLY in BuildPlan, once
    // per direct ingredient — never here recursively, or a raw ingredient
    // like Iron could zero its own budget before its parent's feasibility
    // gate (below) reads it, self-blocking a chain with enough material.

    PlanRecord rec;
    if (!_planRecords.TryGetValue(alias, out rec))
    {
        rec = new PlanRecord { Alias = alias };
        _planRecords[alias] = rec;
    }
    rec.Target = target;
    rec.Stock = Get(_onHand, alias);
    rec.Queued = Get(_queuedOutput, alias);
    rec.Need = Math.Max(0, shortage);

    if (shortage <= 0.0001)
    {
        rec.Action = rec.Action == "None" ? "Satisfied" : rec.Action;
        return;
    }

    Recipe recipe;
    if (!_recipes.TryGetValue(alias, out recipe))
    {
        // Terminal/raw material — we never operate refineries.
        rec.Action = "RawShortage";
        rec.BlockedBy = alias;
        rec.Blocked = rec.Need;
        return;
    }

    MyDefinitionId blueprint;
    if (!TryGetBlueprint(alias, out blueprint))
    {
        _unknownBlueprints.Add(alias);
        rec.Action = "UnknownBlueprint";
        rec.Blocked = rec.Need;
        return;
    }

    List<IMyProductionBlock> machines = GetBestMachines(alias, blueprint, recipe);
    if (machines.Count == 0)
    {
        _noMachines.Add(alias);
        rec.Action = "NoMachine";
        rec.Blocked = rec.Need;
        return;
    }

    double recipeOutput = recipe.Output > 0 ? recipe.Output : 1.0;
    int jobsWanted = (int)Math.Ceiling(shortage / recipeOutput);
    if (jobsWanted <= 0) return;

    stack.Add(alias);

    // Recurse for the FULL aspirational amount first so deep shortages
    // surface for future cycles regardless of what limits jobs this cycle.
    for (int i = 0; i < recipe.Inputs.Count; i++)
    {
        Ingredient ing = recipe.Inputs[i];
        string dep = Canon(ing.Item);
        double depDemand = ing.Amount * jobsWanted;
        EnsureFeasible(dep, depDemand, isSupportBranch, stack, depth + 1);
    }

    stack.Remove(alias);

    // Feasibility gate: limited by whichever ingredient's budget supports
    // fewest jobs. Not-yet-produced output never counts here — multi-cycle convergence.
    int feasibleJobs = jobsWanted;
    string blockingAlias = "";
    for (int i = 0; i < recipe.Inputs.Count; i++)
    {
        Ingredient ing = recipe.Inputs[i];
        string dep = Canon(ing.Item);
        double budget = GetBudget(dep);
        int supportable = (int)Math.Floor(budget / ing.Amount + 0.0001);
        if (supportable < feasibleJobs)
        {
            feasibleJobs = supportable;
            blockingAlias = dep;
        }
    }
    if (feasibleJobs < 0) feasibleJobs = 0;

    if (feasibleJobs > 0)
    {
        for (int i = 0; i < recipe.Inputs.Count; i++)
        {
            Ingredient ing = recipe.Inputs[i];
            string dep = Canon(ing.Item);
            _budget[dep] = GetBudget(dep) - (feasibleJobs * ing.Amount);
        }

        AddTo(_plannedOutput, alias, feasibleJobs * recipeOutput);
        AddTo(_plannedJobs, alias, feasibleJobs);

        if (!_planOrderSet.Contains(alias))
        {
            _planOrderSet.Add(alias);
            _planOrder.Add(alias);
        }
    }

    rec.Feasible = feasibleJobs * recipeOutput;
    rec.Blocked = Math.Max(0, rec.Need - rec.Feasible);
    rec.BlockedBy = feasibleJobs < jobsWanted ? blockingAlias : "";
    rec.JobsAdded = feasibleJobs;
    rec.Action = feasibleJobs >= jobsWanted ? "Queued" : (feasibleJobs > 0 ? "PartialQueued" : "Blocked");
}

void AddTo(Dictionary<string, double> d, string key, double amount)
{
    key = Canon(key);
    double old;
    d[key] = (d.TryGetValue(key, out old) ? old : 0) + amount;
}

void AddTo(Dictionary<string, int> d, string key, int amount)
{
    key = Canon(key);
    int old;
    d[key] = (d.TryGetValue(key, out old) ? old : 0) + amount;
}

int GetInt(Dictionary<string, int> d, string key)
{
    int v;
    return d != null && d.TryGetValue(Canon(key), out v) ? v : 0;
}

// ---------------------------------------------------------------------------
// Machine selection (spec #18) — ported ranking lessons from v1.
// ---------------------------------------------------------------------------
class MachineChoice
{
    public IMyProductionBlock Block;
    public int Rank;
    public double QueueLoad;
}

bool IsManualBench(string machineText)
{
    if (machineText.Contains("basicassemblingbench")) return true;
    if (machineText.Contains("basic assembling bench")) return true;
    if (machineText.Contains("assemblingbench")) return true;
    if (machineText.Contains("assembling bench")) return true;
    return false;
}

string MachineText(IMyProductionBlock pb)
{
    return ((pb.CustomName ?? "") + " " + pb.BlockDefinition.SubtypeId.ToString()).ToLowerInvariant();
}

int MachineRank(string machineText, Recipe recipe, bool survival)
{
    if (survival) return 9000;
    for (int i = 0; i < recipe.PreferredMachines.Count; i++)
    {
        string token = recipe.PreferredMachines[i].ToLowerInvariant();
        if (token == "assembler" && machineText.Contains("advanced assembler")) continue;
        if (machineText.Contains(token)) return i * 100;
    }
    return 5000;
}

double QueueLoad(IMyProductionBlock pb)
{
    List<MyProductionItem> q = new List<MyProductionItem>();
    try { pb.GetQueue(q); } catch { return 999999; }
    double total = 0;
    for (int i = 0; i < q.Count; i++) total += (double)q[i].Amount;
    return total;
}

List<IMyProductionBlock> GetBestMachines(string alias, MyDefinitionId bp, Recipe recipe)
{
    List<MachineChoice> choices = new List<MachineChoice>();

    for (int i = 0; i < _managedMachines.Count; i++)
    {
        IMyProductionBlock pb = _managedMachines[i];
        if (pb == null || !pb.IsWorking) continue;

        IMyAssembler asm = pb as IMyAssembler;
        if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;

        string text = MachineText(pb);
        if (IsManualBench(text)) continue;

        bool survival = text.Contains("survivalkit") || text.Contains("survival kit");
        if (survival && !_cfg.AllowSurvivalKitFallback) continue;

        bool canUse = false;
        try { canUse = pb.CanUseBlueprint(bp); } catch { canUse = false; }
        if (!canUse) continue;

        choices.Add(new MachineChoice { Block = pb, Rank = MachineRank(text, recipe, survival), QueueLoad = QueueLoad(pb) });
    }

    if (choices.Count == 0) return new List<IMyProductionBlock>();

    int bestRank = int.MaxValue;
    for (int i = 0; i < choices.Count; i++) if (choices[i].Rank < bestRank) bestRank = choices[i].Rank;

    List<MachineChoice> best = new List<MachineChoice>();
    for (int i = 0; i < choices.Count; i++) if (choices[i].Rank == bestRank) best.Add(choices[i]);
    best.Sort((a, b) => a.QueueLoad.CompareTo(b.QueueLoad));

    List<IMyProductionBlock> result = new List<IMyProductionBlock>();
    for (int i = 0; i < best.Count; i++) result.Add(best[i].Block);
    return result;
}

bool TryGetBlueprint(string alias, out MyDefinitionId bp)
{
    alias = Canon(alias);
    string text = null;
    if (_cfg.BlueprintOverrides.ContainsKey(alias)) text = _cfg.BlueprintOverrides[alias];
    else if (_builtinBlueprints.ContainsKey(alias)) text = _builtinBlueprints[alias];

    if (string.IsNullOrWhiteSpace(text)) { bp = default(MyDefinitionId); return false; }
    return MyDefinitionId.TryParse(text, out bp);
}

// ---------------------------------------------------------------------------
// Apply plan — THE ONLY QUEUE MUTATION IN THIS SCRIPT IS AddQueueItem().
// ---------------------------------------------------------------------------
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
        if (machines.Count == 0) { _noMachines.Add(alias); continue; }

        int baseJobs = jobs / machines.Count;
        int remainder = jobs % machines.Count;

        for (int i = 0; i < machines.Count; i++)
        {
            int add = baseJobs + (i < remainder ? 1 : 0);
            if (add <= 0) continue;
            try
            {
                machines[i].AddQueueItem(bp, (MyFixedPoint)add); // sole queue mutation
                PlanRecord rec;
                if (_planRecords.TryGetValue(alias, out rec)) rec.Action = "Queued";
                _cycleLog.Add("+" + add + " " + Friendly(alias) + " -> " + machines[i].CustomName);
            }
            catch (Exception ex)
            {
                _cycleLog.Add("QUEUE ERROR " + Friendly(alias) + ": " + ex.Message);
            }
        }
    }
}

// Manufactured-vs-terminal is recipe-driven: Polymer carries the Ingot
// TypeId for storage purposes but still has a Recipe entry, so planning
// recurses into it instead of stopping at TypeId.
void BuildKnowledgeBase()
{
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

    // Blueprint IDs validated in-game; only source besides [BlueprintOverrides].
    AddBuiltinBlueprint("Electromagnet", "MyObjectBuilder_BlueprintDefinition/Electromagnet");
    AddBuiltinBlueprint("CopperWire", "MyObjectBuilder_BlueprintDefinition/CopperWire");
    AddBuiltinBlueprint("Motor", "MyObjectBuilder_BlueprintDefinition/POMotorComponent");
    AddBuiltinBlueprint("HeatingElement", "MyObjectBuilder_BlueprintDefinition/HeatingElement");
    AddBuiltinBlueprint("SteelPlate", "MyObjectBuilder_BlueprintDefinition/POSteelPlate");
    AddBuiltinBlueprint("SmallSteelTube", "MyObjectBuilder_BlueprintDefinition/POSmallTube");
    AddBuiltinBlueprint("AdvancedComputer", "MyObjectBuilder_BlueprintDefinition/AdvancedComputer");
    AddBuiltinBlueprint("Plastic", "MyObjectBuilder_BlueprintDefinition/PolymerToPlastic");
    AddBuiltinBlueprint("Construction", "MyObjectBuilder_BlueprintDefinition/POConstructionComponent");
    AddBuiltinBlueprint("LargeSteelTube", "MyObjectBuilder_BlueprintDefinition/POLargeTube");
    AddBuiltinBlueprint("BasicComputer", "MyObjectBuilder_BlueprintDefinition/POComputerComponent");
    AddBuiltinBlueprint("Rubber", "MyObjectBuilder_BlueprintDefinition/Rubber");
    AddBuiltinBlueprint("TitaniumPlate", "MyObjectBuilder_BlueprintDefinition/TitaniumPlate");
    AddBuiltinBlueprint("Ceramic", "MyObjectBuilder_BlueprintDefinition/Ceramic");
    AddBuiltinBlueprint("Polymer", "MyObjectBuilder_BlueprintDefinition/SyntheticPolymer");
    AddBuiltinBlueprint("Lightbulb", "MyObjectBuilder_BlueprintDefinition/Lightbulb");
    AddBuiltinBlueprint("Display", "MyObjectBuilder_BlueprintDefinition/PODisplay");
    AddBuiltinBlueprint("MedicalComponent", "MyObjectBuilder_BlueprintDefinition/POMedicalComponent");
    AddBuiltinBlueprint("Thermocouple", "MyObjectBuilder_BlueprintDefinition/Thermocouple");
    AddBuiltinBlueprint("LithiumPowerCell", "MyObjectBuilder_BlueprintDefinition/POPowerCell");
    // Unvalidated, not hard-coded: AluminumPlate, GoldWire, MetalGrid, SensorCluster, Glass.

    AddRecipe("SteelPlate", 1, new string[] { "Plate Stamp" }, I("IronIngot", 20));
    AddRecipe("AluminumPlate", 1, new string[] { "Plate Stamp" }, I("AluminumIngot", 5));
    AddRecipe("TitaniumPlate", 1, new string[] { "Plate Stamp" }, I("TitaniumIngot", 12));

    AddRecipe("CopperWire", 1, new string[] { "Wire Drawer" }, I("CopperIngot", 1));
    AddRecipe("GoldWire", 1, new string[] { "Wire Drawer" }, I("GoldIngot", 0.6));

    AddRecipe("LargeSteelTube", 1, new string[] { "Extruder" }, I("IronIngot", 4));
    AddRecipe("SmallSteelTube", 1, new string[] { "Extruder" }, I("IronIngot", 2));

    AddRecipe("Construction", 1, new string[] { "Fabricator" }, I("IronIngot", 5));
    AddRecipe("MetalGrid", 1, new string[] { "Fabricator" }, I("IronIngot", 3), I("NickelIngot", 3), I("CobaltIngot", 3));
    AddRecipe("Electromagnet", 1, new string[] { "Fabricator" }, I("IronIngot", 1.5), I("NickelIngot", 1), I("CopperWire", 3));
    AddRecipe("HeatingElement", 1, new string[] { "Fabricator" }, I("NickelIngot", 5), I("CopperIngot", 5));
    AddRecipe("Lightbulb", 10, new string[] { "Fabricator" }, I("Glass", 1), I("CopperWire", 10));

    AddRecipe("Motor", 1, new string[] { "Assembler" }, I("Electromagnet", 3), I("LargeSteelTube", 1), I("CopperWire", 3));
    AddRecipe("MedicalComponent", 1, new string[] { "Assembler" }, I("IronIngot", 12), I("NickelIngot", 8), I("SilverIngot", 8));

    AddRecipe("BasicComputer", 1, new string[] { "Microelectronics Factory", "Fabricator" }, I("CopperWire", 3), I("SiliconWafer", 2));
    AddRecipe("AdvancedComputer", 1, new string[] { "Microelectronics Factory" }, I("GoldWire", 3), I("SiliconWafer", 2), I("Plastic", 2));
    AddRecipe("SensorCluster", 1, new string[] { "Microelectronics Factory" }, I("Glass", 1), I("BasicComputer", 2), I("CopperWire", 3), I("SiliconWafer", 3), I("NickelIngot", 3));
    AddRecipe("Display", 1, new string[] { "Microelectronics Factory" }, I("CopperWire", 2), I("SiliconWafer", 3), I("Plastic", 2), I("SilverIngot", 0.1), I("BasicComputer", 1), I("NickelIngot", 1));
    AddRecipe("Thermocouple", 1, new string[] { "Microelectronics Factory" }, I("SiliconWafer", 2), I("CopperWire", 3), I("Plastic", 1), I("AluminumIngot", 2));

    AddRecipe("Ceramic", 1, new string[] { "Ceramics Furnace" }, I("SiliconWafer", 3), I("Carbon", 2));
    AddRecipe("Glass", 1, new string[] { "Ceramics Furnace" }, I("SiliconWafer", 3));

    AddRecipe("Polymer", 1, new string[] { "Synthetics Factory" }, I("SiliconWafer", 1), I("Carbon", 1.5), I("Sulfur", 0.5));
    AddRecipe("Plastic", 1, new string[] { "Synthetics Factory" }, I("Polymer", 1.5));
    AddRecipe("Rubber", 1, new string[] { "Synthetics Factory" }, I("Polymer", 2));

    AddRecipe("LithiumPowerCell", 1, new string[] { "Advanced Assembler" }, I("AluminumPlate", 1), I("CopperWire", 4), I("LithiumPaste", 10), I("Rubber", 2), I("Carbon", 3));
}

void AddItem(string alias, string friendly, string type)
{
    _items[alias] = new ItemDef { Alias = alias, Friendly = friendly, Type = type };
    _aliases[alias] = alias;
    int slash = type.IndexOf('/');
    if (slash >= 0 && slash + 1 < type.Length)
    {
        string subtype = type.Substring(slash + 1);
        if (!_aliases.ContainsKey(subtype)) _aliases[subtype] = alias;
    }
}

void AddAlias(string alias, string canonical) { _aliases[alias] = canonical; }
void AddBuiltinBlueprint(string alias, string blueprint) { _builtinBlueprints[Canon(alias)] = blueprint; }

Ingredient I(string item, double amount) { return new Ingredient { Item = Canon(item), Amount = amount }; }

void AddRecipe(string item, double output, string[] preferred, params Ingredient[] inputs)
{
    Recipe r = new Recipe { Item = Canon(item), Output = output };
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

string F(double v)
{
    if (Math.Abs(v - Math.Round(v)) < 0.0001) return Math.Round(v).ToString("N0");
    if (Math.Abs(v) >= 1000) return v.ToString("N1");
    return v.ToString("0.###");
}

class ItemDef { public string Alias; public string Friendly; public string Type; }
class Ingredient { public string Item; public double Amount; }
class Recipe
{
    public string Item;
    public double Output;
    public List<Ingredient> Inputs = new List<Ingredient>();
    public List<string> PreferredMachines = new List<string>();
}

// Script owns [IOPM.*] sections only; everything else in Custom Data is left as-is.
string _lastWrittenCustomData = null;

void WriteDiagnostics()
{
    MyIni ini = new MyIni();
    MyIniParseResult result;
    if (!ini.TryParse(Me.CustomData, out result)) return; // don't clobber unparsable user data

    List<string> sections = new List<string>();
    ini.GetSections(sections);
    for (int i = 0; i < sections.Count; i++)
        if (sections[i].StartsWith("IOPM.", StringComparison.OrdinalIgnoreCase))
            ini.DeleteSection(sections[i]);

    ini.Set("IOPM.Status", "Version", VERSION);
    ini.Set("IOPM.Status", "State", _cfg.GeneralEnabled ? "Running" : "Disabled");
    ini.Set("IOPM.Status", "Sorting", _cfg.SortingEnabled ? "OK" : "Off");
    ini.Set("IOPM.Status", "Production", _cfg.ProductionEnabled ? "OK" : "Off");
    ini.Set("IOPM.Status", "Warnings", _warnings.Count);
    ini.Set("IOPM.Status", "LastCycleSeconds", Math.Round(_lastCycleSeconds, 2));

    foreach (var kv in _categoryPools)
    {
        string cat = kv.Key;
        List<ContainerInfo> pool = kv.Value;
        int healthy = 0; double totalVol = 0, curVol = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            if (_ignoredInventories.Contains(pool[i].Inventory)) continue;
            healthy++;
            totalVol += (double)pool[i].Inventory.MaxVolume;
            curVol += (double)pool[i].Inventory.CurrentVolume;
        }
        double fillPct = totalVol > 0 ? curVol / totalVol * 100.0 : 0;
        string section = "IOPM.Warehouse." + cat;
        ini.Set(section, "Containers", pool.Count);
        ini.Set(section, "Healthy", healthy);
        ini.Set(section, "FillPercent", Math.Round(fillPct, 1));
    }

    ini.Set("IOPM.Production", "StockItems", _cfg.StockTargets.Count);
    int ready = 0, producing = 0, blockedCount = 0;
    foreach (var kv in _planRecords)
    {
        PlanRecord r = kv.Value;

        // Ready/Producing/Blocked stay scoped to [Stock] roots, but every
        // plan record (including dependencies/support items) still gets its
        // own diagnostic section below.
        if (_cfg.StockTargets.ContainsKey(r.Alias))
        {
            if (r.Action == "Satisfied") ready++;
            else if (r.Action == "Blocked" || r.Action == "RawShortage" || r.Action == "NoMachine" || r.Action == "UnknownBlueprint") blockedCount++;
            else producing++;
        }

        string section = "IOPM.LastPlan." + r.Alias;
        ini.Set(section, "Target", F(r.Target));
        ini.Set(section, "Stock", F(r.Stock));
        ini.Set(section, "Queued", F(r.Queued));
        ini.Set(section, "Need", F(r.Need));
        ini.Set(section, "Feasible", F(r.Feasible));
        ini.Set(section, "Blocked", F(r.Blocked));
        if (r.BlockedBy != "") ini.Set(section, "BlockedBy", r.BlockedBy);
        ini.Set(section, "Action", r.Action);
        ini.Set(section, "JobsAdded", r.JobsAdded);

        // Support-specific values: present only when this alias carries
        // existing-queue support demand.
        double supportDemand = Get(_queueSupportDemand, r.Alias);
        if (supportDemand > 0.0001)
        {
            ini.Set(section, "SupportDemand", F(supportDemand));
            ini.Set(section, "SupportReserved", F(Get(_supportReserved, r.Alias)));
        }
    }
    ini.Set("IOPM.Production", "Ready", ready);
    ini.Set("IOPM.Production", "Producing", producing);
    ini.Set("IOPM.Production", "Blocked", blockedCount);

    int healthyMachines = 0, stalledMachines = 0, recoveringMachines = 0, notWorkingMachines = 0;
    foreach (var kv in _machineHealth)
    {
        MachineHealth h = kv.Value;
        bool stillManaged = false;
        for (int i = 0; i < _managedMachines.Count; i++)
            if (_managedMachines[i].EntityId == h.EntityId) { stillManaged = true; break; }
        if (!stillManaged) continue;

        // "Stalled" reserved for StallCycles reaching the threshold; below that it's Waiting.
        bool pastThreshold = h.StallCycles >= _cfg.StallDetectionCycles;
        if (h.NotWorking) notWorkingMachines++;
        else if (h.Recovering) recoveringMachines++;
        else if (pastThreshold) stalledMachines++;
        else healthyMachines++;

        if (h.Recovering || h.StallCycles > 0 || h.NotWorking)
        {
            string safeName = SanitizeSectionName(h.Name);
            string section = "IOPM.Machine." + safeName;
            string state = h.NotWorking ? "NotWorking" : (h.Recovering ? "Recovering" : (pastThreshold ? "Stalled" : "Waiting"));
            ini.Set(section, "State", state);
            ini.Set(section, "StalledCycles", h.StallCycles);
            ini.Set(section, "InputFillPercent", Math.Round(h.LastInputFillPercent, 1));
            ini.Set(section, "RecoveryAttempted", h.Recovering);
            ini.Set(section, "ItemsEvacuated", F(h.LastItemsEvacuated));
            ini.Set(section, "ItemsBlocked", F(h.LastItemsBlocked));
        }
    }
    ini.Set("IOPM.Machines", "Healthy", healthyMachines);
    ini.Set("IOPM.Machines", "Stalled", stalledMachines);
    ini.Set("IOPM.Machines", "Recovering", recoveringMachines);
    ini.Set("IOPM.Machines", "NotWorking", notWorkingMachines);

    ini.Set("IOPM.Organization", "Examined", _orgExamined);
    ini.Set("IOPM.Organization", "OutOfOrder", _orgOutOfOrder);
    ini.Set("IOPM.Organization", "Attempts", _orgAttempts);
    ini.Set("IOPM.Organization", "Succeeded", _orgSucceeded);
    ini.Set("IOPM.Organization", "Failed", _orgFailed);
    ini.Set("IOPM.Organization", "LastContainer", _orgLastContainer);
    ini.Set("IOPM.Organization", "LastItem", _orgLastItem);
    ini.Set("IOPM.Organization", "LastSourceIndex", _orgLastSourceIndex);
    ini.Set("IOPM.Organization", "LastTargetIndex", _orgLastTargetIndex);
    ini.Set("IOPM.Organization", "LastResult", _orgLastResult);

    for (int i = 0; i < _warnings.Count && i < 20; i++)
    {
        string section = "IOPM.Warning." + (i + 1);
        ini.Set(section, "Severity", "Warning");
        ini.Set(section, "Message", _warnings[i]);
    }

    for (int i = 0; i < _unknownItems.Count && i < 20; i++)
    {
        string section = "IOPM.Unknown." + (i + 1);
        ini.Set(section, "Item", _unknownItems[i].ItemType);
        ini.Set(section, "Reason", _unknownItems[i].Reason);
        ini.Set(section, "SeenIn", _unknownItems[i].SeenIn);
    }
    for (int i = 0; i < _unknownBlueprints.Count && i < 20; i++)
    {
        int idx = _unknownItems.Count + i + 1;
        string section = "IOPM.Unknown." + idx;
        ini.Set(section, "Item", "?");
        ini.Set(section, "Reason", "NoBlueprint");
        ini.Set(section, "SeenIn", string.Join(",", _unknownBlueprints));
    }

    if (_configErrors.Count > 0)
        for (int i = 0; i < _configErrors.Count; i++)
            ini.Set("IOPM.ConfigError." + (i + 1), "Message", _configErrors[i]);

    string finalText = ini.ToString();
    if (!string.Equals(finalText, _lastWrittenCustomData, StringComparison.Ordinal))
    {
        Me.CustomData = finalText;
        _lastWrittenCustomData = finalText;
        // Re-read rather than trust finalText: if the game normalizes
        // anything on set/get, this keeps the no-self-trigger guarantee intact.
        _lastCustomDataSeen = Me.CustomData;
    }
}

string SanitizeSectionName(string name)
{
    string result = "";
    for (int i = 0; i < name.Length; i++)
    {
        char c = name[i];
        if (char.IsLetterOrDigit(c)) result += c;
    }
    return result == "" ? "Unnamed" : result;
}

// ---------------------------------------------------------------------------
// Status display (spec #20) — operational health, not a diagnostic dump.
// ---------------------------------------------------------------------------
void RenderStatusScreens()
{
    string text = BuildStatusText();
    for (int i = 0; i < _statusScreens.Count; i++)
    {
        IMyTextSurface s = _statusScreens[i];
        if (s == null) continue;
        s.ContentType = ContentType.TEXT_AND_IMAGE;
        s.Font = "Monospace";
        s.FontSize = 0.8f;
        s.WriteText(text, false);
    }
}

string BuildStatusText()
{
    System.Text.StringBuilder sb = new System.Text.StringBuilder();
    sb.Append("IO PRODUCTION MANAGER v").Append(VERSION).Append("\n\n");

    sb.Append("SYSTEM\n");
    sb.Append("Status: ").Append(_cfg.GeneralEnabled ? "RUNNING" : "DISABLED").Append("\n");
    sb.Append("Sorting: ").Append(_cfg.SortingEnabled ? "OK" : "OFF").Append("\n");
    sb.Append("Production: ").Append(_cfg.ProductionEnabled ? "ON" : "OFF").Append("\n");
    sb.Append("Last Cycle: ").Append(Math.Round(_lastCycleSeconds, 1)).Append("s\n");
    if (_usingFallbackStatusScreen)
        sb.Append("WARNING: no [StatusScreen] block found; showing on PB.\n");
    sb.Append("\n");

    sb.Append("WAREHOUSE\n");
    for (int i = 0; i < CATEGORY_ORDER.Length; i++)
    {
        string cat = CATEGORY_ORDER[i];
        List<ContainerInfo> pool;
        if (!_categoryPools.TryGetValue(cat, out pool) || pool.Count == 0) continue;
        double totalVol = 0, curVol = 0; int healthy = 0;
        for (int k = 0; k < pool.Count; k++)
        {
            if (_ignoredInventories.Contains(pool[k].Inventory)) continue;
            healthy++;
            totalVol += (double)pool[k].Inventory.MaxVolume;
            curVol += (double)pool[k].Inventory.CurrentVolume;
        }
        double pct = totalVol > 0 ? curVol / totalVol * 100.0 : 0;
        sb.Append(PadRight(cat, 12)).Append(PadRight(healthy.ToString(), 4)).Append(Math.Round(pct)).Append("%\n");
    }
    sb.Append("\n");

    sb.Append("PRODUCTION\n");
    int ready = 0, producing = 0, blocked = 0;
    List<PlanRecord> stockRecords = new List<PlanRecord>();
    foreach (var kv in _planRecords)
        if (_cfg.StockTargets.ContainsKey(kv.Key)) stockRecords.Add(kv.Value);
    for (int i = 0; i < stockRecords.Count; i++)
    {
        if (stockRecords[i].Action == "Satisfied") ready++;
        else if (stockRecords[i].Action == "Blocked" || stockRecords[i].Action == "RawShortage" ||
                 stockRecords[i].Action == "NoMachine" || stockRecords[i].Action == "UnknownBlueprint") blocked++;
        else producing++;
    }
    sb.Append(PadRight("Ready", 12)).Append(ready).Append("\n");
    sb.Append(PadRight("Producing", 12)).Append(producing).Append("\n");
    sb.Append(PadRight("Blocked", 12)).Append(blocked).Append("\n\n");

    stockRecords.Sort((a, b) =>
    {
        int rankA = a.Action == "Blocked" ? 0 : (a.Action == "PartialQueued" || a.Action == "Queued" ? 1 : 2);
        int rankB = b.Action == "Blocked" ? 0 : (b.Action == "PartialQueued" || b.Action == "Queued" ? 1 : 2);
        return rankA.CompareTo(rankB);
    });
    int shown = 0;
    for (int i = 0; i < stockRecords.Count && shown < 6; i++)
    {
        PlanRecord r = stockRecords[i];
        if (r.Action == "Satisfied") continue;
        string line = Friendly(r.Alias) + " " + F(r.Stock + r.Queued) + "/" + F(r.Target);
        if (r.Action == "Blocked" || r.Action == "RawShortage" || r.Action == "NoMachine" || r.Action == "UnknownBlueprint")
            line += "  BLOCKED" + (r.BlockedBy != "" ? ": " + Friendly(r.BlockedBy) : "");
        else if (r.Feasible > 0)
            line += "  +" + F(r.Feasible);
        sb.Append(line).Append("\n");
        shown++;
    }
    sb.Append("\n");

    sb.Append("MACHINES\n");
    int healthyM = 0, stalledM = 0, recoveringM = 0, notWorkingM = 0;
    foreach (var kv in _machineHealth)
    {
        bool stillManaged = false;
        for (int i = 0; i < _managedMachines.Count; i++)
            if (_managedMachines[i].EntityId == kv.Key) { stillManaged = true; break; }
        if (!stillManaged) continue;
        if (kv.Value.NotWorking) notWorkingM++;
        else if (kv.Value.Recovering) recoveringM++;
        else if (kv.Value.StallCycles >= _cfg.StallDetectionCycles) stalledM++;
        else healthyM++;
    }
    sb.Append(PadRight("Healthy", 12)).Append(healthyM).Append("\n");
    sb.Append(PadRight("Stalled", 12)).Append(stalledM).Append("\n");
    sb.Append(PadRight("Recovering", 12)).Append(recoveringM).Append("\n");
    if (notWorkingM > 0) sb.Append(PadRight("Not Working", 12)).Append(notWorkingM).Append("\n");
    sb.Append("\n");

    if (_warnings.Count > 0)
    {
        sb.Append("WARNINGS\n");
        for (int i = 0; i < _warnings.Count && i < 5; i++)
            sb.Append(i + 1).Append(". ").Append(_warnings[i]).Append("\n");
    }

    return sb.ToString();
}

string PadRight(string s, int len)
{
    if (s.Length >= len) return s + " ";
    return s + new string(' ', len - s.Length);
}
