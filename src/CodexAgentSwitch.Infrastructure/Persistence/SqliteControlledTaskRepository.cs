using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteControlledTaskRepository(SqliteDatabase database) : IControlledTaskRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(ControlledTaskSession task, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO controlled_tasks(id, payload_json, created_at, updated_at)
            VALUES($id, $payload, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(task, JsonOptions));
        command.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ControlledTaskSession?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM controlled_tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize(json) : null;
    }

    public async Task<IReadOnlyList<ControlledTaskSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ControlledTaskSession>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM controlled_tasks ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Deserialize(reader.GetString(0)));
        }

        return result;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var usage = connection.CreateCommand())
        {
            usage.Transaction = transaction;
            usage.CommandText = "DELETE FROM usage_snapshots WHERE task_group_id = $id";
            usage.Parameters.AddWithValue("$id", id);
            await usage.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = "DELETE FROM task_groups WHERE id = $id";
            ledger.Parameters.AddWithValue("$id", id);
            await ledger.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var task = connection.CreateCommand())
        {
            task.Transaction = transaction;
            task.CommandText = "DELETE FROM controlled_tasks WHERE id = $id";
            task.Parameters.AddWithValue("$id", id);
            await task.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static ControlledTaskSession Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlledTaskSession>(json, JsonOptions)
                ?? throw new InvalidDataException("Stored controlled task JSON is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored controlled task JSON is invalid.", exception);
        }
    }
}
