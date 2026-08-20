using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using System.Text.Json;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class CodexDesktopAppLauncherTests
{
    [Fact]
    public async Task Default_native_mode_launches_the_registered_gui_app_and_never_the_cli()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"desktop-launch-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        try
        {
            var starter = new RecordingDesktopStarter();
            var validator = new PassThroughConfigurationValidator();
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                starter,
                validator);
            var profile = Profile.CreateDefault(DateTimeOffset.UtcNow) with
            {
                MainAgent = new AgentSelection("gpt-5.6-terra", "high"),
                WorkerPolicy = new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
            };

            var result = await launcher.LaunchAsync(profile, project);

            Assert.Equal("OpenAI.Codex_testpublisher!App", starter.AppUserModelId);
            Assert.Null(starter.ExecutablePath);
            Assert.Equal("OpenAI.Codex_testpublisher!App", result.LaunchTarget);
            var config = await File.ReadAllTextAsync(result.ConfigurationPath);
            Assert.Contains("# >>> Codex Agent Switch managed profile >>>", config);
            Assert.Contains("model = \"gpt-5.6-terra\"", config);
            Assert.DoesNotContain("codex.exe", config, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model_provider", config, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model_providers", config, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[agents.cas_luna_worker]", config, StringComparison.Ordinal);
            Assert.DoesNotContain("developer_instructions", config, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.codex_agent_switch]", config, StringComparison.Ordinal);
            Assert.Contains("required = true", config, StringComparison.Ordinal);
            var hooks = await File.ReadAllTextAsync(Path.Combine(project, ".codex", "hooks.json"));
            Assert.Contains("\"matcher\": \"Bash|apply_patch|Edit|Write\"", hooks, StringComparison.Ordinal);
            AssertManagedHookContract(hooks);
            Assert.Contains("\"commandWindows\"", hooks, StringComparison.Ordinal);
            Assert.Contains("\"Stop\"", hooks, StringComparison.Ordinal);
            Assert.Contains("--hook stop", hooks, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("agents.default_subagent", config, StringComparison.OrdinalIgnoreCase);
            var projectInstructions = await File.ReadAllTextAsync(Path.Combine(project, "AGENTS.md"));
            Assert.Contains("Codex Agent Switch managed native worker routing", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("agent_type=\"cas_luna_worker\"", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("fork_turns=\"none\"", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("fork_turns is mandatory", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("never omit it", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("never use fork_turns=\"all\"", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Delegation Capability Preflight", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Initial Delegation Check", projectInstructions, StringComparison.Ordinal);
            Assert.True(
                projectInstructions.IndexOf("Delegation Capability Preflight", StringComparison.Ordinal)
                    < projectInstructions.IndexOf("Initial Delegation Check", StringComparison.Ordinal));
            Assert.Contains("INITIAL_LOCALIZATION_COMPLETE", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("WORKER_RESULT_RECEIVED", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("WORKER_REVIEW_COMPLETE", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("BUILD_TEST_BOUNDED_FIXES", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Queue relevant triggers", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Hard Gate remains the backstop", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Never duplicate DELEGATED or RUNNING Worker work", projectInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("cost-window", projectInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lease metadata", projectInstructions, StringComparison.OrdinalIgnoreCase);
            var managedStart = projectInstructions.IndexOf("<!-- >>> Codex Agent Switch managed", StringComparison.Ordinal);
            var managedEnd = projectInstructions.IndexOf("<!-- <<< Codex Agent Switch managed", StringComparison.Ordinal);
            Assert.True(managedStart >= 0 && managedEnd > managedStart);
            var managedBlock = projectInstructions[managedStart..];
            Assert.True(managedBlock.Length < 4_657, $"Managed block was not reduced from the audited 4,657 characters: {managedBlock.Length}.");
            Assert.True(managedBlock.Split('\n').Length < 25, "Managed block line count was not reduced from the audited 25 lines.");
            var workerPath = Path.Combine(project, ".codex", "agents", "cas-luna-worker.toml");
            var worker = await File.ReadAllTextAsync(workerPath);
            Assert.Contains("name = \"cas_luna_worker\"", worker, StringComparison.Ordinal);
            Assert.Contains("model = \"gpt-5.6-luna\"", worker, StringComparison.Ordinal);
            Assert.Single(validator.Candidates);
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
    public async Task Native_worker_routing_is_added_to_the_documented_project_instruction_file_and_is_reversible()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-project-instructions-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDirectory);
        const string originalInstructions = "# Existing project guidance\n\nKeep user instructions.\n";
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "AGENTS.md"), originalInstructions);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                new RecordingDesktopStarter(),
                new PassThroughConfigurationValidator());
            var project = new AgentProject("project", "Project", projectDirectory, false, now, now);
            var profile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
            };

            var applied = Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project]));

            Assert.True(applied.Succeeded, applied.ErrorMessage);
            var effectiveProjectInstructions = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "AGENTS.md"));
            Assert.Contains(originalInstructions.Trim(), effectiveProjectInstructions, StringComparison.Ordinal);
            Assert.Contains("agent_type=\"cas_luna_worker\"", effectiveProjectInstructions, StringComparison.Ordinal);
            Assert.Contains("fork_turns=\"none\"", effectiveProjectInstructions, StringComparison.Ordinal);
            Assert.Contains("never use fork_turns=\"all\"", effectiveProjectInstructions, StringComparison.Ordinal);

            var restore = await launcher.RestoreProjectConfigurationAsync(project with
            {
                NativeCodexAdaptation = new NativeCodexProjectAdaptation(
                    profile.Id, profile.Name, applied.ConfigurationPath, applied.BackupPath, now, "test", true),
            });

            Assert.True(restore.Succeeded, restore.ErrorMessage);
            Assert.Equal(originalInstructions, await File.ReadAllTextAsync(Path.Combine(projectDirectory, "AGENTS.md")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Managed_worker_routing_replaces_backend_instructions_and_disabled_worker_restores_user_content()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-worker-routing-switch-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDirectory);
        var originalInstructions = string.Join(Environment.NewLine, "# User guidance", string.Empty, "Keep this text unchanged.", string.Empty);
        var instructionsPath = Path.Combine(projectDirectory, "AGENTS.md");
        await File.WriteAllTextAsync(instructionsPath, originalInstructions);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                new RecordingDesktopStarter(),
                new PassThroughConfigurationValidator());
            var project = new AgentProject("project", "Project", projectDirectory, false, now, now);
            var nativeProfile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
            };
            var externalProfile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(true, WorkerSource.ExternalProvider, "deepseek-default", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            };
            var disabledProfile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(false, WorkerSource.Disabled, null, null, 0, RoutingMode.Single, FallbackAction.SingleAgent),
            };

            Assert.True(Assert.Single(await launcher.ApplyToProjectsAsync(nativeProfile, [project])).Succeeded);
            var nativeInstructions = await File.ReadAllTextAsync(instructionsPath);
            Assert.StartsWith(originalInstructions, nativeInstructions, StringComparison.Ordinal);
            Assert.Contains("Codex Agent Switch managed native worker routing", nativeInstructions, StringComparison.Ordinal);
            Assert.Contains("spawn_agent", nativeInstructions, StringComparison.Ordinal);
            Assert.Contains("cas_luna_worker", nativeInstructions, StringComparison.Ordinal);

            Assert.True(Assert.Single(await launcher.ApplyToProjectsAsync(externalProfile, [project])).Succeeded);
            var externalInstructions = await File.ReadAllTextAsync(instructionsPath);
            Assert.StartsWith(originalInstructions, externalInstructions, StringComparison.Ordinal);
            Assert.Contains("Codex Agent Switch managed external worker routing", externalInstructions, StringComparison.Ordinal);
            Assert.Contains("Delegation Capability Preflight", externalInstructions, StringComparison.Ordinal);
            Assert.Contains("Initial Delegation Check", externalInstructions, StringComparison.Ordinal);
            Assert.Contains("delegate_worker", externalInstructions, StringComparison.Ordinal);
            Assert.Contains("consume_worker_result", externalInstructions, StringComparison.Ordinal);
            Assert.Contains("do not poll", externalInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("spawn_agent", externalInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("cas_luna_worker", externalInstructions, StringComparison.Ordinal);

            Assert.True(Assert.Single(await launcher.ApplyToProjectsAsync(nativeProfile, [project])).Succeeded);
            var restoredNativeInstructions = await File.ReadAllTextAsync(instructionsPath);
            Assert.Contains("Codex Agent Switch managed native worker routing", restoredNativeInstructions, StringComparison.Ordinal);
            Assert.Contains("spawn_agent", restoredNativeInstructions, StringComparison.Ordinal);
            Assert.Contains("cas_luna_worker", restoredNativeInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("managed external worker routing", restoredNativeInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("ExternalWorkerExecutor", restoredNativeInstructions, StringComparison.Ordinal);

            Assert.True(Assert.Single(await launcher.ApplyToProjectsAsync(disabledProfile, [project])).Succeeded);
            Assert.Equal(originalInstructions, await File.ReadAllTextAsync(instructionsPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Disabled_worker_policy_preserves_unrelated_hooks_and_removes_only_managed_hook()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-hooks-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, ".codex"));
        var hooksPath = Path.Combine(projectDirectory, ".codex", "hooks.json");
        await File.WriteAllTextAsync(hooksPath, "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Other\",\"hooks\":[{\"type\":\"command\",\"commandWindows\":\"other.exe\"}]}],\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"commandWindows\":\"other-stop.exe\"}]},{\"hooks\":[{\"type\":\"command\",\"commandWindows\":\"CodexAgentSwitch.ToolHost.exe --hook stop\"}]}]}}");
        try
        {
            var launcher = CreateLauncher(new AppDataPaths(Path.Combine(root, "app-data")), new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"), new RecordingDesktopStarter(), new PassThroughConfigurationValidator());
            var now = DateTimeOffset.UtcNow;
            var profile = Profile.CreateDefault(now) with { WorkerPolicy = new WorkerPolicy(false, WorkerSource.Disabled, null, null, 0, RoutingMode.Single, FallbackAction.SingleAgent) };
            var project = new AgentProject("hooks", "Hooks", projectDirectory, false, now, now);
            var result = await launcher.ApplyToProjectsAsync(profile, [project]);
            Assert.True(Assert.Single(result).Succeeded);
            var hooks = await File.ReadAllTextAsync(hooksPath);
            Assert.Contains("other.exe", hooks, StringComparison.Ordinal);
            Assert.Contains("other-stop.exe", hooks, StringComparison.Ordinal);
            Assert.DoesNotContain("--hook pre-tool-use", hooks, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--hook stop", hooks, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Existing_commandWindows_only_managed_hooks_are_migrated_without_touching_unrelated_hooks()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-hooks-migration-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var codexDirectory = Path.Combine(projectDirectory, ".codex");
        Directory.CreateDirectory(codexDirectory);
        var hooksPath = Path.Combine(codexDirectory, "hooks.json");
        const string oldHooks = """
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "Other", "hooks": [{ "type": "command", "command": "other.exe --safe" }] },
                  { "matcher": "Bash|apply_patch|Edit|Write", "hooks": [{ "type": "command", "commandWindows": "CodexAgentSwitch.ToolHost.exe --hook pre-tool-use --pipe legacy-pipe" }] }
                ],
                "Stop": [
                  { "hooks": [{ "type": "command", "command": "other-stop.exe" }] },
                  { "hooks": [{ "type": "command", "commandWindows": "CodexAgentSwitch.ToolHost.exe --hook stop --pipe legacy-pipe" }] }
                ]
              }
            }
            """;
        await File.WriteAllTextAsync(hooksPath, oldHooks);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcher = CreateLauncher(new AppDataPaths(Path.Combine(root, "app-data")), new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"), new RecordingDesktopStarter(), new PassThroughConfigurationValidator());
            var profile = Profile.CreateDefault(now) with { WorkerPolicy = new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent) };
            var project = new AgentProject("hooks-migration", "Hooks migration", projectDirectory, false, now, now);

            Assert.True(Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project])).Succeeded);
            var migrated = await File.ReadAllTextAsync(hooksPath);
            AssertManagedHookContract(migrated);
            Assert.Contains("other.exe --safe", migrated, StringComparison.Ordinal);
            Assert.Contains("other-stop.exe", migrated, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy-pipe", migrated, StringComparison.Ordinal);

            Assert.True(Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project])).Succeeded);
            Assert.Equal(migrated, await File.ReadAllTextAsync(hooksPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Malformed_unrelated_hooks_are_not_overwritten()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-hooks-malformed-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var codexDirectory = Path.Combine(projectDirectory, ".codex");
        Directory.CreateDirectory(codexDirectory);
        var hooksPath = Path.Combine(codexDirectory, "hooks.json");
        const string malformed = "{ not-json";
        await File.WriteAllTextAsync(hooksPath, malformed);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcher = CreateLauncher(new AppDataPaths(Path.Combine(root, "app-data")), new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"), new RecordingDesktopStarter(), new PassThroughConfigurationValidator());
            var profile = Profile.CreateDefault(now) with { WorkerPolicy = new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent) };
            var project = new AgentProject("hooks-malformed", "Hooks malformed", projectDirectory, false, now, now);

            var result = Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project]));

            Assert.False(result.Succeeded);
            Assert.Contains("not valid JSON", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(malformed, await File.ReadAllTextAsync(hooksPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cli_executable_cannot_be_saved_as_a_desktop_entry()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"desktop-entry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cliPath = Path.Combine(root, "codex.exe");
        await File.WriteAllTextAsync(cliPath, "not a desktop application");
        try
        {
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration(null),
                new RecordingDesktopStarter(),
                new PassThroughConfigurationValidator());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.SaveManualExecutableAsync(cliPath));

            Assert.Contains("CLI", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task Desktop_only_launch_does_not_write_or_select_a_project()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var starter = new RecordingDesktopStarter();
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                starter,
                new PassThroughConfigurationValidator());

            var target = await launcher.LaunchDesktopAsync();

            Assert.Equal("OpenAI.Codex_testpublisher!App", target);
            Assert.Equal(target, starter.AppUserModelId);
            Assert.Empty(Directory.EnumerateFiles(root, "config.toml", SearchOption.AllDirectories));
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
    public async Task Batch_adaptation_only_writes_selected_projects_preserves_unrelated_settings_and_keeps_a_restore_backup()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        Assert.StartsWith("E:\\", testRoot, StringComparison.OrdinalIgnoreCase);
        var root = Path.Combine(testRoot, $"desktop-batch-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(first, ".codex"));
        Directory.CreateDirectory(second);
        const string original = "custom_user_setting = true\n";
        await File.WriteAllTextAsync(Path.Combine(first, ".codex", "config.toml"), original);
        try
        {
            var starter = new RecordingDesktopStarter();
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                starter,
                new PassThroughConfigurationValidator());
            var now = DateTimeOffset.UtcNow;
            var profile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(false, WorkerSource.Disabled, null, null, 0, RoutingMode.Single, FallbackAction.SingleAgent),
            };
            var firstProject = new AgentProject("first", "First", first, false, now, now);
            var secondProject = new AgentProject("second", "Second", second, false, now, now);
            var missingProject = new AgentProject("missing", "Missing", Path.Combine(root, "missing"), false, now, now);

            var result = await launcher.ApplyToProjectsAndLaunchAsync(profile, [firstProject, secondProject, missingProject]);

            Assert.True(result.DesktopStarted);
            Assert.Equal("OpenAI.Codex_testpublisher!App", starter.AppUserModelId);
            Assert.True(result.Projects.Count(item => item.Succeeded) == 2, string.Join(" | ", result.Projects.Select(item => $"{item.Project.Name}:{item.ErrorMessage}")));
            var firstResult = Assert.Single(result.Projects, item => item.Project.Id == firstProject.Id);
            Assert.NotNull(firstResult.BackupPath);
            Assert.Equal(original, await File.ReadAllTextAsync(firstResult.BackupPath!));
            var firstConfig = await File.ReadAllTextAsync(firstResult.ConfigurationPath);
            Assert.Contains(original.Trim(), firstConfig, StringComparison.Ordinal);
            Assert.Contains("# >>> Codex Agent Switch managed profile >>>", firstConfig, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(missingProject.WorkingDirectory, ".codex", "config.toml")));

            var restore = await launcher.RestoreProjectConfigurationAsync(firstProject with
            {
                NativeCodexAdaptation = new NativeCodexProjectAdaptation(
                    profile.Id, profile.Name, firstResult.ConfigurationPath, firstResult.BackupPath, now, "test", true),
            });
            Assert.True(restore.Succeeded);
            Assert.Equal(original, await File.ReadAllTextAsync(firstResult.ConfigurationPath));
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
    public async Task Validation_failure_keeps_the_original_project_configuration_byte_for_byte()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-validation-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var configDirectory = Path.Combine(projectDirectory, ".codex");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "config.toml");
        var original = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'1', (byte)'\n' };
        await File.WriteAllBytesAsync(configPath, original);
        try
        {
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                new RecordingDesktopStarter(),
                new RejectingConfigurationValidator());
            var now = DateTimeOffset.UtcNow;
            var project = new AgentProject("project", "Project", projectDirectory, false, now, now);
            var profile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(false, WorkerSource.Disabled, null, null, 0, RoutingMode.Single, FallbackAction.SingleAgent),
            };

            var result = Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project]));

            Assert.False(result.Succeeded);
            Assert.Equal(original, await File.ReadAllBytesAsync(configPath));
            Assert.Empty(Directory.EnumerateFiles(configDirectory, "*.tmp"));
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
    public async Task Native_external_worker_is_gated_and_never_registered_as_a_runnable_custom_agent()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-external-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                new RecordingDesktopStarter(),
                new PassThroughConfigurationValidator());
            var project = new AgentProject("project", "Project", projectDirectory, false, now, now);
            var profile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(true, WorkerSource.ExternalProvider, "deepseek-default", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            };

            var result = Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project]));

            Assert.True(result.Succeeded, result.ErrorMessage);
            var projectConfiguration = await File.ReadAllTextAsync(Path.Combine(projectDirectory, ".codex", "config.toml"));
            Assert.Contains("agents.enabled = false", projectConfiguration, StringComparison.Ordinal);
            Assert.Contains("Native external collaboration remains gated", projectConfiguration, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.codex_agent_switch]", projectConfiguration, StringComparison.Ordinal);
            Assert.DoesNotContain("developer_instructions", projectConfiguration, StringComparison.Ordinal);
            Assert.DoesNotContain("workerId='deepseek-default'", projectConfiguration, StringComparison.Ordinal);
            Assert.DoesNotContain("model_provider", projectConfiguration, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model_providers", projectConfiguration, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(projectDirectory, ".codex", "agents", "cas-external-worker.toml")));
            var projectInstructions = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "AGENTS.md"));
            Assert.Contains("Codex Agent Switch managed external worker routing", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Delegation Capability Preflight", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("Initial Delegation Check", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("delegate_worker", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("consume_worker_result", projectInstructions, StringComparison.Ordinal);
            Assert.Contains("do not poll", projectInstructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("omit workerId", projectInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("spawn_agent", projectInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("cas_luna_worker", projectInstructions, StringComparison.Ordinal);
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
    public async Task User_managed_worker_conflict_returns_specific_path_and_preserves_project_config()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-worker-conflict-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var codexDirectory = Path.Combine(projectDirectory, ".codex");
        var agentPath = Path.Combine(codexDirectory, "agents", "cas-luna-worker.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        const string originalConfig = "model = \"gpt-5.6-terra\"\n";
        await File.WriteAllTextAsync(Path.Combine(codexDirectory, "config.toml"), originalConfig);
        await File.WriteAllTextAsync(agentPath, "name = \"user_luna\"\nmodel = \"gpt-5.6-luna\"\n");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                new RecordingDesktopStarter(),
                new PassThroughConfigurationValidator());
            var project = new AgentProject("project", "Project", projectDirectory, false, now, now);
            var result = Assert.Single(await launcher.ApplyToProjectsAsync(Profile.CreateDefault(now), [project]));

            Assert.False(result.Succeeded);
            Assert.Contains("cas-luna-worker.toml", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("防止覆盖用户文件", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(originalConfig, await File.ReadAllTextAsync(Path.Combine(codexDirectory, "config.toml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CodexDesktopAppLauncher CreateLauncher(
        AppDataPaths paths,
        ICodexDesktopAppRegistration registration,
        ICodexDesktopProcessStarter starter,
        ICodexProjectConfigurationValidator validator) =>
        new(
            paths,
            registration,
            starter,
            new FixedLocator(),
            new PassThroughModelResolver(),
            validator);

    private static void AssertManagedHookContract(string json)
    {
        using var document = JsonDocument.Parse(json);
        var hooks = document.RootElement.GetProperty("hooks");
        AssertManagedHookGroup(hooks.GetProperty("PreToolUse"), "pre-tool-use");
        AssertManagedHookGroup(hooks.GetProperty("Stop"), "stop");
    }

    private static void AssertManagedHookGroup(JsonElement groups, string expectedHook)
    {
        var handlers = groups.EnumerateArray()
            .SelectMany(group => group.GetProperty("hooks").EnumerateArray())
            .Where(handler => handler.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String
                && command.GetString()!.Contains("CodexAgentSwitch.ToolHost.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var handler = Assert.Single(handlers);
        Assert.Equal("command", handler.GetProperty("type").GetString());
        var command = handler.GetProperty("command").GetString();
        var windows = handler.GetProperty("commandWindows").GetString();
        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.Equal(command, windows);
        Assert.Contains($"--hook {expectedHook}", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--pipe CodexAgentSwitch-Scheduler-", command, StringComparison.Ordinal);
    }

    private sealed class RecordingDesktopStarter : ICodexDesktopProcessStarter
    {
        public string? AppUserModelId { get; private set; }

        public string? ExecutablePath { get; private set; }

        public void StartAppsFolder(string appUserModelId) => AppUserModelId = appUserModelId;

        public void StartExecutable(string executablePath) => ExecutablePath = executablePath;
    }

    private sealed class FixedDesktopRegistration(string? appUserModelId) : ICodexDesktopAppRegistration
    {
        public string? FindAppUserModelId() => appUserModelId;
    }

    private sealed class FixedLocator : CodexCommandLocator
    {
        public override Task<CodexCommandDiscovery> LocateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexCommandDiscovery(CodexCommand.Direct("codex.exe"), "codex-cli test", "ready", []));
    }

    private sealed class PassThroughModelResolver : ICodexModelResolver
    {
        public Task<CodexModelResolution> ResolveAsync(CodexAppServerClient client, string requestedModelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexModelResolution(requestedModelId, requestedModelId, null));

        public Task<CodexModelResolution> ResolveAsync(CodexCommand command, string requestedModelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodexModelResolution(requestedModelId, requestedModelId, null));
    }

    private sealed class PassThroughConfigurationValidator : ICodexProjectConfigurationValidator
    {
        public List<string> Candidates { get; } = [];

        public Task ValidateAsync(CodexCommand command, string candidateToml, CancellationToken cancellationToken = default)
        {
            Candidates.Add(candidateToml);
            Assert.DoesNotContain("model_provider", candidateToml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model_providers", candidateToml, StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        }

        public Task ValidateLayeredAsync(CodexCommand command, CodexConfigurationLayers candidate, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("model_provider", candidate.ProjectToml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model_providers", candidate.ProjectToml, StringComparison.OrdinalIgnoreCase);
            Candidates.Add(candidate.ProjectToml);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingConfigurationValidator : ICodexProjectConfigurationValidator
    {
        public Task ValidateAsync(CodexCommand command, string candidateToml, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("invalid type: map, expected a string");
    }

}
