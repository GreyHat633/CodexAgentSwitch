using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace CodexAgentSwitch.Bootstrapper;

public sealed record OsSnapshot(Version Version, Architecture Architecture);
public enum RuntimeComponent { Framework, Main, Singleton, Ddlm, CompleteMarker }
public sealed record RuntimeInstallation(Version Version, Architecture Architecture, string Source, Version? PackageVersion = null, RuntimeComponent Component = RuntimeComponent.Framework);
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
        TryRead(() => ReadInstalledVersionKeys(Registry.LocalMachine, InstalledVersionsKey, result));
        TryRead(() => ReadInstalledVersionKeys(Registry.CurrentUser, InstalledVersionsKey, result));
        TryRead(() => ReadPackageKeys(Registry.LocalMachine, PackageRepositoryKey, result));
        TryRead(() => ReadPackageKeys(Registry.CurrentUser, PackageRepositoryKey, result));
        return result.Distinct().ToArray();
    }

    private static void TryRead(Action read)
    {
        try { read(); }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (IOException) { }
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
            result.Add(new(version, architecture, $"registry:{path}\\{name}", Component: RuntimeComponent.CompleteMarker));
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
        if (parts.Length < 4 || !Version.TryParse(parts[1], out var packageVersion)) return false;
        var architecture = parts[2].ToLowerInvariant() switch { "x64" => Architecture.X64, "x86" => Architecture.X86, "arm64" => Architecture.Arm64, _ => (Architecture?)null };
        if (architecture is null) return false;

        var name = parts[0];
        Version channel;
        RuntimeComponent component;
        if (name.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(name["Microsoft.WindowsAppRuntime.".Length..], out channel!))
        {
            component = RuntimeComponent.Framework;
        }
        else if (name.StartsWith("MicrosoftCorporationII.WindowsAppRuntime.Main.", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(name["MicrosoftCorporationII.WindowsAppRuntime.Main.".Length..], out channel!))
        {
            component = RuntimeComponent.Main;
        }
        else if (name.Equals("Microsoft.WindowsAppRuntime.Singleton", StringComparison.OrdinalIgnoreCase))
        {
            channel = new Version(0, 0);
            component = RuntimeComponent.Singleton;
        }
        else if (name.StartsWith("Microsoft.WinAppRuntime.DDLM.", StringComparison.OrdinalIgnoreCase))
        {
            channel = new Version(0, 0);
            component = RuntimeComponent.Ddlm;
        }
        else return false;

        installation = new(channel, architecture.Value, $"package:{packageName}", packageVersion, component);
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

public sealed class BundledInstallerLocator(string applicationDirectory, string installerFileName = "WindowsAppRuntimeInstall-x64.exe") : IInstallerLocator
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
        var completeMarker = installations.Any(x => x.Component == RuntimeComponent.CompleteMarker
            && x.Architecture == Architecture.X64 && IsRequiredChannel(x.Version));
        var frameworks = installations.Where(x => x.Component == RuntimeComponent.Framework
            && x.Architecture == Architecture.X64 && IsRequiredChannel(x.Version)).ToArray();
        var matching = completeMarker || frameworks.Any(framework => framework.PackageVersion is not null
            && HasMatchingComponent(installations, RuntimeComponent.Main, framework.PackageVersion)
            && HasMatchingComponent(installations, RuntimeComponent.Singleton, framework.PackageVersion)
            && HasMatchingComponent(installations, RuntimeComponent.Ddlm, framework.PackageVersion));
        var sameMajor = installations.Any(x => x.Architecture == Architecture.X64
            && x.Component is RuntimeComponent.Framework or RuntimeComponent.CompleteMarker
            && x.Version.Major == RequiredRuntime.Major);
        var hasRequiredFramework = frameworks.Length > 0;
        var message = !supported ? $"Unsupported system: Windows 10 22H2 (build 19045) or Windows 11 x64 is required; detected build {os.Version.Build}, {os.Architecture}."
            : matching ? $"Windows App Runtime {RequiredRuntime.Major}.{RequiredRuntime.Minor} x64 is complete (Framework, Main, Singleton, and DDLM)."
            : hasRequiredFramework ? "Windows App Runtime 1.8 x64 is incomplete; Framework, Main, Singleton, and DDLM must be installed together."
            : sameMajor ? "Windows App Runtime x64 is installed, but version 1.8 or newer is required."
            : "Windows App Runtime 1.8 x64 is not installed.";
        return new(supported, matching, !matching && sameMajor, os, installations, message);
    }

    private static bool IsRequiredChannel(Version version) =>
        version.Major == RequiredRuntime.Major && version.Minor >= RequiredRuntime.Minor;

    private static bool HasMatchingComponent(IEnumerable<RuntimeInstallation> installations, RuntimeComponent component, Version packageVersion) =>
        installations.Any(x => x.Component == component && x.Architecture == Architecture.X64 && x.PackageVersion == packageVersion);

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
