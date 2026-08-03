using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class HistoryPage : Page, IContentActionHandler
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        if (action != "history:export")
        {
            return;
        }

        var paths = App.Services.GetRequiredService<AppDataPaths>();
        var directory = Path.Combine(paths.Root, "exports");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"history-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(destination, """
            # Codex Agent Switch 历史报告

            - 任务：审计 Spark profile
            - 采用状态：partially_adopted
            - 重复劳动：一次定向抽查，未发生完整重复
            - Usage：快照已保留
            - 凭据：未包含
            """);
        HistoryActionBar.Severity = InfoBarSeverity.Success;
        HistoryActionBar.Title = "历史报告已导出";
        HistoryActionBar.Message = destination;
        HistoryActionBar.IsOpen = true;
    }
}
