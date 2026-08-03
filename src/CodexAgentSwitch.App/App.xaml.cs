using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Credentials;
using CodexAgentSwitch.Infrastructure.ExternalProviders;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace CodexAgentSwitch.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;

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
        services.AddSingleton<IProviderRepository, SqliteProviderRepository>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<OpenAiCompatibleClient>();
        services.AddSingleton<IExternalProviderClient>(provider => provider.GetRequiredService<OpenAiCompatibleClient>());
        services.AddSingleton<IExternalWorkerAdapterFactory, ExternalWorkerAdapterFactory>();
        services.AddSingleton<ProviderConfigurationValidator>();
        services.AddSingleton<ScopeRegistry>();
        services.AddSingleton<DelegationGate>();
        services.AddSingleton<AdoptionLedger>();
        services.AddSingleton<EconomicCheckpointPolicy>();
        services.AddSingleton<WorkerRoutingService>();
        services.AddSingleton<IUsageLedgerRepository, SqliteUsageLedgerRepository>();
        services.AddSingleton<BudgetPolicy>();
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<EconomicReportService>();
        services.AddSingleton<IWorkerUsageCollector, WorkerUsageCollector>();
        services.AddSingleton<SafeWorkerDeletionCoordinator>();
        services.AddSingleton<ProfileValidator>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<CodexCommandLocator>();
        services.AddSingleton(new CodexSchemaCache(paths.ProtocolCacheDirectory));
        services.AddSingleton<CodexRuntimeManager>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        Services = services.BuildServiceProvider(validateScopes: true);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var database = Services.GetRequiredService<SqliteDatabase>();
            await database.InitializeAsync();
            var profileService = Services.GetRequiredService<ProfileService>();
            await profileService.EnsureDefaultAsync();
            await EnsureBuiltInProvidersAsync();
            _window = new MainWindow();
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
}
