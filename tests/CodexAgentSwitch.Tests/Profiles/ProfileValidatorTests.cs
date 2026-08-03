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

    [Fact]
    public void Duplicate_names_are_rejected_except_for_the_same_profile()
    {
        var validator = new ProfileValidator();
        var first = Profile.CreateDefault(_now);
        var duplicate = first with { Id = Guid.NewGuid(), Name = first.Name.ToUpperInvariant() };

        var duplicateResult = validator.ValidateUniqueName(duplicate, [first]);
        var sameResult = validator.ValidateUniqueName(first, [first]);

        Assert.Contains(duplicateResult.Issues, issue => issue.Code == "profile.name.duplicate");
        Assert.DoesNotContain(sameResult.Issues, issue => issue.Code == "profile.name.duplicate");
    }

    [Fact]
    public void Disabled_worker_cannot_persist_a_selectable_source()
    {
        var profile = Profile.CreateDefault(_now) with
        {
            WorkerPolicy = Profile.CreateDefault(_now).WorkerPolicy with
            {
                Enabled = false,
                Source = WorkerSource.NativeCodex,
                MaxWorkers = 0,
            },
        };

        var result = new ProfileValidator().Validate(profile);

        Assert.Contains(result.Issues, issue => issue.Code == "profile.workers.disabled_count");
    }
}
