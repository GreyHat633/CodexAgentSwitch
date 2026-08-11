using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
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

    public async Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(
        string taskGroupId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<RepartitionTelemetry>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, recorded_at, trigger, decision, reason, work_summary, worker_identity, result
            FROM scheduler_repartitions
            WHERE task_group_id = $task_group_id
            ORDER BY sequence ASC
            """;
        command.Parameters.AddWithValue("$task_group_id", taskGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RepartitionTelemetry(
                taskGroupId,
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                (RepartitionTrigger)reader.GetInt32(2),
                (WorkOwner)reader.GetInt32(3),
                (RepartitionReasonCode)reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    public async Task AppendRepartitionAsync(
        RepartitionTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scheduler_repartitions(
                task_group_id, sequence, recorded_at, trigger, decision, reason,
                work_summary, worker_identity, result)
            VALUES($task_group_id, $sequence, $recorded_at, $trigger, $decision, $reason,
                $work_summary, $worker_identity, $result)
            """;
        command.Parameters.AddWithValue("$task_group_id", telemetry.TaskGroupId);
        command.Parameters.AddWithValue("$sequence", telemetry.Sequence);
        command.Parameters.AddWithValue("$recorded_at", telemetry.RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$trigger", (int)telemetry.Trigger);
        command.Parameters.AddWithValue("$decision", (int)telemetry.Decision);
        command.Parameters.AddWithValue("$reason", (int)telemetry.Reason);
        command.Parameters.AddWithValue("$work_summary", telemetry.WorkSummary);
        command.Parameters.AddWithValue("$worker_identity", (object?)telemetry.WorkerIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)telemetry.Result ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ScheduledDelegation Deserialize(string json) =>
        JsonSerializer.Deserialize<ScheduledDelegation>(json, JsonOptions)
        ?? throw new InvalidDataException("Stored scheduler task JSON is invalid.");
}
