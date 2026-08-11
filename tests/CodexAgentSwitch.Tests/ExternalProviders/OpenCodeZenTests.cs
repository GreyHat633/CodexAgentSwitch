using System.Net;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.ExternalProviders;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.ExternalProviders;

public sealed class OpenCodeZenTests
{
    private const string Secret = "zen-test-secret";

    [Fact]
    public void Preset_uses_stable_reference_exact_endpoint_and_chat_allowlist()
    {
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow);

        Assert.Equal("provider/opencode-zen", provider.CredentialReference);
        Assert.Equal("https://opencode.ai/zen/v1", provider.BaseUri?.AbsoluteUri.TrimEnd('/'));
        Assert.All(OpenCodeZenCatalog.Models, model => Assert.True(model.Supports(ProviderProtocol.ChatCompletions)));
        Assert.DoesNotContain(OpenCodeZenCatalog.Models, model => model.Supports(ProviderProtocol.Responses));
    }

    [Fact]
    public async Task ListModels_uses_official_endpoint_and_filters_exact_allowlist()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://opencode.ai/zen/v1/models", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json("{\"data\":[{\"id\":\"deepseek-v4-flash\"},{\"id\":\"future-unknown\"},{\"id\":\"deepseek-v4-flash\"},{\"id\":\"kimi-k3\"}]}"));
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentials(Secret));

        var models = await client.ListModelsAsync(ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow));

        Assert.Equal(["deepseek-v4-flash", "kimi-k3"], models);
    }

    [Fact]
    public async Task TestConnection_uses_chat_completions_and_never_cli()
    {
        var paths = new List<string>();
        var handler = new Handler(async (request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                return Json("{\"data\":[{\"id\":\"kimi-k3\"}]}");
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Secret, request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"model\":\"kimi-k3\"", body);
            Assert.DoesNotContain(Secret, body);
            return Json("{\"model\":\"kimi-k3\",\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentials(Secret));
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with { ModelId = "kimi-k3" };

        var result = await client.TestConnectionAsync(provider);

        Assert.True(result.Succeeded);
        Assert.Equal(["/zen/v1/models", "/zen/v1/chat/completions"], paths);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(provider));
        Assert.DoesNotContain(Secret, result.Message);
    }

    [Fact]
    public async Task Missing_credential_fails_before_any_request()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("CLI must not be called"));
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentials(null));
        var result = await client.TestConnectionAsync(ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with { ModelId = "kimi-k3" });

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderErrorKind.Authentication, result.ErrorKind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Factory_routes_zen_through_openai_compatible_worker_adapter()
    {
        var client = new OpenAiCompatibleClient(new HttpClient(new Handler((_, _) => Task.FromResult(Json("{}")))), new FakeCredentials(Secret));
        var factory = new ExternalWorkerAdapterFactory(client, new FakeClock());
        var adapter = factory.Create(ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow));

        Assert.IsType<OpenAiCompatibleWorkerAdapter>(adapter);
    }

    [Fact]
    public async Task Capabilities_require_credential_even_when_catalog_is_public()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("{\"data\":[{\"id\":\"kimi-k3\"}]}")));
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentials(null));
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with
        {
            IsEnabled = true,
            ModelId = "kimi-k3",
        };
        await using var adapter = new OpenAiCompatibleWorkerAdapter(provider, client, new FakeClock());

        var capabilities = await adapter.GetCapabilitiesAsync();

        Assert.False(capabilities.IsAvailable);
        Assert.Contains(capabilities.Warnings, warning => warning.Contains("API Key", StringComparison.Ordinal));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Worker_uses_same_selected_provider_and_chat_completions_route()
    {
        var paths = new List<string>();
        var handler = new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Secret, request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("{\"model\":\"kimi-k3\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ZEN_OK\"}}],\"usage\":{\"total_tokens\":3}}"));
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new FakeCredentials(Secret));
        var factory = new ExternalWorkerAdapterFactory(client, new FakeClock());
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with
        {
            IsEnabled = true,
            ModelId = "kimi-k3",
        };
        await using var adapter = (OpenAiCompatibleWorkerAdapter)factory.Create(provider);
        var task = new WorkerTask(
            "zen-group",
            "zen-group-L1",
            "test",
            "Reply with ZEN_OK.",
            Environment.CurrentDirectory,
            "kimi-k3",
            "none",
            new WorkerScope([], [], [ScopeOperation.Read]),
            ["ZEN_OK"],
            ["completed"],
            []);

        var job = await adapter.SpawnAsync(task);
        var result = await adapter.WaitAsync(job.JobId, TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.Equal(WorkerJobStatus.Completed, result.Status);
        Assert.Equal("ZEN_OK", result.Summary);
        Assert.Equal("opencode-zen", result.ProviderId);
        Assert.Equal(new Uri("https://opencode.ai/zen/v1/chat/completions"), result.RequestUri);
        Assert.Equal(["/zen/v1/chat/completions"], paths);
    }

    [Fact]
    public async Task Sqlite_roundtrip_persists_selection_and_reference_without_secret()
    {
        var root = Environment.GetEnvironmentVariable("CAS_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;

        var path = Path.Combine(root, $"zen-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path);
        await database.InitializeAsync();
        var repository = new SqliteProviderRepository(database);
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with
        {
            ModelId = "kimi-k3",
            IsEnabled = true,
        };

        await repository.UpsertAsync(provider);
        var loaded = await repository.GetAsync(provider.Id);

        Assert.Equal("kimi-k3", loaded?.ModelId);
        Assert.Equal(OpenCodeZenCatalog.CredentialReference, loaded?.CredentialReference);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(loaded));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return callback(request, cancellationToken);
        }
    }

    private sealed class FakeCredentials(string? secret) : ICredentialStore
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
