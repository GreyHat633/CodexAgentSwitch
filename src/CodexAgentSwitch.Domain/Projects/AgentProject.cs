namespace CodexAgentSwitch.Domain.Projects;

public sealed record AgentProject(
    string Id,
    string Name,
    string WorkingDirectory,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? DefaultProfileId = null,
    NativeCodexProjectAdaptation? NativeCodexAdaptation = null);

public sealed record NativeCodexProjectAdaptation(
    Guid ProfileId,
    string ProfileName,
    string ConfigurationPath,
    string? BackupPath,
    DateTimeOffset AppliedAt,
    string ConfigurationSummary,
    bool OriginalConfigurationExisted,
    NativeCodexAppliedSnapshot? AppliedSnapshot = null);

/// <summary>
/// Immutable deployment-time data.  It intentionally duplicates the fields
/// required by the project UI so a later edit to its source Profile cannot
/// rewrite an already-applied native Codex project in memory or on disk.
/// </summary>
public sealed record NativeCodexAppliedSnapshot(
    Guid ProfileId,
    string ProfileName,
    string MainModel,
    string MainReasoningEffort,
    string WorkerKind,
    string? WorkerRole,
    string? WorkerModel,
    string? ProviderId,
    string WorkerReasoningEffort,
    int MaxWorkers,
    string RoutingMode,
    string ValidationStatus,
    string ConfigurationFingerprint);
