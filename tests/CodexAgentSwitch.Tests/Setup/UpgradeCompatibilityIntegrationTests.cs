using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Credentials;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.Setup;

public sealed class UpgradeCompatibilityIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Installed_upgrade_preserves_profile_identity_and_credential_references()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_UPGRADE_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var sourcePath = RequiredPath("CAS_UPGRADE_SOURCE_DATABASE");
        var upgradedPath = RequiredPath("CAS_UPGRADE_DATABASE");
        Assert.StartsWith("E:\\", sourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("E:\\", upgradedPath, StringComparison.OrdinalIgnoreCase);

        var source = new SqliteDatabase(sourcePath);
        var upgraded = new SqliteDatabase(upgradedPath);
        var sourceProfiles = await new SqliteProfileRepository(source).ListAsync();
        var upgradedProfiles = await new SqliteProfileRepository(upgraded).ListAsync();
        Assert.Equal(
            sourceProfiles.Select(profile => (profile.Id, profile.Name)).OrderBy(item => item.Id).ToArray(),
            upgradedProfiles.Select(profile => (profile.Id, profile.Name)).OrderBy(item => item.Id).ToArray());

        var sourceProvider = await new SqliteProviderRepository(source).GetAsync("deepseek-default");
        var upgradedProvider = await new SqliteProviderRepository(upgraded).GetAsync("deepseek-default");
        Assert.NotNull(sourceProvider);
        Assert.NotNull(upgradedProvider);
        Assert.Equal(sourceProvider!.CredentialReference, upgradedProvider!.CredentialReference);
        Assert.Equal(ProviderKind.DeepSeek, upgradedProvider.Kind);

        if (!string.IsNullOrWhiteSpace(upgradedProvider.CredentialReference))
        {
            var credentials = new WindowsCredentialStore();
            Assert.True(
                await credentials.ExistsAsync(upgradedProvider.CredentialReference),
                "升级后 Credential Manager 中的 Provider 凭据引用不可用。");
        }
    }

    private static string RequiredPath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"{variable} is required.");
        return Path.GetFullPath(value);
    }
}
