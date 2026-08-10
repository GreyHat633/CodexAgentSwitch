using System.Collections.ObjectModel;
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class DashboardPage : Page, IContentActionHandler
{
    public ObservableCollection<ProjectCard> Projects { get; } = [];
    public ObservableCollection<TaskCard> Tasks { get; } = [];
    private readonly IAgentSwitchUiStateSource source;
    private readonly IWorkerScheduler scheduler;

    public DashboardPage()
    {
        InitializeComponent();
        source = App.Services.GetRequiredService<IAgentSwitchUiStateSource>();
        scheduler = App.Services.GetRequiredService<IWorkerScheduler>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) { scheduler.SnapshotChanged -= OnSnapshotChanged; scheduler.SnapshotChanged += OnSnapshotChanged; await RefreshAsync(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) => scheduler.SnapshotChanged -= OnSnapshotChanged;
    private void OnSnapshotChanged(object? sender, SchedulerSnapshot args) => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) _ = RefreshAsync(); });

    private async Task RefreshAsync()
    {
        try
        {
            var state = await source.ReadAsync();
            StateText.Text = $"{state.StateLabel} · {state.StateDetail}";
            Projects.Clear();
            foreach (var p in state.Projects.Where(p => p.IsConfigured)) Projects.Add(new ProjectCard(p.Id, p.Name, p.WorkingDirectory, p.ProfileName, p.MainAgent, p.Worker, p.StateLabel, p.AppliedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未应用"));
            Tasks.Clear();
            foreach (var t in state.Tasks.Where(t => t.IsActive).OrderByDescending(t => t.UpdatedAt).Take(3)) Tasks.Add(new TaskCard(t.ProjectName, t.Title, t.WorkerName, t.StateLabel));
            ProjectsEmpty.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TasksEmpty.Visibility = Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { DashboardActionBar.Title = "无法读取 Agent Switch 状态"; DashboardActionBar.Message = ex.Message; DashboardActionBar.IsOpen = true; DashboardActionBar.Severity = InfoBarSeverity.Error; }
    }

    public Task HandleContentActionAsync(string action, Button source)
    {
        return action switch
        {
            "dashboard:launch-desktop" => LaunchDesktopAsync(source),
            _ => Task.CompletedTask,
        };
    }

    private async Task LaunchDesktopAsync(Button button)
    {
        if (Projects.Count == 0)
        {
            DashboardActionBar.Severity = InfoBarSeverity.Warning;
            DashboardActionBar.Title = "请先配置项目";
            DashboardActionBar.Message = "当前没有已应用方案的项目。请进入“项目配置”，选择项目与方案并完成应用。";
            DashboardActionBar.IsOpen = true;
            return;
        }

        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "启动中…";
        try
        {
            var selected = Projects.Count == 1 ? Projects[0] : await SelectLaunchProjectAsync();
            if (selected is null)
            {
                DashboardActionBar.Severity = InfoBarSeverity.Informational;
                DashboardActionBar.Title = "已取消启动";
                DashboardActionBar.Message = "没有启动或修改 Codex。";
                DashboardActionBar.IsOpen = true;
                return;
            }

            DashboardActionBar.Severity = InfoBarSeverity.Informational;
            DashboardActionBar.Title = "正在启动 Codex";
            DashboardActionBar.Message = $"正在使用“{selected.Name}”的已应用配置启动官方 Codex 桌面应用。";
            DashboardActionBar.IsOpen = true;
            var target = await App.Services.GetRequiredService<ICodexDesktopLauncher>().LaunchDesktopAsync();
            DashboardActionBar.Severity = InfoBarSeverity.Success;
            DashboardActionBar.Title = "Codex 已启动";
            DashboardActionBar.Message = $"已通过 {target} 启动。请在 Codex 中打开“{selected.Name}”：{selected.WorkingDirectory}";
        }
        catch (Exception ex)
        {
            DashboardActionBar.Title = "无法启动 Codex";
            DashboardActionBar.Message = ex.Message;
            DashboardActionBar.Severity = InfoBarSeverity.Error;
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    private async Task<ProjectCard?> SelectLaunchProjectAsync()
    {
        var picker = new ComboBox
        {
            Header = "选择要在 Codex 中使用的项目",
            ItemsSource = Projects,
            DisplayMemberPath = nameof(ProjectCard.Name),
            SelectedIndex = 0,
            MinWidth = 420,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "启动 Codex",
            Content = picker,
            PrimaryButtonText = "启动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? picker.SelectedItem as ProjectCard
            : null;
    }

    public sealed class ProjectCard(string id, string name, string workingDirectory, string profile, string mainAgent, string worker, string state, string applied)
    { public string Id { get; set; } = id; public string Name { get; set; } = name; public string WorkingDirectory { get; set; } = workingDirectory; public string Profile { get; set; } = profile; public string MainAgent { get; set; } = mainAgent; public string Worker { get; set; } = worker; public string State { get; set; } = state; public string Applied { get; set; } = applied; }
    public sealed class TaskCard(string project, string title, string worker, string state)
    { public string Project { get; set; } = project; public string Title { get; set; } = title; public string Worker { get; set; } = worker; public string State { get; set; } = state; }
}
