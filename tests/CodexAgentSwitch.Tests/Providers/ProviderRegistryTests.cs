using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Tests.Providers;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Zen_credential_policy_never_resolves_an_api_key_reference()
    {
        var zen = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow);
        var deepSeek = ProviderConfiguration.DeepSeekPreset(DateTimeOffset.UtcNow);

        Assert.False(ProviderCredentialPolicy.UsesApiKey(zen));
        Assert.Null(ProviderCredentialPolicy.ResolveReference(zen));
        Assert.True(ProviderCredentialPolicy.UsesApiKey(deepSeek));
        Assert.Equal("provider/deepseek-default", ProviderCredentialPolicy.ResolveReference(deepSeek));
    }

    [Fact]
    public async Task Zen_is_listed_before_enablement_and_requires_zen_auth_only()
    {
        var repository = new MemoryRepository([]);
        var credentials = new Credentials();
        var registry = new ProviderRegistry(repository, new FakeClient(null), credentials, new Clock(),
            _ => Task.FromResult(new ProviderAuthResult(true, false, "Run opencode auth login.")));

        var snapshot = await registry.LoadAsync();
        var zen = snapshot.Find("opencode-zen");

        Assert.NotNull(zen);
        Assert.False(zen.Provider.IsEnabled);
        Assert.Equal(ProviderAuthState.Missing, zen.AuthState);
        Assert.Empty(zen.Models);
        Assert.Equal(0, credentials.ExistsCalls);
        Assert.Contains("已停用", zen.Status);
    }

    [Fact]
    public async Task Zen_refresh_uses_any_discovered_model_and_probe_failures_are_unavailable()
    {
        var repository = new MemoryRepository([]);
        var registry = Create(repository, _ => Task.FromResult(new ProviderAuthResult(true, true, "ok")), (_, _) => Task.FromResult<IReadOnlyList<string>>(["vendor/zen-new"]));
        var refreshed = await registry.RefreshAsync("opencode-zen");

        Assert.Contains(refreshed.Models, model => model.Id == "vendor/zen-new");

        var failedProbe = Create(repository, _ => throw new InvalidOperationException("probe crashed"));
        var snapshot = await failedProbe.LoadAsync();
        var zen = snapshot.Find("opencode-zen");
        Assert.Equal(ProviderAuthState.Unavailable, zen?.AuthState);
        Assert.Contains("probe crashed", zen?.Status ?? string.Empty);
    }

    [Fact]
    public async Task Refresh_failure_keeps_saved_model_and_save_roundtrip_keeps_raw_id()
    {
        var now = DateTimeOffset.UtcNow;
        var saved = ProviderConfiguration.OpenCodeZenPreset(now) with { ModelId = "vendor/model.raw" };
        var repository = new MemoryRepository([saved]);
        var registry = Create(repository, _ => Task.FromResult(new ProviderAuthResult(true, true, "ok")), (_, _) => throw new InvalidOperationException("catalog unavailable"));

        var failed = await registry.RefreshAsync(saved.Id);
        Assert.True(failed.RefreshFailed);
        Assert.Equal("vendor/model.raw", failed.Provider.ModelId);

        var updated = await registry.SaveSelectionAsync(saved.Id, "vendor/model.next");
        Assert.Equal("vendor/model.next", (await repository.GetAsync(saved.Id))!.ModelId);
        Assert.Equal(updated.ModelId, (await repository.GetAsync(saved.Id))!.ModelId);
    }

    private static ProviderRegistry Create(
        MemoryRepository repository,
        Func<CancellationToken, Task<ProviderAuthResult>> auth,
        Func<ProviderConfiguration, CancellationToken, Task<IReadOnlyList<string>>>? models = null) =>
        new(repository, new FakeClient(models), new Credentials(), new Clock(), auth);

    private sealed class FakeClient(
        Func<ProviderConfiguration, CancellationToken, Task<IReadOnlyList<string>>>? models) : IExternalProviderClient
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            models is null ? Task.FromResult<IReadOnlyList<string>>(["zen-a"]) : models(provider, cancellationToken);

        public Task<ProviderConnectionResult> TestConnectionAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderConnectionResult(true, ProviderErrorKind.None, "ok", TimeSpan.Zero, provider.ModelId, null, [], true));
    }

    private sealed class MemoryRepository(IEnumerable<ProviderConfiguration> seed) : IProviderRepository
    {
        private readonly Dictionary<string, ProviderConfiguration> values = seed.ToDictionary(item => item.Id, StringComparer.Ordinal);
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderConfiguration>>(values.Values.ToArray());
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(values.GetValueOrDefault(id));
        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) { values[provider.Id] = provider; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) { values.Remove(id); return Task.CompletedTask; }
    }

    private sealed class Credentials : ICredentialStore
    {
        public int ExistsCalls { get; private set; }
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) { ExistsCalls++; return Task.FromResult(false); }
        public Task SaveAsync(string referenceId, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
