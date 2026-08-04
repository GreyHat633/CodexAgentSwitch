using System.Text.Json;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
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

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync();
            if (profile is not null)
            {
                await UpdateProfileAsync(profile);
                await UpdateBudgetAsync(profile);
            }

            UpdateRuntime(await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync());
            await UpdateLatestConversationAsync();
            if (string.Equals(Environment.GetEnvironmentVariable("CAS_UI_TEST_LONG_STATUS"), "1", StringComparison.Ordinal))
            {
                CodexDetectedText.Text = "Codex 已检测：Windows 10 长路径兼容性与系统字体回退验证状态正常";
                AppServerStatusText.Text = "CodexAgentSwitch 受控服务按需启动；窗口缩小时此状态文字应自然换行且不得覆盖状态图标";
            }

            await Task.Delay(500);
            WriteEnvironmentLayoutTrace();
        }
        catch (Exception exception)
        {
            ShowAction("首页加载失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        if (action != "dashboard:launch-native")
        {
            return;
        }

        source.IsEnabled = false;
        try
        {
            var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync()
                ?? throw new InvalidOperationException("尚未设置当前配置方案。");
            var project = (await App.Services.GetRequiredService<ProjectService>().ListAsync())
                .FirstOrDefault(item => !item.IsArchived)
                ?? throw new InvalidOperationException("请先在 CodexAgentSwitch 模式中新建项目并设置工作目录。");
            var result = await App.Services.GetRequiredService<INativeCodexLauncher>()
                .LaunchAsync(profile, project.WorkingDirectory);
            ShowAction(
                "原生 Codex 已启动",
                $"已应用方案“{profile.Name}”，进程 {result.ProcessId}。原生界面中的委派、会话和主线程 Usage 不由 Agent Switch 监控。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowAction("原生 Codex 启动失败", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            source.IsEnabled = true;
        }
    }

    private async Task UpdateProfileAsync(Profile profile)
    {
        ProfileNameText.Text = profile.Name;
        AgentSummaryText.Text = $"{profile.MainAgent.ModelId}（推理强度：{ReasoningLabel(profile.MainAgent.ReasoningEffort)}）";
        MainAgentNameText.Text = AgentDisplayName(profile.MainAgent.ModelId);
        MainReasoningText.Text = $"模型：{profile.MainAgent.ModelId} · 推理强度：{ReasoningLabel(profile.MainAgent.ReasoningEffort)}";
        WorkerPolicyText.Text = profile.WorkerPolicy.Enabled
            ? $"批准：{ApprovalModeLabel(profile.ApprovalMode)} · 最多 {profile.WorkerPolicy.MaxWorkers} 个 Worker · {RoutingLabel(profile.WorkerPolicy.RoutingMode)} · 失败时{FallbackLabel(profile.WorkerPolicy.FallbackAction)}"
            : $"批准：{ApprovalModeLabel(profile.ApprovalMode)} · Worker 未启用；所有任务由主代理处理。";

        if (!profile.WorkerPolicy.Enabled || profile.WorkerPolicy.Source == WorkerSource.Disabled)
        {
            WorkerSourceBadgeText.Text = "未启用";
            WorkerNameText.Text = "未启用";
            WorkerDescriptionText.Text = "当前方案不会调用任何 Worker。";
            CredentialStatusText.Text = "当前方案未使用外部 Provider";
            return;
        }

        if (profile.WorkerPolicy.Source == WorkerSource.NativeCodex)
        {
            WorkerSourceBadgeText.Text = "原生 Codex";
            WorkerNameText.Text = NativeWorkerDisplay(profile.WorkerPolicy.PreferredProviderId);
            WorkerDescriptionText.Text = $"最大数量：{profile.WorkerPolicy.MaxWorkers} · {RoutingLabel(profile.WorkerPolicy.RoutingMode)}";
            CredentialStatusText.Text = "原生 Codex Worker 不需要外部 API Key";
            return;
        }

        var providerId = profile.WorkerPolicy.PreferredProviderId;
        var provider = providerId is null
            ? null
            : await App.Services.GetRequiredService<IProviderRepository>().GetAsync(providerId);
        var credentialReady = provider?.CredentialReference is not null
            && await App.Services.GetRequiredService<ICredentialStore>().ExistsAsync(provider.CredentialReference);
        if (provider is null || !provider.IsEnabled || provider.BaseUri is null || string.IsNullOrWhiteSpace(provider.ModelId) || !credentialReady)
        {
            WorkerSourceBadgeText.Text = "配置异常";
            WorkerNameText.Text = "Provider 未就绪";
            WorkerDescriptionText.Text = provider is null
                ? $"方案引用的 Provider {providerId ?? "(空)"} 不存在。"
                : $"{provider.Name} 缺少启用状态、Base URL、模型或凭据。";
            CredentialStatusText.Text = "外部 Provider 配置异常";
            return;
        }

        WorkerSourceBadgeText.Text = "外部 Provider";
        WorkerNameText.Text = ModelDisplay(provider.ModelId);
        WorkerDescriptionText.Text = $"Provider：{provider.Name} · Model：{provider.ModelId} · 最大数量：{profile.WorkerPolicy.MaxWorkers}";
        CredentialStatusText.Text = $"{provider.Name} 凭据可用";
    }

    private async Task UpdateBudgetAsync(Profile profile)
    {
        var repository = App.Services.GetRequiredService<IUsageLedgerRepository>();
        var groups = await repository.ListTaskGroupsAsync();
        var today = DateTimeOffset.Now.Date;
        decimal actual = 0;
        foreach (var group in groups)
        {
            var usage = await repository.ListUsageAsync(group.Id);
            actual += usage
                .Where(item => item.CapturedAt.ToLocalTime().Date == today && item.Cost.Value is not null)
                .Sum(item => item.Cost.Value ?? 0);
        }

        var daily = profile.Budget.Daily;
        BudgetSummaryText.Text = daily is null
            ? $"{actual:0.####} {profile.Budget.Currency}"
            : $"{actual:0.####} / {daily:0.##} {profile.Budget.Currency}";
        var ratio = daily is > 0 ? Math.Min(100, (double)(actual / daily.Value * 100)) : 0;
        BudgetProgressBar.Value = ratio;
        BudgetStateText.Text = daily is null
            ? "当前方案没有设置每日费用上限。"
            : $"今日已记录外部 Provider 实际或估算费用，占每日预算 {ratio:0.#}%。";
    }

    private async Task UpdateLatestConversationAsync()
    {
        var conversation = (await App.Services.GetRequiredService<IControlledTaskRepository>().ListAsync()).FirstOrDefault();
        if (conversation is null)
        {
            LatestTaskTitleText.Text = "最近对话";
            LatestTaskSummaryText.Text = "尚无对话。请进入 CodexAgentSwitch 模式新建项目和对话。";
            LatestTaskInfoBar.IsOpen = false;
            return;
        }

        LatestTaskTitleText.Text = conversation.Title;
        var lastMessage = conversation.Turns.SelectMany(turn => turn.Messages)
            .LastOrDefault(message => message.Actor == TaskMessageActor.MainAgent);
        LatestTaskSummaryText.Text = lastMessage?.Content ?? "该对话尚无主代理回复。";
        LatestTaskInfoBar.IsOpen = true;
        LatestTaskInfoBar.Title = StatusLabel(conversation.Status);
        var worker = conversation.Turns.SelectMany(turn => turn.Workers).LastOrDefault();
        LatestTaskInfoBar.Message = worker is null
            ? "最近一轮未调用 Worker。"
            : $"Worker：{worker.ProviderName ?? worker.ProviderId ?? worker.AdapterId} · {worker.ResponseModelId ?? worker.ModelId} · {worker.Status}";
        LatestTaskInfoBar.Severity = conversation.Status == ControlledTaskStatus.Completed
            ? InfoBarSeverity.Success
            : conversation.Status == ControlledTaskStatus.Failed ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
    }

    private void UpdateRuntime(CodexRuntimeState state)
    {
        CodexDetectedText.Text = state.Installed ? $"Codex {state.Version}" : "Codex 未检测";
        AppServerStatusText.Text = state.AppServerRunning
            ? "CodexAgentSwitch 受控服务已连接"
            : "CodexAgentSwitch 受控服务将在发送消息时按需启动";
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
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            rasterizationScale = XamlRoot?.RasterizationScale ?? 1d,
            pageWidth = ActualWidth,
            pageHeight = ActualHeight,
            rows = new[]
            {
                new { name = "codex", icon = Bounds(CodexStatusIcon, this), text = Bounds(CodexDetectedText, this) },
                new { name = "appServer", icon = Bounds(AppServerStatusIcon, this), text = Bounds(AppServerStatusText, this) },
                new { name = "credential", icon = Bounds(CredentialStatusIcon, this), text = Bounds(CredentialStatusText, this) },
            },
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ShowAction(string title, string message, InfoBarSeverity severity)
    {
        DashboardActionBar.Title = title;
        DashboardActionBar.Message = message;
        DashboardActionBar.Severity = severity;
        DashboardActionBar.IsOpen = true;
    }

    private static string AgentDisplayName(string modelId) => modelId switch
    {
        "gpt-5.6-terra" => "Terra",
        "gpt-5.6-luna" => "Luna",
        "gpt-5.6-sol" => "Sol",
        _ => modelId,
    };

    private static string NativeWorkerDisplay(string? id) => id switch
    {
        "native-sol" => "Sol",
        "native-terra" => "Terra",
        "native-luna" => "Luna",
        _ => "配置异常",
    };

    private static string ModelDisplay(string modelId) =>
        DeepSeekV4Catalog.TryGet(modelId, out var model) ? model.DisplayName : modelId;

    private static string ReasoningLabel(string effort) => effort switch
    {
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        _ => effort,
    };

    private static string RoutingLabel(RoutingMode mode) => mode switch
    {
        RoutingMode.Economic => "经济优先",
        RoutingMode.Balanced => "平衡模式",
        RoutingMode.Performance => "性能优先",
        RoutingMode.Manual => "手动模式",
        RoutingMode.Single => "单代理模式",
        _ => mode.ToString(),
    };

    private static string FallbackLabel(FallbackAction action) => action switch
    {
        FallbackAction.NativeLuna => "明确回退到原生 Luna",
        FallbackAction.SingleAgent => "由主代理接管",
        FallbackAction.AskUser => "询问用户",
        FallbackAction.StopDelegation => "停止委派",
        _ => action.ToString(),
    };

    private static string ApprovalModeLabel(ExecutionApprovalMode mode) => mode switch
    {
        ExecutionApprovalMode.Safe => "安全模式",
        ExecutionApprovalMode.FullAuto => "完全自动",
        _ => "自动模式",
    };

    private static string StatusLabel(ControlledTaskStatus status) => status switch
    {
        ControlledTaskStatus.Queued => "排队中",
        ControlledTaskStatus.WorkerRunning => "Worker 运行中",
        ControlledTaskStatus.MainAgentRunning => "主代理生成中",
        ControlledTaskStatus.WaitingForApproval => "等待批准",
        ControlledTaskStatus.Completed => "已完成",
        ControlledTaskStatus.Failed => "失败",
        ControlledTaskStatus.Interrupted => "已停止",
        ControlledTaskStatus.UnknownRecoverable => "需要恢复",
        _ => status.ToString(),
    };
}
