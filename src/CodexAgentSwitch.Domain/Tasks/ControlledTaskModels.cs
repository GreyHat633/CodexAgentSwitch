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

public sealed record ControlledTaskMessage(
    Guid Id,
    string TurnId,
    TaskMessageActor Actor,
    string Content,
    DateTimeOffset CreatedAt,
    bool IsFinal,
    string? WorkerJobId = null);

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
    string? StatusMessage);

public sealed record ControlledTaskTurn(
    string Id,
    string? ServerTurnId,
    string UserInput,
    ControlledTaskStatus Status,
    IReadOnlyList<ControlledWorkerRun> Workers,
    IReadOnlyList<ControlledTaskMessage> Messages,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

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
    string? ErrorMessage);

