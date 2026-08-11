using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Application.Providers;

public sealed record ProviderValidationReport(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}
public sealed class ProviderConfigurationValidator(ICredentialStore credentialStore)
{
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Host",
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
    };

    public async Task<ProviderValidationReport> ValidateAsync(
        ProviderConfiguration provider,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (provider.Kind == ProviderKind.NativeCodex)
        {
            return new ProviderValidationReport([], []);
        }

        if (provider.BaseUri is null || !provider.BaseUri.IsAbsoluteUri)
        {
            errors.Add("Provider Base URL 必须是绝对 URL。");
        }
        else if (provider.BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            if (provider.BaseUri.Scheme == Uri.UriSchemeHttp && provider.BaseUri.IsLoopback)
            {
                warnings.Add("本机 HTTP Provider 未加密，仅应用于受信任的开发服务。");
            }
            else
            {
                errors.Add("外部 Provider 必须使用 HTTPS；仅允许 localhost 使用 HTTP。");
            }
        }

        if (provider.Kind == ProviderKind.DeepSeek
            && provider.BaseUri is not null
            && !string.Equals(provider.BaseUri.AbsoluteUri.TrimEnd('/'), DeepSeekV4Catalog.BaseUrl, StringComparison.Ordinal))
        {
            errors.Add($"DeepSeek Base URL must be exactly {DeepSeekV4Catalog.BaseUrl}.");
        }

        if (provider.Kind == ProviderKind.OpenCodeZen
            && provider.BaseUri is not null
            && !string.Equals(provider.BaseUri.AbsoluteUri.TrimEnd('/'), OpenCodeZenCatalog.BaseUrl, StringComparison.Ordinal))
        {
            errors.Add($"OpenCode Zen Base URL must be exactly {OpenCodeZenCatalog.BaseUrl}.");
        }

        if (provider.Timeout < TimeSpan.FromSeconds(2) || provider.Timeout > TimeSpan.FromMinutes(10))
        {
            errors.Add("请求超时必须在 2 秒到 10 分钟之间。");
        }

        foreach (var header in provider.Headers)
        {
            if (ReservedHeaders.Contains(header.Key))
            {
                errors.Add($"自定义 Header 不得覆盖 {header.Key}。");
            }

            if (header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value.Contains('\r') || header.Value.Contains('\n'))
            {
                errors.Add("自定义 Header 不得包含换行符。");
            }
        }

        if (provider.Kind != ProviderKind.OpenCodeZen
            && (string.IsNullOrWhiteSpace(provider.CredentialReference)
                || !await credentialStore.ExistsAsync(provider.CredentialReference!, cancellationToken)))
        {
            errors.Add("API Key 尚未保存到 Windows Credential Manager。");
        }

        if (provider.Kind == ProviderKind.DeepSeek
            && !string.IsNullOrWhiteSpace(provider.ModelId)
            && !DeepSeekV4Catalog.TryGet(provider.ModelId, out _))
        {
            errors.Add("DeepSeek Provider only supports the DeepSeek V4 Flash and Pro catalog.");
        }

        if (string.IsNullOrWhiteSpace(provider.ModelId))
        {
            warnings.Add("未指定 Model ID；连接测试发现模型后仍需手动选择。");
        }

        if (provider.Kind == ProviderKind.OpenCodeZen && string.IsNullOrWhiteSpace(provider.ModelId))
        {
            warnings.Add("OpenCode Zen model selection is missing; refresh models and choose one before running a Worker.");
        }

        return new ProviderValidationReport(errors, warnings);
    }
}
