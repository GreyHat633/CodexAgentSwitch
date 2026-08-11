using System.Net;
using System.Text;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.ExternalProviders;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.ExternalProviders;

public sealed class OpenCodeZenTests
{
    [Fact]
    public void Auth_probe_rejects_unrelated_provider_and_accepts_zen_marker()
    {
        var empty = OpenCodeZenProcessRunner.ClassifyAuthResult(new OpenCodeProcessResult(
            0,
            "Credentials ~\\.local\\share\\opencode\\auth.json\n0 credentials",
            ""));
        var unrelated = OpenCodeZenProcessRunner.ClassifyAuthResult(new OpenCodeProcessResult(
            0,
            "Credentials ~\\.local\\share\\opencode\\auth.json\nGitHub: logged in",
            ""));
        var zen = OpenCodeZenProcessRunner.ClassifyAuthResult(new OpenCodeProcessResult(
            0,
            "Credentials ~\\.local\\share\\opencode\\auth.json\nOpenCode Zen: logged in",
            ""));

        Assert.False(empty.IsAuthenticated);
        Assert.Contains("未找到", empty.Message);
        Assert.False(unrelated.IsAuthenticated);
        Assert.True(zen.IsAuthenticated);
        Assert.Contains("已找到", zen.Message);

        var failed = OpenCodeZenProcessRunner.ClassifyAuthResult(new OpenCodeProcessResult(17, "", ""));
        Assert.Contains("退出码 17", failed.Message);
        Assert.Contains("'opencode auth login'", failed.Message);
    }

    [Fact]
    public async Task Discovery_uses_official_data_ids_without_credentials()
    {
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"zen-a\"},{\"id\":\"zen-a\"},{\"id\":\"zen-b\"}]}", Encoding.UTF8, "application/json"),
        }));
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new EmptyCredentials());
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow);

        var models = await client.ListModelsAsync(provider);

        Assert.Equal(["zen-a", "zen-b"], models);
        Assert.Null(handler.Last!.Headers.Authorization);
    }

    [Fact]
    public async Task Connection_test_probes_auth_before_catalog_without_model_request()
    {
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"zen-a\"}]}", Encoding.UTF8, "application/json"),
        }));
        var runner = new RecordingRunner();
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new EmptyCredentials(), runner);
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with { ModelId = "zen-a" };

        var result = await client.TestConnectionAsync(provider);

        Assert.True(result.Succeeded);
        Assert.True(runner.ProbeCalled);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Cli_adapter_prefixes_only_at_invocation_and_preserves_selection()
    {
        var runner = new RecordingRunner();
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with { IsEnabled = true, ModelId = "zen-a" };
        await using var adapter = new OpenCodeZenWorkerAdapter(provider, null!, runner, new Clock());
        var job = await adapter.SpawnAsync(new WorkerTask("g", "t", "obj", "hello", Environment.CurrentDirectory, "zen-a", "none", new WorkerScope([], [], [ScopeOperation.Read]), [], [], []));
        var result = await adapter.WaitAsync(job.JobId, TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.Equal("opencode/zen-a", runner.Model);
        Assert.Equal(Environment.CurrentDirectory, runner.WorkingDirectory);
        Assert.Equal("zen-a", result!.ResponseModelId);
    }

    [Fact]
    public async Task Capabilities_mark_disappeared_saved_model_unavailable_without_replacing_it()
    {
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"zen-b\"}]}", Encoding.UTF8, "application/json"),
        }));
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with { IsEnabled = true, ModelId = "zen-a" };
        var client = new OpenAiCompatibleClient(new HttpClient(handler), new EmptyCredentials());
        await using var adapter = new OpenCodeZenWorkerAdapter(provider, client, new RecordingRunner(), new Clock());

        var capabilities = await adapter.GetCapabilitiesAsync();

        Assert.False(capabilities.IsAvailable);
        Assert.Contains(capabilities.Warnings, warning => warning.Contains("zen-a", StringComparison.Ordinal));
        Assert.Equal("zen-a", provider.ModelId);
    }

    [Fact]
    public async Task Sqlite_roundtrip_preserves_raw_selected_model_id()
    {
        var root = Environment.GetEnvironmentVariable("CAS_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var path = Path.Combine(root!, $"zen-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path);
        await database.InitializeAsync();
        var repository = new SqliteProviderRepository(database);
        var provider = ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow) with { ModelId = "vendor/model.raw" };

        await repository.UpsertAsync(provider);
        var loaded = await repository.GetAsync(provider.Id);

        Assert.Equal("vendor/model.raw", loaded?.ModelId);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Last = request; CallCount++; return callback(request, cancellationToken); }
    }
    private sealed class EmptyCredentials : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveAsync(string referenceId, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class RecordingRunner : IOpenCodeProcessRunner
    {
        public string? Model { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public bool ProbeCalled { get; private set; }
        public Task<OpenCodeProbeResult> ProbeAsync(string workingDirectory, CancellationToken cancellationToken = default)
        { ProbeCalled = true; return Task.FromResult(new OpenCodeProbeResult(true, true, "ok")); }
        public Task<OpenCodeProcessResult> RunAsync(string workingDirectory, string model, string prompt, CancellationToken cancellationToken = default)
        { WorkingDirectory = workingDirectory; Model = model; return Task.FromResult(new OpenCodeProcessResult(0, "ok", "")); }
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
