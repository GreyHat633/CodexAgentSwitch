using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexAgentSwitch.Application.Profiles;
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
using UiDispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CodexAgentSwitch.App.Views;

public sealed partial class RunningTasksPage : Page, IContentActionHandler
{
    private readonly ProjectService projects = App.Services.GetRequiredService<ProjectService>();
    private readonly IProfileRepository profiles = App.Services.GetRequiredService<IProfileRepository>();
    private readonly ControlledTaskService conversations = App.Services.GetRequiredService<ControlledTaskService>();
    private string? selectedProjectId;
    private string? selectedConversationId;
    private string? renderedConversationId;
    private bool subscribed;
    private ScrollViewer? messageScroller;
    private bool userReadingHistory;
    private bool? sidebarUserPreference;
    private readonly UiDispatcherQueueTimer autoScrollTimer;
    private bool pendingAutoScroll;

    public ObservableCollection<ProjectListItem> ProjectItems { get; } = [];
    public ObservableCollection<ConversationListItem> ConversationItems { get; } = [];
    public ObservableCollection<ConversationMessageItem> MessageItems { get; } = [];

    public RunningTasksPage()
    {
        InitializeComponent();
        ProjectListView.ItemsSource = ProjectItems;
        ConversationListView.ItemsSource = ConversationItems;
        MessageListView.ItemsSource = MessageItems;
        autoScrollTimer = DispatcherQueue.CreateTimer();
        autoScrollTimer.Interval = TimeSpan.FromMilliseconds(180);
        autoScrollTimer.Tick += OnAutoScrollTimerTick;
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

        autoScrollTimer.Stop();
        pendingAutoScroll = false;
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
                case "cas:project-open-directory":
                    await OpenProjectDirectoryAsync();
                    break;
                case "cas:project-archive":
                    await ToggleProjectArchiveAsync();
                    break;
                case "cas:project-delete":
                    await DeleteProjectAsync();
                    break;
                case "cas:conversation-profile":
                    ShowConversationProfileMenu(source);
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
                case "cas:approval":
                    await ShowApprovalAsync();
                    break;
                case "cas:bottom":
                    ScrollToBottom(userInitiated: true);
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

        var profile = await profiles.GetDefaultAsync() ?? throw new InvalidOperationException("尚未设置当前配置方案。");
        var created = await projects.CreateAsync(name.Text, directory.Text, profile.Id);
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

    private async Task OpenProjectDirectoryAsync()
    {
        var project = await RequireProjectAsync();
        if (!Directory.Exists(project.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"项目工作目录不存在：{project.WorkingDirectory}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = project.WorkingDirectory,
            UseShellExecute = true,
        });
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
        var profileNames = (await profiles.ListAsync()).ToDictionary(profile => profile.Id, profile => profile.Name);
        var list = (await projects.ListAsync()).Select(project => ProjectListItem.From(
            project,
            project.DefaultProfileId is { } profileId && profileNames.TryGetValue(profileId, out var name)
                ? name
                : "当前默认方案")).ToArray();
        ProjectItems.Clear();
        foreach (var item in list)
        {
            ProjectItems.Add(item);
        }

        EmptyProjectsText.Visibility = list.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var target = selectId ?? selectedProjectId;
        var selected = target is null
            ? ProjectItems.FirstOrDefault(project => !project.IsArchived) ?? ProjectItems.FirstOrDefault()
            : ProjectItems.FirstOrDefault(project => project.Id == target);
        if (selected is null)
        {
            SetProjectActionsEnabled(false);
            NewConversationButton.IsEnabled = false;
            ClearConversation();
            ConversationItems.Clear();
            EmptyConversationsText.Visibility = Visibility.Visible;
            return;
        }

        selectedProjectId = selected.Id;
        SetProjectActionsEnabled(true);
        ProjectListView.SelectedItem = selected;
        NewConversationButton.IsEnabled = !selected.IsArchived;
        await RefreshConversationsAsync(selected.Id, selectedConversationId);
    }

    private async Task RefreshConversationsAsync(string projectId, string? selectId = null)
    {
        var list = (await conversations.ListProjectConversationsAsync(projectId)).Select(ConversationListItem.From).ToArray();
        ConversationItems.Clear();
        foreach (var item in list)
        {
            ConversationItems.Add(item);
        }

        EmptyConversationsText.Visibility = list.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var target = selectId ?? selectedConversationId;
        var selected = target is null ? ConversationItems.FirstOrDefault() : ConversationItems.FirstOrDefault(conversation => conversation.Id == target);
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

        RenderConversation(conversation);
    }

    private void RenderConversation(ControlledTaskSession conversation)
    {
        var selectionChanged = !string.Equals(renderedConversationId, conversation.Id, StringComparison.Ordinal);
        renderedConversationId = conversation.Id;
        ConversationTitleText.Text = conversation.Title;
        var lastTurn = conversation.Turns.LastOrDefault();
        var lastWorker = lastTurn?.Workers.LastOrDefault();
        var workerText = lastWorker is null
            ? lastTurn?.Delegation?.Reason ?? "尚未提交内容"
            : $"Worker：{lastWorker.ProviderName ?? lastWorker.ProviderId ?? lastWorker.AdapterId} · {lastWorker.ResponseModelId ?? lastWorker.ModelId}";
        var snapshot = lastTurn?.ProfileSnapshot ?? conversation.InitialProfileSnapshot;
        var approvalMode = snapshot?.ApprovalMode ?? ExecutionApprovalMode.Automatic;
        var providerText = snapshot?.Provider is null
            ? "Provider：原生 Codex / 未启用"
            : $"Provider：{snapshot.Provider.Name} · {snapshot.Provider.ModelId}";
        var routingText = snapshot is null ? "路由：历史对话未记录" : $"路由：{snapshot.WorkerPolicy.RoutingMode}";
        var sourceText = conversation.InitialProfileSnapshot is null ? "方案来源：旧版会话" : "方案来源：项目默认方案的对话快照";
        ConversationMetadataText.Text = $"主代理：{conversation.MainModelId}（{conversation.MainReasoningEffort}） · {workerText}\n{providerText} · {routingText} · {sourceText}\nThread：{conversation.MainThreadId ?? "尚未创建"} · 批准：{ApprovalModeText(approvalMode)}";
        var messagesChanged = SynchronizeMessages(conversation.Turns.SelectMany(turn => turn.Messages));

        var running = IsRunning(conversation.Status);
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = !running && conversation.Turns.Count > 0 && conversation.Status is not ControlledTaskStatus.Completed
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApprovalButton.Visibility = conversation.Status == ControlledTaskStatus.WaitingForApproval ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !running;
        ComposerTextBox.IsEnabled = true;
        // Keep the composer focused and editable while a response streams; only the
        // send action is disabled until the current turn reaches a terminal state.
        ComposerTextBox.IsReadOnly = false;
        UseWorkerCheckBox.IsEnabled = !running;
        ConversationStatusBar.IsOpen = conversation.Status is not ControlledTaskStatus.Completed
            && (conversation.Turns.Count > 0 || !string.IsNullOrWhiteSpace(conversation.ErrorMessage));
        ConversationStatusBar.Title = StatusText(conversation.Status);
        ConversationStatusBar.Message = conversation.ErrorMessage ?? lastTurn?.Delegation?.Reason ?? string.Empty;
        ConversationStatusBar.Severity = conversation.Status switch
        {
            ControlledTaskStatus.Completed => InfoBarSeverity.Success,
            ControlledTaskStatus.Failed or ControlledTaskStatus.UnknownRecoverable => InfoBarSeverity.Error,
            ControlledTaskStatus.Interrupted or ControlledTaskStatus.WaitingForApproval => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };

        if (selectionChanged)
        {
            userReadingHistory = false;
            QueueAutoScroll();
        }
        else if (messagesChanged && !userReadingHistory)
        {
            QueueAutoScroll();
        }

        WriteStreamingLayoutTrace(conversation, messagesChanged);
    }

    private bool SynchronizeMessages(IEnumerable<ControlledTaskMessage> messages)
    {
        var expected = messages.ToArray();
        var changed = false;
        var index = 0;
        for (; index < expected.Length; index++)
        {
            var message = expected[index];
            if (index < MessageItems.Count && MessageItems[index].Id == message.Id)
            {
                changed |= MessageItems[index].Update(message);
                continue;
            }

            while (MessageItems.Count > index)
            {
                MessageItems.RemoveAt(MessageItems.Count - 1);
                changed = true;
            }

            MessageItems.Add(new ConversationMessageItem(message));
            changed = true;
        }

        while (MessageItems.Count > expected.Length)
        {
            MessageItems.RemoveAt(MessageItems.Count - 1);
            changed = true;
        }

        return changed;
    }

    private void ClearConversation()
    {
        selectedConversationId = null;
        renderedConversationId = null;
        ConversationTitleText.Text = "选择或新建对话";
        ConversationMetadataText.Text = "一个对话始终绑定同一个主 Thread。";
        MessageItems.Clear();
        ConversationStatusBar.IsOpen = false;
        StopButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ApprovalButton.Visibility = Visibility.Collapsed;
        SetConversationActionsEnabled(false);
        ComposerTextBox.IsEnabled = false;
        ComposerTextBox.IsReadOnly = true;
        UseWorkerCheckBox.IsEnabled = false;
        SendButton.IsEnabled = false;
    }

    private void SetProjectActionsEnabled(bool enabled)
    {
        OpenProjectDirectoryButton.IsEnabled = enabled;
    }

    private void SetConversationActionsEnabled(bool enabled)
    {
        // Per-conversation menus are attached to the selected list item. Keeping
        // this hook avoids scattering selection semantics through command handlers.
    }

    private Task OnConversationChangedAsync(ControlledTaskSession conversation)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (conversation.ProjectId == selectedProjectId && conversation.Id == selectedConversationId)
            {
                RenderConversation(conversation);
                var listIndex = -1;
                for (var index = 0; index < ConversationItems.Count; index++)
                {
                    if (ConversationItems[index].Id == conversation.Id)
                    {
                        listIndex = index;
                        break;
                    }
                }

                if (listIndex >= 0)
                {
                    ConversationItems[listIndex] = ConversationListItem.From(conversation);
                }
            }
        });
        return Task.CompletedTask;
    }

    private void OpenProjectMoreMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        if (anchor.DataContext is ProjectListItem project)
        {
            selectedProjectId = project.Id;
            ProjectListView.SelectedItem = project;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItem("使用当前方案作为项目默认", SetProjectDefaultToCurrentProfileAsync));
        flyout.Items.Add(MenuItem("从下一轮应用当前方案到项目全部对话", ApplyCurrentProfileToProjectAsync));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MenuItem("重命名", RenameProjectAsync));
        flyout.Items.Add(MenuItem("更改工作目录", ChangeProjectDirectoryAsync));
        flyout.Items.Add(MenuItem("归档", ToggleProjectArchiveAsync));
        flyout.Items.Add(MenuItem("删除", DeleteProjectAsync, isDestructive: true));
        flyout.ShowAt(anchor);
    }

    private void ShowConversationProfileMenu(FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItem("从下一轮应用当前方案到此对话", ApplyCurrentProfileToConversationAsync));
        flyout.Items.Add(MenuItem("从下一轮应用当前方案到项目全部对话", ApplyCurrentProfileToProjectAsync));
        flyout.ShowAt(anchor);
    }

    private async Task SetProjectDefaultToCurrentProfileAsync()
    {
        var project = await RequireProjectAsync();
        var profile = await profiles.GetDefaultAsync() ?? throw new InvalidOperationException("尚未设置当前配置方案。");
        await projects.SetDefaultProfileAsync(project.Id, profile.Id);
        await RefreshProjectsAsync(project.Id);
        ShowInfo("项目默认方案已更新", $"“{profile.Name}”只会自动用于此后新建的对话；已有对话保持各自快照。", InfoBarSeverity.Success);
    }

    private async Task ApplyCurrentProfileToConversationAsync()
    {
        await RequireConversationAsync();
        var profile = await profiles.GetDefaultAsync() ?? throw new InvalidOperationException("尚未设置当前配置方案。");
        await conversations.ApplyProfileFromNextTurnAsync(selectedConversationId!, profile.Id);
        await ShowConversationAsync(selectedConversationId!);
        ShowInfo("下一轮将使用新方案", $"当前对话从下一轮起改用“{profile.Name}”；历史 Turn 不会被修改。", InfoBarSeverity.Success);
    }

    private async Task ApplyCurrentProfileToProjectAsync()
    {
        var project = await RequireProjectAsync();
        var profile = await profiles.GetDefaultAsync() ?? throw new InvalidOperationException("尚未设置当前配置方案。");
        await projects.SetDefaultProfileAsync(project.Id, profile.Id);
        await conversations.ApplyProjectProfileFromNextTurnAsync(project.Id, profile.Id);
        await RefreshProjectsAsync(project.Id);
        await RefreshConversationsAsync(project.Id, selectedConversationId);
        ShowInfo("项目方案已安排", $"“{profile.Name}”已设为默认；非运行中的项目对话将从下一轮起使用它。", InfoBarSeverity.Success);
    }

    private void OpenConversationMoreMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        if (anchor.DataContext is ConversationListItem conversation)
        {
            selectedConversationId = conversation.Id;
            ConversationListView.SelectedItem = conversation;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItem("重命名", RenameConversationAsync));
        flyout.Items.Add(MenuItem("删除", DeleteConversationAsync, isDestructive: true));
        flyout.ShowAt(anchor);
    }

    private MenuFlyoutItem MenuItem(string text, Func<Task> action, bool isDestructive = false)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (isDestructive)
        {
            item.Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                ShowError("操作失败", exception.Message);
            }
        };
        return item;
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
            if (MessageItems.Count > 0 && !userReadingHistory)
            {
                QueueAutoScroll();
            }
        }
    }

    private void OnMessageViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (messageScroller is null)
        {
            return;
        }

        userReadingHistory = messageScroller.ScrollableHeight - messageScroller.VerticalOffset > 96;
        BackToBottomButton.Visibility = userReadingHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueueAutoScroll()
    {
        if (userReadingHistory || messageScroller is null)
        {
            return;
        }

        pendingAutoScroll = true;
        if (!autoScrollTimer.IsRunning)
        {
            autoScrollTimer.Start();
        }
    }

    private void OnAutoScrollTimerTick(UiDispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!pendingAutoScroll || userReadingHistory || messageScroller is null)
        {
            pendingAutoScroll = false;
            return;
        }

        pendingAutoScroll = false;
        messageScroller.ChangeView(null, messageScroller.ScrollableHeight, null, disableAnimation: true);
    }

    private void ScrollToBottom(bool userInitiated)
    {
        userReadingHistory = false;
        BackToBottomButton.Visibility = Visibility.Collapsed;
        pendingAutoScroll = false;
        autoScrollTimer.Stop();
        messageScroller?.ChangeView(null, messageScroller.ScrollableHeight, null, disableAnimation: userInitiated);
    }

    private void WriteStreamingLayoutTrace(ControlledTaskSession conversation, bool messagesChanged)
    {
        var path = Environment.GetEnvironmentVariable("CAS_STREAM_LAYOUT_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(path) || !messagesChanged)
        {
            return;
        }

        // This trace is test-only and intentionally refuses the system drive so
        // UI diagnostics follow the same E-drive storage policy as application data.
        var fullPath = Path.GetFullPath(path);
        if (Path.GetPathRoot(fullPath)?.Equals("C:\\", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var last = MessageItems.LastOrDefault()?.Message;
        var trace = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.UtcNow,
            conversationId = conversation.Id,
            status = conversation.Status.ToString(),
            messageCount = MessageItems.Count,
            lastMessageLength = last?.Content.Length ?? 0,
            stableItemsSource = ReferenceEquals(MessageListView.ItemsSource, MessageItems),
            userReadingHistory,
            verticalOffset = messageScroller?.VerticalOffset,
            scrollableHeight = messageScroller?.ScrollableHeight,
        });
        File.AppendAllText(fullPath, trace + Environment.NewLine);
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
        SidebarColumn.Width = visible ? new GridLength(248) : new GridLength(0);
        WorkspaceGrid.ColumnSpacing = visible ? 16 : 0;
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

    public sealed class ProjectListItem
    {
        public ProjectListItem(string id, string name, string workingDirectory, string state, bool isArchived)
        {
            Id = id;
            Name = name;
            WorkingDirectory = workingDirectory;
            State = state;
            IsArchived = isArchived;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public string WorkingDirectory { get; set; }

        public string State { get; set; }

        public bool IsArchived { get; set; }

        public static ProjectListItem From(AgentProject project, string profileName) => new(
            project.Id,
            project.Name,
            project.WorkingDirectory,
            project.IsArchived ? $"已归档 · 默认方案：{profileName}" : $"使用中 · 默认方案：{profileName}",
            project.IsArchived);
    }

    public sealed class ConversationListItem
    {
        public ConversationListItem(string id, string title, string status, string updated)
        {
            Id = id;
            Title = title;
            Status = status;
            Updated = updated;
        }

        public string Id { get; set; }

        public string Title { get; set; }

        public string Status { get; set; }

        public string Updated { get; set; }

        public static ConversationListItem From(ControlledTaskSession conversation) => new(
            conversation.Id,
            conversation.Title,
            StatusText(conversation.Status),
            conversation.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"));
    }

    public sealed class ConversationMessageItem : INotifyPropertyChanged
    {
        private ControlledTaskMessage message;

        public ConversationMessageItem(ControlledTaskMessage message)
        {
            this.message = message;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid Id => message.Id;

        public ControlledTaskMessage Message => message;

        public bool Update(ControlledTaskMessage next)
        {
            if (Equals(message, next))
            {
                return false;
            }

            message = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            return true;
        }
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
