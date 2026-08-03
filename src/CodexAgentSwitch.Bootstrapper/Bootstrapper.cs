using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexAgentSwitch.Bootstrapper;

public sealed record OsSnapshot(Version Version, Architecture Architecture);
public sealed record RuntimeInstallation(Version Version, Architecture Architecture, string Source, Version? PackageVersion = null);
public sealed record BootstrapperStatus(bool SupportedOs, bool RuntimePresent, bool RuntimeVersionMismatch, OsSnapshot Os, IReadOnlyList<RuntimeInstallation> Installations, string Message);

public interface IOsProbe { OsSnapshot Read(); }
public interface IRuntimeInventory { IReadOnlyList<RuntimeInstallation> Find(); }
public interface IInstallerLocator { string? Find(); }
public interface IProcessLauncher { int Launch(string fileName, string? arguments = null); }

public sealed class SystemOsProbe : IOsProbe
{
    public OsSnapshot Read() => new(Environment.OSVersion.Version, RuntimeInformation.OSArchitecture);
}

public sealed class WindowsAppRuntimeInventory : IRuntimeInventory
{
    private const string InstalledVersionsKey = @"SOFTWARE\Microsoft\WindowsAppRuntime\InstalledVersions";
    private const string PackageRepositoryKey = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public IReadOnlyList<RuntimeInstallation> Find()
    {
        var result = new List<RuntimeInstallation>();
        ReadInstalledVersionKeys(Registry.LocalMachine, InstalledVersionsKey, result);
        ReadInstalledVersionKeys(Registry.CurrentUser, InstalledVersionsKey, result);
        ReadPackageKeys(Registry.LocalMachine, PackageRepositoryKey, result);
        ReadPackageKeys(Registry.CurrentUser, PackageRepositoryKey, result);
        return result.Distinct().ToArray();
    }

    private static void ReadInstalledVersionKeys(RegistryKey root, string path, ICollection<RuntimeInstallation> result)
    {
        using var key = root.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetSubKeyNames())
        {
            if (!Version.TryParse(name, out var version)) continue;
            using var versionKey = key.OpenSubKey(name);
            var architecture = ParseArchitecture(versionKey?.GetValue("Architecture") as string) ?? Architecture.X64;
            result.Add(new(version, architecture, $"registry:{path}\\{name}"));
        }
    }

    private static void ReadPackageKeys(RegistryKey root, string path, ICollection<RuntimeInstallation> result)
    {
        using var key = root.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetSubKeyNames())
            if (TryParsePackageName(name, out var installation)) result.Add(installation with { Source = $"registry:{path}\\{name}" });
    }

    public static bool TryParsePackageName(string packageName, out RuntimeInstallation installation)
    {
        installation = null!;
        var parts = packageName.Split('_');
        if (parts.Length < 4 || !parts[0].StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase)) return false;
        var channelText = parts[0]["Microsoft.WindowsAppRuntime.".Length..];
        if (!Version.TryParse(channelText, out var channel) || !Version.TryParse(parts[1], out var packageVersion)) return false;
        var architecture = parts[2].ToLowerInvariant() switch { "x64" => Architecture.X64, "x86" => Architecture.X86, "arm64" => Architecture.Arm64, _ => (Architecture?)null };
        if (architecture is null) return false;
        installation = new(channel, architecture.Value, $"package:{packageName}", packageVersion);
        return true;
    }

    private static Architecture? ParseArchitecture(string? value) => value?.ToLowerInvariant() switch
    {
        "x64" or "amd64" => Architecture.X64,
        "x86" => Architecture.X86,
        "arm64" => Architecture.Arm64,
        _ => null
    };
}

public sealed class BundledInstallerLocator(string applicationDirectory, string installerFileName = "WindowsAppRuntime-1.8-x64.exe") : IInstallerLocator
{
    public string? Find()
    {
        var root = Path.GetFullPath(applicationDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, "RuntimeInstaller", installerFileName));
        return candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }
}

public sealed class SystemProcessLauncher : IProcessLauncher
{
    public int Launch(string fileName, string? arguments = null)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName) { Arguments = arguments ?? string.Empty, UseShellExecute = true });
        return process?.Id ?? -1;
    }
}

public sealed class BootstrapperService(IOsProbe osProbe, IRuntimeInventory inventory, IInstallerLocator installerLocator, IProcessLauncher processLauncher)
{
    public static readonly Version RequiredRuntime = new(1, 8);
    public BootstrapperStatus Inspect()
    {
        var os = osProbe.Read();
        var installations = inventory.Find();
        var supported = os.Architecture == Architecture.X64 && os.Version.Build >= 19045;
        var matching = installations.Any(x => x.Architecture == Architecture.X64 && x.Version.Major == RequiredRuntime.Major && x.Version.Minor >= RequiredRuntime.Minor);
        var sameMajor = installations.Any(x => x.Architecture == Architecture.X64 && x.Version.Major == RequiredRuntime.Major);
        var message = !supported ? $"Unsupported system: Windows 10 22H2 (build 19045) or Windows 11 x64 is required; detected build {os.Version.Build}, {os.Architecture}." : matching ? $"Windows App Runtime {RequiredRuntime.Major}.{RequiredRuntime.Minor} x64 is available." : sameMajor ? $"Windows App Runtime x64 is installed, but version 1.8 or newer is required." : "Windows App Runtime 1.8 x64 is not installed.";
        return new(supported, matching, !matching && sameMajor, os, installations, message);
    }

    public bool InstallAfterConfirmation(Func<string, bool> confirm, out string message)
    {
        var path = installerLocator.Find();
        if (path is null) { message = "Bundled Windows App Runtime x64 installer was not found."; return false; }
        if (!confirm(path)) { message = "Installation cancelled."; return false; }
        try { processLauncher.Launch(path); message = "The bundled installer was started. Re-check runtime status after it completes."; return true; }
        catch (Exception ex) { message = $"The installer could not be started: {ex.Message}"; return false; }
    }

    public bool LaunchMainApp(string applicationDirectory, out string message)
    {
        var path = Path.GetFullPath(Path.Combine(applicationDirectory, "CodexAgentSwitch.App.exe"));
        if (!File.Exists(path)) { message = $"Main app was not found: {path}"; return false; }
        try { processLauncher.Launch(path); message = "Main app launched."; return true; }
        catch (Exception ex) { message = $"The main app could not be started: {ex.Message}"; return false; }
    }
}
