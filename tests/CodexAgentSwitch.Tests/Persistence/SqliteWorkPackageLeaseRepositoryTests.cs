using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.Persistence;

public sealed class SqliteWorkPackageLeaseRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? Path.GetTempPath(), "cas-leases-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Invalidated_and_completed_latest_snapshots_do_not_resurrect_after_reload()
    {
        Directory.CreateDirectory(root);
        var database = new SqliteDatabase(Path.Combine(root, "leases.db"));
        await database.InitializeAsync();
        var repository = new SqliteWorkPackageLeaseRepository(database);
        var lease = NewLease("pkg", WorkPackageLeaseStatus.MAIN_OWNED);

        await repository.SaveAsync(lease);
        lease.Invalidate("superseded");
        await repository.SaveAsync(lease);
        Assert.Null(await repository.GetActiveAsync("pkg", root));

        var completed = NewLease("pkg-complete", WorkPackageLeaseStatus.MAIN_OWNED);
        await repository.SaveAsync(completed);
        completed.OnPackageComplete();
        await repository.SaveAsync(completed);
        Assert.Null(await new SqliteWorkPackageLeaseRepository(database).GetActiveAsync("pkg-complete", root));
    }

    [Fact]
    public async Task Legacy_repartition_schema_is_migrated_idempotently()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy.db");
        var database = new SqliteDatabase(path);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE scheduler_repartitions (task_group_id TEXT NOT NULL, sequence INTEGER NOT NULL, recorded_at TEXT NOT NULL, trigger INTEGER NOT NULL, decision INTEGER NOT NULL, reason INTEGER NOT NULL, work_summary TEXT NOT NULL, worker_identity TEXT NULL, result TEXT NULL, PRIMARY KEY(task_group_id, sequence));";
            await command.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();
        await database.InitializeAsync();
        var repository = new SqliteSchedulerTaskRepository(database);
        await repository.AppendRepartitionAsync(new CodexAgentSwitch.Application.Scheduling.RepartitionTelemetry(
            "legacy", 1, DateTimeOffset.UtcNow, RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, "legacy append", null, null,
            "pkg", root, "Implementation", [root], 0));
        var item = Assert.Single(await repository.ListRepartitionsAsync("legacy"));
        Assert.Equal("pkg", item.PackageId);
    }

    private WorkPackageLease NewLease(string packageId, WorkPackageLeaseStatus status) => new(
        packageId, "group", root, WorkOwner.Main, "Implementation", RepartitionReasonCode.FINAL_INTEGRATION,
        RepartitionTrigger.PHASE_CHANGE, DateTimeOffset.UtcNow, 0, [root], status);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
