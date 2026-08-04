using System.Diagnostics;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed record CodexCommandDiscovery(CodexCommand? Command, string? Version, string Status, IReadOnlyList<string> Attempts)
{
    public bool IsAvailable => Command is not null;
}

public class CodexCommandLocator
{
    public virtual async Task<CodexCommandDiscovery> LocateAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new List<CodexCommand>();
        var configuredExecutable = Environment.GetEnvironmentVariable("CAS_CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            candidates.Add(CodexCommand.Direct(configuredExecutable));
        }

        var configuredScript = Environment.GetEnvironmentVariable("CAS_CODEX_CLI_JS");
        if (!string.IsNullOrWhiteSpace(configuredScript))
        {
            candidates.Add(new CodexCommand("node", [configuredScript]));
        }

        candidates.Add(CodexCommand.Direct("codex.exe"));
        candidates.Add(CodexCommand.Direct("codex"));
        var attempts = new List<string>();
        foreach (var candidate in candidates.Distinct())
        {
            var probe = await ProbeAsync(candidate, cancellationToken);
            attempts.Add(probe.Status);
            if (probe.Version is not null)
            {
                return new CodexCommandDiscovery(candidate, probe.Version, "Codex CLI is executable.", attempts);
            }
        }

        return new CodexCommandDiscovery(null, null, "Codex CLI was not executable. Configure CAS_CODEX_EXECUTABLE or CAS_CODEX_CLI_JS.", attempts);
    }

    private static async Task<(string? Version, string Status)> ProbeAsync(CodexCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in command.PrefixArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return (output.Trim(), $"{command.Executable}: {output.Trim()}");
            }

            return (null, $"{command.Executable}: exit {process.ExitCode}; {Sanitize(error)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, $"{command.Executable}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string Sanitize(string value) =>
        value.Length <= 300 ? value.Trim() : value[..300].Trim();
}
