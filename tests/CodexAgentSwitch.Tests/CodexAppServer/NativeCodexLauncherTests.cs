using System.Diagnostics;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class NativeCodexLauncherTests
{
    private const string Secret = "native-launch-secret-must-not-be-written";

    [Fact]
    public void Formal_composition_root_can_resolve_native_launcher_and_model_resolver()
    {
        var now = DateTimeOffset.UtcNow;
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var services = new ServiceCollection();
        services.AddSingleton<CodexCommandLocator, FixedLocator>();
        services.AddSingleton(new AppDataPaths(Path.Combine(testRoot, "cas-native-launcher-di")));
        services.AddSingleton<INativeCodexProcessStarter, RecordingStarter>();
        services.AddSingleton<ICodexModelResolver, FakeModelResolver>();
        services.AddSingleton<IProviderRepository, EmptyProviderRepository>();
        services.AddSingleton<ICredentialStore, EmptyCredentialStore>();
        services.AddSingleton<INativeCodexLauncher, NativeCodexLauncher>();
        services.AddSingleton<ICodexDesktopAppRegistration, RegistryCodexDesktopAppRegistration>();
        services.AddSingleton<ICodexDesktopProcessStarter, CodexDesktopProcessStarter>();
        services.AddSingleton<ICodexProjectConfigurationValidator, CodexProjectConfigurationValidator>();
        services.AddSingleton<ICodexDesktopLauncher, CodexDesktopAppLauncher>();

        using var container = services.BuildServiceProvider(validateScopes: true);

        Assert.IsType<NativeCodexLauncher>(container.GetRequiredService<INativeCodexLauncher>());
        Assert.IsType<FakeModelResolver>(container.GetRequiredService<ICodexModelResolver>());
        Assert.IsType<CodexDesktopAppLauncher>(container.GetRequiredService<ICodexDesktopLauncher>());
    }

    [Fact]
    public async Task External_profile_starts_cli_with_native_external_capability_marked_unsupported()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(Path.GetFullPath(testRoot), $"native-launch-{Guid.NewGuid():N}");
        Assert.StartsWith("E:\\", root, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var profile = new Profile(
                Guid.NewGuid(),
                "Sol + DeepSeek",
                new AgentSelection("gpt-5.6-terra", "high"),
                new WorkerPolicy(true, WorkerSource.ExternalProvider, "deepseek-default", null, 2, RoutingMode.Balanced, FallbackAction.StopDelegation),
                new BudgetLimits(null, null, null, null, null, "CNY"),
                true,
                now,
                now,
                null)
            {
                ApprovalMode = ExecutionApprovalMode.Automatic,
            };
            var starter = new RecordingStarter();
            var launcher = new NativeCodexLauncher(
                new FixedLocator(),
                new AppDataPaths(root),
                starter,
                new FakeModelResolver());

            var result = await launcher.LaunchAsync(profile, root);

            Assert.Equal(4242, result.ProcessId);
            Assert.NotNull(starter.StartInfo);
            Assert.Empty(Directory.EnumerateFiles(root, "worker-*.toml", SearchOption.AllDirectories));
            var audit = await File.ReadAllTextAsync(Path.Combine(root, "native-codex", $"launch-{profile.Id:D}.json"));
            Assert.Contains("deepseek-default", audit, StringComparison.Ordinal);
            Assert.Contains("\"externalProviderWorkerSupported\": false", audit, StringComparison.Ordinal);
            Assert.Contains("\"workerCapability\": \"Unsupported\"", audit, StringComparison.Ordinal);
            Assert.DoesNotContain(starter.StartInfo!.ArgumentList, argument => argument.Contains("agents.default_subagent_model", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    [Trait("Category", "LiveNative")]
    public async Task Eligible_native_profile_starts_a_real_codex_process_and_writes_a_secret_free_audit()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_NATIVE_LAUNCH_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(Path.GetFullPath(testRoot), $"native-launch-live-{Guid.NewGuid():N}");
        Assert.StartsWith("E:\\", root, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        Process? process = null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var profile = new Profile(
                Guid.NewGuid(),
                "LIVE Terra native",
                new AgentSelection("gpt-5.6-terra", "low"),
                new WorkerPolicy(false, WorkerSource.Disabled, null, null, 1, RoutingMode.Single, FallbackAction.SingleAgent),
                new BudgetLimits(null, null, null, null, null, "CNY"),
                true,
                now,
                now,
                null);
            var launcher = new NativeCodexLauncher(
                new CodexCommandLocator(),
                new AppDataPaths(root),
                new NativeCodexProcessStarter(),
                new CodexModelResolver());

            var result = await launcher.LaunchAsync(profile, root);
            process = Process.GetProcessById(result.ProcessId);
            await Task.Delay(750);
            process.Refresh();
            Assert.False(process.HasExited, "原生 Codex 进程在启动后立即退出。");
            var audit = await File.ReadAllTextAsync(Path.Combine(root, "native-codex", $"launch-{profile.Id:D}.json"));
            Assert.Contains("gpt-5.6-terra", audit);
            Assert.DoesNotContain(Secret, audit);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class RecordingStarter : INativeCodexProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public int Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return 4242;
        }
    }

    private sealed class FixedLocator : CodexCommandLocator
    {
        public override Task<CodexCommandDiscovery> LocateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexCommandDiscovery(CodexCommand.Direct("codex.exe"), "codex-cli 0.146.0", "ready", []));
    }

    private sealed class FakeModelResolver : ICodexModelResolver
    {
        public Task<CodexModelResolution> ResolveAsync(
            CodexAppServerClient client,
            string requestedModelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolve(requestedModelId));

        public Task<CodexModelResolution> ResolveAsync(
            CodexCommand command,
            string requestedModelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolve(requestedModelId));

        private static CodexModelResolution Resolve(string requestedModelId) =>
            new(
                requestedModelId,
                requestedModelId,
                null);
    }

    private sealed class EmptyProviderRepository : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderConfiguration>>([]);
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ProviderConfiguration?>(null);
        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyCredentialStore : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveAsync(string referenceId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
