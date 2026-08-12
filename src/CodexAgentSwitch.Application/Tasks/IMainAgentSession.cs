using System.Text.Json;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

public enum MainAgentEventKind
{
    TurnStarted,
    OutputDelta,
    StatusChanged,
    TraceItem,
    ApprovalRequested,
    TurnCompleted,
    CompactionStarted,
    CompactionCompleted,
}

public sealed record MainAgentEvent(
    MainAgentEventKind Kind,
    string ThreadId,
    string TurnId,
    string? Text,
    string? Status,
    JsonElement? RawEvent,
    TaskMessageKind? MessageKind = null);

public sealed record MainAgentTurnHandle(string ThreadId, string TurnId);

public sealed record MainAgentCompactionHandle(string ThreadId, bool RequestAccepted, JsonElement RawResponse);

public sealed record MainThreadBindingResult(
    string ThreadId,
    string SessionId,
    string Source,
    string WorkingDirectory,
    string Status,
    bool Resumed,
    JsonElement RawThread);

public sealed record MainAgentRolloverResult(string PreviousThreadId, string NewThreadId, CompactCheckpoint Checkpoint, MainAgentTurnHandle? FirstTurn);

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
        ExecutionApprovalMode approvalMode,
        CancellationToken cancellationToken = default);

    Task ResumeThreadAsync(
        string threadId,
        string modelId,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
        CancellationToken cancellationToken = default);

    Task<MainAgentTurnHandle> StartTurnAsync(
        string threadId,
        string prompt,
        string modelId,
        string reasoningEffort,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
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

    /// <summary>
    /// Validates and, when necessary, resumes an already persisted thread.
    /// Implementations must never create a replacement thread.
    /// </summary>
    Task<MainThreadBindingResult> BindExistingThreadAsync(
        string threadId,
        string expectedSessionId,
        string expectedSource,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Existing-thread binding is not available for this session.");

    /// <summary>Starts native compaction. Completion is reported through EventReceived.</summary>
    Task<MainAgentCompactionHandle> CompactThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Native thread compaction is not available for this session.");

    /// <summary>Creates a fresh thread and optionally starts it with a bounded checkpoint replay.</summary>
    /// Worker terminal resume remains scheduler-event-driven; this API adds no polling loop.
    Task<MainAgentRolloverResult> RolloverThreadAsync(
        string previousThreadId,
        CompactCheckpoint checkpoint,
        string modelId,
        string reasoningEffort,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
        bool startFirstTurn = true,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Fresh-thread rollover is not available for this session.");
}
