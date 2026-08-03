namespace CodexAgentSwitch.Domain.Providers;

public enum ProviderKind
{
    NativeCodex,
    DeepSeek,
    OpenAiCompatible,
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
        new Uri("https://api.deepseek.com"),
        null,
        null,
        new Dictionary<string, string>(),
        TimeSpan.FromSeconds(60),
        false,
        null,
        now,
        now);
}
