using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Tasks;

public sealed class TaskProfileSnapshotFactory(
    IProviderRepository providers,
    IClock clock)
{
    public async Task<TaskProfileSnapshot> CaptureAsync(
        Profile profile,
        CancellationToken cancellationToken = default)
    {
        TaskProviderSnapshot? providerSnapshot = null;
        if (profile.WorkerPolicy.Enabled
            && profile.WorkerPolicy.Source == WorkerSource.ExternalProvider)
        {
            var providerId = profile.WorkerPolicy.PreferredProviderId
                ?? throw new InvalidOperationException("当前方案没有选择外部 Provider。");
            var provider = await providers.GetAsync(providerId, cancellationToken)
                ?? throw new InvalidOperationException($"Provider {providerId} 不存在。");
            providerSnapshot = new TaskProviderSnapshot(
                provider.Id,
                provider.Name,
                provider.Kind,
                provider.BaseUri,
                provider.CredentialReference,
                provider.ModelId,
                provider.Timeout,
                provider.IsEnabled,
                provider.Pricing);
        }

        return new TaskProfileSnapshot(
            profile.Id,
            profile.Name,
            profile.MainAgent,
            profile.WorkerPolicy,
            profile.Budget,
            providerSnapshot,
            clock.UtcNow,
            profile.ApprovalMode);
    }
}

public sealed class DelegationDecisionService(IClock clock)
{
    public DelegationDecision Decide(
        TaskProfileSnapshot snapshot,
        bool? requested,
        bool forced = false)
    {
        var policy = snapshot.WorkerPolicy;
        if (forced)
        {
            if (!policy.Enabled || policy.Source == WorkerSource.Disabled)
            {
                throw new InvalidOperationException("当前方案没有启用 Worker，不能执行强制测试。");
            }

            return Create(DelegationDecisionKind.InvokeWorker, "用户要求强制测试当前 Worker。", true, snapshot);
        }

        if (!policy.Enabled || policy.Source == WorkerSource.Disabled)
        {
            return Create(DelegationDecisionKind.Skip, "当前方案已停用 Worker。", false, snapshot);
        }

        if (policy.RoutingMode == RoutingMode.Single)
        {
            return Create(DelegationDecisionKind.Skip, "当前方案使用单代理模式。", false, snapshot);
        }

        if (requested == false)
        {
            return Create(DelegationDecisionKind.Skip, "本轮明确不调用 Worker。", false, snapshot);
        }

        if (policy.RoutingMode == RoutingMode.Manual && requested != true)
        {
            return Create(DelegationDecisionKind.Skip, "手动模式下本轮没有明确允许 Worker。", false, snapshot);
        }

        return Create(DelegationDecisionKind.InvokeWorker, "当前方案与本轮设置允许调用 Worker。", false, snapshot);
    }

    private DelegationDecision Create(
        DelegationDecisionKind kind,
        string reason,
        bool forced,
        TaskProfileSnapshot snapshot) =>
        new(
            kind,
            reason,
            forced,
            snapshot.Provider?.Id ?? (snapshot.WorkerPolicy.Source == WorkerSource.NativeCodex ? "native-codex" : null),
            snapshot.Provider?.ModelId ?? NativeModel(snapshot.WorkerPolicy),
            clock.UtcNow);

    private static string? NativeModel(WorkerPolicy policy) => policy.Source != WorkerSource.NativeCodex
        ? null
        : policy.PreferredProviderId switch
        {
            "native-sol" => "gpt-5.6-sol",
            "native-terra" => "gpt-5.6-terra",
            "native-luna" => "gpt-5.6-luna",
            _ => null,
        };
}

public sealed class ExternalProviderResolver
{
    public ProviderConfiguration Resolve(TaskProfileSnapshot snapshot)
    {
        if (snapshot.WorkerPolicy.Source != WorkerSource.ExternalProvider)
        {
            throw new InvalidOperationException("当前任务快照不是外部 Provider Worker。");
        }

        var provider = snapshot.Provider
            ?? throw new InvalidOperationException("任务快照缺少外部 Provider 配置。");
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException($"Provider {provider.Name} 在任务创建时未启用。");
        }

        if (provider.BaseUri is null || string.IsNullOrWhiteSpace(provider.ModelId))
        {
            throw new InvalidOperationException($"Provider {provider.Name} 的 Base URL 或 Model ID 未配置。");
        }

        return new ProviderConfiguration(
            provider.Id,
            provider.Name,
            provider.Kind,
            provider.BaseUri,
            provider.CredentialReference,
            provider.ModelId,
            new Dictionary<string, string>(),
            provider.Timeout,
            provider.IsEnabled,
            provider.Pricing,
            snapshot.CapturedAt,
            snapshot.CapturedAt);
    }
}

public sealed record WorkerExecutionResult(
    WorkerJob StartedJob,
    WorkerJob FinalJob,
    WorkerResult Result,
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ReasoningEffort,
    ProviderPricing? Pricing);

public sealed record WorkerExecutionStarted(
    WorkerJob Job,
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ReasoningEffort,
    ProviderPricing? Pricing);

public sealed class WorkerOrchestrator(
    IExternalWorkerAdapterFactory externalWorkers,
    IControlledTaskRuntime runtime,
    ExternalProviderResolver externalProviderResolver)
{
    public async Task<WorkerExecutionResult> ExecuteAsync(
        TaskProfileSnapshot snapshot,
        WorkerTask task,
        bool forceNativeLuna = false,
        Func<WorkerExecutionStarted, Task>? onStarted = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(snapshot, forceNativeLuna);
        try
        {
            var startedJob = await resolved.Adapter.SpawnAsync(task with
            {
                ModelId = resolved.ModelId,
                ReasoningEffort = resolved.ReasoningEffort,
            }, cancellationToken);
            if (onStarted is not null)
            {
                await onStarted(new WorkerExecutionStarted(
                    startedJob,
                    resolved.ProviderId,
                    resolved.ProviderName,
                    resolved.ModelId,
                    resolved.ReasoningEffort,
                    resolved.Pricing));
            }

            var result = await resolved.Adapter.WaitAsync(startedJob.JobId, TimeSpan.FromHours(2), cancellationToken)
                ?? throw new TimeoutException("Worker 在等待期限内没有返回终态。");
            var final = await resolved.Adapter.ReadStatusAsync(startedJob.JobId, cancellationToken);
            return new WorkerExecutionResult(
                startedJob,
                final,
                result,
                resolved.ProviderId,
                resolved.ProviderName,
                resolved.ModelId,
                resolved.ReasoningEffort,
                resolved.Pricing);
        }
        finally
        {
            if (resolved.OwnedAdapter is not null)
            {
                await resolved.OwnedAdapter.DisposeAsync();
            }
        }
    }

    private ResolvedWorker Resolve(TaskProfileSnapshot snapshot, bool forceNativeLuna)
    {
        if (forceNativeLuna)
        {
            return new ResolvedWorker(runtime.NativeWorker, "gpt-5.6-luna", "medium", "native-codex", "原生 Codex", null, null);
        }

        if (snapshot.WorkerPolicy.Source == WorkerSource.NativeCodex)
        {
            var modelId = snapshot.WorkerPolicy.PreferredProviderId switch
            {
                "native-sol" => "gpt-5.6-sol",
                "native-terra" => "gpt-5.6-terra",
                "native-luna" => "gpt-5.6-luna",
                _ => throw new InvalidOperationException("当前方案的原生 Worker ID 无效。"),
            };
            return new ResolvedWorker(runtime.NativeWorker, modelId, "medium", "native-codex", "原生 Codex", null, null);
        }

        if (snapshot.WorkerPolicy.Source == WorkerSource.ExternalProvider)
        {
            var provider = externalProviderResolver.Resolve(snapshot);
            var adapter = externalWorkers.Create(provider);
            return new ResolvedWorker(
                adapter,
                provider.ModelId!,
                "medium",
                provider.Id,
                provider.Name,
                provider.Pricing,
                adapter as IAsyncDisposable);
        }

        throw new InvalidOperationException("当前方案未启用 Worker。");
    }

    private sealed record ResolvedWorker(
        IWorkerAdapter Adapter,
        string ModelId,
        string ReasoningEffort,
        string ProviderId,
        string ProviderName,
        ProviderPricing? Pricing,
        IAsyncDisposable? OwnedAdapter);
}
