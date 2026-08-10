using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.App.ViewModels;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Credentials;
using CodexAgentSwitch.Infrastructure.ExternalProviders;
using CodexAgentSwitch.Infrastructure.ExternalAgents;
using CodexAgentSwitch.Infrastructure.Persistence;
using CodexAgentSwitch.Infrastructure.Scheduling;
using CodexAgentSwitch.Infrastructure.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace CodexAgentSwitch.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private TrayIconService? trayIcon;

    public static IServiceProvider Services { get; private set; } = null!;

    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        var paths = AppDataPaths.Resolve();
        paths.EnsureCreated();
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(new SqliteDatabase(paths.DatabasePath));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IProviderRepository, SqliteProviderRepository>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<OpenAiCompatibleClient>();
        services.AddSingleton<IExternalProviderClient>(provider => provider.GetRequiredService<OpenAiCompatibleClient>());
        services.AddSingleton<IExternalToolHost, LocalExternalToolHost>();
        services.AddSingleton<IExternalWorkerAdapterFactory, ExternalWorkerAdapterFactory>();
        services.AddSingleton<ProviderConfigurationValidator>();
        services.AddSingleton<ScopeRegistry>();
        services.AddSingleton<DelegationGate>();
        services.AddSingleton<AdoptionLedger>();
        services.AddSingleton<EconomicCheckpointPolicy>();
        services.AddSingleton<EconomicPolicyV2>();
        services.AddSingleton<WorkerRoutingService>();
        services.AddSingleton<IUsageLedgerRepository, SqliteUsageLedgerRepository>();
        services.AddSingleton<IControlledTaskRepository, SqliteControlledTaskRepository>();
        services.AddSingleton<BudgetPolicy>();
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<EconomicReportService>();
        services.AddSingleton<IWorkerUsageCollector, WorkerUsageCollector>();
        services.AddSingleton<IUsageSource, CodexSessionUsageSource>();
        services.AddSingleton<SafeWorkerDeletionCoordinator>();
        services.AddSingleton<ProfileValidator>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<ProfileMigrationService>();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<CodexCommandLocator>();
        services.AddSingleton<ICodexModelResolver, CodexModelResolver>();
        services.AddSingleton<INativeCodexProcessStarter, NativeCodexProcessStarter>();
        services.AddSingleton<INativeCodexLauncher, NativeCodexLauncher>();
        services.AddSingleton<ICodexDesktopAppRegistration, RegistryCodexDesktopAppRegistration>();
        services.AddSingleton<ICodexDesktopProcessStarter, CodexDesktopProcessStarter>();
        services.AddSingleton<ICodexProjectConfigurationValidator, CodexProjectConfigurationValidator>();
        services.AddSingleton<ICodexDesktopLauncher, CodexDesktopAppLauncher>();
        services.AddSingleton(new CodexSchemaCache(paths.ProtocolCacheDirectory));
        services.AddSingleton<CodexRuntimeManager>();
        services.AddSingleton<IControlledTaskRuntime, ControlledTaskRuntime>();
        services.AddSingleton<TaskProfileSnapshotFactory>();
        services.AddSingleton<DelegationDecisionService>();
        services.AddSingleton<ExternalProviderResolver>();
        services.AddSingleton<WorkerOrchestrator>();
        services.AddSingleton<ControlledTaskService>();
        services.AddSingleton<ISchedulerTaskRepository, SqliteSchedulerTaskRepository>();
        services.AddSingleton<IWorkerExecutor, NativeWorkerExecutor>();
        services.AddSingleton<IWorkerExecutor, ExternalWorkerExecutor>();
        services.AddSingleton<AppliedProjectWorkerGuard>();
        services.AddSingleton<ITaskPacketResolver>(provider => provider.GetRequiredService<AppliedProjectWorkerGuard>());
        services.AddSingleton<IDelegationPolicyGuard>(provider => provider.GetRequiredService<AppliedProjectWorkerGuard>());
        services.AddSingleton<ISchedulerResultObserver, SchedulerUsageRecorder>();
        services.AddSingleton<IWorkerScheduler, WorkerScheduler>();
        #if DEBUG
        var mockUiScenario = Environment.GetEnvironmentVariable("CAS_UI_MOCK_STATE");
        if (!string.IsNullOrWhiteSpace(mockUiScenario))
        {
            services.AddSingleton<IAgentSwitchUiStateSource>(new MockAgentSwitchUiStateSource(mockUiScenario));
        }
        else
        #endif
        {
            services.AddSingleton<IAgentSwitchUiStateSource, AgentSwitchUiStateProjection>();
        }
        services.AddSingleton<SchedulerIpcServer>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        Services = services.BuildServiceProvider(validateScopes: true);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var database = Services.GetRequiredService<SqliteDatabase>();
            await database.InitializeAsync();
            await Services.GetRequiredService<ProfileMigrationService>().MigrateAllAsync();
            var profileService = Services.GetRequiredService<ProfileService>();
            await profileService.EnsureDefaultAsync();
            await EnsureBuiltInProvidersAsync();
            await EnsureProjectMigrationAsync();
            await EnsureOnboardingStateAsync();
            var scheduler = Services.GetRequiredService<IWorkerScheduler>();
            await scheduler.StartAsync();
            await Services.GetRequiredService<SchedulerIpcServer>().StartAsync();
            _window = new MainWindow();
            MainWindow = _window;
            trayIcon = new TrayIconService(_window, scheduler);
            _window.Activate();
        }
        catch (Exception exception)
        {
            try
            {
                var paths = Services.GetRequiredService<AppDataPaths>();
                paths.EnsureCreated();
                File.WriteAllText(
                    Path.Combine(paths.LogsDirectory, "startup-crash.txt"),
                    DiagnosticBundleExporter.Redact(exception.ToString()));
            }
            catch { }

            throw;
        }
    }

    private async Task EnsureBuiltInProvidersAsync()
    {
        var repository = Services.GetRequiredService<IProviderRepository>();
        var profileRepository = Services.GetRequiredService<IProfileRepository>();
        var clock = Services.GetRequiredService<IClock>();
        if (await repository.GetAsync("native-codex") is null)
        {
            await repository.UpsertAsync(ProviderConfiguration.Native(clock.UtcNow));
        }

        var deepSeek = await repository.GetAsync("deepseek-default");
        if (deepSeek is null)
        {
            await repository.UpsertAsync(ProviderConfiguration.DeepSeekPreset(clock.UtcNow));
        }
        else
        {
            var migrated = DeepSeekV4Migration.Migrate(deepSeek);
            if (migrated.Kind == ProviderKind.DeepSeek && migrated.Id == "deepseek-default")
            {
                migrated = migrated with
                {
                    BaseUri = new Uri(DeepSeekV4Catalog.BaseUrl),
                    ModelId = string.IsNullOrWhiteSpace(migrated.ModelId)
                        ? DeepSeekV4Catalog.FlashModelId
                        : migrated.ModelId,
                };
            }

            if (!Equals(migrated, deepSeek))
            {
                await repository.UpsertAsync(migrated with { UpdatedAt = clock.UtcNow });
            }
        }

        foreach (var provider in await repository.ListAsync())
        {
            if (provider.Kind != ProviderKind.DeepSeek || provider.Id == "deepseek-default")
            {
                continue;
            }

            var migrated = DeepSeekV4Migration.Migrate(provider);
            if (!Equals(migrated, provider))
            {
                await repository.UpsertAsync(migrated with { UpdatedAt = clock.UtcNow });
            }
        }

        foreach (var profile in await profileRepository.ListAsync())
        {
            var migration = DeepSeekV4Migration.MigrateModel(profile.MainAgent.ModelId);
            if (!migration.Changed)
            {
                continue;
            }

            // Keep the persisted reasoning effort unchanged: deepseek-reasoner was
            // a thinking-intent selection and V4 Flash carries that intent forward.
            await profileRepository.UpsertAsync(DeepSeekV4Migration.Migrate(profile) with { UpdatedAt = clock.UtcNow });
        }
    }

    private async Task EnsureProjectMigrationAsync()
    {
        var taskRepository = Services.GetRequiredService<IControlledTaskRepository>();
        var projectRepository = Services.GetRequiredService<IProjectRepository>();
        var projectService = Services.GetRequiredService<ProjectService>();
        var conversations = await taskRepository.ListAsync();
        var projects = (await projectRepository.ListAsync()).ToList();
        foreach (var group in conversations
                     .Where(conversation => string.IsNullOrWhiteSpace(conversation.ProjectId))
                     .GroupBy(conversation => conversation.WorkingDirectory, StringComparer.OrdinalIgnoreCase))
        {
            var project = projects.FirstOrDefault(candidate =>
                string.Equals(candidate.WorkingDirectory, group.Key, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                if (!Directory.Exists(group.Key))
                {
                    continue;
                }

                var folderName = Path.GetFileName(group.Key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var baseName = string.IsNullOrWhiteSpace(folderName) ? "迁移项目" : $"迁移项目 · {folderName}";
                var name = baseName;
                var suffix = 2;
                while (projects.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    name = $"{baseName} ({suffix++})";
                }

                project = await projectService.CreateAsync(name, group.Key);
                projects.Add(project);
            }

            foreach (var conversation in group)
            {
                await taskRepository.UpsertAsync(conversation with { ProjectId = project.Id });
            }
        }
    }

    private async Task EnsureOnboardingStateAsync()
    {
        var paths = Services.GetRequiredService<AppDataPaths>();
        var statePath = Path.Combine(paths.Root, "onboarding.completed.json");
        if (File.Exists(statePath))
        {
            return;
        }

        // Existing installations already carrying a user-created Profile are not
        // first-run installations. Mark them complete without changing profiles or
        // credentials, while fresh installations still open the five-step wizard.
        var profiles = await Services.GetRequiredService<IProfileRepository>().ListAsync();
        if (profiles.Any(profile => !profile.IsBuiltIn))
        {
            await File.WriteAllTextAsync(
                statePath,
                $"{{\"completedAt\":\"{DateTimeOffset.UtcNow:O}\",\"migratedExistingProfile\":true}}",
                new System.Text.UTF8Encoding(false));
        }
    }
}
