using CodexAgentSwitch.Infrastructure.Credentials;

namespace CodexAgentSwitch.Tests.Credentials;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public async Task Credential_round_trip_uses_reference_and_can_be_deleted()
    {
        var store = new WindowsCredentialStore();
        var reference = "integration." + Guid.NewGuid().ToString("N");
        const string secret = "test-only-secret";
        try
        {
            await store.SaveAsync(reference, secret);
            Assert.True(await store.ExistsAsync(reference));
            Assert.Equal(secret, await store.ReadAsync(reference));
        }
        finally
        {
            await store.DeleteAsync(reference);
        }

        Assert.False(await store.ExistsAsync(reference));
    }
}
