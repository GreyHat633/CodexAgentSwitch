using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Domain.Projects;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteProjectRepository(SqliteDatabase database) : IProjectRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = new List<AgentProject>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM agent_projects ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(Deserialize(reader.GetString(0)));
        }

        return projects;
    }

    public async Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM agent_projects WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize(json) : null;
    }

    public async Task UpsertAsync(AgentProject project, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_projects(id, name, working_directory, is_archived, payload_json, created_at, updated_at)
            VALUES($id, $name, $directory, $archived, $payload, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                working_directory = excluded.working_directory,
                is_archived = excluded.is_archived,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$directory", project.WorkingDirectory);
        command.Parameters.AddWithValue("$archived", project.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(project, JsonOptions));
        command.Parameters.AddWithValue("$created", project.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", project.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_projects WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AgentProject Deserialize(string json) =>
        JsonSerializer.Deserialize<AgentProject>(json, JsonOptions)
        ?? throw new InvalidDataException("存储的项目 JSON 无效。");
}
