using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Scheduling;

public sealed class SchedulerUsageRecorder(IUsageLedgerRepository usage, IClock clock) : ISchedulerResultObserver
{
    public async Task OnResultAsync(ScheduledDelegation task, CancellationToken cancellationToken = default)
    {
        var result = task.Result;
        if (result is null)
        {
            return;
        }

        var status = result.State == DelegationState.Failed ? WorkerJobStatus.Failed : WorkerJobStatus.Completed;
        var worker = new WorkerLedgerEntry(
            task.Packet.TaskId,
            string.Empty,
            task.Transport.ToString(),
            result.ModelId ?? "unavailable",
            "unavailable",
            status,
            task.StartedAt ?? task.CreatedAt,
            task.CompletedAt,
            AdoptionStatus.Pending,
            task.Packet.Goal,
            task.Packet.Goal,
            null,
            false,
            result.Summary,
            result.FailureReason);
        await usage.UpsertTaskGroupAsync(new TaskGroupLedger(
            task.Packet.TaskId,
            "official-codex-desktop",
            "unavailable",
            "unavailable",
            task.CreatedAt,
            task.CompletedAt,
            [worker],
            task.UpdatedAt), cancellationToken);

        var providerUsage = result.Usage;
        var evidence = providerUsage is null ? EvidenceKind.Unavailable : EvidenceKind.Actual;
        await usage.AppendUsageAsync(new UsageSnapshot(
            Guid.NewGuid(),
            task.Packet.TaskId,
            task.Packet.TaskId,
            result.ProviderId ?? (task.Transport == WorkerTransport.NativeCustomAgent ? "native-codex" : "unavailable"),
            result.ModelId ?? "unavailable",
            clock.UtcNow,
            new MeasuredLong(providerUsage?.InputTokens, evidence),
            new MeasuredLong(providerUsage?.OutputTokens, evidence),
            new MeasuredLong(providerUsage?.TotalTokens, evidence),
            new MeasuredLong(task.Transport == WorkerTransport.ExternalProvider ? 1 : null, task.Transport == WorkerTransport.ExternalProvider ? EvidenceKind.Actual : EvidenceKind.Unavailable),
            new MeasuredDecimal(null, EvidenceKind.Unavailable),
            "CNY",
            null,
            providerUsage is null ? ["当前 Transport 未提供可靠 Usage；未伪造数值。"] : ["Provider 返回的实际 token Usage。"]), cancellationToken);
    }
}
