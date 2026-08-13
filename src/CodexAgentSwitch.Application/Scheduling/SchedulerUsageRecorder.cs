using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Scheduling;

public sealed class SchedulerUsageRecorder(
    IUsageLedgerRepository usage,
    IClock clock,
    CostCalculator? costCalculator = null) : ISchedulerResultObserver
{
    private readonly CostCalculator costCalculator = costCalculator ?? new CostCalculator();

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
        var currency = result.Currency ?? result.Pricing?.Currency ?? "unavailable";
        var cost = CalculateCost(result, providerUsage, currency);
        IReadOnlyList<string> notes = providerUsage is null
            ? ["Worker result did not expose token usage; cost remains unavailable."]
            : cost.Evidence == EvidenceKind.Unavailable
                ? ["Provider usage was returned, but pricing or currency was not verified; cost remains unavailable."]
                : ["Provider usage returned; cost calculated from configured provider pricing."];
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
            cost,
            currency,
            null,
            notes), cancellationToken);
    }

    private MeasuredDecimal CalculateCost(WorkerResultPacket result, ProviderUsage? providerUsage, string currency)
    {
        if (result.CostVerified != true
            || result.Pricing is null
            || providerUsage?.InputTokens is null
            || providerUsage.OutputTokens is null
            || !string.Equals(result.Pricing.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            return new MeasuredDecimal(null, EvidenceKind.Unavailable);
        }

        return costCalculator.Calculate(result.Pricing, providerUsage.InputTokens, providerUsage.OutputTokens);
    }
}
