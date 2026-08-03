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
    int RequestedWorkers = 1);

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
    bool HighDuplicateRisk);

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
