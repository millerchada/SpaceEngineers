// Minimal Space Engineers PB API stubs — FOR LOCAL COMPILE-CHECKING ONLY.
// Never pasted into the game and never part of any artifact. check_pb.py wraps a PB script
// in the MyGridProgram shape and compiles it against these with csc, so that errors the game
// would otherwise be the first to find — shadowed locals (CS0136), duplicate locals (CS0128),
// typos, wrong argument counts, plain syntax errors — surface on this machine instead.
//
// These are SHAPES, not behaviour. Every member returns default. Nothing here is executed.
// A member only needs to exist with a compatible signature for the check to be meaningful.
// If a new API is used by a script, add it here; the compiler will name what is missing.
//
// WHAT THIS DOES NOT CHECK: the PB whitelist. csc happily accepts Comparison<T>, which the
// game rejects. check_pb.py screens for known-prohibited constructs separately.
using System;
using System.Collections.Generic;

namespace Sandbox.ModAPI.Ingame {

  public struct MyDefinitionId {
    public string TypeId { get { return null; } }
    public string SubtypeId { get { return null; } }
    public string SubtypeName { get { return null; } }
    public static bool TryParse(string s, out MyDefinitionId id) { id = default(MyDefinitionId); return false; }
  }

  public struct MyItemType {
    public string TypeId { get { return null; } }
    public string SubtypeId { get { return null; } }
  }

  // MyFixedPoint is arithmetic, so the operators must exist or every comparison in a real
  // script reports CS0019 and drowns the errors that matter.
  public struct MyFixedPoint {
    public static explicit operator MyFixedPoint(double d) { return default(MyFixedPoint); }
    public static explicit operator MyFixedPoint(int d) { return default(MyFixedPoint); }
    public static explicit operator MyFixedPoint(float d) { return default(MyFixedPoint); }
    public static explicit operator double(MyFixedPoint f) { return 0; }
    public static explicit operator float(MyFixedPoint f) { return 0; }
    public static implicit operator MyFixedPoint(long d) { return default(MyFixedPoint); }
    public static MyFixedPoint operator +(MyFixedPoint a, MyFixedPoint b) { return a; }
    public static MyFixedPoint operator -(MyFixedPoint a, MyFixedPoint b) { return a; }
    public static MyFixedPoint operator *(MyFixedPoint a, MyFixedPoint b) { return a; }
    public static MyFixedPoint operator /(MyFixedPoint a, MyFixedPoint b) { return a; }
    public static MyFixedPoint operator -(MyFixedPoint a) { return a; }
    public static bool operator <(MyFixedPoint a, MyFixedPoint b) { return false; }
    public static bool operator >(MyFixedPoint a, MyFixedPoint b) { return false; }
    public static bool operator <=(MyFixedPoint a, MyFixedPoint b) { return false; }
    public static bool operator >=(MyFixedPoint a, MyFixedPoint b) { return false; }
    public static bool operator ==(MyFixedPoint a, MyFixedPoint b) { return false; }
    public static bool operator !=(MyFixedPoint a, MyFixedPoint b) { return false; }
    public override bool Equals(object o) { return false; }
    public override int GetHashCode() { return 0; }
    public static MyFixedPoint Zero { get { return default(MyFixedPoint); } }
    public static MyFixedPoint MaxValue { get { return default(MyFixedPoint); } }
    public static MyFixedPoint MinValue { get { return default(MyFixedPoint); } }
  }

  public struct MyInventoryItem {
    public MyItemType Type { get { return default(MyItemType); } }
    public MyFixedPoint Amount { get { return default(MyFixedPoint); } }
  }

  public struct MyProductionItem {
    public MyDefinitionId BlueprintId { get { return default(MyDefinitionId); } }
    public MyFixedPoint Amount { get { return default(MyFixedPoint); } }
  }

  public enum MyAssemblerMode { Assembly, Disassembly }
  public enum MyShipConnectorStatus { Unconnected, Connectable, Connected }
  public enum UpdateType { None, Update1, Update10, Update100, Once, Terminal, Trigger, Script, Mod, IGC, Antenna }
  public enum UpdateFrequency { None, Once, Update1, Update10, Update100 }

  public interface IMyCubeGrid {
    string CustomName { get; set; }
    long EntityId { get; }
  }

  public interface IMyInventory {
    void GetItems(List<MyInventoryItem> items, Func<MyInventoryItem, bool> filter = null);
    MyInventoryItem? GetItemAt(int index);
    int ItemCount { get; }
    MyFixedPoint CurrentVolume { get; }
    MyFixedPoint MaxVolume { get; }
    MyFixedPoint CurrentMass { get; }
    bool IsFull { get; }
    bool CanItemsBeAdded(MyFixedPoint amount, MyItemType type);
    bool TransferItemTo(IMyInventory dst, int index, int? targetIndex = null, bool? stackIfPossible = null, MyFixedPoint? amount = null);
    bool TransferItemTo(IMyInventory dst, MyInventoryItem item, MyFixedPoint? amount = null);
    bool IsConnectedTo(IMyInventory other);
  }

  public interface IMyTerminalBlock {
    string CustomName { get; set; }
    string CustomData { get; set; }
    long EntityId { get; }
    bool IsWorking { get; }
    bool IsFunctional { get; }
    bool Enabled { get; set; }
    bool HasInventory { get; }
    int InventoryCount { get; }
    IMyCubeGrid CubeGrid { get; }
    MyDefinitionId BlockDefinition { get; }
    IMyInventory GetInventory(int index);
    bool IsSameConstructAs(IMyTerminalBlock other);
  }

  public interface IMyCargoContainer : IMyTerminalBlock { }
  public interface IMyShipDrill : IMyTerminalBlock { }
  public interface IMyShipToolBase : IMyTerminalBlock { }
  public interface IMyShipWelder : IMyShipToolBase { }
  public interface IMyShipGrinder : IMyShipToolBase { }
  public interface IMyReactor : IMyTerminalBlock { }
  public interface IMyGasGenerator : IMyTerminalBlock { }
  public interface IMyUserControllableGun : IMyTerminalBlock { }
  public interface IMyLargeTurretBase : IMyUserControllableGun { }
  public interface IMyCockpit : IMyTerminalBlock { }
  public interface IMyCollector : IMyTerminalBlock { }
  public interface IMyParachute : IMyTerminalBlock { }

  public interface IMyShipConnector : IMyTerminalBlock {
    bool ThrowOut { get; set; }
    IMyShipConnector OtherConnector { get; }
    MyShipConnectorStatus Status { get; }
  }

  public interface IMyProductionBlock : IMyTerminalBlock {
    IMyInventory InputInventory { get; }
    IMyInventory OutputInventory { get; }
    bool IsProducing { get; }
    void AddQueueItem(MyDefinitionId blueprint, MyFixedPoint amount);
    void GetQueue(List<MyProductionItem> items);
    bool CanUseBlueprint(MyDefinitionId blueprint);
  }

  public interface IMyAssembler : IMyProductionBlock { MyAssemblerMode Mode { get; set; } }
  public interface IMyRefinery : IMyProductionBlock { }

  public enum ContentType { NONE, TEXT_AND_IMAGE, SCRIPT }
  public enum TextAlignment { LEFT, CENTER, RIGHT }

  public interface IMyTextSurface {
    ContentType ContentType { get; set; }
    string Font { get; set; }
    float FontSize { get; set; }
    float TextPadding { get; set; }
    TextAlignment Alignment { get; set; }
    string FontColor { get; set; }
    bool WriteText(string value, bool append = false);
  }

  public interface IMyTextSurfaceProvider { IMyTextSurface GetSurface(int index); int SurfaceCount { get; } }
  public interface IMyProgrammableBlock : IMyTerminalBlock, IMyTextSurfaceProvider { }

  public interface IMyGridTerminalSystem {
    void GetBlocks(List<IMyTerminalBlock> blocks);
    void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class;
    IMyTerminalBlock GetBlockWithName(string name);
  }

  public struct MyIniKey { public string Name { get { return null; } } public string Section { get { return null; } } }

  public struct MyIniParseResult {
    public int LineNo { get { return 0; } }
    public string Error { get { return null; } }
  }

  public struct MyIniValue {
    public bool ToBoolean(bool def = false) { return def; }
    public int ToInt32(int def = 0) { return def; }
    public double ToDouble(double def = 0) { return def; }
    public string ToString(string def) { return def; }
    public override string ToString() { return null; }
  }

  public class MyIni {
    public bool TryParse(string content) { return false; }
    public bool TryParse(string content, out MyIniParseResult result) { result = default(MyIniParseResult); return false; }
    public MyIniValue Get(string section, string key) { return default(MyIniValue); }
    public void Set(string section, string key, string value) { }
    public void Set(string section, string key, int value) { }
    public void Set(string section, string key, double value) { }
    public void Set(string section, string key, bool value) { }
    public void GetSections(List<string> sections) { }
    public void GetKeys(List<MyIniKey> keys) { }
    public void GetKeys(string section, List<MyIniKey> keys) { }
    public bool ContainsSection(string section) { return false; }
    public bool ContainsKey(string section, string key) { return false; }
    public void DeleteSection(string section) { }
    public void Delete(string section, string key) { }
    public void Clear() { }
    public override string ToString() { return null; }
  }

  public class MyGridProgramRuntimeInfo {
    public UpdateFrequency UpdateFrequency { get; set; }
    public int CurrentInstructionCount { get { return 0; } }
    public int MaxInstructionCount { get { return 0; } }
    public TimeSpan TimeSinceLastRun { get { return TimeSpan.Zero; } }
    public double LastRunTimeMs { get { return 0; } }
  }

  // The shape the game wraps a PB script in.
  public abstract class MyGridProgram {
    public IMyGridTerminalSystem GridTerminalSystem { get { return null; } }
    public IMyProgrammableBlock Me { get { return null; } }
    public MyGridProgramRuntimeInfo Runtime { get { return null; } }
    public void Echo(string text) { }
    public string Storage { get; set; }
  }
}

namespace VRage.Game.ModAPI.Ingame { }
namespace VRageMath { }
