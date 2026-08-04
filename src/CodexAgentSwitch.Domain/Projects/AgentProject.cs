namespace CodexAgentSwitch.Domain.Projects;

public sealed record AgentProject(
    string Id,
    string Name,
    string WorkingDirectory,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
