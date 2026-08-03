using System.Text;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class HistoryPage : Page, IContentActionHandler
{
    private readonly IControlledTaskRepository repository;
    private IReadOnlyList<ControlledTaskSession> sessions = [];
    private string? selectedTaskId;

    public HistoryPage()
    {
        InitializeComponent();
        repository = App.Services.GetRequiredService<IControlledTaskRepository>();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        sessions = await repository.ListAsync();
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs args) => ApplyFilter();

    private void OnStatusFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (HistoryListView is not null)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        if (HistoryListView is null || SearchTextBox is null || StatusFilterComboBox is null)
        {
            return;
        }

        var query = SearchTextBox.Text.Trim();
        var status = (StatusFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var filtered = sessions
            .Where(task => query.Length == 0
                || task.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || task.Turns.Any(turn => turn.UserInput.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Where(task => status switch
            {
                "completed" => task.Status == ControlledTaskStatus.Completed,
                "failed" => task.Status is ControlledTaskStatus.Failed or ControlledTaskStatus.UnknownRecoverable,
                _ => true,
            })
            .Select(HistoryListItem.From)
            .ToArray();
        HistoryListView.ItemsSource = filtered;
        EmptyHistoryText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = filtered.FirstOrDefault(item => item.Id == selectedTaskId) ?? filtered.FirstOrDefault();
        if (selected is not null)
        {
            HistoryListView.SelectedItem = selected;
        }
        else
        {
            ClearDetails();
        }
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (HistoryListView.SelectedItem is not HistoryListItem item)
        {
            return;
        }

        selectedTaskId = item.Id;
        var task = sessions.Single(value => value.Id == item.Id);
        HistoryTitleText.Text = task.Title;
        HistoryMetadataText.Text = $"方案：{task.ProfileName} · 主代理：{task.MainModelId}（{task.MainReasoningEffort}）\nThread：{task.MainThreadId ?? "未创建"} · 创建：{task.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        HistoryMessagesItemsControl.ItemsSource = task.Turns
            .SelectMany(turn => turn.Messages)
            .Select(HistoryMessageItem.From)
            .ToArray();
        HistoryStatusBar.IsOpen = true;
        HistoryStatusBar.Title = RunningTasksPage.TaskListItemViewModel.From(task).Status;
        HistoryStatusBar.Message = task.ErrorMessage ?? $"共 {task.Turns.Count} 个 Turn，{task.Turns.Sum(turn => turn.Workers.Count)} 个真实 Worker 运行记录。";
        HistoryStatusBar.Severity = task.Status switch
        {
            ControlledTaskStatus.Completed => InfoBarSeverity.Success,
            ControlledTaskStatus.Failed or ControlledTaskStatus.UnknownRecoverable => InfoBarSeverity.Error,
            ControlledTaskStatus.Interrupted => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        ExportHistoryButton.IsEnabled = true;
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        if (action != "history:export" || selectedTaskId is null)
        {
            return;
        }

        var task = sessions.Single(value => value.Id == selectedTaskId);
        var paths = App.Services.GetRequiredService<AppDataPaths>();
        var directory = Path.Combine(paths.Root, "exports");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"task-{task.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md");
        var report = new StringBuilder()
            .AppendLine("# Codex Agent Switch 真实任务报告")
            .AppendLine()
            .AppendLine($"- 任务 ID：{task.Id}")
            .AppendLine($"- 标题：{task.Title}")
            .AppendLine($"- 状态：{task.Status}")
            .AppendLine($"- 配置方案：{task.ProfileName}")
            .AppendLine($"- 主 Thread：{task.MainThreadId ?? "未创建"}")
            .AppendLine($"- Worker 数量：{task.Turns.Sum(turn => turn.Workers.Count)}")
            .AppendLine()
            .AppendLine("## 消息");
        foreach (var message in task.Turns.SelectMany(turn => turn.Messages))
        {
            report.AppendLine().AppendLine($"### {message.Actor}").AppendLine().AppendLine(DiagnosticBundleExporter.Redact(message.Content));
        }

        await File.WriteAllTextAsync(destination, report.ToString());
        HistoryActionBar.Severity = InfoBarSeverity.Success;
        HistoryActionBar.Title = "真实任务报告已导出";
        HistoryActionBar.Message = destination;
        HistoryActionBar.IsOpen = true;
    }

    private void ClearDetails()
    {
        selectedTaskId = null;
        HistoryTitleText.Text = "选择任务查看历史";
        HistoryMetadataText.Text = string.Empty;
        HistoryMessagesItemsControl.ItemsSource = null;
        HistoryStatusBar.IsOpen = false;
        ExportHistoryButton.IsEnabled = false;
    }

    public sealed record HistoryListItem(string Id, string Title, string Metadata)
    {
        public static HistoryListItem From(ControlledTaskSession task) => new(
            task.Id,
            task.Title,
            $"{task.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {RunningTasksPage.TaskListItemViewModel.From(task).Status}");
    }

    public sealed record HistoryMessageItem(string Actor, string Content, string Timestamp)
    {
        public static HistoryMessageItem From(ControlledTaskMessage message)
        {
            var item = RunningTasksPage.TaskMessageItemViewModel.From(message);
            return new HistoryMessageItem(item.Actor, item.Content, item.Timestamp);
        }
    }
}
