using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Domain.Usage;

public enum EvidenceKind
{
    Actual,
    Estimated,
    Unavailable,
}

public sealed record MeasuredLong(long? Value, EvidenceKind Evidence);

public sealed record MeasuredDecimal(decimal? Value, EvidenceKind Evidence);

public sealed record UsageSnapshot(
    Guid Id,
    string TaskGroupId,
    string? WorkerJobId,
    string ProviderId,
    string ModelId,
    DateTimeOffset CapturedAt,
    MeasuredLong InputTokens,
    MeasuredLong OutputTokens,
    MeasuredLong TotalTokens,
    MeasuredLong Requests,
    MeasuredDecimal Cost,
    string Currency,
    string? QuotaWindow,
    IReadOnlyList<string> Notes);

public sealed record WorkerLedgerEntry(
    string JobId,
    string ThreadId,
    string AdapterId,
    string ModelId,
    string ReasoningEffort,
    WorkerJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    AdoptionStatus AdoptionStatus,
    string DelegatedWork,
    string PlannedSkippedWork,
    string? ActualSkippedWork,
    bool DuplicateWork,
    string? ResultSummary,
    string? FallbackEvent);

public sealed record TaskGroupLedger(
    string Id,
    string MainThreadId,
    string MainModelId,
    string MainReasoningEffort,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkerLedgerEntry> Workers,
    DateTimeOffset UpdatedAt);

public enum BudgetCheckpoint
{
    Percent25 = 25,
    Percent50 = 50,
    Percent80 = 80,
    Percent100 = 100,
}

public sealed record BudgetConsumption(
    decimal TaskCost,
    decimal DailyCost,
    decimal MonthlyCost,
    long Tokens,
    int Requests,
    bool TaskCostUnknown = false,
    bool DailyCostUnknown = false,
    bool MonthlyCostUnknown = false)
{
    public bool TaskCostKnown => !TaskCostUnknown;
    public bool DailyCostKnown => !DailyCostUnknown;
    public bool MonthlyCostKnown => !MonthlyCostUnknown;
    public bool HasUnknownMonetaryCost => TaskCostUnknown || DailyCostUnknown || MonthlyCostUnknown;
    public bool HasUnknownCost => HasUnknownMonetaryCost;
}

public sealed record BudgetAssessment(
    bool AllowNewRequests,
    decimal HighestRatio,
    IReadOnlyList<BudgetCheckpoint> ReachedCheckpoints,
    IReadOnlyList<string> Reasons);

public enum EconomicConclusion
{
    PossiblySaved,
    CannotDetermine,
    PossiblyIncreased,
}

public sealed record TaskEconomicReport(
    string TaskGroupId,
    string MainAgent,
    IReadOnlyList<string> Workers,
    IReadOnlyList<string> ActualSkippedWork,
    bool DuplicateWork,
    MeasuredDecimal ExternalCost,
    MeasuredLong TotalTokens,
    EconomicConclusion Conclusion,
    string ConclusionReason);
