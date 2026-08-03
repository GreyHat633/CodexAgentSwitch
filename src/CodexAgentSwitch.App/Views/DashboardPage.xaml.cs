using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

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
        if (string.Equals(Environment.GetEnvironmentVariable("CAS_UI_TEST_LONG_STATUS"), "1", StringComparison.Ordinal))
        {
            CodexDetectedText.Text = "Codex 已检测：Windows 10 长路径兼容性与系统字体回退验证状态正常";
            AppServerStatusText.Text = "App Server 正在使用本地受控协议；窗口缩小时此状态文字应自然换行且不得覆盖状态图标";
        }

        await Task.Delay(700);
        WriteEnvironmentLayoutTrace();
    }

    private void WriteEnvironmentLayoutTrace()
    {
        var path = Environment.GetEnvironmentVariable("CAS_LAYOUT_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        static object Bounds(FrameworkElement element, UIElement root)
        {
            var point = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point());
            return new { x = point.X, y = point.Y, width = element.ActualWidth, height = element.ActualHeight };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = new[]
        {
            new { name = "codex", icon = Bounds(CodexStatusIcon, this), text = Bounds(CodexDetectedText, this) },
            new { name = "appServer", icon = Bounds(AppServerStatusIcon, this), text = Bounds(AppServerStatusText, this) },
            new { name = "credential", icon = Bounds(CredentialStatusIcon, this), text = Bounds(CredentialStatusText, this) },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            rasterizationScale = XamlRoot?.RasterizationScale ?? 1d,
            pageWidth = ActualWidth,
            pageHeight = ActualHeight,
            rows,
        }, new JsonSerializerOptions { WriteIndented = true }));
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
