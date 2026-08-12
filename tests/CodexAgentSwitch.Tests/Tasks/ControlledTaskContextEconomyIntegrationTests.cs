using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ControlledTaskContextEconomyIntegrationTests
{
    [Fact]
    public async Task Rollover_replays_checkpoint_persists_new_thread_and_runs_pending_turn_on_new_thread()
    {
        var now = DateTimeOffset.UtcNow;
        var main = new RecordingMainAgent();
        var usage = new MemoryUsageLedger();
        var budget = new SessionContextBudget(new SessionContextBudgetOptions(
            compactAge: TimeSpan.FromDays(1), rolloverAge: TimeSpan.FromDays(2),
            compactTurns: 1, rolloverTurns: 2));
        var harness = CreateHarness(now, main, usage, budget);

        var started = await harness.Service.StartAsync("first", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        await harness.Service.ContinueAsync(first.Id, "second", useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);

        Assert.Equal("thread-new", completed.MainThreadId);
        Assert.Single(main.RolloverCalls);
        Assert.Equal("thread-old", main.RolloverCalls[0].PreviousThreadId);
        Assert.Equal(first.Id, main.RolloverCalls[0].Checkpoint.SourceTaskId);
        Assert.Equal("thread-old", main.RolloverCalls[0].Checkpoint.SourceThreadId);
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-new" && turn.Prompt == main.RolloverCalls[0].Checkpoint.RenderReplayText());
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-new" && turn.Prompt.Contains("second", StringComparison.Ordinal));
        Assert.DoesNotContain(main.StartedTurns, turn => turn.ThreadId == "thread-old" && turn.Prompt.Contains("second", StringComparison.Ordinal));

        await harness.Service.ContinueAsync(completed.Id, "third", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, completed.Id, minimumTurns: 3);
        Assert.Single(main.RolloverCalls);
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-new" && turn.Prompt.Contains("third", StringComparison.Ordinal));
        Assert.DoesNotContain(main.StartedTurns, turn => turn.ThreadId == "thread-old" && turn.Prompt.Contains("third", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compact_calls_only_compact_on_the_existing_thread_before_pending_turn()
    {
        var main = new RecordingMainAgent();
        var budget = new SessionContextBudget(new SessionContextBudgetOptions(
            compactAge: TimeSpan.FromDays(1), rolloverAge: TimeSpan.FromDays(2),
            compactTurns: 2, rolloverTurns: 100));
        var harness = CreateHarness(DateTimeOffset.UtcNow, main, new MemoryUsageLedger(), budget);
        var started = await harness.Service.StartAsync("first", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        await harness.Service.ContinueAsync(first.Id, "second", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);
        Assert.Single(main.CompactedThreads);
        Assert.Equal("thread-old", main.CompactedThreads[0]);
        Assert.Empty(main.RolloverCalls);
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-old" && turn.Prompt.Contains("second", StringComparison.Ordinal));
        await harness.Service.ContinueAsync(first.Id, "third", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 3);
        Assert.Single(main.CompactedThreads);
    }

    [Fact]
    public async Task Continue_below_budget_does_not_call_context_apis()
    {
        var main = new RecordingMainAgent();
        var harness = CreateHarness(DateTimeOffset.UtcNow, main, new MemoryUsageLedger(), new SessionContextBudget());
        var started = await harness.Service.StartAsync("first", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        await harness.Service.ContinueAsync(first.Id, "second", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);
        Assert.Empty(main.CompactedThreads);
        Assert.Empty(main.RolloverCalls);
    }

    private static Harness CreateHarness(DateTimeOffset now, RecordingMainAgent main, MemoryUsageLedger usage, SessionContextBudget budget)
    {
        var profile = new Profile(Guid.NewGuid(), "test", new AgentSelection("model", "medium"),
            new WorkerPolicy(false, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
            new BudgetLimits(null, null, null, null, null, "CNY"), true, now, now, now);
        var profiles = new MemoryProfiles(profile);
        var tasks = new MemoryTasks();
        var clock = new FixedClock(now);
        var runtime = new FakeRuntime(main);
        var service = new ControlledTaskService(tasks, profiles, runtime,
            new TaskProfileSnapshotFactory(new EmptyProviders(), clock), new DelegationDecisionService(clock),
            new WorkerOrchestrator(new RejectingExternalFactory(), runtime, new ExternalProviderResolver()),
            usage, new WorkerUsageCollector(new CostCalculator()), clock, contextBudget: budget);
        var root = Environment.CurrentDirectory;
        return new Harness(service, tasks, root);
    }

    private static async Task<ControlledTaskSession> WaitForTerminalAsync(MemoryTasks tasks, string id, int minimumTurns = 1)
    {
        for (var i = 0; i < 100; i++)
        {
            var value = await tasks.GetAsync(id);
            if (value is not null && value.Turns.Count >= minimumTurns && value.Status == ControlledTaskStatus.Completed) return value;
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }

    private sealed record Harness(ControlledTaskService Service, MemoryTasks Tasks, string WorkingDirectory);
    private sealed class FixedClock(DateTimeOffset value) : IClock { public DateTimeOffset UtcNow => value; }
    private sealed class MemoryTasks : IControlledTaskRepository
    {
        private readonly Dictionary<string, ControlledTaskSession> values = new();
        public Task UpsertAsync(ControlledTaskSession task, CancellationToken cancellationToken = default) { values[task.Id] = task; return Task.CompletedTask; }
        public Task<ControlledTaskSession?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(values.TryGetValue(id, out var value) ? value : null);
        public Task<IReadOnlyList<ControlledTaskSession>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ControlledTaskSession>>(values.Values.ToArray());
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) { values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class MemoryProfiles(Profile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Profile>>([profile]);
        public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(id == profile.Id ? profile : null);
        public Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(profile);
        public Task UpsertAsync(Profile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class EmptyProviders : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderConfiguration>>([]);
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ProviderConfiguration?>(null);
        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class MemoryUsageLedger(IEnumerable<UsageSnapshot>? initial = null) : IUsageLedgerRepository
    {
        private readonly List<UsageSnapshot> values = initial?.ToList() ?? [];
        public Task UpsertTaskGroupAsync(TaskGroupLedger ledger, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TaskGroupLedger?> GetTaskGroupAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<TaskGroupLedger?>(null);
        public Task<IReadOnlyList<TaskGroupLedger>> ListTaskGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskGroupLedger>>([]);
        public Task AppendUsageAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) { values.Add(snapshot); return Task.CompletedTask; }
        public Task<IReadOnlyList<UsageSnapshot>> ListUsageAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UsageSnapshot>>(values);
    }
    private sealed class FakeRuntime(RecordingMainAgent main) : IControlledTaskRuntime
    {
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IMainAgentSession MainAgent => main;
        public IWorkerAdapter NativeWorker => throw new NotSupportedException();
    }
    private sealed class RejectingExternalFactory : IExternalWorkerAdapterFactory { public IWorkerAdapter Create(CodexAgentSwitch.Domain.Providers.ProviderConfiguration provider) => throw new NotSupportedException(); }

    private sealed class RecordingMainAgent : IMainAgentSession
    {
        public event Func<MainAgentEvent, Task>? EventReceived;
        public List<(string ThreadId, string Prompt)> StartedTurns { get; } = [];
        public List<string> CompactedThreads { get; } = [];
        public List<(string PreviousThreadId, CompactCheckpoint Checkpoint)> RolloverCalls { get; } = [];
        private int turn;
        public Task<string> CreateThreadAsync(string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.FromResult("thread-old");
        public Task ResumeThreadAsync(string threadId, string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MainAgentTurnHandle> StartTurnAsync(string threadId, string prompt, string modelId, string reasoningEffort, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default)
        { _ = EventReceived; var id = $"turn-{++turn}"; StartedTurns.Add((threadId, prompt)); return Task.FromResult(new MainAgentTurnHandle(threadId, id)); }
        public Task<MainAgentTurnResult> WaitForTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.FromResult(new MainAgentTurnResult(threadId, turnId, ControlledTaskStatus.Completed, "ok", null, JsonSerializer.SerializeToElement(new { status = "completed" })));
        public Task<MainAgentTurnResult> ReadTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => WaitForTurnAsync(threadId, turnId, cancellationToken);
        public Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToApprovalAsync(string threadId, string turnId, bool approve, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MainAgentCompactionHandle> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default) { CompactedThreads.Add(threadId); return Task.FromResult(new MainAgentCompactionHandle(threadId, true, default)); }
        public Task<MainAgentRolloverResult> RolloverThreadAsync(string previousThreadId, CompactCheckpoint checkpoint, string modelId, string reasoningEffort, string workingDirectory, ExecutionApprovalMode approvalMode, bool startFirstTurn = true, CancellationToken cancellationToken = default)
        { RolloverCalls.Add((previousThreadId, checkpoint)); var first = new MainAgentTurnHandle("thread-new", "replay"); StartedTurns.Add(("thread-new", checkpoint.RenderReplayText())); return Task.FromResult(new MainAgentRolloverResult(previousThreadId, "thread-new", checkpoint, first)); }
    }
}
