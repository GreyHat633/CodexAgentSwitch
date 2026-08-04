using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CodexAgentSwitch.App.Views;

public sealed partial class RunningTasksPage : Page, IContentActionHandler
{
    private readonly ProjectService projects = App.Services.GetRequiredService<ProjectService>();
    private readonly ControlledTaskService conversations = App.Services.GetRequiredService<ControlledTaskService>();
    private string? selectedProjectId;
    private string? selectedConversationId;
    private bool subscribed;
    private ScrollViewer? messageScroller;
    private bool userReadingHistory;
    private int renderedMessageCount;
    private bool? sidebarUserPreference;

    public RunningTasksPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnPageSizeChanged;
        MessageListView.Loaded += OnMessageListLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!subscribed)
        {
            conversations.TaskChanged += OnConversationChangedAsync;
            subscribed = true;
        }

        try
        {
            await conversations.RecoverAsync();
            await RefreshProjectsAsync();
        }
        catch (Exception exception)
        {
            ShowError("恢复失败", exception.Message);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (subscribed)
        {
            conversations.TaskChanged -= OnConversationChangedAsync;
            subscribed = false;
        }

        if (messageScroller is not null)
        {
            messageScroller.ViewChanged -= OnMessageViewChanged;
            messageScroller = null;
        }
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        try
        {
            switch (action)
            {
                case "cas:project-new":
                    await NewProjectAsync();
                    break;
                case "cas:project-rename":
                    await RenameProjectAsync();
                    break;
                case "cas:project-directory":
                    await ChangeProjectDirectoryAsync();
                    break;
                case "cas:project-archive":
                    await ToggleProjectArchiveAsync();
                    break;
                case "cas:project-delete":
                    await DeleteProjectAsync();
                    break;
                case "cas:conversation-new":
                    await NewConversationAsync();
                    break;
                case "cas:conversation-rename":
                    await RenameConversationAsync();
                    break;
                case "cas:conversation-delete":
                    await DeleteConversationAsync();
                    break;
                case "cas:toggle-sidebar":
                    sidebarUserPreference = SidebarGrid.Visibility != Visibility.Visible;
                    SetSidebarVisible(sidebarUserPreference.Value);
                    break;
                case "cas:send":
                    await SendAsync();
                    break;
                case "cas:stop":
                    await RequireConversationAsync();
                    await conversations.CancelAsync(selectedConversationId!);
                    ShowInfo("正在停止", "已向当前主 Turn 发送停止请求。");
                    break;
                case "cas:retry":
                    await RequireConversationAsync();
                    await conversations.RetryLastTurnAsync(selectedConversationId!);
                    break;
                case "cas:force-worker":
                    await RequireConversationAsync();
                    await conversations.ForceTestCurrentWorkerAsync(selectedConversationId!);
                    ShowInfo("Worker 测试已提交", "本次调用绕过经济路由，仅执行当前方案选中的 Worker，不使用静默回退。", InfoBarSeverity.Success);
                    break;
                case "cas:approval":
                    await ShowApprovalAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            ShowError("操作失败", exception.Message);
        }
    }

    private async Task NewProjectAsync()
    {
        var name = new TextBox { Header = "项目名称", PlaceholderText = "例如：Codex Agent Switch" };
        var directory = new TextBox
        {
            Header = "工作目录",
            Text = Environment.CurrentDirectory,
            PlaceholderText = "E:\\项目目录",
        };
        var dialog = FormDialog("新建项目", name, directory);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var created = await projects.CreateAsync(name.Text, directory.Text);
        selectedProjectId = created.Id;
        await RefreshProjectsAsync(created.Id);
        ShowInfo("项目已创建", $"“{created.Name}”已保存，工作目录为 {created.WorkingDirectory}。", InfoBarSeverity.Success);
    }

    private async Task RenameProjectAsync()
    {
        var project = await RequireProjectAsync();
        var name = new TextBox { Header = "项目名称", Text = project.Name };
        if (await FormDialog("重命名项目", name).ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await projects.RenameAsync(project.Id, name.Text);
        await RefreshProjectsAsync(project.Id);
    }

    private async Task ChangeProjectDirectoryAsync()
    {
        var project = await RequireProjectAsync();
        var directory = new TextBox { Header = "工作目录", Text = project.WorkingDirectory };
        if (await FormDialog("设置项目工作目录", directory).ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await projects.ChangeWorkingDirectoryAsync(project.Id, directory.Text);
        await RefreshProjectsAsync(project.Id);
    }

    private async Task ToggleProjectArchiveAsync()
    {
        var project = await RequireProjectAsync();
        if (project.IsArchived)
        {
            await projects.UnarchiveAsync(project.Id);
        }
        else
        {
            await projects.ArchiveAsync(project.Id);
        }

        await RefreshProjectsAsync(project.Id);
    }

    private async Task DeleteProjectAsync()
    {
        var project = await RequireProjectAsync();
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除项目",
            Content = $"将删除项目“{project.Name}”及其本地对话、Usage 和历史记录。此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await conversations.DeleteProjectConversationsAsync(project.Id);
        await projects.DeleteAsync(project.Id);
        selectedProjectId = null;
        selectedConversationId = null;
        await RefreshProjectsAsync();
    }

    private async Task NewConversationAsync()
    {
        var project = await RequireProjectAsync();
        if (project.IsArchived)
        {
            throw new InvalidOperationException("归档项目不能新建对话，请先取消归档。");
        }

        var conversation = await conversations.CreateConversationAsync(project.Id, project.WorkingDirectory);
        selectedConversationId = conversation.Id;
        await RefreshConversationsAsync(project.Id, conversation.Id);
        ComposerTextBox.Focus(FocusState.Programmatic);
    }

    private async Task RenameConversationAsync()
    {
        var conversation = await RequireConversationAsync();
        var name = new TextBox { Header = "对话名称", Text = conversation.Title };
        if (await FormDialog("重命名对话", name).ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await conversations.RenameConversationAsync(conversation.Id, name.Text);
        await RefreshConversationsAsync(conversation.ProjectId!, conversation.Id);
    }

    private async Task DeleteConversationAsync()
    {
        var conversation = await RequireConversationAsync();
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除对话",
            Content = $"确认删除“{conversation.Title}”及其 Usage 和历史记录？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await conversations.DeleteConversationAsync(conversation.Id);
        selectedConversationId = null;
        await RefreshConversationsAsync(conversation.ProjectId!);
    }

    private async Task SendAsync()
    {
        var project = await RequireProjectAsync();
        var input = ComposerTextBox.Text.Trim();
        if (input.Length == 0)
        {
            throw new InvalidOperationException("请输入对话内容。");
        }

        if (selectedConversationId is null)
        {
            var created = await conversations.CreateConversationAsync(project.Id, project.WorkingDirectory);
            selectedConversationId = created.Id;
        }

        SendButton.IsEnabled = false;
        try
        {
            await conversations.ContinueAsync(selectedConversationId, input, UseWorkerCheckBox.IsChecked);
            ComposerTextBox.Text = string.Empty;
            userReadingHistory = false;
            await RefreshConversationsAsync(project.Id, selectedConversationId);
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private async Task ShowApprovalAsync()
    {
        await RequireConversationAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "主代理请求批准",
            Content = "请审查当前状态信息后选择批准或拒绝。",
            PrimaryButtonText = "批准",
            SecondaryButtonText = "拒绝",
            CloseButtonText = "稍后处理",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await conversations.RespondToApprovalAsync(selectedConversationId!, true);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await conversations.RespondToApprovalAsync(selectedConversationId!, false);
        }
    }

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProjectListView.SelectedItem is not ProjectListItem item)
        {
            return;
        }

        selectedProjectId = item.Id;
        selectedConversationId = null;
        SetProjectActionsEnabled(true);
        ArchiveProjectButton.Content = item.IsArchived ? "取消归档" : "归档";
        NewConversationButton.IsEnabled = !item.IsArchived;
        await RefreshConversationsAsync(item.Id);
    }

    private async void OnConversationSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ConversationListView.SelectedItem is not ConversationListItem item)
        {
            return;
        }

        selectedConversationId = item.Id;
        SetConversationActionsEnabled(true);
        userReadingHistory = false;
        await ShowConversationAsync(item.Id);
    }

    private async Task RefreshProjectsAsync(string? selectId = null)
    {
        var list = (await projects.ListAsync()).Select(ProjectListItem.From).ToArray();
        ProjectListView.ItemsSource = list;
        var target = selectId ?? selectedProjectId;
        var selected = target is null ? list.FirstOrDefault(project => !project.IsArchived) ?? list.FirstOrDefault() : list.FirstOrDefault(project => project.Id == target);
        if (selected is null)
        {
            SetProjectActionsEnabled(false);
            NewConversationButton.IsEnabled = false;
            ClearConversation();
            ConversationListView.ItemsSource = null;
            EmptyConversationsText.Visibility = Visibility.Visible;
            return;
        }

        selectedProjectId = selected.Id;
        SetProjectActionsEnabled(true);
        ProjectListView.SelectedItem = selected;
        ArchiveProjectButton.Content = selected.IsArchived ? "取消归档" : "归档";
        NewConversationButton.IsEnabled = !selected.IsArchived;
        await RefreshConversationsAsync(selected.Id, selectedConversationId);
    }

    private async Task RefreshConversationsAsync(string projectId, string? selectId = null)
    {
        var list = (await conversations.ListProjectConversationsAsync(projectId)).Select(ConversationListItem.From).ToArray();
        ConversationListView.ItemsSource = list;
        EmptyConversationsText.Visibility = list.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var target = selectId ?? selectedConversationId;
        var selected = target is null ? list.FirstOrDefault() : list.FirstOrDefault(conversation => conversation.Id == target);
        if (selected is null)
        {
            ClearConversation();
            return;
        }

        selectedConversationId = selected.Id;
        SetConversationActionsEnabled(true);
        ConversationListView.SelectedItem = selected;
        await ShowConversationAsync(selected.Id);
    }

    private async Task ShowConversationAsync(string id)
    {
        var conversation = await conversations.GetAsync(id);
        if (conversation is null)
        {
            ClearConversation();
            return;
        }

        ConversationTitleText.Text = conversation.Title;
        var lastTurn = conversation.Turns.LastOrDefault();
        var lastWorker = lastTurn?.Workers.LastOrDefault();
        var workerText = lastWorker is null
            ? lastTurn?.Delegation?.Reason ?? "尚未提交内容"
            : $"Worker：{lastWorker.ProviderName ?? lastWorker.ProviderId ?? lastWorker.AdapterId} · {lastWorker.ResponseModelId ?? lastWorker.ModelId}";
        var approvalMode = lastTurn?.ProfileSnapshot?.ApprovalMode ?? ExecutionApprovalMode.Automatic;
        ConversationMetadataText.Text = $"方案：{conversation.ProfileName} · 主代理：{conversation.MainModelId}（{conversation.MainReasoningEffort}）· 批准：{ApprovalModeText(approvalMode)}\nThread：{conversation.MainThreadId ?? "尚未创建"} · {workerText}";
        var messages = conversation.Turns.SelectMany(turn => turn.Messages).ToArray();
        var shouldScroll = !userReadingHistory && messages.Length >= renderedMessageCount;
        renderedMessageCount = messages.Length;
        MessageListView.ItemsSource = messages;

        var running = IsRunning(conversation.Status);
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = !running && conversation.Turns.Count > 0 && conversation.Status is not ControlledTaskStatus.Completed
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApprovalButton.Visibility = conversation.Status == ControlledTaskStatus.WaitingForApproval ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !running;
        ForceWorkerButton.IsEnabled = !running;
        ComposerTextBox.IsEnabled = !running;
        UseWorkerCheckBox.IsEnabled = !running;
        ConversationStatusBar.IsOpen = conversation.Turns.Count > 0;
        ConversationStatusBar.Title = StatusText(conversation.Status);
        ConversationStatusBar.Message = conversation.ErrorMessage ?? lastTurn?.Delegation?.Reason ?? string.Empty;
        ConversationStatusBar.Severity = conversation.Status switch
        {
            ControlledTaskStatus.Completed => InfoBarSeverity.Success,
            ControlledTaskStatus.Failed or ControlledTaskStatus.UnknownRecoverable => InfoBarSeverity.Error,
            ControlledTaskStatus.Interrupted or ControlledTaskStatus.WaitingForApproval => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };

        if (shouldScroll)
        {
            DispatcherQueue.TryEnqueue(() => messageScroller?.ChangeView(null, messageScroller.ScrollableHeight, null));
        }
    }

    private void ClearConversation()
    {
        selectedConversationId = null;
        ConversationTitleText.Text = "选择或新建对话";
        ConversationMetadataText.Text = "一个对话始终绑定同一个主 Thread。";
        MessageListView.ItemsSource = null;
        renderedMessageCount = 0;
        ConversationStatusBar.IsOpen = false;
        StopButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ApprovalButton.Visibility = Visibility.Collapsed;
        SetConversationActionsEnabled(false);
        ComposerTextBox.IsEnabled = false;
        UseWorkerCheckBox.IsEnabled = false;
        ForceWorkerButton.IsEnabled = false;
        SendButton.IsEnabled = false;
    }

    private void SetProjectActionsEnabled(bool enabled)
    {
        RenameProjectButton.IsEnabled = enabled;
        ProjectDirectoryButton.IsEnabled = enabled;
        ArchiveProjectButton.IsEnabled = enabled;
        DeleteProjectButton.IsEnabled = enabled;
    }

    private void SetConversationActionsEnabled(bool enabled)
    {
        RenameConversationButton.IsEnabled = enabled;
        DeleteConversationButton.IsEnabled = enabled;
    }

    private Task OnConversationChangedAsync(ControlledTaskSession conversation)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (conversation.ProjectId == selectedProjectId)
            {
                await RefreshConversationsAsync(selectedProjectId!, selectedConversationId ?? conversation.Id);
            }
        });
        return Task.CompletedTask;
    }

    private void OnComposerKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (args.Key == VirtualKey.Enter && control.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            args.Handled = true;
            _ = HandleContentActionAsync("cas:send", SendButton);
        }
    }

    private void OnMessageListLoaded(object sender, RoutedEventArgs args)
    {
        messageScroller = FindDescendant<ScrollViewer>(MessageListView);
        if (messageScroller is not null)
        {
            messageScroller.ViewChanged += OnMessageViewChanged;
        }
    }

    private void OnMessageViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (messageScroller is null)
        {
            return;
        }

        userReadingHistory = messageScroller.ScrollableHeight - messageScroller.VerticalOffset > 80;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sidebarUserPreference is null)
        {
            SetSidebarVisible(args.NewSize.Width >= 1100);
        }
    }

    private void SetSidebarVisible(bool visible)
    {
        SidebarGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = visible ? new GridLength(280) : new GridLength(0);
        ToggleSidebarButton.Content = visible ? "隐藏侧栏" : "显示侧栏";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ToggleSidebarButton,
            visible ? "隐藏项目与对话侧栏" : "显示项目与对话侧栏");
    }

    private ContentDialog FormDialog(string title, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
    }

    private async Task<AgentProject> RequireProjectAsync()
    {
        if (selectedProjectId is null)
        {
            throw new InvalidOperationException("请先选择一个项目。");
        }

        return await projects.GetAsync(selectedProjectId)
            ?? throw new InvalidOperationException("所选项目已经不存在。");
    }

    private async Task<ControlledTaskSession> RequireConversationAsync()
    {
        if (selectedConversationId is null)
        {
            throw new InvalidOperationException("请先选择一个对话。");
        }

        return await conversations.GetAsync(selectedConversationId)
            ?? throw new InvalidOperationException("所选对话已经不存在。");
    }

    private void ShowError(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Error);

    private void ShowInfo(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        ActionBar.Title = title;
        ActionBar.Message = message;
        ActionBar.Severity = severity;
        ActionBar.IsOpen = true;
    }

    private static bool IsRunning(ControlledTaskStatus status) => status is
        ControlledTaskStatus.Queued or ControlledTaskStatus.WorkerRunning or ControlledTaskStatus.MainAgentRunning or ControlledTaskStatus.WaitingForApproval;

    private static string StatusText(ControlledTaskStatus status) => status switch
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

    private static string ApprovalModeText(ExecutionApprovalMode mode) => mode switch
    {
        ExecutionApprovalMode.Safe => "安全",
        ExecutionApprovalMode.FullAuto => "完全自动",
        _ => "自动",
    };

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T result)
            {
                return result;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    public sealed record ProjectListItem(string Id, string Name, string WorkingDirectory, string State, bool IsArchived)
    {
        public static ProjectListItem From(AgentProject project) => new(
            project.Id,
            project.Name,
            project.WorkingDirectory,
            project.IsArchived ? "已归档" : "使用中",
            project.IsArchived);
    }

    public sealed record ConversationListItem(string Id, string Title, string Status, string Updated)
    {
        public static ConversationListItem From(ControlledTaskSession conversation) => new(
            conversation.Id,
            conversation.Title,
            StatusText(conversation.Status),
            conversation.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"));
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
