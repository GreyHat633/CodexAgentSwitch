using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Scheduling;

namespace CodexAgentSwitch.Application.Scheduling;

public sealed record DelegationPreflightCapabilities(
    bool RegistrationReady = false,
    bool NativeSpawnReady = false,
    bool NativeAgentCapability = false);

/// <summary>Optional host-provided readiness; no model call is made.</summary>
public interface IDelegationPreflightCapabilities
{
    DelegationPreflightCapabilities Current { get; }
}

public sealed class DelegationPreflight(
    IProjectRepository projects,
    IProfileRepository profiles,
    IEnumerable<IWorkerExecutor> executors,
    AppliedProjectWorkerGuard? resolver = null,
    Func<SchedulerState>? schedulerState = null,
    Func<int>? activeTaskCount = null,
    IDelegationPreflightCapabilities? capabilities = null) : IDelegationPreflight
{
    private readonly IReadOnlyList<IWorkerExecutor> executors = executors.ToArray();
    private readonly AppliedProjectWorkerGuard resolver = resolver ?? new AppliedProjectWorkerGuard(projects);
    private Func<SchedulerState>? schedulerStateAccessor = schedulerState;
    private Func<int>? activeTaskCountAccessor = activeTaskCount;

    public void AttachScheduler(Func<SchedulerState> state, Func<int> activeCount)
    {
        schedulerStateAccessor = state;
        activeTaskCountAccessor = activeCount;
    }

    public async Task<DelegationPreflightResult> EvaluateAsync(DelegationPreflightRequest request, CancellationToken cancellationToken = default)
    {
        // A standalone preflight has no scheduler callback; the scheduler
        // facade supplies its own stopped/paused check before calling us.
        var schedulerReady = schedulerStateAccessor is null || schedulerStateAccessor() is SchedulerState.Ready or SchedulerState.Working;
        var resolution = await resolver.ResolveProjectAsync(request.WorkingDirectory, request.ProjectId, cancellationToken);
        if (resolution.Project is null)
        {
            var projectReason = resolution.Source switch
            {
                "PROJECT_ID_MISMATCH" => "PROJECT_ID_MISMATCH",
                "AMBIGUOUS_PROJECT_MAPPING" => "PROJECT_MAPPING_AMBIGUOUS",
                _ => "PROJECT_NOT_RESOLVED",
            };
            return Result(
                schedulerReady, resolution.Source, projectReason, null, null,
                false, false, false, false, false, Readiness(null, null),
                candidates: resolution.Candidates);
        }

        var project = resolution.Project;
        var applied = project.NativeCodexAdaptation?.AppliedSnapshot;
        if (applied is null)
        {
            return Result(schedulerReady, resolution.Source, "PROFILE_NOT_APPLIED", project.Id, null, true, false, false, false, false, Readiness(null, null));
        }

        var profile = await profiles.GetAsync(applied.ProfileId, cancellationToken);
        if (profile is null)
        {
            return Result(schedulerReady, resolution.Source, "PROFILE_NOT_APPLIED", project.Id, null, true, false, false, false, false, Readiness(applied, null));
        }

        var workerId = string.Equals(applied.WorkerKind, nameof(EffectiveWorkerKind.NativeAgent), StringComparison.Ordinal)
            ? applied.WorkerRole
            : string.Equals(applied.WorkerKind, nameof(EffectiveWorkerKind.ExternalAgent), StringComparison.Ordinal)
                ? applied.ProviderId
                : null;
        var requestedWorker = request.WorkerId;
        if (!string.IsNullOrWhiteSpace(requestedWorker) && !string.Equals(requestedWorker, workerId, StringComparison.Ordinal))
        {
            return Result(schedulerReady, resolution.Source, "WORKER_REGISTRATION_FAILED", project.Id, workerId, true, true, false, false, false, Readiness(applied, null));
        }

        var enabled = !string.Equals(applied.WorkerKind, nameof(EffectiveWorkerKind.None), StringComparison.Ordinal)
            && (profile.WorkerPolicy.Enabled || string.Equals(applied.WorkerKind, nameof(EffectiveWorkerKind.NativeAgent), StringComparison.Ordinal));
        if (!enabled)
        {
            return Result(schedulerReady, resolution.Source, "WORKER_DISABLED", project.Id, workerId, true, true, false, false, false, Readiness(applied, null));
        }
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return Result(schedulerReady, resolution.Source, "WORKER_ROLE_MISSING", project.Id, null, true, true, true, false, false, Readiness(applied, null));
        }

        var packet = new TaskPacket(request.TaskId ?? "preflight", project.Id, request.WorkingDirectory, workerId, "preflight", ["preflight"], [], [], ["preflight"], [], "preflight");
        var caps = Readiness(applied, packet);
        var available = caps.RegistrationReady;
        if (!available)
        {
            return Result(schedulerReady, resolution.Source, "WORKER_REGISTRATION_FAILED", project.Id, workerId, true, true, true, false, false, caps);
        }

        var maxWorkers = Math.Max(1, applied.MaxWorkers);
        var slot = (activeTaskCountAccessor?.Invoke() ?? 0) < maxWorkers;
        if (!slot)
        {
            return Result(schedulerReady, resolution.Source, "WORKER_SLOT_UNAVAILABLE", project.Id, workerId, true, true, true, true, false, caps);
        }

        if (!schedulerReady)
        {
            return Result(false, resolution.Source, "SCHEDULER_UNAVAILABLE", project.Id, workerId, true, true, true, true, true, caps);
        }

        var native = string.Equals(applied.WorkerKind, nameof(EffectiveWorkerKind.NativeAgent), StringComparison.Ordinal);
        if (native && !caps.RegistrationReady)
            return Result(schedulerReady, resolution.Source, "WORKER_REGISTRATION_FAILED", project.Id, workerId, true, true, true, true, true, caps);
        if (native && !caps.NativeAgentCapability)
            return Result(schedulerReady, resolution.Source, "NATIVE_AGENT_UNAVAILABLE", project.Id, workerId, true, true, true, true, true, caps);
        if (native && !caps.NativeSpawnReady)
            return Result(schedulerReady, resolution.Source, "NATIVE_SPAWN_FAILED", project.Id, workerId, true, true, true, true, true, caps);
        return Result(true, resolution.Source, "READY", project.Id, workerId, true, true, true, true, true, caps, applied.ProfileId.ToString());
    }

    private static DelegationPreflightResult Result(
        bool schedulerReady,
        string source,
        string reason,
        string? projectId,
        string? workerId,
        bool projectResolved,
        bool profileResolved,
        bool workerEnabled,
        bool workerAvailable,
        bool slotAvailable,
        DelegationPreflightCapabilities caps,
        string? profileId = null,
        IReadOnlyList<DelegationProjectCandidate>? candidates = null)
    {
        var native = workerId?.StartsWith("cas_", StringComparison.Ordinal) == true
            || workerId?.StartsWith("native-", StringComparison.Ordinal) == true;
        var registration = caps.RegistrationReady;
        var spawn = caps.NativeSpawnReady;
        var capability = caps.NativeAgentCapability;
        var nativeReady = !native || registration && spawn && capability;
        var dispatch = reason == "READY" && schedulerReady && projectResolved && profileResolved && workerEnabled && workerAvailable && slotAvailable
            && (!native || registration && spawn && capability);
        return new DelegationPreflightResult(schedulerReady, nativeReady, registration, spawn, capability,
            projectResolved, profileResolved, workerEnabled, workerAvailable, slotAvailable, dispatch,
            projectId, workerId, source, profileId, reason, [reason], candidates);
    }

    private DelegationPreflightCapabilities Readiness(NativeCodexAppliedSnapshot? applied, TaskPacket? packet)
    {
        if (capabilities is not null) return capabilities.Current;
        var native = applied is not null && string.Equals(applied.WorkerKind, nameof(EffectiveWorkerKind.NativeAgent), StringComparison.Ordinal);
        var registration = packet is not null && executors.Any(item => item.CanExecute(packet));
        var capability = native && string.Equals(applied?.ValidationStatus, nameof(WorkerExecutionCapability.Supported), StringComparison.OrdinalIgnoreCase);
        var spawn = native && !string.IsNullOrWhiteSpace(applied?.WorkerRole)
            && !string.IsNullOrWhiteSpace(applied?.ConfigurationFingerprint);
        return new DelegationPreflightCapabilities(registration, spawn, capability);
    }
}
