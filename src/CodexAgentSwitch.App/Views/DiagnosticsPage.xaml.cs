using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += RunDiagnostics;
    }

    private async void RunDiagnostics(object sender, RoutedEventArgs e)
    {
        DataRootText.Text = $"数据目录：{App.Services.GetRequiredService<AppDataPaths>().Root}";
        try
        {
            var runtime = await App.Services.GetRequiredService<IWorkerScheduler>().GetRuntimeDiagnosticsAsync();
            EconomyDiagnosticsText.Text = $"当前窗口额度 {runtime.Economy.CurrentWindowCredits:0.###} / 阈值 {runtime.Economy.CurrentThreshold:0.###}；退避阶段 {runtime.Economy.BackoffStage}；所有权 {LeaseStatusLabel(runtime.Ownership)}；包 {runtime.PackageId ?? "无"}；Worker {runtime.WorkerIdentity ?? "无"}；最后原因 {runtime.LastReason ?? "无"}；防护命中 {runtime.GuardHits}。";
            var state = await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync();
            CodexStatusText.Text = state.Installed ? "已检测" : "不可用";
            CodexDetailText.Text = state.Installed ? $"{state.Version}；应用服务器：{(state.AppServerRunning ? "运行中" : "未启动")}。" : state.Message;
            SchemaDetailText.Text = state.Schema is null ? "协议结构：启动应用服务器时生成并校验" : $"协议结构 SHA-256：{state.Schema.Sha256}";
            DiagnosticResultBar.Severity = state.Installed ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            DiagnosticResultBar.Title = state.Installed ? "Codex 检测通过" : "Codex 未检测";
            DiagnosticResultBar.Message = state.Message;
        }
        catch (Exception exception)
        {
            DiagnosticResultBar.Severity = InfoBarSeverity.Error;
            DiagnosticResultBar.Title = "诊断失败";
            DiagnosticResultBar.Message = exception.Message;
        }
    }

    private static string LeaseStatusLabel(WorkPackageLeaseStatus? status) => status switch
    {
        null => "无",
        WorkPackageLeaseStatus.DISCOVERY => "发现",
        WorkPackageLeaseStatus.MAIN_OWNED => "主代理持有",
        WorkPackageLeaseStatus.WORKER_OWNED => "Worker 持有",
        WorkPackageLeaseStatus.REVIEW => "审查",
        WorkPackageLeaseStatus.INVALID => "无效",
        WorkPackageLeaseStatus.COMPLETED => "已完成",
        _ => status.Value.ToString(),
    };

    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = DiagnosticBundleExporter.Export(App.Services.GetRequiredService<AppDataPaths>());
            DiagnosticResultBar.Severity = InfoBarSeverity.Success;
            DiagnosticResultBar.Title = "诊断包已导出";
            DiagnosticResultBar.Message = path;
        }
        catch (Exception exception)
        {
            DiagnosticResultBar.Severity = InfoBarSeverity.Error;
            DiagnosticResultBar.Title = "诊断包导出失败";
            DiagnosticResultBar.Message = exception.Message;
        }
    }
}
