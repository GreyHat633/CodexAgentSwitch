using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Scheduling;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteSchedulerTaskRepository(SqliteDatabase database) : ISchedulerTaskRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM scheduler_tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", taskId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize(json) : null;
    }

    public async Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ScheduledDelegation>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM scheduler_tasks ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Deserialize(reader.GetString(0)));
        }

        return result;
    }

    public async Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scheduler_tasks(id, state, payload_json, created_at, updated_at)
            VALUES($id, $state, $payload, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                state = excluded.state,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", task.Packet.TaskId);
        command.Parameters.AddWithValue("$state", (int)task.State);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(task, JsonOptions));
        command.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ScheduledDelegation Deserialize(string json) =>
        JsonSerializer.Deserialize<ScheduledDelegation>(json, JsonOptions)
        ?? throw new InvalidDataException("Stored scheduler task JSON is invalid.");
}
