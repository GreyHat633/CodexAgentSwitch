using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Common;
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
        services.AddSingleton<ProfileValidator>();
        services.AddSingleton<ProfileService>();
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
            var dataRoot = Environment.GetEnvironmentVariable("CAS_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                var diagnosticDirectory = Path.Combine(dataRoot, "diagnostics");
                Directory.CreateDirectory(diagnosticDirectory);
                File.WriteAllText(Path.Combine(diagnosticDirectory, "startup-crash.txt"), exception.ToString());
            }

            throw;
        }
    }

    private async Task EnsureBuiltInProvidersAsync()
    {
        var repository = Services.GetRequiredService<IProviderRepository>();
        var clock = Services.GetRequiredService<IClock>();
        if (await repository.GetAsync("native-codex") is null)
        {
            await repository.UpsertAsync(ProviderConfiguration.Native(clock.UtcNow));
        }

        if (await repository.GetAsync("deepseek-default") is null)
        {
            await repository.UpsertAsync(ProviderConfiguration.DeepSeekPreset(clock.UtcNow));
        }
    }
}
