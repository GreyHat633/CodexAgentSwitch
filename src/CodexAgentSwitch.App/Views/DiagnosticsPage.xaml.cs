using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
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
            var state = await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync();
            CodexStatusText.Text = state.Installed ? "已检测" : "不可用";
            CodexDetailText.Text = state.Installed ? $"{state.Version}；App Server：{(state.AppServerRunning ? "运行中" : "未启动")}。" : state.Message;
            SchemaDetailText.Text = state.Schema is null ? "Schema：启动 App Server 时生成并校验" : $"Schema SHA-256：{state.Schema.Sha256}";
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
