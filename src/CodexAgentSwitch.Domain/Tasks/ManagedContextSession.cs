namespace CodexAgentSwitch.Domain.Tasks;

public enum ManagedContextOwnershipState
{
    Pending,
    Owned,
    Idle,
    Compacting,
    Verifying,
    Released,
    Lost,
    Faulted,
}

/// <summary>
/// Provenance required before CAS can observe or control one context-economy thread.
/// The record intentionally contains identifiers only; prompt, response, tool input,
/// source content, and Worker Result bodies must never be persisted here.
/// </summary>
public sealed record ManagedContextSession(
    string ProjectId,
    string CanonicalProjectRoot,
    string ThreadId,
    string SessionId,
    string TaskSessionId,
    string AppServerInstanceId,
    string OwnershipLeaseId,
    ManagedContextOwnershipState OwnershipState,
    DateTimeOffset? LastTokenUsageAt = null,
    DateTimeOffset? LastSafeBoundaryAt = null,
    DateTimeOffset? LastCompactionAt = null,
    DateTimeOffset? LastVerifiedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? LastCompactionRequestedAt = null,
    DateTimeOffset? LastCompactionStartedAt = null,
    DateTimeOffset? LastCompactionCompletedAt = null,
    string? LastCompactionRequestId = null);
