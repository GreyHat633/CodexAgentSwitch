using System.Collections.Concurrent;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed class OpenAiCompatibleWorkerAdapter : IWorkerAdapter, IAsyncDisposable
{
    private readonly ProviderConfiguration provider;
    private readonly OpenAiCompatibleClient client;
    private readonly IClock clock;
    private readonly SemaphoreSlim concurrency = new(3, 3);
    private readonly ConcurrentDictionary<string, ExternalJobRuntime> jobs = new(StringComparer.Ordinal);

    public OpenAiCompatibleWorkerAdapter(
        ProviderConfiguration provider,
        OpenAiCompatibleClient client,
        IClock clock)
    {
        if (provider.Kind == ProviderKind.NativeCodex)
        {
            throw new ArgumentException("External adapter requires an external Provider.", nameof(provider));
        }

        this.provider = provider;
        this.client = client;
        this.clock = clock;
    }

    public string AdapterId => $"external:{provider.Id}";

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!provider.IsEnabled)
        {
            return new WorkerCapabilities(AdapterId, false, [], 3, ["Provider 已停用。"]);
        }

        try
        {
            var discovered = await client.ListModelsAsync(provider, cancellationToken);
            var models = discovered.Select(ToCapability).ToArray();
            if (models.Length == 0 && !string.IsNullOrWhiteSpace(provider.ModelId))
            {
                models = [ToCapability(provider.ModelId)];
            }

            return new WorkerCapabilities(AdapterId, true, models, 3, models.Length == 0 ? ["Provider 未返回模型，请手动配置 Model ID。"] : []);
        }
        catch (ProviderRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            var models = string.IsNullOrWhiteSpace(provider.ModelId) ? [] : new[] { ToCapability(provider.ModelId) };
            return new WorkerCapabilities(AdapterId, models.Length > 0, models, 3, ["Provider 不支持模型发现，使用手动 Model ID。"]);
        }
        catch (ProviderRequestException exception)
        {
            return new WorkerCapabilities(AdapterId, false, [], 3, [exception.Message]);
        }
    }

    public Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException("Provider 已停用，不能创建新 Worker。");
        }

        var modelId = string.IsNullOrWhiteSpace(task.ModelId) ? provider.ModelId : task.ModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("外部 Worker 必须指定 Model ID。");
        }

        var jobId = Guid.NewGuid().ToString("D");
        var job = new WorkerJob(
            AdapterId,
            jobId,
            $"external-{jobId}",
            $"request-{Guid.NewGuid():D}",
            task.TaskId,
            WorkerJobStatus.Starting,
            clock.UtcNow,
            null,
            null);
        var runtime = new ExternalJobRuntime(task, modelId, job);
        if (!jobs.TryAdd(jobId, runtime))
        {
            throw new InvalidOperationException("无法注册外部 Worker Job。");
        }

        _ = Task.Run(() => ExecuteAsync(runtime));
        return Task.FromResult(job);
    }

    public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Runtime(jobId).Job);
    }

    public async Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        if (wait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(wait));
        }

        var runtime = Runtime(jobId);
        if (runtime.Result is not null)
        {
            return runtime.Result;
        }

        try
        {
            return await runtime.Completion.Task.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Runtime(jobId);
        if (request.Kind == WorkerSteerKind.ContinueWaiting)
        {
            return Task.CompletedTask;
        }

        throw new NotSupportedException("外部 Chat Completions Worker 不支持 Turn 中途纠偏或审批；请取消后重新提交。");
    }

    public Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = Runtime(jobId);
        runtime.Cancellation.Cancel();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = Runtime(jobId);
        if (!IsTerminal(runtime.Job.Status))
        {
            throw new InvalidOperationException("运行中的外部 Worker 不能删除。");
        }

        if (jobs.TryRemove(jobId, out var removed))
        {
            removed.Cancellation.Dispose();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var runtime in jobs.Values)
        {
            runtime.Cancellation.Cancel();
            runtime.Cancellation.Dispose();
        }

        concurrency.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ExecuteAsync(ExternalJobRuntime runtime)
    {
        var entered = false;
        try
        {
            await concurrency.WaitAsync(runtime.Cancellation.Token);
            entered = true;
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Running, StatusMessage = "Provider request running." };
            var completion = await client.CompleteAsync(provider, runtime.ModelId, runtime.Task.Prompt, runtime.Cancellation.Token);
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Completed, CompletedAt = clock.UtcNow, StatusMessage = "completed" };
            Complete(
                runtime,
                new WorkerResult(
                    runtime.Task.TaskId,
                    WorkerJobStatus.Completed,
                    completion.Content,
                    completion.RawResponse,
                    completion.Usage is null ? ["Provider 未返回 Usage；费用只能标记为不可用或估算。"] : [],
                    []));
        }
        catch (ProviderRequestException exception)
        {
            var status = exception.Kind == ProviderErrorKind.Cancelled ? WorkerJobStatus.Interrupted : WorkerJobStatus.Failed;
            runtime.Job = runtime.Job with { Status = status, CompletedAt = clock.UtcNow, StatusMessage = exception.Message };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, status, exception.Message, null, [], [exception.Kind.ToString()]));
        }
        catch (OperationCanceledException)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Interrupted, CompletedAt = clock.UtcNow, StatusMessage = "Provider request cancelled." };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Interrupted, "Provider request cancelled.", null, [], []));
        }
        catch (Exception exception)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Failed, CompletedAt = clock.UtcNow, StatusMessage = "Unexpected Provider failure." };
            Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Failed, "Unexpected Provider failure.", null, [], [exception.GetType().Name]));
        }
        finally
        {
            if (entered)
            {
                concurrency.Release();
            }
        }
    }

    private static WorkerModelCapability ToCapability(string id) => new(id, id, [], "none", false);

    private ExternalJobRuntime Runtime(string jobId) => jobs.TryGetValue(jobId, out var runtime)
        ? runtime
        : throw new KeyNotFoundException($"External Worker job not found: {jobId}");

    private static bool IsTerminal(WorkerJobStatus status) => status is WorkerJobStatus.Completed or WorkerJobStatus.Failed or WorkerJobStatus.Interrupted or WorkerJobStatus.UnknownRecoverable or WorkerJobStatus.Deleted;

    private static void Complete(ExternalJobRuntime runtime, WorkerResult result)
    {
        runtime.Result = result;
        runtime.Completion.TrySetResult(result);
    }

    private sealed class ExternalJobRuntime(WorkerTask task, string modelId, WorkerJob job)
    {
        public WorkerTask Task { get; } = task;

        public string ModelId { get; } = modelId;

        public WorkerJob Job { get; set; } = job;

        public WorkerResult? Result { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<WorkerResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
