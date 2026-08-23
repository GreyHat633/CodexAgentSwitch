using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteManagedContextSessionStore(SqliteDatabase database) : IManagedContextSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ManagedContextSession?> LoadByTaskSessionAsync(
        string taskSessionId,
        CancellationToken cancellationToken = default) =>
        LoadAsync("task_session_id", taskSessionId, cancellationToken);

    public Task<ManagedContextSession?> LoadByThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        LoadAsync("thread_id", threadId, cancellationToken);

    public async Task<IReadOnlyList<ManagedContextSession>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<ManagedContextSession>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM managed_context_sessions ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(Deserialize(reader.GetString(0)));
        }

        return sessions;
    }

    public async Task UpsertAsync(
        ManagedContextSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.TaskSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AppServerInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.OwnershipLeaseId);
        var persisted = session with { UpdatedAt = session.UpdatedAt ?? DateTimeOffset.UtcNow };
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO managed_context_sessions(
                task_session_id, project_id, canonical_project_root, thread_id, session_id,
                app_server_instance_id, ownership_lease_id,
                ownership_state, payload_json, updated_at)
            VALUES(
                $task_session_id, $project_id, $canonical_project_root, $thread_id, $session_id,
                $app_server_instance_id, $ownership_lease_id,
                $ownership_state, $payload_json, $updated_at)
            ON CONFLICT(task_session_id) DO UPDATE SET
                project_id = excluded.project_id,
                canonical_project_root = excluded.canonical_project_root,
                thread_id = excluded.thread_id,
                session_id = excluded.session_id,
                app_server_instance_id = excluded.app_server_instance_id,
                ownership_lease_id = excluded.ownership_lease_id,
                ownership_state = excluded.ownership_state,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$task_session_id", persisted.TaskSessionId);
        command.Parameters.AddWithValue("$project_id", persisted.ProjectId);
        command.Parameters.AddWithValue("$canonical_project_root", persisted.CanonicalProjectRoot);
        command.Parameters.AddWithValue("$thread_id", persisted.ThreadId);
        command.Parameters.AddWithValue("$session_id", persisted.SessionId);
        command.Parameters.AddWithValue("$app_server_instance_id", persisted.AppServerInstanceId);
        command.Parameters.AddWithValue("$ownership_lease_id", persisted.OwnershipLeaseId);
        command.Parameters.AddWithValue("$ownership_state", (int)persisted.OwnershipState);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(persisted, JsonOptions));
        command.Parameters.AddWithValue("$updated_at", persisted.UpdatedAt!.Value.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryUpdateLeaseAsync(
        ManagedContextSession session,
        string expectedOwnershipLeaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.TaskSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.OwnershipLeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnershipLeaseId);
        var persisted = session with { UpdatedAt = session.UpdatedAt ?? DateTimeOffset.UtcNow };
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE managed_context_sessions SET
                project_id = $project_id,
                canonical_project_root = $canonical_project_root,
                thread_id = $thread_id,
                session_id = $session_id,
                app_server_instance_id = $app_server_instance_id,
                ownership_lease_id = $ownership_lease_id,
                ownership_state = $ownership_state,
                payload_json = $payload_json,
                updated_at = $updated_at
            WHERE task_session_id = $task_session_id
              AND ownership_lease_id = $expected_ownership_lease_id
            """;
        command.Parameters.AddWithValue("$task_session_id", persisted.TaskSessionId);
        command.Parameters.AddWithValue("$project_id", persisted.ProjectId);
        command.Parameters.AddWithValue("$canonical_project_root", persisted.CanonicalProjectRoot);
        command.Parameters.AddWithValue("$thread_id", persisted.ThreadId);
        command.Parameters.AddWithValue("$session_id", persisted.SessionId);
        command.Parameters.AddWithValue("$app_server_instance_id", persisted.AppServerInstanceId);
        command.Parameters.AddWithValue("$ownership_lease_id", persisted.OwnershipLeaseId);
        command.Parameters.AddWithValue("$expected_ownership_lease_id", expectedOwnershipLeaseId);
        command.Parameters.AddWithValue("$ownership_state", (int)persisted.OwnershipState);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(persisted, JsonOptions));
        command.Parameters.AddWithValue("$updated_at", persisted.UpdatedAt!.Value.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task DeleteAsync(
        string taskSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM managed_context_sessions WHERE task_session_id = $task_session_id";
        command.Parameters.AddWithValue("$task_session_id", taskSessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ManagedContextSession?> LoadAsync(
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM managed_context_sessions WHERE {column} = $value";
        command.Parameters.AddWithValue("$value", value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string json ? Deserialize(json) : null;
    }

    private static ManagedContextSession Deserialize(string json) =>
        JsonSerializer.Deserialize<ManagedContextSession>(json, JsonOptions)
        ?? throw new InvalidDataException("存储的受管上下文会话 JSON 无效。");
}
