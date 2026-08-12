using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Tests.Scheduling;

public sealed class MainContextBoundaryIntegrationTests
{
    [Fact]
    public async Task Source_vscode_stop_binds_and_compacts_the_exact_existing_thread()
    {
        const string threadId = "019ff34a-4163-7322-8071-cca28f9458fc";
        const string cwd = "E:\\AISPace\\project";
        var main = new RecordingMainSession();
        var coordinator = new MainContextEconomyCoordinator(main, new ContextEconomyOptions
        {
            CompactionTimeout = TimeSpan.FromSeconds(1),
        });
        await using var scheduler = new WorkerScheduler(
            [],
            new MemoryRepository(),
            new FixedClock(),
            usageSource: new FixedUsageSource(new NativeUsageRecord(
                threadId, cwd, cwd, "gpt-5.6-sol", "high", "Sol", 1,
                80, 40, 40, 1, 0, 81, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "session.jsonl", "cwd", 80, 40, 100, "vscode")),
            contextRuntime: new FixedRuntime(main),
            contextEconomy: coordinator);

        var result = await scheduler.ObserveMainContextBoundaryAsync(
            new(threadId, threadId, cwd, "vscode", "stop"));

        Assert.True(result.BindingAccepted, result.Reason);
        Assert.True(result.TelemetryAvailable);
        Assert.True(result.CompactionRequested);
        Assert.True(result.CompactionSucceeded, result.Reason);
        Assert.Equal(threadId, main.BoundThreadId);
        Assert.Equal(threadId, Assert.Single(main.CompactedThreads));
        Assert.Equal(0, main.CreateThreadCalls);
    }

    [Fact]
    public async Task Missing_or_inconsistent_vscode_binding_fails_closed()
    {
        var main = new RecordingMainSession();
        await using var scheduler = new WorkerScheduler(
            [], new MemoryRepository(), new FixedClock(),
            usageSource: new FixedUsageSource(),
            contextRuntime: new FixedRuntime(main),
            contextEconomy: new MainContextEconomyCoordinator(main));

        var result = await scheduler.ObserveMainContextBoundaryAsync(
            new("session-a", "thread-b", "E:\\AISPace\\project", "vscode", "stop"));

        Assert.False(result.BindingAccepted);
        Assert.Equal(ContextEconomyState.ContextProtectionBlocked, result.State);
        Assert.Empty(main.CompactedThreads);
    }

    private sealed class FixedUsageSource(params NativeUsageRecord[] records) : IUsageSource
    {
        public IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default) => records;
    }

    private sealed class FixedRuntime(RecordingMainSession main) : IControlledTaskRuntime
    {
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IMainAgentSession MainAgent => main;
        public IWorkerAdapter NativeWorker => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class MemoryRepository : ISchedulerTaskRepository
    {
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult<ScheduledDelegation?>(null);
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>([]);
        public Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RepartitionTelemetry>>([]);
        public Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingMainSession : IMainAgentSession
    {
        public event Func<MainAgentEvent, Task>? EventReceived;
        public string? BoundThreadId { get; private set; }
        public List<string> CompactedThreads { get; } = [];
        public int CreateThreadCalls { get; private set; }

        public Task<MainThreadBindingResult> BindExistingThreadAsync(string threadId, string expectedSessionId, string expectedSource, string workingDirectory, CancellationToken cancellationToken = default)
        {
            BoundThreadId = threadId;
            return Task.FromResult(new MainThreadBindingResult(threadId, expectedSessionId, expectedSource, workingDirectory, "idle", false,
                JsonSerializer.SerializeToElement(new { id = threadId, sessionId = expectedSessionId, source = expectedSource, cwd = workingDirectory, status = new { type = "idle" } })));
        }

        public async Task<MainAgentCompactionHandle> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default)
        {
            CompactedThreads.Add(threadId);
            await (EventReceived?.Invoke(new(MainAgentEventKind.CompactionStarted, threadId, "", null, null, null)) ?? Task.CompletedTask);
            await (EventReceived?.Invoke(new(MainAgentEventKind.CompactionCompleted, threadId, "", null, null, null)) ?? Task.CompletedTask);
            return new(threadId, true, JsonSerializer.SerializeToElement(new { accepted = true }));
        }

        public Task<string> CreateThreadAsync(string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) { CreateThreadCalls++; return Task.FromResult("forbidden"); }
        public Task ResumeThreadAsync(string threadId, string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MainAgentTurnHandle> StartTurnAsync(string threadId, string prompt, string modelId, string reasoningEffort, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MainAgentTurnResult> WaitForTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MainAgentTurnResult> ReadTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToApprovalAsync(string threadId, string turnId, bool approve, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
