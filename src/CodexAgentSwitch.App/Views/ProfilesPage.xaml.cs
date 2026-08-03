using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ProfilesPage : Page
{
    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync();
        if (profile is null)
        {
            CurrentProfileBar.Severity = InfoBarSeverity.Warning;
            CurrentProfileBar.Title = "没有默认配置方案";
            CurrentProfileBar.Message = "请创建或导入方案。";
            return;
        }

        CurrentProfileNameText.Text = profile.Name;
        CurrentProfileSummaryText.Text = $"{profile.MainAgent.ModelId} {profile.MainAgent.ReasoningEffort} · {profile.WorkerPolicy.Source} · 最多 {profile.WorkerPolicy.MaxWorkers} 个 Worker";
        CurrentProfileBar.Severity = InfoBarSeverity.Success;
        CurrentProfileBar.Title = $"当前方案：{profile.Name}";
        CurrentProfileBar.Message = $"{CurrentProfileSummaryText.Text} · 每日预算 {profile.Budget.Daily?.ToString("0.##") ?? "未设置"} {profile.Budget.Currency}";
    }

    private async void ExportCurrentProfile(object sender, RoutedEventArgs e)
    {
        var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync();
        if (profile is null)
        {
            return;
        }

        var export = App.Services.GetRequiredService<ProfileService>().Export(profile);
        var path = Path.Combine(AppContext.BaseDirectory, "data", $"profile-{profile.Id:D}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, export);
        CurrentProfileBar.Severity = InfoBarSeverity.Success;
        CurrentProfileBar.Title = "配置方案已导出";
        CurrentProfileBar.Message = $"{path}；导出内容不包含 API Key。";
    }

    private async void EnableSingleAgentMode(object sender, RoutedEventArgs e)
    {
        var repository = App.Services.GetRequiredService<IProfileRepository>();
        var current = await repository.GetDefaultAsync();
        if (current is null)
        {
            return;
        }

        var updated = current with
        {
            Name = "单人模式",
            WorkerPolicy = current.WorkerPolicy with { Enabled = false, Source = WorkerSource.Disabled, MaxWorkers = 0 },
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await repository.UpsertAsync(updated);
        await RefreshAsync();
    }
}
