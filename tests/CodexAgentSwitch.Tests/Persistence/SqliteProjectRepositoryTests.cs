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
