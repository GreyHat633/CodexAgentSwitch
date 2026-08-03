using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using CodexAgentSwitch.App.Views;

namespace CodexAgentSwitch.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1280, 800));
        RootNavigation.RequestedTheme = Environment.GetEnvironmentVariable("CAS_THEME") switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        var initialTag = Environment.GetEnvironmentVariable("CAS_CAPTURE_PAGE") ?? "dashboard";
        var initialItem = RootNavigation.MenuItems
            .Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, initialTag, StringComparison.Ordinal))
            ?? (NavigationViewItem)RootNavigation.MenuItems[0];
        RootNavigation.SelectedItem = initialItem;
        ContentFrame.Navigate(PageTypeForTag(initialTag));
        RootNavigation.Loaded += CaptureOnLoadedAsync;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
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
