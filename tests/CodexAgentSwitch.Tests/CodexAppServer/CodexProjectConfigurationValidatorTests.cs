using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class CodexProjectConfigurationValidatorTests
{
    [Fact]
    [Trait("Category", "LiveCodexConfig")]
    public async Task Current_codex_loads_the_whitelisted_fixture_and_rejects_the_legacy_map_fixture()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CODEX_CONFIG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"codex-project-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var command = (await new CodexCommandLocator().LocateAsync()).Command
                ?? throw new InvalidOperationException("当前测试环境没有可执行的 Codex CLI。");
            var validator = new CodexProjectConfigurationValidator(new AppDataPaths(root));
            var fixtures = Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "native-codex");
            var valid = await File.ReadAllTextAsync(Path.Combine(fixtures, "valid-project-whitelist.toml"));
            var invalid = await File.ReadAllTextAsync(Path.Combine(fixtures, "invalid-model-provider-map.toml"));

            await validator.ValidateAsync(command, valid);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateLayeredAsync(
                command,
                new CodexConfigurationLayers(valid, invalid)));

            Assert.Contains("invalid type: map, expected a string", exception.Message, StringComparison.OrdinalIgnoreCase);
            var validationParent = Path.Combine(root, "native-codex", "config-validation");
            Assert.True(!Directory.Exists(validationParent) || !Directory.EnumerateDirectories(validationParent).Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "LiveCodexConfig")]
    public async Task Current_codex_loads_a_user_provider_and_project_external_worker_role()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CODEX_CONFIG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"codex-external-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var command = (await new CodexCommandLocator().LocateAsync()).Command
                ?? throw new InvalidOperationException("当前测试环境没有可执行的 Codex CLI。");
            var validator = new CodexProjectConfigurationValidator(new AppDataPaths(root));
            var userConfiguration = """
                [model_providers.cas_deepseek_default]
                name = "DeepSeek"
                base_url = "https://api.deepseek.com"
                wire_api = "responses"

                [model_providers.cas_deepseek_default.auth]
                command = "E:/agent-switch/CodexAgentSwitch.CredentialBroker.exe"
                args = ["--credential-reference", "provider/deepseek"]
                timeout_ms = 5000
                refresh_interval_ms = 300000
                """;
            var projectConfiguration = """
                model = "gpt-5.6-terra"
                model_reasoning_effort = "high"

                [agents.cas_external_worker]
                description = "Use DeepSeek for bounded delegated work."
                config_file = "./agents/cas-external-worker.toml"
                """;
            var agentConfiguration = """
                name = "cas_external_worker"
                description = "Bounded DeepSeek worker."
                model = "deepseek-v4-flash"
                model_provider = "cas_deepseek_default"
                model_reasoning_effort = "medium"
                developer_instructions = "Return concise, verifiable results."
                """;

            await validator.ValidateLayeredAsync(
                command,
                new CodexConfigurationLayers(
                    projectConfiguration,
                    userConfiguration,
                    new Dictionary<string, string>
                    {
                        ["agents/cas-external-worker.toml"] = agentConfiguration,
                    }));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexAgentSwitch.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("无法定位 CodexAgentSwitch.sln。");
    }
}
