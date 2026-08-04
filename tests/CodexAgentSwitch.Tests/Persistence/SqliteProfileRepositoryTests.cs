using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Tests.Persistence;

public sealed class SqliteProfileRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cas-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Round_trip_preserves_profile_and_default_uniqueness()
    {
        Directory.CreateDirectory(_directory);
        var database = new SqliteDatabase(Path.Combine(_directory, "test.db"));
        await database.InitializeAsync();
        var repository = new SqliteProfileRepository(database);
        var now = DateTimeOffset.UtcNow;
        var first = Profile.CreateDefault(now);
        var second = Profile.CreateDefault(now) with { Id = Guid.NewGuid(), Name = "第二方案" };

        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        var profiles = await repository.ListAsync();
        Assert.Equal(2, profiles.Count);
        Assert.Equal(second.Id, (await repository.GetDefaultAsync())!.Id);
        Assert.False((await repository.GetAsync(first.Id))!.IsDefault);
    }

    [Fact]
    public async Task A_new_repository_instance_reloads_edited_profile()
    {
        Directory.CreateDirectory(_directory);
        var database = new SqliteDatabase(Path.Combine(_directory, "restart.db"));
        await database.InitializeAsync();
        var repository = new SqliteProfileRepository(database);
        var profile = Profile.CreateDefault(DateTimeOffset.UtcNow);
        await repository.UpsertAsync(profile);
        await repository.UpsertAsync(profile with { Name = "edited" });

        var reloaded = await new SqliteProfileRepository(database).GetAsync(profile.Id);

        Assert.Equal("edited", reloaded!.Name);
        Assert.Equal(profile.Id, reloaded.Id);
    }

    [Fact]
    public async Task Round_trip_preserves_full_automatic_approval_mode()
    {
        Directory.CreateDirectory(_directory);
        var database = new SqliteDatabase(Path.Combine(_directory, "approval.db"));
        await database.InitializeAsync();
        var repository = new SqliteProfileRepository(database);
        var profile = Profile.CreateDefault(DateTimeOffset.UtcNow) with
        {
            ApprovalMode = ExecutionApprovalMode.FullAuto,
        };

        await repository.UpsertAsync(profile);

        var reloaded = await new SqliteProfileRepository(database).GetAsync(profile.Id);

        Assert.Equal(ExecutionApprovalMode.FullAuto, reloaded!.ApprovalMode);
    }

    [Fact]
    public async Task Legacy_economic_profile_is_classified_as_built_in_without_migration()
    {
        Directory.CreateDirectory(_directory);
        var database = new SqliteDatabase(Path.Combine(_directory, "legacy.db"));
        await database.InitializeAsync();
        var repository = new SqliteProfileRepository(database);
        var id = Guid.NewGuid();
        const string payload = """
            {
              "id": "00000000-0000-0000-0000-000000000002",
              "name": "经济模式",
              "mainAgent": { "modelId": "gpt-5.6-sol", "reasoningEffort": "high" },
              "workerPolicy": { "enabled": true, "source": 1, "preferredProviderId": "native-luna", "fallbackProviderId": null, "maxWorkers": 1, "routingMode": 0, "fallbackAction": 1 },
              "budget": { "perTask": 0.5, "daily": 3, "monthly": 30, "tokenLimit": null, "requestLimit": null, "currency": "CNY" },
              "isDefault": true,
              "createdAt": "2026-08-03T00:00:00+00:00",
              "updatedAt": "2026-08-03T00:00:00+00:00",
              "lastUsedAt": null
            }
            """;
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO profiles(id, name, is_default, payload_json, created_at, updated_at, last_used_at) VALUES($id, $name, 1, $payload, $created, $updated, NULL)";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$name", "经济模式");
            command.Parameters.AddWithValue("$payload", payload.Replace("00000000-0000-0000-0000-000000000002", id.ToString("D"), StringComparison.Ordinal));
            command.Parameters.AddWithValue("$created", "2026-08-03T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$updated", "2026-08-03T00:00:00.0000000+00:00");
            await command.ExecuteNonQueryAsync();
        }

        var reloaded = await repository.GetAsync(id);

        Assert.True(reloaded!.IsBuiltIn);
        Assert.Equal("内置预设", reloaded.KindLabel);
        Assert.Equal(ExecutionApprovalMode.Automatic, reloaded.ApprovalMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
