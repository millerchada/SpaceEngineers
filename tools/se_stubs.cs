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

  // VERIFIED THE HARD WAY (2026-09-19, in-game compile): MyDefinitionId.TypeId is a
  // VRage.ObjectBuilders.MyObjectBuilderType STRUCT, not a string. This file described it as
  // a string, so `d.TypeId == null ? "" : d.TypeId` compiled locally and the GAME reported
  // "Operator '==' is ambiguous" and "no implicit conversion between 'string' and
  // 'MyObjectBuilderType'". Use TypeId.ToString(). MyItemType.TypeId below IS a string - the
  // two are different types and only this one is a struct.
  public struct MyDefinitionId {
    public VRage.ObjectBuilders.MyObjectBuilderType TypeId { get { return default(VRage.ObjectBuilders.MyObjectBuilderType); } }
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
    bool IsStatic { get; }
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

  // Verified: VRage.Game.ModAPI.Ingame.IMyEntity exposes Components, typed
  // VRage.Game.Components.Interfaces.IMyEntityComponentContainer, which derives from
  // IMyComponentContainer. TryGet<T>(out T) is declared on IMyComponentContainer and its type
  // parameter is UNCONSTRAINED - so `block.Components.TryGet(out chat)` binds with T inferred
  // as the component interface. Reproduced as one type here because the stubs describe shapes,
  // not the real inheritance chain.
  public interface IMyComponentContainer {
    bool TryGet<T>(out T component);
    bool Contains(Type type);
  }

  public interface IMyTerminalBlock {
    IMyComponentContainer Components { get; }
    string CustomName { get; set; }
    string CustomData { get; set; }
    string DetailedInfo { get; }
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

  // BROADCAST CONTROLLER CHAT API. Verified against the shipped game assemblies on
  // 2026-09-12, not guessed from terminal properties:
  //   Sandbox.Common.dll  Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent
  //     BroadcastTarget {get;set;}  CustomName {get;set;}  UseAntenna {get;set;}
  //     MaxMessageCount {get;}  GetMessage(int)  SetMessage(int,string)
  //     SendMessage(int)  SendMessage(string)  SendGps()  SendRandomMessage()
  //   Sandbox.ModAPI.Ingame.BroadcastTarget = { Owner, Faction, Everyone }
  //   Sandbox.ModAPI.Ingame.IMyBroadcastControllerBlock is a MARKER interface: it declares no
  //     members at all, which is why the chat surface has to be reached through the component.
  // SendMessage returns void. There is no delivery acknowledgement anywhere in this API.
  public enum BroadcastTarget { Owner, Faction, Everyone }

  public interface IMyChatBroadcastControllerComponent {
    BroadcastTarget BroadcastTarget { get; set; }
    string CustomName { get; set; }
    bool UseAntenna { get; set; }
    int MaxMessageCount { get; }
    string GetMessage(int messageIndex);
    void SetMessage(int messageIndex, string message);
    void SendMessage(int messageIndex);
    void SendMessage(string message);
    void SendGps();
    void SendRandomMessage();
  }

  public interface IMyBroadcastControllerBlock : IMyTerminalBlock { }

  // Verified: Sandbox.ModAPI.Ingame.IMyFunctionalBlock declares Enabled { get; set; } and
  // RequestEnable(bool). This stub file has historically carried Enabled on IMyTerminalBlock
  // instead, which compiles the same for every existing use; the real hierarchy is recorded
  // here because IOPM's antenna wake depends specifically on Enabled being SETTABLE on a
  // radio antenna, and IMyRadioAntenna really does derive from IMyFunctionalBlock.
  public interface IMyFunctionalBlock : IMyTerminalBlock { void RequestEnable(bool enable); }

  // Verified: Sandbox.ModAPI.Ingame.IMyRadioAntenna exposes Radius, ShowShipName,
  // IsBroadcasting (get only), EnableBroadcasting, HudText - plus the inherited working state,
  // and Enabled via IMyFunctionalBlock.
  public interface IMyRadioAntenna : IMyFunctionalBlock {
    float Radius { get; set; }
    bool ShowShipName { get; set; }
    bool IsBroadcasting { get; }
    bool EnableBroadcasting { get; set; }
    string HudText { get; set; }
  }


  // ---------------------------------------------------------------------------------------
  // POWER. Added for IO_Power_Control. Verified shapes:
  //   Sandbox.ModAPI.Ingame.IMyPowerProducer : IMyFunctionalBlock
  //     float CurrentOutput {get;}   float MaxOutput {get;}       -- both in MW
  //   Sandbox.ModAPI.Ingame.IMyBatteryBlock : IMyPowerProducer
  //     CurrentStoredPower/MaxStoredPower (MWh), CurrentInput/MaxInput (MW),
  //     ChargeMode {get;set;}, IsCharging {get;}, HasCapacityRemaining {get;}
  //   Sandbox.ModAPI.Ingame.ChargeMode = { Auto, Recharge, Discharge }
  // There is NO ingame interface for a power CONSUMER and no per-block consumption property.
  // DetailedInfo (above) is the only per-block electrical figure the PB API exposes at all.
  public interface IMyPowerProducer : IMyFunctionalBlock {
    float CurrentOutput { get; }
    float MaxOutput { get; }
  }

  public enum ChargeMode { Auto, Recharge, Discharge }

  public interface IMyBatteryBlock : IMyPowerProducer {
    float CurrentStoredPower { get; }
    float MaxStoredPower { get; }
    float CurrentInput { get; }
    float MaxInput { get; }
    ChargeMode ChargeMode { get; set; }
    bool IsCharging { get; }
    bool HasCapacityRemaining { get; }
  }

  public interface IMySolarPanel : IMyPowerProducer { }

  public interface IMyJumpDrive : IMyFunctionalBlock {
    float CurrentStoredPower { get; }
    float MaxStoredPower { get; }
  }

  // Ship control. GetShipSpeed() is the only velocity figure used here: it avoids pulling
  // VRageMath.Vector3D into the stubs for a single magnitude.
  public interface IMyShipController : IMyTerminalBlock {
    bool IsUnderControl { get; }
    bool IsMainCockpit { get; set; }
    bool CanControlShip { get; }
    double GetShipSpeed();
  }
  public interface IMyRemoteControl : IMyShipController { }

  public interface IMyThrust : IMyFunctionalBlock {
    float MaxEffectiveThrust { get; }
    float MaxThrust { get; }
    float CurrentThrust { get; }
  }
  public interface IMyGyro : IMyFunctionalBlock { }
  public interface IMyShipMergeBlock : IMyFunctionalBlock { bool IsConnected { get; } }
  public interface IMyAirVent : IMyFunctionalBlock { bool CanPressurize { get; } float GetOxygenLevel(); }
  public interface IMyGasTank : IMyFunctionalBlock { double FilledRatio { get; } bool Stockpile { get; set; } }
  public interface IMyLightingBlock : IMyFunctionalBlock { }
  public interface IMyDoor : IMyFunctionalBlock { }
  public interface IMyBeacon : IMyFunctionalBlock { }
  public interface IMyLaserAntenna : IMyFunctionalBlock { }
  public interface IMySensorBlock : IMyFunctionalBlock { }
  public interface IMyCameraBlock : IMyFunctionalBlock { }
  public interface IMyOreDetector : IMyFunctionalBlock { }
  public interface IMyProjector : IMyFunctionalBlock { }
  public interface IMyGravityGeneratorBase : IMyFunctionalBlock { }
  public interface IMyConveyorSorter : IMyFunctionalBlock { }
  public interface IMyTimerBlock : IMyFunctionalBlock { }
  public interface IMyMotorSuspension : IMyFunctionalBlock { }
  public interface IMyPistonBase : IMyFunctionalBlock { }
  public interface IMyMotorBase : IMyFunctionalBlock { }
  public interface IMyMotorStator : IMyMotorBase { }
  public interface IMyUpgradeModule : IMyFunctionalBlock { }
  public interface IMyTextPanel : IMyTerminalBlock, IMyTextSurface, IMyTextSurfaceProvider { }
  public interface IMyMedicalRoom : IMyFunctionalBlock { }

  public interface IMyCargoContainer : IMyTerminalBlock { }
  public interface IMyShipDrill : IMyTerminalBlock { }
  public interface IMyShipToolBase : IMyTerminalBlock { }
  public interface IMyShipWelder : IMyShipToolBase { }
  public interface IMyShipGrinder : IMyShipToolBase { }
  public interface IMyReactor : IMyPowerProducer { bool UseConveyorSystem { get; set; } }
  public interface IMyGasGenerator : IMyTerminalBlock { }
  public interface IMyUserControllableGun : IMyTerminalBlock { }
  public interface IMyLargeTurretBase : IMyUserControllableGun { }
  public interface IMyCockpit : IMyShipController { }
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
    // Verified against Sandbox.Common.dll: IMyGridTerminalSystem.GetBlockWithId(long) really
    // does exist on the INGAME interface and returns IMyTerminalBlock. IOPM uses it to resolve
    // an interrupted antenna wake by EntityId, which a rename cannot defeat.
    IMyTerminalBlock GetBlockWithId(long id);
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
    public int MaxInstructionCount { get { return 50000; } }
    public TimeSpan TimeSinceLastRun { get { return TimeSpan.Zero; } }
    public double LastRunTimeMs { get { return 0; } }
  }

  // Minimal stand-ins so a TEST can construct and run a Program. The real game supplies these;
  // returning null was fine while the only job was compiling, but the constructor reads
  // Me.CustomData and writes Runtime.UpdateFrequency, so both must exist to run anything.
  public class StubProgrammableBlock : IMyProgrammableBlock {
    public IMyComponentContainer Components { get { return null; } }
    public string CustomName { get; set; }
    public string CustomData { get; set; }
    public string DetailedInfo { get { return ""; } }
    public long EntityId { get; set; }
    public bool IsWorking { get { return true; } }
    public bool IsFunctional { get { return true; } }
    public bool Enabled { get; set; }
    public bool HasInventory { get { return false; } }
    public int InventoryCount { get { return 0; } }
    public IMyCubeGrid CubeGrid { get; set; }
    public MyDefinitionId BlockDefinition { get { return default(MyDefinitionId); } }
    public IMyInventory GetInventory(int index) { return null; }
    public bool IsSameConstructAs(IMyTerminalBlock other) { return true; }
    public IMyTextSurface GetSurface(int index) { return null; }
    public int SurfaceCount { get { return 0; } }
    public StubProgrammableBlock() { CustomName = "PB"; CustomData = ""; EntityId = 1; }
  }

  public class StubGridTerminalSystem : IMyGridTerminalSystem {
    public void GetBlocks(List<IMyTerminalBlock> blocks) { if (blocks != null) blocks.Clear(); }
    public void GetBlocksOfType<T>(List<T> blocks, Func<T, bool> collect = null) where T : class {
      if (blocks != null) blocks.Clear();
    }
    public IMyTerminalBlock GetBlockWithName(string name) { return null; }
    public IMyTerminalBlock GetBlockWithId(long id) { return null; }
  }

  // The shape the game wraps a PB script in.
  public abstract class MyGridProgram {
    public IMyGridTerminalSystem GridTerminalSystem { get; set; }
    public IMyProgrammableBlock Me { get; set; }
    public MyGridProgramRuntimeInfo Runtime { get; set; }
    public void Echo(string text) { }
    public string Storage { get; set; }
    protected MyGridProgram() {
      GridTerminalSystem = new StubGridTerminalSystem();
      Me = new StubProgrammableBlock();
      Runtime = new MyGridProgramRuntimeInfo();
      Storage = "";
    }
  }
}

// Blocks that live in SpaceEngineers.Game.ModAPI.Ingame rather than Sandbox.ModAPI.Ingame.
// The game's PB wrapper has a using for this namespace; check_pb.py's preamble now does too.
namespace VRage.ObjectBuilders {
  // Opaque on purpose: the point of this shape is that it is NOT a string and cannot be
  // compared to null, so a script that treats it as one fails HERE instead of in the game.
  public struct MyObjectBuilderType {
    public override string ToString() { return null; }
  }
}

namespace SpaceEngineers.Game.ModAPI.Ingame {
  using Sandbox.ModAPI.Ingame;
  public interface IMyLandingGear : IMyFunctionalBlock { bool IsLocked { get; } bool IsParkingEnabled { get; set; } }
  public interface IMyOxygenFarm : IMyFunctionalBlock { }
  public interface IMyAirtightHangarDoor : IMyDoor { }
  public interface IMyOxygenGenerator : IMyGasGenerator { }
  public interface IMyOxygenTank : IMyGasTank { }
}

namespace VRage.Game.ModAPI.Ingame { }
namespace VRageMath { }
