using System.Collections.Concurrent;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed class OpenCodeZenWorkerAdapter(
    ProviderConfiguration provider,
    OpenAiCompatibleClient catalogClient,
    IOpenCodeProcessRunner processRunner,
    IClock clock) : IWorkerAdapter, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Runtime> jobs = new(StringComparer.Ordinal);

    public string AdapterId => $"external:{provider.Id}";

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!provider.IsEnabled)
        {
            return new WorkerCapabilities(AdapterId, false, [], 1, ["OpenCode Zen provider is disabled."]);
        }

        try
        {
            var probe = await processRunner.ProbeAsync(Environment.CurrentDirectory, cancellationToken);
            if (!probe.IsAvailable || !probe.IsAuthenticated)
            {
                return new WorkerCapabilities(AdapterId, false, [], 1, [probe.Message])
                {
                    ToolCapabilities = ZenToolCapabilities,
                };
            }

            var models = await catalogClient.ListModelsAsync(provider, cancellationToken);
            var capabilities = models.Select(id => new WorkerModelCapability(id, id, [], "none", string.Equals(id, provider.ModelId, StringComparison.Ordinal))).ToArray();
            var missingSelection = string.IsNullOrWhiteSpace(provider.ModelId);
            var disappeared = !missingSelection && !models.Contains(provider.ModelId, StringComparer.Ordinal);
            var warnings = missingSelection
                ? new[] { "OpenCode Zen model selection is missing; refresh models and choose one." }
                : disappeared
                    ? new[] { $"Saved OpenCode Zen model '{provider.ModelId}' is no longer in the refreshed catalog; reselect a model." }
                : Array.Empty<string>();
            return new WorkerCapabilities(AdapterId, capabilities.Length > 0 && warnings.Length == 0, capabilities, 1, warnings)
            {
                ToolCapabilities = ZenToolCapabilities,
            };
        }
        catch (Exception exception)
        {
            return new WorkerCapabilities(AdapterId, false, [], 1, [exception.Message]);
        }
    }

    private static IReadOnlySet<WorkerToolCapability> ZenToolCapabilities { get; } = new HashSet<WorkerToolCapability>
    {
        WorkerToolCapability.Text,
        WorkerToolCapability.ProjectRead,
        WorkerToolCapability.Search,
        WorkerToolCapability.Patch,
        WorkerToolCapability.Shell,
        WorkerToolCapability.BuildAndTest,
        WorkerToolCapability.MultiTurn,
        WorkerToolCapability.SelfRepair,
    };

    public Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException("OpenCode Zen provider is disabled.");
        }

        var modelId = string.IsNullOrWhiteSpace(task.ModelId) ? provider.ModelId : task.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("OpenCode Zen model selection is missing; refresh models and choose one.");
        }

        var id = Guid.NewGuid().ToString("D");
        var job = new WorkerJob(AdapterId, id, $"external-{id}", $"request-{id}", task.TaskId,
            WorkerJobStatus.Starting, clock.UtcNow, null, null);
        var runtime = new Runtime(task, modelId, job);
        if (!jobs.TryAdd(id, runtime)) throw new InvalidOperationException("Unable to register OpenCode Zen worker job.");
        _ = Task.Run(() => ExecuteAsync(runtime));
        return Task.FromResult(job);
    }

    public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Get(jobId).Job);
    }

    public async Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        if (wait <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wait));
        var runtime = Get(jobId);
        if (runtime.Result is not null) return runtime.Result;
        try { return await runtime.Completion.Task.WaitAsync(wait, cancellationToken); }
        catch (TimeoutException) { return null; }
    }

    public Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Get(jobId);
        if (request.Kind == WorkerSteerKind.ContinueWaiting) return Task.CompletedTask;
        throw new NotSupportedException("OpenCode CLI workers do not support mid-turn steering.");
    }

    public Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Get(jobId).Cancellation.Cancel();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = Get(jobId);
        if (runtime.Job.Status is WorkerJobStatus.Starting or WorkerJobStatus.Running)
            throw new InvalidOperationException("Running OpenCode CLI workers cannot be deleted.");
        if (jobs.TryRemove(jobId, out var removed)) removed.Cancellation.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var runtime in jobs.Values) { runtime.Cancellation.Cancel(); runtime.Cancellation.Dispose(); }
        return ValueTask.CompletedTask;
    }

    private async Task ExecuteAsync(Runtime runtime)
    {
        try
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Running, StatusMessage = "OpenCode CLI request running." };
            var result = await processRunner.RunAsync(runtime.Task.WorkingDirectory,
                OpenCodeZenCatalog.InvocationModel(runtime.ModelId), runtime.Task.Prompt, runtime.Cancellation.Token);
            var status = result.ExitCode == 0 ? WorkerJobStatus.Completed : WorkerJobStatus.Failed;
            runtime.Job = runtime.Job with { Status = status, CompletedAt = clock.UtcNow, StatusMessage = result.ExitCode == 0 ? "completed" : result.StandardError };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, status, result.ExitCode == 0 ? result.StandardOutput : result.StandardError,
                JsonSerializer.SerializeToElement(new { result.ExitCode, result.StandardOutput, result.StandardError }),
                status == WorkerJobStatus.Completed ? [] : ["OpenCode CLI failed"],
                status == WorkerJobStatus.Completed ? [] : [result.StandardError], provider.Id, provider.Name,
                null, runtime.ModelId, null, status == WorkerJobStatus.Completed ? null : "ProcessFailed"));
        }
        catch (OperationCanceledException)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Interrupted, CompletedAt = clock.UtcNow, StatusMessage = "OpenCode CLI request cancelled." };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Interrupted, "OpenCode CLI request cancelled.", null, [], [], provider.Id, provider.Name, null, runtime.ModelId, null, "Cancelled"));
        }
        catch (Exception exception)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Failed, CompletedAt = clock.UtcNow, StatusMessage = exception.Message };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Failed, exception.Message, null, ["OpenCode CLI unavailable"], [exception.GetType().Name], provider.Id, provider.Name, null, runtime.ModelId, null, exception.GetType().Name));
        }
    }

    private Runtime Get(string id) => jobs.TryGetValue(id, out var runtime) ? runtime : throw new KeyNotFoundException($"OpenCode Zen worker job not found: {id}");

    private static void Complete(Runtime runtime, WorkerResult result) { runtime.Result = result; runtime.Completion.TrySetResult(result); }

    private sealed class Runtime(WorkerTask task, string modelId, WorkerJob job)
    {
        public WorkerTask Task { get; } = task;
        public string ModelId { get; } = modelId;
        public WorkerJob Job { get; set; } = job;
        public WorkerResult? Result { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<WorkerResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
