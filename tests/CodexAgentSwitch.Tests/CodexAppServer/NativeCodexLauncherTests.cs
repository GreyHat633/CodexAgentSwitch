using System.Diagnostics;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class NativeCodexLauncherTests
{
    private const string Secret = "native-launch-secret-must-not-be-written";

    [Theory]
    [InlineData(ExecutionApprovalMode.Safe, "untrusted", "read-only")]
    [InlineData(ExecutionApprovalMode.Automatic, "on-request", "workspace-write")]
    [InlineData(ExecutionApprovalMode.FullAuto, "never", "danger-full-access")]
    public async Task External_profile_generates_real_codex_arguments_and_secret_free_audit(
        ExecutionApprovalMode approvalMode,
        string expectedApprovalPolicy,
        string expectedSandbox)
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(Path.GetFullPath(testRoot), $"native-launch-{Guid.NewGuid():N}");
        Assert.StartsWith("E:\\", root, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var provider = new ProviderConfiguration(
                "deepseek-default",
                "DeepSeek",
                ProviderKind.DeepSeek,
                new Uri(DeepSeekV4Catalog.BaseUrl),
                "provider/deepseek-default",
                DeepSeekV4Catalog.FlashModelId,
                new Dictionary<string, string>(),
                TimeSpan.FromSeconds(60),
                true,
                null,
                now,
                now);
            var profile = new Profile(
                Guid.NewGuid(),
                "Sol + DeepSeek",
                new AgentSelection("gpt-5.6-terra", "high"),
                new WorkerPolicy(true, WorkerSource.ExternalProvider, provider.Id, null, 2, RoutingMode.Balanced, FallbackAction.StopDelegation),
                new BudgetLimits(null, null, null, null, null, "CNY"),
                true,
                now,
                now,
                null)
            {
                ApprovalMode = approvalMode,
            };
            var starter = new RecordingStarter();
            var launcher = new NativeCodexLauncher(
                new FixedLocator(),
                new MemoryProviderRepository(provider),
                new FakeCredentialStore(),
                new AppDataPaths(root),
                starter,
                new FakeModelResolver());

            var result = await launcher.LaunchAsync(profile, root);

            Assert.Equal(4242, result.ProcessId);
            Assert.NotNull(starter.StartInfo);
            Assert.Equal(root, starter.StartInfo!.WorkingDirectory);
            Assert.Contains("-m", starter.StartInfo.ArgumentList);
            Assert.Contains("gpt-5.6-terra", starter.StartInfo.ArgumentList);
            Assert.Contains("model_reasoning_effort=\"high\"", starter.StartInfo.ArgumentList);
            Assert.Contains(expectedApprovalPolicy, starter.StartInfo.ArgumentList);
            Assert.Contains(expectedSandbox, starter.StartInfo.ArgumentList);
            Assert.Contains("agents.max_concurrent_threads_per_session=2", starter.StartInfo.ArgumentList);
            Assert.Equal(Secret, starter.StartInfo.Environment["CAS_NATIVE_WORKER_API_KEY"]);
            Assert.True(File.Exists(result.GeneratedConfigurationPath));
            var workerToml = await File.ReadAllTextAsync(result.GeneratedConfigurationPath);
            Assert.Contains("model = \"deepseek-v4-flash\"", workerToml);
            Assert.Contains("model_provider = \"cas_external\"", workerToml);
            Assert.Contains("wire_api = \"responses\"", workerToml);
            Assert.DoesNotContain(Secret, workerToml);
            var allDiskText = string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.DoesNotContain(Secret, allDiskText);
        }
        finally
        {
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

    private sealed class MemoryProviderRepository(ProviderConfiguration provider) : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderConfiguration>>([provider]);

        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderConfiguration?>(string.Equals(id, provider.Id, StringComparison.Ordinal) ? provider : null);

        public Task UpsertAsync(ProviderConfiguration value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SaveAsync(string referenceId, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Secret);

        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
