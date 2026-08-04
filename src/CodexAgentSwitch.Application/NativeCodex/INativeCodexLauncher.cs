using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Application.NativeCodex;

public sealed record NativeCodexLaunchResult(
    int ProcessId,
    string Executable,
    string WorkingDirectory,
    string GeneratedConfigurationPath,
    string Summary);

public interface INativeCodexLauncher
{
    Task<NativeCodexLaunchResult> LaunchAsync(
        Profile profile,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
