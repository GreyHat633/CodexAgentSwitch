using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexAgentSwitch.App.ViewModels;
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ActivityTasksPage : Page
{
    private readonly IAgentSwitchUiStateSource source;
    private readonly IWorkerScheduler scheduler;
    private readonly DispatcherTimer durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ObservableCollection<ActivityTaskItem> ActiveTasks { get; } = [];
    public ObservableCollection<ActivityTaskItem> RecentTasks { get; } = [];

    public ActivityTasksPage()
    {
        InitializeComponent();
        source = App.Services.GetRequiredService<IAgentSwitchUiStateSource>();
        scheduler = App.Services.GetRequiredService<IWorkerScheduler>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        durationTimer.Tick += (_, _) =>
        {
            foreach (var task in ActiveTasks)
            {
                task.RefreshDuration();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        scheduler.SnapshotChanged -= OnSnapshotChanged;
        scheduler.SnapshotChanged += OnSnapshotChanged;
        durationTimer.Start();
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        scheduler.SnapshotChanged -= OnSnapshotChanged;
        durationTimer.Stop();
    }

    private void OnSnapshotChanged(object? sender, SchedulerSnapshot snapshot) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                _ = RefreshAsync();
            }
        });

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await source.ReadAsync();
            Replace(ActiveTasks, snapshot.Tasks.Where(task => task.IsActive).OrderByDescending(task => task.UpdatedAt));
            Replace(RecentTasks, snapshot.Tasks.Where(task => !task.IsActive).OrderByDescending(task => task.UpdatedAt).Take(20));
            ActiveEmptyText.Visibility = ActiveTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentEmptyText.Visibility = RecentTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadErrorBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            LoadErrorBar.Title = "无法读取活动任务";
            LoadErrorBar.Message = exception.Message;
            LoadErrorBar.IsOpen = true;
        }
    }

    private static void Replace(ObservableCollection<ActivityTaskItem> target, IEnumerable<WorkerTaskUiStatus> source)
    {
        target.Clear();
        foreach (var task in source)
        {
            target.Add(new ActivityTaskItem(task));
        }
    }

    public sealed class ActivityTaskItem : INotifyPropertyChanged
    {
        private readonly DateTimeOffset? startedAt;
        private readonly DateTimeOffset? completedAt;
        private string duration;

        public ActivityTaskItem(WorkerTaskUiStatus task)
        {
            Project = task.ProjectName;
            Title = task.Title;
            Profile = task.ProfileName;
            Worker = $"{task.WorkerKind} · {task.WorkerName}";
            State = task.StateLabel;
            StateBrush = UiPresentation.ToneBrush(task.Tone);
            Updated = $"更新于 {task.UpdatedAt.ToLocalTime():MM-dd HH:mm:ss}";
            Failure = task.FailureReason ?? string.Empty;
            FailureVisibility = string.IsNullOrWhiteSpace(task.FailureReason) ? Visibility.Collapsed : Visibility.Visible;
            startedAt = task.StartedAt ?? task.CreatedAt;
            completedAt = task.CompletedAt;
            duration = UiPresentation.Duration(startedAt, completedAt);
        }

        public string Project { get; }
        public string Title { get; }
        public string Profile { get; }
        public string Worker { get; }
        public string State { get; }
        public Brush StateBrush { get; }
        public string Updated { get; }
        public string Failure { get; }
        public Visibility FailureVisibility { get; }
        public string Duration { get => duration; private set { if (duration == value) return; duration = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDuration() => Duration = UiPresentation.Duration(startedAt, completedAt);

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
