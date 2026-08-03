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

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await repository.ListAsync());
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
    public async Task Default_profile_cannot_be_deleted()
    {
        var repository = new InMemoryProfileRepository();
        var service = new ProfileService(repository, new ProfileValidator(), new FixedClock());
        var profile = await service.EnsureDefaultAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(profile.Id));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class InMemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, Profile> _profiles = [];

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
    }
}
