using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class DashboardPage : Page, IContentActionHandler
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

    public async Task HandleContentActionAsync(string action, Button source)
    {
        if (action != "dashboard:disable-worker")
        {
            return;
        }

        var repository = App.Services.GetRequiredService<IProfileRepository>();
        var current = await repository.GetDefaultAsync();
        if (current is null)
        {
            DashboardActionBar.Severity = InfoBarSeverity.Warning;
            DashboardActionBar.Title = "没有可更新的默认方案";
            DashboardActionBar.Message = "请先创建或导入配置方案。";
            DashboardActionBar.IsOpen = true;
            return;
        }

        await repository.UpsertAsync(current with
        {
            WorkerPolicy = current.WorkerPolicy with
            {
                Enabled = false,
                Source = WorkerSource.Disabled,
                PreferredProviderId = null,
                MaxWorkers = 0,
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        WorkerPolicyText.Text = "Worker 已停用；所有任务由主代理处理。";
        DisableWorkerButton.IsEnabled = false;
        DisableWorkerButton.Content = "Worker 已停用";
        DashboardActionBar.Severity = InfoBarSeverity.Success;
        DashboardActionBar.Title = "Worker 策略已更新";
        DashboardActionBar.Message = "重新启动应用后仍会保留此设置。";
        DashboardActionBar.IsOpen = true;
    }
}
