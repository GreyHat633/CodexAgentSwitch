using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteMainContextEconomyStateStore(SqliteDatabase database) : IMainContextEconomyStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ContextEconomySnapshot?> LoadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM main_context_economy WHERE thread_id = $thread_id";
        command.Parameters.AddWithValue("$thread_id", threadId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json
            ? JsonSerializer.Deserialize<ContextEconomySnapshot>(json, JsonOptions)?.Normalize()
            : null;
    }

    public async Task SaveAsync(
        ContextEconomySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        snapshot = snapshot.Normalize();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO main_context_economy(thread_id, state, payload_json, updated_at)
            VALUES($thread_id, $state, $payload_json, $updated_at)
            ON CONFLICT(thread_id) DO UPDATE SET
                state = excluded.state,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$thread_id", snapshot.ThreadId);
        command.Parameters.AddWithValue("$state", (int)snapshot.State);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(snapshot, JsonOptions));
        command.Parameters.AddWithValue("$updated_at", (snapshot.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
