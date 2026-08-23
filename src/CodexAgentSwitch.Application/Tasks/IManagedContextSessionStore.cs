using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

public interface IManagedContextSessionStore
{
    Task<ManagedContextSession?> LoadByTaskSessionAsync(
        string taskSessionId,
        CancellationToken cancellationToken = default);

    Task<ManagedContextSession?> LoadByThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedContextSession>> ListAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ManagedContextSession session,
        CancellationToken cancellationToken = default);

    Task<bool> TryUpdateLeaseAsync(
        ManagedContextSession session,
        string expectedOwnershipLeaseId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string taskSessionId,
        CancellationToken cancellationToken = default);
}
