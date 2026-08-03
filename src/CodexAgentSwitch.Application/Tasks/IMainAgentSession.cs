using System.Text.Json;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

public enum MainAgentEventKind
{
    TurnStarted,
    OutputDelta,
    StatusChanged,
    ApprovalRequested,
    TurnCompleted,
}

public sealed record MainAgentEvent(
    MainAgentEventKind Kind,
    string ThreadId,
    string TurnId,
    string? Text,
    string? Status,
    JsonElement? RawEvent);

public sealed record MainAgentTurnHandle(string ThreadId, string TurnId);

public sealed record MainAgentTurnResult(
    string ThreadId,
    string TurnId,
    ControlledTaskStatus Status,
    string? FinalText,
    string? ErrorMessage,
    JsonElement RawTurn);

public interface IMainAgentSession
{
    event Func<MainAgentEvent, Task>? EventReceived;

    Task<string> CreateThreadAsync(
        string modelId,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task ResumeThreadAsync(
        string threadId,
        string modelId,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<MainAgentTurnHandle> StartTurnAsync(
        string threadId,
        string prompt,
        string modelId,
        string reasoningEffort,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<MainAgentTurnResult> WaitForTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<MainAgentTurnResult> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task RespondToApprovalAsync(
        string threadId,
        string turnId,
        bool approve,
        CancellationToken cancellationToken = default);
}
