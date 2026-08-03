using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Usage;

public sealed class SafeWorkerDeletionCoordinator(
    IUsageLedgerRepository repository,
    IWorkerUsageCollector usageCollector,
    IClock clock)
{
    public async Task<TaskGroupLedger> ArchiveAndDeleteAsync(
        TaskGroupLedger ledger,
        string jobId,
        IWorkerAdapter adapter,
        AdoptionRecord adoption,
        WorkerUsageContext usageContext,
        CancellationToken cancellationToken = default)
    {
        var status = await adapter.ReadStatusAsync(jobId, cancellationToken);
        if (!IsTerminal(status.Status))
        {
            throw new InvalidOperationException("Worker 仍在运行；必须等待终态后才能归档和删除。");
        }

        var result = await adapter.WaitAsync(jobId, TimeSpan.FromSeconds(5), cancellationToken);
        var existing = ledger.Workers.SingleOrDefault(worker => worker.JobId == jobId)
            ?? throw new InvalidOperationException("Task Group ledger does not contain the Worker job.");
        var updatedWorker = existing with
        {
            Status = status.Status,
            CompletedAt = status.CompletedAt,
            AdoptionStatus = adoption.Status,
            ActualSkippedWork = adoption.ActualSkippedWork,
            DuplicateWork = adoption.DuplicateWork,
            ResultSummary = result?.Summary,
        };
        var updatedLedger = ledger with
        {
            Workers = ledger.Workers.Select(worker => worker.JobId == jobId ? updatedWorker : worker).ToArray(),
            UpdatedAt = clock.UtcNow,
        };
        var snapshot = usageCollector.Capture(ledger.Id, jobId, result, usageContext);

        // Persistence deliberately precedes destructive Thread deletion.
        await repository.UpsertTaskGroupAsync(updatedLedger, cancellationToken);
        await repository.AppendUsageAsync(snapshot, cancellationToken);
        await adapter.DeleteAsync(jobId, cancellationToken);
        return updatedLedger;
    }

    private static bool IsTerminal(WorkerJobStatus status) => status is WorkerJobStatus.Completed or WorkerJobStatus.Failed or WorkerJobStatus.Interrupted or WorkerJobStatus.UnknownRecoverable;
}
