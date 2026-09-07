// Endless Drill Mk1 - Basic Controller
// Space Engineers programmable block script
//
// A minimal walking-drill controller for exactly five blocks:
// Advanced Rotor (Bottom), Advanced Rotor (Top), Piston (Drills),
// Welder (Drill), Drill Rail Projector.
//
// No storage/cargo/ejector monitoring and no multi-projector role system -
// this build has a single projector, referenced directly. Everything else
// (rotor sequencing, piston endpoint handling, construction-progress
// gating, controlled stop, commissioning, diagnostics) follows the same
// proven design as the full Endless Drill Mk1 Headless controller.
//
// Commands:
// COMMISSION - Run full validation without moving anything.
// START      - Begin automatic cycling with construction-assurance gating.
// PAUSE      - Stop piston and welder; preserve current physical state.
// RESUME     - Reconstruct state and continue.
// STOP       - Return to SAFE_RETRACTED, then stop.
// ESTOP      - Immediate piston/welder stop; requires RESET.
// RESET      - Clear a latched fault/E-stop after correcting the machine.
// STATUS     - Print current state.
// REPORT     - Write a full copyable diagnostic report to Custom Data.
// ONCE       - Run one construction-assured walking cycle and stop.
//
// Exact block names expected:
// Advanced Rotor (Bottom)
// Advanced Rotor (Top)
// Piston (Drills)
// Welder (Drill)
// Drill Rail Projector

const string VERSION = "1.0.0-basic";

const string TOP_ROTOR_NAME = "Advanced Rotor (Top)";
const string BOTTOM_ROTOR_NAME = "Advanced Rotor (Bottom)";
const string PISTON_NAME = "Piston (Drills)";
const string WELDER_NAME = "Welder (Drill)";
const string PROJECTOR_NAME = "Drill Rail Projector";

// Confirmed for the current rig build (piston facing up; bottom rotor mates
// at 0 deg on this orientation). Adjust if the rig is rebuilt again.
const double TOP_EXPECTED_DEG = 0.0;
const double BOTTOM_EXPECTED_DEG = 0.0;
const double ROTOR_ANGLE_TOLERANCE_DEG = 2.0;

const float EXTEND_SPEED = 0.10f;
const float RETRACT_SPEED = -1.0f;

// Mechanically observed endpoints, not the piston API's MinLimit/MaxLimit.
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

IMyMotorStator _topRotor;
IMyMotorStator _bottomRotor;
IMyPistonBase _piston;
IMyShipWelder _welder;
IMyProjector _projector;

ProjectorLifecycleState _projectorLifecycle = ProjectorLifecycleState.Unavailable;
int _previousRemaining = -1;
int _currentRemaining = -1;
int _progressDelta = 0;
double _progressSeconds = 0.0;
DateTime _lastProgressAt = DateTime.MinValue;
double _maxProgressGap = 0.0;
int _cycleStartRemaining = -1;
int _cycleEndRemaining = -1;
int _cycleBlocksCompleted = 0;

ConstructionAssuranceState _constructionAssurance = ConstructionAssuranceState.NotRequired;
string _constructionAssuranceReason = "Controller idle.";
int _constructionProgressThisCycle = 0;

SupervisorDecision _supervisorDecision = SupervisorDecision.Hold;
string _supervisorReason = "Controller idle.";
OperationStep _supervisorRequestedOperation = OperationStep.None;

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

        case "STATUS":
            DiscoverHardware();
            UpdateProjectorLifecycle(0.0);
            _state = DetectState();
            EvaluateConstructionAssurance();
            EvaluateSupervisor(_operation);
            break;

        case "REPORT":
            DiscoverHardware();
            UpdateProjectorLifecycle(0.0);
            EvaluateConstructionAssurance();
            EvaluateSupervisor(_operation);
            WriteFullReportToCustomData();
            Log("Full diagnostic report written to Custom Data.");
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

        // Keep welding while waiting on components (top still attached), so
        // supplying them can clear the hold without the piston advancing blindly.
        bool constructionHold =
            _constructionAssurance == ConstructionAssuranceState.HoldForComponents &&
            _topRotor != null && _topRotor.IsAttached &&
            (_operation == OperationStep.Extend || _operation == OperationStep.AttachBottom);

        SetWelder(constructionHold);

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
    _projector = GridTerminalSystem.GetBlockWithName(PROJECTOR_NAME) as IMyProjector;

    return _topRotor != null && _bottomRotor != null && _piston != null;
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
    pass &= CheckBlock(_projector, PROJECTOR_NAME, false);

    if (_projector == null)
        Log("INFO projector not found.");
    else
        Log("PASS projector resolved: " + _projector.CustomName +
            " #" + _projector.EntityId + " | remaining=" + _projector.RemainingBlocks);

    if (_topRotor != null)
        pass &= CommissionRotor(_topRotor, "Top", TOP_EXPECTED_DEG);

    if (_bottomRotor != null)
        pass &= CommissionRotor(_bottomRotor, "Bottom", BOTTOM_EXPECTED_DEG);

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

bool IsOperationalProjector(IMyProjector projector)
{
    if (projector == null)
        return false;

    return projector.Enabled &&
           projector.IsFunctional &&
           projector.IsWorking &&
           projector.IsProjecting;
}

void EvaluateConstructionAssurance()
{
    if (_mode != ControllerMode.Running)
    {
        _constructionAssurance = ConstructionAssuranceState.NotRequired;
        _constructionAssuranceReason = "Construction assurance is inactive while the controller is not running.";
        return;
    }

    if (_cycleStartRemaining < 0)
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Current cycle has no valid projector baseline.";
        return;
    }

    if (_projector == null)
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Projector is no longer present on the construct; operator validation required.";
        return;
    }

    if (!_projector.IsFunctional)
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Projector is not functional; operator validation required.";
        return;
    }

    int remaining = _projector.RemainingBlocks;
    _constructionProgressThisCycle = Math.Max(0, _cycleStartRemaining - remaining);

    // A completed projection is sufficient construction evidence on its own,
    // even if it falls short of the normal minimum-block threshold.
    if (remaining <= 0)
    {
        _constructionAssurance = ConstructionAssuranceState.SafeToTransfer;
        _constructionAssuranceReason = "Projector completed: " + _cycleStartRemaining +
            "/" + _cycleStartRemaining + " blocks built; completion satisfies construction evidence.";
        return;
    }

    if (!IsOperationalProjector(_projector))
    {
        _constructionAssurance = ConstructionAssuranceState.Unavailable;
        _constructionAssuranceReason = "Projector stopped projecting with " + remaining +
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
            else
                SetSupervisor(SupervisorDecision.Ready, "Retraction authorized.");
            return;

        case OperationStep.AttachTop:
            if (_state != MachineState.RetractedBottomAnchored &&
                _state != MachineState.SafeRetracted)
                SetSupervisor(SupervisorDecision.Denied, "Top attachment requires the retracted checkpoint.");
            else if (!_bottomRotor.IsAttached)
                SetSupervisor(SupervisorDecision.Denied, "Bottom anchor must remain attached during top attachment.");
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
    _cycleStartRemaining = _projector != null ? _projector.RemainingBlocks : -1;
    _cycleEndRemaining = _cycleStartRemaining;
    _cycleBlocksCompleted = 0;
    _constructionProgressThisCycle = 0;
    _maxProgressGap = 0.0;
    _constructionAssurance = _projector != null
        ? ConstructionAssuranceState.Armed
        : ConstructionAssuranceState.Unavailable;
    _constructionAssuranceReason = _projector != null
        ? "Cycle armed; waiting for projected rail progress."
        : "No projector at cycle start.";
}

void CompleteCycleTelemetry()
{
    _cycleEndRemaining = _projector != null ? _projector.RemainingBlocks : -1;

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
    if (_projector == null || !IsOperationalProjector(_projector))
    {
        _projectorLifecycle = ProjectorLifecycleState.Unavailable;
        _previousRemaining = -1;
        _currentRemaining = -1;
        _progressDelta = 0;
        _progressSeconds = 0.0;
        return;
    }

    int remaining = _projector.RemainingBlocks;

    if (_currentRemaining < 0)
    {
        _previousRemaining = remaining;
        _currentRemaining = remaining;
        _progressDelta = 0;
        _progressSeconds = 0.0;
        _lastProgressAt = DateTime.Now;
        _projectorLifecycle = remaining <= 0
            ? ProjectorLifecycleState.Complete
            : ProjectorLifecycleState.Ready;
        return;
    }

    _previousRemaining = _currentRemaining;
    _currentRemaining = remaining;
    _progressDelta = _previousRemaining - _currentRemaining;

    if (remaining <= 0)
    {
        _projectorLifecycle = ProjectorLifecycleState.Complete;
        _progressSeconds = 0.0;
        return;
    }

    if (_progressDelta > 0)
    {
        if (_progressSeconds > _maxProgressGap)
            _maxProgressGap = _progressSeconds;

        _projectorLifecycle = ProjectorLifecycleState.Progressing;
        _progressSeconds = 0.0;
        _lastProgressAt = DateTime.Now;
        Log("Projector progress: " + _previousRemaining + " -> " +
            _currentRemaining + " remaining.");
        return;
    }

    if (_progressDelta < 0)
    {
        _projectorLifecycle = ProjectorLifecycleState.Ready;
        _progressSeconds = 0.0;
        _lastProgressAt = DateTime.Now;
        Log("Projector remaining count increased: " + _previousRemaining +
            " -> " + _currentRemaining + ".");
        return;
    }

    if (!IsConstructionExpected())
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

void WriteFullReportToCustomData()
{
    var sb = new StringBuilder(8192);

    sb.AppendLine("ENDLESS DRILL BASIC DIAGNOSTIC REPORT");
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
    sb.AppendLine("SupervisorDecision=" + _supervisorDecision.ToString().ToUpperInvariant());
    sb.AppendLine("SupervisorRequestedOperation=" + _supervisorRequestedOperation);
    sb.AppendLine("SupervisorReason=" + _supervisorReason);
    sb.AppendLine("ConstructionAssurance=" + _constructionAssurance.ToString().ToUpperInvariant());
    sb.AppendLine("ConstructionAssuranceReason=" + _constructionAssuranceReason);
    sb.AppendLine("ConstructionProgressThisCycle=" + _constructionProgressThisCycle);
    sb.AppendLine("ConstructionRequiredBlocks=" + MIN_REQUIRED_BLOCKS_PER_CYCLE);
    sb.AppendLine("ConstructionProgressTimeoutSeconds=" + CONSTRUCTION_PROGRESS_TIMEOUT_SECONDS.ToString("0.0"));
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

    sb.AppendLine("[Projector]");
    if (_projector == null)
    {
        sb.AppendLine("Found=false");
    }
    else
    {
        sb.AppendLine("Found=true");
        sb.AppendLine("Name=" + _projector.CustomName);
        sb.AppendLine("EntityId=" + _projector.EntityId);
        sb.AppendLine("Enabled=" + _projector.Enabled);
        sb.AppendLine("Functional=" + _projector.IsFunctional);
        sb.AppendLine("Working=" + _projector.IsWorking);
        sb.AppendLine("Projecting=" + _projector.IsProjecting);
        sb.AppendLine("RemainingBlocks=" + _projector.RemainingBlocks);
        sb.AppendLine("Lifecycle=" + ProjectorLifecycleLabel());
        sb.AppendLine("PreviousRemaining=" + _previousRemaining);
        sb.AppendLine("CurrentRemaining=" + _currentRemaining);
        sb.AppendLine("ProgressDelta=" + _progressDelta);
        sb.AppendLine("SecondsSinceProgress=" + _progressSeconds.ToString("0.0"));
        sb.AppendLine("LastProgressAt=" +
            (_lastProgressAt == DateTime.MinValue ? "" : _lastProgressAt.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine("MaximumProgressGap=" + _maxProgressGap.ToString("0.0"));
        sb.AppendLine("CycleStartRemaining=" + _cycleStartRemaining);
        sb.AppendLine("CycleEndRemaining=" + _cycleEndRemaining);
        sb.AppendLine("CycleBlocksCompleted=" + _cycleBlocksCompleted);
    }
    sb.AppendLine();

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

    _status.AppendLine("ENDLESS DRILL MK I BASIC  " + VERSION);
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

    if (_projector != null)
    {
        _status.Append("Projector: remaining=")
            .Append(_projector.RemainingBlocks)
            .Append(" lifecycle=")
            .AppendLine(ProjectorLifecycleLabel());
    }

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
