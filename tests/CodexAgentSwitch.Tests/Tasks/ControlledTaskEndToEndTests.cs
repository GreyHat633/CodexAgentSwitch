using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ControlledTaskEndToEndTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task User_input_runs_worker_then_sol_streams_persists_usage_and_resumes_thread()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CONTROLLED_TASK_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var configuredRoot = Environment.GetEnvironmentVariable("CAS_E2E_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(configuredRoot));
        var root = Path.Combine(Path.GetFullPath(configuredRoot!), $"controlled-task-{Guid.NewGuid():N}");
        Assert.True(root.StartsWith("E:\\", StringComparison.OrdinalIgnoreCase), "E2E root must be on E drive.");
        Directory.CreateDirectory(root);
        CodexRuntimeManager? runtimeManager = null;
        try
        {
            var clock = new SystemClock();
            var database = new SqliteDatabase(Path.Combine(root, "state.db"));
            await database.InitializeAsync();
            var profileRepository = new SqliteProfileRepository(database);
            var providerRepository = new SqliteProviderRepository(database);
            var taskRepository = new SqliteControlledTaskRepository(database);
            var usageRepository = new SqliteUsageLedgerRepository(database);
            var usageCollector = new WorkerUsageCollector(new CostCalculator());
            var now = clock.UtcNow;
            var profile = new Profile(
                Guid.NewGuid(),
                "E2E Sol + Luna",
                new AgentSelection("gpt-5.6-sol", "low"),
                new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
                new BudgetLimits(null, null, null, null, null, "CNY"),
                true,
                now,
                now,
                now);
            await profileRepository.UpsertAsync(profile);

            var discovery = await new CodexCommandLocator().LocateAsync();
            Assert.True(discovery.IsAvailable, discovery.Status + Environment.NewLine + string.Join(Environment.NewLine, discovery.Attempts));
            runtimeManager = new CodexRuntimeManager(
                new CodexCommandLocator(),
                new CodexSchemaCache(Path.Combine(root, "protocol-cache")),
                clock);
            var controlledRuntime = new ControlledTaskRuntime(runtimeManager);
            await controlledRuntime.EnsureStartedAsync();
            var protocolEvents = new ConcurrentBag<MainAgentEventKind>();
            controlledRuntime.MainAgent.EventReceived += activity =>
            {
                protocolEvents.Add(activity.Kind);
                return Task.CompletedTask;
            };
            var taskStates = new ConcurrentBag<ControlledTaskStatus>();
            var service = new ControlledTaskService(
                taskRepository,
                profileRepository,
                controlledRuntime,
                new TaskProfileSnapshotFactory(providerRepository, clock),
                new DelegationDecisionService(clock),
                new WorkerOrchestrator(
                    new RejectExternalWorkerFactory(),
                    controlledRuntime,
                    new ExternalProviderResolver()),
                usageRepository,
                usageCollector,
                clock);
            service.TaskChanged += task =>
            {
                taskStates.Add(task.Status);
                return Task.CompletedTask;
            };

            var started = await service.StartAsync(
                "Return exactly CAS_CONTROLLED_E2E_OK. Do not call tools.",
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                useWorker: true);
            var completed = await WaitForTerminalAsync(taskRepository, started.Id, TimeSpan.FromMinutes(10));
            Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
            Assert.False(string.IsNullOrWhiteSpace(completed.MainThreadId));
            Assert.Single(completed.Turns);
            Assert.Single(completed.Turns[0].Workers);
            Assert.Equal("native-codex", completed.Turns[0].Workers[0].AdapterId);
            Assert.Equal("gpt-5.6-luna", completed.Turns[0].Workers[0].ModelId);
            Assert.Contains(completed.Turns[0].Messages, message => message.Actor == TaskMessageActor.Worker && !string.IsNullOrWhiteSpace(message.Content));
            Assert.Contains(completed.Turns[0].Messages, message => message.Actor == TaskMessageActor.MainAgent && message.Content.Contains("CAS_CONTROLLED_E2E_OK", StringComparison.Ordinal));
            Assert.True((await usageRepository.ListUsageAsync(completed.Id)).Count >= 2);
            Assert.Contains(ControlledTaskStatus.WorkerRunning, taskStates);
            Assert.Contains(ControlledTaskStatus.MainAgentRunning, taskStates);
            Assert.Contains(ControlledTaskStatus.Completed, taskStates);
            Assert.Contains(MainAgentEventKind.TurnCompleted, protocolEvents);
            Assert.Contains(MainAgentEventKind.OutputDelta, protocolEvents);

            var originalThreadId = completed.MainThreadId;
            await service.ContinueAsync(completed.Id, "Return exactly CAS_CONTROLLED_RESUME_OK. Do not call tools.", useWorker: false);
            var resumed = await WaitForTerminalAsync(taskRepository, completed.Id, TimeSpan.FromMinutes(10), minimumTurns: 2);
            Assert.Equal(ControlledTaskStatus.Completed, resumed.Status);
            Assert.Equal(originalThreadId, resumed.MainThreadId);
            Assert.Equal(2, resumed.Turns.Count);
            Assert.Contains(resumed.Turns[1].Messages, message => message.Actor == TaskMessageActor.MainAgent && message.Content.Contains("CAS_CONTROLLED_RESUME_OK", StringComparison.Ordinal));

            var reloaded = await new SqliteControlledTaskRepository(database).GetAsync(resumed.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.Turns.Count);
            Assert.Equal(originalThreadId, reloaded.MainThreadId);
        }
        finally
        {
            if (runtimeManager is not null)
            {
                await runtimeManager.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<ControlledTaskSession> WaitForTerminalAsync(
        IControlledTaskRepository repository,
        string id,
        TimeSpan timeout,
        int minimumTurns = 1)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var task = await repository.GetAsync(id, cancellation.Token)
                ?? throw new InvalidOperationException("Controlled task disappeared during E2E run.");
            if (task.Turns.Count >= minimumTurns && task.Status is
                ControlledTaskStatus.Completed or ControlledTaskStatus.Failed or ControlledTaskStatus.Interrupted or ControlledTaskStatus.UnknownRecoverable)
            {
                return task;
            }

            await Task.Delay(250, cancellation.Token);
        }
    }

    private sealed class RejectExternalWorkerFactory : IExternalWorkerAdapterFactory
    {
        public IWorkerAdapter Create(CodexAgentSwitch.Domain.Providers.ProviderConfiguration provider) =>
            throw new InvalidOperationException("The controlled native E2E test must not resolve an external Provider.");
    }
}
