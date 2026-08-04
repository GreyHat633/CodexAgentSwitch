using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;

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
            Assert.Contains("agents.default_subagent", config, StringComparison.OrdinalIgnoreCase);
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
    public async Task External_worker_profile_registers_user_provider_and_project_role_without_project_provider_table()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"desktop-external-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDirectory);
        var codexHome = Path.Combine(root, "codex-home");
        var brokerPath = Path.Combine(root, "broker.exe");
        await File.WriteAllTextAsync(brokerPath, "test broker");
        var previousHome = Environment.GetEnvironmentVariable("CAS_CODEX_HOME");
        var previousBroker = Environment.GetEnvironmentVariable("CAS_NATIVE_CREDENTIAL_BROKER");
        Environment.SetEnvironmentVariable("CAS_CODEX_HOME", codexHome);
        Environment.SetEnvironmentVariable("CAS_NATIVE_CREDENTIAL_BROKER", brokerPath);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var provider = ProviderConfiguration.DeepSeekPreset(now) with
            {
                IsEnabled = true,
                CredentialReference = "provider/deepseek",
            };
            var providerRepository = new InMemoryProviderRepository([provider]);
            var launcher = CreateLauncher(
                new AppDataPaths(Path.Combine(root, "app-data")),
                new FixedDesktopRegistration("OpenAI.Codex_testpublisher!App"),
                new RecordingDesktopStarter(),
                new PassThroughConfigurationValidator(),
                providerRepository,
                new AvailableCredentialStore());
            var project = new AgentProject("project", "Project", projectDirectory, false, now, now);
            var profile = Profile.CreateDefault(now) with
            {
                WorkerPolicy = new WorkerPolicy(true, WorkerSource.ExternalProvider, "deepseek-default", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            };

            var result = Assert.Single(await launcher.ApplyToProjectsAsync(profile, [project]));

            Assert.True(result.Succeeded, result.ErrorMessage);
            var projectConfiguration = await File.ReadAllTextAsync(Path.Combine(projectDirectory, ".codex", "config.toml"));
            var agentConfiguration = await File.ReadAllTextAsync(Path.Combine(projectDirectory, ".codex", "agents", "cas-external-worker.toml"));
            var userConfiguration = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
            Assert.Contains("[agents.cas_external_worker]", projectConfiguration, StringComparison.Ordinal);
            Assert.DoesNotContain("model_provider", projectConfiguration, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model_providers", projectConfiguration, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("model_provider = \"cas_deepseek_default\"", agentConfiguration, StringComparison.Ordinal);
            Assert.Contains("[model_providers.cas_deepseek_default]", userConfiguration, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(brokerPath), userConfiguration, StringComparison.Ordinal);
            Assert.DoesNotContain("test-secret", userConfiguration, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAS_CODEX_HOME", previousHome);
            Environment.SetEnvironmentVariable("CAS_NATIVE_CREDENTIAL_BROKER", previousBroker);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CodexDesktopAppLauncher CreateLauncher(
        AppDataPaths paths,
        ICodexDesktopAppRegistration registration,
        ICodexDesktopProcessStarter starter,
        ICodexProjectConfigurationValidator validator,
        IProviderRepository? providers = null,
        ICredentialStore? credentials = null) =>
        new(
            paths,
            registration,
            starter,
            new FixedLocator(),
            new PassThroughModelResolver(),
            providers ?? new InMemoryProviderRepository([]),
            credentials ?? new AvailableCredentialStore(),
            validator);

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

    private sealed class InMemoryProviderRepository(IReadOnlyList<ProviderConfiguration> providers) : IProviderRepository
    {
        private readonly Dictionary<string, ProviderConfiguration> values = providers.ToDictionary(provider => provider.Id, StringComparer.Ordinal);

        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderConfiguration>>(values.Values.ToArray());

        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.TryGetValue(id, out var provider) ? provider : null);

        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default)
        {
            values[provider.Id] = provider;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            values.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class AvailableCredentialStore : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task SaveAsync(string referenceId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-secret");
        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
