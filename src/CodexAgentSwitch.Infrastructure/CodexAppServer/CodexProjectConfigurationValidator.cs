using System.Diagnostics;
using System.Text;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

/// <summary>
/// Validates a candidate project configuration without ever placing that
/// candidate in a user project.  The current Codex executable is the source
/// of truth here: it performs both TOML parsing and Codex configuration
/// loading with an isolated, E-drive CODEX_HOME.
/// </summary>
public interface ICodexProjectConfigurationValidator
{
    Task ValidateAsync(CodexCommand command, string candidateToml, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the exact layer combination Codex will see: a user-level
    /// configuration, a trusted project configuration and any project custom
    /// agent files.  Provider declarations deliberately belong only to the
    /// user layer; project TOML never receives them.
    /// </summary>
    Task ValidateLayeredAsync(
        CodexCommand command,
        CodexConfigurationLayers candidate,
        CancellationToken cancellationToken = default) =>
        ValidateAsync(command, candidate.ProjectToml, cancellationToken);
}

public sealed record CodexConfigurationLayers(
    string ProjectToml,
    string? UserToml = null,
    IReadOnlyDictionary<string, string>? ProjectFiles = null);

public sealed record CodexProjectConfigurationReport(
    bool HooksPresent,
    bool PreToolUseConfigured,
    string ReviewNotice)
{
    public const string UserControlledReviewNotice =
        "Project trust and exact hook-command review remain user-controlled; Agent Switch never auto-grants trust or hook hash trust.";
}

public sealed class CodexProjectConfigurationValidator(AppDataPaths paths) : ICodexProjectConfigurationValidator
{
    public static CodexProjectConfigurationReport ReportHooks(IReadOnlyDictionary<string, string>? projectFiles)
    {
        var hooks = projectFiles?.TryGetValue("hooks.json", out var value) == true ? value : null;
        var configured = hooks?.Contains("PreToolUse", StringComparison.OrdinalIgnoreCase) == true
            && hooks.Contains("commandWindows", StringComparison.OrdinalIgnoreCase);
        return new(hooks is not null, configured, CodexProjectConfigurationReport.UserControlledReviewNotice);
    }

    public async Task ValidateAsync(CodexCommand command, string candidateToml, CancellationToken cancellationToken = default)
    {
        await ValidateLayeredAsync(command, new CodexConfigurationLayers(candidateToml), cancellationToken);
    }

    public async Task ValidateLayeredAsync(
        CodexCommand command,
        CodexConfigurationLayers candidate,
        CancellationToken cancellationToken = default)
    {
        var validationRoot = Path.Combine(paths.NativeCodexDirectory, "config-validation", Guid.NewGuid().ToString("N"));
        var processTemp = Path.Combine(paths.Root, "process-temp", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(validationRoot);
            Directory.CreateDirectory(processTemp);
            var codexHome = Path.Combine(validationRoot, "codex-home");
            var projectRoot = Path.Combine(validationRoot, "project");
            var projectCodexDirectory = Path.Combine(projectRoot, ".codex");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(projectCodexDirectory);
            // Codex 0.146 gates project-local config behind an explicit trust
            // entry recorded in CODEX_HOME/config.toml. Without it the strict
            // load below rejects the candidate before parsing it, so the
            // isolated home always trusts exactly the isolated project root.
            var userConfiguration = (candidate.UserToml ?? string.Empty).TrimEnd();
            userConfiguration = string.Concat(
                userConfiguration,
                Environment.NewLine,
                Environment.NewLine,
                $"[projects.'{ToTrustedProjectKey(projectRoot)}']",
                Environment.NewLine,
                "trust_level = \"trusted\"");
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "config.toml"),
                userConfiguration,
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(projectCodexDirectory, "config.toml"),
                candidate.ProjectToml,
                new UTF8Encoding(false),
                cancellationToken);
            foreach (var file in candidate.ProjectFiles ?? new Dictionary<string, string>())
            {
                var relative = file.Key.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative)
                    || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or ".."))
                {
                    throw new InvalidOperationException("候选 Codex 自定义代理文件路径无效。");
                }

                var path = Path.Combine(projectCodexDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Value, new UTF8Encoding(false), cancellationToken);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var prefix in command.PrefixArguments)
            {
                startInfo.ArgumentList.Add(prefix);
            }

            // "off" forces app-server to parse and strictly load the
            // configuration before it returns its documented no-transport
            // sentinel. It never opens a task transport or contacts a model.
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(projectRoot);
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--strict-config");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("off");
            startInfo.Environment["CODEX_HOME"] = codexHome;
            // Codex refuses to bootstrap helper aliases when CODEX_HOME itself
            // is under TEMP. Keep both locations on E:, but as siblings.
            startInfo.Environment["TEMP"] = processTemp;
            startInfo.Environment["TMP"] = processTemp;

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Codex 配置验证进程。");
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
                var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
                var detail = RedactAndTrim(string.Concat(standardOutput, Environment.NewLine, standardError));
                if (detail.Contains("no transport configured", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "Codex 未能加载候选项目配置。"
                    : $"Codex 未能加载候选项目配置：{detail}");
            }
            finally
            {
                process.Dispose();
            }
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(validationRoot);
            await DeleteOwnedDirectoryAsync(processTemp);
        }
    }

    private static string ToTrustedProjectKey(string path) =>
        path.Replace('/', '\\').ToLowerInvariant();

    private static string RedactAndTrim(string detail)
    {
        var compact = detail.Trim();
        return compact.Length <= 800 ? compact : compact[..800];
    }

    private static async Task DeleteOwnedDirectoryAsync(string path)
    {
        // Codex briefly retains SQLite/WAL handles while app-server shuts down
        // on Windows. A bounded retry keeps validation cleanup deterministic
        // without replacing the actual validation outcome.
        for (var attempt = 0; attempt < 12 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 11)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(125));
            }
            catch (UnauthorizedAccessException) when (attempt < 11)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(125));
            }
        }
    }
}
