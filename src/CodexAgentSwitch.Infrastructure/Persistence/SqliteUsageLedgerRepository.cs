using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteUsageLedgerRepository(SqliteDatabase database) : IUsageLedgerRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertTaskGroupAsync(TaskGroupLedger ledger, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_groups(id, payload_json, created_at, updated_at)
            VALUES($id, $payload, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", ledger.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(ledger, JsonOptions));
        command.Parameters.AddWithValue("$created", ledger.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", ledger.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TaskGroupLedger?> GetTaskGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM task_groups WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize<TaskGroupLedger>(json) : null;
    }

    public async Task<IReadOnlyList<TaskGroupLedger>> ListTaskGroupsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TaskGroupLedger>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM task_groups ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Deserialize<TaskGroupLedger>(reader.GetString(0)));
        }

        return result;
    }

    public async Task AppendUsageAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage_snapshots(id, task_group_id, worker_job_id, captured_at, payload_json)
            VALUES($id, $group, $job, $captured, $payload)
            ON CONFLICT(id) DO UPDATE SET payload_json = excluded.payload_json
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
        command.Parameters.AddWithValue("$group", snapshot.TaskGroupId);
        command.Parameters.AddWithValue("$job", snapshot.WorkerJobId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$captured", snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UsageSnapshot>> ListUsageAsync(string taskGroupId, CancellationToken cancellationToken = default)
    {
        var result = new List<UsageSnapshot>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM usage_snapshots WHERE task_group_id = $group ORDER BY captured_at";
        command.Parameters.AddWithValue("$group", taskGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Deserialize<UsageSnapshot>(reader.GetString(0)));
        }

        return result;
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException($"Stored {typeof(T).Name} JSON is invalid.");
}
