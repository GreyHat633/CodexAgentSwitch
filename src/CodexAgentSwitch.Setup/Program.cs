namespace CodexAgentSwitch.Setup;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var payload = Value(args, "--payload")
            ?? Path.Combine(AppContext.BaseDirectory, "CodexAgentSwitch-win10-x64.zip");
        var target = Value(args, "--target") ?? SetupEngine.DefaultTarget();
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            var result = await Engine().InstallAsync(payload, target, new Progress<string>(Console.WriteLine));
            Console.WriteLine(result.Message);
            return 0;
        }

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Engine().Uninstall(target, args.Contains("--delete-data", StringComparer.OrdinalIgnoreCase)).Message);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(payload));
        return 0;
    }

    private static SetupEngine Engine()
    {
        var redirectedPrograms = Environment.GetEnvironmentVariable("CAS_START_MENU_ROOT");
        return new SetupEngine(new WindowsStartMenuShortcut(
            string.IsNullOrWhiteSpace(redirectedPrograms) ? null : Path.GetFullPath(redirectedPrograms)));
    }

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
