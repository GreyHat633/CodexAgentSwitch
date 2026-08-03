using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Usage;

public interface IUsageLedgerRepository
{
    Task UpsertTaskGroupAsync(TaskGroupLedger ledger, CancellationToken cancellationToken = default);

    Task<TaskGroupLedger?> GetTaskGroupAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskGroupLedger>> ListTaskGroupsAsync(CancellationToken cancellationToken = default);

    Task AppendUsageAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageSnapshot>> ListUsageAsync(string taskGroupId, CancellationToken cancellationToken = default);
}
