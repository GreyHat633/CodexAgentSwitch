using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.Persistence;
using CodexAgentSwitch.Infrastructure.Scheduling;
using CodexAgentSwitch.Infrastructure.Usage;

namespace CodexAgentSwitch.Tests.Scheduling;

public sealed class RealContextEconomyAcceptanceTests
{
    [Fact]
    [Trait("Category", "LiveContextEconomy")]
    public async Task Real_vscode_thread_compacts_through_stop_hook_and_verifies_token_reduction()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CONTEXT_ECONOMY_E2E"), "1", StringComparison.Ordinal))
            return;

        var configuredRoot = Environment.GetEnvironmentVariable("CAS_E2E_ROOT")
            ?? throw new InvalidOperationException("CAS_E2E_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(Path.GetFullPath(configuredRoot), $"context-economy-live-{Guid.NewGuid():N}");
        Assert.StartsWith("E:\\", root, StringComparison.OrdinalIgnoreCase);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        CodexRuntimeManager? runtimeManager = null;
        try
        {
            var clock = new SystemClock();
            var database = new SqliteDatabase(Path.Combine(root, "state.db"));
            await database.InitializeAsync();
            var store = new SqliteMainContextEconomyStateStore(database);
            runtimeManager = new CodexRuntimeManager(
                new CodexCommandLocator(),
                new CodexSchemaCache(Path.Combine(root, "protocol-cache")),
                clock);
            var runtime = new ControlledTaskRuntime(runtimeManager);
            await runtime.EnsureStartedAsync();
            var main = runtime.MainAgent;
            var lifecycle = new ConcurrentBag<MainAgentEvent>();
            main.EventReceived += activity =>
            {
                if (activity.Kind is MainAgentEventKind.CompactionStarted or MainAgentEventKind.CompactionCompleted)
                    lifecycle.Add(activity);
                return Task.CompletedTask;
            };
            var coordinator = new MainContextEconomyCoordinator(
                main,
                new ContextEconomyOptions { CompactionTimeout = TimeSpan.FromMinutes(2) },
                store);
            var usage = new CodexSessionUsageSource(
                Environment.GetEnvironmentVariable("CODEX_HOME"),
                Path.Combine(root, "logs", "usage.jsonl"));
            await using var scheduler = new WorkerScheduler(
                [], new MemoryRepository(), clock,
                usageSource: usage,
                contextRuntime: runtime,
                contextEconomy: coordinator);
            var pipeName = "cas-context-live-" + Guid.NewGuid().ToString("N");
            await using var server = new SchedulerIpcServer(scheduler, pipeName);
            await server.StartAsync();

            var threadId = await main.CreateThreadAsync(
                "gpt-5.6-sol", workspace, ExecutionApprovalMode.Safe);
            var contextFiles = BuildToolContextFiles(workspace, 20);
            var preSamples = new List<NativeUsageRecord>();
            MainContextBoundaryResult? lastPreBoundary = null;
            foreach (var (path, index) in contextFiles.Select((path, index) => (path, index)))
            {
                var previous = preSamples.LastOrDefault();
                await RunTurnAsync(
                    main,
                    threadId,
                    workspace,
                    $"Use the shell to run exactly: Get-Content -LiteralPath '{path}' -Raw. Then reply exactly TOOL_{index:D2}.",
                    ExecutionApprovalMode.FullAuto);
                var current = await WaitForUsageAsync(usage, threadId, workspace,
                    item => item.LatestInputTokens is > 0
                        && (previous is null || item.EndedAt > previous.EndedAt));
                preSamples.Add(current);
                var pressure = current.LatestInputTokens / (decimal)current.ContextWindowTokens!;
                if (pressure >= 0.55m) break;
                lastPreBoundary = await scheduler.ObserveMainContextBoundaryAsync(
                    new(threadId, threadId, workspace, "vscode", "stop"));
                Assert.True(lastPreBoundary.BindingAccepted, lastPreBoundary.Reason);
                Assert.False(lastPreBoundary.CompactionRequested);
            }

            Assert.True(preSamples.Count >= 2, "Real pressure acceptance requires multiple pre-compaction samples.");
            var preTwo = preSamples[^1];
            Assert.True(preTwo.LatestInputTokens / (decimal)preTwo.ContextWindowTokens! >= 0.55m,
                $"Expected candidate pressure, observed {preTwo.LatestInputTokens}/{preTwo.ContextWindowTokens}.");

            var stopResult = await RunToolHostStopAsync(pipeName, root, threadId, workspace);
            Assert.Equal(0, stopResult.ExitCode);
            Assert.Contains("\"hookEventName\":\"Stop\"", stopResult.StandardOutput);
            var compactBoundary = (await scheduler.GetRuntimeDiagnosticsAsync()).ContextEconomy!;
            Assert.True(compactBoundary.BindingAccepted);
            Assert.True(compactBoundary.TelemetryAvailable);
            Assert.True(compactBoundary.CompactionRequested);
            Assert.True(compactBoundary.CompactionSucceeded);
            Assert.Equal(ContextEconomyState.Verifying, compactBoundary.State);

            var compactEvents = lifecycle.OrderBy(item => item.Kind).ToArray();
            Assert.Contains(compactEvents, item => item.Kind == MainAgentEventKind.CompactionStarted);
            Assert.Contains(compactEvents, item => item.Kind == MainAgentEventKind.CompactionCompleted);
            Assert.All(compactEvents, item => Assert.Equal(
                "contextCompaction",
                item.RawEvent!.Value.GetProperty("item").GetProperty("type").GetString()));

            var postCompactBinding = await main.BindExistingThreadAsync(
                threadId,
                threadId,
                "vscode",
                workspace);
            Assert.Equal("idle", postCompactBinding.Status);

            await RunTurnAsync(main, threadId, workspace, "Reply exactly POST_ONE. Do not call tools.");
            var postOne = await WaitForUsageAsync(usage, threadId, workspace,
                item => item.LatestInputTokens is > 0 && item.LatestInputTokens < preTwo.LatestInputTokens);
            await scheduler.ObserveMainContextBoundaryAsync(new(threadId, threadId, workspace, "vscode", "stop"));
            await RunTurnAsync(main, threadId, workspace, "Reply exactly POST_TWO. Do not call tools.");
            var postTwo = await WaitForUsageAsync(usage, threadId, workspace,
                item => item.EndedAt > postOne.EndedAt);
            var finalBoundary = await scheduler.ObserveMainContextBoundaryAsync(
                new(threadId, threadId, workspace, "vscode", "stop"));
            var snapshot = await store.LoadAsync(threadId);

            Assert.True(finalBoundary.BindingAccepted, finalBoundary.Reason);
            Assert.NotNull(snapshot);
            Assert.Equal(CompactionEffectiveness.Effective, snapshot!.LastEffectiveness?.Classification);
            Assert.True(snapshot.LastEffectiveness?.Reduction >= 0.40m);
            Assert.Equal(ContextEconomyState.Cooldown, snapshot.State);

            var evidencePath = Path.Combine(root, "context-economy-acceptance.json");
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new
            {
                ThreadId = threadId,
                WorkingDirectory = workspace,
                PreSamples = preSamples.Select(item => new { item.LatestInputTokens, item.ContextWindowTokens, item.EndedAt }),
                PostOne = new { postOne.LatestInputTokens, postOne.ContextWindowTokens, postOne.EndedAt },
                PostTwo = new { postTwo.LatestInputTokens, postTwo.ContextWindowTokens, postTwo.EndedAt },
                LastPreBoundary = lastPreBoundary,
                CompactBoundary = compactBoundary,
                FinalBoundary = finalBoundary,
                Snapshot = snapshot,
                StructuralCompactionEvents = compactEvents.Select(item => new
                {
                    Kind = item.Kind.ToString(),
                    ItemType = item.RawEvent!.Value.GetProperty("item").GetProperty("type").GetString(),
                    Raw = item.RawEvent.Value,
                }),
                UsageScan = usage.LastScanMetrics,
                CompletedAt = DateTimeOffset.UtcNow,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("REAL_CONTEXT_ECONOMY_EVIDENCE=" + evidencePath);
        }
        finally
        {
            if (runtimeManager is not null) await runtimeManager.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static IReadOnlyList<string> BuildToolContextFiles(string workspace, int count)
    {
        var paths = new List<string>(count);
        for (var fileIndex = 0; fileIndex < count; fileIndex++)
        {
            var path = Path.Combine(workspace, $"context-{fileIndex:D2}.txt");
            var content = string.Join(Environment.NewLine, Enumerable.Range(0, 3_000).Select(line =>
                $"context-record-{fileIndex:D2}-{line:D5} alpha beta gamma delta epsilon zeta eta theta"));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            paths.Add(path);
        }
        return paths;
    }

    private static async Task RunTurnAsync(
        IMainAgentSession main,
        string threadId,
        string workspace,
        string prompt,
        ExecutionApprovalMode approvalMode = ExecutionApprovalMode.Safe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var turn = await main.StartTurnAsync(
            threadId, prompt, "gpt-5.6-sol", "low", workspace, approvalMode, timeout.Token);
        var result = await main.WaitForTurnAsync(threadId, turn.TurnId, timeout.Token);
        Assert.Equal(ControlledTaskStatus.Completed, result.Status);
        Assert.Null(result.ErrorMessage);
    }

    private static async Task<NativeUsageRecord> WaitForUsageAsync(
        CodexSessionUsageSource source,
        string threadId,
        string workspace,
        Func<NativeUsageRecord, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var record = source.Read(timeout.Token)
                .Where(item => string.Equals(item.SessionId, threadId, StringComparison.Ordinal)
                    && string.Equals(item.SessionSource, "vscode", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFullPath(item.Cwd!), Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.EndedAt)
                .FirstOrDefault();
            if (record is not null && predicate(record)) return record;
            await Task.Delay(250, timeout.Token);
        }
    }

    private static async Task<ProcessResult> RunToolHostStopAsync(
        string pipeName,
        string dataRoot,
        string sessionId,
        string workingDirectory)
    {
        var start = new ProcessStartInfo(FindToolHostExecutable())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--hook");
        start.ArgumentList.Add("stop");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.Environment["CAS_DATA_ROOT"] = dataRoot;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start ToolHost.");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { session_id = sessionId, cwd = workingDirectory }));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        return new(process.ExitCode, output, error);
    }

    private static string FindToolHostExecutable()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Directory.EnumerateFiles(
                Path.Combine(root, "src", "CodexAgentSwitch.ToolHost", "bin", configuration),
                "CodexAgentSwitch.ToolHost.exe",
                SearchOption.AllDirectories)
            .Single();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CodexAgentSwitch.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Unable to locate CodexAgentSwitch.sln.");
    }

    private sealed class MemoryRepository : ISchedulerTaskRepository
    {
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult<ScheduledDelegation?>(null);
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>([]);
        public Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RepartitionTelemetry>>([]);
        public Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
