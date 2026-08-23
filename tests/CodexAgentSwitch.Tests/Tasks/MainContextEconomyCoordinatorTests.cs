using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class MainContextEconomyCoordinatorTests
{
    [Fact]
    public async Task Compaction_requires_started_and_completed_events_on_the_bound_thread()
    {
        var session = new FakeSession { EmitLifecycle = true };
        var coordinator = new MainContextEconomyCoordinator(session, new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromSeconds(1) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);
        var snapshot = await coordinator.GetSnapshotAsync("thread-a");
        Assert.Equal(ContextEconomyState.Verifying, snapshot!.State);
        Assert.Equal(1, session.CompactionCalls);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.LastCompactionRequestId));
        Assert.NotNull(snapshot.LastCompactionRequestedAt);
        Assert.NotNull(snapshot.LastCompactionStartedAt);
        Assert.NotNull(snapshot.LastCompactionCompletedAt);
    }

    [Fact]
    public async Task Failed_ack_is_bounded_to_two_attempts_and_blocks_protection()
    {
        var session = new FakeSession { Acknowledge = false };
        var coordinator = new MainContextEconomyCoordinator(session, new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromMilliseconds(20) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        var result = await coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);
        Assert.Equal(ContextEconomyState.ContextProtectionBlocked, result.State);
        Assert.Equal(2, session.CompactionCalls);
    }

    [Fact]
    public async Task Acknowledgement_without_lifecycle_never_reports_success()
    {
        var session = new FakeSession();
        var coordinator = new MainContextEconomyCoordinator(session, new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromMilliseconds(20) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        var result = await coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);
        Assert.NotNull(result.Compaction);
        Assert.False(result.Compaction!.Succeeded);
        Assert.False(result.Compaction.TerminalLifecycleObserved);
    }

    [Fact]
    public async Task Unrelated_thread_lifecycle_is_ignored()
    {
        var session = new FakeSession();
        var coordinator = new MainContextEconomyCoordinator(session, new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromMilliseconds(20) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        session.EmitLifecycleForThread = "thread-other";
        var result = await coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);
        Assert.NotNull(result.Compaction);
        Assert.False(result.Compaction!.Succeeded);
    }

    [Fact]
    public async Task Concurrent_safe_boundary_calls_issue_one_request()
    {
        var session = new FakeSession { EmitLifecycle = true, LifecycleDelay = TimeSpan.FromMilliseconds(15) };
        var coordinator = new MainContextEconomyCoordinator(session, new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromSeconds(1) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(60, 100));
        await Task.WhenAll(coordinator.CompactAtSafeBoundaryAsync("thread-a"), coordinator.CompactAtSafeBoundaryAsync("thread-a"));
        Assert.Equal(1, session.CompactionCalls);
    }

    [Fact]
    public async Task Restarting_compacting_state_blocks_automatic_retry_until_terminal_evidence_exists()
    {
        var store = new MemoryStore(new ContextEconomySnapshot(
            "thread-a",
            ContextEconomyState.Compacting,
            1,
            0,
            [],
            [],
            "in-flight",
            LastCompactionRequestId: "request-before-crash"));
        var session = new FakeSession();
        var coordinator = new MainContextEconomyCoordinator(session, stateStore: store);
        await coordinator.BindThreadAsync("thread-a", session);
        var snapshot = await coordinator.GetSnapshotAsync("thread-a");
        Assert.Equal(ContextEconomyState.ContextProtectionBlocked, snapshot!.State);
        Assert.Equal("request-before-crash", snapshot.LastCompactionRequestId);

        await coordinator.ObserveTurnAsync("thread-a", Sample(90, 100), safeBoundary: true);
        Assert.Equal(0, session.CompactionCalls);
    }

    [Fact]
    public async Task Host_automatic_compaction_cancels_pending_request_and_starts_verification_epoch()
    {
        var session = new FakeSession();
        var coordinator = new MainContextEconomyCoordinator(session);
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(70, 100), safeBoundary: false);

        var compactedAt = DateTimeOffset.Parse("2026-08-12T05:49:07.087Z");
        var observed = await coordinator.ObserveStructuredCompactionAsync(
            "thread-a", CompactionTrigger.HostAutomatic, compactedAt);
        var duplicate = await coordinator.ObserveStructuredCompactionAsync(
            "thread-a", CompactionTrigger.HostAutomatic, compactedAt);
        var activeRequest = await coordinator.CompactAtSafeBoundaryAsync("thread-a");
        var snapshot = await coordinator.GetSnapshotAsync("thread-a");

        Assert.Equal(ContextEconomyState.Verifying, observed.State);
        Assert.Equal(CompactionTrigger.HostAutomatic, snapshot!.LastCompactionTrigger);
        Assert.Equal(compactedAt, snapshot.StructuredCompactedAt);
        Assert.True(duplicate.DuplicateSuppressed);
        Assert.Null(activeRequest);
        Assert.Equal(0, session.CompactionCalls);
    }

    [Fact]
    public async Task Structured_compacted_completes_inflight_agent_switch_transaction_once()
    {
        var session = new FakeSession { HoldAcknowledgement = true };
        var coordinator = new MainContextEconomyCoordinator(session, new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromSeconds(2) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        var pending = coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);
        await session.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var compacted = coordinator.ObserveStructuredCompactionAsync(
            "thread-a", CompactionTrigger.HostAutomatic, DateTimeOffset.UtcNow);
        session.ReleaseAcknowledgement.TrySetResult();

        var result = await pending;
        var observation = await compacted;
        var snapshot = await coordinator.GetSnapshotAsync("thread-a");

        Assert.True(result.Compaction!.Succeeded);
        Assert.Equal(CompactionTrigger.AgentSwitch, observation.Trigger);
        Assert.Equal(CompactionTrigger.AgentSwitch, snapshot!.LastCompactionTrigger);
        Assert.Equal(1, session.CompactionCalls);
    }

    [Fact]
    public async Task Control_guard_is_revalidated_immediately_before_compaction_rpc()
    {
        var session = new FakeSession { EmitLifecycle = true };
        var coordinator = new MainContextEconomyCoordinator(
            session,
            new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromSeconds(1) });
        var guardCalls = 0;
        await coordinator.BindThreadAsync(
            "thread-a",
            session,
            _ =>
            {
                guardCalls++;
                return Task.FromResult(ContextControlValidation.Reject("lease changed"));
            });
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));

        var result = await coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);

        Assert.Equal(1, guardCalls);
        Assert.Equal(0, session.CompactionCalls);
        Assert.Equal(ContextEconomyState.ContextProtectionBlocked, result.State);
        Assert.False(result.Compaction!.Succeeded);
        Assert.Contains("lease changed", result.Compaction.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_compaction_item_blocks_a_parallel_request_and_its_completion_starts_verification()
    {
        var session = new FakeSession { EmitLifecycle = true };
        var coordinator = new MainContextEconomyCoordinator(
            session,
            new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromSeconds(1) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(70, 100));

        await session.EmitAsync(new(MainAgentEventKind.CompactionStarted, "thread-a", "compact-host", null, "inProgress", null));
        var delayed = await coordinator.ObserveTurnAsync("thread-a", Sample(80, 100), safeBoundary: true);
        await session.EmitAsync(new(MainAgentEventKind.CompactionCompleted, "thread-a", "compact-host", null, "completed", null));
        var snapshot = await coordinator.GetSnapshotAsync("thread-a");

        Assert.False(delayed.CompactionRequested);
        Assert.Equal(0, session.CompactionCalls);
        Assert.Equal(ContextEconomyState.Verifying, snapshot!.State);
        Assert.Equal(CompactionTrigger.HostAutomatic, snapshot.LastCompactionTrigger);
    }

    [Fact]
    public async Task Realtime_updates_for_one_turn_replace_the_sample_before_safe_boundary()
    {
        var session = new FakeSession { EmitLifecycle = true };
        var coordinator = new MainContextEconomyCoordinator(
            session,
            new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromSeconds(1) });
        await coordinator.BindThreadAsync("thread-a", session);
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));
        await coordinator.ObserveTurnAsync("thread-a", Sample(20, 100));

        await coordinator.ObserveTurnAsync(
            "thread-a",
            Sample(60, 100) with { TurnId = "turn-live" },
            safeBoundary: false);
        var result = await coordinator.ObserveTurnAsync(
            "thread-a",
            Sample(80, 100) with { TurnId = "turn-live" },
            safeBoundary: true);
        var snapshot = await coordinator.GetSnapshotAsync("thread-a");

        Assert.True(result.CompactionRequested);
        Assert.Equal(3, snapshot!.Samples.Count);
        Assert.Equal(80, snapshot.Samples[^1].InputTokens);
        Assert.Equal("turn-live", snapshot.Samples[^1].TurnId);
        Assert.Equal(1, session.CompactionCalls);
    }

    private static ContextTurnSample Sample(long input, long window) => new(input, input / 2, input, window);

    private sealed class FakeSession : IMainAgentSession
    {
        public event Func<MainAgentEvent, Task>? EventReceived;
        public bool EmitLifecycle { get; init; }
        public bool Acknowledge { get; init; } = true;
        public string? EmitLifecycleForThread { get; set; }
        public TimeSpan LifecycleDelay { get; init; }
        public bool HoldAcknowledgement { get; init; }
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompactionCalls { get; private set; }
        public Task<string> CreateThreadAsync(string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.FromResult("thread-a");
        public Task ResumeThreadAsync(string threadId, string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MainAgentTurnHandle> StartTurnAsync(string threadId, string prompt, string modelId, string reasoningEffort, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.FromResult(new MainAgentTurnHandle(threadId, "turn"));
        public Task<MainAgentTurnResult> WaitForTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.FromResult(Result(threadId, turnId));
        public Task<MainAgentTurnResult> ReadTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.FromResult(Result(threadId, turnId));
        public Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToApprovalAsync(string threadId, string turnId, bool approve, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<MainAgentCompactionHandle> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default)
        {
            CompactionCalls++;
            RequestEntered.TrySetResult();
            if (HoldAcknowledgement)
                await ReleaseAcknowledgement.Task.WaitAsync(cancellationToken);
            if (EmitLifecycle || EmitLifecycleForThread is not null)
            {
                var eventThread = EmitLifecycleForThread ?? threadId;
                await (EventReceived?.Invoke(new(MainAgentEventKind.CompactionStarted, eventThread, "", null, null, null)) ?? Task.CompletedTask);
                await Task.Delay(LifecycleDelay);
                await (EventReceived?.Invoke(new(MainAgentEventKind.CompactionCompleted, eventThread, "", null, null, null)) ?? Task.CompletedTask);
            }
            return new(threadId, Acknowledge, JsonSerializer.SerializeToElement(new { acknowledged = Acknowledge }));
        }
        public Task EmitAsync(MainAgentEvent value) => EventReceived?.Invoke(value) ?? Task.CompletedTask;
        private static MainAgentTurnResult Result(string thread, string turn) => new(thread, turn, ControlledTaskStatus.Completed, null, null, default);
    }

    private sealed class MemoryStore(ContextEconomySnapshot snapshot) : IMainContextEconomyStateStore
    {
        public Task<ContextEconomySnapshot?> LoadAsync(string threadId, CancellationToken cancellationToken = default) => Task.FromResult<ContextEconomySnapshot?>(snapshot);
        public Task SaveAsync(ContextEconomySnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
