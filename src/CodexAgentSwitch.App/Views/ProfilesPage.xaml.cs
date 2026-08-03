using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ProfilesPage : Page, IContentActionHandler
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
        var path = Path.Combine(App.Services.GetRequiredService<AppDataPaths>().Root, "exports", $"profile-{profile.Id:D}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, export);
        CurrentProfileBar.Severity = InfoBarSeverity.Success;
        CurrentProfileBar.Title = "配置方案已导出";
        CurrentProfileBar.Message = $"{path}；导出内容不包含 API Key。";
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        var repository = App.Services.GetRequiredService<IProfileRepository>();
        var current = await repository.GetDefaultAsync();
        if (current is null)
        {
            CurrentProfileBar.Severity = InfoBarSeverity.Warning;
            CurrentProfileBar.Title = "没有可操作的默认方案";
            CurrentProfileBar.Message = "请先恢复默认方案或导入方案。";
            return;
        }

        var refresh = false;
        switch (action)
        {
            case "profile:new":
                await SaveNewAsync(current with { Name = "自定义方案" }, makeDefault: true);
                refresh = true;
                break;
            case "profile:copy-current":
                await SaveNewAsync(current with { Name = current.Name + " - 副本" }, makeDefault: false);
                break;
            case "profile:balanced":
                await ApplyModeAsync(current, "平衡模式", "high", true, WorkerSource.NativeCodex, 2, RoutingMode.Balanced);
                refresh = true;
                break;
            case "profile:copy-balanced":
                await SaveNewAsync(CreateMode(current, "平衡模式 - 副本", "high", true, WorkerSource.NativeCodex, 2, RoutingMode.Balanced), makeDefault: false);
                break;
            case "profile:performance":
                await ApplyModeAsync(current, "性能模式", "xhigh", true, WorkerSource.NativeCodex, 3, RoutingMode.Performance);
                refresh = true;
                break;
            case "profile:single":
                await ApplyModeAsync(current, "单人模式", "high", false, WorkerSource.Disabled, 0, RoutingMode.Manual);
                refresh = true;
                break;
            case "profile:import":
                refresh = await ImportAsync();
                break;
            case "profile:more":
                CurrentProfileBar.Severity = InfoBarSeverity.Informational;
                CurrentProfileBar.Title = "平衡模式详情";
                CurrentProfileBar.Message = "最多两个原生 Worker；超预算时回退单代理，不会把 API Key 写入方案。";
                break;
        }

        if (refresh)
        {
            await RefreshAsync();
        }
    }

    private async Task ApplyModeAsync(Profile current, string name, string effort, bool enabled, WorkerSource source, int maxWorkers, RoutingMode routingMode)
    {
        var updated = CreateMode(current, name, effort, enabled, source, maxWorkers, routingMode) with
        {
            IsDefault = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await App.Services.GetRequiredService<ProfileService>().SaveAsync(updated);
    }

    private static Profile CreateMode(Profile current, string name, string effort, bool enabled, WorkerSource source, int maxWorkers, RoutingMode routingMode) => current with
    {
        Name = name,
        MainAgent = current.MainAgent with { ReasoningEffort = effort },
        WorkerPolicy = current.WorkerPolicy with
        {
            Enabled = enabled,
            Source = source,
            PreferredProviderId = source == WorkerSource.Disabled ? null : "native-luna",
            MaxWorkers = maxWorkers,
            RoutingMode = routingMode,
            FallbackAction = FallbackAction.SingleAgent,
        },
    };

    private async Task SaveNewAsync(Profile template, bool makeDefault)
    {
        var now = DateTimeOffset.UtcNow;
        var created = template with
        {
            Id = Guid.NewGuid(),
            IsDefault = makeDefault,
            CreatedAt = now,
            UpdatedAt = now,
            LastUsedAt = null,
        };
        await App.Services.GetRequiredService<ProfileService>().SaveAsync(created);
        CurrentProfileBar.Severity = InfoBarSeverity.Success;
        CurrentProfileBar.Title = makeDefault ? "新方案已创建并启用" : "方案副本已创建";
        CurrentProfileBar.Message = created.Name;
    }

    private async Task<bool> ImportAsync()
    {
        var paths = App.Services.GetRequiredService<AppDataPaths>();
        var source = Path.Combine(paths.Root, "imports", "profile.json");
        if (!File.Exists(source))
        {
            CurrentProfileBar.Severity = InfoBarSeverity.Warning;
            CurrentProfileBar.Title = "等待导入文件";
            CurrentProfileBar.Message = $"请将配置方案放到 {source} 后再次点击导入。";
            return false;
        }

        var service = App.Services.GetRequiredService<ProfileService>();
        var imported = service.Import(await File.ReadAllTextAsync(source)) with { IsDefault = true };
        await service.SaveAsync(imported);
        CurrentProfileBar.Severity = InfoBarSeverity.Success;
        CurrentProfileBar.Title = "配置方案已导入并启用";
        CurrentProfileBar.Message = imported.Name;
        return true;
    }
}
