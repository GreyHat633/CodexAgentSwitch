using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public interface INativeCodexProcessStarter
{
    int Start(ProcessStartInfo startInfo);
}

public sealed class NativeCodexProcessStarter : INativeCodexProcessStarter
{
    public int Start(ProcessStartInfo startInfo) =>
        (Process.Start(startInfo) ?? throw new InvalidOperationException("原生 Codex 进程没有启动。")).Id;
}

public sealed class NativeCodexLauncher(
    CodexCommandLocator locator,
    IProviderRepository providers,
    ICredentialStore credentials,
    AppDataPaths paths,
    INativeCodexProcessStarter processStarter,
    ICodexModelResolver modelResolver) : INativeCodexLauncher
{
    private const string ProviderKeyEnvironment = "CAS_NATIVE_WORKER_API_KEY";

    public async Task<NativeCodexLaunchResult> LaunchAsync(
        Profile profile,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{cwd}");
        }

        var discovery = await locator.LocateAsync(cancellationToken);
        var command = discovery.Command
            ?? throw new InvalidOperationException(discovery.Status);
        var mainModel = await modelResolver.ResolveAsync(command, profile.MainAgent.ModelId, cancellationToken);
        Directory.CreateDirectory(paths.NativeCodexDirectory);
        var workerConfigPath = Path.Combine(paths.NativeCodexDirectory, $"worker-{profile.Id:D}.toml");
        var auditPath = Path.Combine(paths.NativeCodexDirectory, $"launch-{profile.Id:D}.json");

        ProviderConfiguration? provider = null;
        string? secret = null;
        if (profile.WorkerPolicy.Enabled && profile.WorkerPolicy.Source == WorkerSource.ExternalProvider)
        {
            var providerId = profile.WorkerPolicy.PreferredProviderId
                ?? throw new InvalidOperationException("当前方案没有选择外部 Provider。");
            provider = await providers.GetAsync(providerId, cancellationToken)
                ?? throw new InvalidOperationException($"Provider {providerId} 不存在。");
            if (!provider.IsEnabled || provider.BaseUri is null || string.IsNullOrWhiteSpace(provider.ModelId))
            {
                throw new InvalidOperationException($"Provider {provider.Name} 未就绪。");
            }

            if (string.IsNullOrWhiteSpace(provider.CredentialReference))
            {
                throw new InvalidOperationException($"Provider {provider.Name} 没有凭据引用。");
            }

            secret = await credentials.ReadAsync(provider.CredentialReference, cancellationToken);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException($"Provider {provider.Name} 的 API Key 不可用。");
            }
        }

        var nativeWorkerModel = profile.WorkerPolicy.Source == WorkerSource.NativeCodex
            ? await modelResolver.ResolveAsync(
                command,
                NativeWorkerModel(profile.WorkerPolicy) ?? throw new InvalidOperationException("原生 Worker ID 无效。"),
                cancellationToken)
            : null;
        var workerConfig = BuildWorkerConfig(profile, provider, nativeWorkerModel?.ModelId);
        await File.WriteAllTextAsync(workerConfigPath, workerConfig, new UTF8Encoding(false), cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var prefix in command.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefix);
        }

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(cwd);
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(mainModel.ModelId);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"model_reasoning_effort={Toml(profile.MainAgent.ReasoningEffort)}");
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(ApprovalPolicy(profile.ApprovalMode));
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(SandboxMode(profile.ApprovalMode));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"agents.enabled={(profile.WorkerPolicy.Enabled ? "true" : "false")}");
        if (profile.WorkerPolicy.Enabled)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"agents.max_concurrent_threads_per_session={Math.Max(1, profile.WorkerPolicy.MaxWorkers)}");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"agents.worker.description={Toml("执行主代理明确委派的有边界任务，并返回可核验结果。")}");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"agents.worker.config_file={Toml(workerConfigPath.Replace('\\', '/'))}");
        }

        var codexHome = ResolveNonSystemDriveCodexHome();
        if (codexHome is not null)
        {
            startInfo.Environment["CODEX_HOME"] = codexHome;
        }

        if (secret is not null)
        {
            startInfo.Environment[ProviderKeyEnvironment] = secret;
        }

        var audit = new
        {
            profileId = profile.Id,
            profileName = profile.Name,
            requestedMainModel = profile.MainAgent.ModelId,
            mainModel = mainModel.ModelId,
            mainModelCompatibilityNotice = mainModel.CompatibilityNotice,
            reasoningEffort = profile.MainAgent.ReasoningEffort,
            approvalMode = profile.ApprovalMode.ToString(),
            workerEnabled = profile.WorkerPolicy.Enabled,
            workerSource = profile.WorkerPolicy.Source.ToString(),
            providerId = provider?.Id ?? (profile.WorkerPolicy.Source == WorkerSource.NativeCodex ? "native-codex" : null),
            workerModel = provider?.ModelId ?? nativeWorkerModel?.ModelId,
            workingDirectory = cwd,
            generatedConfigurationPath = workerConfigPath,
            generatedAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(
            auditPath,
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken);

        var processId = processStarter.Start(startInfo);
        return new NativeCodexLaunchResult(
            processId,
            command.Executable,
            cwd,
            workerConfigPath,
            "已应用当前方案并启动原生 Codex。后续界面、委派决策和主线程统计由原生 Codex 自行负责。"
            + (mainModel.CompatibilityNotice is null ? string.Empty : $" {mainModel.CompatibilityNotice}"));
    }

    private static string BuildWorkerConfig(Profile profile, ProviderConfiguration? provider, string? nativeWorkerModel)
    {
        var builder = new StringBuilder()
            .AppendLine("name = \"worker\"")
            .AppendLine("description = \"Codex Agent Switch generated worker configuration.\"")
            .AppendLine("developer_instructions = \"\"\"")
            .AppendLine("Complete only the bounded work delegated by the main Codex agent. Return concise, verifiable results and stop before expanding scope.")
            .AppendLine("\"\"\"");

        if (!profile.WorkerPolicy.Enabled)
        {
            return builder.AppendLine("# Worker is disabled by the active profile.").ToString();
        }

        if (profile.WorkerPolicy.Source == WorkerSource.NativeCodex)
        {
            builder.AppendLine($"model = {Toml(nativeWorkerModel ?? throw new InvalidOperationException("原生 Worker ID 无效。"))}");
            builder.AppendLine("model_reasoning_effort = \"medium\"");
            return builder.ToString();
        }

        if (profile.WorkerPolicy.Source != WorkerSource.ExternalProvider || provider is null)
        {
            throw new InvalidOperationException("当前 Worker 配置无法转换为原生 Codex 配置。");
        }

        builder.AppendLine($"model = {Toml(provider.ModelId!)}");
        builder.AppendLine("model_reasoning_effort = \"medium\"");
        builder.AppendLine("model_provider = \"cas_external\"");
        builder.AppendLine();
        builder.AppendLine("[model_providers.cas_external]");
        builder.AppendLine($"name = {Toml(provider.Name)}");
        builder.AppendLine($"base_url = {Toml(provider.BaseUri!.AbsoluteUri.TrimEnd('/'))}");
        builder.AppendLine($"env_key = {Toml(ProviderKeyEnvironment)}");
        builder.AppendLine("wire_api = \"responses\"");
        builder.AppendLine("request_max_retries = 2");
        builder.AppendLine("stream_max_retries = 2");
        return builder.ToString();
    }

    private static string? NativeWorkerModel(WorkerPolicy policy) => policy.Source != WorkerSource.NativeCodex
        ? null
        : policy.PreferredProviderId switch
        {
            "native-sol" => "gpt-5.6-sol",
            "native-terra" => "gpt-5.6-terra",
            "native-luna" => "gpt-5.6-luna",
            _ => null,
        };

    private static string? ResolveNonSystemDriveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CAS_CODEX_HOME")
            ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        const string establishedEDriveHome = @"E:\AI\CODEX\.codex";
        return Directory.Exists(establishedEDriveHome) ? establishedEDriveHome : null;
    }

    private static string Toml(string value) => JsonSerializer.Serialize(value);

    private static string ApprovalPolicy(ExecutionApprovalMode mode) =>
        ExecutionApprovalPolicy.Resolve(mode).ApprovalPolicy;

    private static string SandboxMode(ExecutionApprovalMode mode) =>
        ExecutionApprovalPolicy.Resolve(mode).SandboxMode;
}
