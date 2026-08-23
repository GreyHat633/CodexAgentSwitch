using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class RealManagedContextEconomyAcceptanceTests
{
    [Fact]
    [Trait("Category", "LiveManagedContextEconomy")]
    public async Task Real_managed_task_compacts_verifies_and_leaves_unmanaged_task_unobserved()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_MANAGED_CONTEXT_E2E"), "1", StringComparison.Ordinal))
            return;

        var configuredRoot = Environment.GetEnvironmentVariable("CAS_E2E_ROOT")
            ?? throw new InvalidOperationException("CAS_E2E_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(Path.GetFullPath(configuredRoot), $"managed-context-live-{Guid.NewGuid():N}");
        Assert.StartsWith("E:\\", root, StringComparison.OrdinalIgnoreCase);
        var managedWorkspace = Path.Combine(root, "managed-workspace");
        var unmanagedWorkspace = Path.Combine(root, "unmanaged-workspace");
        Directory.CreateDirectory(managedWorkspace);
        Directory.CreateDirectory(unmanagedWorkspace);
        CodexRuntimeManager? runtimeManager = null;
        try
        {
            var clock = new SystemClock();
            var database = new SqliteDatabase(Path.Combine(root, "state.db"));
            await database.InitializeAsync();
            var profiles = new SqliteProfileRepository(database);
            var projects = new SqliteProjectRepository(database);
            var tasks = new SqliteControlledTaskRepository(database);
            var usageLedger = new SqliteUsageLedgerRepository(database);
            var managedSessions = new SqliteManagedContextSessionStore(database);
            var contextStore = new SqliteMainContextEconomyStateStore(database);
            var profile = CreateProfile(clock.UtcNow);
            await profiles.UpsertAsync(profile);
            var project = CreateProject(profile, managedWorkspace, clock.UtcNow);
            await projects.UpsertAsync(project);

            runtimeManager = new CodexRuntimeManager(
                new CodexCommandLocator(),
                new CodexSchemaCache(Path.Combine(root, "protocol-cache")),
                clock);
            var runtime = new ControlledTaskRuntime(runtimeManager);
            await runtime.EnsureStartedAsync();
            var contextEvents = new ConcurrentQueue<object>();
            runtime.MainAgent.EventReceived += value =>
            {
                if (value.Kind is MainAgentEventKind.TokenUsageUpdated
                    or MainAgentEventKind.CompactionStarted
                    or MainAgentEventKind.CompactionCompleted
                    or MainAgentEventKind.StatusChanged)
                {
                    contextEvents.Enqueue(new
                    {
                        Kind = value.Kind.ToString(),
                        value.ThreadId,
                        value.TurnId,
                        value.Status,
                        InputTokens = value.TokenUsage?.InputTokens,
                        ContextWindow = value.TokenUsage?.ModelContextWindow,
                        At = DateTimeOffset.UtcNow,
                    });
                }
                return Task.CompletedTask;
            };
            var coordinator = new MainContextEconomyCoordinator(
                new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromMinutes(2) },
                contextStore);
            var service = new ControlledTaskService(
                tasks,
                profiles,
                runtime,
                new TaskProfileSnapshotFactory(new EmptyProviders(), clock),
                new DelegationDecisionService(clock),
                new WorkerOrchestrator(new RejectingExternalFactory(), runtime, new ExternalProviderResolver()),
                usageLedger,
                new WorkerUsageCollector(new CostCalculator()),
                clock,
                projectRepository: projects,
                contextEconomy: coordinator,
                managedContextSessions: managedSessions,
                managedContextPolicy: new ManagedProjectContextPolicy());

            var contextFiles = BuildToolContextFiles(managedWorkspace, 20);
            ControlledTaskSession? managedTask = null;
            ContextEconomySnapshot? compacted = null;
            for (var index = 0; index < contextFiles.Count; index++)
            {
                var prompt = $"Use the shell to run exactly: Get-Content -LiteralPath '{contextFiles[index]}' -Raw. Then reply exactly MANAGED_{index:D2}.";
                managedTask = managedTask is null
                    ? await service.StartInProjectAsync(project.Id, prompt, managedWorkspace, useWorker: false)
                    : await service.ContinueAsync(managedTask.Id, prompt, useWorker: false);
                managedTask = await WaitForManagedTurnSettledAsync(tasks, managedSessions, managedTask.Id, index + 1);
                compacted = await contextStore.LoadAsync(managedTask.MainThreadId!);
                if (compacted?.State is ContextEconomyState.Verifying or ContextEconomyState.Cooldown)
                    break;
            }

            Assert.NotNull(managedTask);
            Assert.NotNull(compacted);
            Assert.Equal(ContextEconomyState.Verifying, compacted!.State);
            Assert.Contains(contextEvents, value => JsonSerializer.Serialize(value).Contains("CompactionStarted", StringComparison.Ordinal));
            Assert.Contains(contextEvents, value => JsonSerializer.Serialize(value).Contains("CompactionCompleted", StringComparison.Ordinal));
            var managedBinding = await managedSessions.LoadByTaskSessionAsync(managedTask!.Id);
            Assert.Equal(ManagedContextOwnershipState.Verifying, managedBinding!.OwnershipState);
            Assert.NotNull(managedBinding.LastCompactionRequestId);
            Assert.NotNull(managedBinding.LastCompactionCompletedAt);

            for (var index = 1; index <= 3; index++)
            {
                await service.ContinueAsync(managedTask.Id, $"Reply exactly POST_{index}. Do not call tools.", useWorker: false);
                managedTask = await WaitForManagedTurnSettledAsync(
                    tasks, managedSessions, managedTask.Id, managedTask.Turns.Count + 1);
            }
            var verified = await contextStore.LoadAsync(managedTask.MainThreadId!);
            Assert.NotNull(verified?.LastEffectiveness);
            Assert.Equal(CompactionEffectiveness.Effective, verified!.LastEffectiveness!.Classification);
            Assert.Equal(ContextEconomyState.Cooldown, verified.State);

            var unmanaged = await service.StartAsync(
                "Reply exactly UNMANAGED_OK. Do not call tools.",
                unmanagedWorkspace,
                useWorker: false);
            unmanaged = await WaitForTerminalAsync(tasks, unmanaged.Id, 1);
            Assert.Null(await managedSessions.LoadByTaskSessionAsync(unmanaged.Id));
            Assert.Null(await contextStore.LoadAsync(unmanaged.MainThreadId!));
            Assert.Single(await managedSessions.ListAsync());

            var evidencePath = Path.Combine(root, "managed-context-acceptance.json");
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new
            {
                ManagedTaskId = managedTask.Id,
                ManagedThreadId = managedTask.MainThreadId,
                Binding = managedBinding,
                Snapshot = verified,
                Events = contextEvents,
                UnmanagedTaskId = unmanaged.Id,
                UnmanagedThreadId = unmanaged.MainThreadId,
                UnmanagedBinding = (object?)null,
                UnmanagedContextState = (object?)null,
                CompletedAt = DateTimeOffset.UtcNow,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("REAL_MANAGED_CONTEXT_EVIDENCE=" + evidencePath);
        }
        finally
        {
            if (runtimeManager is not null) await runtimeManager.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static Profile CreateProfile(DateTimeOffset now) => new(
        Guid.NewGuid(),
        "managed-live",
        new AgentSelection("gpt-5.6-sol", "low"),
        new WorkerPolicy(false, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
        new BudgetLimits(null, null, null, null, null, "CNY"),
        true,
        now,
        now,
        now)
    {
        ApprovalMode = ExecutionApprovalMode.FullAuto,
        SchemaVersion = Profile.CurrentSchemaVersion,
    };

    private static AgentProject CreateProject(Profile profile, string workspace, DateTimeOffset now)
    {
        var applied = new NativeCodexAppliedSnapshot(
            profile.Id,
            profile.Name,
            profile.MainAgent.ModelId,
            profile.MainAgent.ReasoningEffort,
            "NativeAgent",
            "cas_luna_worker",
            "gpt-5.6-luna",
            "openai",
            "high",
            1,
            "Economic",
            "Supported",
            "live-acceptance");
        return new AgentProject(
            "managed-live-project",
            "Managed Live",
            workspace,
            false,
            now,
            now,
            profile.Id,
            new NativeCodexProjectAdaptation(
                profile.Id,
                profile.Name,
                Path.Combine(workspace, ".codex", "config.toml"),
                null,
                now,
                "live-acceptance",
                false,
                applied));
    }

    private static IReadOnlyList<string> BuildToolContextFiles(string workspace, int count)
    {
        var paths = new List<string>(count);
        for (var fileIndex = 0; fileIndex < count; fileIndex++)
        {
            var path = Path.Combine(workspace, $"context-{fileIndex:D2}.txt");
            var content = string.Join(Environment.NewLine, Enumerable.Range(0, 3_000).Select(line =>
                $"managed-context-record-{fileIndex:D2}-{line:D5} alpha beta gamma delta epsilon zeta eta theta"));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            paths.Add(path);
        }
        return paths;
    }

    private static async Task<ControlledTaskSession> WaitForManagedTurnSettledAsync(
        IControlledTaskRepository tasks,
        IManagedContextSessionStore sessions,
        string taskId,
        int minimumTurns)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var task = await tasks.GetAsync(taskId, timeout.Token);
            var binding = await sessions.LoadByTaskSessionAsync(taskId, timeout.Token);
            if (task is not null
                && task.Turns.Count >= minimumTurns
                && task.Status == ControlledTaskStatus.Completed
                && binding?.OwnershipState is ManagedContextOwnershipState.Idle or ManagedContextOwnershipState.Verifying)
            {
                return task;
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static async Task<ControlledTaskSession> WaitForTerminalAsync(
        IControlledTaskRepository tasks,
        string taskId,
        int minimumTurns)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var task = await tasks.GetAsync(taskId, timeout.Token);
            if (task is not null && task.Turns.Count >= minimumTurns && task.Status == ControlledTaskStatus.Completed)
                return task;
            await Task.Delay(100, timeout.Token);
        }
    }

    private sealed class EmptyProviders : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderConfiguration>>([]);
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ProviderConfiguration?>(null);
        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RejectingExternalFactory : IExternalWorkerAdapterFactory
    {
        public IWorkerAdapter Create(ProviderConfiguration provider) => throw new NotSupportedException();
    }
}
