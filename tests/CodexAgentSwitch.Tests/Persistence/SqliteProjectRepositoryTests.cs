using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Tests.Persistence;

public sealed class SqliteProjectRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? Path.GetTempPath(),
        "cas-projects-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Projects_survive_repository_recreation_and_order_by_updated_at()
    {
        Directory.CreateDirectory(directory);
        var database = new SqliteDatabase(Path.Combine(directory, "projects.db"));
        await database.InitializeAsync();
        var repository = new SqliteProjectRepository(database);
        var first = NewProject("first", DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = NewProject("second", DateTimeOffset.UtcNow);

        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        var reloaded = await new SqliteProjectRepository(database).ListAsync();

        Assert.Equal([second.Id, first.Id], reloaded.Select(project => project.Id));
        Assert.Equal(first, await repository.GetAsync(first.Id));
    }

    [Fact]
    public async Task Schema_is_additive_and_archive_update_is_persisted()
    {
        Directory.CreateDirectory(directory);
        var database = new SqliteDatabase(Path.Combine(directory, "schema.db"));
        await database.InitializeAsync();
        var project = NewProject("archive", DateTimeOffset.UtcNow);
        var repository = new SqliteProjectRepository(database);
        await repository.UpsertAsync(project);

        await repository.UpsertAsync(project with { IsArchived = true, UpdatedAt = project.UpdatedAt.AddMinutes(1) });
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('controlled_tasks', 'agent_projects') ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(["agent_projects", "controlled_tasks"], names);
        Assert.True((await repository.GetAsync(project.Id))!.IsArchived);
    }

    [Fact]
    public async Task Applied_native_snapshot_survives_repository_recreation()
    {
        Directory.CreateDirectory(directory);
        var database = new SqliteDatabase(Path.Combine(directory, "snapshot.db"));
        await database.InitializeAsync();
        var project = NewProject("snapshot", DateTimeOffset.UtcNow);
        var snapshot = new NativeCodexAppliedSnapshot(
            Guid.NewGuid(), "Sol + Luna", "gpt-5.6-sol", "high", "NativeAgent",
            "cas_luna_worker", "gpt-5.6-luna", "openai", "medium", 3,
            "Economic", "Validated", "ABC123");
        await new SqliteProjectRepository(database).UpsertAsync(project with
        {
            NativeCodexAdaptation = new NativeCodexProjectAdaptation(
                snapshot.ProfileId, snapshot.ProfileName, ".codex/config.toml", null,
                DateTimeOffset.UtcNow, "native worker applied", false, snapshot),
        });

        var reloaded = await new SqliteProjectRepository(database).GetAsync(project.Id);

        Assert.Equal(snapshot, reloaded!.NativeCodexAdaptation!.AppliedSnapshot);
    }

    private static AgentProject NewProject(string name, DateTimeOffset timestamp) =>
        new(Guid.NewGuid().ToString("D"), name, Directory.GetCurrentDirectory(), false, timestamp, timestamp);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
