using System.Net;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.ExternalProviders;

namespace CodexAgentSwitch.Tests.ExternalProviders;

public sealed class OpenAiCompatibleClientTests
{
    private const string Secret = "test-secret-that-must-not-leak";

    [Fact]
    public async Task Connection_test_discovers_model_and_parses_usage()
    {
        var calls = new List<HttpRequestMessage>();
        var handler = new StubHandler((request, _) =>
        {
            calls.Add(CloneMetadata(request));
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"deepseek-v4-flash\"}]}")
                : Json(HttpStatusCode.OK, "{\"model\":\"deepseek-v4-flash\",\"choices\":[{\"message\":{\"content\":\"OK\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1,\"total_tokens\":4}}"));
        });
        var client = Client(handler);

        var result = await client.TestConnectionAsync(Provider());

        Assert.True(result.Succeeded);
        Assert.Equal(["deepseek-v4-flash"], result.Models);
        Assert.Equal(3, result.Usage?.InputTokens);
        Assert.Equal(1, result.Usage?.OutputTokens);
        Assert.Equal(4, result.Usage?.TotalTokens);
        Assert.Equal(2, handler.CallCount);
        Assert.All(calls, request => Assert.Equal(Secret, request.Headers.Authorization?.Parameter));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(Provider()));
        Assert.DoesNotContain(Secret, result.Message);
    }

    [Fact]
    public async Task Unsupported_model_discovery_uses_manual_model()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, "{\"model\":\"manual-model\",\"choices\":[{\"message\":{\"content\":\"OK\"}}]}")));

        var result = await Client(handler).TestConnectionAsync(Provider(modelId: "manual-model"));

        Assert.True(result.Succeeded);
        Assert.False(result.ModelDiscoverySupported);
        Assert.Equal("manual-model", result.ResponseModel);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Connection_test_sends_the_selected_model_not_the_first_discovered_model()
    {
        string? completionPayload = null;
        var handler = new StubHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"deepseek-v4-flash\"},{\"id\":\"deepseek-v4-pro\"}]}");
            }

            completionPayload = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"model\":\"deepseek-v4-pro\",\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
        });

        var result = await Client(handler).TestConnectionAsync(Provider(modelId: "deepseek-v4-pro"));

        Assert.True(result.Succeeded);
        Assert.Contains("\"model\":\"deepseek-v4-pro\"", completionPayload);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderErrorKind.ServiceUnavailable)]
    public async Task Provider_errors_are_typed_and_never_retried(HttpStatusCode status, ProviderErrorKind expected)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(status, "{\"error\":\"ignored\"}")));

        var result = await Client(handler).TestConnectionAsync(Provider());

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.ErrorKind);
        Assert.Equal(1, handler.CallCount);
        Assert.DoesNotContain(Secret, result.Message);
    }

    [Fact]
    public async Task Timeout_is_reported_without_retry()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        });

        var result = await Client(handler).TestConnectionAsync(Provider(timeout: TimeSpan.FromMilliseconds(30)));

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderErrorKind.Timeout, result.ErrorKind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Worker_adapter_maps_completion_and_can_be_deleted_after_terminal_result()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"deepseek-v4-flash\"}]}")
                : Json(HttpStatusCode.OK, "{\"model\":\"deepseek-v4-flash\",\"choices\":[{\"message\":{\"content\":\"worker-result\"}}],\"usage\":{\"total_tokens\":8}}")));
        var provider = Provider() with { IsEnabled = true };
        await using var adapter = new OpenAiCompatibleWorkerAdapter(provider, Client(handler), new FakeClock());
        var task = new WorkerTask(
            "group-1",
            "group-1-L1",
            "test",
            "Return a result.",
            Environment.CurrentDirectory,
            "deepseek-v4-flash",
            "none",
            new WorkerScope([], [], [ScopeOperation.Read]),
            ["result"],
            ["has result"],
            []);

        var job = await adapter.SpawnAsync(task);
        var result = await adapter.WaitAsync(job.JobId, TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.Equal(WorkerJobStatus.Completed, result.Status);
        Assert.Equal("worker-result", result.Summary);
        Assert.Equal("deepseek-test", result.ProviderId);
        Assert.Equal("DeepSeek Test", result.ProviderName);
        Assert.Equal(new Uri("https://api.deepseek.test/v1/chat/completions"), result.RequestUri);
        Assert.Equal("deepseek-v4-flash", result.ResponseModelId);
        Assert.Equal(8, result.Usage?.TotalTokens);
        await adapter.DeleteAsync(job.JobId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => adapter.ReadStatusAsync(job.JobId));
    }

    [Fact]
    public async Task Pro_is_rejected_by_the_current_worker_protocol()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(
            Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"deepseek-v4-pro\"}]}")));
        var provider = Provider(modelId: "deepseek-v4-pro") with { IsEnabled = true };
        await using var adapter = new OpenAiCompatibleWorkerAdapter(provider, Client(handler), new FakeClock());

        var capabilities = await adapter.GetCapabilitiesAsync();

        Assert.False(capabilities.IsAvailable);
        Assert.Contains(DeepSeekV4Catalog.UnsupportedWorkerReason, capabilities.Warnings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SpawnAsync(new WorkerTask(
            "group-1",
            "group-1-L1",
            "test",
            "Return a result.",
            Environment.CurrentDirectory,
            "deepseek-v4-pro",
            "high",
            new WorkerScope([], [], [ScopeOperation.Read]),
            ["result"],
            ["has result"],
            [])));
    }

    [Fact]
    public async Task Validator_rejects_remote_http_reserved_headers_and_missing_credential()
    {
        var credentials = new FakeCredentialStore(null);
        var validator = new ProviderConfigurationValidator(credentials);
        var provider = Provider() with
        {
            BaseUri = new Uri("http://example.test/v1"),
            Headers = new Dictionary<string, string> { ["Authorization"] = "override" },
            CredentialReference = "missing",
        };

        var result = await validator.ValidateAsync(provider);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);
    }

    private static OpenAiCompatibleClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new FakeCredentialStore(Secret));

    private static ProviderConfiguration Provider(string? modelId = null, TimeSpan? timeout = null) => new(
        "deepseek-test",
        "DeepSeek Test",
        ProviderKind.DeepSeek,
        new Uri("https://api.deepseek.test/v1"),
        "credential-ref",
        modelId,
        new Dictionary<string, string>(),
        timeout ?? TimeSpan.FromSeconds(5),
        true,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpRequestMessage CloneMetadata(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        clone.Headers.Authorization = source.Headers.Authorization;
        return clone;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return response(request, cancellationToken);
        }
    }

    private sealed class FakeCredentialStore(string? secret) : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(secret is not null);

        public Task SaveAsync(string referenceId, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(secret);

        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
