using System.IO.Pipes;
using System.Text;
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
using CodexAgentSwitch.Infrastructure.Persistence;
using CodexAgentSwitch.Infrastructure.Scheduling;
using CodexAgentSwitch.Infrastructure.Usage;

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
        var hooks = (await scheduler.GetRuntimeDiagnosticsAsync()).Hooks!;
        Assert.Equal(1, hooks.StopSeenCount);
        Assert.Equal(1, hooks.ContextBoundarySeenCount);
        Assert.True(hooks.ContextStateBound);
        Assert.Equal(80, hooks.LastObservedInputTokens);
        Assert.NotNull(hooks.LastObservedPressure);
        Assert.NotNull(hooks.LastCompactionRequestAt);
        Assert.Equal("Succeeded", hooks.LastCompactionResult);
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

    [Fact]
    public async Task Nullable_context_window_flows_through_parser_and_persists_bound_thread_state()
    {
        const string threadId = "019ff34a-4163-7322-8071-cca28f9458fd";
        const string cwd = "E:\\AISPace\\context-boundary-test";
        var root = CreateTestDirectory("nullable-boundary");
        var databasePath = Path.Combine(root, "state.db");
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"),
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = threadId, cwd, source = "vscode" } }) + Environment.NewLine +
                JsonSerializer.Serialize(new
                {
                    type = "event_msg",
                    payload = new
                    {
                        info = new
                        {
                            model_context_window = (long?)null,
                            last_token_usage = new { input_tokens = 10, cached_input_tokens = 4 },
                        },
                    },
                }) + Environment.NewLine);
            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var store = new SqliteMainContextEconomyStateStore(database);
            var main = new RecordingMainSession();
            var coordinator = new MainContextEconomyCoordinator(main, stateStore: store);
            await using var scheduler = new WorkerScheduler(
                [], new MemoryRepository(), new FixedClock(),
                usageSource: new CodexSessionUsageSource(root),
                contextRuntime: new FixedRuntime(main),
                contextEconomy: coordinator);

            var pipeName = "cas-main-boundary-" + Guid.NewGuid().ToString("N");
            await using var server = new SchedulerIpcServer(scheduler, pipeName);
            await server.StartAsync();
            var response = await SendRequestAsync(pipeName, JsonSerializer.Serialize(new
            {
                method = "mainContextBoundary",
                payload = new MainContextBoundaryRequest(threadId, threadId, cwd, "vscode", "stop"),
            }));
            using var responseDocument = JsonDocument.Parse(response);
            Assert.True(responseDocument.RootElement.GetProperty("ok").GetBoolean(), response);
            var result = responseDocument.RootElement.GetProperty("result")
                .Deserialize<MainContextBoundaryResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var persisted = await store.LoadAsync(threadId);
            var runtime = (await scheduler.GetRuntimeDiagnosticsAsync()).ContextEconomy;

            Assert.True(result.BindingAccepted, result.Reason);
            Assert.True(result.TelemetryAvailable);
            Assert.DoesNotContain("Number", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(threadId, main.BoundThreadId);
            Assert.NotNull(persisted);
            Assert.Equal(threadId, persisted!.ThreadId);
            Assert.Equal(10, Assert.Single(persisted.Samples).NativeInputTokens);
            Assert.Null(Assert.Single(persisted.Samples).ContextWindowTokens);
            Assert.True(runtime!.BindingAccepted);
            Assert.True(runtime.TelemetryAvailable);
            Assert.Equal(10, runtime.LatestInputTokens);
            Assert.Null(runtime.ContextWindowTokens);
            Assert.NotNull(runtime.LastBoundaryAt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Exact_binding_without_usage_returns_business_reason_instead_of_exception()
    {
        const string threadId = "missing-session";
        const string cwd = "E:\\AISPace\\missing-session";
        var main = new RecordingMainSession();
        await using var scheduler = new WorkerScheduler(
            [], new MemoryRepository(), new FixedClock(),
            usageSource: new FixedUsageSource(),
            contextRuntime: new FixedRuntime(main),
            contextEconomy: new MainContextEconomyCoordinator(main));

        var result = await scheduler.ObserveMainContextBoundaryAsync(
            new(threadId, threadId, cwd, "vscode", "stop"));

        Assert.False(result.BindingAccepted);
        Assert.False(result.TelemetryAvailable);
        Assert.Equal(ContextEconomyState.Idle, result.State);
        Assert.Equal("No exact source=vscode usage sample is available for this thread and cwd.", result.Reason);
    }

    [Fact]
    public async Task Unrelated_project_terminal_result_does_not_block_current_boundary()
    {
        const string threadId = "project-b-thread";
        const string cwd = "E:\\AISPace\\ProjectB";
        var main = new RecordingMainSession();
        await using var scheduler = new WorkerScheduler(
            [], new MemoryRepository(PendingTask("E:\\AISPace\\ProjectA")), new FixedClock(),
            usageSource: new FixedUsageSource(new NativeUsageRecord(
                threadId, cwd, cwd, "gpt-5.6-sol", "high", "Sol", 1,
                10, 4, 6, 1, 0, 11, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "session.jsonl", "cwd", 10, 4, 100, "vscode")),
            contextRuntime: new FixedRuntime(main),
            contextEconomy: new MainContextEconomyCoordinator(main));
        await scheduler.StartAsync();

        var result = await scheduler.ObserveMainContextBoundaryAsync(
            new(threadId, threadId, cwd, "vscode", "stop"));

        Assert.True(result.BindingAccepted, result.Reason);
        Assert.True(result.TelemetryAvailable);
    }

    [Fact]
    public async Task Same_working_directory_terminal_result_still_defers_boundary()
    {
        const string threadId = "project-a-thread";
        const string cwd = "E:\\AISPace\\ProjectA";
        var main = new RecordingMainSession();
        await using var scheduler = new WorkerScheduler(
            [], new MemoryRepository(PendingTask(cwd)), new FixedClock(),
            usageSource: new FixedUsageSource(new NativeUsageRecord(
                threadId, cwd, cwd, "gpt-5.6-sol", "high", "Sol", 1,
                10, 4, 6, 1, 0, 11, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "session.jsonl", "cwd", 10, 4, 100, "vscode")),
            contextRuntime: new FixedRuntime(main),
            contextEconomy: new MainContextEconomyCoordinator(main));
        await scheduler.StartAsync();

        var result = await scheduler.ObserveMainContextBoundaryAsync(
            new(threadId, threadId, cwd, "vscode", "stop"));

        Assert.False(result.BindingAccepted);
        Assert.Contains("terminal result", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static ScheduledDelegation PendingTask(string workingDirectory)
    {
        var now = DateTimeOffset.UtcNow;
        var packet = new TaskPacket(
            "stale-task", "project-a", workingDirectory, "cas_luna_worker", "goal",
            ["scope"], [], [], ["accepted"], [], "result");
        return new ScheduledDelegation(
            packet, WorkerTransport.NativeCustomAgent, DelegationState.ResultPending,
            now, now, now, now, null, null);
    }

    private static string CreateTestDirectory(string name)
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"main-boundary-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<string> SendRequestAsync(string pipeName, string request)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(request);
        return await reader.ReadLineAsync() ?? throw new IOException("Scheduler did not respond.");
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

    private sealed class MemoryRepository(params ScheduledDelegation[] tasks) : ISchedulerTaskRepository
    {
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult<ScheduledDelegation?>(null);
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>(tasks);
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
