using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class RunningTasksPage : Page, IContentActionHandler
{
    private readonly ControlledTaskService service;
    private string? selectedTaskId;
    private bool subscribed;

    public RunningTasksPage()
    {
        InitializeComponent();
        service = App.Services.GetRequiredService<ControlledTaskService>();
        WorkingDirectoryTextBox.Text = Environment.GetEnvironmentVariable("CAS_DEFAULT_WORKING_DIRECTORY")
            ?? "E:\\AISPace\\主模型项目区";
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!subscribed)
        {
            service.TaskChanged += OnTaskChangedAsync;
            subscribed = true;
        }

        try
        {
            await service.RecoverAsync();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError("任务恢复失败", exception.Message);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (subscribed)
        {
            service.TaskChanged -= OnTaskChangedAsync;
            subscribed = false;
        }
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        try
        {
            switch (action)
            {
                case "task:start":
                    await StartTaskAsync();
                    break;
                case "task:refresh":
                    await RefreshAsync();
                    break;
                case "task:continue":
                    await ContinueTaskAsync();
                    break;
                case "task:cancel":
                    await RequireSelectionAsync();
                    await service.CancelAsync(selectedTaskId!);
                    ShowInfo("正在取消", "已向真实 Turn 发送中断请求。");
                    break;
                case "task:approve":
                    await RequireSelectionAsync();
                    await service.RespondToApprovalAsync(selectedTaskId!, true);
                    ShowInfo("已批准", "审批结果已发送到当前主代理 Turn。");
                    break;
                case "task:decline":
                    await RequireSelectionAsync();
                    await service.RespondToApprovalAsync(selectedTaskId!, false);
                    ShowInfo("已拒绝", "拒绝结果已发送到当前主代理 Turn。");
                    break;
            }
        }
        catch (Exception exception)
        {
            ShowError("操作失败", exception.Message);
        }
    }

    private async Task StartTaskAsync()
    {
        var input = TaskInputTextBox.Text.Trim();
        if (input.Length == 0)
        {
            throw new InvalidOperationException("请输入任务内容。");
        }

        StartTaskButton.IsEnabled = false;
        try
        {
            var task = await service.StartAsync(
                input,
                WorkingDirectoryTextBox.Text,
                UseWorkerCheckBox.IsChecked);
            selectedTaskId = task.Id;
            TaskInputTextBox.Text = string.Empty;
            ShowInfo("任务已创建", "已建立真实任务记录，正在启动受控 Thread/Turn。", InfoBarSeverity.Success);
            await RefreshAsync(task.Id);
        }
        finally
        {
            StartTaskButton.IsEnabled = true;
        }
    }

    private async Task ContinueTaskAsync()
    {
        await RequireSelectionAsync();
        var input = ContinueInputTextBox.Text.Trim();
        if (input.Length == 0)
        {
            throw new InvalidOperationException("请输入下一轮内容。");
        }

        ContinueTaskButton.IsEnabled = false;
        try
        {
            await service.ContinueAsync(selectedTaskId!, input, UseWorkerCheckBox.IsChecked);
            ContinueInputTextBox.Text = string.Empty;
            ShowInfo("下一轮已提交", "内容已提交到同一个受控主 Thread。", InfoBarSeverity.Success);
            await RefreshAsync(selectedTaskId);
        }
        finally
        {
            ContinueTaskButton.IsEnabled = true;
        }
    }

    private Task RequireSelectionAsync() => selectedTaskId is null
        ? Task.FromException(new InvalidOperationException("请先选择一个真实任务。"))
        : Task.CompletedTask;

    private async Task OnTaskChangedAsync(ControlledTaskSession task)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshAsync(selectedTaskId ?? task.Id));
        await Task.CompletedTask;
    }

    private async Task RefreshAsync(string? selectTaskId = null)
    {
        var sessions = await service.ListAsync();
        var items = sessions.Select(TaskListItemViewModel.From).ToArray();
        TaskListView.ItemsSource = items;
        EmptyTasksText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var targetId = selectTaskId ?? selectedTaskId;
        var selected = targetId is null ? items.FirstOrDefault() : items.FirstOrDefault(item => item.Id == targetId);
        if (selected is not null)
        {
            TaskListView.SelectedItem = selected;
            selectedTaskId = selected.Id;
            await ShowDetailsAsync(selected.Id);
        }
        else
        {
            ClearDetails();
        }
    }

    private async void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (TaskListView.SelectedItem is not TaskListItemViewModel item)
        {
            return;
        }

        selectedTaskId = item.Id;
        await ShowDetailsAsync(item.Id);
    }

    private async Task ShowDetailsAsync(string taskId)
    {
        var task = await service.GetAsync(taskId);
        if (task is null)
        {
            ClearDetails();
            return;
        }

        TaskTitleText.Text = task.Title;
        TaskMetadataText.Text = $"方案：{task.ProfileName} · 主代理：{task.MainModelId}（{task.MainReasoningEffort}）\nThread：{task.MainThreadId ?? "尚未创建"} · 更新：{task.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        TaskMessagesItemsControl.ItemsSource = task.Turns
            .SelectMany(turn => turn.Messages)
            .Select(TaskMessageItemViewModel.From)
            .ToArray();
        TaskStatusBar.IsOpen = true;
        TaskStatusBar.Title = StatusText(task.Status);
        TaskStatusBar.Message = task.ErrorMessage ?? WorkerSummary(task);
        TaskStatusBar.Severity = task.Status switch
        {
            ControlledTaskStatus.Completed => InfoBarSeverity.Success,
            ControlledTaskStatus.Failed or ControlledTaskStatus.UnknownRecoverable => InfoBarSeverity.Error,
            ControlledTaskStatus.Interrupted or ControlledTaskStatus.WaitingForApproval => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        var running = IsRunning(task.Status);
        ContinueTaskButton.IsEnabled = !running;
        CancelTaskButton.IsEnabled = running;
        CancelTaskButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var waiting = task.Status == ControlledTaskStatus.WaitingForApproval;
        ApproveTaskButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        DeclineTaskButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDetails()
    {
        selectedTaskId = null;
        TaskTitleText.Text = "选择任务查看详情";
        TaskMetadataText.Text = string.Empty;
        TaskMessagesItemsControl.ItemsSource = null;
        TaskStatusBar.IsOpen = false;
        ContinueTaskButton.IsEnabled = false;
        CancelTaskButton.Visibility = Visibility.Collapsed;
        ApproveTaskButton.Visibility = Visibility.Collapsed;
        DeclineTaskButton.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Error);

    private void ShowInfo(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        TaskActionBar.Title = title;
        TaskActionBar.Message = message;
        TaskActionBar.Severity = severity;
        TaskActionBar.IsOpen = true;
    }

    private static bool IsRunning(ControlledTaskStatus status) => status is
        ControlledTaskStatus.Queued or ControlledTaskStatus.WorkerRunning or ControlledTaskStatus.MainAgentRunning or ControlledTaskStatus.WaitingForApproval;

    private static string StatusText(ControlledTaskStatus status) => status switch
    {
        ControlledTaskStatus.Queued => "排队中",
        ControlledTaskStatus.WorkerRunning => "工作代理运行中",
        ControlledTaskStatus.MainAgentRunning => "主代理运行中",
        ControlledTaskStatus.WaitingForApproval => "等待批准",
        ControlledTaskStatus.Completed => "已完成",
        ControlledTaskStatus.Failed => "失败",
        ControlledTaskStatus.Interrupted => "已取消",
        ControlledTaskStatus.UnknownRecoverable => "需要恢复",
        _ => status.ToString(),
    };

    private static string WorkerSummary(ControlledTaskSession task)
    {
        var workers = task.Turns.SelectMany(turn => turn.Workers).ToArray();
        return workers.Length == 0
            ? "本任务没有调用工作代理。"
            : $"真实 Worker：{workers.Length} 个；最近状态：{workers[^1].Status}。";
    }

    public sealed record TaskListItemViewModel(string Id, string Title, string Status, string Metadata)
    {
        public static TaskListItemViewModel From(ControlledTaskSession task) => new(
            task.Id,
            task.Title,
            StatusText(task.Status),
            $"{task.ProfileName} · {task.UpdatedAt.ToLocalTime():MM-dd HH:mm}");
    }

    public sealed record TaskMessageItemViewModel(string Actor, string Content, string Timestamp)
    {
        public static TaskMessageItemViewModel From(ControlledTaskMessage message) => new(
            message.Actor switch
            {
                TaskMessageActor.User => "用户",
                TaskMessageActor.Worker => "工作代理",
                TaskMessageActor.MainAgent => "主代理",
                _ => "系统",
            },
            message.Content,
            message.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
