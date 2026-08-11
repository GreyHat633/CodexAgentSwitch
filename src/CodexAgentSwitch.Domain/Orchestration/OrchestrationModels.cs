using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Domain.Orchestration;

public enum PreferredWorkerType
{
    ReadHeavy,
    ScopedImplementation,
    Test,
    ExternalLowCost,
    UserSelected,
}

public enum TaskRiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Stable points at which the current work may be repartitioned.  Keep this
/// list deliberately closed: instruction injection and transport map these
/// values by name, so adding an implicit/default trigger would be ambiguous.
/// </summary>
public enum RepartitionTrigger
{
    INITIAL_LOCALIZATION_COMPLETE,
    ARCHITECTURE_RESOLVED,
    WORKER_RESULT_RECEIVED,
    WORKER_REVIEW_COMPLETE,
    PHASE_CHANGE,
    BUILD_TEST_BOUNDED_FIXES,
    MODULE_COMPLETE,
    WORK_CONVERGED,
}

public enum WorkOwner
{
    Main,
    Worker,
}

public enum RepartitionReasonCode
{
    ARCHITECTURE_UNRESOLVED,
    CROSS_MODULE_DECISION,
    INVESTIGATION_UNRESOLVED,
    WORKER_CAPABILITY_MISSING,
    TOO_SMALL_TO_DELEGATE,
    REVIEW_REQUIRED,
    FINAL_INTEGRATION,
    BOUNDED_IMPLEMENTATION,
    BOUNDED_FIX,
    BOUNDED_UI,
    BOUNDED_TESTING,
    REPETITIVE_WORK,
}

/// <summary>Short, stable reasons suitable for model-visible status output.</summary>
public static class RepartitionReasons
{
    // These values are intentionally short and form the allow-list injected
    // into the MAIN/WORKER instruction contract.
    public static IReadOnlySet<RepartitionReasonCode> AllowedMain { get; } =
        new HashSet<RepartitionReasonCode>
        {
            RepartitionReasonCode.ARCHITECTURE_UNRESOLVED,
            RepartitionReasonCode.CROSS_MODULE_DECISION,
            RepartitionReasonCode.INVESTIGATION_UNRESOLVED,
            RepartitionReasonCode.WORKER_CAPABILITY_MISSING,
            RepartitionReasonCode.TOO_SMALL_TO_DELEGATE,
            RepartitionReasonCode.REVIEW_REQUIRED,
            RepartitionReasonCode.FINAL_INTEGRATION,
        };

    public static IReadOnlySet<RepartitionReasonCode> AllowedWorker { get; } =
        new HashSet<RepartitionReasonCode>
        {
            RepartitionReasonCode.BOUNDED_IMPLEMENTATION,
            RepartitionReasonCode.BOUNDED_FIX,
            RepartitionReasonCode.BOUNDED_UI,
            RepartitionReasonCode.BOUNDED_TESTING,
            RepartitionReasonCode.REPETITIVE_WORK,
        };

    public static bool IsAllowed(WorkOwner owner, RepartitionReasonCode reason) =>
        owner == WorkOwner.Main ? AllowedMain.Contains(reason) : AllowedWorker.Contains(reason);
}

/// <summary>A compact snapshot of the work currently under consideration.</summary>
public sealed record CurrentWorkState(
    string CurrentWork,
    IReadOnlyList<string> KnownRemainingWork,
    WorkOwner CurrentOwner,
    RepartitionTrigger LastTrigger,
    RepartitionReasonCode OwnershipReason,
    string WorkerState)
{
    public bool ReasonMatchesOwner => RepartitionReasons.IsAllowed(CurrentOwner, OwnershipReason);

    public void Validate()
    {
        if (!ReasonMatchesOwner)
        {
            throw new ArgumentException("The ownership reason does not match the current owner.");
        }
    }
}

public sealed record RepartitionRecord(
    long Sequence,
    RepartitionTrigger Trigger,
    WorkOwner Decision,
    RepartitionReasonCode Reason,
    string WorkSummary,
    string? WorkerIdentity,
    string? Result)
{
    public WorkOwner Owner => Decision;
    public bool ReasonMatchesOwner => RepartitionReasons.IsAllowed(Decision, Reason);

    public void Validate()
    {
        if (!ReasonMatchesOwner)
        {
            throw new ArgumentException("The telemetry reason does not match the decision owner.");
        }
    }
}

/// <summary>
/// The seven positive delegation signals are intentionally explicit.  The
/// evaluator uses a majority (four or more) rather than a fragile all-of rule.
/// </summary>
public sealed record RepartitionWorkPackage(
    TaskRiskLevel RiskLevel,
    bool Clear,
    bool Bounded,
    bool Stable,
    bool Capable,
    bool Verifiable,
    bool NonOverlapping,
    bool Worthwhile,
    bool TrivialOverhead = false,
    RepartitionReasonCode PreferredWorkerReason = RepartitionReasonCode.BOUNDED_IMPLEMENTATION)
{
    public void Validate()
    {
        if (!RepartitionReasons.AllowedWorker.Contains(PreferredWorkerReason))
        {
            throw new ArgumentException("Preferred worker reason must be a WORKER reason.");
        }
    }

    public int PositiveConditionCount =>
        (Clear ? 1 : 0) +
        (Bounded ? 1 : 0) +
        (Stable ? 1 : 0) +
        (Capable ? 1 : 0) +
        (Verifiable ? 1 : 0) +
        (NonOverlapping ? 1 : 0) +
        (Worthwhile ? 1 : 0);
}

/// <summary>Result of the deterministic MAIN/WORKER repartition check.</summary>
public sealed record RepartitionDecision
{
    public RepartitionDecision(
        WorkOwner owner,
        TaskRiskLevel riskLevel,
        int positiveConditionCount,
        bool delegationPreferred,
        RepartitionReasonCode reason)
    {
        if (!RepartitionReasons.IsAllowed(owner, reason))
        {
            throw new ArgumentException("The ownership reason does not match the owner.", nameof(reason));
        }

        Owner = owner;
        RiskLevel = riskLevel;
        PositiveConditionCount = positiveConditionCount;
        DelegationPreferred = delegationPreferred;
        Reason = reason;
    }

    public WorkOwner Owner { get; }
    public TaskRiskLevel RiskLevel { get; }
    public int PositiveConditionCount { get; }
    public bool DelegationPreferred { get; }
    public RepartitionReasonCode Reason { get; }
    public bool WorkerOwns => Owner == WorkOwner.Worker;
    public bool SolLeads => Owner == WorkOwner.Main;
    public bool ReasonMatchesOwner => RepartitionReasons.IsAllowed(Owner, Reason);
}

public enum ReviewBudget
{
    Minimal,
    Focused,
    Deep,
}

public sealed record DelegationRequest(
    string TaskGroupId,
    string TaskId,
    string Objective,
    string SolWillSkip,
    WorkerScope Scope,
    IReadOnlyList<string> Deliverables,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> StopConditions,
    PreferredWorkerType PreferredWorkerType,
    int RequestedWorkers = 1)
{
    public TaskRiskLevel RiskLevel { get; init; } = TaskRiskLevel.Medium;
}

public enum DelegationScopeStatus
{
    Active,
    Completed,
    Cancelled,
}

public sealed record DelegatedScope(
    string JobId,
    string OwnerWorker,
    WorkerScope Scope,
    DateTimeOffset StartedAt,
    DelegationScopeStatus Status);

public enum GateSeverity
{
    Warning,
    Error,
}

public sealed record GateIssue(string Code, string Message, GateSeverity Severity);

public sealed record DelegationGateResult(IReadOnlyList<GateIssue> Issues)
{
    public bool CanDelegate => Issues.All(issue => issue.Severity != GateSeverity.Error);
}

public sealed record DelegationGateContext(
    RoutingMode RoutingMode,
    int ProfileMaxWorkers,
    int ActiveWorkers,
    bool ProviderAvailable,
    bool WithinBudget,
    bool HighDuplicateRisk,
    int MaxActiveWorkers = 1);

public sealed record EconomicPolicyDecision(
    TaskRiskLevel RiskLevel,
    bool WorkerOwnsClosedLoop,
    bool SolLeads,
    ReviewBudget ReviewBudget,
    ReviewLevel ReviewLevel,
    int MaxActiveWorkers,
    bool CompactResultRequired,
    bool DuplicateImplementationAllowed,
    string Reason);

public enum WorkerEscalationKind
{
    ScopeExpansionRequired,
    DesignAssumptionInvalid,
    SharedProtocolChangeRequired,
    RepeatedValidationFailure,
}

public sealed record WorkerEscalation(
    string TaskId,
    WorkerEscalationKind Kind,
    string Reason,
    IReadOnlyList<string> Evidence,
    DateTimeOffset RaisedAt);

public sealed record SolContextCheckpoint(
    string Head,
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> ArchitectureDecisions,
    IReadOnlyList<string> KnownRisks,
    string NextStep,
    DateTimeOffset CreatedAt);

public enum ScopeAccessIntent
{
    DirectedSpotCheck,
    Read,
    Search,
    Execute,
    Modify,
    Test,
    FullTakeover,
}

public enum ScopeAccessDecisionKind
{
    Allowed,
    WarningRequiresConfirmation,
    Blocked,
}

public sealed record ScopeAccessDecision(
    ScopeAccessDecisionKind Kind,
    string Message,
    IReadOnlyList<string> ConflictingJobIds);

public enum AdoptionStatus
{
    Pending,
    Adopted,
    PartiallyAdopted,
    Rejected,
}

public enum ReviewLevel
{
    R0,
    R1,
    R2,
}

public sealed record AdoptionRecord(
    string JobId,
    AdoptionStatus Status,
    string PlannedSkippedWork,
    string? ActualSkippedWork,
    bool DuplicateWork,
    string? DuplicateReason,
    ReviewLevel ReviewLevel,
    string? RejectionReason,
    DateTimeOffset UpdatedAt);

public enum EconomicCheckpointDecision
{
    NotDue,
    Continue,
    Refine,
    CancelAndTakeOver,
}

public sealed record EconomicCheckpointInput(
    TimeSpan Elapsed,
    decimal? BudgetUsedRatio,
    bool HasDeliverableProgress,
    bool ScopeDriftDetected,
    bool TargetIsWrong);

public sealed record EconomicCheckpointResult(
    EconomicCheckpointDecision Decision,
    string Reason);
