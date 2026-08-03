using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class NativeCodexIntegrationTests
{
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
}
