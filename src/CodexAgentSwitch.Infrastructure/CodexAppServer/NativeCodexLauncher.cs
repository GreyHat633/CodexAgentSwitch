using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Domain.Profiles;
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
    AppDataPaths paths,
    INativeCodexProcessStarter processStarter,
    ICodexModelResolver modelResolver) : INativeCodexLauncher
{
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
        var auditPath = Path.Combine(paths.NativeCodexDirectory, $"launch-{profile.Id:D}.json");

        var worker = EffectiveWorkerDefinition.Resolve(profile.WorkerPolicy);
        var nativeWorkerModel = worker.Kind == EffectiveWorkerKind.NativeAgent && worker.CanRunInNativeCodex
            ? await modelResolver.ResolveAsync(
                command,
                worker.ModelId ?? throw new InvalidOperationException("原生 Worker 配置无效。"),
                cancellationToken)
            : null;
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
        startInfo.ArgumentList.Add($"agents.enabled={(worker.Kind == EffectiveWorkerKind.NativeAgent && worker.CanRunInNativeCodex ? "true" : "false")}");
        if (worker.Kind == EffectiveWorkerKind.NativeAgent && worker.CanRunInNativeCodex)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"agents.max_concurrent_threads_per_session={worker.MaxWorkers}");
        }

        var codexHome = ResolveNonSystemDriveCodexHome();
        if (codexHome is not null)
        {
            startInfo.Environment["CODEX_HOME"] = codexHome;
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
            providerId = profile.WorkerPolicy.Source == WorkerSource.NativeCodex ? "native-codex" : profile.WorkerPolicy.PreferredProviderId,
            workerModel = nativeWorkerModel?.ModelId,
            workerRole = worker.AgentRole,
            workerCapability = worker.Capability.ToString(),
            workerCapabilityMessage = worker.CapabilityMessage,
            workingDirectory = cwd,
            generatedConfigurationPath = (string?)null,
            externalProviderWorkerSupported = worker.Kind != EffectiveWorkerKind.ExternalAgent || worker.CanRunInNativeCodex,
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
            null,
            "已应用当前方案并启动原生 Codex。后续界面、委派决策和主线程统计由原生 Codex 自行负责。"
            + (mainModel.CompatibilityNotice is null ? string.Empty : $" {mainModel.CompatibilityNotice}"));
    }

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
