// Endless Drill Mark I - Mechanical Commissioning Controller
// Space Engineers programmable block script
//
// - Discovers/validates rotors, piston, welder, projectors, ejector, storage.
// - Headless build: LCD discovery, rendering, and writes are removed.
// - Runs the physically confirmed walking cycle; never intentionally detaches
//   both rotors; uses piston position feedback, not fixed delays.
// - Reconstructs machine state from hardware after recompile/reload; requires
//   RESUME after reload; never resumes motion automatically.
// - Motion authorization is centralized in EvaluateSupervisor().
//
// Version history (full defect writeups live in project history, not here):
// 0.8.5  ExecuteControlledStop: fixed endpoint-stop and bottom-attach timing.
// 0.8.6  ExecuteControlledStop: welder stays on until parked and stable.
// 0.8.7  Construction assurance evaluates the pinned cycle projector by id,
//        not whatever SelectActiveProjector() currently favors.
// 0.8.8  Added storage-capacity hold/resume (aggregate), supervisor-wired.
// 0.8.9  Construction-progress timeout freezes during a storage hold.
// 0.8.10 Storage hold also triggers on max single cargo-container utilization.
// 0.8.11 Supervisor storage-hold reason reports the actual trigger(s).
// 0.8.12 Behavior-preserving size reduction only; no logic changes.
// 0.8.13 Retraction speed raised to 1.0 m/s (RETRACT_SPEED). Storage hold no
//        longer blocks Retract/AttachTop (non-ore-producing return leg);
//        Extend and all rotor-detach steps remain gated as before.
// 0.8.14 Storage hold now driven solely by max individual drill inventory
//        utilization, not cargo-container fullness or aggregate volume;
//        those remain diagnostic only. Retract/AttachTop exemptions unchanged.
// 0.8.15 Rig rebuild: bottom rotor mates at 0 deg (was 180 deg) after the
//        piston was physically reversed. BOTTOM_EXPECTED_DEG updated to
//        match; no other constant or logic changed.
//
// Commands:
// COMMISSION  - Run full validation without moving anything.
// START       - Begin automatic cycling with construction-assurance gating.
// PAUSE       - Stop piston and welder; preserve current physical state.
// RESUME      - Reconstruct state and continue.
// STOP        - Return to SAFE_RETRACTED, then stop.
// ESTOP       - Immediate piston/welder stop; requires RESET.
// RESET       - Clear a latched fault/E-stop after correcting the machine.
// STATUS      - Print current state.
// EJECTOR     - Enforce ejector sorter/connector settings.
// PROJECTORS  - Discover and report all projectors and lifecycle state.
// REPORT      - Write a full copyable diagnostic report to Custom Data.
// LCDS        - Disabled in this headless build.
// ONCE        - Run one construction-assured walking cycle and stop.
//
// Exact block names expected:
// Advanced Rotor (Bottom)
// Advanced Rotor (Top)
// Piston (Drills)
// Welder (Drill)
// Projector
// Drill Rail Projector
// Recursive Drill Projector
// Ejector Sorter
// Ejector Connector

const string VERSION = "0.8.15-headless";

const string TOP_ROTOR_NAME = "Advanced Rotor (Top)";
const string BOTTOM_ROTOR_NAME = "Advanced Rotor (Bottom)";
const string PISTON_NAME = "Piston (Drills)";
const string WELDER_NAME = "Welder (Drill)";

const string BASE_PROJECTOR_NAME = "Projector";
const string START_PROJECTOR_NAME = "Drill Rail Projector";
const string RECURSIVE_PROJECTOR_NAME = "Recursive Drill Projector";

const string EJECTOR_SORTER_NAME = "Ejector Sorter";
const string EJECTOR_CONNECTOR_NAME = "Ejector Connector";

const double TOP_EXPECTED_DEG = 0.0;
const double BOTTOM_EXPECTED_DEG = 0.0;
const double ROTOR_ANGLE_TOLERANCE_DEG = 2.0;

const float EXTEND_SPEED = 0.10f;
const float RETRACT_SPEED = -1.0f;

// Mechanically observed endpoints, not the piston API's MinLimit/MaxLimit
// (some modded pistons report those inconsistently).
const float RETRACTED_POSITION = 0.0f;
const float EXTENDED_POSITION = 7.3f;
const float RETRACTED_TOLERANCE = 0.30f;
const float EXTENDED_TOLERANCE = 0.10f;

const int ATTACH_STABLE_TICKS = 5;
const double PROJECTOR_STALL_WARNING_SECONDS = 45.0;

// A proven cycle completes 4 projected blocks; anchors are not transferred
// until at least this much progress is confirmed during the extension.
const int MIN_REQUIRED_BLOCKS_PER_CYCLE = 4;
const double CONSTRUCTION_PROGRESS_TIMEOUT_SECONDS = 35.0;

const double STALL_TIMEOUT_SECONDS = 8.0;
const float MIN_PROGRESS_METERS = 0.002f;

// Hysteresis (hold >=95%, resume <=80%) prevents chattering at one threshold.
const double STORAGE_HOLD_THRESHOLD = 0.95;
const double STORAGE_RESUME_THRESHOLD = 0.80;
const int STORAGE_EVAL_INTERVAL_TICKS = 6;

IMyMotorStator _topRotor;
IMyMotorStator _bottomRotor;
IMyPistonBase _piston;
IMyShipWelder _welder;

IMyProjector _baseProjector;
IMyProjector _startProjector;

readonly List<IMyProjector> _allProjectors = new List<IMyProjector>();
readonly List<IMyProjector> _recursiveProjectors = new List<IMyProjector>();

IMyProjector _activeProjector;
long _activeProjectorId = 0;

ProjectorLifecycleState _projectorLifecycle = ProjectorLifecycleState.Unavailable;
int _previousRemaining = -1;
int _currentRemaining = -1;
int _activeProgressDelta = 0;
double _progressSeconds = 0.0;
DateTime _activeLastProgressAt = DateTime.MinValue;
double _maxProgressGap = 0.0;
int _cycleStartRemaining = -1;
int _cycleEndRemaining = -1;
int _cycleBlocksCompleted = 0;

ConstructionAssuranceState _constructionAssurance = ConstructionAssuranceState.NotRequired;
string _constructionAssuranceReason = "Controller idle.";
int _constructionProgressThisCycle = 0;
long _cycleProjectorId = 0;

SupervisorDecision _supervisorDecision = SupervisorDecision.Hold;
string _supervisorReason = "Controller idle.";
OperationStep _supervisorRequestedOperation = OperationStep.None;

IMyConveyorSorter _ejectorSorter;
IMyShipConnector _ejectorConnector;

// Refreshed alongside DiscoverHardware() so newly built drill-head blocks
// on a growing recursive rail are picked up automatically.
readonly List<IMyShipDrill> _drills = new List<IMyShipDrill>();
readonly List<IMyCargoContainer> _cargoContainers = new List<IMyCargoContainer>();

bool _storageHoldActive = false;
double _storageUtilization = -1.0;
double _storageCurrentVolume = 0.0;
double _storageMaximumVolume = 0.0;
int _monitoredInventoryCount = 0;
int _storageEvalTickCounter = 0;
// Diagnostic only as of v0.8.14 - no longer used to activate or preserve
// storage hold. See _maximumDrillInventoryUtilization for the control signal.
double _maximumCargoContainerUtilization = -1.0;
bool _cargoContainerThresholdExceeded = false;

// The storage-hold control signal: highest utilization among monitored
// drill inventories, and which drill currently holds it.
double _maximumDrillInventoryUtilization = -1.0;
bool _drillInventoryThresholdExceeded = false;
string _maximumDrillInventoryName = "";
long _maximumDrillInventoryEntityId = 0;

readonly StringBuilder _status = new StringBuilder(4096);
readonly Queue<string> _events = new Queue<string>();

ControllerMode _mode = ControllerMode.Idle;
MachineState _state = MachineState.Unknown;
OperationStep _operation = OperationStep.None;

bool _faultLatched = false;
string _faultReason = "";

int _stableAttachTicks = 0;
long _stableAttachEntityId = 0;

float _lastPistonPosition = 0f;
double _secondsWithoutProgress = 0.0;

int _completedCycles = 0;
bool _runOneCycle = false;
bool _pendingBottomAttach = false;
bool _pendingTopAttach = false;

enum ControllerMode
{
    Idle,
    Running,
    Paused,
    ControlledStop,
    EmergencyStop,
    Fault
}

enum ProjectorLifecycleState
{
    Unavailable,
    Ready,
    Waiting,
    Progressing,
    Stalled,
    Complete
}

enum ConstructionAssuranceState
{
    NotRequired,
    Armed,
    Building,
    HoldForComponents,
    SafeToTransfer,
    Unavailable
}

enum SupervisorDecision
{
    Ready,
    Hold,
    Denied
}

enum ProjectorRole
{
    Base,
    Starter,
    Recursive,
    Unknown
}

enum OperationStep
{
    None,
    DetachBottom,
    Extend,
    AttachBottom,
    DetachTop,
    Retract,
    AttachTop
}

enum MachineState
{
    SafeRetracted,
    BottomReleasedRetracted,
    Extending,
    ExtendedTopAnchored,
    SafeExtended,
    TopReleasedExtended,
    Retracting,
    RetractedBottomAnchored,
    BothDetached,
    SafeBothAttachedMidStroke,
    InvalidAnchorMotion,
    Unknown
}

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    DiscoverHardware();
    UpdateProjectorLifecycle(0.0);
    RecomputeStorageUtilization();
    StopMotion();
    _state = DetectState();
    _operation = RecoverOperationFromState(_state);
    _lastPistonPosition = _piston != null ? _piston.CurrentPosition : 0f;

    Log("Controller loaded. Motion remains stopped.");
    Log("Detected state: " + StateLabel(_state));
    Log("Recovered operation: " + _operation.ToString());
}

public void Save()
{
    // Hardware is the source of truth on reload; only non-safety telemetry persists.
    Storage =
        "cycles=" + _completedCycles.ToString() + "\n" +
        "version=" + VERSION;
}

public void Main(string argument, UpdateType updateSource)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(argument))
            HandleCommand(argument.Trim().ToUpperInvariant());

        if ((updateSource & (UpdateType.Update10 | UpdateType.Update100 | UpdateType.Once)) != 0)
            Tick();

        RenderStatus();
        Echo(_status.ToString());
    }
    catch (Exception ex)
    {
        LatchFault("Unhandled script exception: " + ex.Message);
        StopMotion();
        RenderStatus();
        Echo(_status.ToString());
    }
}

void HandleCommand(string command)
{
    switch (command)
    {
        case "COMMISSION":
            DiscoverHardware();
            RunCommissioning();
            break;

        case "START":
            if (!CanLeaveFaultState()) return;
            DiscoverHardware();
            _state = DetectState();

            if (_state != MachineState.SafeRetracted)
            {
                Log("START rejected: machine must be SAFE_RETRACTED.");
                return;
            }

            _runOneCycle = false;
            _pendingBottomAttach = false;
            _pendingTopAttach = false;
            _operation = OperationStep.DetachBottom;
            _mode = ControllerMode.Running;
            ResetProgressWatch();
            BeginCycleTelemetry();
            Log("Automatic cycling started with construction assurance armed.");
            break;

        case "ONCE":
            if (!CanLeaveFaultState()) return;
            DiscoverHardware();
            _state = DetectState();

            if (_state != MachineState.SafeRetracted)
            {
                Log("ONCE rejected: machine must be SAFE_RETRACTED.");
                return;
            }

            _runOneCycle = true;
            _pendingBottomAttach = false;
            _pendingTopAttach = false;
            _operation = OperationStep.DetachBottom;
            _mode = ControllerMode.Running;
            ResetProgressWatch();
            BeginCycleTelemetry();
            Log("One-cycle test started with construction assurance armed.");
            break;

        case "PAUSE":
            StopMotion();
            SetWelder(false);
            _mode = ControllerMode.Paused;
            Log("Paused in physical state " + StateLabel(DetectState()) + ".");
            break;

        case "RESUME":
            if (!CanLeaveFaultState()) return;
            DiscoverHardware();
            _state = DetectState();

            if (!IsRecoverableState(_state))
            {
                LatchFault("Cannot resume from state " + StateLabel(_state) + ".");
                return;
            }

            _operation = RecoverOperationFromState(_state);
            _mode = ControllerMode.Running;
            ResetProgressWatch();
            Log("Resumed from " + StateLabel(_state) + " at operation " + _operation + ".");
            break;

        case "STOP":
            if (!CanLeaveFaultState()) return;
            DiscoverHardware();
            _mode = ControllerMode.ControlledStop;
            ResetProgressWatch();
            Log("Controlled stop requested.");
            break;

        case "ESTOP":
            StopMotion();
            SetWelder(false);
            _mode = ControllerMode.EmergencyStop;
            _faultLatched = true;
            _faultReason = "Emergency stop commanded.";
            Log("EMERGENCY STOP.");
            break;

        case "RESET":
            StopMotion();
            SetWelder(false);
            DiscoverHardware();
            _state = DetectState();

            if (_state == MachineState.BothDetached)
            {
                Log("RESET rejected: both rotors are detached.");
                return;
            }

            _faultLatched = false;
            _faultReason = "";
            _pendingBottomAttach = false;
            _pendingTopAttach = false;
            _operation = OperationStep.None;
            _mode = ControllerMode.Idle;
            ResetProgressWatch();
            Log("Fault/E-stop reset. State: " + StateLabel(_state) + ".");
            break;

        case "EJECTOR":
            DiscoverHardware();
            ConfigureEjector();
            break;

        case "STATUS":
            DiscoverHardware();
            DiscoverProjectors();
            UpdateProjectorLifecycle(0.0);
            _state = DetectState();
            EvaluateConstructionAssurance();
            EvaluateStorageCapacity(true);
            EvaluateSupervisor(_operation);
            break;

        case "PROJECTORS":
            DiscoverHardware();
            DiscoverProjectors();
            UpdateProjectorLifecycle(0.0);
            EvaluateConstructionAssurance();
            EvaluateSupervisor(_operation);
            LogProjectorReport();
            WriteProjectorReportToCustomData();
            break;

        case "REPORT":
            DiscoverHardware();
            DiscoverProjectors();
            UpdateProjectorLifecycle(0.0);
            EvaluateConstructionAssurance();
            EvaluateStorageCapacity(true);
            EvaluateSupervisor(_operation);
            WriteFullReportToCustomData();
            Log("Full diagnostic report written to Custom Data.");
            break;

        case "LCDS":
            Log("LCD subsystem is disabled in headless build " + VERSION + ".");
            break;

        default:
            Log("Unknown command: " + command);
            break;
    }
}

void Tick()
{
    if (!DiscoverHardware())
    {
        LatchFault("Required mechanical hardware is missing.");
        StopMotion();
        return;
    }

    _state = DetectState();
    UpdateProjectorLifecycle(Runtime.TimeSinceLastRun.TotalSeconds);
    EvaluateConstructionAssurance();
    EvaluateStorageCapacity(false);
    EvaluateSupervisor(_operation);

    if (_state == MachineState.BothDetached)
    {
        LatchFault("CATASTROPHIC STATE: both rotors detached.");
        StopMotion();
        return;
    }

    // Both rotors attached mid-stroke is a safe parked state, not a fault;
    // START/ONCE still require SAFE_RETRACTED.
    if (_state == MachineState.InvalidAnchorMotion)
    {
        LatchFault("Piston moving in a direction not supported by the attached anchor.");
        StopMotion();
        return;
    }

    if (_faultLatched ||
        _mode == ControllerMode.Fault ||
        _mode == ControllerMode.EmergencyStop ||
        _mode == ControllerMode.Paused ||
        _mode == ControllerMode.Idle)
    {
        StopMotion();
        SetWelder(false);
        return;
    }

    if (_mode == ControllerMode.ControlledStop)
    {
        ExecuteControlledStop();
        return;
    }

    if (_mode == ControllerMode.Running)
        ExecuteAutomaticCycle();
}

void ExecuteAutomaticCycle()
{
    EvaluateSupervisor(_operation);
    if (_supervisorDecision != SupervisorDecision.Ready)
    {
        StopMotion();

        if (_storageHoldActive)
        {
            // Storage hold always forces the welder off, overriding construction hold.
            SetWelder(false);
        }
        else
        {
            // Keep welding while waiting on components (top still attached), so
            // supplying them can clear the hold without the piston advancing blindly.
            bool constructionHold =
                _constructionAssurance == ConstructionAssuranceState.HoldForComponents &&
                _topRotor != null && _topRotor.IsAttached &&
                (_operation == OperationStep.Extend || _operation == OperationStep.AttachBottom);

            SetWelder(constructionHold);
        }

        if (_supervisorDecision == SupervisorDecision.Denied)
            LatchFault("Supervisor denied " + _operation + ": " + _supervisorReason);

        return;
    }

    // Each operation checks its own completion condition before commanding
    // motion or running stall detection.
    switch (_operation)
    {
        case OperationStep.None:
            _operation = RecoverOperationFromState(_state);
            if (_operation == OperationStep.None)
            {
                StopMotion();
                LatchFault("No legal operation can be recovered from " + StateLabel(_state) + ".");
            }
            return;

        case OperationStep.DetachBottom:
            StopMotion();

            if (_state == MachineState.BottomReleasedRetracted)
            {
                ResetProgressWatch();
                _operation = OperationStep.Extend;
                Log("Operation complete: bottom detached.");
                return;
            }

            if (_state != MachineState.SafeRetracted)
            {
                LatchFault("DetachBottom expected SAFE_RETRACTED, found " + StateLabel(_state) + ".");
                return;
            }

            if (!ValidateRotorForDetach(_bottomRotor, BOTTOM_EXPECTED_DEG, "Bottom"))
                return;

            if (!_topRotor.IsAttached)
            {
                LatchFault("Top rotor is not attached before bottom detach.");
                return;
            }

            SetWelder(true);
            _bottomRotor.Detach();
            Log("Command: detach bottom rotor.");
            return;

        case OperationStep.Extend:
            if (_state == MachineState.ExtendedTopAnchored)
            {
                StopMotion();
                ResetProgressWatch();
                _operation = OperationStep.AttachBottom;
                Log("Operation complete: piston extended.");
                return;
            }

            if (_state != MachineState.BottomReleasedRetracted &&
                _state != MachineState.Extending)
            {
                LatchFault("Extend expected top-anchored state, found " + StateLabel(_state) + ".");
                return;
            }

            if (!_topRotor.IsAttached || _bottomRotor.IsAttached)
            {
                LatchFault("Extend interlock failed: top must be attached and bottom detached.");
                return;
            }

            // Completion is checked above before motion/stall monitoring.
            if (IsExtended(_piston.CurrentPosition))
            {
                StopMotion();
                ResetProgressWatch();
                _operation = OperationStep.AttachBottom;
                Log("Operation complete: piston reached extended endpoint.");
                return;
            }

            SetWelder(true);
            CommandPiston(EXTEND_SPEED);
            WatchPistonProgress(true);
            return;

        case OperationStep.AttachBottom:
            StopMotion();

            if (_state == MachineState.SafeExtended)
            {
                if (!AttachmentStable(_bottomRotor))
                    return;

                ResetAttachStability();
                _operation = OperationStep.DetachTop;
                Log("Operation complete: bottom attachment stable.");
                return;
            }

            if (_state != MachineState.ExtendedTopAnchored)
            {
                LatchFault("AttachBottom expected EXTENDED_TOP_ANCHORED, found " + StateLabel(_state) + ".");
                return;
            }

            if (!ValidateRotorForAttach(_bottomRotor, BOTTOM_EXPECTED_DEG, "Bottom"))
                return;

            _bottomRotor.Attach();
            return;

        case OperationStep.DetachTop:
            StopMotion();

            if (_state == MachineState.TopReleasedExtended)
            {
                ResetProgressWatch();
                _operation = OperationStep.Retract;
                Log("Operation complete: top detached.");
                return;
            }

            if (_state != MachineState.SafeExtended)
            {
                LatchFault("DetachTop expected SAFE_EXTENDED, found " + StateLabel(_state) + ".");
                return;
            }

            if (!ValidateRotorForDetach(_topRotor, TOP_EXPECTED_DEG, "Top"))
                return;

            if (!_bottomRotor.IsAttached)
            {
                LatchFault("Bottom rotor is not attached before top detach.");
                return;
            }

            _topRotor.Detach();
            Log("Command: detach top rotor.");
            return;

        case OperationStep.Retract:
            if (_state == MachineState.RetractedBottomAnchored)
            {
                StopMotion();
                ResetProgressWatch();
                _operation = OperationStep.AttachTop;
                Log("Operation complete: piston retracted.");
                return;
            }

            if (_state != MachineState.TopReleasedExtended &&
                _state != MachineState.Retracting)
            {
                LatchFault("Retract expected bottom-anchored state, found " + StateLabel(_state) + ".");
                return;
            }

            if (!_bottomRotor.IsAttached || _topRotor.IsAttached)
            {
                LatchFault("Retract interlock failed: bottom must be attached and top detached.");
                return;
            }

            // Completion is checked above before motion/stall monitoring.
            if (IsRetracted(_piston.CurrentPosition))
            {
                StopMotion();
                ResetProgressWatch();
                _operation = OperationStep.AttachTop;
                Log("Operation complete: piston reached retracted endpoint.");
                return;
            }

            SetWelder(true);
            CommandPiston(RETRACT_SPEED);
            WatchPistonProgress(false);
            return;

        case OperationStep.AttachTop:
            StopMotion();

            if (_state == MachineState.SafeRetracted)
            {
                if (!AttachmentStable(_topRotor))
                    return;

                ResetAttachStability();
                CompleteCycleTelemetry();
                _completedCycles++;
                Log("Operation complete: top attachment stable. Cycle " + _completedCycles + ".");

                if (_runOneCycle)
                {
                    _runOneCycle = false;
                    _operation = OperationStep.None;
                    _mode = ControllerMode.Idle;
                    SetWelder(false);
                    Log("One-cycle test complete.");
                    return;
                }

                _operation = OperationStep.DetachBottom;
                BeginCycleTelemetry();
                return;
            }

            if (_state != MachineState.RetractedBottomAnchored)
            {
                LatchFault("AttachTop expected RETRACTED_BOTTOM, found " + StateLabel(_state) + ".");
                return;
            }

            if (!ValidateRotorForAttach(_topRotor, TOP_EXPECTED_DEG, "Top"))
                return;

            _topRotor.Attach();
            return;
    }
}

OperationStep RecoverOperationFromState(MachineState state)
{
    switch (state)
    {
        case MachineState.SafeRetracted:
            return OperationStep.DetachBottom;
        case MachineState.BottomReleasedRetracted:
        case MachineState.Extending:
            return OperationStep.Extend;
        case MachineState.ExtendedTopAnchored:
            return OperationStep.AttachBottom;
        case MachineState.SafeExtended:
            return OperationStep.DetachTop;
        case MachineState.TopReleasedExtended:
        case MachineState.Retracting:
            return OperationStep.Retract;
        case MachineState.RetractedBottomAnchored:
            return OperationStep.AttachTop;
        default:
            return OperationStep.None;
    }
}

void ExecuteControlledStop()
{
    // Controlled stop always tries to return to both attached and retracted.
    switch (_state)
    {
        case MachineState.SafeRetracted:
            StopMotion();

            // Keep welding until top-attachment stability is confirmed: the
            // machine isn't truly parked yet, so components may still be needed.
            if (_pendingTopAttach)
            {
                SetWelder(true);

                if (!AttachmentStable(_topRotor))
                    return;

                _pendingTopAttach = false;
                ResetAttachStability();
                Log("Controlled stop: top attachment stable.");
            }

            SetWelder(false);
            _operation = OperationStep.None;
            _mode = ControllerMode.Idle;
            _runOneCycle = false;
            Log("Controlled stop complete: SAFE_RETRACTED.");
            break;

        case MachineState.BottomReleasedRetracted:
        case MachineState.Extending:
            // Finish reaching the extended checkpoint before transferring anchor.
            if (!_topRotor.IsAttached)
            {
                LatchFault("Controlled stop cannot continue: top anchor lost.");
                return;
            }

            // Must check completion before re-arming motion (as the automatic
            // cycle does), or the piston drives against its endpoint forever.
            if (IsExtended(_piston.CurrentPosition))
            {
                StopMotion();
                ResetProgressWatch();
                return;
            }

            SetWelder(true);
            CommandPiston(EXTEND_SPEED);
            WatchPistonProgress(true);
            break;

        case MachineState.ExtendedTopAnchored:
            // Bottom attachment is only mechanically valid at the extended
            // endpoint. Mirrors the RETRACTED_BOTTOM -> top-attach case below.
            StopMotion();
            SetWelder(true);
            _pendingBottomAttach = true;
            _bottomRotor.Attach();
            break;

        case MachineState.SafeExtended:
            StopMotion();
            SetWelder(true);

            if (_pendingBottomAttach)
            {
                if (!AttachmentStable(_bottomRotor))
                    return;

                _pendingBottomAttach = false;
                ResetAttachStability();
                Log("Controlled stop: bottom attachment stable.");
                return;
            }

            _topRotor.Detach();
            ResetAttachStability();
            Log("Controlled stop: detached top rotor.");
            break;

        case MachineState.TopReleasedExtended:
        case MachineState.Retracting:
            if (!_bottomRotor.IsAttached)
            {
                LatchFault("Controlled stop cannot retract: bottom anchor lost.");
                return;
            }

            // Same completion-before-motion fix as the extend side above.
            if (IsRetracted(_piston.CurrentPosition))
            {
                StopMotion();
                ResetProgressWatch();
                return;
            }

            SetWelder(true);
            CommandPiston(RETRACT_SPEED);
            WatchPistonProgress(false);
            break;

        case MachineState.RetractedBottomAnchored:
            StopMotion();
            SetWelder(true);
            _pendingTopAttach = true;
            _topRotor.Attach();
            break;

        case MachineState.SafeBothAttachedMidStroke:
            StopMotion();
            SetWelder(false);
            _mode = ControllerMode.Idle;
            Log("Controlled stop complete: safely parked with both rotors attached mid-stroke.");
            break;

        default:
            StopMotion();
            LatchFault("Controlled stop cannot recover state " + StateLabel(_state) + ".");
            break;
    }
}

MachineState DetectState()
{
    if (_topRotor == null || _bottomRotor == null || _piston == null)
        return MachineState.Unknown;

    bool top = _topRotor.IsAttached;
    bool bottom = _bottomRotor.IsAttached;

    if (!top && !bottom)
        return MachineState.BothDetached;

    float position = _piston.CurrentPosition;
    bool retracted = IsRetracted(position);
    bool extended = IsExtended(position);
    bool movingOut = _piston.Velocity > 0.001f;
    bool movingIn = _piston.Velocity < -0.001f;

    if (top && bottom)
    {
        if (movingOut || movingIn)
            return MachineState.SafeBothAttachedMidStroke;

        if (retracted)
            return MachineState.SafeRetracted;

        if (extended)
            return MachineState.SafeExtended;

        return MachineState.SafeBothAttachedMidStroke;
    }

    if (top && !bottom)
    {
        if (movingIn)
            return MachineState.InvalidAnchorMotion;

        if (retracted && !movingOut)
            return MachineState.BottomReleasedRetracted;

        if (extended && !movingOut)
            return MachineState.ExtendedTopAnchored;

        return MachineState.Extending;
    }

    if (!top && bottom)
    {
        if (movingOut)
            return MachineState.InvalidAnchorMotion;

        if (extended && !movingIn)
            return MachineState.TopReleasedExtended;

        if (retracted && !movingIn)
            return MachineState.RetractedBottomAnchored;

        return MachineState.Retracting;
    }

    return MachineState.Unknown;
}

bool IsRetracted(float position)
{
    return Math.Abs(position - RETRACTED_POSITION) <= RETRACTED_TOLERANCE;
}

bool IsExtended(float position)
{
    return Math.Abs(position - EXTENDED_POSITION) <= EXTENDED_TOLERANCE;
}

void CommandPiston(float velocity)
{
    if (_piston == null) return;
    _piston.Enabled = true;
    _piston.Velocity = velocity;
}

void StopMotion()
{
    if (_piston != null)
        _piston.Velocity = 0f;
}

void SetWelder(bool enabled)
{
    if (_welder != null && _welder.IsFunctional)
        _welder.Enabled = enabled;
}

void WatchPistonProgress(bool extending)
{
    if (_piston == null) return;

    float current = _piston.CurrentPosition;

    // Never diagnose a stall after the requested endpoint has been reached.
    if ((extending && IsExtended(current)) ||
        (!extending && IsRetracted(current)))
    {
        _secondsWithoutProgress = 0.0;
        _lastPistonPosition = current;
        return;
    }

    float delta = current - _lastPistonPosition;

    bool progressed = extending
        ? delta >= MIN_PROGRESS_METERS
        : delta <= -MIN_PROGRESS_METERS;

    if (progressed)
        _secondsWithoutProgress = 0.0;
    else
        _secondsWithoutProgress += Runtime.TimeSinceLastRun.TotalSeconds;

    _lastPistonPosition = current;

    if (_secondsWithoutProgress >= STALL_TIMEOUT_SECONDS)
    {
        string direction = extending ? "extension" : "retraction";
        LatchFault("Piston stalled during " + direction + " at " + current.ToString("0.00") + " m.");
        StopMotion();
    }
}

void ResetProgressWatch()
{
    _secondsWithoutProgress = 0.0;
    _lastPistonPosition = _piston != null ? _piston.CurrentPosition : 0f;
}

bool AttachmentStable(IMyMotorStator rotor)
{
    if (rotor == null)
        return false;

    if (!rotor.IsAttached)
    {
        ResetAttachStability();
        return false;
    }

    if (_stableAttachEntityId != rotor.EntityId)
    {
        _stableAttachEntityId = rotor.EntityId;
        _stableAttachTicks = 1;
        return false;
    }

    _stableAttachTicks++;
    return _stableAttachTicks >= ATTACH_STABLE_TICKS;
}

void ResetAttachStability()
{
    _stableAttachTicks = 0;
    _stableAttachEntityId = 0;
}

bool ValidateRotorBasic(IMyMotorStator rotor, string label)
{
    if (rotor == null)
    {
        LatchFault(label + " rotor missing.");
        return false;
    }

    if (!rotor.IsFunctional || !rotor.Enabled)
    {
        LatchFault(label + " rotor is disabled or damaged.");
        return false;
    }

    return true;
}

bool RequireShareInertiaTensor(IMyMotorStator rotor, string label)
{
    if (GetShareInertiaTensor(rotor))
        return true;

    LatchFault(label + " rotor Share Inertia Tensor is OFF.");
    return false;
}

bool ValidateRotorForDetach(IMyMotorStator rotor, double expectedAngle, string label)
{
    if (!ValidateRotorBasic(rotor, label))
        return false;

    if (!rotor.IsAttached)
    {
        LatchFault(label + " rotor is already detached.");
        return false;
    }

    if (!RequireShareInertiaTensor(rotor, label))
        return false;

    double angle = RotorAngleDegrees(rotor);
    if (AngularDifference(angle, expectedAngle) > ROTOR_ANGLE_TOLERANCE_DEG)
    {
        LatchFault(label + " rotor angle " + angle.ToString("0.0") +
                   " deg is outside expected " + expectedAngle.ToString("0.0") + " deg.");
        return false;
    }

    return true;
}

bool ValidateRotorForAttach(IMyMotorStator rotor, double expectedAngle, string label)
{
    // Detached rotors may not report a meaningful angle; it's checked right
    // after attachment via the regular commissioning/status path instead.
    if (!ValidateRotorBasic(rotor, label))
        return false;

    return RequireShareInertiaTensor(rotor, label);
}

bool GetShareInertiaTensor(IMyMotorStator rotor)
{
    if (rotor == null)
        return false;

    // Exposed only as a terminal property, not a direct IMyMotorStator member.
    return rotor.GetValueBool("ShareInertiaTensor");
}

double RotorAngleDegrees(IMyMotorStator rotor)
{
    double degrees = rotor.Angle * 180.0 / Math.PI;
    degrees %= 360.0;
    if (degrees < 0.0) degrees += 360.0;
    return degrees;
}

double AngularDifference(double a, double b)
{
    double diff = Math.Abs(a - b) % 360.0;
    return diff > 180.0 ? 360.0 - diff : diff;
}

bool DiscoverHardware()
{
    _topRotor = GridTerminalSystem.GetBlockWithName(TOP_ROTOR_NAME) as IMyMotorStator;
    _bottomRotor = GridTerminalSystem.GetBlockWithName(BOTTOM_ROTOR_NAME) as IMyMotorStator;
    _piston = GridTerminalSystem.GetBlockWithName(PISTON_NAME) as IMyPistonBase;
    _welder = GridTerminalSystem.GetBlockWithName(WELDER_NAME) as IMyShipWelder;

    _baseProjector = null;
    _startProjector = GridTerminalSystem.GetBlockWithName(START_PROJECTOR_NAME) as IMyProjector;
    DiscoverProjectors();

    _ejectorSorter = GridTerminalSystem.GetBlockWithName(EJECTOR_SORTER_NAME) as IMyConveyorSorter;
    _ejectorConnector = GridTerminalSystem.GetBlockWithName(EJECTOR_CONNECTOR_NAME) as IMyShipConnector;

    DiscoverStorageBlocks();

    return _topRotor != null && _bottomRotor != null && _piston != null;
}

void DiscoverStorageBlocks()
{
    _drills.Clear();
    _cargoContainers.Clear();

    GridTerminalSystem.GetBlocksOfType<IMyShipDrill>(
        _drills,
        block => block != null && block.IsSameConstructAs(Me));

    GridTerminalSystem.GetBlocksOfType<IMyCargoContainer>(
        _cargoContainers,
        block => block != null && block.IsSameConstructAs(Me));
}

void RunCommissioning()
{
    StopMotion();
    SetWelder(false);

    bool pass = true;

    pass &= CheckBlock(_topRotor, TOP_ROTOR_NAME, true);
    pass &= CheckBlock(_bottomRotor, BOTTOM_ROTOR_NAME, true);
    pass &= CheckBlock(_piston, PISTON_NAME, true);
    pass &= CheckBlock(_welder, WELDER_NAME, false);

    pass &= CheckBlock(_baseProjector, BASE_PROJECTOR_NAME, false);
    pass &= CheckBlock(_startProjector, START_PROJECTOR_NAME, false);
    if (_baseProjector == null)
        Log("INFO base projector could not be resolved.");
    else
        Log("PASS base projector resolved: " + _baseProjector.CustomName +
            " #" + _baseProjector.EntityId);

    if (_startProjector == null)
        Log("INFO starter projector not found.");
    else
        Log("PASS starter projector resolved: " + _startProjector.CustomName +
            " #" + _startProjector.EntityId);

    if (_recursiveProjectors.Count == 0)
        Log("INFO no recursive projectors found.");
    else
        Log("PASS recursive projectors found: " + _recursiveProjectors.Count);

    pass &= CheckBlock(_ejectorSorter, EJECTOR_SORTER_NAME, false);
    pass &= CheckBlock(_ejectorConnector, EJECTOR_CONNECTOR_NAME, false);

    if (_topRotor != null)
    {
        pass &= CommissionRotor(_topRotor, "Top", TOP_EXPECTED_DEG);
    }

    if (_bottomRotor != null)
    {
        pass &= CommissionRotor(_bottomRotor, "Bottom", BOTTOM_EXPECTED_DEG);
    }

    if (_piston != null)
    {
        if (!_piston.IsFunctional)
        {
            pass = false;
            Log("FAIL piston is not functional.");
        }

        Log("Piston limits: " + _piston.MinLimit.ToString("0.00") +
            " to " + _piston.MaxLimit.ToString("0.00") + " m.");
    }

    _state = DetectState();
    Log("Commissioning state: " + StateLabel(_state) + ".");

    if (!IsRecoverableState(_state))
    {
        pass = false;
        Log("FAIL machine is not in a recognized recoverable state.");
    }

    Log(pass ? "COMMISSIONING PASS." : "COMMISSIONING FAILED.");
}

bool CommissionRotor(IMyMotorStator rotor, string label, double expectedAngle)
{
    bool pass = true;

    if (!rotor.IsFunctional)
    {
        pass = false;
        Log("FAIL " + label + " rotor is not functional.");
    }

    if (!rotor.Enabled)
    {
        pass = false;
        Log("FAIL " + label + " rotor is disabled.");
    }

    if (!GetShareInertiaTensor(rotor))
    {
        pass = false;
        Log("FAIL " + label + " rotor Share Inertia Tensor is OFF.");
    }

    if (rotor.IsAttached)
    {
        double angle = RotorAngleDegrees(rotor);
        double error = AngularDifference(angle, expectedAngle);

        if (error > ROTOR_ANGLE_TOLERANCE_DEG)
        {
            pass = false;
            Log("FAIL " + label + " rotor angle " + angle.ToString("0.0") +
                " deg; expected " + expectedAngle.ToString("0.0") + " deg.");
        }
        else
        {
            Log("PASS " + label + " rotor angle " + angle.ToString("0.0") + " deg.");
        }
    }
    else
    {
        Log("INFO " + label + " rotor detached; angle check deferred.");
    }

    return pass;
}

bool CheckBlock(IMyTerminalBlock block, string name, bool required)
{
    if (block == null)
    {
        Log((required ? "FAIL missing required block: " : "INFO optional block not found: ") + name);
        return !required;
    }

    Log("PASS found: " + name);
    return true;
}

void DiscoverProjectors()
{
    _allProjectors.Clear();
    _recursiveProjectors.Clear();

    GridTerminalSystem.GetBlocksOfType<IMyProjector>(
        _allProjectors,
        projector => projector != null && projector.IsSameConstructAs(Me));

    var baseCandidates = new List<IMyProjector>();

    for (int i = 0; i < _allProjectors.Count; i++)
    {
        IMyProjector projector = _allProjectors[i];

        if (projector.CustomName == START_PROJECTOR_NAME)
        {
            _startProjector = projector;
            continue;
        }

        if (projector.CustomName == RECURSIVE_PROJECTOR_NAME)
        {
            _recursiveProjectors.Add(projector);
            continue;
        }

        if (projector.CustomName == BASE_PROJECTOR_NAME)
        {
            _baseProjector = projector;
            continue;
        }

        baseCandidates.Add(projector);
    }

    // Infer an auto-renamed base projector (e.g. "Projector 2") as the sole
    // remaining candidate that is neither the starter nor recursive.
    if (_baseProjector == null && baseCandidates.Count == 1)
        _baseProjector = baseCandidates[0];

    SelectActiveProjector();
}

ProjectorRole GetProjectorRole(IMyProjector projector)
{
    if (projector == null)
        return ProjectorRole.Unknown;

    if (_baseProjector != null && projector.EntityId == _baseProjector.EntityId)
        return ProjectorRole.Base;

    if (_startProjector != null && projector.EntityId == _startProjector.EntityId)
        return ProjectorRole.Starter;

    for (int i = 0; i < _recursiveProjectors.Count; i++)
    {
        if (projector.EntityId == _recursiveProjectors[i].EntityId)
            return ProjectorRole.Recursive;
    }

    return ProjectorRole.Unknown;
}

string ProjectorRoleLabel(IMyProjector projector)
{
    switch (GetProjectorRole(projector))
    {
        case ProjectorRole.Base: return "BASE";
        case ProjectorRole.Starter: return "STARTER";
        case ProjectorRole.Recursive: return "RECURSIVE";
        default: return "UNKNOWN";
    }
}

void SelectActiveProjector()
{
    IMyProjector selected = null;

    // Prefer an operational recursive projector.
    // If several exist, choose the one with the most unfinished work.
    int highestRemaining = -1;

    for (int i = 0; i < _recursiveProjectors.Count; i++)
    {
        IMyProjector projector = _recursiveProjectors[i];

        if (!IsOperationalProjector(projector))
            continue;

        if (projector.RemainingBlocks > highestRemaining)
        {
            selected = projector;
            highestRemaining = projector.RemainingBlocks;
        }
    }

    // Fall back to the starter projector only when no valid recursive projector exists.
    if (selected == null && IsOperationalProjector(_startProjector))
        selected = _startProjector;

    long selectedId = selected != null ? selected.EntityId : 0;

    if (selectedId != _activeProjectorId)
    {
        string previousName = _activeProjector != null
            ? _activeProjector.CustomName + " #" + _activeProjector.EntityId
            : "none";

        string selectedName = selected != null
            ? selected.CustomName + " #" + selected.EntityId
            : "none";

        Log("Active projector changed: " + previousName + " -> " + selectedName + ".");
        _activeProjectorId = selectedId;
        _previousRemaining = -1;
        _currentRemaining = -1;
        _activeProgressDelta = 0;
        _progressSeconds = 0.0;
        _activeLastProgressAt = DateTime.MinValue;
        _projectorLifecycle = selected != null
            ? ProjectorLifecycleState.Ready
            : ProjectorLifecycleState.Unavailable;
    }

    _activeProjector = selected;
}

bool IsOperationalProjector(IMyProjector projector)
{
    if (projector == null)
        return false;

    return projector.Enabled &&
           projector.IsFunctional &&
           projector.IsWorking &&
           projector.IsProjecting;
}

// Cadenced (~once/second) from Tick(); forceRecompute bypasses the cadence
// for on-demand STATUS/REPORT diagnostics.
void EvaluateStorageCapacity(bool forceRecompute)
{
    if (!forceRecompute)
    {
        _storageEvalTickCounter++;
        if (_storageEvalTickCounter < STORAGE_EVAL_INTERVAL_TICKS)
            return;
    }

    _storageEvalTickCounter = 0;
    RecomputeStorageUtilization();
}

void RecomputeStorageUtilization()
{
    double currentVolume = 0.0;
    double maxVolume = 0.0;
    int inventoryCount = 0;

    // Highest utilization among monitored drill inventories is the storage-
    // hold control signal: a single blocked drill is direct evidence of
    // conveyor/downstream backpressure, regardless of how the rest of the
    // fleet or bulk cargo happens to be distributed.
    _maximumDrillInventoryUtilization = -1.0;
    _maximumDrillInventoryName = "";
    _maximumDrillInventoryEntityId = 0;
    for (int i = 0; i < _drills.Count; i++)
    {
        AccumulateInventory(_drills[i], ref currentVolume, ref maxVolume, ref inventoryCount);

        double drillUtilization = DrillInventoryUtilization(_drills[i]);
        if (drillUtilization > _maximumDrillInventoryUtilization)
        {
            _maximumDrillInventoryUtilization = drillUtilization;
            _maximumDrillInventoryName = _drills[i].CustomName;
            _maximumDrillInventoryEntityId = _drills[i].EntityId;
        }
    }

    // Cargo-container and aggregate figures remain diagnostic only; they no
    // longer activate or preserve storage hold (see _maximumDrillInventoryUtilization).
    _maximumCargoContainerUtilization = -1.0;
    for (int i = 0; i < _cargoContainers.Count; i++)
    {
        AccumulateInventory(_cargoContainers[i], ref currentVolume, ref maxVolume, ref inventoryCount);

        double containerUtilization = CargoContainerUtilization(_cargoContainers[i]);
        if (containerUtilization > _maximumCargoContainerUtilization)
            _maximumCargoContainerUtilization = containerUtilization;
    }

    if (_ejectorConnector != null && _ejectorConnector.IsSameConstructAs(Me))
        AccumulateInventory(_ejectorConnector, ref currentVolume, ref maxVolume, ref inventoryCount);

    _monitoredInventoryCount = inventoryCount;
    _storageCurrentVolume = currentVolume;
    _storageMaximumVolume = maxVolume;
    _cargoContainerThresholdExceeded = _maximumCargoContainerUtilization >= STORAGE_HOLD_THRESHOLD;
    _drillInventoryThresholdExceeded = _maximumDrillInventoryUtilization >= STORAGE_HOLD_THRESHOLD;

    if (maxVolume <= 0.0)
    {
        // No monitored capacity: report unavailable rather than divide by
        // zero, and never leave a stale hold with nothing to measure.
        if (_storageHoldActive)
        {
            _storageHoldActive = false;
            Log("Storage hold cleared: monitored storage unavailable.");
        }

        _storageUtilization = -1.0;
        return;
    }

    _storageUtilization = currentVolume / maxVolume;

    // Drill inventory utilization is the sole hold/resume signal. Cargo and
    // aggregate figures are logged alongside it for context only.
    bool holdCondition = _drillInventoryThresholdExceeded;
    bool resumeCondition = _maximumDrillInventoryUtilization < 0.0 ||
        _maximumDrillInventoryUtilization <= STORAGE_RESUME_THRESHOLD;

    if (!_storageHoldActive && holdCondition)
    {
        _storageHoldActive = true;
        Log("Storage hold activated:\nDrill inventory utilization " + DrillInventoryUtilizationLabel() +
            ".\nMaximum cargo container utilization " + CargoContainerUtilizationLabel() +
            ".\nAggregate utilization " + (_storageUtilization * 100.0).ToString("0.0") + "%.");
    }
    else if (_storageHoldActive && resumeCondition)
    {
        _storageHoldActive = false;
        Log("Storage hold cleared:\nDrill inventory utilization " + DrillInventoryUtilizationLabel() +
            ".\nMaximum cargo container utilization " + CargoContainerUtilizationLabel() +
            ".\nAggregate utilization " + (_storageUtilization * 100.0).ToString("0.0") + "%. Resuming operation.");
    }
}

void AccumulateInventory(IMyTerminalBlock block, ref double currentVolume, ref double maxVolume, ref int inventoryCount)
{
    if (block == null || !block.HasInventory)
        return;

    IMyInventory inventory = block.GetInventory(0);
    if (inventory == null)
        return;

    currentVolume += (double)inventory.CurrentVolume;
    maxVolume += (double)inventory.MaxVolume;
    inventoryCount++;
}

double CargoContainerUtilization(IMyCargoContainer cargo)
{
    if (cargo == null || !cargo.HasInventory)
        return -1.0;

    IMyInventory inventory = cargo.GetInventory(0);
    if (inventory == null)
        return -1.0;

    double maxVolume = (double)inventory.MaxVolume;
    return maxVolume > 0.0 ? (double)inventory.CurrentVolume / maxVolume : -1.0;
}

double DrillInventoryUtilization(IMyShipDrill drill)
{
    if (drill == null || !drill.HasInventory)
        return -1.0;

    IMyInventory inventory = drill.GetInventory(0);
    if (inventory == null)
        return -1.0;

    double maxVolume = (double)inventory.MaxVolume;
    return maxVolume > 0.0 ? (double)inventory.CurrentVolume / maxVolume : -1.0;
}

string StorageUtilizationLabel()
{
    return _storageUtilization >= 0.0
        ? (_storageUtilization * 100.0).ToString("0.0") + "%"
        : "UNAVAILABLE";
}

string CargoContainerUtilizationLabel()
{
    return _maximumCargoContainerUtilization >= 0.0
        ? (_maximumCargoContainerUtilization * 100.0).ToString("0.0") + "%"
        : "UNAVAILABLE";
}

string DrillInventoryUtilizationLabel()
{
    return _maximumDrillInventoryUtilization >= 0.0
        ? (_maximumDrillInventoryUtilization * 100.0).ToString("0.0") + "%"
        : "UNAVAILABLE";
}

// Drill inventory utilization is the sole storage-hold trigger; cargo and
// aggregate figures are diagnostic only and are never reported as the cause.
string StorageHoldReason()
{
    string holdPct = (STORAGE_HOLD_THRESHOLD * 100.0).ToString("0.0");
    string resumePct = (STORAGE_RESUME_THRESHOLD * 100.0).ToString("0.0");

    if (_drillInventoryThresholdExceeded)
        return "STORAGEFULL: drill inventory utilization " + DrillInventoryUtilizationLabel() +
            " is at or above the " + holdPct + "% hold threshold; awaiting recovery to " +
            resumePct + "% or below.";

    // Hysteresis band: hold is still active but drill utilization has
    // dropped back below the hold threshold, not yet to the resume threshold.
    return "STORAGEFULL: drill inventory utilization " + DrillInventoryUtilizationLabel() +
        " has not yet recovered to " + resumePct + "% or below.";
}

// Resolves the pinned cycle projector by id, independent of whatever
// SelectActiveProjector() currently considers "active". Null if gone.
IMyProjector FindProjectorById(long entityId)
{
    if (entityId == 0)
        return null;

    for (int i = 0; i < _allProjectors.Count; i++)
    {
        if (_allProjectors[i].EntityId == entityId)
            return _allProjectors[i];
    }

    return null;
}

void EvaluateConstructionAssurance()
{
    if (_mode != ControllerMode.Running)
    {
        _constructionAssurance = ConstructionAssuranceState.NotRequired;
        _constructionAssuranceReason = "Construction assurance is inactive while the controller is not running.";
        return;
    }

    if (_cycleProjectorId == 0 || _cycleStartRemaining < 0)
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Current cycle has no valid projector baseline.";
        return;
    }

    IMyProjector cycleProjector = FindProjectorById(_cycleProjectorId);

    if (cycleProjector == null)
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Cycle projector is no longer present on the construct; operator validation required.";
        return;
    }

    if (!cycleProjector.IsFunctional)
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Cycle projector is not functional; operator validation required.";
        return;
    }

    int cycleRemaining = cycleProjector.RemainingBlocks;
    _constructionProgressThisCycle = Math.Max(0, _cycleStartRemaining - cycleRemaining);

    // A completed projection is sufficient construction evidence on its own,
    // even if it falls short of the normal minimum-block threshold.
    if (cycleRemaining <= 0)
    {
        _constructionAssurance = ConstructionAssuranceState.SafeToTransfer;
        _constructionAssuranceReason = "Cycle projector completed: " + _cycleStartRemaining +
            "/" + _cycleStartRemaining + " blocks built; completion satisfies construction evidence.";
        return;
    }

    if (!IsOperationalProjector(cycleProjector))
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Cycle projector stopped projecting with " + cycleRemaining +
            " block(s) unfinished; operator validation required.";
        return;
    }

    if (_constructionProgressThisCycle >= MIN_REQUIRED_BLOCKS_PER_CYCLE)
    {
        _constructionAssurance = ConstructionAssuranceState.SafeToTransfer;
        _constructionAssuranceReason = "Required rail progress confirmed: " +
            _constructionProgressThisCycle + "/" + MIN_REQUIRED_BLOCKS_PER_CYCLE + " blocks.";
        return;
    }

    bool extensionPhase =
        _operation == OperationStep.Extend &&
        (_state == MachineState.BottomReleasedRetracted ||
         _state == MachineState.Extending ||
         _state == MachineState.ExtendedTopAnchored);

    bool transferPhase =
        _operation == OperationStep.AttachBottom ||
        _operation == OperationStep.DetachTop;

    if (transferPhase || _state == MachineState.ExtendedTopAnchored ||
        _progressSeconds >= CONSTRUCTION_PROGRESS_TIMEOUT_SECONDS)
    {
        _constructionAssurance = ConstructionAssuranceState.HoldForComponents;
        _constructionAssuranceReason = "Construction hold: only " +
            _constructionProgressThisCycle + "/" + MIN_REQUIRED_BLOCKS_PER_CYCLE +
            " required blocks completed. Piston stopped; welder remains available.";
        return;
    }

    if (extensionPhase)
    {
        _constructionAssurance = ConstructionAssuranceState.Building;
        _constructionAssuranceReason = "Building projected rail: " +
            _constructionProgressThisCycle + "/" + MIN_REQUIRED_BLOCKS_PER_CYCLE + " blocks confirmed.";
        return;
    }

    _constructionAssurance = ConstructionAssuranceState.Armed;
    _constructionAssuranceReason = "Cycle armed; construction progress has not yet reached the transfer threshold.";
}

void EvaluateSupervisor(OperationStep requestedOperation)
{
    _supervisorRequestedOperation = requestedOperation;

    if (_faultLatched || _mode == ControllerMode.Fault || _mode == ControllerMode.EmergencyStop)
    {
        SetSupervisor(SupervisorDecision.Denied, "A fault or emergency stop is active.");
        return;
    }

    if (_mode == ControllerMode.Paused)
    {
        SetSupervisor(SupervisorDecision.Hold, "Controller is paused.");
        return;
    }

    if (_mode == ControllerMode.Idle)
    {
        SetSupervisor(SupervisorDecision.Hold, "Controller is idle.");
        return;
    }

    if (_topRotor == null || _bottomRotor == null || _piston == null)
    {
        SetSupervisor(SupervisorDecision.Denied, "Required mechanical hardware is missing.");
        return;
    }

    if (!_topRotor.IsFunctional || !_bottomRotor.IsFunctional || !_piston.IsFunctional)
    {
        SetSupervisor(SupervisorDecision.Denied, "Required mechanical hardware is not functional.");
        return;
    }

    if (_state == MachineState.BothDetached)
    {
        SetSupervisor(SupervisorDecision.Denied, "Both rotors are detached.");
        return;
    }

    // Storage hold outranks per-operation authorization (blocking rotor
    // attach/detach too), but only after every genuine denial above has
    // already had the chance to return first. Temporary hold, not a fault.
    // Retract and AttachTop are exempt: they are the non-ore-producing
    // return leg of the cycle (drill moving away from the cutting face,
    // then finishing the safe park), so storage hold must not prevent
    // finishing retraction and completing the current cycle.
    if (_storageHoldActive &&
        requestedOperation != OperationStep.Retract &&
        requestedOperation != OperationStep.AttachTop)
    {
        SetSupervisor(SupervisorDecision.Hold, StorageHoldReason());
        return;
    }

    switch (requestedOperation)
    {
        case OperationStep.None:
            SetSupervisor(SupervisorDecision.Hold, "No operation requested.");
            return;

        case OperationStep.DetachBottom:
            if (_state != MachineState.SafeRetracted &&
                _state != MachineState.BottomReleasedRetracted)
                SetSupervisor(SupervisorDecision.Denied, "Bottom detach requires the retracted transfer checkpoint.");
            else if (!_topRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Top anchor is not attached.");
            else
                SetSupervisor(SupervisorDecision.Ready, "Bottom detach authorized or awaiting confirmation.");
            return;

        case OperationStep.Extend:
            if (_state != MachineState.BottomReleasedRetracted &&
                _state != MachineState.Extending &&
                _state != MachineState.ExtendedTopAnchored)
                SetSupervisor(SupervisorDecision.Denied, "Extension is outside its legal motion envelope.");
            else if (!_topRotor.IsAttached || _bottomRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Extension requires top attached and bottom detached.");
            else if (_constructionAssurance == ConstructionAssuranceState.SafeToTransfer ||
                     _constructionAssurance == ConstructionAssuranceState.Building ||
                     _constructionAssurance == ConstructionAssuranceState.Armed)
                SetSupervisor(SupervisorDecision.Ready, "Extension authorized by construction assurance.");
            else
                SetSupervisor(SupervisorDecision.Hold, _constructionAssuranceReason);
            return;

        case OperationStep.AttachBottom:
            if (_state != MachineState.ExtendedTopAnchored &&
                _state != MachineState.SafeExtended)
                SetSupervisor(SupervisorDecision.Denied, "Bottom attachment requires the extended checkpoint.");
            else if (!_topRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Top anchor must remain attached during bottom attachment.");
            else if (_constructionAssurance != ConstructionAssuranceState.SafeToTransfer)
                SetSupervisor(SupervisorDecision.Hold, _constructionAssuranceReason);
            else
                SetSupervisor(SupervisorDecision.Ready, "Bottom attachment authorized; required rail progress confirmed.");
            return;

        case OperationStep.DetachTop:
            if (_state != MachineState.SafeExtended &&
                _state != MachineState.TopReleasedExtended)
                SetSupervisor(SupervisorDecision.Denied, "Top detach requires the extended transfer checkpoint.");
            else if (!_bottomRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Bottom anchor is not attached.");
            else if (_constructionAssurance != ConstructionAssuranceState.SafeToTransfer)
                SetSupervisor(SupervisorDecision.Hold, "Top detach blocked: " + _constructionAssuranceReason);
            else
                SetSupervisor(SupervisorDecision.Ready, "Top detach authorized; construction transfer gate satisfied.");
            return;

        case OperationStep.Retract:
            if (_state != MachineState.TopReleasedExtended &&
                _state != MachineState.Retracting &&
                _state != MachineState.RetractedBottomAnchored)
                SetSupervisor(SupervisorDecision.Denied, "Retraction is outside its legal motion envelope.");
            else if (!_bottomRotor.IsAttached || _topRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Retraction requires bottom attached and top detached.");
            else if (_storageHoldActive)
                SetSupervisor(SupervisorDecision.Ready, "Retraction authorized; storage hold applies only to ore-producing motion.");
            else
                SetSupervisor(SupervisorDecision.Ready, "Retraction authorized.");
            return;

        case OperationStep.AttachTop:
            if (_state != MachineState.RetractedBottomAnchored &&
                _state != MachineState.SafeRetracted)
                SetSupervisor(SupervisorDecision.Denied, "Top attachment requires the retracted checkpoint.");
            else if (!_bottomRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Bottom anchor must remain attached during top attachment.");
            else if (_storageHoldActive)
                SetSupervisor(SupervisorDecision.Ready, "Top attachment authorized; storage hold applies only to ore-producing motion.");
            else
                SetSupervisor(SupervisorDecision.Ready, "Top attachment authorized or awaiting confirmation.");
            return;
    }
}

void SetSupervisor(SupervisorDecision decision, string reason)
{
    _supervisorDecision = decision;
    _supervisorReason = reason;
}

void BeginCycleTelemetry()
{
    _cycleStartRemaining = _activeProjector != null ? _activeProjector.RemainingBlocks : -1;
    _cycleEndRemaining = _cycleStartRemaining;
    _cycleBlocksCompleted = 0;
    _constructionProgressThisCycle = 0;
    _cycleProjectorId = _activeProjector != null ? _activeProjector.EntityId : 0;
    _maxProgressGap = 0.0;
    _constructionAssurance = _activeProjector != null
        ? ConstructionAssuranceState.Armed
        : ConstructionAssuranceState.Unavailable;
    _constructionAssuranceReason = _activeProjector != null
        ? "Cycle armed; waiting for projected rail progress."
        : "No active construction projector at cycle start.";
}

void CompleteCycleTelemetry()
{
    IMyProjector cycleProjector = FindProjectorById(_cycleProjectorId);
    _cycleEndRemaining = cycleProjector != null ? cycleProjector.RemainingBlocks : -1;

    if (_cycleStartRemaining >= 0 && _cycleEndRemaining >= 0)
        _cycleBlocksCompleted = Math.Max(0, _cycleStartRemaining - _cycleEndRemaining);
    else
        _cycleBlocksCompleted = 0;

    Log("Cycle construction: " + _cycleStartRemaining + " -> " +
        _cycleEndRemaining + " remaining; completed=" + _cycleBlocksCompleted +
        "; max gap=" + _maxProgressGap.ToString("0.0") + " s.");
}

bool IsConstructionExpected()
{
    return _mode == ControllerMode.Running &&
           _operation == OperationStep.Extend &&
           (_state == MachineState.BottomReleasedRetracted ||
            _state == MachineState.Extending);
}

void UpdateProjectorLifecycle(double elapsedSeconds)
{
    if (_activeProjector == null || !IsOperationalProjector(_activeProjector))
    {
        _projectorLifecycle = ProjectorLifecycleState.Unavailable;
        _previousRemaining = -1;
        _currentRemaining = -1;
        _activeProgressDelta = 0;
        _progressSeconds = 0.0;
        return;
    }

    int remaining = _activeProjector.RemainingBlocks;

    if (_currentRemaining < 0)
    {
        _previousRemaining = remaining;
        _currentRemaining = remaining;
        _activeProgressDelta = 0;
        _progressSeconds = 0.0;
        _activeLastProgressAt = DateTime.Now;
        _projectorLifecycle = remaining <= 0
            ? ProjectorLifecycleState.Complete
            : ProjectorLifecycleState.Ready;
        return;
    }

    _previousRemaining = _currentRemaining;
    _currentRemaining = remaining;
    _activeProgressDelta = _previousRemaining - _currentRemaining;

    if (remaining <= 0)
    {
        _projectorLifecycle = ProjectorLifecycleState.Complete;
        _progressSeconds = 0.0;
        return;
    }

    if (_activeProgressDelta > 0)
    {
        if (_progressSeconds > _maxProgressGap)
            _maxProgressGap = _progressSeconds;

        _projectorLifecycle = ProjectorLifecycleState.Progressing;
        _progressSeconds = 0.0;
        _activeLastProgressAt = DateTime.Now;
        Log("Projector progress: " + _previousRemaining + " -> " +
            _currentRemaining + " remaining.");
        return;
    }

    if (_activeProgressDelta < 0)
    {
        _projectorLifecycle = ProjectorLifecycleState.Ready;
        _progressSeconds = 0.0;
        _activeLastProgressAt = DateTime.Now;
        Log("Projector remaining count increased: " + _previousRemaining +
            " -> " + _currentRemaining + ".");
        return;
    }

    // Freeze (don't reset) the progress clock during a storage hold: the lack
    // of progress is the intentional pause, not a real construction stall.
    if (!IsConstructionExpected() || _storageHoldActive)
    {
        _projectorLifecycle = _mode == ControllerMode.Idle
            ? ProjectorLifecycleState.Ready
            : ProjectorLifecycleState.Waiting;
        return;
    }

    _progressSeconds += Math.Max(0.0, elapsedSeconds);
    if (_progressSeconds > _maxProgressGap)
        _maxProgressGap = _progressSeconds;

    _projectorLifecycle = _progressSeconds >= PROJECTOR_STALL_WARNING_SECONDS
        ? ProjectorLifecycleState.Stalled
        : ProjectorLifecycleState.Waiting;
}

string ProjectorLifecycleLabel()
{
    return _projectorLifecycle.ToString().ToUpperInvariant();
}

void LogProjectorReport()
{
    Log("PROJECTOR REPORT BEGIN");
    Log("Total projectors on construct: " + _allProjectors.Count);
    Log("Recursive projectors: " + _recursiveProjectors.Count);

    if (_activeProjector == null)
        Log("Active projector: none.");
    else
        Log("Active projector: " + _activeProjector.CustomName +
            " | id=" + _activeProjector.EntityId +
            " | remaining=" + _activeProjector.RemainingBlocks +
            " | lifecycle=" + ProjectorLifecycleLabel());

    for (int i = 0; i < _allProjectors.Count; i++)
    {
        IMyProjector projector = _allProjectors[i];

        string role = ProjectorRoleLabel(projector);

        Log(
            role +
            " | name=" + projector.CustomName +
            " | id=" + projector.EntityId +
            " | functional=" + projector.IsFunctional +
            " | working=" + projector.IsWorking +
            " | enabled=" + projector.Enabled +
            " | projecting=" + projector.IsProjecting +
            " | remaining=" + projector.RemainingBlocks
        );
    }

    Log("PROJECTOR REPORT END");
}

void WriteProjectorReportToCustomData()
{
    var sb = new StringBuilder(8192);

    sb.AppendLine("ENDLESS DRILL PROJECTOR REPORT");
    sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    sb.AppendLine("Version: " + VERSION);
    sb.AppendLine();

    sb.AppendLine("SUMMARY");
    AppendProjectorSummary(sb);

    Me.CustomData = sb.ToString();
}

// Shared by WriteProjectorReportToCustomData() and WriteFullReportToCustomData();
// each caller writes its own distinct section header before calling this.
void AppendProjectorSummary(StringBuilder sb)
{
    sb.AppendLine("TotalProjectors=" + _allProjectors.Count);
    sb.AppendLine("RecursiveProjectors=" + _recursiveProjectors.Count);
    sb.AppendLine("ActiveProjectorName=" +
        (_activeProjector != null ? _activeProjector.CustomName : ""));
    sb.AppendLine("ActiveProjectorEntityId=" +
        (_activeProjector != null ? _activeProjector.EntityId.ToString() : "0"));
    sb.AppendLine("ActiveProjectorRole=" +
        (_activeProjector != null ? ProjectorRoleLabel(_activeProjector) : ""));
    sb.AppendLine("ActiveProjectorRemainingBlocks=" +
        (_activeProjector != null ? _activeProjector.RemainingBlocks.ToString() : "-1"));
    sb.AppendLine("ActiveProjectorLifecycle=" + ProjectorLifecycleLabel());
    sb.AppendLine("ActiveProjectorPreviousRemaining=" + _previousRemaining);
    sb.AppendLine("ActiveProjectorCurrentRemaining=" + _currentRemaining);
    sb.AppendLine("ActiveProjectorProgressDelta=" + _activeProgressDelta);
    sb.AppendLine("ActiveProjectorSecondsSinceProgress=" +
        _progressSeconds.ToString("0.0"));
    sb.AppendLine("ActiveProjectorLastProgressAt=" +
        (_activeLastProgressAt == DateTime.MinValue
            ? ""
            : _activeLastProgressAt.ToString("yyyy-MM-dd HH:mm:ss")));
    sb.AppendLine("ActiveProjectorMaximumProgressGap=" + _maxProgressGap.ToString("0.0"));
    sb.AppendLine("CycleStartRemaining=" + _cycleStartRemaining);
    sb.AppendLine("CycleEndRemaining=" + _cycleEndRemaining);
    sb.AppendLine("CycleBlocksCompleted=" + _cycleBlocksCompleted);
    sb.AppendLine("BaseProjectorName=" +
        (_baseProjector != null ? _baseProjector.CustomName : ""));
    sb.AppendLine("BaseProjectorEntityId=" +
        (_baseProjector != null ? _baseProjector.EntityId.ToString() : "0"));
    sb.AppendLine("StarterProjectorName=" +
        (_startProjector != null ? _startProjector.CustomName : ""));
    sb.AppendLine("StarterProjectorEntityId=" +
        (_startProjector != null ? _startProjector.EntityId.ToString() : "0"));
    sb.AppendLine();

    for (int i = 0; i < _allProjectors.Count; i++)
    {
        IMyProjector projector = _allProjectors[i];
        sb.AppendLine("[Projector." + i + "]");
        sb.AppendLine("Role=" + ProjectorRoleLabel(projector));
        sb.AppendLine("Name=" + projector.CustomName);
        sb.AppendLine("EntityId=" + projector.EntityId);
        sb.AppendLine("Enabled=" + projector.Enabled);
        sb.AppendLine("Functional=" + projector.IsFunctional);
        sb.AppendLine("Working=" + projector.IsWorking);
        sb.AppendLine("Projecting=" + projector.IsProjecting);
        sb.AppendLine("RemainingBlocks=" + projector.RemainingBlocks);
        sb.AppendLine();
    }
}

void WriteFullReportToCustomData()
{
    var sb = new StringBuilder(16384);

    sb.AppendLine("ENDLESS DRILL FULL DIAGNOSTIC REPORT");
    sb.AppendLine("Generated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    sb.AppendLine("Version=" + VERSION);
    sb.AppendLine();

    sb.AppendLine("[Controller]");
    sb.AppendLine("Mode=" + _mode);
    sb.AppendLine("State=" + StateLabel(_state));
    sb.AppendLine("Operation=" + _operation);
    sb.AppendLine("CompletedCycles=" + _completedCycles);
    sb.AppendLine("FaultLatched=" + _faultLatched);
    sb.AppendLine("FaultReason=" + _faultReason);
    sb.AppendLine("ProjectorLifecycle=" + ProjectorLifecycleLabel());
    sb.AppendLine("SupervisorDecision=" + _supervisorDecision.ToString().ToUpperInvariant());
    sb.AppendLine("SupervisorRequestedOperation=" + _supervisorRequestedOperation);
    sb.AppendLine("SupervisorReason=" + _supervisorReason);
    sb.AppendLine("ConstructionAssurance=" + _constructionAssurance.ToString().ToUpperInvariant());
    sb.AppendLine("ConstructionAssuranceReason=" + _constructionAssuranceReason);
    sb.AppendLine("ConstructionProgressThisCycle=" + _constructionProgressThisCycle);
    sb.AppendLine("ConstructionRequiredBlocks=" + MIN_REQUIRED_BLOCKS_PER_CYCLE);
    sb.AppendLine("ConstructionProgressTimeoutSeconds=" + CONSTRUCTION_PROGRESS_TIMEOUT_SECONDS.ToString("0.0"));
    sb.AppendLine("CycleProjectorEntityId=" + _cycleProjectorId);
    sb.AppendLine();

    sb.AppendLine("[Storage]");
    sb.AppendLine("StorageUtilizationPercent=" + StorageUtilizationLabel());
    sb.AppendLine("StorageHoldActive=" + _storageHoldActive);
    sb.AppendLine("StorageHoldThresholdPercent=" + (STORAGE_HOLD_THRESHOLD * 100.0).ToString("0.0"));
    sb.AppendLine("StorageResumeThresholdPercent=" + (STORAGE_RESUME_THRESHOLD * 100.0).ToString("0.0"));
    sb.AppendLine("MaximumDrillInventoryUtilizationPercent=" + DrillInventoryUtilizationLabel());
    sb.AppendLine("DrillInventoryThresholdExceeded=" + _drillInventoryThresholdExceeded);
    sb.AppendLine("DrillInventoryHoldThresholdPercent=" + (STORAGE_HOLD_THRESHOLD * 100.0).ToString("0.0"));
    sb.AppendLine("DrillInventoryResumeThresholdPercent=" + (STORAGE_RESUME_THRESHOLD * 100.0).ToString("0.0"));
    sb.AppendLine("MaximumDrillInventoryName=" + _maximumDrillInventoryName);
    sb.AppendLine("MaximumDrillInventoryEntityId=" + _maximumDrillInventoryEntityId);
    // Diagnostic only below - cargo/aggregate no longer activate or preserve storage hold.
    sb.AppendLine("MaximumCargoContainerUtilizationPercent=" + CargoContainerUtilizationLabel());
    sb.AppendLine("CargoContainerThresholdExceeded=" + _cargoContainerThresholdExceeded);
    sb.AppendLine("CargoContainerHoldThresholdPercent=" + (STORAGE_HOLD_THRESHOLD * 100.0).ToString("0.0"));
    sb.AppendLine("CargoContainerResumeThresholdPercent=" + (STORAGE_RESUME_THRESHOLD * 100.0).ToString("0.0"));
    sb.AppendLine("MonitoredDrillCount=" + _drills.Count);
    sb.AppendLine("MonitoredCargoContainerCount=" + _cargoContainers.Count);
    sb.AppendLine("MonitoredInventoryCount=" + _monitoredInventoryCount);
    sb.AppendLine("StorageCurrentVolume=" + _storageCurrentVolume.ToString("0.000"));
    sb.AppendLine("StorageMaximumVolume=" + _storageMaximumVolume.ToString("0.000"));
    sb.AppendLine();

    sb.AppendLine("[Piston]");
    if (_piston == null)
    {
        sb.AppendLine("Found=false");
    }
    else
    {
        sb.AppendLine("Found=true");
        sb.AppendLine("Name=" + _piston.CustomName);
        sb.AppendLine("Position=" + _piston.CurrentPosition.ToString("0.000"));
        sb.AppendLine("Velocity=" + _piston.Velocity.ToString("0.000"));
        sb.AppendLine("MinLimit=" + _piston.MinLimit.ToString("0.000"));
        sb.AppendLine("MaxLimit=" + _piston.MaxLimit.ToString("0.000"));
        sb.AppendLine("Functional=" + _piston.IsFunctional);
        sb.AppendLine("Working=" + _piston.IsWorking);
    }
    sb.AppendLine();

    AppendRotorReport(sb, "TopRotor", _topRotor);
    AppendRotorReport(sb, "BottomRotor", _bottomRotor);

    sb.AppendLine("[ProjectorSummary]");
    AppendProjectorSummary(sb);

    sb.AppendLine("[RecentEvents]");
    int eventIndex = 0;
    foreach (string line in _events)
    {
        sb.AppendLine("Event" + eventIndex + "=" + line);
        eventIndex++;
    }

    Me.CustomData = sb.ToString();
}

void AppendRotorReport(StringBuilder sb, string section, IMyMotorStator rotor)
{
    sb.AppendLine("[" + section + "]");

    if (rotor == null)
    {
        sb.AppendLine("Found=false");
        sb.AppendLine();
        return;
    }

    sb.AppendLine("Found=true");
    sb.AppendLine("Name=" + rotor.CustomName);
    sb.AppendLine("EntityId=" + rotor.EntityId);
    sb.AppendLine("Attached=" + rotor.IsAttached);
    sb.AppendLine("AngleDegrees=" + RotorAngleDegrees(rotor).ToString("0.000"));
    sb.AppendLine("Enabled=" + rotor.Enabled);
    sb.AppendLine("Functional=" + rotor.IsFunctional);
    sb.AppendLine("Working=" + rotor.IsWorking);
    sb.AppendLine("ShareInertiaTensor=" + GetShareInertiaTensor(rotor));
    sb.AppendLine();
}

void ConfigureEjector()
{
    if (_ejectorSorter == null || _ejectorConnector == null)
    {
        Log("Ejector configuration skipped: sorter or connector missing.");
        return;
    }

    _ejectorSorter.Enabled = true;
    _ejectorSorter.DrainAll = true;

    var filters = new List<MyInventoryItemFilter>();
    filters.Add(new MyInventoryItemFilter(MyDefinitionId.Parse("MyObjectBuilder_Ore/Stone")));
    filters.Add(new MyInventoryItemFilter(MyDefinitionId.Parse("MyObjectBuilder_Ingot/Stone")));
    _ejectorSorter.SetFilter(MyConveyorSorterMode.Whitelist, filters);

    _ejectorConnector.Enabled = true;
    _ejectorConnector.CollectAll = false;
    _ejectorConnector.ThrowOut = true;

    Log("Ejector enforced: whitelist Stone + Gravel, Drain All ON, Throw Out ON.");
}

bool CanLeaveFaultState()
{
    if (_faultLatched ||
        _mode == ControllerMode.Fault ||
        _mode == ControllerMode.EmergencyStop)
    {
        Log("Command rejected: run RESET after correcting the fault.");
        return false;
    }

    return true;
}

bool IsRecoverableState(MachineState state)
{
    return state == MachineState.SafeRetracted ||
           state == MachineState.BottomReleasedRetracted ||
           state == MachineState.Extending ||
           state == MachineState.ExtendedTopAnchored ||
           state == MachineState.SafeExtended ||
           state == MachineState.TopReleasedExtended ||
           state == MachineState.Retracting ||
           state == MachineState.RetractedBottomAnchored ||
           state == MachineState.SafeBothAttachedMidStroke;
}

void LatchFault(string reason)
{
    if (_faultLatched && _faultReason == reason)
        return;

    _faultLatched = true;
    _faultReason = reason;
    _mode = ControllerMode.Fault;
    StopMotion();
    SetWelder(false);
    Log("FAULT: " + reason);
}

void Log(string message)
{
    string line = DateTime.Now.ToString("HH:mm:ss") + " " + message;
    _events.Enqueue(line);

    while (_events.Count > 14)
        _events.Dequeue();
}

void RenderStatus()
{
    _status.Clear();

    _status.AppendLine("ENDLESS DRILL MK I  " + VERSION);
    _status.AppendLine("================================");
    _status.Append("Mode:  ").AppendLine(_mode.ToString().ToUpperInvariant());
    _status.Append("State: ").AppendLine(StateLabel(_state));
    _status.Append("Op:    ").AppendLine(_operation.ToString().ToUpperInvariant());

    if (_piston != null)
    {
        _status.Append("Piston: ")
            .Append(_piston.CurrentPosition.ToString("0.00"))
            .Append(" / ")
            .Append(EXTENDED_POSITION.ToString("0.00"))
            .Append(" m  v=")
            .Append(_piston.Velocity.ToString("0.00"))
            .AppendLine();

        _status.Append("API limits: ")
            .Append(_piston.MinLimit.ToString("0.00"))
            .Append(" .. ")
            .Append(_piston.MaxLimit.ToString("0.00"))
            .AppendLine(" m");
    }

    if (_topRotor != null)
    {
        _status.Append("Top:    ")
            .Append(_topRotor.IsAttached ? "ATTACHED" : "DETACHED")
            .Append("  ")
            .Append(RotorAngleDegrees(_topRotor).ToString("0.0"))
            .AppendLine(" deg");
    }

    if (_bottomRotor != null)
    {
        _status.Append("Bottom: ")
            .Append(_bottomRotor.IsAttached ? "ATTACHED" : "DETACHED")
            .Append("  ")
            .Append(RotorAngleDegrees(_bottomRotor).ToString("0.0"))
            .AppendLine(" deg");
    }

    _status.Append("Cycles: ").AppendLine(_completedCycles.ToString());

    _status.Append("Storage: ")
        .Append(StorageUtilizationLabel())
        .Append(_storageHoldActive ? "  HOLD" : "")
        .Append("  (drills=").Append(_drills.Count)
        .Append(" cargo=").Append(_cargoContainers.Count)
        .Append(" inv=").Append(_monitoredInventoryCount)
        .AppendLine(")");

    AppendProjectorStatus("Starter", _startProjector);
    _status.Append("Projectors: total=")
        .Append(_allProjectors.Count)
        .Append(" recursive=")
        .AppendLine(_recursiveProjectors.Count.ToString());

    if (_faultLatched)
    {
        _status.AppendLine("--------------------------------");
        _status.AppendLine("FAULT LATCHED");
        _status.AppendLine(_faultReason);
        _status.AppendLine("Correct problem, then run RESET.");
    }

    _status.AppendLine("--------------------------------");
    _status.AppendLine("Recent events:");

    foreach (string line in _events)
        _status.AppendLine(line);
}

void AppendProjectorStatus(string label, IMyProjector projector)
{
    if (projector == null)
        return;

    _status.Append(label)
        .Append(": ")
        .Append(projector.IsProjecting ? "PROJECTING" : "NO PROJECTION")
        .Append("  remaining=")
        .Append(projector.RemainingBlocks)
        .AppendLine();
}

string StateLabel(MachineState state)
{
    switch (state)
    {
        case MachineState.SafeRetracted: return "SAFE_RETRACTED";
        case MachineState.BottomReleasedRetracted: return "BOTTOM_RELEASED";
        case MachineState.Extending: return "EXTENDING";
        case MachineState.ExtendedTopAnchored: return "EXTENDED_TOP_ANCHORED";
        case MachineState.SafeExtended: return "SAFE_EXTENDED";
        case MachineState.TopReleasedExtended: return "TOP_RELEASED";
        case MachineState.Retracting: return "RETRACTING";
        case MachineState.RetractedBottomAnchored: return "RETRACTED_BOTTOM";
        case MachineState.BothDetached: return "CATASTROPHIC_BOTH_DETACHED";
        case MachineState.SafeBothAttachedMidStroke: return "SAFE_BOTH_ATTACHED_MID_STROKE";
        case MachineState.InvalidAnchorMotion: return "INVALID_ANCHOR_MOTION";
        default: return "UNKNOWN";
    }
}
