using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class CodexProjectConfigurationValidatorTests
{
    [Fact]
    public void Hook_report_requires_user_controlled_trust_and_exact_command_review()
    {
        var report = CodexProjectConfigurationValidator.ReportHooks(new Dictionary<string, string>
        {
            ["hooks.json"] = "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\",\"hooks\":[{\"commandWindows\":\"ToolHost.exe\"}]}]}}",
        });
        Assert.True(report.HooksPresent);
        Assert.True(report.PreToolUseConfigured);
        Assert.Contains("trust", report.ReviewNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user-controlled", report.ReviewNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolHost_hook_failure_uses_supported_deny_shape()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "CodexAgentSwitch.ToolHost", "Program.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("permissionDecision = \"deny\"", source, StringComparison.Ordinal);
        Assert.Contains("Agent Switch ownership gate is unavailable; retry after Scheduler recovery.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision = \"ask\"", source, StringComparison.Ordinal);
    }
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
    public async Task Current_codex_loads_a_project_scoped_native_luna_worker_role()
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
            var projectConfiguration = """
                model = "gpt-5.6-sol"
                model_reasoning_effort = "high"
                agents.enabled = true
                agents.max_concurrent_threads_per_session = 3
                developer_instructions = "Delegate bounded work only to cas_luna_worker."

                [agents.cas_luna_worker]
                description = "Use the configured Luna worker for bounded delegated work."
                config_file = "./agents/cas-luna-worker.toml"

                [mcp_servers.codex_agent_switch]
                command = "cmd.exe"
                args = ["/c", "exit", "0"]
                startup_timeout_sec = 5
                tool_timeout_sec = 7200
                enabled = true
                """;
            var agentConfiguration = """
                name = "cas_luna_worker"
                description = "Bounded Luna worker."
                model = "gpt-5.6-luna"
                model_reasoning_effort = "medium"
                developer_instructions = "Return concise, verifiable results."
                """;

            await validator.ValidateLayeredAsync(
                command,
                new CodexConfigurationLayers(
                    projectConfiguration,
                    null,
                    new Dictionary<string, string>
                    {
                        ["agents/cas-luna-worker.toml"] = agentConfiguration,
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
