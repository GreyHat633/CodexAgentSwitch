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
            ["hooks.json"] = "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe test-pipe\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe test-pipe\"}]}],\"PostToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook post-tool-use --pipe test-pipe\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook post-tool-use --pipe test-pipe\"}]}],\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook stop --pipe test-pipe\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook stop --pipe test-pipe\"}]}]}}",
        });
        Assert.True(report.HooksPresent);
        Assert.True(report.PreToolUseConfigured);
        Assert.True(report.PostToolUseConfigured);
        Assert.True(report.StopConfigured);
        Assert.Null(report.ValidationError);
        Assert.Contains("trust", report.ReviewNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user-controlled", report.ReviewNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"hooks\":{\"PreToolUse\":[{\"hooks\":[{\"type\":\"command\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe test\"}]}]}}")]
    [InlineData("{\"hooks\":{\"PreToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"   \"}]}]}}")]
    [InlineData("{\"hooks\":{\"PreToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook stop --pipe test\"}]}]}}")]
    [InlineData("{ not-json")]
    public void Hook_report_rejects_invalid_or_commandWindows_only_contracts(string hooks)
    {
        var report = CodexProjectConfigurationValidator.ReportHooks(new Dictionary<string, string> { ["hooks.json"] = hooks });

        Assert.True(report.HooksPresent);
        Assert.False(report.PreToolUseConfigured);
        Assert.False(report.StopConfigured);
        Assert.False(string.IsNullOrWhiteSpace(report.ValidationError));
    }

    [Fact]
    public void Hook_report_accepts_command_without_optional_windows_override()
    {
        const string hooks = "{\"hooks\":{\"PreToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe test\"}]}],\"PostToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook post-tool-use --pipe test\"}]}],\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook stop --pipe test\"}]}]}}";

        var report = CodexProjectConfigurationValidator.ReportHooks(new Dictionary<string, string> { ["hooks.json"] = hooks });

        Assert.True(report.PreToolUseConfigured);
        Assert.True(report.PostToolUseConfigured);
        Assert.True(report.StopConfigured);
        Assert.Null(report.ValidationError);
    }

    [Fact]
    public void ToolHost_hook_entry_is_frozen_fail_open_and_contains_no_denial_branch()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "CodexAgentSwitch.ToolHost", "Program.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("RunFrozenHookNoOpAsync", source, StringComparison.Ordinal);
        Assert.Contains("without parsing, telemetry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunPreToolUseHookAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", source, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(null)]
    [InlineData(150_000)]
    [InlineData(180_000)]
    [InlineData(200_000)]
    [Trait("Category", "LiveCodexConfig")]
    public async Task Current_codex_strict_config_accepts_every_profile_auto_compact_preset(int? limit)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CODEX_CONFIG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"codex-auto-compact-{limit?.ToString() ?? "default"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var command = (await new CodexCommandLocator().LocateAsync()).Command
                ?? throw new InvalidOperationException("当前测试环境没有可执行的 Codex CLI。");
            var validator = new CodexProjectConfigurationValidator(new AppDataPaths(root));
            var projectConfiguration = "model = \"gpt-5.6-sol\"\nmodel_reasoning_effort = \"high\"\n"
                + (limit is int value ? $"model_auto_compact_token_limit = {value}\n" : string.Empty)
                + "approval_policy = \"never\"\nsandbox_mode = \"danger-full-access\"\n";

            await validator.ValidateAsync(command, projectConfiguration);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "LiveCodexConfig")]
    public async Task Current_codex_strict_config_accepts_a_project_layer_containing_the_valid_hook_fixture()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_CODEX_CONFIG_E2E"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"codex-hook-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var command = (await new CodexCommandLocator().LocateAsync()).Command
                ?? throw new InvalidOperationException("当前测试环境没有可执行的 Codex CLI。");
            var validator = new CodexProjectConfigurationValidator(new AppDataPaths(root));
            var fixtures = Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "native-codex");
            var projectConfiguration = await File.ReadAllTextAsync(Path.Combine(fixtures, "valid-project-whitelist.toml"));
            const string validHooks = "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"apply_patch|Edit|Write\",\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe contract-test\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe contract-test\"}]}],\"PostToolUse\":[{\"matcher\":\"apply_patch|Edit|Write\",\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook post-tool-use --pipe contract-test\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook post-tool-use --pipe contract-test\"}]}],\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"CodexAgentSwitch.ToolHost.exe --hook stop --pipe contract-test\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook stop --pipe contract-test\"}]}]}}";

            // This proves the layered candidate remains loadable, but it is
            // deliberately not a negative hook-parser oracle: app-server
            // strict config does not reject commandWindows-only hook files.
            await validator.ValidateLayeredAsync(command, new CodexConfigurationLayers(
                projectConfiguration,
                null,
                new Dictionary<string, string> { ["hooks.json"] = validHooks }));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
