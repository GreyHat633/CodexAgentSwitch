using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Application.Usage;

namespace CodexAgentSwitch.Application.Scheduling;

public sealed record RepartitionTelemetry(
    string TaskGroupId,
    long Sequence,
    DateTimeOffset RecordedAt,
    RepartitionTrigger Trigger,
    WorkOwner Decision,
    RepartitionReasonCode Reason,
    string WorkSummary,
    string? WorkerIdentity,
    string? Result,
    string? PackageId = null,
    string? WorkingDirectory = null,
    string? PackageKind = null,
    IReadOnlyList<string>? DeclaredScopes = null,
    int? CostWindowIndex = null)
{
    public RepartitionRecord Record => new(
        Sequence,
        Trigger,
        Decision,
        Reason,
        WorkSummary,
        WorkerIdentity,
        Result);
}

public interface IWorkPackageLeaseRepository
{
    Task<WorkPackageLease?> GetActiveAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default);
    Task<WorkPackageLease?> GetActiveForWorkingDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkPackageLease>> ListAsync(string? packageId = null, CancellationToken cancellationToken = default);
    Task SaveAsync(WorkPackageLease lease, CancellationToken cancellationToken = default);
}

public sealed record PreToolUseRequest(string SessionId, string WorkingDirectory, string ToolName, string? ToolInput);

public sealed record PreToolUseResult(
    string SessionId,
    string ToolName,
    string WorkingDirectory,
    string Classification,
    bool Allowed,
    bool RequiresSafetyPolicy,
    string Reason);

public sealed record SchedulerRuntimeDiagnostics(
    MainCostGuardTelemetry Economy,
    WorkPackageLeaseStatus? Ownership,
    string? PackageId,
    string? WorkerIdentity,
    string? LastReason,
    int GuardHits);

public interface ISchedulerTaskRepository
{
    Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default);
    Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default);
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
    Task<RepartitionTelemetry> RecordRepartitionAsync(
        string taskGroupId,
        RepartitionTrigger trigger,
        WorkOwner decision,
        RepartitionReasonCode reason,
        string workSummary,
        string? workerIdentity = null,
        string? result = null,
        CancellationToken cancellationToken = default) => Task.FromException<RepartitionTelemetry>(new NotSupportedException("Repartition telemetry is not available on this scheduler."));
    Task<RepartitionTelemetry> RecordRepartitionAsync(
        string taskGroupId,
        RepartitionTrigger trigger,
        WorkOwner decision,
        RepartitionReasonCode reason,
        string workSummary,
        string? workerIdentity,
        string? result,
        string? packageId,
        string? workingDirectory,
        string? packageKind,
        IReadOnlyList<string>? declaredScopes,
        int? costWindowIndex,
        CancellationToken cancellationToken = default) => Task.FromException<RepartitionTelemetry>(new NotSupportedException("Repartition telemetry is not available on this scheduler."));
    Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<RepartitionTelemetry>>(new NotSupportedException("Repartition telemetry is not available on this scheduler."));
    Task<PreToolUseResult> EvaluatePreToolUseAsync(PreToolUseRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<PreToolUseResult>(new NotSupportedException("PreToolUse is not available on this scheduler."));
    Task<WorkPackageLease?> CompletePackageAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default) =>
        Task.FromException<WorkPackageLease?>(new NotSupportedException("Package leases are not available on this scheduler."));
    Task<SchedulerRuntimeDiagnostics> GetRuntimeDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<SchedulerRuntimeDiagnostics>(new NotSupportedException("Runtime diagnostics are not available on this scheduler."));
}
