using Microsoft.UI.Xaml;

namespace CodexAgentSwitch.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            var dataRoot = Environment.GetEnvironmentVariable("CAS_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                var diagnosticDirectory = Path.Combine(dataRoot, "diagnostics");
                Directory.CreateDirectory(diagnosticDirectory);
                File.WriteAllText(Path.Combine(diagnosticDirectory, "startup-crash.txt"), exception.ToString());
            }

            throw;
        }
    }
}
