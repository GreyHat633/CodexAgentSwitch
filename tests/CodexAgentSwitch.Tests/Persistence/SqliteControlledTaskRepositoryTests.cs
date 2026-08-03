using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Tests.Persistence;

public sealed class SqliteControlledTaskRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine("E:\\AISPace", "cas-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Fresh_database_round_trips_nested_task_data()
    {
        var database = await CreateDatabaseAsync("round-trip.db");
        var task = CreateTask("task-1", DateTimeOffset.UtcNow);

        await new SqliteControlledTaskRepository(database).UpsertAsync(task);

        var loaded = await new SqliteControlledTaskRepository(database).GetAsync(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded.Id);
        Assert.Equal(task.ProfileId, loaded.ProfileId);
        Assert.Equal(task.Title, loaded.Title);
        Assert.Equal(task.Status, loaded.Status);
        Assert.Equal(task.CreatedAt, loaded.CreatedAt);
        Assert.Single(loaded!.Turns);
        Assert.Equal(task.Turns[0].Id, loaded.Turns[0].Id);
        Assert.Equal(task.Turns[0].UserInput, loaded.Turns[0].UserInput);
        Assert.Single(loaded.Turns[0].Messages);
        Assert.Equal(task.Turns[0].Messages[0].Content, loaded.Turns[0].Messages[0].Content);
        Assert.Single(loaded.Turns[0].Workers);
        Assert.Equal(task.Turns[0].Workers[0].JobId, loaded.Turns[0].Workers[0].JobId);
    }

    [Fact]
    public async Task Upsert_updates_existing_row_and_list_orders_by_updated_at_descending()
    {
        var database = await CreateDatabaseAsync("ordering.db");
        var first = CreateTask("first", DateTimeOffset.UtcNow.AddMinutes(-2));
        var second = CreateTask("second", DateTimeOffset.UtcNow.AddMinutes(-1));
        var updatedFirst = first with { Title = "updated", UpdatedAt = DateTimeOffset.UtcNow };
        var repository = new SqliteControlledTaskRepository(database);

        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);
        await repository.UpsertAsync(updatedFirst);

        var stored = await repository.ListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(first.Id, stored[0].Id);
        Assert.Equal("updated", stored[0].Title);
        Assert.Equal(second.Id, stored[1].Id);
    }

    [Fact]
    public async Task Initialization_is_additive_for_existing_tables()
    {
        var path = Path.Combine(directory, "legacy.db");
        Directory.CreateDirectory(directory);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE task_groups (id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(path);
        await database.InitializeAsync();

        await using var check = new SqliteConnection(database.ConnectionString);
        await check.OpenAsync();
        await using var commandCheck = check.CreateCommand();
        commandCheck.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('task_groups', 'controlled_tasks') ORDER BY name";
        await using var reader = await commandCheck.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Equal(["controlled_tasks", "task_groups"], names);
    }

    private async Task<SqliteDatabase> CreateDatabaseAsync(string name)
    {
        Directory.CreateDirectory(directory);
        var database = new SqliteDatabase(Path.Combine(directory, name));
        await database.InitializeAsync();
        return database;
    }

    private static ControlledTaskSession CreateTask(string id, DateTimeOffset now) => new(
        id, Guid.NewGuid(), "profile", "title", "E:\\workspace", "main-model", "high", "main-thread",
        ControlledTaskStatus.WorkerRunning,
        [new ControlledTaskTurn(
            "turn-1", "server-turn", "input", ControlledTaskStatus.WorkerRunning,
            [new ControlledWorkerRun("job-1", "worker-thread", "turn-1", "adapter", "worker-model", "medium", WorkerJobStatus.Running, now, null, "summary", "status")],
            [new ControlledTaskMessage(Guid.NewGuid(), "turn-1", TaskMessageActor.Worker, "message", now, true, "job-1")],
            now, null, null)],
        now, now, null, null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
