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
            return new WorkerCapabilities(AdapterId, false, [], 1, ["OpenCode Zen 服务商已停用。"]);
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
                ? new[] { "OpenCode Zen 尚未选择模型；请刷新模型并选择一个。" }
                : disappeared
                    ? new[] { $"已保存的 OpenCode Zen 模型“{provider.ModelId}”不在刷新后的目录中；请重新选择模型。" }
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
            throw new InvalidOperationException("OpenCode Zen 服务商已停用。");
        }

        var modelId = string.IsNullOrWhiteSpace(task.ModelId) ? provider.ModelId : task.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("OpenCode Zen 尚未选择模型；请刷新模型并选择一个。");
        }

        var id = Guid.NewGuid().ToString("D");
        var job = new WorkerJob(AdapterId, id, $"external-{id}", $"request-{id}", task.TaskId,
            WorkerJobStatus.Starting, clock.UtcNow, null, null);
        var runtime = new Runtime(task, modelId, job);
        if (!jobs.TryAdd(id, runtime)) throw new InvalidOperationException("无法登记 OpenCode Zen Worker 任务。");
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
        throw new NotSupportedException("OpenCode CLI Worker 不支持回合中途转向。");
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
            throw new InvalidOperationException("运行中的 OpenCode CLI Worker 不能删除。");
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
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Running, StatusMessage = "OpenCode CLI 请求运行中。" };
            var result = await processRunner.RunAsync(runtime.Task.WorkingDirectory,
                OpenCodeZenCatalog.InvocationModel(runtime.ModelId), runtime.Task.Prompt, runtime.Cancellation.Token);
            var status = result.ExitCode == 0 ? WorkerJobStatus.Completed : WorkerJobStatus.Failed;
            runtime.Job = runtime.Job with { Status = status, CompletedAt = clock.UtcNow, StatusMessage = result.ExitCode == 0 ? "已完成" : result.StandardError };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, status, result.ExitCode == 0 ? result.StandardOutput : result.StandardError,
                JsonSerializer.SerializeToElement(new { result.ExitCode, result.StandardOutput, result.StandardError }),
                status == WorkerJobStatus.Completed ? [] : ["OpenCode CLI 执行失败"],
                status == WorkerJobStatus.Completed ? [] : [result.StandardError], provider.Id, provider.Name,
                null, runtime.ModelId, null, status == WorkerJobStatus.Completed ? null : "ProcessFailed"));
        }
        catch (OperationCanceledException)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Interrupted, CompletedAt = clock.UtcNow, StatusMessage = "OpenCode CLI 请求已取消。" };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Interrupted, "OpenCode CLI 请求已取消。", null, [], [], provider.Id, provider.Name, null, runtime.ModelId, null, "Cancelled"));
        }
        catch (Exception exception)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Failed, CompletedAt = clock.UtcNow, StatusMessage = exception.Message };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Failed, exception.Message, null, ["OpenCode CLI 不可用"], [exception.GetType().Name], provider.Id, provider.Name, null, runtime.ModelId, null, exception.GetType().Name));
        }
    }

    private Runtime Get(string id) => jobs.TryGetValue(id, out var runtime) ? runtime : throw new KeyNotFoundException($"找不到 OpenCode Zen Worker 任务：{id}");

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
