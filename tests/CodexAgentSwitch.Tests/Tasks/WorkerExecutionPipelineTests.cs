using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class WorkerExecutionPipelineTests
{
    [Fact]
    public async Task External_profile_snapshot_is_immutable_and_executes_deepseek_without_native_luna()
    {
        var now = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        var provider = new ProviderConfiguration(
            "deepseek-default",
            "DeepSeek",
            ProviderKind.DeepSeek,
            new Uri("https://api.deepseek.com"),
            "provider/deepseek-default",
            DeepSeekV4Catalog.FlashModelId,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(60),
            true,
            new ProviderPricing(1, 2, "CNY", null),
            now,
            now);
        var providers = new MemoryProviderRepository(provider);
        var profile = new Profile(
            Guid.NewGuid(),
            "Sol + DeepSeek",
            new AgentSelection("gpt-5.6-sol", "high"),
            new WorkerPolicy(true, WorkerSource.ExternalProvider, provider.Id, null, 1, RoutingMode.Balanced, FallbackAction.StopDelegation),
            new BudgetLimits(3, null, null, null, null, "CNY"),
            true,
            now,
            now,
            null)
        {
            ApprovalMode = ExecutionApprovalMode.FullAuto,
            ExternalWorkerPermission = ExternalWorkerPermissionMode.ReadOnly,
        };
        var snapshot = await new TaskProfileSnapshotFactory(providers, new FixedClock(now)).CaptureAsync(profile);

        await providers.UpsertAsync(provider with
        {
            Name = "后来被修改的名称",
            ModelId = DeepSeekV4Catalog.ProModelId,
            IsEnabled = false,
        });

        var native = new RecordingAdapter("native-codex");
        var external = new RecordingAdapter("external:deepseek-default", provider.Id, provider.Name);
        var factory = new RecordingExternalFactory(external);
        var orchestrator = new WorkerOrchestrator(factory, new FakeRuntime(native), new ExternalProviderResolver());
        var task = CreateTask();

        var result = await orchestrator.ExecuteAsync(snapshot, task);

        Assert.Equal("DeepSeek", snapshot.Provider?.Name);
        Assert.Equal(DeepSeekV4Catalog.FlashModelId, snapshot.Provider?.ModelId);
        Assert.Equal(DeepSeekV4Catalog.FlashModelId, result.ModelId);
        Assert.Equal("deepseek-default", result.ProviderId);
        Assert.Equal(1, external.SpawnCount);
        Assert.Equal(0, native.SpawnCount);
        Assert.Equal(DeepSeekV4Catalog.FlashModelId, external.LastTask?.ModelId);
        Assert.Equal("deepseek-default", factory.LastProvider?.Id);
        Assert.Equal(new Uri("https://api.deepseek.com"), factory.LastProvider?.BaseUri);
        Assert.Equal(ExecutionApprovalMode.FullAuto, external.LastTask?.ApprovalMode);
        Assert.Equal(ExternalWorkerPermissionMode.ReadOnly, external.LastTask?.ExternalWorkerPermission);
    }

    [Fact]
    public async Task Invalid_native_worker_id_fails_instead_of_silently_using_luna()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TaskProfileSnapshot(
            Guid.NewGuid(),
            "bad native",
            new AgentSelection("gpt-5.6-sol", "high"),
            new WorkerPolicy(true, WorkerSource.NativeCodex, "unknown-native", null, 1, RoutingMode.Balanced, FallbackAction.StopDelegation),
            new BudgetLimits(null, null, null, null, null, "CNY"),
            null,
            now);
        var native = new RecordingAdapter("native-codex");
        var orchestrator = new WorkerOrchestrator(new RejectingExternalFactory(), new FakeRuntime(native), new ExternalProviderResolver());

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(snapshot, CreateTask()));
        Assert.Equal(0, native.SpawnCount);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("ultra")]
    public async Task Native_worker_preserves_live_reasoning_effort_through_execution(string effort)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TaskProfileSnapshot(
            Guid.NewGuid(),
            "Astra worker",
            new AgentSelection(NativeCodexRoleCatalog.Astra.ModelId, "high"),
            new WorkerPolicy(true, WorkerSource.NativeCodex, NativeCodexRoleCatalog.Astra.WorkerId, null, 1, RoutingMode.Balanced, FallbackAction.StopDelegation, effort),
            new BudgetLimits(null, null, null, null, null, "CNY"),
            null,
            now);
        var native = new RecordingAdapter("native-codex");
        var orchestrator = new WorkerOrchestrator(new RejectingExternalFactory(), new FakeRuntime(native), new ExternalProviderResolver());

        var result = await orchestrator.ExecuteAsync(snapshot, CreateTask());

        Assert.Equal(NativeCodexRoleCatalog.Astra.ModelId, native.LastTask?.ModelId);
        Assert.Equal(effort, native.LastTask?.ReasoningEffort);
        Assert.Equal(effort, result.ReasoningEffort);
    }

    [Fact]
    public async Task External_worker_missing_coding_capabilities_is_rejected_before_spawn()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new TaskProviderSnapshot(
            "text-only",
            "Text Only",
            ProviderKind.OpenAiCompatible,
            new Uri("https://provider.test/v1"),
            "credential-ref",
            "text-model",
            TimeSpan.FromSeconds(30),
            true,
            null);
        var snapshot = new TaskProfileSnapshot(
            Guid.NewGuid(),
            "external",
            new AgentSelection("gpt-5.6-sol", "high"),
            new WorkerPolicy(true, WorkerSource.ExternalProvider, provider.Id, null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            new BudgetLimits(null, null, null, null, null, "CNY"),
            provider,
            now);
        var external = new RecordingAdapter(
            "external:text-only",
            provider.Id,
            provider.Name,
            new HashSet<WorkerToolCapability> { WorkerToolCapability.Text, WorkerToolCapability.ProjectRead });
        var orchestrator = new WorkerOrchestrator(
            new RecordingExternalFactory(external),
            new FakeRuntime(new RecordingAdapter("native-codex")),
            new ExternalProviderResolver());
        var task = CreateTask() with
        {
            Scope = new WorkerScope(
                ["src/Feature.cs"],
                [],
                [ScopeOperation.Read, ScopeOperation.Modify, ScopeOperation.Execute, ScopeOperation.Test]),
            AllowedWriteScope = ["src/Feature.cs"],
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(snapshot, task));

        Assert.Contains(nameof(WorkerToolCapability.Patch), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkerToolCapability.Shell), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkerToolCapability.BuildAndTest), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkerToolCapability.MultiTurn), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkerToolCapability.SelfRepair), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, external.SpawnCount);
    }

    private static WorkerTask CreateTask() => new(
        "group",
        "group-W1",
        "test",
        "return a verifiable result",
        Environment.CurrentDirectory,
        "pending",
        "medium",
        new WorkerScope([], [], [ScopeOperation.Read]),
        ["result"],
        ["completed"],
        []);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class MemoryProviderRepository(ProviderConfiguration initial) : IProviderRepository
    {
        private ProviderConfiguration provider = initial;

        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderConfiguration>>([provider]);

        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderConfiguration?>(string.Equals(id, provider.Id, StringComparison.Ordinal) ? provider : null);

        public Task UpsertAsync(ProviderConfiguration value, CancellationToken cancellationToken = default)
        {
            provider = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRuntime(IWorkerAdapter native) : IControlledTaskRuntime
    {
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IMainAgentSession MainAgent => null!;

        public IWorkerAdapter NativeWorker => native;
    }

    private sealed class RecordingExternalFactory(RecordingAdapter adapter) : IExternalWorkerAdapterFactory
    {
        public ProviderConfiguration? LastProvider { get; private set; }

        public IWorkerAdapter Create(ProviderConfiguration provider)
        {
            LastProvider = provider;
            return adapter;
        }
    }

    private sealed class RejectingExternalFactory : IExternalWorkerAdapterFactory
    {
        public IWorkerAdapter Create(ProviderConfiguration provider) => throw new InvalidOperationException("External factory must not be called.");
    }

    private sealed class RecordingAdapter(
        string adapterId,
        string? providerId = null,
        string? providerName = null,
        IReadOnlySet<WorkerToolCapability>? toolCapabilities = null) : IWorkerAdapter
    {
        public int SpawnCount { get; private set; }

        public WorkerTask? LastTask { get; private set; }

        public string AdapterId => adapterId;

        public IReadOnlySet<WorkerToolCapability> ToolCapabilities { get; } = toolCapabilities ?? new HashSet<WorkerToolCapability>();

        public Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerCapabilities(adapterId, true, [], 1, []) { ToolCapabilities = ToolCapabilities });

        public Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default)
        {
            SpawnCount++;
            LastTask = task;
            return Task.FromResult(new WorkerJob(adapterId, "job", "thread", "turn", task.TaskId, WorkerJobStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed"));
        }

        public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerJob(adapterId, jobId, "thread", "turn", LastTask!.TaskId, WorkerJobStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed"));

        public Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkerResult?>(new WorkerResult(
                LastTask!.TaskId,
                WorkerJobStatus.Completed,
                "worker result",
                null,
                [],
                [],
                providerId,
                providerName,
                providerId is null ? null : new Uri("https://api.deepseek.com/chat/completions"),
                LastTask.ModelId,
                new ProviderUsage(2, 3, 5)));

        public Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
