using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class ProfileMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Current_schema_is_v5()
    {
        Assert.Equal(5, Profile.CurrentSchemaVersion);
    }

    [Fact]
    public void V4_profile_migrates_to_native_default_and_is_idempotent()
    {
        var legacy = Profile.CreateDefault(Now) with
        {
            SchemaVersion = 4,
        };

        var first = ProfileDataMigration.Migrate(legacy, Now.AddMinutes(1));
        var second = ProfileDataMigration.Migrate(first.Profile, Now.AddMinutes(2));

        Assert.True(first.Changed);
        Assert.Equal(5, first.Profile.SchemaVersion);
        Assert.Null(first.Profile.AutoCompactTokenLimit);
        Assert.False(second.Changed);
        Assert.Equal(first.Profile, second.Profile);
    }

    [Fact]
    public void Unsupported_persisted_threshold_normalizes_to_native_default()
    {
        var malformed = Profile.CreateDefault(Now) with
        {
            AutoCompactTokenLimit = 175_000,
        };

        var result = ProfileDataMigration.Migrate(malformed, Now.AddMinutes(1));

        Assert.True(result.Changed);
        Assert.Null(result.Profile.AutoCompactTokenLimit);
        Assert.False(result.Profile.RequiresRepair);
    }

    [Theory]
    [InlineData(150_000)]
    [InlineData(180_000)]
    [InlineData(200_000)]
    public void Supported_threshold_survives_migration(int limit)
    {
        var profile = Profile.CreateDefault(Now) with
        {
            SchemaVersion = 4,
            AutoCompactTokenLimit = limit,
        };

        var result = ProfileDataMigration.Migrate(profile, Now.AddMinutes(1));

        Assert.Equal(limit, result.Profile.AutoCompactTokenLimit);
        Assert.Equal(5, result.Profile.SchemaVersion);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("ultra")]
    public void Astra_profile_and_live_reasoning_effort_survive_migration(string effort)
    {
        var profile = Profile.CreateDefault(Now) with
        {
            SchemaVersion = 4,
            MainAgent = new AgentSelection("gpt-6-astra", effort),
            WorkerPolicy = Profile.CreateDefault(Now).WorkerPolicy with
            {
                PreferredProviderId = "native-astra",
                ReasoningEffort = effort,
            },
        };

        var result = ProfileDataMigration.Migrate(profile, Now.AddMinutes(1));

        Assert.Equal("gpt-6-astra", result.Profile.MainAgent.ModelId);
        Assert.Equal(effort, result.Profile.MainAgent.ReasoningEffort);
        Assert.Equal("native-astra", result.Profile.WorkerPolicy.PreferredProviderId);
        Assert.Equal(effort, result.Profile.WorkerPolicy.ReasoningEffort);
    }

    [Fact]
    public void Unknown_nonempty_model_and_worker_are_preserved_for_explicit_repair()
    {
        var profile = Profile.CreateDefault(Now) with
        {
            SchemaVersion = 4,
            MainAgent = new AgentSelection("future-model", "future-effort"),
            WorkerPolicy = Profile.CreateDefault(Now).WorkerPolicy with { PreferredProviderId = "native-future" },
        };

        var result = ProfileDataMigration.Migrate(profile, Now.AddMinutes(1));

        Assert.Equal("future-model", result.Profile.MainAgent.ModelId);
        Assert.Equal("future-effort", result.Profile.MainAgent.ReasoningEffort);
        Assert.Equal("native-future", result.Profile.WorkerPolicy.PreferredProviderId);
    }
}
