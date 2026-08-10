using System.Collections.Concurrent;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.ExternalAgents;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed class OpenAiCompatibleWorkerAdapter : IWorkerAdapter, IAsyncDisposable
{
    private static readonly IReadOnlySet<WorkerToolCapability> TextOnlyCapabilities = new HashSet<WorkerToolCapability>
    {
        WorkerToolCapability.Text,
    };
    private static readonly IReadOnlySet<WorkerToolCapability> ExternalAgentCapabilities = new HashSet<WorkerToolCapability>
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
    private readonly ProviderConfiguration provider;
    private readonly OpenAiCompatibleClient client;
    private readonly IClock clock;
    private readonly OpenAiCompatibleExternalAgentRuntime? agentRuntime;
    private readonly SemaphoreSlim concurrency = new(3, 3);
    private readonly ConcurrentDictionary<string, ExternalJobRuntime> jobs = new(StringComparer.Ordinal);

    public OpenAiCompatibleWorkerAdapter(
        ProviderConfiguration provider,
        OpenAiCompatibleClient client,
        IClock clock,
        OpenAiCompatibleExternalAgentRuntime? agentRuntime = null)
    {
        if (provider.Kind == ProviderKind.NativeCodex)
        {
            throw new ArgumentException("External adapter requires an external Provider.", nameof(provider));
        }

        this.provider = provider;
        this.client = client;
        this.clock = clock;
        this.agentRuntime = agentRuntime;
    }

    public string AdapterId => $"external:{provider.Id}";

    public IReadOnlySet<WorkerToolCapability> ToolCapabilities => agentRuntime is null
        ? TextOnlyCapabilities
        : ExternalAgentCapabilities;

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (!provider.IsEnabled)
        {
            return WithToolCapabilities(new WorkerCapabilities(AdapterId, false, [], 3, ["Provider 已停用。"]));
        }

        try
        {
            var discovered = await client.ListModelsAsync(provider, cancellationToken);
            if (provider.Kind == ProviderKind.DeepSeek)
            {
                discovered = DeepSeekV4Catalog.FilterToV4(discovered);
                if (discovered.Count == 0)
                {
                    discovered = DeepSeekV4Catalog.FallbackModelIds;
                }
            }

            var models = discovered.Select(ToCapability).ToArray();
            if (models.Length == 0 && !string.IsNullOrWhiteSpace(provider.ModelId))
            {
                models = [ToCapability(provider.ModelId)];
            }

            if (provider.Kind == ProviderKind.DeepSeek
                && provider.ModelId is not null
                && DeepSeekV4Catalog.TryGet(provider.ModelId, out var selected)
                && !selected.Supports(ProviderProtocol.CodexWorker))
            {
                return WithToolCapabilities(new WorkerCapabilities(AdapterId, false, models, 3, [selected.WorkerUnavailableReason ?? DeepSeekV4Catalog.UnsupportedWorkerReason]));
            }

            return WithToolCapabilities(new WorkerCapabilities(AdapterId, true, models, 3, models.Length == 0 ? ["Provider 未返回模型，请手动配置 Model ID。"] : []));
        }
        catch (ProviderRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            var models = provider.Kind == ProviderKind.DeepSeek
                ? DeepSeekV4Catalog.Models.Select(model => ToCapability(model.Id)).ToArray()
                : string.IsNullOrWhiteSpace(provider.ModelId) ? [] : new[] { ToCapability(provider.ModelId) };
            if (provider.Kind == ProviderKind.DeepSeek
                && provider.ModelId is not null
                && DeepSeekV4Catalog.TryGet(provider.ModelId, out var selected)
                && !selected.Supports(ProviderProtocol.CodexWorker))
            {
                return WithToolCapabilities(new WorkerCapabilities(AdapterId, false, models, 3, [selected.WorkerUnavailableReason ?? DeepSeekV4Catalog.UnsupportedWorkerReason]));
            }
            return WithToolCapabilities(new WorkerCapabilities(AdapterId, models.Length > 0, models, 3, ["Provider 不支持模型发现，使用手动 Model ID。"]));
        }
        catch (ProviderRequestException exception)
        {
            return WithToolCapabilities(new WorkerCapabilities(AdapterId, false, [], 3, [exception.Message]));
        }
    }

    private WorkerCapabilities WithToolCapabilities(WorkerCapabilities capabilities) => capabilities with
    {
        ToolCapabilities = ToolCapabilities,
    };

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

        if (provider.Kind == ProviderKind.DeepSeek
            && DeepSeekV4Catalog.TryGet(modelId, out var selected)
            && !selected.Supports(ProviderProtocol.CodexWorker))
        {
            throw new InvalidOperationException(selected.WorkerUnavailableReason ?? DeepSeekV4Catalog.UnsupportedWorkerReason);
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
            var completion = agentRuntime is null
                ? await ExecuteLegacyTextTurnAsync(runtime)
                : await ExecuteAgentRuntimeAsync(runtime);
            runtime.Job = runtime.Job with
            {
                Status = completion.Status,
                CompletedAt = clock.UtcNow,
                StatusMessage = completion.Status == WorkerJobStatus.Completed ? "completed" : completion.Unresolved.FirstOrDefault() ?? completion.Status.ToString(),
            };
            Complete(
                runtime,
                new WorkerResult(
                    runtime.Task.TaskId,
                    completion.Status,
                    completion.Content,
                    completion.RawResponse,
                    completion.Risks,
                    completion.Unresolved,
                    provider.Id,
                    provider.Name,
                    completion.RequestUri,
                    completion.ResponseModel ?? runtime.ModelId,
                    completion.Usage)
                {
                    ChangedFiles = completion.ChangedFiles,
                    ProviderTurns = completion.ProviderTurns,
                    ToolCalls = completion.ToolCalls,
                    FailedToolCalls = completion.FailedToolCalls,
                    DeniedToolCalls = completion.DeniedToolCalls,
                    Duration = completion.Duration,
                });
        }
        catch (ProviderRequestException exception)
        {
            var status = exception.Kind == ProviderErrorKind.Cancelled ? WorkerJobStatus.Interrupted : WorkerJobStatus.Failed;
            runtime.Job = runtime.Job with { Status = status, CompletedAt = clock.UtcNow, StatusMessage = exception.Message };
            Complete(runtime, new WorkerResult(
                runtime.Task.TaskId,
                status,
                exception.Message,
                null,
                [],
                [exception.Kind.ToString()],
                provider.Id,
                provider.Name,
                ProviderEndpoint(),
                runtime.ModelId,
                null,
                exception.Kind.ToString()));
        }
        catch (OperationCanceledException)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Interrupted, CompletedAt = clock.UtcNow, StatusMessage = "Provider request cancelled." };
            Complete(runtime, new WorkerResult(
                runtime.Task.TaskId,
                WorkerJobStatus.Interrupted,
                "Provider request cancelled.",
                null,
                [],
                [],
                provider.Id,
                provider.Name,
                ProviderEndpoint(),
                runtime.ModelId,
                null,
                ProviderErrorKind.Cancelled.ToString()));
        }
        catch (Exception exception)
        {
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.Failed, CompletedAt = clock.UtcNow, StatusMessage = "Unexpected Provider failure." };
            Complete(runtime, new WorkerResult(
                runtime.Task.TaskId,
                WorkerJobStatus.Failed,
                "Unexpected Provider failure.",
                null,
                [],
                [exception.GetType().Name],
                provider.Id,
                provider.Name,
                ProviderEndpoint(),
                runtime.ModelId,
                null,
                exception.GetType().Name));
        }
        finally
        {
            if (entered)
            {
                concurrency.Release();
            }
        }
    }

    private async Task<ExternalExecution> ExecuteLegacyTextTurnAsync(ExternalJobRuntime runtime)
    {
        var completion = await client.CompleteAsync(provider, runtime.ModelId, runtime.Task.Prompt, runtime.Cancellation.Token);
        return new ExternalExecution(
            WorkerJobStatus.Completed,
            completion.Content,
            completion.RawResponse,
            completion.Usage is null ? ["Provider 未返回 Usage；费用只能标记为不可用或估算。"] : [],
            [],
            completion.RequestUri,
            completion.ResponseModel,
            completion.Usage);
    }

    private async Task<ExternalExecution> ExecuteAgentRuntimeAsync(ExternalJobRuntime runtime)
    {
        var permissionMode = runtime.Task.ExternalWorkerPermission switch
        {
            ExternalWorkerPermissionMode.ReadOnly => ExternalToolPermissionMode.ReadOnly,
            ExternalWorkerPermissionMode.FullAccess => ExternalToolPermissionMode.FullAccess,
            _ => ExternalToolPermissionMode.WorkspaceFullAccess,
        };
        var session = new ExternalToolSession(
            runtime.Task.TaskId,
            runtime.Task.WorkingDirectory,
            runtime.Task.WorkingDirectory,
            permissionMode,
            runtime.Task.AllowedReadScope.Count > 0 ? runtime.Task.AllowedReadScope : runtime.Task.Scope.Files,
            runtime.Task.AllowedWriteScope,
            clock.UtcNow);
        var result = await agentRuntime!.ExecuteAsync(
            provider,
            runtime.ModelId,
            runtime.Task.Prompt,
            session,
            runtime.Cancellation.Token);
        var status = result.State switch
        {
            ExternalAgentRuntimeState.Completed => WorkerJobStatus.Completed,
            ExternalAgentRuntimeState.Cancelled => WorkerJobStatus.Interrupted,
            _ => WorkerJobStatus.Failed,
        };
        return new ExternalExecution(
            status,
            result.Content,
            CompactResult(result, status),
            result.Usage is null ? ["Provider 未返回 Usage；费用只能标记为不可用或估算。", .. result.Risks] : result.Risks,
            status == WorkerJobStatus.Completed ? [] : [result.State.ToString()],
            ProviderEndpoint(),
            runtime.ModelId,
            result.Usage)
        {
            ChangedFiles = result.ChangedFiles,
            ProviderTurns = result.ProviderTurns,
            ToolCalls = result.ToolCalls,
            FailedToolCalls = result.FailedToolCalls,
            DeniedToolCalls = result.DeniedToolCalls,
            Duration = result.Duration,
        };
    }

    private static System.Text.Json.JsonElement CompactResult(
        ExternalAgentRuntimeResult result,
        WorkerJobStatus status) => System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            Summary = result.Content,
            ChangedFiles = result.ChangedFiles,
            Validation = status == WorkerJobStatus.Completed ? "runtime-completed" : result.State.ToString(),
            Acceptance = status == WorkerJobStatus.Completed,
            RiskNotes = result.Risks,
            NeedReview = true,
            Activity = result.Activity.Select(item => new
            {
                item.Sequence,
                item.ToolCallId,
                item.ToolName,
                item.Succeeded,
                item.Denied,
                item.TimedOut,
                item.ExitCode,
                item.ChangedFiles,
            }),
            Runtime = new
            {
                State = result.State.ToString(),
                result.ProviderTurns,
                result.ToolCalls,
                result.FailedToolCalls,
                result.DeniedToolCalls,
                DurationMilliseconds = result.Duration.TotalMilliseconds,
            },
        });

    private static WorkerModelCapability ToCapability(string id)
    {
        if (DeepSeekV4Catalog.TryGet(id, out var model))
        {
            var supportsWorker = model.Supports(ProviderProtocol.CodexWorker);
            return new(
                model.Id,
                model.DisplayName,
                supportsWorker ? ["low", "medium", "high"] : [],
                supportsWorker ? "medium" : "none",
                string.Equals(model.Id, DeepSeekV4Catalog.FlashModelId, StringComparison.Ordinal));
        }

        return new(id, id, [], "none", false);
    }

    private ExternalJobRuntime Runtime(string jobId) => jobs.TryGetValue(jobId, out var runtime)
        ? runtime
        : throw new KeyNotFoundException($"External Worker job not found: {jobId}");

    private Uri? ProviderEndpoint() => provider.BaseUri is null
        ? null
        : new Uri($"{provider.BaseUri.AbsoluteUri.TrimEnd('/')}/chat/completions", UriKind.Absolute);

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

    private sealed record ExternalExecution(
        WorkerJobStatus Status,
        string? Content,
        System.Text.Json.JsonElement? RawResponse,
        IReadOnlyList<string> Risks,
        IReadOnlyList<string> Unresolved,
        Uri? RequestUri,
        string? ResponseModel,
        ProviderUsage? Usage = null)
    {
        public IReadOnlyList<string> ChangedFiles { get; init; } = [];

        public int? ProviderTurns { get; init; }

        public int? ToolCalls { get; init; }

        public int? FailedToolCalls { get; init; }

        public int? DeniedToolCalls { get; init; }

        public TimeSpan? Duration { get; init; }
    }
}
