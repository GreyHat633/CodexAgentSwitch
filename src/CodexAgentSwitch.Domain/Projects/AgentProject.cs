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
    bool OriginalConfigurationExisted);
