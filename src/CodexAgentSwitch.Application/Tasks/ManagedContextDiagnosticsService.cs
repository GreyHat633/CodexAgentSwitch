using System.Security.Cryptography;
using System.Text;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

public sealed record ManagedContextDiagnosticEntry(
    string ProjectId,
    string ProjectName,
    string CanonicalRootFingerprint,
    string ThreadId,
    string SessionId,
    ManagedContextOwnershipState OwnershipState,
    ContextEconomyState? ContextState,
    long? LatestInputTokens,
    decimal? CurrentPressure,
    DateTimeOffset? LastSafeBoundaryAt,
    DateTimeOffset? CompactionRequestedAt,
    DateTimeOffset? CompactionStartedAt,
    DateTimeOffset? CompactionCompletedAt,
    string? CompactionRequestId,
    CompactionEffectiveness? Effectiveness,
    int CooldownRemaining,
    string ReasonCode);

public sealed record ManagedContextDiagnosticsSnapshot(
    IReadOnlyList<ManagedContextDiagnosticEntry> Entries,
    int MonitoringCount,
    int LostCount,
    int FaultedCount);

public interface IManagedContextDiagnosticsService
{
    Task<ManagedContextDiagnosticsSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects persisted managed-session state without reading prompts, responses,
/// source files, tool arguments, Worker Results, or global Codex session data.
/// </summary>
public sealed class ManagedContextDiagnosticsService(
    IManagedContextSessionStore sessions,
    IMainContextEconomyStateStore contextStates,
    IProjectRepository projects) : IManagedContextDiagnosticsService
{
    public async Task<ManagedContextDiagnosticsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var projectNames = (await projects.ListAsync(cancellationToken))
            .ToDictionary(value => value.Id, value => value.Name, StringComparer.Ordinal);
        var entries = new List<ManagedContextDiagnosticEntry>();
        foreach (var binding in await sessions.ListAsync(cancellationToken))
        {
            ContextEconomySnapshot? context = null;
            try
            {
                context = await contextStates.LoadAsync(binding.ThreadId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A corrupt or unavailable context snapshot is represented as
                // unavailable diagnostics; it never broadens the read scope.
            }

            var latest = context?.Samples.LastOrDefault();
            entries.Add(new ManagedContextDiagnosticEntry(
                binding.ProjectId,
                projectNames.GetValueOrDefault(binding.ProjectId) ?? binding.ProjectId,
                Fingerprint(binding.CanonicalProjectRoot),
                binding.ThreadId,
                binding.SessionId,
                binding.OwnershipState,
                context?.State,
                latest?.InputTokens,
                context?.PostCompactionPressure ?? context?.PreCompactionPressure,
                binding.LastSafeBoundaryAt,
                binding.LastCompactionRequestedAt ?? context?.LastCompactionRequestedAt,
                binding.LastCompactionStartedAt ?? context?.LastCompactionStartedAt,
                binding.LastCompactionCompletedAt ?? context?.LastCompactionCompletedAt,
                binding.LastCompactionRequestId ?? context?.LastCompactionRequestId,
                context?.LastEffectiveness?.Classification,
                context?.CooldownRemaining ?? 0,
                ReasonCode(binding, context)));
        }

        return new ManagedContextDiagnosticsSnapshot(
            entries.OrderByDescending(value => value.CompactionCompletedAt ?? value.LastSafeBoundaryAt).ToArray(),
            entries.Count(value => value.OwnershipState is ManagedContextOwnershipState.Owned
                or ManagedContextOwnershipState.Idle
                or ManagedContextOwnershipState.Verifying),
            entries.Count(value => value.OwnershipState == ManagedContextOwnershipState.Lost),
            entries.Count(value => value.OwnershipState == ManagedContextOwnershipState.Faulted));
    }

    private static string ReasonCode(ManagedContextSession binding, ContextEconomySnapshot? context) =>
        binding.OwnershipState switch
        {
            ManagedContextOwnershipState.Lost => "OWNERSHIP_LOST",
            ManagedContextOwnershipState.Faulted => "CONTEXT_CONTROL_FAULTED",
            ManagedContextOwnershipState.Released => "OWNERSHIP_RELEASED",
            _ when context is null => "CONTEXT_STATE_UNAVAILABLE",
            _ => context.State.ToString().ToUpperInvariant(),
        };

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
}
