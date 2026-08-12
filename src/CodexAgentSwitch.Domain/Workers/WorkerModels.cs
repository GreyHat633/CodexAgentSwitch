using System.Text.Json;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Domain.Workers;

public sealed record WorkerModelCapability(
    string Id,
    string DisplayName,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string DefaultReasoningEffort,
    bool IsDefault);

public enum WorkerToolCapability
{
    Text,
    ProjectRead,
    Search,
    Patch,
    Shell,
    BuildAndTest,
    MultiTurn,
    SelfRepair,
}

public sealed record WorkerCapabilities(
    string AdapterId,
    bool IsAvailable,
    IReadOnlyList<WorkerModelCapability> Models,
    int MaxConcurrency,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlySet<WorkerToolCapability> ToolCapabilities { get; init; } = new HashSet<WorkerToolCapability>();
}

public enum ScopeOperation
{
    Read,
    Search,
    Modify,
    Execute,
    Test,
}

public sealed record WorkerScope(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Modules,
    IReadOnlyList<ScopeOperation> Operations);

public sealed record WorkerTask(
    string TaskGroupId,
    string TaskId,
    string Objective,
    string Prompt,
    string WorkingDirectory,
    string ModelId,
    string ReasoningEffort,
    WorkerScope Scope,
    IReadOnlyList<string> Deliverables,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> StopConditions,
    JsonElement? OutputSchema = null,
    ExecutionApprovalMode ApprovalMode = ExecutionApprovalMode.Automatic)
{
    public BudgetLimits? BudgetSnapshot { get; init; }

    public IReadOnlyList<string> AllowedReadScope { get; init; } = [];

    public IReadOnlyList<string> AllowedWriteScope { get; init; } = [];

    public ExternalWorkerPermissionMode ExternalWorkerPermission { get; init; } = ExternalWorkerPermissionMode.WorkspaceFullAccess;
}

public enum WorkerJobStatus
{
    Starting,
    Running,
    WaitingForApproval,
    Completed,
    Failed,
    Interrupted,
    UnknownRecoverable,
    Deleted,
}

public sealed record WorkerJob(
    string AdapterId,
    string JobId,
    string ThreadId,
    string TurnId,
    string TaskId,
    WorkerJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? StatusMessage);

public sealed record WorkerResult(
    string TaskId,
    WorkerJobStatus Status,
    string? Summary,
    JsonElement? RawResult,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Unresolved,
    string? ProviderId = null,
    string? ProviderName = null,
    Uri? RequestUri = null,
    string? ResponseModelId = null,
    ProviderUsage? Usage = null,
    string? FailureKind = null)
{
    public BudgetLimits? BudgetSnapshot { get; init; }

    public bool? CostVerified { get; init; }

    public int? LeaseExtensionCount { get; init; }

    public string? HardLimitReason { get; init; }

    public bool? FinalizationAttempted { get; init; }

    public bool? FinalizationSucceeded { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public int? ProviderTurns { get; init; }

    public int? ToolCalls { get; init; }

    public int? FailedToolCalls { get; init; }

    public int? DeniedToolCalls { get; init; }

    public TimeSpan? Duration { get; init; }
}

public enum WorkerSteerKind
{
    AddInstruction,
    ContinueWaiting,
    Approve,
    Decline,
}

public sealed record WorkerSteerRequest(WorkerSteerKind Kind, string? Message, string? RequestId = null);
