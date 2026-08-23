using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

/// <summary>Persistence boundary for context-economy state. Implementations may use a file, database, or memory.</summary>
public interface IMainContextEconomyStateStore
{
    Task<ContextEconomySnapshot?> LoadAsync(string threadId, CancellationToken cancellationToken = default);
    Task SaveAsync(ContextEconomySnapshot snapshot, CancellationToken cancellationToken = default);
}

public interface IMainContextEconomyCoordinator
{
    Task BindThreadAsync(
        string threadId,
        IMainAgentSession session,
        Func<CancellationToken, Task<ContextControlValidation>>? controlGuard = null,
        CancellationToken cancellationToken = default);
    Task<ContextEconomyObservationResult> ObserveTurnAsync(
        string threadId,
        ContextTurnSample sample,
        bool safeBoundary = false,
        CancellationToken cancellationToken = default);
    Task<ContextEconomyCompactionResult?> CompactAtSafeBoundaryAsync(
        string threadId,
        CancellationToken cancellationToken = default);
    Task<StructuredCompactionObservation> ObserveStructuredCompactionAsync(
        string threadId,
        CompactionTrigger trigger,
        DateTimeOffset compactedAt,
        IReadOnlyList<ContextTurnSample>? preCompactionSamples = null,
        CancellationToken cancellationToken = default);
    Task<ContextEconomySnapshot?> GetSnapshotAsync(string threadId, CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
}

public sealed record ContextControlValidation(bool Allowed, string Reason)
{
    public static ContextControlValidation Permit(string reason = "Control lease is valid.") => new(true, reason);
    public static ContextControlValidation Reject(string reason) => new(false, reason);
}
