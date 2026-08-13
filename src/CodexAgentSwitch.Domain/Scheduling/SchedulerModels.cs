using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Profiles;
using System.Security.Cryptography;
using System.Text;

namespace CodexAgentSwitch.Domain.Scheduling;

public enum SchedulerState
{
    Stopped,
    Ready,
    Working,
    Paused,
    Faulted,
}

public enum DelegationState
{
    Created,
    Delegated,
    Running,
    ResultReceived,
    Reviewing,
    Adopted,
    Failed,
    Cancelled,
    ResultPending,
    Blocked,
}

public enum WorkerTransport
{
    NativeCustomAgent,
    ExternalProvider,
}

/// <summary>
/// Plaintext, bounded work handed to either executor. It deliberately contains
/// no parent-thread transcript or encrypted collaboration payload.
/// </summary>
public sealed record TaskPacket(
    string TaskId,
    string ProjectId,
    string WorkingDirectory,
    string WorkerId,
    string Goal,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> AllowedReadScope,
    IReadOnlyList<string> AllowedWriteScope,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints,
    string OutputContract)
{
    public void Validate()
    {
        Require(TaskId, "TaskId");
        Require(WorkingDirectory, "WorkingDirectory");
        Require(WorkerId, "WorkerId");
        Require(Goal, "Goal");
        Require(OutputContract, "OutputContract");
        if (Scope.Count == 0)
        {
            throw new InvalidOperationException("TaskPacket.Scope 至少需要一个明确范围。");
        }

        if (AcceptanceCriteria.Count == 0)
        {
            throw new InvalidOperationException("TaskPacket.AcceptanceCriteria 至少需要一项。");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"TaskPacket.{name} 必填。");
        }
    }
}

public sealed record NativeWorkerInvocation(
    string AgentRole,
    string ForkTurns,
    TaskPacket TaskPacket,
    string Instruction);

public sealed record WorkerResultPacket(
    string TaskId,
    DelegationState State,
    string Summary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Validation,
    IReadOnlyList<string> Risks,
    string? ProviderId = null,
    string? ModelId = null,
    ProviderUsage? Usage = null,
    NativeWorkerInvocation? NativeInvocation = null,
    string? FailureReason = null,
    int? ProviderTurns = null,
    int? ToolCalls = null,
    int? FailedToolCalls = null,
    int? DeniedToolCalls = null,
    double? DurationMilliseconds = null,
    int? LeaseExtensionCount = null,
    string? HardLimitReason = null,
    BudgetLimits? ConfiguredTaskBudgetSnapshot = null,
    bool? CostVerified = null,
    bool? FinalizationAttempted = null,
    bool? FinalizationSucceeded = null,
    ProviderPricing? Pricing = null,
    string? Currency = null,
    bool? RecoveryAttempted = null,
    bool? RetryAttempted = null,
    string? RecentFailureSummary = null,
    IReadOnlyList<string>? Scope = null);

public sealed record ScheduledDelegation(
    TaskPacket Packet,
    WorkerTransport Transport,
    DelegationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    WorkerResultPacket? Result,
    string? FailureReason,
    DateTimeOffset? ResultDeliveredAt = null);

public sealed record SchedulerSnapshot(
    SchedulerState State,
    int ActiveTaskCount,
    IReadOnlyList<ScheduledDelegation> ActiveTasks,
    string? FaultMessage);

/// <summary>Inputs accepted by the model-free delegation preflight.</summary>
public sealed record DelegationPreflightRequest(
    string WorkingDirectory,
    string? ProjectId = null,
    string? WorkerId = null,
    string? TaskId = null);

public sealed record DelegationProjectCandidate(
    string ProjectId,
    string DisplayName,
    string NormalizedRoot);

/// <summary>
/// Deterministic scheduler readiness.  This is intentionally a data-only
/// contract: preflight never reads model/provider output and never opens the
/// persistence implementation directly.
/// </summary>
public sealed record DelegationPreflightResult(
    bool SchedulerReady,
    bool NativeReady,
    bool RegistrationReady,
    bool NativeSpawnReady,
    bool NativeAgentCapability,
    bool ProjectResolved,
    bool ProfileResolved,
    bool WorkerEnabled,
    bool WorkerAvailable,
    bool SlotAvailable,
    bool DispatchReady,
    string? ProjectId,
    string? WorkerId,
    string? ProjectResolutionSource,
    string? ProfileId,
    string ReasonCode,
    IReadOnlyList<string>? ReasonCodes = null,
    IReadOnlyList<DelegationProjectCandidate>? ProjectCandidates = null)
{
    public IReadOnlyList<string> Reasons => ReasonCodes ?? [ReasonCode];
    public IReadOnlyList<DelegationProjectCandidate> Candidates => ProjectCandidates ?? [];
    public bool SchedulerReachable => true;
    public bool AppliedProfileResolved => ProfileResolved;
    public bool WorkerRoleResolved => !string.IsNullOrWhiteSpace(WorkerId);
    public bool WorkerSlotAvailable => SlotAvailable;
    public string? WorkerRole => WorkerId;
    public string PreflightReasonCode => ReasonCode;
}

public static class SchedulerEndpoint
{
    public static string PipeName
    {
        get
        {
            var identity = $"{Environment.MachineName}\\{Environment.UserName}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
            return $"CodexAgentSwitch-Scheduler-{hash}";
        }
    }
}
