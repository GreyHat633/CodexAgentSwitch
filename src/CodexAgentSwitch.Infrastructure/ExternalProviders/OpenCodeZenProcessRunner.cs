using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed record OpenCodeProcessResult(int ExitCode, string StandardOutput, string StandardError);
public sealed record OpenCodeProbeResult(bool IsAvailable, bool IsAuthenticated, string Message);

public interface IOpenCodeProcessRunner
{
    Task<OpenCodeProbeResult> ProbeAsync(string workingDirectory, CancellationToken cancellationToken = default);

    Task<OpenCodeProcessResult> RunAsync(
        string workingDirectory,
        string model,
        string prompt,
        CancellationToken cancellationToken = default);
}

public sealed class OpenCodeZenProcessRunner : IOpenCodeProcessRunner
{
    public async Task<OpenCodeProbeResult> ProbeAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        OpenCodeProcessResult result;
        try
        {
            result = await RunCommandAsync(workingDirectory, ["auth", "list"], cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return new OpenCodeProbeResult(false, false, exception.Message);
        }

        return ClassifyAuthResult(result);
    }

    public static OpenCodeProbeResult ClassifyAuthResult(OpenCodeProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            return new OpenCodeProbeResult(true, false,
                $"OpenCode CLI 登录探测失败（退出码 {result.ExitCode}）；请运行 'opencode auth login' 后重试。");
        }

        var output = Regex.Replace($"{result.StandardOutput}\n{result.StandardError}", "\\x1B\\[[0-9;]*[A-Za-z]", string.Empty).Trim();
        var lower = output.ToLowerInvariant();
        var providerLines = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains("auth.json", StringComparison.OrdinalIgnoreCase)
                && !line.Equals("Credentials", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(line, "^0\\s+credentials?$", RegexOptions.IgnoreCase))
            .ToArray();
        var hasZenProvider = providerLines.Any(line =>
            line.Contains("OpenCode Zen", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(line, "(^|\\W)opencode($|\\W)", RegexOptions.IgnoreCase));
        var notAuthenticated = string.IsNullOrWhiteSpace(output)
            || lower.Contains("not logged", StringComparison.Ordinal)
            || lower.Contains("not authenticated", StringComparison.Ordinal)
            || lower.Contains("no credentials", StringComparison.Ordinal)
            || lower.Contains("no authentication", StringComparison.Ordinal)
            || Regex.IsMatch(output, "(^|\\n)\\s*0\\s+credentials?\\s*($|\\n)", RegexOptions.IgnoreCase)
            || !hasZenProvider;
        return notAuthenticated
            ? new OpenCodeProbeResult(true, false, "OpenCode CLI 已安装，但未找到 OpenCode 登录。请运行 'opencode auth login'。")
            : new OpenCodeProbeResult(true, true, "OpenCode CLI 已安装，已找到现有登录。");
    }

    public async Task<OpenCodeProcessResult> RunAsync(
        string workingDirectory,
        string model,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        return await RunCommandAsync(workingDirectory, ["run", "--auto", "--model", model, prompt], cancellationToken);
    }

    private static async Task<OpenCodeProcessResult> RunCommandAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "opencode",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 OpenCode CLI；请安装 'opencode' 并确保它位于 PATH 中。");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            });
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            return new OpenCodeProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("PATH 中缺少或无法使用 OpenCode CLI。", exception);
        }
    }

}
