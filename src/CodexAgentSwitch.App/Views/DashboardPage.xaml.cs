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
            foreach (var p in state.Projects.Where(p => p.IsConfigured)) Projects.Add(new ProjectCard(p.Name, p.WorkingDirectory, p.ProfileName, p.MainAgent, p.Worker, p.StateLabel, p.AppliedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未应用"));
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
            "dashboard:launch-native" => NavigateNativeAsync("desktop"),
            "dashboard:launch-desktop" => LaunchDesktopAsync(),
            _ => Task.CompletedTask,
        };
    }

    private Task NavigateNativeAsync(string mode) { Frame.Navigate(typeof(NativeProjectAdapterPage), mode); return Task.CompletedTask; }
    private async Task LaunchDesktopAsync() { try { await App.Services.GetRequiredService<ICodexDesktopLauncher>().LaunchDesktopAsync(); } catch (Exception ex) { DashboardActionBar.Title = "无法启动 Codex"; DashboardActionBar.Message = ex.Message; DashboardActionBar.IsOpen = true; DashboardActionBar.Severity = InfoBarSeverity.Error; } }

    public sealed class ProjectCard(string name, string workingDirectory, string profile, string mainAgent, string worker, string state, string applied)
    { public string Name { get; set; } = name; public string WorkingDirectory { get; set; } = workingDirectory; public string Profile { get; set; } = profile; public string MainAgent { get; set; } = mainAgent; public string Worker { get; set; } = worker; public string State { get; set; } = state; public string Applied { get; set; } = applied; }
    public sealed class TaskCard(string project, string title, string worker, string state)
    { public string Project { get; set; } = project; public string Title { get; set; } = title; public string Worker { get; set; } = worker; public string State { get; set; } = state; }
}
