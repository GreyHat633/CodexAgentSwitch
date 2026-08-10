namespace CodexAgentSwitch.Application.Usage;

public sealed record NativeUsageRecord(
    string SessionId,
    string? Cwd,
    string? Project,
    string? Model,
    string? ReasoningEffort,
    string AgentRole,
    long Calls,
    long InputTokens,
    long CachedInputTokens,
    long UncachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string SourcePath,
    string Attribution);

public interface IUsageSource
{
    IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default);
}
