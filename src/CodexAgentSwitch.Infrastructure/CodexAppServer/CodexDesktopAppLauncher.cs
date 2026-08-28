using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Win32;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public interface ICodexDesktopProcessStarter
{
    void StartAppsFolder(string appUserModelId);

    void StartExecutable(string executablePath);
}

public interface ICodexDesktopAppRegistration
{
    string? FindAppUserModelId();
}

public sealed class RegistryCodexDesktopAppRegistration : ICodexDesktopAppRegistration
{
    public string? FindAppUserModelId()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\ActivatableClasses\Package");
        var package = packages?.GetSubKeyNames()
            .Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (package is null)
        {
            return null;
        }

        var marker = package.LastIndexOf("__", StringComparison.Ordinal);
        if (marker < 0 || marker + 2 >= package.Length)
        {
            return null;
        }

        return $"OpenAI.Codex_{package[(marker + 2)..]}!App";
    }
}

public sealed class CodexDesktopProcessStarter : ICodexDesktopProcessStarter
{
    public void StartAppsFolder(string appUserModelId)
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{appUserModelId}",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动 Windows 应用列表中的 Codex 桌面应用。");
    }

    public void StartExecutable(string executablePath)
    {
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("无法启动指定的 Codex 桌面应用入口。");
    }
}

public sealed class CodexDesktopAppLauncher(
    AppDataPaths paths,
    ICodexDesktopAppRegistration registration,
    ICodexDesktopProcessStarter processStarter,
    CodexCommandLocator locator,
    ICodexModelResolver modelResolver,
    ICodexProjectConfigurationValidator configurationValidator) : ICodexDesktopLauncher
{
    private const string AutoCompactTokenLimitKey = "model_auto_compact_token_limit";
    private const string ManagedStart = "# >>> Codex Agent Switch managed profile >>>";
    private const string ManagedEnd = "# <<< Codex Agent Switch managed profile <<<";
    private const string ProjectInstructionsStart = "<!-- >>> Codex Agent Switch managed native worker routing >>> -->";
    private const string ProjectInstructionsEnd = "<!-- <<< Codex Agent Switch managed native worker routing <<< -->";
    private const string ProjectInstructionsFile = "AGENTS.md";
    private const string WorkerMarker = "# >>> Codex Agent Switch worker >>>";
    private const string ExternalWorkerMarker = "# >>> Codex Agent Switch external worker >>>";
    private static readonly string[] ManagedWorkerAgentFiles =
    [
        "agents/cas-sol-worker.toml",
        "agents/cas-terra-worker.toml",
        "agents/cas-luna-worker.toml",
        "agents/cas-external-worker.toml",
    ];
    private const string DesktopEntryFile = "desktop-entry.json";
    private const string HooksFile = "hooks.json";

    public async Task<CodexDesktopAppDiscovery> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manual = await ReadManualExecutableAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(manual))
        {
            return File.Exists(manual)
                ? new(true, "已使用手动指定的 Codex 桌面应用入口。", null, manual, true)
                : new(false, "手动指定的 Codex 桌面应用入口已不存在，请在设置页重新选择。", null, manual, true);
        }

        var environmentEntry = Environment.GetEnvironmentVariable("CAS_CODEX_DESKTOP_APP");
        if (!string.IsNullOrWhiteSpace(environmentEntry))
        {
            var normalized = Path.GetFullPath(environmentEntry);
            return IsCli(normalized)
                ? new(false, "CAS_CODEX_DESKTOP_APP 指向的是 Codex CLI；请改为图形桌面应用入口。", null, normalized, true)
                : File.Exists(normalized)
                    ? new(true, "已检测到 CAS_CODEX_DESKTOP_APP 指定的桌面应用。", null, normalized, true)
                    : new(false, "CAS_CODEX_DESKTOP_APP 指向的桌面应用不存在。", null, normalized, true);
        }

        var appUserModelId = registration.FindAppUserModelId();
        return appUserModelId is null
            ? new(false, "未检测到官方 Codex 图形桌面应用。可在“设置”中手动指定 ChatGPT.exe/Codex 桌面应用入口；不会自动改用 CLI。", null, null, false)
            : new(true, "已检测到官方 Codex 图形桌面应用（Windows AppUserModelId）。", appUserModelId, null, false);
    }

    public async Task SaveManualExecutableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("请填写 Codex 图形桌面应用的可执行文件路径。");
        }

        var normalized = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(normalized))
        {
            throw new FileNotFoundException("找不到指定的 Codex 桌面应用入口。", normalized);
        }

        if (IsCli(normalized))
        {
            throw new InvalidOperationException("Codex CLI 不能作为“原生 Codex 模式”的桌面应用入口。请指定官方图形应用（例如 ChatGPT.exe）。");
        }

        paths.EnsureCreated();
        var destination = Path.Combine(paths.NativeCodexDirectory, DesktopEntryFile);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new ManualDesktopEntry(normalized)), new UTF8Encoding(false), cancellationToken);
    }

    public async Task<string> LaunchDesktopAsync(CancellationToken cancellationToken = default)
    {
        var discovery = await DetectAsync(cancellationToken);
        if (!discovery.IsAvailable)
        {
            throw new InvalidOperationException(discovery.Status);
        }

        return StartDesktop(discovery);
    }

    public async Task<CodexDesktopLaunchResult> LaunchAsync(
        Profile profile,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var discovery = await DetectAsync(cancellationToken);
        if (!discovery.IsAvailable)
        {
            throw new InvalidOperationException(discovery.Status);
        }

        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException($"项目工作目录不存在：{cwd}");
        }

        if (Path.GetPathRoot(cwd)?.Equals("C:\\", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("为遵守本机存储策略，不能向 C 盘项目写入 Codex 配置。请将项目工作目录设置为 E 盘后重试。");
        }

        var command = (await locator.LocateAsync(cancellationToken)).Command
            ?? throw new InvalidOperationException("Codex CLI 未就绪，无法校验当前 Profile 的主代理。请先在设置页修复 Codex CLI。");
        await modelResolver.ResolveAsync(command, profile.MainAgent.ModelId, cancellationToken);
        var worker = EffectiveWorkerDefinition.Resolve(profile.WorkerPolicy);
        if (worker.Kind == EffectiveWorkerKind.NativeAgent && worker.Capability == WorkerExecutionCapability.Supported)
        {
            await modelResolver.ResolveAsync(
                command,
                worker.ModelId ?? throw new InvalidOperationException("原生 Worker 配置无效。"),
                cancellationToken);
        }

        var configuration = await WriteProjectConfigurationAsync(profile, cwd, null, command, cancellationToken);
        var target = StartDesktop(discovery);
        var summary = configuration.UserAutoCompactTokenLimitPreserved
            ? "已写入项目级 Codex 配置并启动官方图形桌面应用。该项目已存在用户自定义的自动压缩阈值，CAS 已保留该值，方案中的上下文压缩档位未覆盖它。请在桌面应用中新建或重新加载该项目对话后使用新配置；当前已运行的对话可能继续使用旧值。"
            : "已写入项目级 Codex 配置并启动官方图形桌面应用。请在桌面应用中新建或重新加载该项目对话后使用新配置；当前已运行的对话可能继续使用旧值。";
        return new CodexDesktopLaunchResult(target, cwd, configuration.Path, false, summary);
    }

    public async Task<CodexDesktopBatchLaunchResult> ApplyToProjectsAndLaunchAsync(
        Profile profile,
        IReadOnlyList<AgentProject> projects,
        CancellationToken cancellationToken = default)
    {
        if (projects.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一个要适配的项目。");
        }

        var discovery = await DetectAsync(cancellationToken);
        if (!discovery.IsAvailable)
        {
            throw new InvalidOperationException(discovery.Status);
        }

        var results = await ApplyToProjectsAsync(profile, projects, cancellationToken);
        if (!results.Any(result => result.Succeeded))
        {
            return new CodexDesktopBatchLaunchResult(results, false, null, "没有项目成功适配，因此未启动 Codex 桌面应用。");
        }

        try
        {
            var target = StartDesktop(discovery);
            return new CodexDesktopBatchLaunchResult(results, true, target, null);
        }
        catch (Exception exception)
        {
            return new CodexDesktopBatchLaunchResult(results, false, null, exception.Message);
        }
    }

    public async Task<IReadOnlyList<NativeProjectAdaptationResult>> ApplyToProjectsAsync(
        Profile profile,
        IReadOnlyList<AgentProject> projects,
        CancellationToken cancellationToken = default)
    {
        if (projects.Count == 0)
        {
            throw new InvalidOperationException("请至少选择一个要适配的项目。");
        }

        var command = (await locator.LocateAsync(cancellationToken)).Command
            ?? throw new InvalidOperationException("Codex CLI 未就绪，无法验证项目配置。请先在设置页修复 Codex CLI。");
        await ValidateNativeModelsAsync(profile, command, cancellationToken);

        var results = new List<NativeProjectAdaptationResult>(projects.Count);
        foreach (var project in projects)
        {
            try
            {
                var cwd = ValidateProjectDirectory(project.WorkingDirectory);
                var write = await WriteProjectConfigurationAsync(profile, cwd, project.Id, command, cancellationToken);
                results.Add(new NativeProjectAdaptationResult(
                    project,
                    true,
                    write.Changed,
                    write.Path,
                    write.BackupPath,
                    BuildAdaptationSummary(write.Changed, write.UserAutoCompactTokenLimitPreserved),
                    ConfigurationFingerprint: write.ConfigurationFingerprint));
            }
            catch (Exception exception)
            {
                var configurationPath = Path.Combine(project.WorkingDirectory, ".codex", "config.toml");
                results.Add(new NativeProjectAdaptationResult(
                    project,
                    false,
                    false,
                    configurationPath,
                    null,
                    "未写入配置。",
                    exception.Message));
            }
        }

        return results;
    }

    public async Task<NativeProjectAdaptationResult> RestoreProjectConfigurationAsync(
        AgentProject project,
        CancellationToken cancellationToken = default)
    {
        var adaptation = project.NativeCodexAdaptation
            ?? throw new InvalidOperationException("该项目没有可恢复的 Agent Switch 配置记录。");
        var configurationPath = adaptation.ConfigurationPath;
        try
        {
            if (!string.IsNullOrWhiteSpace(adaptation.BackupPath) && File.Exists(adaptation.BackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
                File.Copy(adaptation.BackupPath, configurationPath, true);
            }
            else if (File.Exists(configurationPath))
            {
                var existing = await File.ReadAllTextAsync(configurationPath, cancellationToken);
                var restored = RemoveManagedBlock(existing);
                if (string.IsNullOrWhiteSpace(restored))
                {
                    File.Delete(configurationPath);
                }
                else
                {
                    await File.WriteAllTextAsync(configurationPath, restored, new UTF8Encoding(false), cancellationToken);
                }
            }

            var projectInstructionsPath = Path.Combine(project.WorkingDirectory, ProjectInstructionsFile);
            var backupDirectory = string.IsNullOrWhiteSpace(adaptation.BackupPath)
                ? null
                : Path.GetDirectoryName(adaptation.BackupPath);
            var instructionsBackupPath = backupDirectory is null
                ? null
                : Path.Combine(backupDirectory, ProjectInstructionsFile);
            var instructionsWereMissing = backupDirectory is not null
                && File.Exists(Path.Combine(backupDirectory, $"{ProjectInstructionsFile}.missing"));
            var hooksPath = Path.Combine(project.WorkingDirectory, ".codex", HooksFile);
            var hooksBackupPath = backupDirectory is null ? null : Path.Combine(backupDirectory, HooksFile);
            var hooksWereMissing = backupDirectory is not null && File.Exists(Path.Combine(backupDirectory, $"{HooksFile}.missing"));
            if (instructionsBackupPath is not null && File.Exists(instructionsBackupPath))
            {
                File.Copy(instructionsBackupPath, projectInstructionsPath, true);
            }
            else if (instructionsWereMissing)
            {
                if (File.Exists(projectInstructionsPath))
                {
                    File.Delete(projectInstructionsPath);
                }
            }
            else if (File.Exists(projectInstructionsPath))
            {
                var existingInstructions = await File.ReadAllTextAsync(projectInstructionsPath, cancellationToken);
                var restoredInstructions = ReplaceManagedProjectInstructions(existingInstructions, null);
                await WriteOrRemoveManagedProjectInstructionsAsync(projectInstructionsPath, restoredInstructions, cancellationToken);
            }

            if (hooksBackupPath is not null && File.Exists(hooksBackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
                File.Copy(hooksBackupPath, hooksPath, true);
            }
            else if (hooksWereMissing && File.Exists(hooksPath))
            {
                File.Delete(hooksPath);
            }

            return new NativeProjectAdaptationResult(project, true, true, configurationPath, adaptation.BackupPath, "已恢复写入前的项目配置。");
        }
        catch (Exception exception)
        {
            return new NativeProjectAdaptationResult(project, false, false, configurationPath, adaptation.BackupPath, "未能恢复项目配置。", exception.Message);
        }
    }

    private async Task<ProjectConfigurationWrite> WriteProjectConfigurationAsync(
        Profile profile,
        string workingDirectory,
        string? projectId,
        CodexCommand command,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(workingDirectory, ".codex");
        var configurationPath = Path.Combine(directory, "config.toml");
        var projectInstructionsPath = Path.Combine(workingDirectory, ProjectInstructionsFile);
        var existing = File.Exists(configurationPath)
            ? await File.ReadAllTextAsync(configurationPath, cancellationToken)
            : null;
        var existingProjectInstructions = File.Exists(projectInstructionsPath)
            ? await File.ReadAllTextAsync(projectInstructionsPath, cancellationToken)
            : null;
        var hooksPath = Path.Combine(directory, HooksFile);
        var existingHooks = File.Exists(hooksPath)
            ? await File.ReadAllTextAsync(hooksPath, cancellationToken)
            : null;
        var worker = EffectiveWorkerDefinition.Resolve(profile.WorkerPolicy);
        var managedAgent = worker.Kind == EffectiveWorkerKind.NativeAgent && worker.CanRunInNativeCodex
            ? BuildNativeAgentConfiguration(worker)
            : null;
        var userOwnedConfiguration = IsolateUserOwnedConfiguration(existing);
        var userAutoCompactTokenLimitPreserved = HasUserOwnedAutoCompactTokenLimit(userOwnedConfiguration);
        var block = BuildManagedConfiguration(profile, worker, !userAutoCompactTokenLimitPreserved);
        var next = MergeManagedConfiguration(userOwnedConfiguration, block);
        var nextProjectInstructions = ReplaceManagedProjectInstructions(
            existingProjectInstructions,
            BuildManagedProjectInstructions(worker));
        var nextHooks = RemoveManagedHooks(existingHooks);
        var existingAgents = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var relativePath in ManagedWorkerAgentFiles)
        {
            var path = Path.Combine(directory, relativePath);
            existingAgents[relativePath] = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        var projectChanged = !string.Equals(existing, next, StringComparison.Ordinal);
        var projectInstructionsChanged = !string.Equals(existingProjectInstructions, nextProjectInstructions, StringComparison.Ordinal);
        var hooksChanged = !string.Equals(existingHooks, nextHooks, StringComparison.Ordinal);
        var agentChanged = HasWorkerAgentChanges(existingAgents, worker.ConfigFile, managedAgent);
        if (!projectChanged && !projectInstructionsChanged && !agentChanged && !hooksChanged)
        {
            return new ProjectConfigurationWrite(
                configurationPath,
                false,
                false,
                null,
                existing is not null,
                Fingerprint(next),
                userAutoCompactTokenLimitPreserved);
        }

        // The candidate lives only beside its target long enough to ensure the
        // replacement is same-directory/atomic. It is parsed and loaded by the
        // installed Codex before any project file or backup is touched.
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"config.toml.cas-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, next, new UTF8Encoding(false), cancellationToken);
            await configurationValidator.ValidateLayeredAsync(
                command,
                new CodexConfigurationLayers(
                    next,
                    null,
                    BuildValidationProjectFiles(worker, managedAgent, nextHooks)),
                cancellationToken);

            string? backupPath = null;
            if (projectChanged || projectInstructionsChanged || agentChanged || hooksChanged)
            {
                var backupDirectory = Path.Combine(
                    paths.NativeCodexDirectory,
                    "project-config-backups",
                    projectId ?? "standalone",
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(backupDirectory);
                backupPath = Path.Combine(backupDirectory, "config.toml");
                if (existing is not null)
                {
                    await File.WriteAllTextAsync(backupPath, existing, new UTF8Encoding(false), cancellationToken);
                }

                var instructionsBackupPath = Path.Combine(backupDirectory, ProjectInstructionsFile);
                if (existingProjectInstructions is null)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(backupDirectory, $"{ProjectInstructionsFile}.missing"),
                        string.Empty,
                        new UTF8Encoding(false),
                        cancellationToken);
                }
                else
                {
                    await File.WriteAllTextAsync(instructionsBackupPath, existingProjectInstructions, new UTF8Encoding(false), cancellationToken);
                }
                if (existingHooks is null)
                {
                    await File.WriteAllTextAsync(Path.Combine(backupDirectory, $"{HooksFile}.missing"), string.Empty, new UTF8Encoding(false), cancellationToken);
                }
                else
                {
                    await File.WriteAllTextAsync(Path.Combine(backupDirectory, HooksFile), existingHooks, new UTF8Encoding(false), cancellationToken);
                }
            }

            var hooksMutationApplied = false;
            try
            {
                foreach (var relativePath in ManagedWorkerAgentFiles)
                {
                    var path = Path.Combine(directory, relativePath);
                    if (string.Equals(relativePath, worker.ConfigFile, StringComparison.Ordinal) && managedAgent is not null)
                    {
                        await WriteManagedWorkerAgentAsync(path, managedAgent, cancellationToken);
                    }
                    else
                    {
                        await RemoveManagedWorkerAgentAsync(path, cancellationToken);
                    }
                }

                if (projectInstructionsChanged)
                {
                    await WriteOrRemoveManagedProjectInstructionsAsync(
                        projectInstructionsPath,
                        nextProjectInstructions,
                        cancellationToken);
                }

                if (hooksChanged)
                {
                    var currentHooks = File.Exists(hooksPath)
                        ? await File.ReadAllTextAsync(hooksPath, cancellationToken)
                        : null;
                    if (!string.Equals(existingHooks, currentHooks, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Existing .codex/hooks.json changed while CAS was preparing its migration; refusing to overwrite concurrent changes.");
                    }

                    if (nextHooks is null)
                    {
                        if (File.Exists(hooksPath)) File.Delete(hooksPath);
                    }
                    else
                    {
                        await WriteTextAtomicallyAsync(hooksPath, nextHooks, cancellationToken);
                    }
                    hooksMutationApplied = true;
                }

                if (projectChanged)
                {
                    File.Move(temporaryPath, configurationPath, overwrite: true);
                }
            }
            catch (Exception exception)
            {
                foreach (var relativePath in ManagedWorkerAgentFiles)
                {
                    var original = existingAgents[relativePath];
                    await RestoreOriginalFileAsync(
                        Path.Combine(directory, relativePath),
                        original,
                        original is null,
                        cancellationToken);
                }

                await RestoreOriginalFileAsync(
                    projectInstructionsPath,
                    existingProjectInstructions is null ? null : Encoding.UTF8.GetBytes(existingProjectInstructions),
                    existingProjectInstructions is null,
                    cancellationToken);
                if (hooksMutationApplied)
                {
                    var currentHooks = File.Exists(hooksPath)
                        ? await File.ReadAllTextAsync(hooksPath, cancellationToken)
                        : null;
                    if (!string.Equals(currentHooks, nextHooks, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "CAS could not roll back .codex/hooks.json because it changed after migration; the backup was retained for manual recovery.",
                            exception);
                    }

                    await RestoreOriginalFileAsync(
                        hooksPath,
                        existingHooks is null ? null : Encoding.UTF8.GetBytes(existingHooks),
                        existingHooks is null,
                        cancellationToken);
                }
                throw;
            }

            return new ProjectConfigurationWrite(
                configurationPath,
                false,
                projectChanged || projectInstructionsChanged || agentChanged || hooksChanged,
                backupPath,
                existing is not null,
                Fingerprint(next),
                userAutoCompactTokenLimitPreserved);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildManagedConfiguration(
        Profile profile,
        EffectiveWorkerDefinition worker,
        bool allowProfileAutoCompactTokenLimit)
    {
        var approval = ExecutionApprovalPolicy.Resolve(profile.ApprovalMode);
        var builder = new StringBuilder()
            .AppendLine(ManagedStart)
            .AppendLine("# Generated from the active Codex Agent Switch profile. Do not place credentials in this file.")
            .AppendLine($"model = {Toml(profile.MainAgent.ModelId)}")
            .AppendLine($"model_reasoning_effort = {Toml(profile.MainAgent.ReasoningEffort)}");

        if (allowProfileAutoCompactTokenLimit && profile.AutoCompactTokenLimit is int autoCompactTokenLimit)
        {
            builder.AppendLine($"{AutoCompactTokenLimitKey} = {autoCompactTokenLimit}");
        }

        builder.AppendLine($"approval_policy = {Toml(approval.ApprovalPolicy)}")
            .AppendLine($"sandbox_mode = {Toml(approval.SandboxMode)}")
            .AppendLine($"agents.enabled = {(worker.CanRunInNativeCodex && worker.Kind == EffectiveWorkerKind.NativeAgent ? "true" : "false")}");

        if (worker.CanRunInNativeCodex && worker.Kind == EffectiveWorkerKind.NativeAgent)
        {
            builder.AppendLine($"agents.max_concurrent_threads_per_session = {worker.MaxWorkers}")
                .AppendLine()
                .AppendLine($"[agents.{worker.AgentRole}]")
                .AppendLine($"description = {Toml($"Configured native worker role {worker.AgentRole}; use only for bounded delegated work.")}")
                .AppendLine($"config_file = {Toml($"./{worker.ConfigFile!.Replace('\\', '/')}")}");
        }
        else if (worker.Kind == EffectiveWorkerKind.ExternalAgent)
        {
            builder.AppendLine($"# Native external collaboration remains gated: {worker.CapabilityMessage}");
        }

        if (profile.WorkerPolicy.Enabled && profile.WorkerPolicy.Source != WorkerSource.Disabled)
        {
            var toolHost = Path.Combine(AppContext.BaseDirectory, "ToolHost", "CodexAgentSwitch.ToolHost.exe");
            builder.AppendLine()
                .AppendLine("[mcp_servers.codex_agent_switch]")
                .AppendLine($"command = {Toml(toolHost)}")
                .AppendLine($"args = [{Toml("--pipe")}, {Toml(SchedulerEndpoint.PipeName)}]")
                .AppendLine("startup_timeout_sec = 5")
                .AppendLine("tool_timeout_sec = 7200")
                .AppendLine("enabled = true")
                .AppendLine("required = true");

        }

        return builder.AppendLine(ManagedEnd).ToString();
    }

    private static string? IsolateUserOwnedConfiguration(string? existing)
    {
        if (existing is null)
        {
            return null;
        }

        if (existing.Contains(ManagedStart, StringComparison.Ordinal))
        {
            return RemoveManagedBlock(existing);
        }

        if (existing.Contains(ManagedEnd, StringComparison.Ordinal))
        {
            throw AutoCompactMergeException();
        }

        return existing;
    }

    private static string MergeManagedConfiguration(string? userOwned, string block)
    {
        if (string.IsNullOrWhiteSpace(userOwned))
        {
            return block;
        }

        var firstTable = Regex.Match(userOwned, @"(?m)^[\t ]*\[");
        if (!firstTable.Success)
        {
            return string.Concat(userOwned.TrimEnd(), Environment.NewLine, Environment.NewLine, block);
        }

        var topLevel = userOwned[..firstTable.Index].TrimEnd();
        var tables = userOwned[firstTable.Index..].TrimStart('\r', '\n');
        return topLevel.Length == 0
            ? string.Concat(block, Environment.NewLine, tables)
            : string.Concat(topLevel, Environment.NewLine, Environment.NewLine, block, Environment.NewLine, tables);
    }

    private static bool HasUserOwnedAutoCompactTokenLimit(string? userOwned)
    {
        if (string.IsNullOrWhiteSpace(userOwned))
        {
            return false;
        }

        if (userOwned.Contains(ManagedStart, StringComparison.Ordinal)
            || userOwned.Contains(ManagedEnd, StringComparison.Ordinal))
        {
            throw AutoCompactMergeException();
        }

        var topLevel = true;
        var definitions = 0;
        string? multilineDelimiter = null;
        foreach (var rawLine in userOwned.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (multilineDelimiter is not null)
            {
                if (rawLine.Contains(AutoCompactTokenLimitKey, StringComparison.Ordinal))
                {
                    throw AutoCompactMergeException();
                }

                if (rawLine.Contains(multilineDelimiter, StringComparison.Ordinal))
                {
                    multilineDelimiter = null;
                }
                continue;
            }

            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                topLevel = false;
                var header = line.Trim('[', ']', ' ', '\t');
                if (IsTargetRootKey(header))
                {
                    throw AutoCompactMergeException();
                }
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                if (line.Contains(AutoCompactTokenLimitKey, StringComparison.Ordinal))
                {
                    throw AutoCompactMergeException();
                }
                continue;
            }

            var key = line[..separator].Trim();
            multilineDelimiter = FindUnclosedTomlMultilineDelimiter(line[(separator + 1)..]);
            if (!IsExactTargetKey(key))
            {
                if (IsTargetRootKey(key))
                {
                    throw AutoCompactMergeException();
                }
                continue;
            }

            if (topLevel)
            {
                definitions++;
            }
        }

        if (definitions > 1)
        {
            throw AutoCompactMergeException();
        }

        return definitions == 1;
    }

    private static string? FindUnclosedTomlMultilineDelimiter(string value)
    {
        foreach (var delimiter in new[] { "\"\"\"", "'''" })
        {
            var occurrences = value.Split(delimiter, StringSplitOptions.None).Length - 1;
            if (occurrences % 2 != 0)
            {
                return delimiter;
            }
        }

        return null;
    }

    private static string StripTomlComment(string line)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (quote != '\0')
            {
                if (quote == '"' && current == '\\' && !escaped)
                {
                    escaped = true;
                    continue;
                }

                if (current == quote && !escaped)
                {
                    quote = '\0';
                }
                escaped = false;
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static bool IsExactTargetKey(string key) =>
        string.Equals(key, AutoCompactTokenLimitKey, StringComparison.Ordinal)
        || string.Equals(key, $"\"{AutoCompactTokenLimitKey}\"", StringComparison.Ordinal)
        || string.Equals(key, $"'{AutoCompactTokenLimitKey}'", StringComparison.Ordinal);

    private static bool IsTargetRootKey(string key)
    {
        var root = key.Split('.', 2, StringSplitOptions.TrimEntries)[0];
        return IsExactTargetKey(root);
    }

    private static InvalidOperationException AutoCompactMergeException() =>
        new($"项目配置中已存在无法安全合并的 {AutoCompactTokenLimitKey}。CAS 未修改该项目，请检查 .codex/config.toml。");

    private static string BuildAdaptationSummary(bool changed, bool userAutoCompactTokenLimitPreserved)
    {
        var update = changed ? "项目配置已更新。" : "项目配置已是当前方案。";
        const string reload = "新的自动压缩阈值会在新建或重新加载该项目对话后生效；当前已运行的对话可能继续使用旧值。";
        if (userAutoCompactTokenLimitPreserved)
        {
            return $"{update} 该项目已存在用户自定义的自动压缩阈值。CAS 已保留该值，方案中的上下文压缩档位未覆盖它。{reload}";
        }

        return $"{update} {reload}";
    }

    private static string? RemoveManagedHooks(string? existing)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Preserve an invalid unrelated file by making the managed update
            // explicit; validation will report the candidate rather than
            // silently discarding user data.
            throw new InvalidOperationException("Existing .codex/hooks.json is not valid JSON; refusing to overwrite unrelated hook entries.");
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        var preToolUse = hooks["PreToolUse"] as JsonArray ?? new JsonArray();
        hooks["PreToolUse"] = preToolUse;
        RemoveManagedHookHandlers(preToolUse);
        var postToolUse = hooks["PostToolUse"] as JsonArray ?? new JsonArray();
        hooks["PostToolUse"] = postToolUse;
        RemoveManagedHookHandlers(postToolUse);
        var stop = hooks["Stop"] as JsonArray ?? new JsonArray();
        hooks["Stop"] = stop;
        RemoveManagedHookHandlers(stop);
        if (preToolUse.Count == 0) hooks.Remove("PreToolUse");
        if (postToolUse.Count == 0) hooks.Remove("PostToolUse");
        if (stop.Count == 0) hooks.Remove("Stop");
        if (hooks.Count == 0) root.Remove("hooks");
        return root.Count == 0 ? null : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RemoveManagedHookHandlers(JsonArray groups)
    {
        for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            if (groups[groupIndex] is not JsonObject group) continue;
            if (IsManagedHookHandler(group))
            {
                groups.RemoveAt(groupIndex);
                continue;
            }
            if (group["hooks"] is not JsonArray handlers) continue;
            for (var handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
            {
                if (handlers[handlerIndex] is JsonObject handler && IsManagedHookHandler(handler))
                    handlers.RemoveAt(handlerIndex);
            }
            if (handlers.Count == 0) groups.RemoveAt(groupIndex);
        }
    }

    private static bool IsManagedHookHandler(JsonObject handler) =>
        IsManagedHookCommand(ReadString(handler["command"]))
        || IsManagedHookCommand(ReadString(handler["commandWindows"]));

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsManagedHookCommand(string? command)
    {
        var input = command?.Trim();
        if (string.IsNullOrEmpty(input)) return false;

        string executable;
        string arguments;
        if (input[0] == '"')
        {
            var closingQuote = input.IndexOf('"', 1);
            if (closingQuote <= 1) return false;
            executable = input[1..closingQuote];
            arguments = input[(closingQuote + 1)..];
        }
        else
        {
            var separator = input.IndexOfAny([' ', '\t']);
            executable = separator < 0 ? input : input[..separator];
            arguments = separator < 0 ? string.Empty : input[separator..];
        }

        return string.Equals(Path.GetFileName(executable), "CodexAgentSwitch.ToolHost.exe", StringComparison.OrdinalIgnoreCase)
            && (HasArgumentPair(arguments, "--hook", "pre-tool-use")
                || HasArgumentPair(arguments, "--hook", "post-tool-use")
                || HasArgumentPair(arguments, "--hook", "stop"));
    }

    private static bool HasArgumentPair(string arguments, string name, string value)
    {
        var tokens = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (string.Equals(tokens[index], name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(tokens[index + 1], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string>? BuildValidationProjectFiles(
        EffectiveWorkerDefinition worker,
        string? managedAgent,
        string? hooks)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hooks is not null) files[HooksFile] = hooks;
        if (managedAgent is not null && worker.ConfigFile is not null)
        {
            files[worker.ConfigFile] = managedAgent;
        }
        return files.Count == 0 ? null : files;
    }

    private static string? BuildManagedProjectInstructions(EffectiveWorkerDefinition worker)
    {
        if (worker.Kind == EffectiveWorkerKind.None)
        {
            return null;
        }

        var heading = worker.Kind == EffectiveWorkerKind.NativeAgent
            ? "## Codex Agent Switch managed native worker routing"
            : "## Codex Agent Switch managed external worker routing";
        var backendRouting = worker.Kind switch
        {
            EffectiveWorkerKind.NativeAgent when worker.CanRunInNativeCodex => BuildNativeDelegationInstructions(worker),
            EffectiveWorkerKind.ExternalAgent => BuildExternalWorkerRoutingInstructions(),
            _ => "No backend-specific Worker route is currently available. Keep work with MAIN unless delegation_preflight resolves a runnable Worker.",
        };

        return new StringBuilder()
            .AppendLine(ProjectInstructionsStart)
            .AppendLine(heading)
            .AppendLine()
            .AppendLine(BuildProactiveDelegationPolicy())
            .AppendLine()
            .AppendLine(backendRouting)
            .Append(ProjectInstructionsEnd)
            .ToString();
    }

    private static string BuildNativeDelegationInstructions(EffectiveWorkerDefinition worker) =>
        $"For bounded delegation, call the codex_agent_switch delegate_worker tool with a complete plaintext TaskPacket. When invoking the configured Native Custom Worker, you MUST call spawn_agent with both actual tool arguments: agent_type=\"{worker.AgentRole}\" and fork_turns=\"none\". fork_turns is mandatory for this managed custom role: never omit it, never use fork_turns=\"all\", and never create a full-history fork. While the task is DELEGATED or RUNNING, do not duplicate its work. Report the result through report_worker_result, then perform only bounded review.";

    private static string BuildExternalWorkerRoutingInstructions() =>
        "For a WORKER decision, call codex_agent_switch delegate_worker with the bounded TaskPacket and omit workerId; Agent Switch resolves and starts the applied External Worker in the background. The call returns promptly: do not poll, narrate waiting, or repeat delegation while it is DELEGATED/RUNNING. At the next natural Main boundary, call consume_worker_result once to receive a persisted terminal packet, then review it. Do not invoke a Native Agent for this backend.";

    private static string BuildProactiveDelegationPolicy() => """
        For non-trivial development, run one Delegation Capability Preflight after minimal localization, then make the Initial Delegation Check before substantive implementation. Tiny or read-only work and user-forbidden delegation are exempt. Prefer WORKER for a clear, bounded, stable, verifiable, non-overlapping package; MAIN owns unresolved architecture or investigation, cross-module decisions, required review, and final integration.

        Main supplies semantic lifecycle changes; Agent Switch owns mechanical state and enforcement. Queue relevant triggers—INITIAL_LOCALIZATION_COMPLETE, ARCHITECTURE_RESOLVED, WORKER_RESULT_RECEIVED, WORKER_REVIEW_COMPLETE, PHASE_CHANGE, BUILD_TEST_BOUNDED_FIXES, MODULE_COMPLETE, WORK_CONVERGED—and resolve MAIN vs WORKER once at the next natural reasoning boundary. A pending decision must be resolved before substantive mutation; the ownership lease remains the mechanical backstop. Hooks and Hard Gate are FrozenDisabled in 0.2.7.0.

        Never duplicate DELEGATED or RUNNING Worker work. Review returned work to the package risk, adopt or reject it before relying on it, then reconsider remaining ownership after WORKER_REVIEW_COMPLETE.
        """;

    private static string? ReplaceManagedProjectInstructions(string? existing, string? block)
    {
        if (block is not null)
        {
            if (existing is null)
            {
                return block;
            }

            return existing.Contains(ProjectInstructionsStart, StringComparison.Ordinal)
                ? ReplaceManagedBlock(existing, block, ProjectInstructionsStart, ProjectInstructionsEnd)
                : string.Concat(existing, Environment.NewLine, Environment.NewLine, block);
        }

        if (existing is null || !existing.Contains(ProjectInstructionsStart, StringComparison.Ordinal))
        {
            return existing;
        }

        var withoutManagedBlock = RemoveManagedProjectInstructions(existing);
        return string.IsNullOrWhiteSpace(withoutManagedBlock) ? null : withoutManagedBlock;
    }

    private static string RemoveManagedProjectInstructions(string existing)
    {
        var start = existing.IndexOf(ProjectInstructionsStart, StringComparison.Ordinal);
        var end = existing.IndexOf(ProjectInstructionsEnd, start, StringComparison.Ordinal);
        if (end < start)
        {
            throw new InvalidOperationException("当前 Agent Switch 管理块不完整，未覆盖原文件。");
        }

        end += ProjectInstructionsEnd.Length;
        if (existing.AsSpan(end).StartsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            end += Environment.NewLine.Length;
        }

        var separator = string.Concat(Environment.NewLine, Environment.NewLine);
        if (start >= separator.Length
            && existing.AsSpan(start - separator.Length, separator.Length).SequenceEqual(separator))
        {
            start -= separator.Length;
        }

        return string.Concat(existing.AsSpan(0, start), existing.AsSpan(end));
    }

    private static bool HasWorkerAgentChanges(
        IReadOnlyDictionary<string, byte[]?> existingAgents,
        string? desiredRelativePath,
        string? desiredConfiguration)
    {
        foreach (var (relativePath, bytes) in existingAgents)
        {
            var existing = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            if (string.Equals(relativePath, desiredRelativePath, StringComparison.Ordinal))
            {
                if (!string.Equals(existing, desiredConfiguration, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (IsManagedWorkerAgent(existing))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildNativeAgentConfiguration(EffectiveWorkerDefinition worker) =>
        new StringBuilder()
            .AppendLine(WorkerMarker)
            .AppendLine($"name = {Toml(worker.AgentRole!)}")
            .AppendLine($"description = {Toml($"Configured native worker {worker.AgentRole}.")}")
            .AppendLine($"model = {Toml(worker.ModelId!)}")
            .AppendLine($"model_reasoning_effort = {Toml(worker.ReasoningEffort!)}")
            .AppendLine("developer_instructions = \"\"\"")
            .AppendLine("Complete only the bounded task delegated by the main Codex agent. Return concise, verifiable findings, changed files, and any remaining risks. Do not change your assigned role or model.")
            .AppendLine("\"\"\"")
            .AppendLine("# <<< Codex Agent Switch worker <<<")
            .ToString();

    private static async Task WriteManagedWorkerAgentAsync(string path, string configuration, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, cancellationToken);
            if (!IsManagedWorkerAgent(existing) && !string.Equals(existing, configuration, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"发现非 Agent Switch 管理的 {path}。为防止覆盖用户文件，已取消应用；请改名或移走该文件后重试。");
            }

            if (string.Equals(existing, configuration, StringComparison.Ordinal))
            {
                return;
            }
        }

        await WriteTextAtomicallyAsync(path, configuration, cancellationToken);
    }

    private static async Task RemoveManagedWorkerAgentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var existing = await File.ReadAllTextAsync(path, cancellationToken);
        if (IsManagedWorkerAgent(existing))
        {
            File.Delete(path);
        }
    }

    private static bool IsManagedWorkerAgent(string? configuration) =>
        configuration?.Contains(WorkerMarker, StringComparison.Ordinal) == true
        || configuration?.Contains(ExternalWorkerMarker, StringComparison.Ordinal) == true;

    private static async Task WriteOrRemoveManagedProjectInstructionsAsync(
        string path,
        string? instructions,
        CancellationToken cancellationToken)
    {
        if (instructions is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        await WriteTextAtomicallyAsync(path, instructions, cancellationToken);
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("配置路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.cas-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task RestoreOriginalFileAsync(
        string? path,
        byte[]? originalBytes,
        bool originallyMissing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (originallyMissing)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        if (originalBytes is not null)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.cas-rollback-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, originalBytes, cancellationToken);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static string ReplaceManagedBlock(string existing, string block, string startMarker, string endMarker)
    {
        var start = existing.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Concat(existing.TrimEnd(), Environment.NewLine, Environment.NewLine, block);
        }

        var end = existing.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < start)
        {
            throw new InvalidOperationException("已有的 Agent Switch 配置块不完整，未覆盖原文件。");
        }

        end += endMarker.Length;
        return string.Concat(existing.AsSpan(0, start), block, existing.AsSpan(end));
    }

    private static string RemoveManagedBlock(string existing)
        => RemoveManagedBlock(existing, ManagedStart, ManagedEnd);

    private static string RemoveManagedBlock(string existing, string startMarker, string endMarker)
    {
        var start = existing.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("当前配置中没有可恢复的 Agent Switch 管理块。");
        }

        var end = existing.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < start)
        {
            throw new InvalidOperationException("当前 Agent Switch 管理块不完整，未覆盖原文件。");
        }

        end += endMarker.Length;
        return string.Concat(existing.AsSpan(0, start), existing.AsSpan(end)).Trim();
    }

    private static string ValidateProjectDirectory(string workingDirectory)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException($"项目工作目录不存在：{cwd}");
        }

        if (Path.GetPathRoot(cwd)?.Equals("C:\\", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("为遵守本机存储策略，不能向 C 盘项目写入 Codex 配置。请选择 E 盘项目目录。");
        }

        return cwd;
    }

    private async Task ValidateNativeModelsAsync(Profile profile, CodexCommand command, CancellationToken cancellationToken)
    {
        await modelResolver.ResolveAsync(command, profile.MainAgent.ModelId, cancellationToken);
        var worker = EffectiveWorkerDefinition.Resolve(profile.WorkerPolicy);
        if (worker.Kind == EffectiveWorkerKind.NativeAgent && worker.Capability == WorkerExecutionCapability.Supported)
        {
            await modelResolver.ResolveAsync(
                command,
                worker.ModelId ?? throw new InvalidOperationException("原生 Worker 配置无效。"),
                cancellationToken);
        }
    }

    private string StartDesktop(CodexDesktopAppDiscovery discovery)
    {
        if (discovery.AppUserModelId is not null)
        {
            processStarter.StartAppsFolder(discovery.AppUserModelId);
            return discovery.AppUserModelId;
        }

        if (discovery.ExecutablePath is not null)
        {
            processStarter.StartExecutable(discovery.ExecutablePath);
            return discovery.ExecutablePath;
        }

        throw new InvalidOperationException("没有可用的 Codex 桌面应用启动入口。");
    }

    private async Task<string?> ReadManualExecutableAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.NativeCodexDirectory, DesktopEntryFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return (await JsonSerializer.DeserializeAsync<ManualDesktopEntry>(stream, cancellationToken: cancellationToken))?.ExecutablePath;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsCli(string executablePath) =>
        string.Equals(Path.GetFileName(executablePath), "codex.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(executablePath), "codex", StringComparison.OrdinalIgnoreCase);

    private static string Toml(string value) => JsonSerializer.Serialize(value);

    private static string Fingerprint(string configuration) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));

    private sealed record ManualDesktopEntry(string ExecutablePath);

    private sealed record ProjectConfigurationWrite(
        string Path,
        bool RequiresExternalCredentialSetup,
        bool Changed,
        string? BackupPath,
        bool OriginalConfigurationExisted,
        string ConfigurationFingerprint,
        bool UserAutoCompactTokenLimitPreserved);
}
