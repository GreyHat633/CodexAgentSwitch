using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using CodexAgentSwitch.App.Views;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.Domain.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace CodexAgentSwitch.App;

public sealed partial class MainWindow : Window
{
    private readonly HashSet<Button> tracedButtons = [];
    private readonly IWorkerScheduler scheduler;
    private readonly IAgentSwitchUiStateSource uiState;

    public MainWindow()
    {
        InitializeComponent();
        scheduler = App.Services.GetRequiredService<IWorkerScheduler>();
        uiState = App.Services.GetRequiredService<IAgentSwitchUiStateSource>();
        scheduler.SnapshotChanged += OnSchedulerSnapshotChanged;
        Closed += (_, _) => scheduler.SnapshotChanged -= OnSchedulerSnapshotChanged;
        _ = RefreshUiStateAsync();
        ApplyWindowIcon();
        RootNavigation.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TracePointerPressed), true);
        RootNavigation.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TracePointerReleased), true);
        RootNavigation.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(TracePointerCanceled), true);
        RootNavigation.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(TracePointerCaptureLost), true);
        RootNavigation.AddHandler(UIElement.TappedEvent, new TappedEventHandler(TraceTapped), true);
        ContentFrame.Navigated += OnContentNavigated;
        RootNavigation.RequestedTheme = Environment.GetEnvironmentVariable("CAS_THEME") switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        var initialTag = Environment.GetEnvironmentVariable("CAS_CAPTURE_PAGE")
            ?? (IsFirstRun() ? "onboarding" : "dashboard");
        var initialItem = RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, initialTag, StringComparison.Ordinal))
            ?? (NavigationViewItem)RootNavigation.MenuItems[0];
        RootNavigation.SelectedItem = initialItem;
        if (ContentFrame.CurrentSourcePageType != PageTypeForTag(initialTag))
        {
            ContentFrame.Navigate(PageTypeForTag(initialTag));
        }
        RootNavigation.Loaded += ResizeForRasterizationScale;
        RootNavigation.Loaded += CaptureOnLoadedAsync;
    }

    private void OnSchedulerSnapshotChanged(object? sender, SchedulerSnapshot snapshot) => _ = RefreshUiStateAsync();

    private async Task RefreshUiStateAsync()
    {
        try { var state = await uiState.ReadAsync(); DispatcherQueue.TryEnqueue(() => UpdateAgentSwitchStatus(state)); }
        catch (Exception ex) { DispatcherQueue.TryEnqueue(() => { SchedulerStateText.Text = "Agent Switch 状态不可用"; SchedulerDetailText.Text = ex.Message; }); }
    }

    private void UpdateAgentSwitchStatus(AgentSwitchUiSnapshot snapshot)
    {
        SchedulerStateText.Text = snapshot.StateLabel;
        var activitySummary = $"{snapshot.ActiveTaskCount} 个活动任务";
        SchedulerDetailText.Text = snapshot.StateDetail.Contains(activitySummary, StringComparison.Ordinal)
            ? snapshot.StateDetail
            : $"{snapshot.StateDetail} · {activitySummary}";
        SchedulerPauseButton.Content = snapshot.State == SchedulerState.Paused ? "恢复" : "暂停";
        SchedulerPauseButton.IsEnabled = snapshot.State is SchedulerState.Ready or SchedulerState.Working or SchedulerState.Paused;
        SchedulerStateDot.Fill = (Brush)Microsoft.UI.Xaml.Application.Current.Resources[snapshot.State switch
        {
            SchedulerState.Ready => "SystemFillColorSuccessBrush",
            SchedulerState.Working => "AccentFillColorDefaultBrush",
            SchedulerState.Faulted => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorCautionBrush",
        }];
    }

    private async void PauseOrResumeSchedulerAsync(object sender, RoutedEventArgs args)
    {
        if (scheduler.Snapshot.State == SchedulerState.Paused)
        {
            await scheduler.ResumeAsync();
        }
        else
        {
            await scheduler.PauseAsync();
        }
    }

    private void NavigateToActiveTasks(object sender, RoutedEventArgs args) => NavigateTo("tasks");

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private static bool IsFirstRun()
    {
        var paths = App.Services.GetRequiredService<CodexAgentSwitch.Infrastructure.Common.AppDataPaths>();
        return !File.Exists(Path.Combine(paths.Root, "onboarding.completed.json"));
    }

    private void OnContentNavigated(object sender, NavigationEventArgs args)
    {
        if (args.Content is not FrameworkElement page)
        {
            return;
        }

        if (page.IsLoaded)
        {
            WireButtonTracing(page);
        }
        else
        {
            page.Loaded += OnPageLoaded;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement page)
        {
            page.Loaded -= OnPageLoaded;
            WireButtonTracing(page);
        }
    }

    private void WireButtonTracing(DependencyObject root)
    {
        if (root is Button button && tracedButtons.Add(button))
        {
            button.Click += OnContentButtonClick;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            WireButtonTracing(VisualTreeHelper.GetChild(root, index));
        }
    }

    private void TracePointerPressed(object sender, PointerRoutedEventArgs args)
        => TracePointer("pointer-pressed", args);

    private void TracePointerReleased(object sender, PointerRoutedEventArgs args)
        => TracePointer("pointer-released", args);

    private void TracePointerCanceled(object sender, PointerRoutedEventArgs args)
        => TracePointer("pointer-canceled", args);

    private void TracePointerCaptureLost(object sender, PointerRoutedEventArgs args)
        => TracePointer("pointer-capture-lost", args);

    private void TracePointer(string kind, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(RootNavigation).Position;
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, RootNavigation)
            .Take(12)
            .Select(DescribeElement)
            .ToArray();
        WriteInputTrace(kind, args.OriginalSource, point.X, point.Y, hits);
    }

    private void TraceTapped(object sender, TappedRoutedEventArgs args) =>
        WriteInputTrace("tapped", args.OriginalSource, args.GetPosition(RootNavigation).X, args.GetPosition(RootNavigation).Y, []);

    private async void OnContentButtonClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button)
        {
            return;
        }

        var action = button.Tag as string;
        WriteInputTrace("button-click", args.OriginalSource, null, null,
            action is null ? [] : [$"action:{action}"]);
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        try
        {
            if (action.StartsWith("navigate:", StringComparison.Ordinal))
            {
                NavigateTo(action["navigate:".Length..]);
            }
            else if (ContentFrame.Content is IContentActionHandler handler)
            {
                await handler.HandleContentActionAsync(action, button);
            }
            else
            {
                throw new InvalidOperationException($"The current page does not handle action '{action}'.");
            }

            WriteInputTrace("action-completed", button, null, null, [$"action:{action}"]);
        }
        catch (Exception exception)
        {
            WriteInputTrace("action-failed", button, null, null, [$"action:{action}", $"error:{exception.GetType().Name}"]);
        }
    }

    private static string DescribeElement(DependencyObject element) => element switch
    {
        FrameworkElement framework => $"{framework.GetType().Name}:{framework.Name}",
        _ => element.GetType().Name,
    };

    private static void WriteInputTrace(string kind, object originalSource, double? x, double? y, IReadOnlyList<string> hits)
    {
        var path = Environment.GetEnvironmentVariable("CAS_INPUT_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now,
            kind,
            originalSource = originalSource.GetType().Name,
            x,
            y,
            hits,
        });
        File.AppendAllText(path, record + Environment.NewLine);
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var item = RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
        if (item is not null && !ReferenceEquals(RootNavigation.SelectedItem, item))
        {
            RootNavigation.SelectedItem = item;
        }

        var pageType = PageTypeForTag(tag);
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private static Type PageTypeForTag(string tag) => tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "native-projects" => typeof(NativeProjectAdapterPage),
            "profiles" => typeof(ProfilesPage),
            "providers" => typeof(ProvidersPage),
            "tasks" => typeof(RunningTasksPage),
            "usage" => typeof(UsageBudgetPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "onboarding" => typeof(OnboardingPage),
            "gallery" => typeof(UiGalleryPage),
            _ => typeof(DashboardPage),
        };

    private static int ReadDimension(string variable, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private void ResizeForRasterizationScale(object sender, RoutedEventArgs args)
    {
        RootNavigation.Loaded -= ResizeForRasterizationScale;
        var scale = RootNavigation.XamlRoot?.RasterizationScale ?? 1d;
        var desiredWidth = (int)Math.Round(ReadDimension("CAS_WINDOW_WIDTH", 1280, 1024, 3840) * scale);
        var desiredHeight = (int)Math.Round(ReadDimension("CAS_WINDOW_HEIGHT", 800, 720, 2160) * scale);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)?.WorkArea;
        if (workArea is { } bounds)
        {
            desiredWidth = Math.Min(desiredWidth, bounds.Width);
            desiredHeight = Math.Min(desiredHeight, bounds.Height);
        }

        AppWindow.Resize(new SizeInt32(Math.Max(1, desiredWidth), Math.Max(1, desiredHeight)));
    }

    private async void CaptureOnLoadedAsync(object sender, RoutedEventArgs args)
    {
        var capturePath = Environment.GetEnvironmentVariable("CAS_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        await Task.Delay(500);
        Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
        await File.WriteAllBytesAsync(capturePath, []);

        var renderTarget = new RenderTargetBitmap();
        await renderTarget.RenderAsync(RootNavigation);
        var pixelBuffer = await renderTarget.GetPixelsAsync();
        var pixels = new byte[pixelBuffer.Length];
        using (var reader = DataReader.FromBuffer(pixelBuffer))
        {
            reader.ReadBytes(pixels);
        }

        var file = await StorageFile.GetFileFromPathAsync(capturePath);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)renderTarget.PixelWidth,
            (uint)renderTarget.PixelHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        if (string.Equals(Environment.GetEnvironmentVariable("CAS_CAPTURE_EXIT"), "1", StringComparison.Ordinal))
        {
            Close();
        }
    }
}
