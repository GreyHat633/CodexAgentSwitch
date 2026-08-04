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

public sealed record WorkerCapabilities(
    string AdapterId,
    bool IsAvailable,
    IReadOnlyList<WorkerModelCapability> Models,
    int MaxConcurrency,
    IReadOnlyList<string> Warnings);

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
    ExecutionApprovalMode ApprovalMode = ExecutionApprovalMode.Automatic);

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
    string? FailureKind = null);

public enum WorkerSteerKind
{
    AddInstruction,
    ContinueWaiting,
    Approve,
    Decline,
}

public sealed record WorkerSteerRequest(WorkerSteerKind Kind, string? Message, string? RequestId = null);
