using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Domain.Tasks;

public enum ControlledTaskStatus
{
    Queued,
    WorkerRunning,
    MainAgentRunning,
    WaitingForApproval,
    Completed,
    Failed,
    Interrupted,
    UnknownRecoverable,
}

public enum TaskMessageActor
{
    User,
    Worker,
    MainAgent,
    System,
}

public enum TaskMessageKind
{
    Text,
    ToolCall,
    FileChange,
    Diff,
    WorkerProgress,
    Usage,
}

public enum DelegationDecisionKind
{
    Skip,
    InvokeWorker,
}

public sealed record TaskProviderSnapshot(
    string Id,
    string Name,
    ProviderKind Kind,
    Uri? BaseUri,
    string? CredentialReference,
    string? ModelId,
    TimeSpan Timeout,
    bool IsEnabled,
    ProviderPricing? Pricing);

public sealed record TaskProfileSnapshot(
    Guid ProfileId,
    string ProfileName,
    AgentSelection MainAgent,
    WorkerPolicy WorkerPolicy,
    BudgetLimits Budget,
    TaskProviderSnapshot? Provider,
    DateTimeOffset CapturedAt,
    ExecutionApprovalMode ApprovalMode = ExecutionApprovalMode.Automatic,
    ExternalWorkerPermissionMode ExternalWorkerPermission = ExternalWorkerPermissionMode.WorkspaceFullAccess);

public sealed record DelegationDecision(
    DelegationDecisionKind Kind,
    string Reason,
    bool Forced,
    string? ProviderId,
    string? ModelId,
    DateTimeOffset DecidedAt,
    string? DelegatedScope = null,
    string? Deliverable = null,
    IReadOnlyList<string>? AcceptanceCriteria = null);

public sealed record ControlledTaskMessage(
    Guid Id,
    string TurnId,
    TaskMessageActor Actor,
    string Content,
    DateTimeOffset CreatedAt,
    bool IsFinal,
    string? WorkerJobId = null,
    TaskMessageKind Kind = TaskMessageKind.Text,
    bool IsCollapsible = false,
    string? Metadata = null);

public sealed record ControlledWorkerRun(
    string JobId,
    string ThreadId,
    string TurnId,
    string AdapterId,
    string ModelId,
    string ReasoningEffort,
    WorkerJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ResultSummary,
    string? StatusMessage,
    string? ProviderId = null,
    string? ProviderName = null,
    string? RequestUri = null,
    string? ResponseModelId = null,
    ProviderUsage? Usage = null,
    string? FailureKind = null);

public sealed record ControlledTaskTurn(
    string Id,
    string? ServerTurnId,
    string UserInput,
    ControlledTaskStatus Status,
    IReadOnlyList<ControlledWorkerRun> Workers,
    IReadOnlyList<ControlledTaskMessage> Messages,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    TaskProfileSnapshot? ProfileSnapshot = null,
    DelegationDecision? Delegation = null);

public sealed record ControlledTaskSession(
    string Id,
    Guid ProfileId,
    string ProfileName,
    string Title,
    string WorkingDirectory,
    string MainModelId,
    string MainReasoningEffort,
    string? MainThreadId,
    ControlledTaskStatus Status,
    IReadOnlyList<ControlledTaskTurn> Turns,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    string? ProjectId = null,
    bool IsArchived = false,
    TaskProfileSnapshot? InitialProfileSnapshot = null);
