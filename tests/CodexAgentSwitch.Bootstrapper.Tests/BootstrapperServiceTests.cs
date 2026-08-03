using System.Runtime.InteropServices;
using CodexAgentSwitch.Bootstrapper;
using Xunit;

namespace CodexAgentSwitch.Bootstrapper.Tests;

public sealed class BootstrapperServiceTests
{
    [Fact]
    public void Compact_layout_prefers_nested_app_and_preserves_legacy_same_directory_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "cas-bootstrapper-layout-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "App");
        Directory.CreateDirectory(nested);
        try
        {
            Assert.Equal(root, BootstrapperLayout.ResolveApplicationDirectory(root));
            File.WriteAllText(Path.Combine(nested, "CodexAgentSwitch.App.exe"), string.Empty);
            Assert.Equal(nested, BootstrapperLayout.ResolveApplicationDirectory(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(19045, Architecture.X64, true)]
    [InlineData(22621, Architecture.X64, true)]
    [InlineData(19044, Architecture.X64, false)]
    [InlineData(22621, Architecture.Arm64, false)]
    public void Supported_os_is_checked_separately(int build, Architecture architecture, bool expected)
    {
        var service = Service(new(build is 19045 ? new Version(10, 0, 19045) : new Version(10, 0, build), architecture), CompleteRuntime());
        Assert.Equal(expected, service.Inspect().SupportedOs);
    }

    [Fact] public void Missing_runtime_is_distinguished() => Assert.False(Service(new(new(10, 0, 19045), Architecture.X64), null).Inspect().RuntimePresent);
    [Fact] public void Older_runtime_is_version_mismatch() => Assert.True(Service(new(new(10, 0, 19045), Architecture.X64), new RuntimeInstallation(new(1, 7), Architecture.X64, "test")).Inspect().RuntimeVersionMismatch);
    [Fact] public void Matching_complete_x64_runtime_is_accepted() => Assert.True(Service(new(new(10, 0, 22621), Architecture.X64), CompleteRuntime()).Inspect().RuntimePresent);
    [Fact] public void Framework_without_ddlm_main_and_singleton_is_incomplete() => Assert.False(Service(new(new(10, 0, 19045), Architecture.X64), new RuntimeInstallation(new(1, 8), Architecture.X64, "test", new(8000, 921, 1539, 0))).Inspect().RuntimePresent);
    [Fact] public void Arm64_runtime_does_not_match() => Assert.False(Service(new(new(10, 0, 22621), Architecture.X64), new RuntimeInstallation(new(1, 8), Architecture.Arm64, "test")).Inspect().RuntimePresent);

    [Theory]
    [InlineData("Microsoft.WindowsAppRuntime.1.8_8000.921.1539.0_x64__8wekyb3d8bbwe", true, Architecture.X64)]
    [InlineData("Microsoft.WindowsAppRuntime.1.7_7000.100.0.0_x64__8wekyb3d8bbwe", true, Architecture.X64)]
    [InlineData("Microsoft.WindowsAppRuntime.1.8_8000.921.1539.0_x86__8wekyb3d8bbwe", true, Architecture.X86)]
    public void Package_name_parser_preserves_channel_and_package_version(string packageName, bool expectedParsed, Architecture expectedArchitecture)
    {
        Assert.Equal(expectedParsed, WindowsAppRuntimeInventory.TryParsePackageName(packageName, out var installation));
        Assert.Equal(expectedArchitecture, installation.Architecture);
        Assert.Equal(new Version(packageName.Contains("1.7") ? 1 : 1, packageName.Contains("1.7") ? 7 : 8), installation.Version);
        Assert.NotNull(installation.PackageVersion);
    }

    [Theory]
    [InlineData("MicrosoftCorporationII.WindowsAppRuntime.Main.1.8_8000.921.1539.0_x64__8wekyb3d8bbwe", RuntimeComponent.Main)]
    [InlineData("Microsoft.WindowsAppRuntime.Singleton_8000.921.1539.0_x64__8wekyb3d8bbwe", RuntimeComponent.Singleton)]
    [InlineData("Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8000.921.1539.0_x64__8wekyb3d8bbwe", RuntimeComponent.Ddlm)]
    public void Runtime_component_package_names_are_recognized(string packageName, RuntimeComponent component)
    {
        Assert.True(WindowsAppRuntimeInventory.TryParsePackageName(packageName, out var installation));
        Assert.Equal(component, installation.Component);
        Assert.Equal(new Version(8000, 921, 1539, 0), installation.PackageVersion);
    }

    [Fact]
    public void Parsed_18_x64_package_is_accepted_and_17_is_mismatch()
    {
        var current = WindowsAppRuntimeInventory.TryParsePackageName("Microsoft.WindowsAppRuntime.1.8_8000.921.1539.0_x64__8wekyb3d8bbwe", out var match);
        var old = WindowsAppRuntimeInventory.TryParsePackageName("Microsoft.WindowsAppRuntime.1.7_7000.100.0.0_x64__8wekyb3d8bbwe", out var mismatch);
        Assert.True(current && old);
        Assert.False(Service(new(new(10, 0, 19045), Architecture.X64), match).Inspect().RuntimePresent);
        Assert.True(Service(new(new(10, 0, 19045), Architecture.X64), mismatch).Inspect().RuntimeVersionMismatch);
        Assert.False(Service(new(new(10, 0, 19045), Architecture.X64), match with { Architecture = Architecture.X86 }).Inspect().RuntimePresent);
    }

    [Fact]
    public void Current_win10_runtime_inventory_detects_18_x64_when_integration_is_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CAS_RUN_RUNTIME_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var status = new BootstrapperService(
            new SystemOsProbe(),
            new WindowsAppRuntimeInventory(),
            new FakeInstaller(null),
            new FakeLauncher()).Inspect();
        Assert.True(status.SupportedOs);
        Assert.Contains(status.Installations, item =>
            item.Version.Major == 1
            && item.Version.Minor >= 8
            && item.Architecture == Architecture.X64
            && item.Component == RuntimeComponent.Framework);
        Assert.False(status.RuntimePresent);
        Assert.Contains("incomplete", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_requires_confirmation_and_never_starts_implicitly()
    {
        var launcher = new FakeLauncher(); var service = new BootstrapperService(new FakeOs(), new FakeInventory(), new FakeInstaller("bundle.exe"), launcher);
        Assert.False(service.InstallAfterConfirmation(_ => false, out _)); Assert.Empty(launcher.Started);
        Assert.True(service.InstallAfterConfirmation(_ => true, out _)); Assert.Single(launcher.Started);
    }

    private static BootstrapperService Service(OsSnapshot os, params RuntimeInstallation[]? runtime) => new(
        new FakeOs(os),
        new FakeInventory(runtime ?? []),
        new FakeInstaller(null),
        new FakeLauncher());
    private static RuntimeInstallation[] CompleteRuntime()
    {
        var packageVersion = new Version(8000, 921, 1539, 0);
        return
        [
            new(new(1, 8), Architecture.X64, "framework", packageVersion, RuntimeComponent.Framework),
            new(new(1, 8), Architecture.X64, "main", packageVersion, RuntimeComponent.Main),
            new(new(0, 0), Architecture.X64, "singleton", packageVersion, RuntimeComponent.Singleton),
            new(new(0, 0), Architecture.X64, "ddlm", packageVersion, RuntimeComponent.Ddlm),
        ];
    }
    private sealed class FakeOs(OsSnapshot? value = null) : IOsProbe { public OsSnapshot Read() => value ?? new(new(10, 0, 19045), Architecture.X64); }
    private sealed class FakeInventory(params RuntimeInstallation[] entries) : IRuntimeInventory { public IReadOnlyList<RuntimeInstallation> Find() => entries; }
    private sealed class FakeInstaller(string? path) : IInstallerLocator { public string? Find() => path; }
    private sealed class FakeLauncher : IProcessLauncher { public List<string> Started { get; } = []; public int Launch(string fileName, string? arguments = null) { Started.Add(fileName); return 1; } }
}
