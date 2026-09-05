using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class NativeCodexIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Current_app_server_returns_validated_four_role_catalog()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_ASTRA_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var discovery = await new CodexCommandLocator().LocateAsync();
        Assert.True(discovery.IsAvailable, discovery.Status);
        await using var client = new CodexAppServerClient(discovery.Command!);
        var capabilities = await new NativeCodexWorkerAdapter(client, new SystemClock()).GetCapabilitiesAsync();
        var expected = new Dictionary<string, string[]>
        {
            ["gpt-6-astra"] = ["low", "medium", "high", "xhigh", "max", "ultra"],
            ["gpt-5.6-sol"] = ["low", "medium", "high", "xhigh", "max", "ultra"],
            ["gpt-5.6-terra"] = ["low", "medium", "high", "xhigh", "max", "ultra"],
            ["gpt-5.6-luna"] = ["low", "medium", "high", "xhigh", "max"],
        };

        foreach (var (modelId, efforts) in expected)
        {
            var model = Assert.Single(capabilities.Models, item => item.Id == modelId);
            Assert.Equal(efforts, model.SupportedReasoningEfforts);
            Console.WriteLine($"CAS_MODEL={model.Id};DEFAULT={model.IsDefault};EFFORTS={string.Join(',', model.SupportedReasoningEfforts)}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Current_app_server_runs_astra_main_turn_with_exact_effort()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_ASTRA_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var discovery = await new CodexCommandLocator().LocateAsync();
        Assert.True(discovery.IsAvailable, discovery.Status);
        await using var client = new CodexAppServerClient(discovery.Command!);
        var session = new CodexMainAgentSession(client);
        var root = RepositoryRoot();
        var threadId = await session.CreateThreadAsync("gpt-6-astra", root, ExecutionApprovalMode.Safe);
        try
        {
            var turn = await session.StartTurnAsync(
                threadId,
                "Return exactly CAS_ASTRA_MAIN_OK. Do not call tools.",
                "gpt-6-astra",
                "low",
                root,
                ExecutionApprovalMode.Safe);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await session.WaitForTurnAsync(turn.ThreadId, turn.TurnId, timeout.Token);

            Assert.Contains("CAS_ASTRA_MAIN_OK", result.FinalText ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            await client.RequestAsync("thread/delete", new { threadId });
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Current_app_server_runs_astra_native_worker()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_ASTRA_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var discovery = await new CodexCommandLocator().LocateAsync();
        Assert.True(discovery.IsAvailable, discovery.Status);
        await using var client = new CodexAppServerClient(discovery.Command!);
        var adapter = new NativeCodexWorkerAdapter(client, new SystemClock());
        var task = new WorkerTask(
            "CAS-ASTRA-INTEGRATION",
            "CAS-ASTRA-INTEGRATION-L1",
            "Validate Astra native Worker execution.",
            "Return exactly CAS_ASTRA_WORKER_OK. Do not call tools.",
            RepositoryRoot(),
            "gpt-6-astra",
            "low",
            new WorkerScope([], [], [ScopeOperation.Read]),
            ["CAS_ASTRA_WORKER_OK"],
            ["Exact response"],
            ["Any tool call"]);
        var job = await adapter.SpawnAsync(task);
        try
        {
            var result = await adapter.WaitAsync(job.JobId, TimeSpan.FromMinutes(2));
            Assert.NotNull(result);
            Assert.Equal(WorkerJobStatus.Completed, result.Status);
            Assert.Contains("CAS_ASTRA_WORKER_OK", result.Summary ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            var status = await adapter.ReadStatusAsync(job.JobId);
            if (status.Status == WorkerJobStatus.Running)
            {
                await adapter.CancelAsync(job.JobId);
            }

            await adapter.DeleteAsync(job.JobId);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Current_app_server_supports_schema_models_and_worker_lifecycle()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CODEX_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var discovery = await new CodexCommandLocator().LocateAsync();
        Assert.True(discovery.IsAvailable, discovery.Status + Environment.NewLine + string.Join(Environment.NewLine, discovery.Attempts));
        var cacheRoot = Path.Combine(Path.GetTempPath(), "cas-protocol-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var schema = await new CodexSchemaCache(cacheRoot).GenerateAsync(discovery.Command!, discovery.Version!);
            Assert.NotEmpty(schema.Sha256);
            Assert.True(Directory.GetFiles(schema.Directory, "*.json", SearchOption.AllDirectories).Length > 100);

            await using var client = new CodexAppServerClient(discovery.Command!);
            var adapter = new NativeCodexWorkerAdapter(client, new SystemClock());
            var capabilities = await adapter.GetCapabilitiesAsync();
            Console.WriteLine($"CAS_MODEL_LIST={string.Join(",", capabilities.Models.Select(item => $"{item.Id}|default={item.IsDefault}"))}");
            var model = capabilities.Models.FirstOrDefault(candidate => candidate.Id.Contains("luna", StringComparison.OrdinalIgnoreCase))
                ?? capabilities.Models.First(candidate => candidate.IsDefault);
            Assert.NotEmpty(model.SupportedReasoningEfforts);

            var task = new WorkerTask(
                "CAS-INTEGRATION",
                "CAS-INTEGRATION-L1",
                "Return a deterministic health response.",
                "Return exactly CAS_OK. Do not call tools.",
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                model.Id,
                model.SupportedReasoningEfforts.First(),
                new WorkerScope([], [], [ScopeOperation.Read]),
                ["CAS_OK"],
                ["Exact response"],
                ["Any tool call"]);
            var job = await adapter.SpawnAsync(task);
            var result = await adapter.WaitAsync(job.JobId, TimeSpan.FromMinutes(2));
            if (result is null)
            {
                await adapter.CancelAsync(job.JobId);
                throw new TimeoutException("Native Codex Worker did not complete in two minutes.");
            }

            Assert.Equal(WorkerJobStatus.Completed, result.Status);
            Assert.Contains("CAS_OK", result.Summary ?? string.Empty, StringComparison.Ordinal);
            await adapter.DeleteAsync(job.JobId);
            Assert.Equal(WorkerJobStatus.Deleted, (await adapter.ReadStatusAsync(job.JobId)).Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Current_app_server_runs_three_independent_workers_without_duplicate_scope()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CODEX_CONCURRENCY_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var discovery = await new CodexCommandLocator().LocateAsync();
        Assert.True(discovery.IsAvailable, discovery.Status);
        await using var client = new CodexAppServerClient(discovery.Command!);
        var adapter = new NativeCodexWorkerAdapter(client, new SystemClock());
        var capabilities = await adapter.GetCapabilitiesAsync();
        var model = capabilities.Models.FirstOrDefault(candidate => candidate.Id.Contains("luna", StringComparison.OrdinalIgnoreCase))
            ?? capabilities.Models.First(candidate => candidate.IsDefault);
        var effort = model.SupportedReasoningEfforts.First();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tasks = Enumerable.Range(1, 3).Select(index => new WorkerTask(
            "CAS-CONCURRENCY",
            $"CAS-CONCURRENCY-L{index}",
            $"Return health marker {index}.",
            $"Return exactly CAS_PARALLEL_{index}. Do not call tools.",
            root,
            model.Id,
            effort,
            new WorkerScope([$"virtual/scope-{index}.txt"], [], [ScopeOperation.Read]),
            [$"CAS_PARALLEL_{index}"],
            ["Exact response"],
            ["Any tool call"])).ToArray();
        var jobs = await Task.WhenAll(tasks.Select(task => adapter.SpawnAsync(task)));
        try
        {
            var results = await Task.WhenAll(jobs.Select(job => adapter.WaitAsync(job.JobId, TimeSpan.FromMinutes(2))));
            Assert.All(results, result => Assert.NotNull(result));
            for (var index = 0; index < results.Length; index++)
            {
                Assert.Equal(WorkerJobStatus.Completed, results[index]!.Status);
                Assert.Contains($"CAS_PARALLEL_{index + 1}", results[index]!.Summary ?? string.Empty, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var job in jobs)
            {
                var status = await adapter.ReadStatusAsync(job.JobId);
                if (status.Status == WorkerJobStatus.Running)
                {
                    await adapter.CancelAsync(job.JobId);
                }

                await adapter.DeleteAsync(job.JobId);
            }
        }
    }

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
