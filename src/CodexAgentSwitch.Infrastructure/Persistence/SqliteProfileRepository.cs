using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

public sealed class SqliteProfileRepository(SqliteDatabase database) : IProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<Profile>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json, is_default FROM profiles ORDER BY is_default DESC, name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(Deserialize(reader.GetString(0), reader.GetInt64(1) == 1));
        }

        return profiles;
    }

    public async Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json, is_default FROM profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Deserialize(reader.GetString(0), reader.GetInt64(1) == 1)
            : null;
    }

    public async Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM profiles WHERE is_default = 1 LIMIT 1";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize(json, isDefault: true) : null;
    }

    public async Task UpsertAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (profile.IsDefault)
        {
            await using var clearDefault = connection.CreateCommand();
            clearDefault.Transaction = (SqliteTransaction)transaction;
            clearDefault.CommandText = "UPDATE profiles SET is_default = 0";
            await clearDefault.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO profiles(id, name, is_default, payload_json, created_at, updated_at, last_used_at)
            VALUES($id, $name, $default, $payload, $created, $updated, $lastUsed)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                is_default = excluded.is_default,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at,
                last_used_at = excluded.last_used_at
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$default", profile.IsDefault ? 1 : 0);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(profile, JsonOptions));
        command.Parameters.AddWithValue("$created", profile.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", profile.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$lastUsed", profile.LastUsedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profiles WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Profile Deserialize(string json, bool isDefault)
    {
        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions)
            ?? throw new InvalidDataException("Stored profile JSON is invalid.");
        return profile with { IsDefault = isDefault };
    }
}
