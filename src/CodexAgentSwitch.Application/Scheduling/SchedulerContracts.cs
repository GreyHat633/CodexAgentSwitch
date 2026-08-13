using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Tasks;

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
    int? CostWindowIndex = null,
    int PendingTriggersCleared = 0,
    int PendingTriggerCount = 0,
    IReadOnlyList<RepartitionTrigger>? CoalescedTriggers = null,
    int OwnershipDecisionCount = 0,
    int HardGateDenials = 0,
    bool LeaseActive = false)
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

/// <summary>Durable state for a repartition group awaiting an ownership decision.</summary>
public sealed record PendingRepartitionState(
    string TaskGroupId,
    string WorkingDirectory,
    IReadOnlyList<RepartitionTrigger> PendingTriggers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int HardGateDenialCount = 0);

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

public sealed record MainContextBoundaryRequest(
    string SessionId,
    string ThreadId,
    string WorkingDirectory,
    string Source,
    string Boundary);

public sealed record MainContextBoundaryResult(
    string ThreadId,
    bool BindingAccepted,
    bool TelemetryAvailable,
    ContextEconomyState State,
    bool CompactionRequested,
    bool CompactionSucceeded,
    string Reason);

public sealed record SchedulerRuntimeDiagnostics(
    MainCostGuardTelemetry Economy,
    WorkPackageLeaseStatus? Ownership,
    string? PackageId,
    string? WorkerIdentity,
    string? LastReason,
    int GuardHits,
    ContextEconomyRuntimeDiagnostics? ContextEconomy = null);

public sealed record ContextEconomyRuntimeDiagnostics(
    string ThreadId,
    ContextEconomyState State,
    CompactionTrigger Trigger,
    decimal? PrePressure,
    long? PreInput,
    decimal? PostPressure,
    long? PostInput,
    DateTimeOffset? StructuredCompactedAt,
    CompactionEffectiveness? Effectiveness,
    int CooldownRemaining,
    string Reason);

public interface ISchedulerTaskRepository
{
    Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default);
    Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default);

    // Optional for repositories that predate durable pending repartition state.
    // SQLite provides the durable implementation; lightweight test repositories
    // can retain the existing no-op behavior.
    Task<IReadOnlyList<PendingRepartitionState>> ListPendingRepartitionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PendingRepartitionState>>([]);
    Task UpsertPendingRepartitionAsync(PendingRepartitionState state, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task RemovePendingRepartitionAsync(string taskGroupId, string workingDirectory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
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

public interface IDelegationPreflight
{
    Task<DelegationPreflightResult> EvaluateAsync(
        DelegationPreflightRequest request,
        CancellationToken cancellationToken = default);
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
    Task<WorkerResultPacket> ConsumeResultAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromException<WorkerResultPacket>(new NotSupportedException("External terminal result consumption is not available on this scheduler."));
    Task<DelegationPreflightResult> DelegationPreflightAsync(
        DelegationPreflightRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DelegationPreflightResult>(new NotSupportedException("Delegation preflight is not available on this scheduler."));
    Task<DelegationPreflightResult> PreflightAsync(
        DelegationPreflightRequest request,
        CancellationToken cancellationToken = default) => DelegationPreflightAsync(request, cancellationToken);
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
    Task<RepartitionTelemetry> EnqueueRepartitionTriggerAsync(
        string taskGroupId, IReadOnlyList<RepartitionTrigger> triggers, string workSummary, string workingDirectory,
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
    Task<MainContextBoundaryResult> ObserveMainContextBoundaryAsync(
        MainContextBoundaryRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<MainContextBoundaryResult>(new NotSupportedException("Main context economy is not available on this scheduler."));
    Task<WorkPackageLease?> CompletePackageAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default) =>
        Task.FromException<WorkPackageLease?>(new NotSupportedException("Package leases are not available on this scheduler."));
    Task<SchedulerRuntimeDiagnostics> GetRuntimeDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<SchedulerRuntimeDiagnostics>(new NotSupportedException("Runtime diagnostics are not available on this scheduler."));
}
