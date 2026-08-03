using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync();
        if (profile is not null)
        {
            ProfileNameText.Text = profile.Name;
            AgentSummaryText.Text = $"{profile.MainAgent.ModelId} {profile.MainAgent.ReasoningEffort} + {profile.WorkerPolicy.Source}";
            WorkerPolicyText.Text = $"最多 {profile.WorkerPolicy.MaxWorkers} 个 Worker · 超预算时执行 {profile.WorkerPolicy.FallbackAction}";
        }

        UpdateRuntime(await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync());
    }

    private async void StartCodex(object sender, RoutedEventArgs e)
    {
        StartCodexButton.IsEnabled = false;
        try
        {
            UpdateRuntime(await App.Services.GetRequiredService<CodexRuntimeManager>().StartAsync());
        }
        catch (Exception exception)
        {
            AppServerStatusText.Text = $"启动失败：{exception.Message}";
        }
        finally
        {
            StartCodexButton.IsEnabled = true;
        }
    }

    private async void StopCodex(object sender, RoutedEventArgs e)
    {
        var runtime = App.Services.GetRequiredService<CodexRuntimeManager>();
        await runtime.StopAsync();
        UpdateRuntime(runtime.State);
    }

    private void UpdateRuntime(CodexRuntimeState state)
    {
        CodexDetectedText.Text = state.Installed ? $"Codex {state.Version}" : "Codex 未检测";
        AppServerStatusText.Text = state.AppServerRunning ? "App Server 已连接" : state.Message;
    }
}
