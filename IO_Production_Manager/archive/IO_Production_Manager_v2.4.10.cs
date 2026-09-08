// IO Production Manager v2.4.10 - warehouse sorting, production planning, stall recovery,
// dock-aware logistics, status + Custom-Data diagnostics. History: CHANGELOG.md.
// ABSOLUTE INVARIANT: AddQueueItem() is the ONLY production-queue mutation in this file.
// No ClearQueue, no removal, no reorder. The script owns [IOPM.*] in Custom Data only.
// INVARIANT: remote/docked inventory NEVER enters base _onHand or [Stock].
// v2.4.10: the seeded template also lists every item type present in the BASE, so ores and
// modded items appear even when the container being seeded is empty.
const string VERSION = "2.4.10";
const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
static readonly StringComparer SCI = StringComparer.OrdinalIgnoreCase;
static Dictionary<string, double> DD() { return new Dictionary<string, double>(SCI); }
static Dictionary<string, int> DI() { return new Dictionary<string, int>(SCI); }
static Dictionary<string, string> DS() { return new Dictionary<string, string>(SCI); }
static HashSet<string> HS() { return new HashSet<string>(SCI); }
const int PHASE_IDLE = 0, PHASE_DISCOVER = 1, PHASE_SORTING = 2, PHASE_SCAN = 3,
  PHASE_STALL = 4, PHASE_DOCKSCAN = 5, PHASE_DOCKSERVICE = 6, PHASE_RESCAN = 7,
  PHASE_BUILDPLAN = 8, PHASE_APPLYPLAN = 9, PHASE_DIAGNOSTICS = 10, PHASE_STATUSRENDER = 11, PHASE_STOCKRENDER = 12;
static readonly string[] PN = new string[] { "Idle", "Discover", "Sorting", "ScanInventory",
  "StallRecovery", "DockScan", "DockService", "RescanInventory", "BuildPlan", "ApplyPlan",
  "WriteDiagnostics", "StatusRender", "StockRender" };
static readonly string[] CATS = new string[]
  { "Ores", "Ingots", "Components", "Ammo", "Tools", "Consumables", "Seeds", "Misc", "Overflow" };
static readonly string[] IGNORE_TAGS = new string[]
  { "[IOPM-Ignore]", "[Locked]", "!GSIM-Locked", "[No Sorting]", "!GSIM-NoSorting" };
static readonly string[] STATUS_TAGS = new string[]
  { "[StatusScreen]", "!StatusScreen", "!GSIM-StatusScreen" };
const string STOCK_TAG = "[IOPM-Stock]";
const string OVF = "Overflow";
// DOCK-BOUNDARY tags: checked on EITHER connector and aggregated over ALL pairs reaching a
// construct. EXCL skips it; NOSORT stops ordinary unload but [Stock] loadouts still serviced.
static readonly string[] EXCL_TAGS = new string[] { "!GSIM-NoGOAT", "[No GOAT]", "[IOPM-Ignore]" };
static readonly string[] NOSORT_TAGS = new string[] { "!GSIM-NoSorting", "[No Sorting]" };
// Per-REMOTE-BLOCK full exclusion: never unloaded, loadout-serviced, sourced or targeted.
static readonly string[] LOCK_TAGS = new string[] { "[IOPM-Ignore]", "[Locked]", "!GSIM-Locked" };
static readonly string[] LOADOUT_TAGS = new string[] { "!GSIM-Stock", "[Stock]" };
const string GSTART = "@GOAT-Stock Definitions START", GEND = "@GOAT-Stock Definitions END";
class Config {
  public bool GeneralEnabled = true;
  public double UpdateSeconds = 5;
  public bool SortingEnabled = true;
  public bool BalanceEnabled = true;
  public double BalanceTolerancePercent = 5;
  public int MaxTransfersPerCycle = 25;
  public string OverflowPolicy = "Overflow"; // fixed compatibility token, not configurable
  public bool ProductionEnabled = false;
  public bool AllowSurvivalKitFallback = false;
  public int StallDetectionCycles = 5;
  public bool InputRecoveryEnabled = true;
  public int RecoveryCooldownCycles = 6;
  public bool DockEnabled = true, UnloadDocked = true, ServiceLoadouts = true,
    UnloadConnInv = true, UnloadCargo = true, UnloadDrills = true;
  public double BorrowPercent = 25;
  public bool SeedLoadoutTemplate = true;
  // ManageFonts=false hands Font/FontSize back to the player.
  public bool ManageFonts = true;
  public double StatusFontSize = 0.8, StockFontSize = 0.8;
  public int StockRowsPerPage = 0; // 0 = every row on one page
  public Dictionary<string, double> StockTargets = DD();
  public Dictionary<string, string> BlueprintOverrides = DS();
}
MyIni _ini = new MyIni();
Config _cfg = new Config();
List<string> _configErrors = new List<string>();
string _lastCustomDataSeen = null;
// Mid-cycle Custom Data change; reload deferred to PHASE_IDLE (config-snapshot rule).
bool _cfgDirty = false;
double _elapsedSinceCycle = 999;
Dictionary<string, ItemDef> _items = new Dictionary<string, ItemDef>(SCI);
Dictionary<string, string> _typeAlias = DS(); // "TypeId/SubtypeId" -> alias (reverse of _items)
Dictionary<string, string> _aliases = DS();
Dictionary<string, Recipe> _recipes = new Dictionary<string, Recipe>(SCI);
Dictionary<string, string> _builtinBlueprints = DS();
Dictionary<string, string> _typeCategory = DS();   // broad TypeId -> category
Dictionary<string, string> _subCat = DS();
class CI {
  public IMyTerminalBlock Block;
  public IMyInventory Inventory;
  public HashSet<string> Categories = HS();
  public bool IsSingleCategory { get { return Categories.Count == 1; } }
}
Dictionary<string, List<CI>> _pools = new Dictionary<string, List<CI>>(SCI);
List<CI> _cons = new List<CI>();
Dictionary<IMyInventory, CI> _invCon = new Dictionary<IMyInventory, CI>();
List<IMyInventory> _plainInv = new List<IMyInventory>();
HashSet<IMyInventory> _ignInv = new HashSet<IMyInventory>();
List<IMyTextSurface> _statScr = new List<IMyTextSurface>();
bool _fallbackScr = false;
List<IMyTextSurface> _stockScr = new List<IMyTextSurface>();
List<IMyProductionBlock> _mach = new List<IMyProductionBlock>();
List<IMyInventory> _mOut = new List<IMyInventory>();
List<IMyInventory> _mIn = new List<IMyInventory>();
List<IMyInventory> _refOut = new List<IMyInventory>();
Dictionary<IMyInventory, string> _invOwner = new Dictionary<IMyInventory, string>();
Dictionary<string, double> _whStock = DD();
Dictionary<string, double> _ovStock = DD();
Dictionary<string, double> _moStock = DD();
Dictionary<string, double> _stagedStock = DD();
Dictionary<string, double> _onHand = DD(); // Warehouse+Overflow+MachineOutput
Dictionary<string, double> _queuedJobs = DD();
Dictionary<string, double> _queuedOutput = DD();
Dictionary<string, double> _demand = DD();
Dictionary<string, double> _queueSupportDemand = DD();
Dictionary<string, double> _budget = DD();
// Physical stock reserved for existing-queue support; no later branch may respend it.
Dictionary<string, double> _supportReserved = DD();
Dictionary<string, double> _plannedOutput = DD();
Dictionary<string, int> _plannedJobs = DI();
HashSet<string> _planOrderSet = HS();
List<string> _planOrder = new List<string>();
class PRec {
  public string Alias;
  public double Target, Stock, Queued, Need, Feasible, Blocked;
  public string BlockedBy = "";
  public string Action = "None";
  public int JobsAdded = 0;
}
Dictionary<string, PRec> _planRecords = new Dictionary<string, PRec>(SCI);
HashSet<string> _unknownBlueprints = HS();
HashSet<string> _noMachines = HS();
List<URec> _unknownItems = new List<URec>();
class URec { public string ItemType; public string Reason; public string SeenIn; }
List<string> _warnings = new List<string>();
List<string> _cycleLog = new List<string>();
double _lastCycleSeconds = 0;
int _cyclePhase = PHASE_IDLE;
int _cycleRecoveryReserve = 0;
int _cycleSortingLeftover = 0;
int _cycleDockReserve = 0;
// Dock state. Remote inventories live ONLY here — never in _cons/_pools/_plainInv/_onHand.
// LQ.Raw is the resolved "TypeId/SubtypeId"; null = unresolved name (diagnostic only).
class LQ { public double Amt; public char Mod; public string Raw; } // E exact, M min, L max, A all
class LCon {
  public string Name; public IMyInventory Inv;
  public Dictionary<string, LQ> Q = new Dictionary<string, LQ>(SCI);
  public int Sat, Short, NoOp; public string Wait = "";
}
class DShip {
  public string Conn; public bool NoSort;
  public List<IMyInventory> Unload = new List<IMyInventory>();
  public List<LCon> Loads = new List<LCon>();
}
// Aggregated boundary policy per remote construct: never GetBlocks()-order dependent.
class DPol { public IMyShipConnector Rep; public bool Excl, NoSort; public string Conn = ""; }
List<DShip> _ships = new List<DShip>();
List<IMyShipConnector> _lconn = new List<IMyShipConnector>();
List<IMyInventory> _baseSrc = new List<IMyInventory>();
Dictionary<string, double> _dockSpent = DD(); // borrowed this cycle, across all ships
// LOADOUT BORROW/COMMITMENT RULE: physical stock the EXISTING queue needs, RECURSIVELY (not
// just direct ingredients). A docked loadout may never spend it. Rebuilt read-only each cycle.
Dictionary<string, double> _dockCommit = DD();
// LOADOUT IDENTITY LAYER, separate from the production alias system: name/SubtypeId -> raw
// type. Membership NEVER adds a Recipe/ItemDef and never puts an item in [Stock] or _onHand.
Dictionary<string, string> _loadAlias = DS();
int _dockCursor = 0; // round-robin continuation, in-memory only (Save stays empty)
int _dkToBase, _dkToLoad, _dkShort, _dkBlocked, _dkUnloadSrc, _dkLoadCons, _dkSeeded;
bool _dockMoved = false;
string _dkLastConn = "";
DateTime _cycleStartUtc;
int _lastInstructions = 0;
int _peakInstructions = 0;
string _lastPhase = "";
string _peakPhaseName = "";
string _diagParseError = "";
int _orgExamined, _orgOutOfOrder, _orgAttempts, _orgSucceeded, _orgFailed;
string _orgLastContainer = "", _orgLastItem = "";
int _orgLastSourceIndex = -1, _orgLastTargetIndex = -1;
bool _orgLastResult = false;
string _orgLastError = "";
List<MyInventoryItem> _itemsA = new List<MyInventoryItem>();
List<MyInventoryItem> _itemsB = new List<MyInventoryItem>();
List<MyProductionItem> _qbuf = new List<MyProductionItem>();
List<CI> _dSingles = new List<CI>(), _dMultis = new List<CI>(), _dRanked = new List<CI>();
List<IMyInventory> _catInvs = new List<IMyInventory>();
class MH {
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
  public bool NotWorking = false; // disabled/unpowered/damaged — evacuation is never a fix for this
}
Dictionary<long, MH> _machineHealth = new Dictionary<long, MH>();
public Program() {
  BuildKnowledgeBase();
  BuildTypeCategoryMap();
  if (string.IsNullOrWhiteSpace(Me.CustomData))
    WriteDefaultCustomData();
  LoadConfig();
  Runtime.UpdateFrequency = UpdateFrequency.Update10;
}
public void Save() { } // machine health is intentionally in-memory only
public void Main(string argument, UpdateType updateSource) {
  if ((updateSource & UpdateType.Update10) == 0) return;
  double dt = Runtime.TimeSinceLastRun.TotalSeconds;
  if (dt <= 0) dt = 0.1667;
  _elapsedSinceCycle += dt;
  // CONFIG SNAPSHOT INVARIANT: a Custom Data change is only ever applied at PHASE_IDLE, so
  // every phase of a cycle runs against one immutable Config.
  if (!string.Equals(_lastCustomDataSeen, Me.CustomData, StringComparison.Ordinal))
    _cfgDirty = true;
  if (_cyclePhase == PHASE_IDLE && _cfgDirty) { LoadConfig(); _cfgDirty = false; }
  if (_cyclePhase == PHASE_IDLE && _cfg.GeneralEnabled && _elapsedSinceCycle >= Math.Max(1, _cfg.UpdateSeconds))
    StartCycle();
  int ph = _cyclePhase;
  Echo("IOPM v" + VERSION + " | Phase=" + PN[ph] + (_cfgDirty ? " | config change pending (applies at Idle)" : "") +
    (_diagParseError != "" ? " | DIAG PARSE FAILED: " + _diagParseError : ""));
  if (ph != PHASE_IDLE) RunCyclePhase();
  _lastInstructions = Runtime.CurrentInstructionCount;
  _lastPhase = PN[ph];
  if (_lastInstructions > _peakInstructions) { _peakInstructions = _lastInstructions; _peakPhaseName = _lastPhase; }
}
void StartCycle() {
  _cycleLog.Clear();
  _warnings.Clear();
  _unknownBlueprints.Clear();
  _noMachines.Clear();
  _unknownItems.Clear();
  _elapsedSinceCycle = 0;
  _cycleStartUtc = DateTime.UtcNow;
  _cyclePhase = PHASE_DISCOVER;
}
void RunCyclePhase() {
  switch (_cyclePhase) {
    case PHASE_DISCOVER: Discover();
      _cyclePhase = PHASE_SORTING;
      break;
    case PHASE_SORTING:
      // MaxTransfersPerCycle governs the SORTING phase only, exactly as v2.4.0. DockService has
      // its OWN slice in its own phase, so a dock never shrinks sorting; a full cycle may then
      // exceed MaxTransfersPerCycle, which is safe: instructions are bounded per phase.
      _cycleRecoveryReserve = Math.Min(_cfg.MaxTransfersPerCycle, Math.Max(2, _cfg.MaxTransfersPerCycle / 4));
      _cycleSortingLeftover = Math.Max(0, _cfg.MaxTransfersPerCycle - _cycleRecoveryReserve);
      if (_cfg.SortingEnabled)
        _cycleSortingLeftover = RunSorting(_cycleSortingLeftover);
      _cyclePhase = PHASE_SCAN;
      break;
    case PHASE_SCAN: ScanInventory();
      ScanQueues();
      _cyclePhase = PHASE_STALL;
      break;
    case PHASE_STALL: if (_cfg.ProductionEnabled && _cfg.InputRecoveryEnabled) {
        if (RunStallRecovery(_cycleRecoveryReserve + _cycleSortingLeftover)) ScanInventory();
      }
      _cyclePhase = PHASE_DOCKSCAN;
      break;
    case PHASE_DOCKSCAN:
      // Read-only: enumerate docks, parse GOAT [Stock], compute the queue commitment ledger.
      DockDiscover();
      PreReserveQueueSupport();
      _cyclePhase = PHASE_DOCKSERVICE;
      break;
    case PHASE_DOCKSERVICE: DockService(); // physical transfers only — never a queue mutation
      _cyclePhase = PHASE_RESCAN;
      break;
    case PHASE_RESCAN:
      // ORDERING INVARIANT: dock before BuildPlan; BuildPlan never sees pre-dock numbers.
      if (_dockMoved) ScanInventory();
      _cyclePhase = PHASE_BUILDPLAN;
      break;
    case PHASE_BUILDPLAN: if (_cfg.ProductionEnabled) BuildPlan();
      _cyclePhase = PHASE_APPLYPLAN;
      break;
    case PHASE_APPLYPLAN: if (_cfg.ProductionEnabled) ApplyPlan(); // AddQueueItem() remains the sole queue mutation
      _cyclePhase = PHASE_DIAGNOSTICS;
      break;
    case PHASE_DIAGNOSTICS: WriteDiagnostics();
      _cyclePhase = PHASE_STATUSRENDER;
      break;
    case PHASE_STATUSRENDER: RenderStatusScreens();
      _cyclePhase = PHASE_STOCKRENDER;
      break;
    case PHASE_STOCKRENDER: RenderStockScreens();
      _lastCycleSeconds = (DateTime.UtcNow - _cycleStartUtc).TotalSeconds;
      _cyclePhase = PHASE_IDLE; // cycle complete; next one (and any deferred config) may start
      break;
  }
}
void LoadConfig() {
  _configErrors.Clear();
  _lastCustomDataSeen = Me.CustomData;
  _bpReverse = null; // overrides may have changed
  MyIniParseResult result;
  if (!_ini.TryParse(Me.CustomData, out result)) {
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
  c.ProductionEnabled = _ini.Get("Production", "Enabled").ToBoolean(false);
  c.AllowSurvivalKitFallback = _ini.Get("Production", "AllowSurvivalKitFallback").ToBoolean(false);
  c.StallDetectionCycles = _ini.Get("Production", "StallDetectionCycles").ToInt32(5);
  c.InputRecoveryEnabled = _ini.Get("Production", "InputRecoveryEnabled").ToBoolean(true);
  c.RecoveryCooldownCycles = _ini.Get("Production", "RecoveryCooldownCycles").ToInt32(6);
  c.DockEnabled = _ini.Get("Docking", "Enabled").ToBoolean(true);
  c.UnloadDocked = _ini.Get("Docking", "UnloadDocked").ToBoolean(true);
  c.ServiceLoadouts = _ini.Get("Docking", "ServiceLoadouts").ToBoolean(true);
  c.UnloadConnInv = _ini.Get("Docking", "UnloadConnectorInventory").ToBoolean(true);
  c.UnloadCargo = _ini.Get("Docking", "UnloadCargo").ToBoolean(true);
  c.UnloadDrills = _ini.Get("Docking", "UnloadDrills").ToBoolean(true);
  c.BorrowPercent = Math.Max(0, Math.Min(100, _ini.Get("Docking", "LoadoutBorrowPercent").ToDouble(25)));
  c.SeedLoadoutTemplate = _ini.Get("Docking", "SeedLoadoutTemplate").ToBoolean(true);
  c.ManageFonts = _ini.Get("Display", "ManageFonts").ToBoolean(true);
  c.StatusFontSize = _ini.Get("Display", "StatusFontSize").ToDouble(0.8);
  c.StockFontSize = _ini.Get("Display", "StockFontSize").ToDouble(0.8);
  c.StockRowsPerPage = Math.Max(0, _ini.Get("Display", "StockRowsPerPage").ToInt32(0));
  List<MyIniKey> keys = new List<MyIniKey>();
  _ini.GetKeys("Stock", keys);
  for (int i = 0; i < keys.Count; i++) {
    string rawName = keys[i].Name;
    string alias = Canon(rawName);
    if (!_items.ContainsKey(alias)) { _configErrors.Add("Unknown [Stock] item: " + rawName); continue; }
    double value;
    if (!double.TryParse(_ini.Get("Stock", rawName).ToString(), out value) || value < 0) {
      _configErrors.Add("Invalid stock target: " + rawName);
      continue;
    }
    c.StockTargets[alias] = value;
  }
  keys.Clear();
  _ini.GetKeys("BlueprintOverrides", keys);
  for (int i = 0; i < keys.Count; i++) {
    string alias = Canon(keys[i].Name);
    string bp = _ini.Get("BlueprintOverrides", keys[i].Name).ToString("").Trim();
    if (_items.ContainsKey(alias) && bp != "") c.BlueprintOverrides[alias] = bp;
  }
  _cfg = c; // single atomic swap; never mutated in place
}
// Production defaults OFF: the player validates warehouse tags before anything is queued.
void WriteDefaultCustomData() {
  Me.CustomData =
"[General]\nEnabled=true\nUpdateSeconds=5\n" +
"[Sorting]\nEnabled=true\nBalance=true\nBalanceTolerancePercent=5\nMaxTransfersPerCycle=25\nOverflowPolicy=Overflow\n" +
"[Production]\nEnabled=false\nAllowSurvivalKitFallback=false\nStallDetectionCycles=5\nInputRecoveryEnabled=true\nRecoveryCooldownCycles=6\n" +
"[Docking]\nEnabled=true\nUnloadDocked=true\nServiceLoadouts=true\nLoadoutBorrowPercent=25\nUnloadConnectorInventory=true\nUnloadCargo=true\nUnloadDrills=true\nSeedLoadoutTemplate=true\n" +
"[Display]\nManageFonts=true\nStatusFontSize=0.8\nStockFontSize=0.8\nStockRowsPerPage=0\n" +
"[Stock]\nSteelPlate=10000\nConstruction=5000\nMotor=1000\n" +
"[BlueprintOverrides]\nExampleItem=\n";
}
bool CIC(string haystack, string needle) {
  if (haystack == null || needle == null) return false;
  return haystack.IndexOf(needle, OIC) >= 0;
}
bool AnyTag(string name, string[] tags) {
  for (int i = 0; i < tags.Length; i++) if (CIC(name, tags[i])) return true;
  return false;
}
HashSet<string> ParseCategories(string name) {
  HashSet<string> found = HS();
  if (string.IsNullOrEmpty(name)) return found;
  string cur = "";
  for (int i = 0; i <= name.Length; i++) {
    char ch = i < name.Length ? name[i] : ' ';
    if (i < name.Length && char.IsLetterOrDigit(ch)) { cur += ch; continue; }
    if (cur.Length > 0) {
      for (int w = 0; w < CATS.Length; w++)
        if (cur.Equals(CATS[w], OIC)) { found.Add(CATS[w]); break; }
      cur = "";
    }
  }
  return found;
}
bool IsProtectedFromSorting(IMyTerminalBlock b) {
  return b is IMyShipWelder || b is IMyShipGrinder || b is IMyShipDrill || b is IMyReactor
    || b is IMyUserControllableGun || b is IMyCockpit || b is IMyShipConnector;
}
void Discover() {
  _pools.Clear();
  _cons.Clear();
  _invCon.Clear();
  _plainInv.Clear();
  _ignInv.Clear();
  _statScr.Clear();
  _fallbackScr = false;
  _stockScr.Clear();
  _mach.Clear();
  _mOut.Clear();
  _mIn.Clear();
  _refOut.Clear();
  _invOwner.Clear();
  _lconn.Clear();
  _baseSrc.Clear();
  for (int i = 0; i < CATS.Length; i++) _pools[CATS[i]] = new List<CI>();
  List<IMyTerminalBlock> all = new List<IMyTerminalBlock>();
  GridTerminalSystem.GetBlocks(all);
  for (int i = 0; i < all.Count; i++) {
    IMyTerminalBlock b = all[i];
    if (b == null || !b.IsSameConstructAs(Me)) continue;
    IMyTextSurfaceProvider tsp = b as IMyTextSurfaceProvider;
    if (tsp != null && tsp.SurfaceCount > 0) {
      if (AnyTag(b.CustomName, STATUS_TAGS)) _statScr.Add(tsp.GetSurface(0));
      if (CIC(b.CustomName, STOCK_TAG)) _stockScr.Add(tsp.GetSurface(0));
    }
    // LOCAL connectors: ALWAYS dock anchors regardless of config; transfer SOURCES only.
    // UnloadConnectorInventory=false gates inventory routing ONLY, never dock discovery.
    IMyShipConnector lc = b as IMyShipConnector;
    if (lc != null) {
      _lconn.Add(lc);
      IMyInventory linv = b.HasInventory ? b.GetInventory(0) : null;
      if (linv != null && _cfg.UnloadConnInv && !AnyTag(b.CustomName, IGNORE_TAGS))
      { _invOwner[linv] = b.CustomName; _plainInv.Add(linv); }
      continue;
    }
    if (!b.HasInventory) continue;
    IMyProductionBlock pb = b as IMyProductionBlock;
    if (pb != null) { ClassifyProductionBlock(pb); continue; }
    if (IsProtectedFromSorting(b)) continue;
    if (!(b is IMyCargoContainer)) continue;
    IMyInventory inv = b.GetInventory(0);
    if (inv == null) continue;
    _invOwner[inv] = b.CustomName;
    if (AnyTag(b.CustomName, IGNORE_TAGS)) { _ignInv.Add(inv); continue; }
    HashSet<string> categories = ParseCategories(b.CustomName);
    if (categories.Count == 0) { _plainInv.Add(inv); continue; }
    if (categories.Contains(OVF) && categories.Count > 1) {
      List<string> otherTags = new List<string>();
      foreach (string cat in categories) if (!cat.Equals(OVF, OIC)) otherTags.Add(cat);
      Warn("InvalidContainerTags: " + b.CustomName + " combines Overflow with " +
        string.Join(",", otherTags) + " — treated as Overflow only");
      categories = HS();
      categories.Add(OVF);
    }
    CI ci = new CI { Block = b, Inventory = inv, Categories = categories };
    _cons.Add(ci); // exactly once per physical container
    _invCon[inv] = ci;
    foreach (string cat in categories) {
      List<CI> pool;
      if (!_pools.TryGetValue(cat, out pool)) { pool = new List<CI>(); _pools[cat] = pool; }
      pool.Add(ci); // same CI instance referenced by every tag's pool
    }
  }
  if (_statScr.Count == 0) { _statScr.Add(Me.GetSurface(0)); _fallbackScr = true; }
  for (int i = 0; i < _cons.Count; i++)
    if (!_ignInv.Contains(_cons[i].Inventory)) _baseSrc.Add(_cons[i].Inventory);
  _baseSrc.AddRange(_plainInv);
  _baseSrc.AddRange(_mOut);
}
void ClassifyProductionBlock(IMyProductionBlock pb) {
  IMyInventory outInv = pb.OutputInventory;
  IMyInventory inInv = pb.InputInventory;
  if (pb is IMyRefinery) {
    if (outInv != null) { _refOut.Add(outInv); _invOwner[outInv] = pb.CustomName; }
    return;
  }
  if (AnyTag(pb.CustomName, IGNORE_TAGS)) return; // [IOPM-Ignore]: fully invisible to IOPM
  if (outInv != null) { _mOut.Add(outInv); _invOwner[outInv] = pb.CustomName; } // always a sorting source
  IMyAssembler asm = pb as IMyAssembler;
  if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) return;
  _mach.Add(pb);
  if (inInv != null) { _mIn.Add(inInv); _invOwner[inInv] = pb.CustomName; }
}
void BuildTypeCategoryMap() {
  _typeCategory.Clear();
  AddPairs(_typeCategory, "Ore=Ores,Ingot=Ingots,Component=Components,AmmoMagazine=Ammo," +
    "OxygenContainerObject=Tools,GasContainerObject=Tools,PhysicalGunObject=Tools," +
    "PhysicalObject=Tools,ConsumableItem=Consumables,Datapad=Misc,SeedItem=Seeds,Package=Misc", "MyObjectBuilder_");
  _subCat.Clear();
  AddPairs(_subCat, "Grain=Consumables,Algae=Consumables,SpaceCredit=Misc", "");
}
void AddPairs(Dictionary<string, string> d, string list, string keyPrefix) {
  string[] e = list.Split(',');
  for (int i = 0; i < e.Length; i++) {
    int eq = e[i].IndexOf('=');
    d[keyPrefix + e[i].Substring(0, eq)] = e[i].Substring(eq + 1);
  }
}
static readonly string[] PROCESS_ITEM_SUBTYPES = new string[] { "Heat" };
bool IsProcessItem(MyItemType type) {
  for (int i = 0; i < PROCESS_ITEM_SUBTYPES.Length; i++)
    if (string.Equals(type.SubtypeId, PROCESS_ITEM_SUBTYPES[i], OIC)) return true;
  return false;
}
bool IsFractionalAllowedTypeId(string typeId) {
  return string.Equals(typeId, "MyObjectBuilder_Ore", OIC) || string.Equals(typeId, "MyObjectBuilder_Ingot", OIC);
}
bool RequiresIntegralAmount(MyItemType type) { return !IsFractionalAllowedTypeId(type.TypeId); }
string ClassifyItem(MyItemType type, out bool isProcessItem) {
  isProcessItem = IsProcessItem(type);
  return isProcessItem ? null : CategoryForItem(type);
}
string CategoryForItem(MyItemType type) {
  string cat;
  if (_subCat.TryGetValue(type.SubtypeId, out cat)) return cat;
  return CategoryForType(type.TypeId);
}
string CategoryForType(string typeIdWithPrefix) {
  string cat;
  if (_typeCategory.TryGetValue(typeIdWithPrefix, out cat)) return cat;
  return null;
}
string RawItemType(MyItemType type) { return type.TypeId + "/" + type.SubtypeId; }
string OwnerName(IMyInventory inv) {
  string name;
  return inv != null && _invOwner.TryGetValue(inv, out name) ? name : "Unknown";
}
string ItemDisplayName(MyItemType type) {
  string alias = AliasFromType(type);
  return alias != null ? Friendly(alias) : type.SubtypeId.ToString();
}
string AliasFromRawType(string full) {
  string alias;
  return _typeAlias.TryGetValue(full, out alias) ? alias : null;
}
string AliasFromType(MyItemType type) { return AliasFromRawType(RawItemType(type)); }
List<CI> PoolFor(string category) {
  List<CI> pool;
  if (_pools.TryGetValue(category, out pool)) return pool;
  return new List<CI>();
}
double FreeVolume(IMyInventory inv) {
  double free = (double)(inv.MaxVolume - inv.CurrentVolume);
  return free > 0 ? free : 0;
}
bool TryTransferItem(IMyInventory src, IMyInventory dest, MyInventoryItem item, MyFixedPoint requested, out MyFixedPoint moved, out bool hadRoom) {
  moved = (MyFixedPoint)0;
  hadRoom = false;
  if (requested <= 0) return false;
  if (ReferenceEquals(src, dest)) return false; // never transfer an inventory to itself
  MyFixedPoint safe = requested;
  bool fullyFits = false;
  try { fullyFits = dest.CanItemsBeAdded(requested, item.Type); } catch { fullyFits = false; }
  if (!fullyFits) {
    double lo = 0, hi = (double)requested;
    for (int iter = 0; iter < 30 && hi - lo > 0.0005; iter++) {
      double mid = (lo + hi) / 2.0;
      bool canFit = false;
      try { canFit = dest.CanItemsBeAdded((MyFixedPoint)mid, item.Type); } catch { canFit = false; }
      if (canFit) lo = mid; else hi = mid;
    }
    if (RequiresIntegralAmount(item.Type)) lo = Math.Floor(lo);
    safe = (MyFixedPoint)lo;
  }
  if (safe <= 0) return false;
  hadRoom = true;
  bool ok = false;
  try { ok = src.TransferItemTo(dest, item, safe); } catch { ok = false; }
  if (ok) moved = safe;
  return ok;
}
List<CI> BestDestinationCandidates(string category, out bool anyCandidateExists) {
  anyCandidateExists = false;
  List<CI> pool = PoolFor(category);
  _dSingles.Clear(); _dMultis.Clear(); _dRanked.Clear();
  for (int i = 0; i < pool.Count; i++) {
    CI ci = pool[i];
    if (_ignInv.Contains(ci.Inventory)) continue;
    anyCandidateExists = true;
    if (FreeVolume(ci.Inventory) <= 0.0001) continue;
    if (ci.IsSingleCategory) _dSingles.Add(ci); else _dMultis.Add(ci);
  }
  // Inline lambda, NOT a cached comparator field - the PB whitelist rejects naming the
  // generic comparison delegate type in source. Non-capturing, so no per-call alloc.
  _dSingles.Sort((a, b) => CmpFreeStatic(a, b));
  _dMultis.Sort((a, b) => CmpFreeStatic(a, b));
  _dRanked.AddRange(_dSingles);
  _dRanked.AddRange(_dMultis);
  return _dRanked;
}
static int CmpFreeStatic(CI a, CI b) {
  double fa = (double)(a.Inventory.MaxVolume - a.Inventory.CurrentVolume);
  double fb = (double)(b.Inventory.MaxVolume - b.Inventory.CurrentVolume);
  if (fa < 0) fa = 0;
  if (fb < 0) fb = 0;
  return fb.CompareTo(fa); // most free first
}
bool TryRouteToCategory(IMyInventory src, MyInventoryItem item, string category, MyFixedPoint want, out MyFixedPoint moved, out CI usedDest, out bool categoryExists, out bool categoryHasRoom) {
  moved = (MyFixedPoint)0;
  usedDest = null;
  List<CI> candidates = BestDestinationCandidates(category, out categoryExists);
  categoryHasRoom = candidates.Count > 0;
  for (int i = 0; i < candidates.Count; i++) {
    CI dest = candidates[i];
    if (ReferenceEquals(dest.Inventory, src)) continue; // never route Overflow back into itself
    MyFixedPoint m; bool hadRoom;
    if (TryTransferItem(src, dest.Inventory, item, want, out m, out hadRoom) && m > 0)
    { moved = m; usedDest = dest; return true; }
    if (hadRoom)
      Warn("Transfer failed: " + ItemDisplayName(item.Type) + " | From=" + OwnerName(src) +
        " | To=" + dest.Block.CustomName + " | Category=" + category + " — trying next container");
  }
  return false;
}
bool RouteItem(IMyInventory src, MyInventoryItem item, string category, out MyFixedPoint moved) {
  bool categoryExists, categoryHasRoom;
  CI usedDest;
  if (TryRouteToCategory(src, item, category, item.Amount, out moved, out usedDest, out categoryExists, out categoryHasRoom))
    return true;
  bool overflowExists = false, overflowHasRoom = false, overflowOk = false;
  if (string.Equals(_cfg.OverflowPolicy, "Overflow", OIC) && !category.Equals(OVF, OIC)) {
    CI overflowDest;
    overflowOk = TryRouteToCategory(src, item, OVF, item.Amount, out moved, out overflowDest, out overflowExists, out overflowHasRoom);
  }
  if (overflowOk) {
    Warn(!categoryExists ? "No " + category + " container — routed to Overflow"
              : category + " pool full/unreachable — using Overflow");
    return true;
  }
  if (!categoryExists) Warn("No " + category + " container");
  else if (!overflowExists) Warn(category + " pool full/unreachable, and no Overflow container — item left in place");
  else Warn(category + " pool full/unreachable, and Overflow full/unreachable — item left in place");
  return false;
}
int RunSorting(int tb) {
  if (tb <= 0) return 0;
  // PRIORITY ORDER (production output is evacuated FIRST, never starved by cosmetics):
  // 1 machine outputs, 2 refinery outputs (evacuation only — queues/inputs never touched),
  // 3 connector/plain, 4 misroute correction, 5 Overflow drain, 6 balance, 7 organize.
  tb = RouteSources(_mOut, tb, false);
  tb = RouteSources(_refOut, tb, false);
  tb = RouteSources(_plainInv, tb, false);
  _catInvs.Clear();
  for (int i = 0; i < _cons.Count; i++) {
    CI ci = _cons[i];
    if (_ignInv.Contains(ci.Inventory)) continue;
    if (ci.Categories.Contains(OVF)) continue;
    _catInvs.Add(ci.Inventory);
  }
  tb = RouteSources(_catInvs, tb, true);
  if (tb > 0) tb = DrainOverflow(tb);
  if (_cfg.BalanceEnabled && tb > 0) tb = BalancePools(tb);
  if (tb > 0) tb = OrganizeInventories(tb);
  return tb;
}
int OrganizeInventories(int tb) {
  _orgExamined = 0; _orgOutOfOrder = 0; _orgAttempts = 0; _orgSucceeded = 0; _orgFailed = 0;
  for (int c = 0; c < _cons.Count && tb > 0; c++) {
    CI ci = _cons[c];
    if (_ignInv.Contains(ci.Inventory)) continue;
    if (ci.Categories.Contains(OVF)) continue; // Overflow: no cosmetic ordering
    List<MyInventoryItem> items = _itemsA;
    items.Clear();
    ci.Inventory.GetItems(items);
    if (items.Count < 2) continue;
    bool hasProcessItem = false;
    for (int k = 0; k < items.Count; k++)
      if (IsProcessItem(items[k].Type)) { hasProcessItem = true; break; }
    if (hasProcessItem) continue; // process resource present — leave this container's slots untouched
    _orgExamined++;
    bool multi = !ci.IsSingleCategory;
    int n = items.Count;
    int[] catRank = new int[n];
    string[] dispName = new string[n];
    for (int k = 0; k < n; k++) {
      try {
        catRank[k] = multi ? CategoryRank(items[k].Type) : 0;
        dispName[k] = ItemDisplayName(items[k].Type) ?? RawItemType(items[k].Type) ?? "";
      } catch (Exception ex) {
        catRank[k] = int.MaxValue; // sort unresolvable items last, deterministically
        dispName[k] = "";
        _orgLastError = ci.Block.CustomName + " | " + RawItemType(items[k].Type) + " | " + ex.Message;
        Warn("Organize key computation failed: " + _orgLastError);
      }
    }
    List<int> order = new List<int>(n);
    for (int k = 0; k < n; k++) order.Add(k);
    order.Sort((a, b) => {
      if (catRank[a] != catRank[b]) return catRank[a].CompareTo(catRank[b]);
      int nameCmp = string.Compare(dispName[a], dispName[b], OIC);
      return nameCmp != 0 ? nameCmp : a.CompareTo(b); // stable: duplicates keep relative order
    });
    int mismatchAt = -1;
    for (int k = 0; k < order.Count; k++) if (order[k] != k) { mismatchAt = k; break; }
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
    if (ok) {
      tb--;
      _cycleLog.Add("ORGANIZE " + _orgLastItem + " in " + ci.Block.CustomName);
    }
  }
  return tb;
}
int CategoryRank(MyItemType type) {
  string cat = CategoryForItem(type);
  if (cat == null) return CATS.Length;
  int idx = Array.IndexOf(CATS, cat);
  return idx < 0 ? CATS.Length : idx;
}
HashSet<string> CatsOf(IMyInventory inv) {
  CI ci;
  return _invCon.TryGetValue(inv, out ci) ? ci.Categories : null;
}
int RouteSources(List<IMyInventory> sources, int tb, bool ownCats) {
  for (int s = 0; s < sources.Count && tb > 0; s++) {
    IMyInventory src = sources[s];
    if (src == null || _ignInv.Contains(src)) continue;
    HashSet<string> cats = ownCats ? CatsOf(src) : null;
    bool movedAny = true;
    while (movedAny && tb > 0) {
      movedAny = false;
      _itemsA.Clear();
      src.GetItems(_itemsA);
      for (int i = 0; i < _itemsA.Count; i++) {
        string typeKey = RawItemType(_itemsA[i].Type);
        bool isProcessItem;
        string category = ClassifyItem(_itemsA[i].Type, out isProcessItem);
        if (isProcessItem) continue; // IO process resource — never warehouse stock
        if (category == null) { RecordUnknownItem(typeKey, "UnknownItemType"); continue; }
        if (cats != null && cats.Contains(category)) continue; // already correctly placed
        MyFixedPoint moved;
        if (RouteItem(src, _itemsA[i], category, out moved)) {
          tb--;
          movedAny = true;
          string qualifier = moved < _itemsA[i].Amount ? " (partial, remainder stays/routes next pass)" : "";
          _cycleLog.Add("SORT " + (AliasFromRawType(typeKey) ?? typeKey) + qualifier);
          break; // items list is now stale; refetch
        }
      }
    }
  }
  return tb;
}
string GroupKey(HashSet<string> categories) {
  List<string> sorted = new List<string>(categories);
  sorted.Sort(SCI);
  return string.Join("+", sorted);
}
int BalancePools(int tb) {
  Dictionary<string, List<CI>> groups = new Dictionary<string, List<CI>>(SCI);
  for (int i = 0; i < _cons.Count; i++) {
    CI ci = _cons[i];
    if (_ignInv.Contains(ci.Inventory)) continue;
    string key = GroupKey(ci.Categories);
    List<CI> list;
    if (!groups.TryGetValue(key, out list)) { list = new List<CI>(); groups[key] = list; }
    list.Add(ci);
  }
  List<string> groupKeys = new List<string>(groups.Keys);
  groupKeys.Sort(SCI); // deterministic pass order
  for (int g = 0; g < groupKeys.Count && tb > 0; g++) {
    List<CI> eligible = groups[groupKeys[g]];
    if (eligible.Count < 2) continue;
    HashSet<string> groupCategories = eligible[0].Categories; // identical across the group by construction
    double totalCapacity = 0;
    for (int i = 0; i < eligible.Count; i++) totalCapacity += (double)eligible[i].Inventory.MaxVolume;
    if (totalCapacity <= 0) continue;
    Dictionary<string, double> perItemTotal = DD();
    Dictionary<string, Dictionary<int, double>> perItemPerContainer = new Dictionary<string, Dictionary<int, double>>(SCI);
    for (int i = 0; i < eligible.Count; i++) {
      _itemsA.Clear();
      eligible[i].Inventory.GetItems(_itemsA);
      for (int k = 0; k < _itemsA.Count; k++) {
        if (IsProcessItem(_itemsA[k].Type)) continue; // IO process resource — never balanced
        string itemCategory = CategoryForItem(_itemsA[k].Type);
        if (itemCategory == null || !groupCategories.Contains(itemCategory)) continue;
        string key = RawItemType(_itemsA[k].Type);
        double amt = (double)_itemsA[k].Amount;
        double old;
        perItemTotal[key] = (perItemTotal.TryGetValue(key, out old) ? old : 0) + amt;
        Dictionary<int, double> perContainer;
        if (!perItemPerContainer.TryGetValue(key, out perContainer))
        { perContainer = new Dictionary<int, double>(); perItemPerContainer[key] = perContainer; }
        double oldc;
        perContainer[i] = (perContainer.TryGetValue(i, out oldc) ? oldc : 0) + amt;
      }
    }
    foreach (var itemKv in perItemTotal) {
      if (tb <= 0) break;
      string typeKey = itemKv.Key;
      double total = itemKv.Value;
      if (total <= 0.0001) continue;
      Dictionary<int, double> perContainer = perItemPerContainer[typeKey];
      int slash = typeKey.IndexOf('/');
      bool integral = slash <= 0 || !IsFractionalAllowedTypeId(typeKey.Substring(0, slash));
      double[] targetShare = new double[eligible.Count];
      if (integral) {
        int totalInt = (int)Math.Round(total);
        int[] baseShare = new int[eligible.Count];
        double[] raw = new double[eligible.Count];
        int assigned = 0;
        for (int i = 0; i < eligible.Count; i++) {
          raw[i] = totalInt * ((double)eligible[i].Inventory.MaxVolume / totalCapacity);
          baseShare[i] = (int)Math.Floor(raw[i]);
          assigned += baseShare[i];
        }
        int remainder = totalInt - assigned;
        List<int> byRemainder = new List<int>();
        for (int i = 0; i < eligible.Count; i++) byRemainder.Add(i);
        byRemainder.Sort((a, b) => {
          int cmp = (raw[b] - baseShare[b]).CompareTo(raw[a] - baseShare[a]);
          return cmp != 0 ? cmp : a.CompareTo(b); // deterministic tie-break
        });
        for (int r = 0; r < remainder && r < byRemainder.Count; r++) baseShare[byRemainder[r]]++;
        for (int i = 0; i < eligible.Count; i++) targetShare[i] = baseShare[i];
      } else {
        for (int i = 0; i < eligible.Count; i++)
          targetShare[i] = total * ((double)eligible[i].Inventory.MaxVolume / totalCapacity);
      }
      int overIdx = -1; double overExcess = 0;
      List<KeyValuePair<int, double>> underCandidates = new List<KeyValuePair<int, double>>();
      for (int i = 0; i < eligible.Count; i++) {
        double have;
        perContainer.TryGetValue(i, out have);
        double tolerance = targetShare[i] * (_cfg.BalanceTolerancePercent / 100.0);
        double diff = have - targetShare[i];
        if (diff > tolerance && diff > overExcess) { overExcess = diff; overIdx = i; }
        if (-diff > tolerance) underCandidates.Add(new KeyValuePair<int, double>(i, -diff));
      }
      if (overIdx < 0 || underCandidates.Count == 0) continue;
      int oi = overIdx;
      underCandidates.RemoveAll(kv => kv.Key == oi);
      if (underCandidates.Count == 0) continue;
      underCandidates.Sort((a, b) => b.Value.CompareTo(a.Value)); // most-deficient first
      _itemsB.Clear();
      eligible[overIdx].Inventory.GetItems(_itemsB);
      int srcItemIdx = -1;
      for (int k = 0; k < _itemsB.Count; k++)
        if (RawItemType(_itemsB[k].Type).Equals(typeKey, OIC)) { srcItemIdx = k; break; }
      if (srcItemIdx < 0) continue;
      for (int u = 0; u < underCandidates.Count; u++) {
        int underIdx = underCandidates[u].Key;
        double moveAmount = Math.Min(overExcess, underCandidates[u].Value);
        double finalAmount = Math.Min((double)_itemsB[srcItemIdx].Amount, moveAmount);
        if (integral) finalAmount = Math.Floor(finalAmount + 0.0001); // never move a fractional unit
        if (finalAmount <= (integral ? 0.9999 : 0.0001)) continue;
        MyFixedPoint moved; bool hadRoom;
        bool ok = TryTransferItem(eligible[overIdx].Inventory, eligible[underIdx].Inventory,
          _itemsB[srcItemIdx], (MyFixedPoint)finalAmount, out moved, out hadRoom);
        if (ok) {
          tb--;
          _cycleLog.Add("BALANCE " + typeKey + " " + eligible[overIdx].Block.CustomName +
            " -> " + eligible[underIdx].Block.CustomName);
          break;
        }
        if (hadRoom)
          Warn("Transfer failed: " + ItemDisplayName(_itemsB[srcItemIdx].Type) +
            " | From=" + eligible[overIdx].Block.CustomName + " | To=" + eligible[underIdx].Block.CustomName +
            " | Category=" + CategoryForItem(_itemsB[srcItemIdx].Type) + " — trying next container");
      }
    }
  }
  return tb;
}
int DrainOverflow(int tb) {
  List<CI> overflowPool = PoolFor(OVF);
  for (int i = 0; i < overflowPool.Count && tb > 0; i++) {
    CI of = overflowPool[i];
    if (_ignInv.Contains(of.Inventory)) continue;
    _itemsA.Clear();
    of.Inventory.GetItems(_itemsA);
    for (int k = 0; k < _itemsA.Count && tb > 0; k++) {
      if (IsProcessItem(_itemsA[k].Type)) continue; // IO process resource — never drained/routed
      string category = CategoryForItem(_itemsA[k].Type);
      if (category == null || category.Equals(OVF, OIC)) continue;
      MyFixedPoint moved; CI dest; bool exists, hasRoom;
      if (TryRouteToCategory(of.Inventory, _itemsA[k], category, _itemsA[k].Amount, out moved, out dest, out exists, out hasRoom)) {
        tb--;
        _cycleLog.Add("DRAIN-OVERFLOW " + RawItemType(_itemsA[k].Type) + " -> " + dest.Block.CustomName);
        break; // list stale, restart this container next outer pass
      }
    }
  }
  return tb;
}
void RecordUnknownItem(string typeKey, string reason) {
  for (int i = 0; i < _unknownItems.Count; i++)
    if (_unknownItems[i].ItemType == typeKey) return; // dedupe per cycle
  _unknownItems.Add(new URec { ItemType = typeKey, Reason = reason, SeenIn = "unknown" });
}
void Warn(string message) {
  if (!_warnings.Contains(message)) _warnings.Add(message);
}
void CountInventory(IMyInventory inv, Dictionary<string, double> totals) {
  if (inv == null) return;
  _itemsA.Clear();
  inv.GetItems(_itemsA);
  for (int i = 0; i < _itemsA.Count; i++) {
    string alias = AliasFromType(_itemsA[i].Type);
    if (alias == null) continue; // unknown items are reported during sorting, not accounting
    AddTo(totals, alias, (double)_itemsA[i].Amount);
  }
}
void ScanInventory() {
  _whStock.Clear();
  _ovStock.Clear();
  _moStock.Clear();
  _stagedStock.Clear();
  _onHand.Clear();
  for (int i = 0; i < _cons.Count; i++) {
    CI ci = _cons[i];
    if (_ignInv.Contains(ci.Inventory)) continue;
    CountInventory(ci.Inventory, ci.Categories.Contains(OVF) ? _ovStock : _whStock);
  }
  for (int i = 0; i < _plainInv.Count; i++) CountInventory(_plainInv[i], _whStock);
  for (int i = 0; i < _mOut.Count; i++) CountInventory(_mOut[i], _moStock);
  for (int i = 0; i < _mIn.Count; i++) CountInventory(_mIn[i], _stagedStock);
  foreach (var kv in _whStock) AddTo(_onHand, kv.Key, kv.Value);
  foreach (var kv in _ovStock) AddTo(_onHand, kv.Key, kv.Value);
  foreach (var kv in _moStock) AddTo(_onHand, kv.Key, kv.Value);
}
Dictionary<string, string> _bpReverse = null;
Dictionary<string, string> BlueprintReverseMap() {
  if (_bpReverse != null) return _bpReverse;
  Dictionary<string, string> map = DS();
  foreach (var kv in _items) {
    MyDefinitionId bp;
    if (!TryGetBlueprint(kv.Key, out bp)) continue;
    string key = bp.ToString();
    if (!map.ContainsKey(key)) map.Add(key, kv.Key);
  }
  _bpReverse = map;
  return map;
}
void ScanQueues() {
  _queuedJobs.Clear();
  _queuedOutput.Clear();
  Dictionary<string, string> reverse = BlueprintReverseMap();
  for (int i = 0; i < _mach.Count; i++) {
    IMyProductionBlock pb = _mach[i];
    IMyAssembler asm = pb as IMyAssembler;
    if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;
    _qbuf.Clear();
    pb.GetQueue(_qbuf); // READ ONLY — never mutated here
    for (int q = 0; q < _qbuf.Count; q++) {
      string alias;
      if (!reverse.TryGetValue(_qbuf[q].BlueprintId.ToString(), out alias)) continue;
      double jobs = (double)_qbuf[q].Amount;
      double yield = 1.0;
      Recipe r;
      if (_recipes.TryGetValue(alias, out r)) yield = r.Output;
      AddTo(_queuedJobs, alias, jobs);
      AddTo(_queuedOutput, alias, jobs * yield);
    }
  }
}
// ===== Dock-aware logistics =====
// _dockCommit via the SAME planner recursion BuildPlan uses (EnsureFeasible, support branch): a
// manual Motor queue short of Electromagnets also commits the Iron/Nickel/CopperWire behind it.
// Read-only; EnsureFeasible's depth/stack guards are reused, so this cannot self-block.
void PreReserveQueueSupport() {
  ClearPlanLedgers();
  _dockCommit.Clear();
  List<string> k = new List<string>(_queuedJobs.Keys); k.Sort(SCI);
  for (int r = 0; r < k.Count; r++) {
    double jobs = Get(_queuedJobs, k[r]); Recipe rec;
    if (jobs <= 0.0001 || !_recipes.TryGetValue(k[r], out rec)) continue;
    for (int i = 0; i < rec.Inputs.Count; i++)
      EnsureFeasible(Canon(rec.Inputs[i].Item), rec.Inputs[i].Amount * jobs, true, HS(), 0);
  }
  // Commit against the whole recursive tree. Already-queued output counts first; merely PLANNED
  // output deliberately does not, since it would be built from these same materials.
  List<string> d = new List<string>(_queueSupportDemand.Keys); d.Sort(SCI);
  for (int i = 0; i < d.Count; i++) {
    double need = EffectiveSupportTarget(d[i]) - Get(_queuedOutput, d[i]);
    if (need <= 0.0001) continue;
    double c = Math.Min(Get(_onHand, d[i]), need);
    if (c > 0.0001) _dockCommit[Canon(d[i])] = c;
  }
  ClearPlanLedgers(); // the shadow plan itself is scratch and is never applied
}
void ClearPlanLedgers() {
  _demand.Clear(); _queueSupportDemand.Clear(); _budget.Clear(); _supportReserved.Clear();
  _plannedOutput.Clear(); _plannedJobs.Clear(); _planOrderSet.Clear();
  _planOrder.Clear(); _planRecords.Clear();
}
// Directly-docked constructs only. GetBlocks() is NOT construct-scoped, so the
// IsSameConstructAs(remoteConnector) filter is mandatory. Uses IMyShipConnector.Status
// (MyShipConnectorStatus) and OtherConnector — the documented ingame members.
void DockDiscover() {
  _ships.Clear(); _dkUnloadSrc = 0; _dkLoadCons = 0; _dkSeeded = 0;
  if (!_cfg.DockEnabled || _lconn.Count == 0) return;
  List<DPol> pols = new List<DPol>();
  for (int c = 0; c < _lconn.Count; c++) {
    IMyShipConnector lc = _lconn[c], oc = null;
    try { if (lc.Status == MyShipConnectorStatus.Connected) oc = lc.OtherConnector; } catch { oc = null; }
    if (oc == null || oc.IsSameConstructAs(Me)) continue;
    DPol p = null;
    for (int i = 0; i < pols.Count; i++) if (pols[i].Rep.IsSameConstructAs(oc)) { p = pols[i]; break; }
    if (p == null) { p = new DPol { Rep = oc }; pols.Add(p); }
    if (p.Conn == "") p.Conn = lc.CustomName; // representative name for diagnostics only
    if (AnyTag(lc.CustomName, EXCL_TAGS) || AnyTag(oc.CustomName, EXCL_TAGS)) p.Excl = true;
    if (AnyTag(lc.CustomName, NOSORT_TAGS) || AnyTag(oc.CustomName, NOSORT_TAGS)) p.NoSort = true;
  }
  if (pols.Count == 0) return;
  List<IMyTerminalBlock> all = new List<IMyTerminalBlock>();
  GridTerminalSystem.GetBlocks(all);
  for (int c = 0; c < pols.Count; c++) {
    DPol p = pols[c];
    if (p.Excl) continue;
    DShip s = new DShip { Conn = p.Conn, NoSort = p.NoSort };
    for (int i = 0; i < all.Count; i++) {
      IMyTerminalBlock b = all[i];
      if (b == null || !b.HasInventory || b.IsSameConstructAs(Me) || !b.IsSameConstructAs(p.Rep)) continue;
      IMyInventory inv = b.GetInventory(0);
      if (inv == null) continue;
      // REMOTE TAG PRECEDENCE. 1: full block exclusion beats everything.
      if (AnyTag(b.CustomName, LOCK_TAGS)) continue;
      bool cargo = b is IMyCargoContainer;
      // 2: an explicit [Stock] container is a loadout, serviced even under a [No Sorting] boundary.
      if (cargo && AnyTag(b.CustomName, LOADOUT_TAGS)) {
        LCon lo = new LCon { Name = b.CustomName, Inv = inv };
        ParseGoatStock(b, lo);
        // ONLY when the block's Custom Data is completely empty: seed a template so quotas can
        // be edited on the block. Anything already there - GOAT's or the player's - is untouched.
        if (lo.Q.Count == 0 && _cfg.SeedLoadoutTemplate && string.IsNullOrWhiteSpace(b.CustomData)) {
          b.CustomData = LoadoutTemplate(inv);
          _dkSeeded++;
          ParseGoatStock(b, lo); // read back what we just wrote
        }
        _invOwner[inv] = b.CustomName; s.Loads.Add(lo); _dkLoadCons++;
        continue;
      }
      // 3: [No Sorting] on the boundary or on the block itself suppresses ordinary unload.
      if (s.NoSort || AnyTag(b.CustomName, NOSORT_TAGS)) continue;
      // 4: unload sources are EXACTLY cargo/connector/drill — reactors/guns/cockpits never.
      if (!((cargo && _cfg.UnloadCargo) || (b is IMyShipConnector && _cfg.UnloadConnInv)
        || (b is IMyShipDrill && _cfg.UnloadDrills))) continue;
      _invOwner[inv] = b.CustomName; s.Unload.Add(inv); _dkUnloadSrc++;
    }
    _ships.Add(s);
  }
}
bool IsMetaSection(string h) {
  return CIC(h, "Container") || CIC(h, "Setting") || CIC(h, "Modifier")
    || CIC(h, "Explanation") || CIC(h, "Pinned") || CIC(h, "Note");
}
bool IsSettingKey(string k) {
  return k.Equals("method", OIC) || k.Equals("rebalancePercentage", OIC) || k.Equals("name", OIC);
}
// Inert starting template for an empty [Stock] container: EVERY item IOPM can resolve by
// name - recipe aliases, the loadout alias table, and whatever the container holds - so the
// player picks from a full menu rather than a curated subset.
// Deduped by resolved raw type, one line per real item: an IOPM alias wins over a
// loadout-table name for the same item, so "SensorCluster" appears and "Detector" does not.
// Grouped by TypeId purely for readability. Group titles are checked against IsMetaSection -
// none of Components/Ingots/Ammo/Ores/Other collides, so the parser still reads the entries.
// NEVER title a group anything containing "Container": that IS a meta keyword and the whole
// group would be silently skipped.
// Process resources (Heat) are excluded; they must never enter a quota table.
// Every entry at "0M".
// The M (minimum) modifier is DELIBERATE and load-bearing. A bare "0" is an EXACT quota, which
// means "remove everything above 0" - seeding that would strip the container of every item
// listed. "0M" never adds (stock is never below 0) and never removes (the push branch skips
// M), so the template does nothing at all until a quantity is edited in.
// Written in GOAT's bounded format so the same parser reads it back and GOAT stays compatible.
string LoadoutTemplate(IMyInventory inv) {
  Dictionary<string, string> byType = DS(); // resolved raw type -> preferred name
  foreach (var kv in _items) if (!byType.ContainsKey(kv.Value.Type)) byType[kv.Value.Type] = kv.Key;
  foreach (var kv in _loadAlias) if (!byType.ContainsKey(kv.Value)) byType[kv.Value] = kv.Key;
  // Observed types, so ores and MODDED items make the menu. Sourced from the whole base, not
  // just this container: a container is seeded exactly when its Custom Data is empty, which is
  // usually also when it holds nothing, so container-only observation lists almost nothing.
  // Deliberately a LOCAL list, not a shared scratch buffer: this runs inside DockDiscover and
  // must not alias a buffer another pass is holding. Cost is paid only on a seed, which needs
  // an empty Custom Data and therefore happens approximately once per container, ever.
  List<MyInventoryItem> its = new List<MyInventoryItem>();
  for (int b = -1; b < _baseSrc.Count; b++) {
    IMyInventory src = b < 0 ? inv : _baseSrc[b];
    if (src == null || (b >= 0 && _ignInv.Contains(src))) continue;
    its.Clear();
    src.GetItems(its);
    for (int i = 0; i < its.Count; i++) {
      MyItemType t = its[i].Type;
      if (IsProcessItem(t)) continue;
      string raw = RawItemType(t);
      if (!byType.ContainsKey(raw)) byType[raw] = t.SubtypeId.ToString();
    }
  }
  List<string> comp = new List<string>(), ing = new List<string>(), amm = new List<string>(),
    ore = new List<string>(), oth = new List<string>();
  foreach (var kv in byType) {
    if (string.IsNullOrEmpty(kv.Value)) continue;
    string r = kv.Key;
    if (r.StartsWith("MyObjectBuilder_Component/", OIC)) comp.Add(kv.Value);
    else if (r.StartsWith("MyObjectBuilder_Ingot/", OIC)) ing.Add(kv.Value);
    else if (r.StartsWith("MyObjectBuilder_AmmoMagazine/", OIC)) amm.Add(kv.Value);
    else if (r.StartsWith("MyObjectBuilder_Ore/", OIC)) ore.Add(kv.Value);
    else oth.Add(kv.Value);
  }
  System.Text.StringBuilder sb = new System.Text.StringBuilder();
  sb.Append(GSTART).Append("\n");
  AppendGroup(sb, "Components", comp);
  AppendGroup(sb, "Ingots", ing);
  AppendGroup(sb, "Ammo", amm);
  AppendGroup(sb, "Ores", ore);
  AppendGroup(sb, "Other", oth);
  return sb.Append(GEND).Append("\n").ToString();
}
void AppendGroup(System.Text.StringBuilder sb, string title, List<string> a) {
  if (a.Count == 0) return;
  a.Sort((x, y) => string.Compare(x, y, OIC));
  sb.Append("~~~~~~~~ ").Append(title).Append(" ~~~~~~~~\n");
  for (int i = 0; i < a.Count; i++) sb.Append(a[i]).Append("=0M\n");
}
// READ-ONLY parse of the bounded @GOAT-Stock region; remote CustomData is NEVER written back.
// GOAT carries "~~~~ Heading ~~~~" subsections and '~' help lines whose examples look like
// definitions, so '~' lines never parse and Container List / Settings / Modifier Explanation /
// Pinned Items bodies are skipped.
void ParseGoatStock(IMyTerminalBlock b, LCon lo) {
  string cd = b.CustomData;
  if (string.IsNullOrEmpty(cd)) return;
  int s = cd.IndexOf(GSTART, OIC);
  if (s < 0) return;
  s = cd.IndexOf('\n', s);
  if (s < 0) return;
  int e = cd.IndexOf(GEND, s, OIC);
  if (e < 0) e = cd.Length;
  string[] lines = cd.Substring(s + 1, e - s - 1).Replace("\r", "").Split('\n');
  bool skip = false;
  int unresolved = 0;
  string firstBad = "";
  for (int i = 0; i < lines.Length; i++) {
    string t = lines[i].Trim();
    if (t.Length == 0) continue;
    if (t[0] == '~') {
      if (t.Length > 1 && t[1] == '~') skip = IsMetaSection(t.Trim('~', ' ', '\t'));
      continue;
    }
    if (t[0] == ';' || t[0] == '#' || t[0] == '[' || t[0] == '@' || t[0] == '>') continue;
    if (skip) continue;
    int eq = t.IndexOf('=');
    if (eq <= 0) continue;
    string raw = t.Substring(0, eq).Trim(), v = t.Substring(eq + 1).Trim();
    if (raw.Length == 0 || IsSettingKey(raw)) continue; // legitimate GOAT metadata, never a warning
    // 'P' is GOAT pinning metadata — stripped; the M/L/exact modifier it rides on is honoured.
    while (v.Length > 0 && (v[v.Length - 1] == 'P' || v[v.Length - 1] == 'p')) v = v.Substring(0, v.Length - 1);
    LQ q = new LQ { Mod = 'E' };
    if (v.Equals("All", OIC)) q.Mod = 'A';
    else {
      char last = v.Length > 0 ? char.ToUpperInvariant(v[v.Length - 1]) : ' ';
      if (last == 'M' || last == 'L') { q.Mod = last; v = v.Substring(0, v.Length - 1).Trim(); }
      if (!double.TryParse(v, out q.Amt) || q.Amt < 0)
      { Warn("Loadout quota invalid in " + b.CustomName + ": " + t); continue; }
    }
    q.Raw = ResolveLoadoutType(raw, lo.Inv);
    if (q.Raw == null) { unresolved++; if (firstBad == "") firstBad = raw; continue; } // untouched
    lo.Q[raw] = q;
  }
  if (unresolved > 0) // ONE compact diagnostic per container, never a per-line flood
    Warn("Loadout item unknown in " + b.CustomName + ": " + firstBad +
      (unresolved > 1 ? " (+" + (unresolved - 1) + " more)" : ""));
}
// LOADOUT RESOLUTION, priority order; resolving a name grants NO production status. A: IOPM
// alias/ItemDef -> raw type. D: explicit "MyObjectBuilder_X/Subtype". B: compact GOAT alias
// table (also keyed by bare SubtypeId). C: exact case-insensitive SubtypeId observed in the
// loadout container or approved LOCAL base sources. No fuzzy matching, no item database.
string ResolveLoadoutType(string raw, IMyInventory loadoutInv) {
  ItemDef def;
  if (_items.TryGetValue(Canon(raw), out def)) return def.Type;
  if (raw.IndexOf('/') > 0 && raw.StartsWith("MyObjectBuilder_", OIC)) return raw;
  string t;
  if (_loadAlias.TryGetValue(raw, out t)) return t;
  t = ObservedType(loadoutInv, raw);
  if (t != null) return t;
  for (int i = 0; i < _baseSrc.Count; i++) {
    t = ObservedType(_baseSrc[i], raw);
    if (t != null) return t;
  }
  return null;
}
string ObservedType(IMyInventory inv, string subtype) {
  if (inv == null || _ignInv.Contains(inv)) return null;
  _itemsA.Clear();
  inv.GetItems(_itemsA);
  for (int i = 0; i < _itemsA.Count; i++)
    if (string.Equals(_itemsA[i].Type.SubtypeId, subtype, OIC)) return RawItemType(_itemsA[i].Type);
  return null;
}
// GENERIC LOADOUT ACCOUNTING: any raw type across approved LOCAL base sources only, never
// remote inventory. Managed items reuse _onHand; loadout-only items are counted here and are
// NEVER inserted into _onHand, [Stock] or the stock LCD.
double BasePhysical(string rawType) {
  string alias = AliasFromRawType(rawType);
  if (alias != null) return Get(_onHand, alias);
  double total = 0;
  for (int i = 0; i < _baseSrc.Count; i++) {
    IMyInventory src = _baseSrc[i];
    if (src == null || _ignInv.Contains(src)) continue;
    total += InvAmount(src, rawType);
  }
  return total;
}
// Hard floor a loadout may never push base stock below. No [Stock] target => reserve 0.
double MinReserve(string alias) {
  double t = Get(_cfg.StockTargets, alias);
  return t <= 0 ? 0 : t * (1.0 - _cfg.BorrowPercent / 100.0);
}
int FindTyped(IMyInventory inv, string rawType) {
  double t;
  return ScanTyped(inv, rawType, out t);
}
double InvAmount(IMyInventory inv, string rawType) {
  double t;
  ScanTyped(inv, rawType, out t);
  return t;
}
int ScanTyped(IMyInventory inv, string rawType, out double total) {
  total = 0;
  int first = -1;
  _itemsA.Clear();
  inv.GetItems(_itemsA);
  for (int i = 0; i < _itemsA.Count; i++)
    if (RawItemType(_itemsA[i].Type).Equals(rawType, OIC)) {
      if (first < 0) first = i;
      total += (double)_itemsA[i].Amount;
    }
  return first;
}
void DockService() {
  _dkToBase = 0; _dkToLoad = 0; _dkShort = 0; _dkBlocked = 0;
  _dockMoved = false;
  _dockSpent.Clear();
  int n = _ships.Count;
  if (n == 0) return;
  // Own bounded slice, spent in its OWN phase rather than carved out of Sorting: with nothing
  // docked sorting capacity equals v2.4.0's, and one tick's unload is never unbounded.
  _cycleDockReserve = Math.Max(2, _cfg.MaxTransfersPerCycle / 4);
  int tb = _cycleDockReserve, start = ((_dockCursor % n) + n) % n; // deterministic round-robin
  for (int k = 0; k < n && tb > 0; k++) {
    DShip s = _ships[(start + k) % n];
    _dkLastConn = s.Conn;
    if (_cfg.ServiceLoadouts) tb = ServiceLoadouts(s, tb);
    if (_cfg.UnloadDocked && !s.NoSort) {
      int b0 = tb;
      tb = RouteSources(s.Unload, tb, false);
      if (b0 != tb) { _dkToBase += b0 - tb; _dockMoved = true; }
    }
  }
  _dockCursor++;
}
int ServiceLoadouts(DShip s, int tb) {
  for (int i = 0; i < s.Loads.Count && tb > 0; i++) {
    LCon lo = s.Loads[i];
    lo.Sat = 0; lo.Short = 0; lo.NoOp = 0; lo.Wait = "";
    foreach (var kv in lo.Q) {
      if (tb <= 0) break;
      LQ q = kv.Value;
      if (q.Raw == null) continue; // unresolved: diagnostic only, container left untouched
      double have = InvAmount(lo.Inv, q.Raw);
      if (q.Mod == 'A') {
        // "All" has no finite quota so it is never "short": satisfied only if something moved,
        // otherwise a distinct no-op state rather than a false claim of satisfaction.
        if (PullToLoadout(kv.Key, q.Raw, 1e9, lo.Inv, ref tb) > 0.0001) lo.Sat++; else lo.NoOp++;
        continue;
      }
      if (have < q.Amt - 0.0001 && q.Mod != 'L') {
        double got = PullToLoadout(kv.Key, q.Raw, q.Amt - have, lo.Inv, ref tb);
        if (have + got < q.Amt - 0.0001)
        { lo.Short++; _dkShort++; if (lo.Wait == "") lo.Wait = kv.Key; }
        else lo.Sat++;
      } else {
        if (have > q.Amt + 0.0001 && q.Mod != 'M') PushExcess(lo.Inv, kv.Key, q.Raw, have - q.Amt, ref tb);
        lo.Sat++;
      }
    }
  }
  return tb;
}
// BORROW RULE: available = physical less (a) stock recursively committed to the EXISTING queue,
// (b) the borrow-percent floor, (c) what earlier loadouts took this cycle. A loadout-only item
// has no alias, so no commitment and no floor. A shortage is ONLY reported, never a new root.
double PullToLoadout(string name, string rawType, double want, IMyInventory dest, ref int tb) {
  string alias = AliasFromRawType(rawType);
  string key = alias ?? rawType; // raw types contain '/', so they can never collide with an alias
  double avail = BasePhysical(rawType) - Get(_dockSpent, key);
  if (alias != null) avail -= Get(_dockCommit, alias) + MinReserve(alias);
  if (avail <= 0.0001) return 0;
  double take = Math.Min(want, avail), moved = 0;
  for (int i = 0; i < _baseSrc.Count && tb > 0 && take > 0.0001; i++) {
    IMyInventory src = _baseSrc[i];
    if (src == null || ReferenceEquals(src, dest) || _ignInv.Contains(src)) continue;
    int k = FindTyped(src, rawType);
    if (k < 0) continue;
    double amt = Math.Min(take, (double)_itemsA[k].Amount);
    if (RequiresIntegralAmount(_itemsA[k].Type)) amt = Math.Floor(amt);
    if (amt <= 0.0001) continue;
    MyFixedPoint mv; bool hadRoom;
    if (TryTransferItem(src, dest, _itemsA[k], (MyFixedPoint)amt, out mv, out hadRoom) && mv > 0) {
      tb--; moved += (double)mv; take -= (double)mv; _dkToLoad++; _dockMoved = true;
      _cycleLog.Add("DOCK-LOAD " + name + " -> " + OwnerName(dest));
    } else if (hadRoom) _dkBlocked++;
  }
  if (moved > 0) AddTo(_dockSpent, key, moved);
  return moved;
}
// Excess only becomes base inventory once it has PHYSICALLY moved into a base container.
bool PushExcess(IMyInventory src, string name, string rawType, double excess, ref int tb) {
  int i = FindTyped(src, rawType);
  if (i < 0 || tb <= 0) return false;
  string cat = CategoryForItem(_itemsA[i].Type);
  if (cat == null) { _dkBlocked++; return false; }
  double amt = Math.Min(excess, (double)_itemsA[i].Amount);
  if (RequiresIntegralAmount(_itemsA[i].Type)) amt = Math.Floor(amt);
  if (amt <= 0.0001) return false;
  MyFixedPoint mv; CI dst; bool ex, hr;
  bool ok = TryRouteToCategory(src, _itemsA[i], cat, (MyFixedPoint)amt, out mv, out dst, out ex, out hr);
  if (!ok && !cat.Equals(OVF, OIC))
    ok = TryRouteToCategory(src, _itemsA[i], OVF, (MyFixedPoint)amt, out mv, out dst, out ex, out hr);
  if (ok) { tb--; _dkToBase++; _dockMoved = true; _cycleLog.Add("DOCK-EXCESS " + name); return true; }
  _dkBlocked++;
  return false;
}
string QueueSignature(List<MyProductionItem> q) {
  if (q.Count == 0) return "";
  return q[0].BlueprintId.ToString() + "|" + ((double)q[0].Amount).ToString("0.####");
}
bool RunStallRecovery(int tb) {
  bool anyRecovery = false;
  for (int i = 0; i < _mach.Count; i++) {
    IMyProductionBlock pb = _mach[i];
    long id = pb.EntityId;
    MH h;
    if (!_machineHealth.TryGetValue(id, out h)) {
      h = new MH { EntityId = id, Name = pb.CustomName };
      _machineHealth[id] = h;
    }
    h.Name = pb.CustomName;
    if (h.CooldownRemaining > 0) {
      h.CooldownRemaining--;
      if (h.CooldownRemaining == 0) { h.Recovering = false; h.StallCycles = 0; }
      continue;
    }
    _qbuf.Clear();
    try { pb.GetQueue(_qbuf); } catch { _qbuf.Clear(); }
    bool functional = pb.IsFunctional && pb.IsWorking;
    if (_qbuf.Count == 0) {
      h.StallCycles = 0;
      h.Recovering = false;
      h.NotWorking = false;
      h.LastQueueSignature = "";
      continue;
    }
    if (!functional) {
      h.StallCycles = 0;
      h.Recovering = false;
      h.NotWorking = true;
      h.LastQueueSignature = QueueSignature(_qbuf);
      Warn("Machine not working (check power/enabled/damage): " + pb.CustomName);
      continue;
    }
    h.NotWorking = false;
    string sig = QueueSignature(_qbuf);
    bool activelyProducing = false;
    try { activelyProducing = pb.IsProducing; } catch { activelyProducing = false; }
    if (sig != h.LastQueueSignature || activelyProducing) { h.StallCycles = 0; h.Recovering = false; }
    else h.StallCycles++;
    h.LastQueueSignature = sig;
    IMyInventory inInv = pb.InputInventory;
    if (inInv != null)
      h.LastInputFillPercent = inInv.MaxVolume > 0 ? ((double)inInv.CurrentVolume / (double)inInv.MaxVolume) * 100.0 : 0;
    if (h.StallCycles >= _cfg.StallDetectionCycles && !h.Recovering) {
      if (tb <= 0) {
        h.LastRecoveryResult = "deferred: no transfer budget available this cycle";
        Warn("Stall recovery deferred (no transfer budget): " + pb.CustomName);
      } else {
        tb = EvacuateMachineInput(pb, h, tb);
        h.Recovering = true;
        h.CooldownRemaining = _cfg.RecoveryCooldownCycles;
        h.StallCycles = 0;
        anyRecovery = true;
      }
    }
  }
  return anyRecovery;
}
int EvacuateMachineInput(IMyProductionBlock pb, MH h, int tb) {
  IMyInventory inInv = pb.InputInventory;
  h.LastItemsEvacuated = 0;
  h.LastItemsBlocked = 0;
  if (inInv == null) { h.LastRecoveryResult = "no input inventory"; return tb; }
  _itemsA.Clear();
  inInv.GetItems(_itemsA);
  double evacuated = 0, blocked = 0;
  for (int k = 0; k < _itemsA.Count; k++) {
    if (IsProcessItem(_itemsA[k].Type)) continue; // IO process resource — left alone, not counted
    if (tb <= 0) { blocked += (double)_itemsA[k].Amount; continue; }
    string category = CategoryForItem(_itemsA[k].Type);
    if (category == null) { blocked += (double)_itemsA[k].Amount; continue; }
    MyFixedPoint moved;
    if (RouteItem(inInv, _itemsA[k], category, out moved))
    { evacuated += (double)moved; blocked += (double)(_itemsA[k].Amount - moved); tb--; }
    else blocked += (double)_itemsA[k].Amount;
  }
  h.LastItemsEvacuated = evacuated;
  h.LastItemsBlocked = blocked;
  h.LastRecoveryResult = "evacuated=" + F(evacuated) + " blocked=" + F(blocked);
  _cycleLog.Add("RECOVERY " + pb.CustomName + " " + h.LastRecoveryResult);
  if (evacuated > 0.0001)
    Warn("Stalled machine input evacuated: " + pb.CustomName +
      (blocked > 0.0001 ? " (partial, " + F(blocked) + " left blocked)" : ""));
  else Warn("Stall recovery blocked (no destination/budget available): " + pb.CustomName);
  return tb;
}
void BuildPlan() {
  _demand.Clear();
  _queueSupportDemand.Clear();
  _budget.Clear();
  _supportReserved.Clear();
  _plannedOutput.Clear();
  _plannedJobs.Clear();
  _planOrderSet.Clear();
  _planOrder.Clear();
  _planRecords.Clear();
  List<string> queueRoots = new List<string>(_queuedJobs.Keys);
  queueRoots.Sort(SCI);
  for (int r = 0; r < queueRoots.Count; r++) {
    string parentAlias = queueRoots[r];
    double jobs = Get(_queuedJobs, parentAlias);
    Recipe recipe;
    if (jobs <= 0.0001 || !_recipes.TryGetValue(parentAlias, out recipe)) continue;
    for (int i = 0; i < recipe.Inputs.Count; i++) {
      Ingredient ing = recipe.Inputs[i];
      string dep = Canon(ing.Item);
      EnsureFeasible(dep, ing.Amount * jobs, true, HS(), 0);
      // Reserve per direct ingredient only: deeper would self-block the parent's gate.
      ReserveSupportBudget(dep, EffectiveSupportTarget(dep));
    }
  }
  List<string> stockRoots = new List<string>(_cfg.StockTargets.Keys);
  stockRoots.Sort((a, b) => {
    int cmp = DeficitPercent(b).CompareTo(DeficitPercent(a)); // descending deficit
    return cmp != 0 ? cmp : string.Compare(a, b, OIC);
  });
  for (int i = 0; i < stockRoots.Count; i++)
    EnsureFeasible(stockRoots[i], _cfg.StockTargets[stockRoots[i]], false, HS(), 0);
}
double DeficitPercent(string alias) {
  double target = Get(_cfg.StockTargets, alias);
  if (target <= 0) return -1;
  double deficit = target - (Get(_onHand, alias) + Get(_queuedOutput, alias));
  return Math.Max(0, deficit) / target * 100.0;
}
double Get(Dictionary<string, double> d, string key) {
  double v;
  return d != null && d.TryGetValue(Canon(key), out v) ? v : 0;
}
double GetBudget(string alias) {
  double v;
  if (_budget.TryGetValue(alias, out v)) return v;
  v = Math.Max(0, Get(_onHand, alias) - Get(_cfg.StockTargets, alias));
  _budget[alias] = v;
  return v;
}
double EffectiveSupportTarget(string alias) {
  double stagedCredit = Math.Min(Get(_stagedStock, alias), Get(_queueSupportDemand, alias));
  return Math.Max(0, Get(_demand, alias) - stagedCredit);
}
void ReserveSupportBudget(string alias, double effectiveSupportTarget) {
  double remainingSupportNeed = effectiveSupportTarget - Get(_queuedOutput, alias) - Get(_plannedOutput, alias);
  if (remainingSupportNeed < 0) remainingSupportNeed = 0;
  double neededFromPhysical = Math.Min(Get(_onHand, alias), remainingSupportNeed);
  double additional = neededFromPhysical - Get(_supportReserved, alias);
  if (additional <= 0.0001) return;
  double budgetNow = GetBudget(alias);
  double actual = Math.Min(additional, budgetNow);
  if (actual <= 0) return;
  _budget[alias] = budgetNow - actual;
  AddTo(_supportReserved, alias, actual);
}
void EnsureFeasible(string alias, double demandIncrement, bool isSupportBranch, HashSet<string> stack, int depth) {
  alias = Canon(alias);
  if (depth > 20 || stack.Contains(alias)) return; // dependency cycle guard
  AddTo(_demand, alias, demandIncrement);
  if (isSupportBranch) AddTo(_queueSupportDemand, alias, demandIncrement);
  double target = Get(_demand, alias);
  double stagedCredit = Math.Min(Get(_stagedStock, alias), Get(_queueSupportDemand, alias));
  double effectiveTarget = Math.Max(0, target - stagedCredit);
  double onHandPhysical = Get(_onHand, alias);
  double availablePhysical = isSupportBranch ? Math.Max(0, onHandPhysical - Get(_cfg.StockTargets, alias)) : onHandPhysical;
  double already = availablePhysical + Get(_queuedOutput, alias) + Get(_plannedOutput, alias);
  double shortage = effectiveTarget - already;
  PRec rec;
  if (!_planRecords.TryGetValue(alias, out rec)) { rec = new PRec { Alias = alias }; _planRecords[alias] = rec; }
  rec.Target = target;
  rec.Stock = onHandPhysical;
  rec.Queued = Get(_queuedOutput, alias);
  rec.Need = Math.Max(0, shortage);
  if (shortage <= 0.0001) {
    if (rec.Action == "None") rec.Action = "Satisfied";
    return;
  }
  Recipe recipe;
  if (!_recipes.TryGetValue(alias, out recipe)) {
    rec.Action = "RawShortage";
    rec.BlockedBy = alias;
    rec.Blocked = rec.Need;
    return;
  }
  MyDefinitionId blueprint;
  if (!TryGetBlueprint(alias, out blueprint)) {
    _unknownBlueprints.Add(alias);
    rec.Action = "UnknownBlueprint";
    rec.Blocked = rec.Need;
    return;
  }
  List<IMyProductionBlock> machines = GetBestMachines(blueprint, recipe);
  if (machines.Count == 0) {
    _noMachines.Add(alias);
    rec.Action = "NoMachine";
    rec.Blocked = rec.Need;
    return;
  }
  double recipeOutput = recipe.Output > 0 ? recipe.Output : 1.0;
  int jobsWanted = (int)Math.Ceiling(shortage / recipeOutput);
  if (jobsWanted <= 0) return;
  stack.Add(alias);
  for (int i = 0; i < recipe.Inputs.Count; i++)
    EnsureFeasible(Canon(recipe.Inputs[i].Item), recipe.Inputs[i].Amount * jobsWanted, isSupportBranch, stack, depth + 1);
  stack.Remove(alias);
  int feasibleJobs = jobsWanted;
  string blockingAlias = "";
  for (int i = 0; i < recipe.Inputs.Count; i++) {
    Ingredient ing = recipe.Inputs[i];
    int supportable = (int)Math.Floor(GetBudget(Canon(ing.Item)) / ing.Amount + 0.0001);
    if (supportable < feasibleJobs) { feasibleJobs = supportable; blockingAlias = Canon(ing.Item); }
  }
  if (feasibleJobs < 0) feasibleJobs = 0;
  if (feasibleJobs > 0) {
    for (int i = 0; i < recipe.Inputs.Count; i++) {
      Ingredient ing = recipe.Inputs[i];
      string dep = Canon(ing.Item);
      _budget[dep] = GetBudget(dep) - (feasibleJobs * ing.Amount);
    }
    AddTo(_plannedOutput, alias, feasibleJobs * recipeOutput);
    AddTo(_plannedJobs, alias, feasibleJobs);
    if (_planOrderSet.Add(alias)) _planOrder.Add(alias);
  }
  rec.Feasible = feasibleJobs * recipeOutput;
  rec.Blocked = Math.Max(0, rec.Need - rec.Feasible);
  rec.BlockedBy = feasibleJobs < jobsWanted ? blockingAlias : "";
  rec.JobsAdded = feasibleJobs;
  rec.Action = feasibleJobs >= jobsWanted ? "Queued" : (feasibleJobs > 0 ? "PartialQueued" : "Blocked");
}
void AddTo(Dictionary<string, double> d, string key, double amount) {
  key = Canon(key);
  double old;
  d[key] = (d.TryGetValue(key, out old) ? old : 0) + amount;
}
void AddTo(Dictionary<string, int> d, string key, int amount) {
  key = Canon(key);
  int old;
  d[key] = (d.TryGetValue(key, out old) ? old : 0) + amount;
}
int GetInt(Dictionary<string, int> d, string key) {
  int v;
  return d != null && d.TryGetValue(Canon(key), out v) ? v : 0;
}
class MachineChoice { public IMyProductionBlock Block; public int Rank; public double QueueLoad; }
bool IsManualBench(string machineText) {
  return machineText.Contains("basicassemblingbench") || machineText.Contains("basic assembling bench")
    || machineText.Contains("assemblingbench") || machineText.Contains("assembling bench");
}
string MachineText(IMyProductionBlock pb) {
  return ((pb.CustomName ?? "") + " " + pb.BlockDefinition.SubtypeId.ToString()).ToLowerInvariant();
}
int MachineRank(string machineText, Recipe recipe, bool survival) {
  if (survival) return 9000;
  for (int i = 0; i < recipe.PreferredMachines.Count; i++) {
    string token = recipe.PreferredMachines[i].ToLowerInvariant();
    if (token == "assembler" && machineText.Contains("advanced assembler")) continue;
    if (machineText.Contains(token)) return i * 100;
  }
  return 5000;
}
double QueueLoad(IMyProductionBlock pb) {
  _qbuf.Clear();
  try { pb.GetQueue(_qbuf); } catch { return 999999; }
  double total = 0;
  for (int i = 0; i < _qbuf.Count; i++) total += (double)_qbuf[i].Amount;
  return total;
}
List<IMyProductionBlock> GetBestMachines(MyDefinitionId bp, Recipe recipe) {
  List<MachineChoice> choices = new List<MachineChoice>();
  for (int i = 0; i < _mach.Count; i++) {
    IMyProductionBlock pb = _mach[i];
    if (pb == null || !pb.IsWorking) continue;
    IMyAssembler asm = pb as IMyAssembler;
    if (asm != null && asm.Mode == MyAssemblerMode.Disassembly) continue;
    string text = MachineText(pb);
    if (IsManualBench(text)) continue; // Basic Assembling Bench is manual-only
    bool survival = text.Contains("survivalkit") || text.Contains("survival kit");
    if (survival && !_cfg.AllowSurvivalKitFallback) continue;
    bool canUse = false;
    try { canUse = pb.CanUseBlueprint(bp); } catch { canUse = false; }
    if (!canUse) continue;
    choices.Add(new MachineChoice { Block = pb, Rank = MachineRank(text, recipe, survival), QueueLoad = QueueLoad(pb) });
  }
  List<IMyProductionBlock> result = new List<IMyProductionBlock>();
  if (choices.Count == 0) return result;
  int bestRank = int.MaxValue;
  for (int i = 0; i < choices.Count; i++) if (choices[i].Rank < bestRank) bestRank = choices[i].Rank;
  List<MachineChoice> best = new List<MachineChoice>();
  for (int i = 0; i < choices.Count; i++) if (choices[i].Rank == bestRank) best.Add(choices[i]);
  best.Sort((a, b) => a.QueueLoad.CompareTo(b.QueueLoad));
  for (int i = 0; i < best.Count; i++) result.Add(best[i].Block);
  return result;
}
bool TryGetBlueprint(string alias, out MyDefinitionId bp) {
  alias = Canon(alias);
  string text;
  if (!_cfg.BlueprintOverrides.TryGetValue(alias, out text) || string.IsNullOrWhiteSpace(text))
    if (!_builtinBlueprints.TryGetValue(alias, out text)) text = null;
  if (string.IsNullOrWhiteSpace(text)) { bp = default(MyDefinitionId); return false; }
  return MyDefinitionId.TryParse(text, out bp);
}
// THE ONLY QUEUE MUTATION IN THIS SCRIPT IS AddQueueItem(), adding positive job counts only.
void ApplyPlan() {
  for (int p = 0; p < _planOrder.Count; p++) {
    string alias = _planOrder[p];
    int jobs = GetInt(_plannedJobs, alias);
    if (jobs <= 0) continue;
    Recipe recipe;
    if (!_recipes.TryGetValue(alias, out recipe)) continue;
    MyDefinitionId bp;
    if (!TryGetBlueprint(alias, out bp)) continue;
    List<IMyProductionBlock> machines = GetBestMachines(bp, recipe);
    if (machines.Count == 0) { _noMachines.Add(alias); continue; }
    int baseJobs = jobs / machines.Count;
    int remainder = jobs % machines.Count;
    for (int i = 0; i < machines.Count; i++) {
      int add = baseJobs + (i < remainder ? 1 : 0);
      if (add <= 0) continue; // positive amounts only
      try {
        machines[i].AddQueueItem(bp, (MyFixedPoint)add); // sole queue mutation
        PRec rec;
        if (_planRecords.TryGetValue(alias, out rec)) rec.Action = "Queued";
        _cycleLog.Add("+" + add + " " + Friendly(alias) + " -> " + machines[i].CustomName);
      } catch (Exception ex) {
        _cycleLog.Add("QUEUE ERROR " + Friendly(alias) + ": " + ex.Message);
      }
    }
  }
}
void BuildKnowledgeBase() {
  // PENDING LIVE UAT: only the BLUEPRINT ids PODetectorComponent/POBulletproofGlass were ever
  // validated in-game; the RESULT subtypes for SensorCluster/Glass were not and may be
  // Detector/BulletproofGlass. Both names are in the loadout table, so loadouts work either way;
  // only base [Stock] accounting depends on the answer.
  // "x=y" pairs are live-verified real SubtypeIds. A wrong subtype FAILS SILENTLY:
  // routing still works but the alias never credits, so Stock reads 0 and the planner
  // reorders forever (cost ~129k surplus BulletproofGlass in v2.3.4-v2.4.2).
  AddItemGroup("MyObjectBuilder_Component", "SteelPlate,AluminumPlate=InteriorPlate,TitaniumPlate,CopperWire,GoldWire,LargeSteelTube=LargeTube,SmallSteelTube=SmallTube,Construction,MetalGrid,Electromagnet,Motor,BasicComputer=Computer,AdvancedComputer,SensorCluster=Detector,Display,Thermocouple,Ceramic,Glass=BulletproofGlass,HeatingElement,Lightbulb,MedicalComponent=Medical,LithiumPowerCell=PowerCell,Plastic,Rubber");
  AddItemGroup("MyObjectBuilder_Ingot", "Polymer,IronIngot=Iron,NickelIngot=Nickel,CobaltIngot=Cobalt,CopperIngot=Copper,GoldIngot=Gold,AluminumIngot=Aluminum,TitaniumIngot=Titanium,SilverIngot=Silver,SiliconWafer=Silicon,Carbon,Sulfur,LithiumPaste=Lithium");
  AddAliasGroup("ConstructionComp=Construction,ConstructionComponent=Construction,LargeTube=LargeSteelTube,SmallTube=SmallSteelTube,Computer=BasicComputer,Medical=MedicalComponent,PowerCell=LithiumPowerCell,InteriorPlate=AluminumPlate,Detector=SensorCluster,BulletproofGlass=Glass,SiliconIngot=SiliconWafer,CarbonIngot=Carbon,SulfurIngot=Sulfur,LithiumIngot=LithiumPaste,PolymerIngot=Polymer");
  AddBlueprintGroup("Electromagnet=Electromagnet,CopperWire=CopperWire,Motor=POMotorComponent,HeatingElement=HeatingElement,SteelPlate=POSteelPlate,SmallSteelTube=POSmallTube,AdvancedComputer=AdvancedComputer,Plastic=PolymerToPlastic,Construction=POConstructionComponent,LargeSteelTube=POLargeTube,BasicComputer=POComputerComponent,Rubber=Rubber,TitaniumPlate=TitaniumPlate,Ceramic=Ceramic,Polymer=SyntheticPolymer,Lightbulb=Lightbulb,Display=PODisplay,MedicalComponent=POMedicalComponent,Thermocouple=Thermocouple,LithiumPowerCell=POPowerCell,GoldWire=GoldWire,AluminumPlate=POInteriorPlate,Glass=POBulletproofGlass,MetalGrid=POMetalGrid,SensorCluster=PODetectorComponent");
  // KNOWLEDGE ONLY - no Recipe and no ItemDef, so never planned, queued or put in [Stock].
  AddBlueprintGroup("Girder=POGirderComponent,RadioCommunication=PORadioCommunicationComponent,Reactor=POReactorComponent,SolarCell=POSolarCell,Superconductor=POSuperconductor,Thrust=POThrustComponent,GravityGenerator=POGravityGeneratorComponent,Explosives=POExplosivesComponent,Canvas=POCanvas,AcidPowerCell=AcidPowerCell,AlkalinePowerCell=AlkalinePowerCell,ArmorGlass=ArmorGlass,ArmoredPlate=ArmoredPlate,Asphalt=Asphalt,Capacitor=Capacitor,CompositeArmor=CompositeArmor,Concrete=Concrete,Cryocooler=Cryocooler,ElectronMatrix=ElectronMatrix,FSSolarCell=FSSolarCell,Fabric=Fabric,LaserEmitter=LaserEmitter,QuantumComputer=QuantumComputer,SuperMagnet=SuperMagnet,TokamakBlanket=TokamakBlanket");
  // LOADOUT-ONLY IDENTITY TABLE - deliberately NOT an item database. GOAT names for vanilla ammo
  // plus vanilla components IOPM knows but does not make; each entry self-registers its bare
  // SubtypeId. Loadout-resolvable ONLY: no Recipe, no ItemDef, never in [Stock]/_onHand.
  AddLoadAlias("MyObjectBuilder_AmmoMagazine", "AutocannonMagazine=AutocannonClip,ArtilleryShell=LargeCalibreAmmo," +
    "AssaultCannonShell=MediumCalibreAmmo,Missile=Missile200mm,GatlingAmmo=NATO_25x184mm," +
    "LargeRailgunSabot=LargeRailgunAmmo,SmallRailgunSabot=SmallRailgunAmmo," +
    "PistolMagazine-S-10=SemiAutoPistolMagazine,PistolMagazine-S-10E=ElitePistolMagazine," +
    "PistolMagazine-S-10A=FullAutoPistolMagazine,RifleMagazine-MR-20=AutomaticRifleGun_Mag_20rd," +
    "RifleMagazine-MR-50A=RapidFireAutomaticRifleGun_Mag_50rd,RifleMagazine-MR-8P=PreciseAutomaticRifleGun_Mag_5rd," +
    "RifleMagazine-MR-30E=UltimateAutomaticRifleGun_Mag_30rd,FlareGunClip=FlareClip");
  AddLoadAlias("MyObjectBuilder_Component", "Girder=Girder,RadioCommunication=RadioCommunication," +
    "Reactor=Reactor,SolarCell=SolarCell,Superconductor=Superconductor,Thrust=Thrust," +
    "GravityGenerator=GravityGenerator,Explosives=Explosives,Canvas=Canvas,Detector=Detector," +
    "BulletproofGlass=BulletproofGlass,RadioCommComponent=RadioCommunication," +
    "ReactorComponent=Reactor,ThrustComponent=Thrust,GravityGenComponent=GravityGenerator," +
    "DetectorComponent=Detector");
  // MANAGED RECIPE SET - exactly 25 aliases. A blueprint id above does NOT add a recipe.
  AddRecipes(new string[] {
    "SteelPlate|1|Plate Stamp|IronIngot:20", "AluminumPlate|1|Plate Stamp|AluminumIngot:5", "TitaniumPlate|1|Plate Stamp|TitaniumIngot:12",
    "CopperWire|1|Wire Drawer|CopperIngot:1", "GoldWire|1|Wire Drawer|GoldIngot:0.6",
    "LargeSteelTube|1|Extruder|IronIngot:4", "SmallSteelTube|1|Extruder|IronIngot:2",
    "Construction|1|Fabricator|IronIngot:5", "MetalGrid|1|Fabricator|IronIngot:3,NickelIngot:3,CobaltIngot:3",
    "Electromagnet|1|Fabricator|IronIngot:1.5,NickelIngot:1,CopperWire:3", "HeatingElement|1|Fabricator|NickelIngot:5,CopperIngot:5",
    "Lightbulb|10|Fabricator|Glass:1,CopperWire:10",
    "Motor|1|Assembler|Electromagnet:3,LargeSteelTube:1,CopperWire:3", "MedicalComponent|1|Assembler|IronIngot:12,NickelIngot:8,SilverIngot:8",
    "BasicComputer|1|Microelectronics Factory;Fabricator|CopperWire:3,SiliconWafer:2",
    "AdvancedComputer|1|Microelectronics Factory|GoldWire:3,SiliconWafer:2,Plastic:2",
    "SensorCluster|1|Microelectronics Factory|Glass:1,BasicComputer:2,CopperWire:3,SiliconWafer:3,NickelIngot:3",
    "Display|1|Microelectronics Factory|CopperWire:2,SiliconWafer:3,Plastic:2,SilverIngot:0.1,BasicComputer:1,NickelIngot:1",
    "Thermocouple|1|Microelectronics Factory|SiliconWafer:2,CopperWire:3,Plastic:1,AluminumIngot:2",
    "Ceramic|1|Ceramics Furnace|SiliconWafer:3,Carbon:2", "Glass|1|Ceramics Furnace|SiliconWafer:3",
    "Polymer|1|Synthetics Factory|SiliconWafer:1,Carbon:1.5,Sulfur:0.5", "Plastic|1|Synthetics Factory|Polymer:1.5", "Rubber|1|Synthetics Factory|Polymer:2",
    "LithiumPowerCell|1|Advanced Assembler|AluminumPlate:1,CopperWire:4,LithiumPaste:10,Rubber:2,Carbon:3"
  });
}
void AddItemGroup(string typeId, string list) {
  string[] e = list.Split(',');
  for (int i = 0; i < e.Length; i++) {
    int eq = e[i].IndexOf('=');
    string alias = eq < 0 ? e[i] : e[i].Substring(0, eq);
    AddItem(alias, typeId + "/" + (eq < 0 ? e[i] : e[i].Substring(eq + 1)));
  }
}
void AddLoadAlias(string typeId, string list) {
  string[] e = list.Split(',');
  for (int i = 0; i < e.Length; i++) {
    int eq = e[i].IndexOf('=');
    string sub = e[i].Substring(eq + 1), full = typeId + "/" + sub;
    _loadAlias[e[i].Substring(0, eq)] = full;
    if (!_loadAlias.ContainsKey(sub)) _loadAlias[sub] = full; // bare SubtypeId resolves too
  }
}
void AddPairsTo(Dictionary<string, string> d, string list, string valPrefix, bool canonKey) {
  string[] e = list.Split(',');
  for (int i = 0; i < e.Length; i++) {
    int eq = e[i].IndexOf('=');
    string k = e[i].Substring(0, eq);
    d[canonKey ? Canon(k) : k] = valPrefix + e[i].Substring(eq + 1);
  }
}
void AddAliasGroup(string list) { AddPairsTo(_aliases, list, "", false); }
void AddBlueprintGroup(string list) {
  AddPairsTo(_builtinBlueprints, list, "MyObjectBuilder_BlueprintDefinition/", true);
}
double ParseAmt(string s) {
  int dot = s.IndexOf('.');
  if (dot < 0) return double.Parse(s);
  string fracStr = s.Substring(dot + 1);
  double div = 1;
  for (int i = 0; i < fracStr.Length; i++) div *= 10;
  return double.Parse(s.Substring(0, dot)) + double.Parse(fracStr) / div;
}
void AddRecipes(string[] rows) {
  for (int r = 0; r < rows.Length; r++) {
    string[] p = rows[r].Split('|');
    string[] mach = p[2].Split(';');
    string[] ing = p[3].Split(',');
    Recipe rec = new Recipe { Item = Canon(p[0]), Output = ParseAmt(p[1]) };
    for (int i = 0; i < mach.Length; i++) rec.PreferredMachines.Add(mach[i]);
    for (int i = 0; i < ing.Length; i++) {
      int c = ing[i].IndexOf(':');
      rec.Inputs.Add(new Ingredient { Item = Canon(ing[i].Substring(0, c)), Amount = ParseAmt(ing[i].Substring(c + 1)) });
    }
    _recipes[rec.Item] = rec;
  }
}
void AddItem(string alias, string type) {
  _items[alias] = new ItemDef { Alias = alias, Type = type };
  _aliases[alias] = alias;
  if (!_typeAlias.ContainsKey(type)) _typeAlias[type] = alias;
  int slash = type.IndexOf('/');
  if (slash >= 0 && slash + 1 < type.Length) {
    string subtype = type.Substring(slash + 1);
    if (!_aliases.ContainsKey(subtype)) _aliases[subtype] = alias;
  }
}
string Canon(string value) {
  if (string.IsNullOrWhiteSpace(value)) return "";
  value = value.Trim();
  string c;
  return _aliases.TryGetValue(value, out c) ? c : value;
}
string Friendly(string alias) {
  alias = Canon(alias);
  if (!_items.ContainsKey(alias)) return alias;
  if (alias == "Construction") return "Construction Component";
  System.Text.StringBuilder sb = new System.Text.StringBuilder();
  for (int i = 0; i < alias.Length; i++) {
    if (i > 0 && char.IsUpper(alias[i]) && !char.IsUpper(alias[i - 1])) sb.Append(' ');
    sb.Append(alias[i]);
  }
  return sb.ToString();
}
// Compact stock-column format: floor (never round up) to k/M so wide values fit a
// narrow column. Diagnostics keep full-precision F() - never use FK() for those.
string FK(double v) {
  double a = Math.Abs(v);
  if (a >= 1000000) return Math.Floor(v / 1000000) + "M";
  if (a >= 10000) return Math.Floor(v / 1000) + "k";
  return F(v);
}
string F(double v) {
  if (Math.Abs(v - Math.Round(v)) < 0.0001) return Math.Round(v).ToString("N0");
  if (Math.Abs(v) >= 1000) return v.ToString("N1");
  return v.ToString("0.###");
}
class ItemDef { public string Alias; public string Type; }
class Ingredient { public string Item; public double Amount; }
class Recipe {
  public string Item;
  public double Output;
  public List<Ingredient> Inputs = new List<Ingredient>();
  public List<string> PreferredMachines = new List<string>();
}
// The script owns [IOPM.*] ONLY; every other Custom Data section survives verbatim.
string _lastWrittenCustomData = null;
void WriteDiagnostics() {
  MyIni ini = new MyIni();
  MyIniParseResult result;
  if (!ini.TryParse(Me.CustomData, out result)) {
    _diagParseError = "L" + result.LineNo + ": " + result.Error;
    return; // don't clobber unparsable user data
  }
  _diagParseError = "";
  EnsureStockAliasesPresent(ini);
  List<string> sections = new List<string>();
  ini.GetSections(sections);
  for (int i = 0; i < sections.Count; i++)
    if (sections[i].StartsWith("IOPM.", OIC)) ini.DeleteSection(sections[i]);
  ini.Set("IOPM.Status", "Version", VERSION);
  ini.Set("IOPM.Status", "State", _cfg.GeneralEnabled ? "Running" : "Disabled");
  ini.Set("IOPM.Status", "Sorting", _cfg.SortingEnabled ? "OK" : "Off");
  ini.Set("IOPM.Status", "Production", _cfg.ProductionEnabled ? "OK" : "Off");
  ini.Set("IOPM.Status", "Warnings", _warnings.Count);
  ini.Set("IOPM.Status", "LastCycleSeconds", Math.Round(_lastCycleSeconds, 2));
  ini.Set("IOPM.Status", "ConfigChangePending", _cfgDirty);
  int curInstr = Runtime.CurrentInstructionCount;
  if (curInstr > _peakInstructions) { _peakInstructions = curInstr; _peakPhaseName = PN[PHASE_DIAGNOSTICS]; }
  ini.Set("IOPM.Runtime", "CurrentPhase", PN[_cyclePhase]);
  ini.Set("IOPM.Runtime", "LastPhase", _lastPhase);
  ini.Set("IOPM.Runtime", "LastInstructions", _lastInstructions);
  ini.Set("IOPM.Runtime", "PeakInstructions", _peakInstructions);
  ini.Set("IOPM.Runtime", "PeakPhase", _peakPhaseName);
  ini.Set("IOPM.Runtime", "MaxInstructions", Runtime.MaxInstructionCount);
  foreach (var kv in _pools) {
    int healthy; double pct;
    PoolStats(kv.Value, out healthy, out pct);
    string sec = "IOPM.Warehouse." + kv.Key;
    ini.Set(sec, "Containers", kv.Value.Count);
    ini.Set(sec, "Healthy", healthy);
    ini.Set(sec, "FillPercent", Math.Round(pct, 1));
  }
  string dk = "IOPM.Docking";
  ini.Set(dk, "ConnectedConstructs", _ships.Count);
  ini.Set(dk, "LoadoutContainers", _dkLoadCons);
  ini.Set(dk, "LoadoutsSeeded", _dkSeeded);
  ini.Set(dk, "UnloadSources", _dkUnloadSrc);
  ini.Set(dk, "BorrowPercent", _cfg.BorrowPercent);
  ini.Set(dk, "ToBaseTransfers", _dkToBase);
  ini.Set(dk, "ToLoadoutTransfers", _dkToLoad);
  ini.Set(dk, "LoadoutShortages", _dkShort);
  ini.Set(dk, "BlockedTransfers", _dkBlocked);
  ini.Set(dk, "LastConnector", _dkLastConn);
  int ln = 0;
  for (int i = 0; i < _ships.Count && ln < 10; i++)
    for (int j = 0; j < _ships[i].Loads.Count && ln < 10; j++) {
      LCon lo = _ships[i].Loads[j];
      if (lo.Short == 0 && lo.NoOp == 0 && lo.Q.Count > 0) continue;
      string sec = "IOPM.Loadout." + (++ln);
      ini.Set(sec, "Container", lo.Name);
      ini.Set(sec, "Satisfied", lo.Sat);
      ini.Set(sec, "Short", lo.Short);
      if (lo.NoOp > 0) ini.Set(sec, "AllNothingAvailable", lo.NoOp);
      if (lo.Wait != "") ini.Set(sec, "WaitingFor", lo.Wait);
      else if (lo.Q.Count == 0) ini.Set(sec, "Note", "no @GOAT-Stock definitions");
    }
  ini.Set("IOPM.StockDisplay", "Rows", _stockRowCount);
  ini.Set("IOPM.StockDisplay", "Screens", _stockScr.Count);
  ini.Set("IOPM.StockDisplay", "LastError", _stockLastError);
  ini.Set("IOPM.Production", "StockItems", _cfg.StockTargets.Count);
  int ready, producing, blockedCount;
  CountPlanStates(out ready, out producing, out blockedCount);
  foreach (var kv in _planRecords) {
    PRec r = kv.Value;
    string sec = "IOPM.LastPlan." + r.Alias;
    ini.Set(sec, "Target", F(r.Target));
    ini.Set(sec, "Stock", F(r.Stock));
    ini.Set(sec, "Queued", F(r.Queued));
    ini.Set(sec, "Need", F(r.Need));
    ini.Set(sec, "Feasible", F(r.Feasible));
    ini.Set(sec, "Blocked", F(r.Blocked));
    // ALWAYS emit every key below: MyIni cannot delete one, so an omitted key would
    // keep its previous value forever and read as current. Blank it, never skip it.
    // Emitted whenever set, NOT only for a blocked Action: a Queued row with Blocked>0 is
    // partially ingredient-limited, and naming that ingredient is the whole point.
    ini.Set(sec, "BlockedBy", r.BlockedBy);
    ini.Set(sec, "Action", r.Action);
    ini.Set(sec, "JobsAdded", r.JobsAdded);
    ini.Set(sec, "SupportDemand", F(Get(_queueSupportDemand, r.Alias)));
    ini.Set(sec, "SupportReserved", F(Get(_supportReserved, r.Alias)));
  }
  ini.Set("IOPM.Production", "Ready", ready);
  ini.Set("IOPM.Production", "Producing", producing);
  ini.Set("IOPM.Production", "Blocked", blockedCount);
  int healthyM, stalledM, recoveringM, notWorkingM;
  CountMachineStates(out healthyM, out stalledM, out recoveringM, out notWorkingM);
  foreach (var kv in _machineHealth) {
    MH h = kv.Value;
    if (!IsManaged(h.EntityId)) continue;
    bool pastThreshold = h.StallCycles >= _cfg.StallDetectionCycles;
    if (h.Recovering || h.StallCycles > 0 || h.NotWorking) {
      string sec = "IOPM.Machine." + SanitizeSectionName(h.Name);
      ini.Set(sec, "State", h.NotWorking ? "NotWorking" : (h.Recovering ? "Recovering" : (pastThreshold ? "Stalled" : "Waiting")));
      ini.Set(sec, "StalledCycles", h.StallCycles);
      ini.Set(sec, "InputFillPercent", Math.Round(h.LastInputFillPercent, 1));
      ini.Set(sec, "RecoveryAttempted", h.Recovering);
      ini.Set(sec, "ItemsEvacuated", F(h.LastItemsEvacuated));
      ini.Set(sec, "ItemsBlocked", F(h.LastItemsBlocked));
    }
  }
  ini.Set("IOPM.Machines", "Healthy", healthyM);
  ini.Set("IOPM.Machines", "Stalled", stalledM);
  ini.Set("IOPM.Machines", "Recovering", recoveringM);
  ini.Set("IOPM.Machines", "NotWorking", notWorkingM);
  string og = "IOPM.Organization";
  ini.Set(og, "Examined", _orgExamined);
  ini.Set(og, "OutOfOrder", _orgOutOfOrder);
  ini.Set(og, "Attempts", _orgAttempts);
  ini.Set(og, "Succeeded", _orgSucceeded);
  ini.Set(og, "Failed", _orgFailed);
  ini.Set(og, "LastAttempt", _orgLastContainer == "" ? "none" :
    _orgLastContainer + " | " + _orgLastItem + " | slot " + _orgLastSourceIndex + "->" +
    _orgLastTargetIndex + " | " + (_orgLastResult ? "ok" : "fail"));
  ini.Set(og, "LastError", _orgLastError);
  for (int i = 0; i < _warnings.Count && i < 20; i++) {
    string sec = "IOPM.Warning." + (i + 1);
    ini.Set(sec, "Severity", "Warning");
    ini.Set(sec, "Message", _warnings[i]);
  }
  for (int i = 0; i < _unknownItems.Count && i < 20; i++) {
    string sec = "IOPM.Unknown." + (i + 1);
    ini.Set(sec, "Item", _unknownItems[i].ItemType);
    ini.Set(sec, "Reason", _unknownItems[i].Reason);
    ini.Set(sec, "SeenIn", _unknownItems[i].SeenIn);
  }
  if (_unknownBlueprints.Count > 0) {
    string sec = "IOPM.Unknown." + (_unknownItems.Count + 1);
    ini.Set(sec, "Reason", "NoBlueprint");
    ini.Set(sec, "SeenIn", string.Join(",", _unknownBlueprints));
  }
  for (int i = 0; i < _configErrors.Count; i++)
    ini.Set("IOPM.ConfigError." + (i + 1), "Message", _configErrors[i]);
  string finalText = ini.ToString();
  if (!string.Equals(finalText, _lastWrittenCustomData, StringComparison.Ordinal)) {
    Me.CustomData = finalText;
    _lastWrittenCustomData = finalText;
    // Re-read, not finalText: the no-self-trigger guarantee must survive normalization.
    _lastCustomDataSeen = Me.CustomData;
  }
}
void PoolStats(List<CI> pool, out int healthy, out double pct) {
  healthy = 0; double tv = 0, cv = 0;
  for (int i = 0; i < pool.Count; i++) {
    if (_ignInv.Contains(pool[i].Inventory)) continue;
    healthy++;
    tv += (double)pool[i].Inventory.MaxVolume;
    cv += (double)pool[i].Inventory.CurrentVolume;
  }
  pct = tv > 0 ? cv / tv * 100.0 : 0;
}
void CountPlanStates(out int ready, out int producing, out int blocked) {
  ready = 0; producing = 0; blocked = 0;
  foreach (var kv in _planRecords) {
    if (!_cfg.StockTargets.ContainsKey(kv.Key)) continue;
    if (kv.Value.Action == "Satisfied") ready++;
    else if (IsBlockedAction(kv.Value.Action)) blocked++;
    else producing++;
  }
}
void CountMachineStates(out int healthy, out int stalled, out int recovering, out int notWorking) {
  healthy = 0; stalled = 0; recovering = 0; notWorking = 0;
  foreach (var kv in _machineHealth) {
    MH h = kv.Value;
    if (!IsManaged(h.EntityId)) continue;
    if (h.NotWorking) notWorking++;
    else if (h.Recovering) recovering++;
    else if (h.StallCycles >= _cfg.StallDetectionCycles) stalled++;
    else healthy++;
  }
}
bool IsBlockedAction(string a) {
  return a == "Blocked" || a == "RawShortage" || a == "NoMachine" || a == "UnknownBlueprint";
}
bool IsManaged(long entityId) {
  for (int i = 0; i < _mach.Count; i++) if (_mach[i].EntityId == entityId) return true;
  return false;
}
string SanitizeSectionName(string name) {
  System.Text.StringBuilder sb = new System.Text.StringBuilder();
  for (int i = 0; i < name.Length; i++) if (char.IsLetterOrDigit(name[i])) sb.Append(name[i]);
  return sb.Length == 0 ? "Unnamed" : sb.ToString();
}
// ContentType MUST be set or nothing renders. Fonts forced only when ManageFonts=true;
// tables are space-padded, so a proportional font misaligns the columns.
void WriteSurfaces(List<IMyTextSurface> screens, string text, double fontSize) {
  for (int i = 0; i < screens.Count; i++) {
    IMyTextSurface s = screens[i];
    if (s == null) continue;
    s.ContentType = ContentType.TEXT_AND_IMAGE;
    if (_cfg.ManageFonts) { s.Font = "Monospace"; s.FontSize = (float)fontSize; }
    s.WriteText(text, false);
  }
}
void RenderStatusScreens() {
  WriteSurfaces(_statScr, BuildStatusText(), _cfg.StatusFontSize);
}
string BuildStatusText() {
  System.Text.StringBuilder sb = new System.Text.StringBuilder();
  sb.Append("IO PRODUCTION MANAGER v").Append(VERSION).Append("\n\n");
  sb.Append("SYSTEM\n");
  sb.Append("Status: ").Append(_cfg.GeneralEnabled ? "RUNNING" : "DISABLED").Append("\n");
  sb.Append("Sorting: ").Append(_cfg.SortingEnabled ? "OK" : "OFF").Append("\n");
  sb.Append("Production: ").Append(_cfg.ProductionEnabled ? "ON" : "OFF").Append("\n");
  sb.Append("Last Cycle: ").Append(Math.Round(_lastCycleSeconds, 1)).Append("s\n");
  if (_cfgDirty) sb.Append("Config change pending (applies next cycle)\n");
  if (_fallbackScr) sb.Append("WARNING: no [StatusScreen] block found; showing on PB.\n");
  sb.Append("\nWAREHOUSE\n");
  for (int i = 0; i < CATS.Length; i++) {
    List<CI> pool;
    if (!_pools.TryGetValue(CATS[i], out pool) || pool.Count == 0) continue;
    int healthy; double pct;
    PoolStats(pool, out healthy, out pct);
    sb.Append(PadRight(CATS[i], 12)).Append(PadRight(healthy.ToString(), 4)) .Append(Math.Round(pct)).Append("%\n");
  }
  sb.Append("\nPRODUCTION\n");
  int ready, producing, blocked;
  CountPlanStates(out ready, out producing, out blocked);
  List<PRec> stockRecords = new List<PRec>();
  foreach (var kv in _planRecords)
    if (_cfg.StockTargets.ContainsKey(kv.Key)) stockRecords.Add(kv.Value);
  sb.Append(PadRight("Ready", 12)).Append(ready).Append("\n");
  sb.Append(PadRight("Producing", 12)).Append(producing).Append("\n");
  sb.Append(PadRight("Blocked", 12)).Append(blocked).Append("\n\n");
  stockRecords.Sort((a, b) => {
    int rankA = a.Action == "Blocked" ? 0 : (a.Action == "PartialQueued" || a.Action == "Queued" ? 1 : 2);
    int rankB = b.Action == "Blocked" ? 0 : (b.Action == "PartialQueued" || b.Action == "Queued" ? 1 : 2);
    return rankA.CompareTo(rankB);
  });
  int shown = 0;
  for (int i = 0; i < stockRecords.Count && shown < 6; i++) {
    PRec r = stockRecords[i];
    if (r.Action == "Satisfied") continue;
    string line = Friendly(r.Alias) + " " + F(r.Stock + r.Queued) + "/" + F(r.Target);
    if (IsBlockedAction(r.Action)) line += "  BLOCKED" + (r.BlockedBy != "" ? ": " + Friendly(r.BlockedBy) : "");
    else if (r.Feasible > 0) line += "  +" + F(r.Feasible);
    sb.Append(line).Append("\n");
    shown++;
  }
  sb.Append("\nMACHINES\n");
  int healthyM, stalledM, recoveringM, notWorkingM;
  CountMachineStates(out healthyM, out stalledM, out recoveringM, out notWorkingM);
  sb.Append(PadRight("Healthy", 12)).Append(healthyM).Append("\n");
  sb.Append(PadRight("Stalled", 12)).Append(stalledM).Append("\n");
  sb.Append(PadRight("Recovering", 12)).Append(recoveringM).Append("\n");
  if (notWorkingM > 0) sb.Append(PadRight("Not Working", 12)).Append(notWorkingM).Append("\n");
  sb.Append("\n");
  if (_warnings.Count > 0) {
    sb.Append("WARNINGS\n");
    for (int i = 0; i < _warnings.Count && i < 5; i++)
      sb.Append(i + 1).Append(". ").Append(_warnings[i]).Append("\n");
  }
  return sb.ToString();
}
// [IOPM-Stock] display - read-only. Qty is BASE stock only; never remote loadout contents.
class StockRow {
  public string Alias = "";
  public string Display = "";
  public double Qty, Quota, Queued;
}
int _stockRowCount = 0;
int _stockPage = 0; // one page per rendered cycle: dwell == UpdateSeconds
string _stockLastError = "";
void RenderStockScreens() {
  if (_stockScr.Count == 0) return;
  WriteSurfaces(_stockScr, BuildStockText(), _cfg.StockFontSize);
}
string BuildStockText() {
  _stockLastError = "";
  List<StockRow> rows = new List<StockRow>();
  foreach (var kv in _recipes) {
    string alias = kv.Key;
    try {
      StockRow row = new StockRow();
      row.Alias = alias ?? "";
      row.Display = Friendly(alias) ?? row.Alias;
      row.Qty = Get(_onHand, alias);
      row.Quota = _cfg.StockTargets.ContainsKey(alias) ? _cfg.StockTargets[alias] : 0;
      row.Queued = Get(_queuedOutput, alias);
      rows.Add(row);
    } catch (Exception ex) {
      _stockLastError = alias + " | " + ex.Message;
      Warn("Stock display row failed: " + _stockLastError);
    }
  }
  rows.Sort((a, b) => {
    int nameCmp = string.Compare(a.Display, b.Display, OIC);
    return nameCmp != 0 ? nameCmp : string.Compare(a.Alias, b.Alias, OIC); // deterministic tie-break
  });
  _stockRowCount = rows.Count;
  int per = _cfg.StockRowsPerPage, total = rows.Count;
  int pages = (per <= 0 || per >= total) ? 1 : (total + per - 1) / per;
  if (_stockPage >= pages) _stockPage = 0;
  int from = pages == 1 ? 0 : _stockPage * per;
  int to = pages == 1 ? total : Math.Min(total, from + per);
  System.Text.StringBuilder sb = new System.Text.StringBuilder();
  sb.Append("IOPM STOCK").Append(pages > 1 ? "  " + (_stockPage + 1) + "/" + pages : "").Append("\n\n");
  sb.Append(PadRight("Item", 24)).Append(PadRight("Qty", 7)).Append(PadRight("Quota", 7)).Append("Queued\n");
  _stockPage = pages > 1 ? (_stockPage + 1) % pages : 0;
  for (int i = from; i < to; i++) {
    StockRow r = rows[i];
    sb.Append(PadRight(r.Display, 24)).Append(PadRight(FK(r.Qty), 7))
     .Append(PadRight(FK(r.Quota), 7)).Append(FK(r.Queued)).Append("\n");
  }
  return sb.ToString();
}
void EnsureStockAliasesPresent(MyIni ini) {
  List<MyIniKey> keys = new List<MyIniKey>();
  ini.GetKeys("Stock", keys);
  HashSet<string> present = HS();
  for (int i = 0; i < keys.Count; i++) present.Add(Canon(keys[i].Name));
  foreach (var kv in _recipes)
    if (!present.Contains(kv.Key)) ini.Set("Stock", kv.Key, 0);
}
string PadRight(string s, int len) {
  if (s.Length >= len) return s + " ";
  return s + new string(' ', len - s.Length);
}
