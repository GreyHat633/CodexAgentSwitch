using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
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
    IProviderRepository providers,
    ICredentialStore credentials,
    ICodexProjectConfigurationValidator configurationValidator) : ICodexDesktopLauncher
{
    private const string ManagedStart = "# >>> Codex Agent Switch managed profile >>>";
    private const string ManagedEnd = "# <<< Codex Agent Switch managed profile <<<";
    private const string ExternalWorkerRole = "cas_external_worker";
    private const string ExternalWorkerAgentFile = "agents/cas-external-worker.toml";
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
        if (profile.WorkerPolicy.Enabled && profile.WorkerPolicy.Source == WorkerSource.NativeCodex)
        {
            await modelResolver.ResolveAsync(
                command,
                NativeWorkerModel(profile.WorkerPolicy) ?? throw new InvalidOperationException("原生 Worker 配置无效。"),
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
                    write.Changed ? "已写入 Agent Switch 管理的项目配置。" : "配置已是当前方案，无需更新。"));
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
        var existing = File.Exists(configurationPath)
            ? await File.ReadAllTextAsync(configurationPath, cancellationToken)
            : null;
        var externalWorker = await PrepareExternalWorkerAsync(profile, cancellationToken);
        var block = BuildManagedConfiguration(profile, externalWorker);
        var next = existing is null
            ? block
            : ReplaceManagedBlock(existing, block);
        var agentPath = Path.Combine(directory, ExternalWorkerAgentFile);
        var existingAgent = File.Exists(agentPath)
            ? await File.ReadAllTextAsync(agentPath, cancellationToken)
            : null;
        var desiredAgent = externalWorker?.AgentConfiguration;
        var agentIsManaged = existingAgent?.Contains("# >>> Codex Agent Switch external worker >>>", StringComparison.Ordinal) == true;
        var projectChanged = !string.Equals(existing, next, StringComparison.Ordinal);
        var userChanged = externalWorker is not null
            && !string.Equals(externalWorker.ExistingUserConfiguration, externalWorker.UserConfiguration, StringComparison.Ordinal);
        var agentChanged = externalWorker is not null
            ? !string.Equals(existingAgent, desiredAgent, StringComparison.Ordinal)
            : agentIsManaged;
        if (!projectChanged && !userChanged && !agentChanged)
        {
            return new ProjectConfigurationWrite(configurationPath, false, false, null, existing is not null);
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
                    externalWorker?.UserConfiguration,
                    externalWorker is null
                        ? null
                        : new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [ExternalWorkerAgentFile] = externalWorker.AgentConfiguration,
                        }),
                cancellationToken);

            string? backupPath = null;
            if (projectChanged && existing is not null)
            {
                var backupDirectory = Path.Combine(
                    paths.NativeCodexDirectory,
                    "project-config-backups",
                    projectId ?? "standalone",
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(backupDirectory);
                backupPath = Path.Combine(backupDirectory, "config.toml");
                await File.WriteAllTextAsync(backupPath, existing, new UTF8Encoding(false), cancellationToken);
            }

            var originalUserBytes = userChanged && externalWorker?.ExistingUserConfiguration is not null
                ? await File.ReadAllBytesAsync(externalWorker.UserConfigurationPath, cancellationToken)
                : null;
            var originalAgentBytes = existingAgent is null ? null : await File.ReadAllBytesAsync(agentPath, cancellationToken);
            try
            {
                if (externalWorker is not null)
                {
                    await WriteUserConfigurationAsync(externalWorker, cancellationToken);
                    await WriteExternalAgentAsync(directory, externalWorker.AgentConfiguration, cancellationToken);
                }
                else if (agentChanged)
                {
                    await RemoveExternalAgentAsync(directory, cancellationToken);
                }

                if (projectChanged)
                {
                    File.Move(temporaryPath, configurationPath, overwrite: true);
                }
            }
            catch
            {
                await RestoreOriginalFileAsync(externalWorker?.UserConfigurationPath, originalUserBytes, externalWorker?.ExistingUserConfiguration is null, cancellationToken);
                await RestoreOriginalFileAsync(agentPath, originalAgentBytes, existingAgent is null, cancellationToken);
                throw;
            }

            return new ProjectConfigurationWrite(configurationPath, false, projectChanged || userChanged || agentChanged, backupPath, existing is not null);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildManagedConfiguration(Profile profile, ExternalWorkerPreparation? externalWorker)
    {
        var approval = ExecutionApprovalPolicy.Resolve(profile.ApprovalMode);
        var builder = new StringBuilder()
            .AppendLine(ManagedStart)
            .AppendLine("# Generated from the active Codex Agent Switch profile. Do not place credentials in this file.")
            .AppendLine($"model = {Toml(profile.MainAgent.ModelId)}")
            .AppendLine($"model_reasoning_effort = {Toml(profile.MainAgent.ReasoningEffort)}")
            .AppendLine($"approval_policy = {Toml(approval.ApprovalPolicy)}")
            .AppendLine($"sandbox_mode = {Toml(approval.SandboxMode)}")
            .AppendLine($"agents.enabled = {(profile.WorkerPolicy.Enabled ? "true" : "false")}");

        if (profile.WorkerPolicy.Enabled)
        {
            builder.AppendLine($"agents.max_concurrent_threads_per_session = {Math.Max(1, profile.WorkerPolicy.MaxWorkers)}");
            if (profile.WorkerPolicy.Source == WorkerSource.NativeCodex)
            {
                builder.AppendLine($"agents.default_subagent_model = {Toml(NativeWorkerModel(profile.WorkerPolicy) ?? throw new InvalidOperationException("原生 Worker 配置无效。"))}")
                    .AppendLine("agents.default_subagent_reasoning_effort = \"medium\"");
            }
            else if (externalWorker is not null)
            {
                // This project layer only declares the role and points to its
                // separate agent file.  The provider itself remains in the
                // user-level CODEX_HOME/config.toml layer.
                builder.AppendLine()
                    .AppendLine($"[agents.{ExternalWorkerRole}]")
                    .AppendLine($"description = {Toml($"Use {externalWorker.ProviderName} for bounded delegated work and return verifiable results.")}")
                    .AppendLine($"config_file = {Toml($"./{ExternalWorkerAgentFile.Replace('\\', '/')}")}");
            }
        }

        return builder.AppendLine(ManagedEnd).ToString();
    }

    private async Task<ExternalWorkerPreparation?> PrepareExternalWorkerAsync(
        Profile profile,
        CancellationToken cancellationToken)
    {
        if (!profile.WorkerPolicy.Enabled || profile.WorkerPolicy.Source != WorkerSource.ExternalProvider)
        {
            return null;
        }

        var providerId = profile.WorkerPolicy.PreferredProviderId
            ?? throw new InvalidOperationException("当前方案没有选择外部 Provider。");
        var provider = await providers.GetAsync(providerId, cancellationToken)
            ?? throw new InvalidOperationException($"找不到外部 Provider：{providerId}。");
        if (!provider.IsEnabled || provider.BaseUri is null || string.IsNullOrWhiteSpace(provider.ModelId))
        {
            throw new InvalidOperationException($"Provider“{provider.Name}”尚未完成可用配置。");
        }

        if (string.IsNullOrWhiteSpace(provider.CredentialReference)
            || !await credentials.ExistsAsync(provider.CredentialReference, cancellationToken))
        {
            throw new InvalidOperationException($"Provider“{provider.Name}”的 API Key 尚未安全保存到 Windows 凭据管理器。");
        }

        if (provider.Kind == ProviderKind.DeepSeek
            && (!DeepSeekV4Catalog.TryGet(provider.ModelId, out var model)
                || !model.Supports(ProviderProtocol.Responses)))
        {
            throw new InvalidOperationException("当前 DeepSeek 模型不支持原生 Codex 所需的 Responses 协议。请选择 DeepSeek V4 Flash 0731。");
        }

        var brokerPath = ResolveCredentialBrokerPath();
        if (!File.Exists(brokerPath))
        {
            throw new InvalidOperationException("安装包缺少原生 Codex 凭据代理。请使用完整安装包或便携版重新安装 Agent Switch。");
        }

        var codexHome = ResolveCodexHome();
        if (Path.GetPathRoot(codexHome)?.Equals("C:\\", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("为遵守本机存储策略，原生 DeepSeek Provider 需要一个位于非 C 盘的 CODEX_HOME。请先在设置中配置 E 盘 CODEX_HOME 后重试。");
        }

        Directory.CreateDirectory(codexHome);
        var userConfigurationPath = Path.Combine(codexHome, "config.toml");
        var existingUserConfiguration = File.Exists(userConfigurationPath)
            ? await File.ReadAllTextAsync(userConfigurationPath, cancellationToken)
            : null;
        var providerKey = ToProviderKey(provider.Id);
        var providerStart = $"# >>> Codex Agent Switch native provider {providerKey} >>>";
        var providerEnd = $"# <<< Codex Agent Switch native provider {providerKey} <<<";
        var providerBlock = BuildUserProviderConfiguration(providerKey, provider, brokerPath);
        var nextUserConfiguration = existingUserConfiguration is null
            ? providerBlock
            : ReplaceManagedBlock(existingUserConfiguration, providerBlock, providerStart, providerEnd);

        return new ExternalWorkerPreparation(
            provider.Name,
            providerKey,
            userConfigurationPath,
            existingUserConfiguration,
            nextUserConfiguration,
            BuildExternalAgentConfiguration(providerKey, provider));
    }

    private string BuildUserProviderConfiguration(string providerKey, ProviderConfiguration provider, string brokerPath)
    {
        var start = $"# >>> Codex Agent Switch native provider {providerKey} >>>";
        var end = $"# <<< Codex Agent Switch native provider {providerKey} <<<";
        var builder = new StringBuilder()
            .AppendLine(start)
            .AppendLine("# Provider metadata only. The API key remains in Windows Credential Manager.")
            .AppendLine($"[model_providers.{providerKey}]")
            .AppendLine($"name = {Toml(provider.Name)}")
            .AppendLine($"base_url = {Toml(provider.BaseUri!.AbsoluteUri.TrimEnd('/'))}")
            .AppendLine("wire_api = \"responses\"")
            .AppendLine("request_max_retries = 2")
            .AppendLine("stream_max_retries = 2");

        if (provider.Headers.Count > 0)
        {
            builder.AppendLine($"http_headers = {TomlInlineTable(provider.Headers)}");
        }

        builder.AppendLine()
            .AppendLine($"[model_providers.{providerKey}.auth]")
            .AppendLine($"command = {Toml(brokerPath)}")
            .AppendLine($"args = [{Toml("--data-root")}, {Toml(paths.Root)}, {Toml("--provider-id")}, {Toml(provider.Id)}]")
            .AppendLine("timeout_ms = 5000")
            .AppendLine("refresh_interval_ms = 300000")
            .AppendLine(end);
        return builder.ToString();
    }

    private static string BuildExternalAgentConfiguration(string providerKey, ProviderConfiguration provider) =>
        new StringBuilder()
            .AppendLine("# >>> Codex Agent Switch external worker >>>")
            .AppendLine($"name = {Toml(ExternalWorkerRole)}")
            .AppendLine($"description = {Toml($"Bounded external-worker role backed by {provider.Name}.")}")
            .AppendLine($"model = {Toml(provider.ModelId!)}")
            .AppendLine($"model_provider = {Toml(providerKey)}")
            .AppendLine("model_reasoning_effort = \"medium\"")
            .AppendLine("developer_instructions = \"\"\"")
            .AppendLine("Complete only the bounded task delegated by the main Codex agent. Return concise, verifiable findings, changed files, and any remaining risks. Do not expand scope.")
            .AppendLine("\"\"\"")
            .AppendLine("# <<< Codex Agent Switch external worker <<<")
            .ToString();

    private static string TomlInlineTable(IReadOnlyDictionary<string, string> values) =>
        "{ " + string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Toml(pair.Key)} = {Toml(pair.Value)}")) + " }";

    private async Task WriteUserConfigurationAsync(ExternalWorkerPreparation preparation, CancellationToken cancellationToken)
    {
        if (string.Equals(preparation.ExistingUserConfiguration, preparation.UserConfiguration, StringComparison.Ordinal))
        {
            return;
        }

        await WriteTextAtomicallyAsync(preparation.UserConfigurationPath, preparation.UserConfiguration, cancellationToken);
    }

    private static async Task WriteExternalAgentAsync(string projectCodexDirectory, string agentConfiguration, CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectCodexDirectory, ExternalWorkerAgentFile);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, cancellationToken);
            if (!existing.Contains("# >>> Codex Agent Switch external worker >>>", StringComparison.Ordinal)
                && !string.Equals(existing, agentConfiguration, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("项目中已存在同名的自定义 Worker 文件，Agent Switch 不会覆盖它。");
            }

            if (string.Equals(existing, agentConfiguration, StringComparison.Ordinal))
            {
                return;
            }
        }

        await WriteTextAtomicallyAsync(path, agentConfiguration, cancellationToken);
    }

    private static async Task RemoveExternalAgentAsync(string projectCodexDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectCodexDirectory, ExternalWorkerAgentFile);
        if (!File.Exists(path))
        {
            return;
        }

        var existing = await File.ReadAllTextAsync(path, cancellationToken);
        if (existing.Contains("# >>> Codex Agent Switch external worker >>>", StringComparison.Ordinal))
        {
            File.Delete(path);
        }
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

    private static string ResolveCredentialBrokerPath()
    {
        var configured = Environment.GetEnvironmentVariable("CAS_NATIVE_CREDENTIAL_BROKER");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "NativeCredentialBroker", "CodexAgentSwitch.CredentialBroker.exe")
            : Path.GetFullPath(configured);
    }

    private static string ResolveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CAS_CODEX_HOME")
            ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        const string establishedEDriveHome = @"E:\\AI\\CODEX\\.codex";
        if (Directory.Exists(establishedEDriveHome))
        {
            return establishedEDriveHome;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static string ToProviderKey(string providerId)
    {
        var normalized = new string(providerId.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray());
        return $"cas_{normalized}";
    }

    private sealed record ExternalWorkerPreparation(
        string ProviderName,
        string ProviderKey,
        string UserConfigurationPath,
        string? ExistingUserConfiguration,
        string UserConfiguration,
        string AgentConfiguration);

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
    {
        var start = existing.IndexOf(ManagedStart, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("当前配置中没有可恢复的 Agent Switch 管理块。");
        }

        var end = existing.IndexOf(ManagedEnd, start, StringComparison.Ordinal);
        if (end < start)
        {
            throw new InvalidOperationException("当前 Agent Switch 管理块不完整，未覆盖原文件。");
        }

        end += ManagedEnd.Length;
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
        if (profile.WorkerPolicy.Enabled && profile.WorkerPolicy.Source == WorkerSource.NativeCodex)
        {
            await modelResolver.ResolveAsync(
                command,
                NativeWorkerModel(profile.WorkerPolicy) ?? throw new InvalidOperationException("原生 Worker 配置无效。"),
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

    private static string? NativeWorkerModel(WorkerPolicy policy) => policy.PreferredProviderId switch
    {
        "native-sol" => "gpt-5.6-sol",
        "native-terra" => "gpt-5.6-terra",
        "native-luna" => "gpt-5.6-luna",
        _ => null,
    };

    private static string Toml(string value) => JsonSerializer.Serialize(value);

    private sealed record ManualDesktopEntry(string ExecutablePath);

    private sealed record ProjectConfigurationWrite(
        string Path,
        bool RequiresExternalCredentialSetup,
        bool Changed,
        string? BackupPath,
        bool OriginalConfigurationExisted);
}
