using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Orchestration;

public sealed record WorkerLaunchResult(
    WorkerJob Job,
    string SelectedAdapterId,
    bool UsedFallback,
    string? PrimaryFailure);

public sealed class WorkerRoutingService
{
    public async Task<WorkerLaunchResult> LaunchAsync(
        WorkerTask task,
        IWorkerAdapter primary,
        IWorkerAdapter? fallback,
        bool allowFallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capabilities = await primary.GetCapabilitiesAsync(cancellationToken);
            if (!capabilities.IsAvailable)
            {
                throw new InvalidOperationException(capabilities.Warnings.FirstOrDefault() ?? "首选 Worker 不可用。");
            }

            var job = await primary.SpawnAsync(task, cancellationToken);
            return new WorkerLaunchResult(job, primary.AdapterId, false, null);
        }
        catch (Exception exception) when (allowFallback && fallback is not null && !ReferenceEquals(primary, fallback))
        {
            var capabilities = await fallback.GetCapabilitiesAsync(cancellationToken);
            if (!capabilities.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"首选 Worker 失败，回退 Worker 也不可用：{string.Join("; ", capabilities.Warnings)}",
                    exception);
            }

            var fallbackJob = await fallback.SpawnAsync(task, cancellationToken);
            return new WorkerLaunchResult(fallbackJob, fallback.AdapterId, true, exception.Message);
        }
    }

    public async Task<WorkerLaunchResult> CancelAndFallbackAsync(
        WorkerTask task,
        IWorkerAdapter failedAdapter,
        string failedJobId,
        IWorkerAdapter fallback,
        CancellationToken cancellationToken = default)
    {
        await failedAdapter.CancelAsync(failedJobId, cancellationToken);
        var capabilities = await fallback.GetCapabilitiesAsync(cancellationToken);
        if (!capabilities.IsAvailable)
        {
            throw new InvalidOperationException($"回退 Worker 不可用：{string.Join("; ", capabilities.Warnings)}");
        }

        var job = await fallback.SpawnAsync(task, cancellationToken);
        return new WorkerLaunchResult(job, fallback.AdapterId, true, "用户从失败或超预算 Worker 一键回退。");
    }
}
