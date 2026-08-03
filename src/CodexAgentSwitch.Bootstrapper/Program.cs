namespace CodexAgentSwitch.Bootstrapper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var directory = AppContext.BaseDirectory;
        var service = new BootstrapperService(new SystemOsProbe(), new WindowsAppRuntimeInventory(), new BundledInstallerLocator(directory), new SystemProcessLauncher());
        Application.Run(new BootstrapperForm(service, BootstrapperLayout.ResolveApplicationDirectory(directory)));
    }
}
