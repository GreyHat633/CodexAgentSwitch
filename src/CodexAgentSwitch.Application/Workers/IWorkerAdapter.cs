using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Workers;

public interface IWorkerAdapter
{
    string AdapterId { get; }

    IReadOnlySet<WorkerToolCapability> ToolCapabilities => new HashSet<WorkerToolCapability>();

    Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default);

    Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default);

    Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default);

    Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default);

    Task CancelAsync(string jobId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string jobId, CancellationToken cancellationToken = default);
}
