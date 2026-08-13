using System.Collections.Concurrent;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;

namespace CodexAgentSwitch.Application.Tasks;

public enum ContinuationCorrelationStage
{
    WorkerResultInjected,
    MainContinuationStarted,
    ExecRequestObserved,
    ExecOutputObserved,
    MainResumeObserved,
}

public sealed record ContinuationCorrelationEvent(
    string CorrelationId,
    string TaskId,
    string LocalTurnId,
    string? MainThreadId,
    string? MainTurnId,
    string? ObservableItemId,
    ContinuationCorrelationStage Stage,
    DateTimeOffset ObservedAt);

public interface IContinuationCorrelationSink
{
    void Record(ContinuationCorrelationEvent activity);
}

public sealed class ContinuationCorrelationTracker(
    IContinuationCorrelationSink sink,
    IClock clock)
{
    private readonly ConcurrentDictionary<string, CorrelationState> states = new(StringComparer.Ordinal);

    public void WorkerResultInjected(string taskId, string localTurnId)
    {
        var state = states.GetOrAdd(Key(taskId, localTurnId), _ => new CorrelationState(taskId, localTurnId));
        Write(state, ContinuationCorrelationStage.WorkerResultInjected);
    }

    public void MainContinuationStarted(
        string taskId,
        string localTurnId,
        string mainThreadId,
        string mainTurnId)
    {
        if (!states.TryGetValue(Key(taskId, localTurnId), out var state)) return;
        state.MainThreadId = mainThreadId;
        state.MainTurnId = mainTurnId;
        Write(state, ContinuationCorrelationStage.MainContinuationStarted);
    }

    public void Observe(string taskId, string localTurnId, MainAgentEvent activity)
    {
        var key = Key(taskId, localTurnId);
        if (!states.TryGetValue(key, out var state)) return;

        if (state.AwaitingResume && !IsCommandCompletion(activity))
        {
            Write(state, ContinuationCorrelationStage.MainResumeObserved, ItemId(activity.RawEvent));
            states.TryRemove(key, out _);
            return;
        }

        if (!IsCommandExecution(activity.RawEvent)) return;
        var itemId = ItemId(activity.RawEvent);
        if (activity.Kind == MainAgentEventKind.TraceItemStarted)
        {
            Write(state, ContinuationCorrelationStage.ExecRequestObserved, itemId);
        }
        else if (activity.Kind == MainAgentEventKind.TraceItem)
        {
            Write(state, ContinuationCorrelationStage.ExecOutputObserved, itemId);
            state.AwaitingResume = true;
        }
    }

    private void Write(
        CorrelationState state,
        ContinuationCorrelationStage stage,
        string? observableItemId = null)
    {
        try
        {
            sink.Record(new ContinuationCorrelationEvent(
                $"{state.TaskId}:{state.LocalTurnId}",
                state.TaskId,
                state.LocalTurnId,
                state.MainThreadId,
                state.MainTurnId,
                observableItemId,
                stage,
                clock.UtcNow.ToUniversalTime()));
        }
        catch
        {
            // Diagnostics must never alter the Main execution path.
        }
    }

    private static bool IsCommandCompletion(MainAgentEvent activity) =>
        activity.Kind == MainAgentEventKind.TraceItem && IsCommandExecution(activity.RawEvent);

    private static bool IsCommandExecution(JsonElement? rawEvent) =>
        Item(rawEvent) is { } item
        && item.TryGetProperty("type", out var type)
        && string.Equals(type.GetString(), "commandExecution", StringComparison.Ordinal);

    private static string? ItemId(JsonElement? rawEvent)
    {
        if (Item(rawEvent) is not { } item) return null;
        foreach (var property in new[] { "id", "callId", "toolCallId" })
        {
            if (item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static JsonElement? Item(JsonElement? rawEvent) =>
        rawEvent is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("item", out var item)
        && item.ValueKind == JsonValueKind.Object
            ? item
            : null;

    private static string Key(string taskId, string localTurnId) => $"{taskId}\n{localTurnId}";

    private sealed class CorrelationState(string taskId, string localTurnId)
    {
        public string TaskId { get; } = taskId;
        public string LocalTurnId { get; } = localTurnId;
        public string? MainThreadId { get; set; }
        public string? MainTurnId { get; set; }
        public bool AwaitingResume { get; set; }
    }
}
