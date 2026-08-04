using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class ProfileServiceTests
{
    [Fact]
    public async Task Ensure_default_creates_exactly_one_profile()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());

        var first = await service.EnsureDefaultAsync();
        var second = await service.EnsureDefaultAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task Create_uses_a_new_id_and_keeps_the_current_profile()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var current = await service.EnsureDefaultAsync();

        var created = await service.CreateAsync(current! with { Name = "用户方案" });

        Assert.NotEqual(current!.Id, created.Id);
        Assert.True(current.IsDefault);
        Assert.False(created.IsDefault);
        Assert.Equal(2, (await repository.ListAsync()).Count);
    }

    [Fact]
    public async Task Save_rejects_duplicate_names_case_insensitively()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var current = await service.EnsureDefaultAsync();

        Assert.NotNull(current);
        await Assert.ThrowsAsync<ProfileValidationException>(() => service.CreateAsync(current! with { Name = current.Name.ToUpperInvariant() }));
    }

    [Fact]
    public async Task Set_default_switches_without_deleting_the_previous_profile()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var current = await service.EnsureDefaultAsync();
        var created = await service.CreateAsync(current! with { Name = "用户方案" });

        await service.SetDefaultAsync(created.Id);

        Assert.Equal(created.Id, (await repository.GetDefaultAsync())!.Id);
        Assert.False((await repository.GetAsync(current!.Id))!.IsDefault);
    }

    [Fact]
    public async Task Activate_sets_default_and_records_last_used_at()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var current = await service.EnsureDefaultAsync();
        var created = await service.CreateAsync(current! with { Name = "可启用方案" });

        var activated = await service.ActivateAsync(created.Id);

        Assert.True(activated.IsDefault);
        Assert.Equal(new FixedClock().UtcNow, activated.LastUsedAt);
        Assert.Equal(created.Id, (await repository.GetDefaultAsync())!.Id);
    }

    [Fact]
    public async Task Suggested_copy_names_remain_unique_after_multiple_copies()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var current = await service.EnsureDefaultAsync();

        var firstName = await service.SuggestUniqueNameAsync(current!.Name);
        await service.CreateAsync(current with { Name = firstName });
        var secondName = await service.SuggestUniqueNameAsync(current.Name);

        Assert.NotEqual(firstName, secondName);
        Assert.NotEqual(string.Empty, secondName);
    }

    [Fact]
    public void Export_contains_no_secret_fields()
    {
        var service = new ProfileService(new InMemoryProfileRepository(), new ProfileValidator(), new FixedClock());

        var json = service.Export(Profile.CreateDefault(new FixedClock().UtcNow));

        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_reads_legacy_profile_json_without_new_agent_slots()
    {
        var service = new ProfileService(new InMemoryProfileRepository(), new ProfileValidator(), new FixedClock());
        const string legacyJson = """
            {
              "version": 1,
              "profile": {
                "id": "00000000-0000-0000-0000-000000000001",
                "name": "legacy",
                "mainAgent": { "modelId": "sol", "reasoningEffort": "high" },
                "workerPolicy": { "enabled": false, "source": 0, "preferredProviderId": null, "fallbackProviderId": null, "maxWorkers": 0, "routingMode": 3, "fallbackAction": 1 },
                "budget": { "perTask": null, "daily": null, "monthly": null, "tokenLimit": null, "requestLimit": null, "currency": "CNY" },
                "isDefault": true,
                "createdAt": "2026-08-03T00:00:00+00:00",
                "updatedAt": "2026-08-03T00:00:00+00:00",
                "lastUsedAt": null
              }
            }
            """;

        var imported = service.Import(legacyJson);

        Assert.Equal("gpt-5.6-sol", imported.MainAgent.ModelId);
        Assert.Equal("high", imported.MainAgent.ReasoningEffort);
        Assert.False(imported.IsDefault);
        Assert.Equal(Profile.CurrentSchemaVersion, imported.SchemaVersion);
    }

    [Fact]
    public async Task Selected_main_agent_and_native_worker_selection_are_persisted()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var template = Profile.CreateDefault(new FixedClock().UtcNow) with
        {
            Name = "Terra 用户方案",
            MainAgent = new AgentSelection("gpt-5.6-terra", "xhigh"),
            WorkerPolicy = new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Balanced, FallbackAction.SingleAgent),
        };

        var saved = await service.CreateAsync(template);
        var reloaded = await repository.GetAsync(saved.Id);

        Assert.Equal("gpt-5.6-terra", reloaded!.MainAgent.ModelId);
        Assert.Equal("xhigh", reloaded.MainAgent.ReasoningEffort);
        Assert.Equal("native-luna", reloaded.WorkerPolicy.PreferredProviderId);
    }

    [Fact]
    public async Task Default_profile_cannot_be_deleted()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var profile = await service.EnsureDefaultAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(profile!.Id));
    }

    [Fact]
    public async Task Deleted_legacy_economic_profile_is_not_reinserted_after_restart()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var economic = (await service.EnsureDefaultAsync())!;
        var replacement = await service.CreateAsync(economic with { Name = "新的默认方案" });
        await service.SetDefaultAsync(replacement.Id);
        await service.DeleteAsync(economic.Id);

        var afterRestart = await service.EnsureDefaultAsync();

        Assert.NotNull(afterRestart);
        Assert.Equal(replacement.Id, afterRestart!.Id);
        Assert.DoesNotContain(await repository.ListAsync(), profile => profile.Name == "经济模式");
    }

    [Fact]
    public async Task Initialized_empty_profile_store_does_not_resurrect_the_economic_preset()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        _ = await service.EnsureDefaultAsync();
        repository.Clear();

        var afterRestart = await service.EnsureDefaultAsync();

        Assert.Null(afterRestart);
        Assert.Empty(await repository.ListAsync());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class InMemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, Profile> _profiles = [];
        private bool _initialized;

        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Profile>>(_profiles.Values.ToList());

        public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.GetValueOrDefault(id));

        public Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.Values.SingleOrDefault(profile => profile.IsDefault));

        public Task UpsertAsync(Profile profile, CancellationToken cancellationToken = default)
        {
            if (profile.IsDefault)
            {
                foreach (var current in _profiles.Values.Where(current => current.IsDefault).ToList())
                {
                    _profiles[current.Id] = current with { IsDefault = false };
                }
            }

            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _profiles.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> HasBeenInitializedAsync(CancellationToken cancellationToken = default) => Task.FromResult(_initialized);

        public Task MarkInitializedAsync(CancellationToken cancellationToken = default)
        {
            _initialized = true;
            return Task.CompletedTask;
        }

        public void Clear() => _profiles.Clear();
    }
}
