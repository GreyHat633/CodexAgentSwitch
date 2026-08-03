using System.Text.Json;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Providers;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteProviderRepository(SqliteDatabase database) : IProviderRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default)
    {
        var providers = new List<ProviderConfiguration>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM providers ORDER BY kind, name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(Deserialize(reader.GetString(0)));
        }

        return providers;
    }

    public async Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM providers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize(json) : null;
    }

    public async Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO providers(id, name, kind, enabled, credential_reference, payload_json, created_at, updated_at)
            VALUES($id, $name, $kind, $enabled, $credential, $payload, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                kind = excluded.kind,
                enabled = excluded.enabled,
                credential_reference = excluded.credential_reference,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", provider.Id);
        command.Parameters.AddWithValue("$name", provider.Name);
        command.Parameters.AddWithValue("$kind", (int)provider.Kind);
        command.Parameters.AddWithValue("$enabled", provider.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$credential", provider.CredentialReference ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(provider, JsonOptions));
        command.Parameters.AddWithValue("$created", provider.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", provider.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM providers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProviderConfiguration Deserialize(string json) =>
        JsonSerializer.Deserialize<ProviderConfiguration>(json, JsonOptions)
        ?? throw new InvalidDataException("Stored provider JSON is invalid.");
}
