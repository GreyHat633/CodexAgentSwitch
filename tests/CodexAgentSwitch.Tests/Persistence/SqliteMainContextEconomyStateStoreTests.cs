using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.Persistence;

public sealed class SqliteMainContextEconomyStateStoreTests
{
    [Fact]
    public async Task Snapshot_round_trips_structured_compaction_telemetry()
    {
        var root = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var path = Path.Combine(root, "context-store-" + Guid.NewGuid().ToString("N"), "state.db");
        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteMainContextEconomyStateStore(database);
            var compactedAt = DateTimeOffset.Parse("2026-08-12T05:49:07.087Z");
            var effectiveness = new CompactionEffectivenessResult(
                CompactionEffectiveness.Effective, 0.7m, 218856, 65000, 180000, 42000, "observed");
            var snapshot = new ContextEconomySnapshot(
                "thread-vscode", ContextEconomyState.Cooldown, 0, 8, [], [], "verified", [], compactedAt,
                CompactionTrigger.HostAutomatic, compactedAt, 0.847m, 218856, 0.252m, 65000, effectiveness);

            await store.SaveAsync(snapshot);
            var loaded = await store.LoadAsync("thread-vscode");

            Assert.Equal(snapshot.ThreadId, loaded!.ThreadId);
            Assert.Equal(snapshot.State, loaded.State);
            Assert.Equal(snapshot.StructuredCompactedAt, loaded.StructuredCompactedAt);
            Assert.Equal(snapshot.PreCompactionInput, loaded.PreCompactionInput);
            Assert.Equal(snapshot.PostCompactionInput, loaded.PostCompactionInput);
            Assert.Equal(CompactionTrigger.HostAutomatic, loaded!.LastCompactionTrigger);
            Assert.Equal(CompactionEffectiveness.Effective, loaded.LastEffectiveness!.Classification);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
