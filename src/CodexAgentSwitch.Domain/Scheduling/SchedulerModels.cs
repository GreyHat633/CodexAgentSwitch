using CodexAgentSwitch.Domain.Providers;
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
    string? FailureReason = null);

public sealed record ScheduledDelegation(
    TaskPacket Packet,
    WorkerTransport Transport,
    DelegationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    WorkerResultPacket? Result,
    string? FailureReason);

public sealed record SchedulerSnapshot(
    SchedulerState State,
    int ActiveTaskCount,
    IReadOnlyList<ScheduledDelegation> ActiveTasks,
    string? FaultMessage);

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
