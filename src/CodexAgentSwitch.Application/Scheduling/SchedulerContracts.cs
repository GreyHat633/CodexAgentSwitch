using CodexAgentSwitch.Domain.Scheduling;

namespace CodexAgentSwitch.Application.Scheduling;

public interface ISchedulerTaskRepository
{
    Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default);
}

public interface IWorkerExecutor
{
    WorkerTransport Transport { get; }
    bool CanExecute(TaskPacket packet);
    Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default);
}

public interface IDelegationPolicyGuard
{
    Task ValidateAsync(TaskPacket packet, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves identity fields that the Codex-facing tool deliberately does not
/// expose as free-form choices. Resolution happens before TaskPacket.Validate
/// and before policy guards so the guards still enforce the final identity.
/// </summary>
public interface ITaskPacketResolver
{
    Task<TaskPacket> ResolveAsync(TaskPacket packet, CancellationToken cancellationToken = default);
}

public interface ISchedulerResultObserver
{
    Task OnResultAsync(ScheduledDelegation task, CancellationToken cancellationToken = default);
}

public interface IWorkerScheduler : IAsyncDisposable
{
    event EventHandler<SchedulerSnapshot>? SnapshotChanged;
    SchedulerSnapshot Snapshot { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(bool force, CancellationToken cancellationToken = default);
    Task<WorkerResultPacket> DispatchAsync(TaskPacket packet, CancellationToken cancellationToken = default);
    Task<WorkerResultPacket> ReportNativeResultAsync(WorkerResultPacket result, CancellationToken cancellationToken = default);
    Task<WorkerResultPacket> MarkReviewingAsync(string taskId, CancellationToken cancellationToken = default);
    Task<WorkerResultPacket> MarkAdoptedAsync(string taskId, string summary, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default);
}
