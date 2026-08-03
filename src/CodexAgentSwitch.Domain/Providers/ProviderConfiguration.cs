using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Domain.Providers;

public enum ProviderKind
{
    NativeCodex,
    DeepSeek,
    OpenAiCompatible,
}

public enum ProviderProtocol
{
    ChatCompletions,
    Responses,
    CodexWorker,
}

public sealed record ProviderModelDefinition(
    string Id,
    string DisplayName,
    IReadOnlySet<ProviderProtocol> SupportedProtocols,
    string? WorkerUnavailableReason = null)
{
    public bool Supports(ProviderProtocol protocol) => SupportedProtocols.Contains(protocol);
}

public static class DeepSeekV4Catalog
{
    public const string BaseUrl = "https://api.deepseek.com";
    public const string FlashModelId = "deepseek-v4-flash";
    public const string ProModelId = "deepseek-v4-pro";
    public const string UnsupportedWorkerReason = "不支持当前Worker协议";

    public static IReadOnlyList<ProviderModelDefinition> Models { get; } =
    [
        new(
            FlashModelId,
            "DeepSeek V4 Flash 0731",
            new HashSet<ProviderProtocol>
            {
                ProviderProtocol.ChatCompletions,
                ProviderProtocol.Responses,
                ProviderProtocol.CodexWorker,
            }),
        new(
            ProModelId,
            "DeepSeek V4 Pro",
            new HashSet<ProviderProtocol> { ProviderProtocol.ChatCompletions },
            UnsupportedWorkerReason),
    ];

    public static IReadOnlyList<string> FallbackModelIds { get; } = Models.Select(model => model.Id).ToArray();

    public static bool TryGet(string? modelId, out ProviderModelDefinition definition)
    {
        definition = Models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal))!;
        return definition is not null;
    }

    public static ProviderModelDefinition Get(string modelId) =>
        TryGet(modelId, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown DeepSeek V4 model: {modelId}", nameof(modelId));

    public static IReadOnlyList<string> FilterToV4(IEnumerable<string> modelIds) =>
        modelIds
            .Where(modelId => Models.Any(model => string.Equals(model.Id, modelId, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

public sealed record DeepSeekModelMigrationResult(
    string? ModelId,
    bool Changed,
    bool PreserveThinkingIntent);

public static class DeepSeekV4Migration
{
    public static DeepSeekModelMigrationResult MigrateModel(string? modelId) =>
        modelId switch
        {
            "deepseek-chat" => new(DeepSeekV4Catalog.FlashModelId, true, false),
            "deepseek-reasoner" => new(DeepSeekV4Catalog.FlashModelId, true, true),
            _ => new(modelId, false, false),
        };

    public static ProviderConfiguration Migrate(ProviderConfiguration provider)
    {
        if (provider.Kind != ProviderKind.DeepSeek)
        {
            return provider;
        }

        var migration = MigrateModel(provider.ModelId);
        if (!migration.Changed)
        {
            return provider;
        }

        return provider with { ModelId = migration.ModelId };
    }

    public static Profile Migrate(Profile profile)
    {
        var migration = MigrateModel(profile.MainAgent.ModelId);
        return migration.Changed
            ? profile with { MainAgent = profile.MainAgent with { ModelId = migration.ModelId! } }
            : profile;
    }
}

public sealed record ProviderPricing(
    decimal? InputPerMillionTokens,
    decimal? OutputPerMillionTokens,
    string Currency,
    DateOnly? UpdatedOn);

public enum ProviderErrorKind
{
    None,
    InvalidConfiguration,
    Authentication,
    RateLimited,
    Timeout,
    ModelUnavailable,
    ServiceUnavailable,
    Protocol,
    Cancelled,
}

public sealed record ProviderUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);

public sealed record ProviderConnectionResult(
    bool Succeeded,
    ProviderErrorKind ErrorKind,
    string Message,
    TimeSpan Latency,
    string? ResponseModel,
    ProviderUsage? Usage,
    IReadOnlyList<string> Models,
    bool ModelDiscoverySupported);

public sealed record ProviderConfiguration(
    string Id,
    string Name,
    ProviderKind Kind,
    Uri? BaseUri,
    string? CredentialReference,
    string? ModelId,
    IReadOnlyDictionary<string, string> Headers,
    TimeSpan Timeout,
    bool IsEnabled,
    ProviderPricing? Pricing,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ProviderConfiguration Native(DateTimeOffset now) => new(
        "native-codex",
        "原生 Codex",
        ProviderKind.NativeCodex,
        null,
        null,
        null,
        new Dictionary<string, string>(),
        TimeSpan.FromSeconds(30),
        true,
        null,
        now,
        now);

    public static ProviderConfiguration DeepSeekPreset(DateTimeOffset now) => new(
        "deepseek-default",
        "DeepSeek",
        ProviderKind.DeepSeek,
        new Uri(DeepSeekV4Catalog.BaseUrl),
        null,
        DeepSeekV4Catalog.FlashModelId,
        new Dictionary<string, string>(),
        TimeSpan.FromSeconds(60),
        false,
        null,
        now,
        now);
}
