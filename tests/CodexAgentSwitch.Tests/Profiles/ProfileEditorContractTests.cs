namespace CodexAgentSwitch.Tests.Profiles;

public sealed class ProfileEditorContractTests
{
    [Fact]
    public void Profile_editor_exposes_exact_compaction_options_and_hides_routing_selector()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "CodexAgentSwitch.App", "ViewModels", "ProfileEditorViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "CodexAgentSwitch.App", "Views", "ProfilesPage.xaml"));

        Assert.Equal(1, Count(viewModel, "\"节省 · 150K\""));
        Assert.Equal(1, Count(viewModel, "\"均衡 · 180K\""));
        Assert.Equal(1, Count(viewModel, "\"连续 · 200K\""));
        Assert.Equal(1, Count(viewModel, "\"默认 · 约218K\""));
        Assert.Contains("AutoCompactTokenLimit = this.AutoCompactTokenLimit", viewModel, StringComparison.Ordinal);
        Assert.Contains("MainAgent = defaults.MainAgent", viewModel, StringComparison.Ordinal);
        Assert.Contains("WorkerPolicy = defaults.WorkerPolicy", viewModel, StringComparison.Ordinal);
        Assert.Contains("AutoCompactTokenLimit = null", viewModel, StringComparison.Ordinal);
        Assert.Contains("RoutingMode,", viewModel, StringComparison.Ordinal);

        Assert.Contains("Header=\"上下文压缩\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AutoCompactOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAutoCompactOption, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"路由模式\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"路由模式\"", xaml, StringComparison.Ordinal);

        Assert.Contains("NativeCodexRoleCatalog.All", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetAvailableNativeModels", viewModel, StringComparison.Ordinal);
        Assert.Contains("WorkerReasoningUnavailableVisibility", xaml, StringComparison.Ordinal);
        Assert.Contains("Astra / Sol / Terra / Luna", xaml, StringComparison.Ordinal);
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexAgentSwitch.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("无法定位 CodexAgentSwitch.sln。");
    }
}
