using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var summary = "已写入项目级 Codex 配置并启动官方图形桌面应用。请在桌面应用中打开同一项目目录以加载该方案。";
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
                    write.Changed ? "已写入 Agent Switch 管理的项目配置。" : "配置已是当前方案，无需更新。",
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
        var worker = EffectiveWorkerDefinition.Resolve(profile.WorkerPolicy);
        var managedAgent = worker.Kind == EffectiveWorkerKind.NativeAgent && worker.CanRunInNativeCodex
            ? BuildNativeAgentConfiguration(worker)
            : null;
        var block = BuildManagedConfiguration(profile, worker);
        var next = existing is null
            ? block
            : ReplaceManagedBlock(existing, block);
        var nextProjectInstructions = ReplaceManagedProjectInstructions(
            existingProjectInstructions,
            BuildManagedProjectInstructions(worker));
        var existingAgents = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var relativePath in ManagedWorkerAgentFiles)
        {
            var path = Path.Combine(directory, relativePath);
            existingAgents[relativePath] = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        var projectChanged = !string.Equals(existing, next, StringComparison.Ordinal);
        var projectInstructionsChanged = !string.Equals(existingProjectInstructions, nextProjectInstructions, StringComparison.Ordinal);
        var agentChanged = HasWorkerAgentChanges(existingAgents, worker.ConfigFile, managedAgent);
        if (!projectChanged && !projectInstructionsChanged && !agentChanged)
        {
            return new ProjectConfigurationWrite(configurationPath, false, false, null, existing is not null, Fingerprint(next));
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
                    managedAgent is null
                        ? null
                        : new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [worker.ConfigFile!] = managedAgent,
                        }),
                cancellationToken);

            string? backupPath = null;
            if (projectChanged || projectInstructionsChanged || agentChanged)
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
            }

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

                if (projectChanged)
                {
                    File.Move(temporaryPath, configurationPath, overwrite: true);
                }
            }
            catch
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
                throw;
            }

            return new ProjectConfigurationWrite(configurationPath, false, projectChanged || projectInstructionsChanged || agentChanged, backupPath, existing is not null, Fingerprint(next));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildManagedConfiguration(Profile profile, EffectiveWorkerDefinition worker)
    {
        var approval = ExecutionApprovalPolicy.Resolve(profile.ApprovalMode);
        var builder = new StringBuilder()
            .AppendLine(ManagedStart)
            .AppendLine("# Generated from the active Codex Agent Switch profile. Do not place credentials in this file.")
            .AppendLine($"model = {Toml(profile.MainAgent.ModelId)}")
            .AppendLine($"model_reasoning_effort = {Toml(profile.MainAgent.ReasoningEffort)}")
            .AppendLine($"approval_policy = {Toml(approval.ApprovalPolicy)}")
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
            builder.AppendLine($"developer_instructions = {Toml(BuildExternalDelegationInstructions())}")
                .AppendLine($"# Native external collaboration remains gated: {worker.CapabilityMessage}");
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
                .AppendLine("enabled = true");
        }

        return builder.AppendLine(ManagedEnd).ToString();
    }

    private static string? BuildManagedProjectInstructions(EffectiveWorkerDefinition worker)
    {
        if (!worker.CanRunInNativeCodex || worker.Kind != EffectiveWorkerKind.NativeAgent)
        {
            return null;
        }

        return new StringBuilder()
            .AppendLine(ProjectInstructionsStart)
            .AppendLine("## Codex Agent Switch managed native worker routing")
            .AppendLine()
            .AppendLine(BuildProactiveDelegationPolicy())
            .AppendLine()
            .AppendLine($"For bounded delegation, call the codex_agent_switch delegate_worker tool with a complete plaintext TaskPacket. When invoking the configured Native Custom Worker, you MUST call spawn_agent with both actual tool arguments: agent_type=\"{worker.AgentRole}\" and fork_turns=\"none\". fork_turns is mandatory for this managed custom role: never omit it, never use fork_turns=\"all\", and never create a full-history fork. While the task is DELEGATED or RUNNING, do not duplicate its work. Report the result through report_worker_result, then perform only bounded review.")
            .AppendLine(ProjectInstructionsEnd)
            .ToString();
    }

    private static string BuildExternalDelegationInstructions() => $"""
        {BuildProactiveDelegationPolicy()}

        For bounded delegation, call the codex_agent_switch delegate_worker tool with a complete plaintext TaskPacket and omit workerId. Agent Switch resolves the Worker from this project's applied snapshot; never choose a Provider Worker identity freely. Never spawn cas_external_worker through native collaboration. While the task is DELEGATED or RUNNING, do not duplicate its work; review and adopt only the returned ResultPacket.
        """;

    private static string BuildProactiveDelegationPolicy() => """
        For every non-trivial, multi-step, or clearly separable development request, complete a Delegation Capability Preflight after the minimum localization needed to identify concrete work and before the first substantive large implementation. Confirm or load the currently available Agent Switch scheduling tools, especially delayed-loaded delegate_worker and Worker orchestration capability; do not assume that a capability exists merely because it is documented.

        If the scheduling tools are available, require the Initial Delegation Check immediately after the preflight and before beginning large implementation. If the tools are unavailable, record a short reason such as WORKER_CAPABILITY_MISSING and continue with MAIN without deadlocking; an unavailable preflight is not permission to skip the check silently.

        Exempt one-line changes, clearly tiny configuration edits, read-only questions, user-forbidden Worker use, and micro tasks whose delegation overhead exceeds their value. Do not wrap every shell command in a delegation decision.

        Do not wait for the user to mention workers and do not generate a long plan merely to satisfy this check.

        The Delegation Capability Preflight is a gate before the Initial Delegation Check, not a ninth RepartitionTrigger.

        Re-evaluate ownership before the next substantive work whenever any of these eight distinct triggers occurs: INITIAL_LOCALIZATION_COMPLETE, ARCHITECTURE_RESOLVED, WORKER_RESULT_RECEIVED, WORKER_REVIEW_COMPLETE, PHASE_CHANGE, BUILD_TEST_BOUNDED_FIXES, MODULE_COMPLETE, or WORK_CONVERGED. WORKER_RESULT_RECEIVED enters bounded review; WORKER_REVIEW_COMPLETE is a separate later trigger that must reconsider all remaining work.

        Prefer WORKER when the current package is clear, bounded, based on stable interfaces, supported by the configured worker, independently verifiable, non-overlapping, and large enough to justify delegation. Risk belongs to the current package and must be re-evaluated when vague or high-risk work converges; do not inherit the original task risk forever. MAIN owns unresolved architecture, cross-module decisions, unresolved investigation, required review, and final integration.

        Every ownership decision must keep a compact work state: current work, known remaining work, owner, trigger, and one short reason. MAIN reasons are ARCHITECTURE_UNRESOLVED, CROSS_MODULE_DECISION, INVESTIGATION_UNRESOLVED, WORKER_CAPABILITY_MISSING, TOO_SMALL_TO_DELEGATE, REVIEW_REQUIRED, or FINAL_INTEGRATION. WORKER reasons are BOUNDED_IMPLEMENTATION, BOUNDED_FIX, BOUNDED_UI, BOUNDED_TESTING, or REPETITIVE_WORK. A MAIN decision without one of these reasons is invalid. At each trigger, call codex_agent_switch record_repartition before beginning the next substantive package; use list_repartitions when resuming or checking prior decisions. The Main Agent reports semantic triggers—the Agent Switch host only validates and stores them and must not pretend to understand project semantics.

        The default active-worker limit is concurrency-only, not a lifetime limit. After Worker A reaches a terminal result and its bounded review completes, a later trigger may dispatch Worker B or C serially. Never increase worker calls merely as a metric, never delegate a trivial package whose overhead is higher than direct work, and never duplicate a DELEGATED or RUNNING package. Review only to the risk-appropriate budget; necessary verification is not full reimplementation.
        """;

    private static string? ReplaceManagedProjectInstructions(string? existing, string? block)
    {
        if (block is not null)
        {
            return existing is null
                ? block
                : ReplaceManagedBlock(existing, block, ProjectInstructionsStart, ProjectInstructionsEnd);
        }

        if (existing is null || !existing.Contains(ProjectInstructionsStart, StringComparison.Ordinal))
        {
            return existing;
        }

        var withoutManagedBlock = RemoveManagedBlock(existing, ProjectInstructionsStart, ProjectInstructionsEnd);
        return string.IsNullOrWhiteSpace(withoutManagedBlock) ? null : withoutManagedBlock;
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

    private static string ReplaceManagedBlock(string existing, string block)
        => ReplaceManagedBlock(existing, block, ManagedStart, ManagedEnd);

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
        string ConfigurationFingerprint);
}
