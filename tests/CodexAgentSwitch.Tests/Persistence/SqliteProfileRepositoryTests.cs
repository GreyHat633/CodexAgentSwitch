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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
