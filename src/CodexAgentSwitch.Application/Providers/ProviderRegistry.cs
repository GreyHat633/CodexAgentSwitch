using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Application.Providers;

public enum ProviderAuthState
{
    NotRequired,
    Authenticated,
    Missing,
    Unavailable,
}

public sealed record ProviderAuthResult(
    bool IsAvailable,
    bool IsAuthenticated,
    string Message);

public sealed record ProviderModelOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class ProviderCredentialPolicy
{
    public static bool UsesApiKey(ProviderConfiguration provider) => provider.Kind != ProviderKind.OpenCodeZen;

    public static string? ResolveReference(ProviderConfiguration provider) =>
        UsesApiKey(provider) ? provider.CredentialReference ?? $"provider/{provider.Id}" : null;
}

public sealed record ProviderRegistryEntry(
    ProviderConfiguration Provider,
    IReadOnlyList<ProviderModelOption> Models,
    ProviderAuthState AuthState,
    string Status,
    bool RefreshFailed = false,
    string? RefreshError = null)
{
    public string Id => Provider.Id;
    public string Name => Provider.Name;
    public ProviderKind Kind => Provider.Kind;
    public bool IsEnabled => Provider.IsEnabled;
    public string ProviderId => Provider.Id;
    public string? ModelId => Provider.ModelId;
    public bool IsAuthenticated => AuthState is ProviderAuthState.NotRequired or ProviderAuthState.Authenticated;
}

public sealed record ProviderRegistrySnapshot(IReadOnlyList<ProviderRegistryEntry> Providers)
{
    public ProviderRegistryEntry? Find(string providerId) =>
        Providers.FirstOrDefault(provider => string.Equals(provider.ProviderId, providerId, StringComparison.Ordinal));
}

public interface IProviderRegistry
{
    Task<ProviderRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task<ProviderRegistryEntry> RefreshAsync(string providerId, CancellationToken cancellationToken = default);

    Task<ProviderConfiguration> SaveSelectionAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Single read/write path for provider cards, onboarding and profile selection.
/// A failed catalog refresh never writes or replaces the persisted selection.
/// </summary>
public sealed class ProviderRegistry(
    IProviderRepository repository,
    IExternalProviderClient client,
    ICredentialStore credentials,
    IClock clock,
    Func<CancellationToken, Task<ProviderAuthResult>>? openCodeAuthProbe = null) : IProviderRegistry
{
    public async Task<ProviderRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var persisted = await repository.ListAsync(cancellationToken);
        var providers = new Dictionary<string, ProviderConfiguration>(StringComparer.Ordinal);
        foreach (var provider in persisted)
        {
            providers[provider.Id] = provider;
        }

        AddBuiltIn(providers, ProviderConfiguration.Native(clock.UtcNow));
        AddBuiltIn(providers, ProviderConfiguration.DeepSeekPreset(clock.UtcNow));
        AddBuiltIn(providers, ProviderConfiguration.OpenCodeZenPreset(clock.UtcNow));

        var entries = new List<ProviderRegistryEntry>(providers.Count);
        foreach (var provider in providers.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            entries.Add(await BuildEntryAsync(provider, cancellationToken));
        }

        return new ProviderRegistrySnapshot(entries);
    }

    public async Task<ProviderRegistryEntry> RefreshAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = await repository.GetAsync(providerId, cancellationToken)
            ?? BuiltIn(providerId);
        if (provider is null)
        {
            throw new KeyNotFoundException($"找不到服务商“{providerId}”。");
        }

        var auth = await ReadAuthAsync(provider, cancellationToken);
        if (provider.Kind == ProviderKind.OpenCodeZen && !auth.IsAuthenticated)
        {
            return BuildEntry(provider, [], auth, refreshFailed: true, auth.Message);
        }

        try
        {
            var models = provider.Kind switch
            {
                ProviderKind.NativeCodex => [],
                ProviderKind.DeepSeek => DeepSeekV4Catalog.FallbackModelIds,
                _ => await client.ListModelsAsync(provider, cancellationToken),
            };
            return BuildEntry(provider, models, auth, false, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return BuildEntry(provider, [], auth, true, exception.Message);
        }
    }

    public async Task<ProviderConfiguration> SaveSelectionAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("模型 ID 不能为空。", nameof(modelId));
        }

        var provider = await repository.GetAsync(providerId, cancellationToken)
            ?? BuiltIn(providerId)
            ?? throw new KeyNotFoundException($"找不到服务商“{providerId}”。");
        var updated = provider with
        {
            ModelId = modelId.Trim(),
            UpdatedAt = clock.UtcNow,
        };
        await repository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<ProviderRegistryEntry> BuildEntryAsync(ProviderConfiguration provider, CancellationToken cancellationToken)
    {
        var auth = await ReadAuthAsync(provider, cancellationToken);
        IReadOnlyList<string> models = provider.Kind switch
        {
            ProviderKind.NativeCodex => [],
            ProviderKind.DeepSeek => DeepSeekV4Catalog.FallbackModelIds,
            _ => provider.ModelId is null ? [] : [provider.ModelId],
        };
        return BuildEntry(provider, models, auth, false, null);
    }

    private async Task<ProviderAuthResult> ReadAuthAsync(ProviderConfiguration provider, CancellationToken cancellationToken)
    {
        if (provider.Kind == ProviderKind.NativeCodex)
        {
            return new(true, true, "Codex 桌面应用已完成身份验证。");
        }

        if (provider.Kind == ProviderKind.OpenCodeZen)
        {
            if (openCodeAuthProbe is null)
            {
                return new(false, false, "无法使用 OpenCode CLI 登录探测；请运行 'opencode auth login'。");
            }

            try
            {
                return await openCodeAuthProbe(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(false, false, "OpenCode CLI 登录探测超时；请重试或运行 'opencode auth login'。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new(false, false, $"OpenCode CLI 登录探测失败：{exception.Message}");
            }
        }

        var configured = !string.IsNullOrWhiteSpace(provider.CredentialReference)
            && await credentials.ExistsAsync(provider.CredentialReference!, cancellationToken);
        return configured
            ? new(true, true, "服务商凭据已配置。")
            : new(true, false, "服务商 API 密钥尚未配置。");
    }

    private static ProviderRegistryEntry BuildEntry(
        ProviderConfiguration provider,
        IReadOnlyList<string> models,
        ProviderAuthResult auth,
        bool refreshFailed,
        string? refreshError)
    {
        var modelIds = models;
        if (refreshFailed && !string.IsNullOrWhiteSpace(provider.ModelId)
            && !modelIds.Contains(provider.ModelId, StringComparer.Ordinal))
        {
            modelIds = modelIds.Append(provider.ModelId).ToArray();
        }

        var options = modelIds
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.Ordinal)
            .Select(model => new ProviderModelOption(
                model,
                provider.Kind == ProviderKind.DeepSeek && DeepSeekV4Catalog.TryGet(model, out var definition)
                    ? definition.DisplayName
                    : model))
            .ToArray();
        var state = !auth.IsAvailable
            ? ProviderAuthState.Unavailable
            : auth.IsAuthenticated ? (provider.Kind == ProviderKind.NativeCodex ? ProviderAuthState.NotRequired : ProviderAuthState.Authenticated) : ProviderAuthState.Missing;
        var disabledPrefix = provider.IsEnabled ? string.Empty : "已停用 · ";
        var status = refreshFailed
            ? $"模型刷新失败：{refreshError ?? auth.Message}"
            : state is ProviderAuthState.Missing or ProviderAuthState.Unavailable
                ? disabledPrefix + auth.Message
                : provider.IsEnabled ? "已启用" : "已停用";
        return new ProviderRegistryEntry(provider, options, state, status, refreshFailed, refreshError);
    }

    private static void AddBuiltIn(IDictionary<string, ProviderConfiguration> providers, ProviderConfiguration provider)
    {
        if (!providers.ContainsKey(provider.Id)) providers[provider.Id] = provider;
    }

    private static ProviderConfiguration? BuiltIn(string providerId) => providerId switch
    {
        "native-codex" => ProviderConfiguration.Native(DateTimeOffset.UtcNow),
        "deepseek-default" => ProviderConfiguration.DeepSeekPreset(DateTimeOffset.UtcNow),
        "opencode-zen" => ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow),
        _ => null,
    };
}
