using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ContinuationCorrelationTests
{
    [Fact]
    public void Worker_continuation_exec_and_resume_share_one_silent_correlation()
    {
        var sink = new RecordingSink();
        var tracker = new ContinuationCorrelationTracker(sink, new FixedClock());
        tracker.WorkerResultInjected("task-1", "local-1");
        tracker.MainContinuationStarted("task-1", "local-1", "thread-1", "turn-1");
        tracker.Observe("task-1", "local-1", Event(MainAgentEventKind.TraceItemStarted, "commandExecution", "exec-1"));
        tracker.Observe("task-1", "local-1", Event(MainAgentEventKind.TraceItem, "commandExecution", "exec-1"));
        tracker.Observe("task-1", "local-1", new MainAgentEvent(
            MainAgentEventKind.OutputDelta, "thread-1", "turn-1", "continued", null, null));

        Assert.Equal(
            [
                ContinuationCorrelationStage.WorkerResultInjected,
                ContinuationCorrelationStage.MainContinuationStarted,
                ContinuationCorrelationStage.ExecRequestObserved,
                ContinuationCorrelationStage.ExecOutputObserved,
                ContinuationCorrelationStage.MainResumeObserved,
            ],
            sink.Events.Select(item => item.Stage));
        Assert.All(sink.Events, item => Assert.Equal("task-1:local-1", item.CorrelationId));
        Assert.Equal("exec-1", sink.Events.Single(item => item.Stage == ContinuationCorrelationStage.ExecRequestObserved).ObservableItemId);
    }

    [Fact]
    public void Non_exec_tools_and_turns_without_worker_results_are_not_logged()
    {
        var sink = new RecordingSink();
        var tracker = new ContinuationCorrelationTracker(sink, new FixedClock());
        tracker.MainContinuationStarted("task-1", "local-1", "thread-1", "turn-1");
        tracker.Observe("task-1", "local-1", Event(MainAgentEventKind.TraceItemStarted, "mcpToolCall", "tool-1"));
        Assert.Empty(sink.Events);
    }

    private static MainAgentEvent Event(MainAgentEventKind kind, string type, string id) => new(
        kind,
        "thread-1",
        "turn-1",
        null,
        kind == MainAgentEventKind.TraceItemStarted ? "running" : "completed",
        JsonSerializer.SerializeToElement(new { item = new { id, type } }),
        TaskMessageKind.ToolCall);

    private sealed class RecordingSink : IContinuationCorrelationSink
    {
        public List<ContinuationCorrelationEvent> Events { get; } = [];
        public void Record(ContinuationCorrelationEvent activity) => Events.Add(activity);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
    }
}
