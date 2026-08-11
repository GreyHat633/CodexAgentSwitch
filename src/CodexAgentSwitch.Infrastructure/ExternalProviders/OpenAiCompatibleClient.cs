using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.ExternalAgents;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed record ExternalCompletion(
    string Content,
    string? ResponseModel,
    ProviderUsage? Usage,
    Uri RequestUri,
    JsonElement RawResponse);

public sealed class ProviderRequestException(
    ProviderErrorKind kind,
    string message,
    HttpStatusCode? statusCode = null,
    TimeSpan? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ProviderErrorKind Kind { get; } = kind;

    public HttpStatusCode? StatusCode { get; } = statusCode;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public sealed class OpenAiCompatibleClient(
    HttpClient httpClient,
    ICredentialStore credentialStore,
    IOpenCodeProcessRunner? openCodeProcessRunner = null) : IExternalProviderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProviderConnectionResult> TestConnectionAsync(
        ProviderConfiguration provider,
        CancellationToken cancellationToken = default)
    {
        if (provider.Kind == ProviderKind.OpenCodeZen)
        {
            var zenStopwatch = Stopwatch.StartNew();
            try
            {
                if (openCodeProcessRunner is null)
                {
                    return new ProviderConnectionResult(false, ProviderErrorKind.ServiceUnavailable,
                        "OpenCode CLI 登录探测未配置。", zenStopwatch.Elapsed, null, null, [], true);
                }

                var probe = await openCodeProcessRunner.ProbeAsync(Environment.CurrentDirectory, cancellationToken);
                if (!probe.IsAvailable || !probe.IsAuthenticated)
                {
                    return new ProviderConnectionResult(false, ProviderErrorKind.Authentication,
                        probe.Message, zenStopwatch.Elapsed, null, null, [], true);
                }

                var zenModels = (await ListModelsAsync(provider, cancellationToken)).ToArray();
                if (string.IsNullOrWhiteSpace(provider.ModelId))
                {
                    return new ProviderConnectionResult(false, ProviderErrorKind.InvalidConfiguration,
                        "OpenCode Zen 尚未选择模型；请刷新模型并选择一个。", zenStopwatch.Elapsed,
                        null, null, zenModels, true);
                }

                if (!zenModels.Contains(provider.ModelId, StringComparer.Ordinal))
                {
                    return new ProviderConnectionResult(false, ProviderErrorKind.ModelUnavailable,
                        $"OpenCode Zen 模型“{provider.ModelId}”未出现在官方目录中。", zenStopwatch.Elapsed,
                        null, null, zenModels, true);
                }

                return new ProviderConnectionResult(true, ProviderErrorKind.None,
                    "OpenCode Zen 模型发现成功；后续请求将由 OpenCode CLI 执行。", zenStopwatch.Elapsed,
                    provider.ModelId, null, zenModels, true);
            }
            catch (ProviderRequestException exception)
            {
                return new ProviderConnectionResult(false, exception.Kind, exception.Message, zenStopwatch.Elapsed,
                    null, null, [], true);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var models = Array.Empty<string>();
        var modelDiscoverySupported = true;
        try
        {
            try
            {
                models = NormalizeModels(provider, await ListModelsAsync(provider, cancellationToken));
                if (provider.Kind == ProviderKind.DeepSeek && models.Length == 0)
                {
                    models = [.. DeepSeekV4Catalog.FallbackModelIds];
                }
            }
            catch (ProviderRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                modelDiscoverySupported = false;
                models = provider.Kind == ProviderKind.DeepSeek
                    ? [.. DeepSeekV4Catalog.FallbackModelIds]
                    : [];
            }

            var modelId = string.IsNullOrWhiteSpace(provider.ModelId) ? models.FirstOrDefault() : provider.ModelId;
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return new ProviderConnectionResult(
                    false,
                    ProviderErrorKind.InvalidConfiguration,
                    modelDiscoverySupported ? "Provider 未返回可用模型，请手动填写 Model ID。" : "Provider 不支持模型列表，请手动填写 Model ID。",
                    stopwatch.Elapsed,
                    null,
                    null,
                    models,
                    modelDiscoverySupported);
            }

            var completion = await CompleteAsync(provider, modelId, "Reply with exactly OK.", cancellationToken);
            return new ProviderConnectionResult(
                true,
                ProviderErrorKind.None,
                "Provider 连接和最小请求测试成功。",
                stopwatch.Elapsed,
                completion.ResponseModel,
                completion.Usage,
                models,
                modelDiscoverySupported);
        }
        catch (ProviderRequestException exception)
        {
            return new ProviderConnectionResult(
                false,
                exception.Kind,
                exception.Message,
                stopwatch.Elapsed,
                null,
                null,
                models,
                modelDiscoverySupported);
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        ProviderConfiguration provider,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(provider, HttpMethod.Get, "models", cancellationToken);
        using var response = await SendAsync(provider, request, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderRequestException(ProviderErrorKind.Protocol, "Provider 的模型列表响应缺少 data 数组。", response.StatusCode);
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ExternalCompletion> CompleteAsync(
        ProviderConfiguration provider,
        string modelId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                model = modelId,
                messages = new[] { new { role = "user", content = prompt } },
                stream = false,
            },
            JsonOptions);
        using var request = await CreateRequestAsync(provider, HttpMethod.Post, "chat/completions", cancellationToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var requestUri = request.RequestUri
            ?? throw new ProviderRequestException(ProviderErrorKind.InvalidConfiguration, "Provider 请求地址无效。");
        using var response = await SendAsync(provider, request, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var content = root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString()
                : null;
        if (content is null)
        {
            throw new ProviderRequestException(ProviderErrorKind.Protocol, "Provider 响应缺少 choices[0].message.content。", response.StatusCode);
        }

        var responseModel = root.TryGetProperty("model", out var model) ? model.GetString() : null;
        var usage = ParseUsage(root);
        return new ExternalCompletion(content, responseModel, usage, requestUri, root.Clone());
    }

    /// <summary>Executes one structured chat-completion turn, optionally exposing function tools.</summary>
    public Task<ExternalAgentCompletion> CompleteAsync(
        ProviderConfiguration provider,
        string modelId,
        IReadOnlyList<ExternalAgentMessage> messages,
        IReadOnlyList<ExternalAgentToolDefinition>? tools = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(provider, new ExternalAgentChatRequest(modelId, messages, tools), cancellationToken);

    public Task<ExternalAgentCompletion> CompleteAsync(
        ProviderConfiguration provider,
        string modelId,
        IReadOnlyList<ExternalAgentMessage> messages,
        CancellationToken cancellationToken) =>
        CompleteAsync(provider, modelId, messages, null, cancellationToken);

    public Task<ExternalAgentCompletion> CompleteWithToolsAsync(
        ProviderConfiguration provider,
        string modelId,
        IReadOnlyList<ExternalAgentMessage> messages,
        IReadOnlyList<ExternalAgentToolDefinition>? tools = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(provider, modelId, messages, tools, cancellationToken);

    public async Task<ExternalAgentCompletion> CompleteAsync(
        ProviderConfiguration provider,
        ExternalAgentChatRequest requestModel,
        CancellationToken cancellationToken = default)
    {
        var payload = OpenAiCompatibleToolCallCodec.SerializeRequest(requestModel);
        using var request = await CreateRequestAsync(provider, HttpMethod.Post, "chat/completions", cancellationToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var requestUri = request.RequestUri
            ?? throw new ProviderRequestException(ProviderErrorKind.InvalidConfiguration, "Provider 请求 URI 无效。");
        using var response = await SendAsync(provider, request, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        try
        {
            return OpenAiCompatibleToolCallCodec.ParseResponse(document.RootElement, requestUri);
        }
        catch (OpenAiCompatibleProtocolException exception)
        {
            throw new ProviderRequestException(ProviderErrorKind.Protocol, exception.Message, response.StatusCode, innerException: exception);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        ProviderConfiguration provider,
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (provider.BaseUri is null)
        {
            throw new ProviderRequestException(ProviderErrorKind.InvalidConfiguration, "Provider Base URL 未配置。");
        }

        if (provider.Kind != ProviderKind.OpenCodeZen && string.IsNullOrWhiteSpace(provider.CredentialReference))
        {
            throw new ProviderRequestException(ProviderErrorKind.InvalidConfiguration, "Provider API Key 引用未配置。");
        }

        var secret = provider.Kind == ProviderKind.OpenCodeZen
            ? null
            : await credentialStore.ReadAsync(provider.CredentialReference!, cancellationToken);
        if (provider.Kind != ProviderKind.OpenCodeZen && string.IsNullOrWhiteSpace(secret))
        {
            throw new ProviderRequestException(ProviderErrorKind.Authentication, "Windows Credential Manager 中找不到 Provider API Key。");
        }

        var request = new HttpRequestMessage(method, Endpoint(provider.BaseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        foreach (var header in provider.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Dispose();
                throw new ProviderRequestException(ProviderErrorKind.InvalidConfiguration, $"Provider Header 无效：{header.Key}。");
            }
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        ProviderConfiguration provider,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(provider.Timeout);
        try
        {
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrorKind.Authentication,
                HttpStatusCode.TooManyRequests => ProviderErrorKind.RateLimited,
                HttpStatusCode.NotFound => ProviderErrorKind.ModelUnavailable,
                >= HttpStatusCode.InternalServerError => ProviderErrorKind.ServiceUnavailable,
                _ => ProviderErrorKind.Protocol,
            };
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var message = kind switch
            {
                ProviderErrorKind.Authentication => "Provider 拒绝认证，请检查 API Key。",
                ProviderErrorKind.RateLimited => retryAfter is null ? "Provider 已限流（HTTP 429）。" : $"Provider 已限流（HTTP 429），建议 {retryAfter.Value.TotalSeconds:0} 秒后重试。",
                ProviderErrorKind.ModelUnavailable => "Provider 端点或 Model ID 不可用（HTTP 404）。",
                ProviderErrorKind.ServiceUnavailable => $"Provider 服务暂时不可用（HTTP {(int)response.StatusCode}）。",
                _ => $"Provider 请求失败（HTTP {(int)response.StatusCode}）。",
            };
            response.Dispose();
            throw new ProviderRequestException(kind, message, response.StatusCode, retryAfter);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderRequestException(ProviderErrorKind.Timeout, $"Provider 请求超过 {provider.Timeout.TotalSeconds:0} 秒。", innerException: exception);
        }
        catch (OperationCanceledException)
        {
            throw new ProviderRequestException(ProviderErrorKind.Cancelled, "Provider 请求已取消。");
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderRequestException(ProviderErrorKind.ServiceUnavailable, "无法连接 Provider。", innerException: exception);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ProviderRequestException(ProviderErrorKind.Protocol, "Provider 返回的不是有效 JSON。", response.StatusCode, innerException: exception);
        }
    }

    private static ProviderUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ProviderUsage(
            ReadInt64(usage, "prompt_tokens", "input_tokens"),
            ReadInt64(usage, "completion_tokens", "output_tokens"),
            ReadInt64(usage, "total_tokens"));
    }

    private static long? ReadInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static Uri Endpoint(Uri baseUri, string relativePath)
    {
        var baseText = baseUri.AbsoluteUri.TrimEnd('/');
        return new Uri($"{baseText}/{relativePath.TrimStart('/')}", UriKind.Absolute);
    }

    private static string[] NormalizeModels(ProviderConfiguration provider, IReadOnlyList<string> models) =>
        provider.Kind == ProviderKind.DeepSeek
            ? [.. DeepSeekV4Catalog.FilterToV4(models)]
            : [.. models.Distinct(StringComparer.Ordinal)];
}
