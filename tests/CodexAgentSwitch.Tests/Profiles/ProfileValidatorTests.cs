using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class ProfileValidatorTests
{
    private readonly DateTimeOffset _now = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_profile_is_valid()
    {
        var result = new ProfileValidator().Validate(Profile.CreateDefault(_now));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Disabled_worker_requires_zero_workers()
    {
        var profile = Profile.CreateDefault(_now) with
        {
            WorkerPolicy = Profile.CreateDefault(_now).WorkerPolicy with { Enabled = false },
        };

        var result = new ProfileValidator().Validate(profile);

        Assert.Contains(result.Issues, issue => issue.Code == "profile.workers.disabled_count");
    }

    [Fact]
    public void External_worker_requires_provider()
    {
        var profile = Profile.CreateDefault(_now) with
        {
            WorkerPolicy = Profile.CreateDefault(_now).WorkerPolicy with
            {
                Source = WorkerSource.ExternalProvider,
                PreferredProviderId = null,
            },
        };

        var result = new ProfileValidator().Validate(profile);

        Assert.Contains(result.Issues, issue => issue.Code == "profile.provider.required");
    }
}
