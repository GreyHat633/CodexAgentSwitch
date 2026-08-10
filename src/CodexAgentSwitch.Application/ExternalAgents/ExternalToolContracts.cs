namespace CodexAgentSwitch.Application.ExternalAgents;

public enum ExternalToolPermissionMode
{
    ReadOnly,
    WorkspaceFullAccess,
    FullAccess,
}

public sealed record ExternalToolSession(
    string TaskId,
    string ProjectPath,
    string WorkingDirectory,
    ExternalToolPermissionMode PermissionMode,
    IReadOnlyList<string> AllowedReadScope,
    IReadOnlyList<string> AllowedWriteScope,
    DateTimeOffset StartedAt);

public sealed record ExternalToolExecutionRequest(
    string ToolCallId,
    string ToolName,
    string Arguments);

public sealed record ExternalToolExecutionResult(
    string ToolCallId,
    string ToolName,
    string StandardOutput,
    string StandardError,
    int? ExitCode,
    bool TimedOut,
    bool Denied,
    bool Truncated,
    IReadOnlyList<string>? ChangedFiles = null)
{
    public bool Succeeded => !Denied && !TimedOut && ExitCode == 0;
}

public interface IExternalToolHost
{
    Task<ExternalToolExecutionResult> ExecuteAsync(
        ExternalToolSession session,
        ExternalToolExecutionRequest request,
        CancellationToken cancellationToken = default);
}
