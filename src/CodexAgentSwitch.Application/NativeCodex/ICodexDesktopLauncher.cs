using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;

namespace CodexAgentSwitch.Application.NativeCodex;

public sealed record CodexDesktopAppDiscovery(
    bool IsAvailable,
    string Status,
    string? AppUserModelId,
    string? ExecutablePath,
    bool IsManualEntry);

public sealed record CodexDesktopLaunchResult(
    string LaunchTarget,
    string WorkingDirectory,
    string ConfigurationPath,
    bool RequiresExternalCredentialSetup,
    string Summary);

public sealed record NativeProjectAdaptationResult(
    AgentProject Project,
    bool Succeeded,
    bool Changed,
    string ConfigurationPath,
    string? BackupPath,
    string Summary,
    string? ErrorMessage = null);

public sealed record CodexDesktopBatchLaunchResult(
    IReadOnlyList<NativeProjectAdaptationResult> Projects,
    bool DesktopStarted,
    string? LaunchTarget,
    string? LaunchError);

public interface ICodexDesktopLauncher
{
    Task<CodexDesktopAppDiscovery> DetectAsync(CancellationToken cancellationToken = default);

    Task SaveManualExecutableAsync(string executablePath, CancellationToken cancellationToken = default);

    Task<CodexDesktopLaunchResult> LaunchAsync(
        Profile profile,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task<CodexDesktopBatchLaunchResult> ApplyToProjectsAndLaunchAsync(
        Profile profile,
        IReadOnlyList<AgentProject> projects,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NativeProjectAdaptationResult>> ApplyToProjectsAsync(
        Profile profile,
        IReadOnlyList<AgentProject> projects,
        CancellationToken cancellationToken = default);

    Task<NativeProjectAdaptationResult> RestoreProjectConfigurationAsync(
        AgentProject project,
        CancellationToken cancellationToken = default);
}
