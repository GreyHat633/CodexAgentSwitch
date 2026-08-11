using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexAgentSwitch.App.ViewModels;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ProfilesPage : Page, IContentActionHandler, INotifyPropertyChanged
{
    private ProfileListItemViewModel? _selectedProfile;
    private ProfileEditorViewModel? _editor;

    public ProfilesPage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; } = [];

    public ProfileListItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Equals(_selectedProfile, value))
            {
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
            UpdateCurrentSummary(value);
        }
    }

    public ProfileEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            _editor = value;
            OnPropertyChanged();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ShowError("加载配置方案失败", exception.Message);
        }
    }

    private async Task RefreshAsync(Guid? preferredSelectionId = null)
    {
        var repository = App.Services.GetRequiredService<IProfileRepository>();
        var selectedId = preferredSelectionId ?? SelectedProfile?.Id;
        var profiles = await repository.ListAsync();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new ProfileListItemViewModel(profile));
        }

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? Profiles.FirstOrDefault(profile => profile.IsDefault)
            ?? Profiles.FirstOrDefault();

        if (SelectedProfile is null)
        {
            CurrentProfileBar.Severity = InfoBarSeverity.Warning;
            CurrentProfileBar.Title = "没有配置方案";
            CurrentProfileBar.Message = "请创建或导入方案。";
        }
        else
        {
            CurrentProfileBar.Severity = SelectedProfile.RequiresRepair ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            CurrentProfileBar.Title = SelectedProfile.RequiresRepair ? "发现需要修复的方案" : $"当前方案：{SelectedProfile.Name}";
            CurrentProfileBar.Message = SelectedProfile.RequiresRepair
                ? SelectedProfile.RepairMessage
                : "方案已从本地数据库加载。";
        }
    }

    private void UpdateCurrentSummary(ProfileListItemViewModel? item)
    {
        var profile = item?.Value;
        CurrentProfileNameText.Text = profile?.Name ?? "未选择";
        CurrentProfileSummaryText.Text = profile is null
            ? string.Empty
            : profile.RequiresRepair
                ? profile.RepairMessage ?? "该方案需要修复。"
            : $"{profile.MainAgent.ModelId} / 推理强度{ReasoningLabel(profile.MainAgent.ReasoningEffort)} · "
              + $"{ApprovalLabel(profile.ApprovalMode)} · "
              + (profile.WorkerPolicy.Enabled
                  ? $"工作代理 {profile.WorkerPolicy.MaxWorkers} 个 · {RoutingLabel(profile.WorkerPolicy.RoutingMode)}"
                  : "未启用工作代理 · 单代理模式");
    }

    private static string ApprovalLabel(ExecutionApprovalMode mode) => mode switch
    {
        ExecutionApprovalMode.Safe => "安全模式",
        ExecutionApprovalMode.FullAuto => "完全自动",
        _ => "自动模式",
    };

    private async void SaveEditor(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (Editor is null)
            {
                args.Cancel = true;
                return;
            }

            var profile = Editor.BuildProfile(DateTimeOffset.UtcNow);
            await App.Services.GetRequiredService<ProfileService>().SaveAsync(profile);
            await RefreshAsync(profile.Id);
            CurrentProfileBar.Severity = InfoBarSeverity.Success;
            CurrentProfileBar.Title = "方案已保存";
            CurrentProfileBar.Message = profile.Name;
        }
        catch (ProfileValidationException exception)
        {
            args.Cancel = true;
            ShowError("方案校验失败", string.Join(Environment.NewLine, exception.Issues.Select(issue => issue.Message)));
        }
        catch (FormatException exception)
        {
            args.Cancel = true;
            ShowError("输入格式错误", exception.Message);
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            ShowError("保存方案失败", exception.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void EditSelectedProfile(object sender, RoutedEventArgs e)
    {
        if (EnsureOperable(SelectedProfile))
        {
            try
            {
                await ShowEditorAsync(ProfileEditorViewModel.ForEdit(SelectedProfile!.Value, await LoadExternalProviderOptionsAsync()));
            }
            catch (Exception exception)
            {
                ShowError("无法编辑方案", exception.Message);
            }
        }
    }

    private async void CopySelectedProfile(object sender, RoutedEventArgs e)
    {
        if (EnsureOperable(SelectedProfile))
        {
            try
            {
                var service = App.Services.GetRequiredService<ProfileService>();
                var uniqueName = await service.SuggestUniqueNameAsync(SelectedProfile!.Name);
                await ShowEditorAsync(ProfileEditorViewModel.ForCopy(SelectedProfile.Value, uniqueName, await LoadExternalProviderOptionsAsync()));
            }
            catch (Exception exception)
            {
                ShowError("无法复制方案", exception.Message);
            }
        }
    }

    private async void DeleteSelectedProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            await App.Services.GetRequiredService<ProfileService>().DeleteAsync(SelectedProfile.Id);
            await RefreshAsync();
            CurrentProfileBar.Severity = InfoBarSeverity.Success;
            CurrentProfileBar.Title = "方案已删除";
            CurrentProfileBar.Message = "已更新本地方案列表。";
        }
        catch (Exception exception)
        {
            ShowError("删除方案失败", exception.Message);
        }
    }

    private async void SetSelectedAsDefault(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (!EnsureOperable(selected))
        {
            return;
        }

        try
        {
            await App.Services.GetRequiredService<ProfileService>().SetDefaultAsync(selected!.Id);
            await RefreshAsync();
            CurrentProfileBar.Severity = InfoBarSeverity.Success;
            CurrentProfileBar.Title = "默认方案已切换";
            CurrentProfileBar.Message = SelectedProfile?.Name ?? string.Empty;
        }
        catch (Exception exception)
        {
            ShowError("切换默认方案失败", exception.Message);
        }
    }

    private async void ActivateSelectedProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (!EnsureOperable(selected))
        {
            return;
        }

        try
        {
            var activated = await App.Services.GetRequiredService<ProfileService>().ActivateAsync(selected!.Id);
            await RefreshAsync();
            CurrentProfileBar.Severity = InfoBarSeverity.Success;
            CurrentProfileBar.Title = "方案已立即启用";
            CurrentProfileBar.Message = $"{activated.Name} · 最后使用时间 {activated.LastUsedAt:O}";
        }
        catch (Exception exception)
        {
            ShowError("立即启用方案失败", exception.Message);
        }
    }

    private async void ExportSelectedProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (!EnsureOperable(selected))
        {
            return;
        }

        try
        {
            var paths = App.Services.GetRequiredService<AppDataPaths>();
            var path = Path.Combine(paths.Root, "exports", $"profile-{selected!.Id:D}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var export = App.Services.GetRequiredService<ProfileService>().Export(selected.Value);
            await File.WriteAllTextAsync(path, export);
            CurrentProfileBar.Severity = InfoBarSeverity.Success;
            CurrentProfileBar.Title = "方案已导出";
            CurrentProfileBar.Message = $"{path}（不包含 API 密钥）";
        }
        catch (Exception exception)
        {
            ShowError("导出方案失败", exception.Message);
        }
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        var service = App.Services.GetRequiredService<ProfileService>();
        var current = SelectedProfile?.Value ?? (await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync());
        switch (action)
        {
            case "profile:new":
                await ShowEditorAsync(ProfileEditorViewModel.ForNew(
                    current ?? Profile.CreateDefault(DateTimeOffset.UtcNow),
                    await LoadExternalProviderOptionsAsync()));
                break;
            case "profile:copy-current":
                if (current is not null)
                {
                    var uniqueName = await service.SuggestUniqueNameAsync(current.Name);
                    await ShowEditorAsync(ProfileEditorViewModel.ForCopy(current, uniqueName, await LoadExternalProviderOptionsAsync()));
                }

                break;
            case "profile:import":
                await ImportAsync();
                break;
            case "profile:balanced":
                if (current is not null) await ApplyModeAsync(service, current, "平衡模式", "high", true, WorkerSource.NativeCodex, 2, RoutingMode.Balanced);
                break;
            case "profile:performance":
                if (current is not null) await ApplyModeAsync(service, current, "性能模式", "xhigh", true, WorkerSource.NativeCodex, 3, RoutingMode.Performance);
                break;
            case "profile:single":
                if (current is not null) await ApplyModeAsync(service, current, "单人模式", "high", false, WorkerSource.Disabled, 0, RoutingMode.Single);
                break;
        }

        await RefreshAsync();
    }

    private async Task ShowEditorAsync(ProfileEditorViewModel editor)
    {
        try
        {
            editor.SetAvailableNativeRoles(await LoadAvailableNativeRolesAsync());
        }
        catch (Exception exception)
        {
            ShowError("无法读取 Codex 模型目录", "将保留全部角色选项；保存和启动时仍会重新校验。" + Environment.NewLine + exception.Message);
        }

        Editor = editor;
        await ProfileEditorDialog.ShowAsync();
        Editor = null;
    }

    private async Task<IReadOnlyList<string>> LoadAvailableNativeRolesAsync()
    {
        var runtime = App.Services.GetRequiredService<IControlledTaskRuntime>();
        await runtime.EnsureStartedAsync();
        var capabilities = await runtime.NativeWorker.GetCapabilitiesAsync();
        return capabilities.Models
            .Select(model => model.Id switch
            {
                "gpt-5.6-sol" => "Sol",
                "gpt-5.6-terra" => "Terra",
                "gpt-5.6-luna" => "Luna",
                _ => null,
            })
            .Where(role => role is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<ProviderSelectionOption>> LoadExternalProviderOptionsAsync()
    {
        var providers = await App.Services.GetRequiredService<IProviderRegistry>().LoadAsync();
        return providers.Providers
            .Where(entry => entry.Provider.Kind != ProviderKind.NativeCodex
                && (entry.Provider.IsEnabled || entry.Provider.Kind == ProviderKind.OpenCodeZen))
            .OrderBy(entry => entry.Provider.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new ProviderSelectionOption(entry.Id, $"{entry.Name}（{entry.Id}） · {entry.Status}"))
            .ToArray();
    }

    private static string ReasoningLabel(string effort) => effort switch
    {
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        _ => effort,
    };

    private static string RoutingLabel(RoutingMode mode) => mode switch
    {
        RoutingMode.Economic => "经济优先",
        RoutingMode.Balanced => "平衡模式",
        RoutingMode.Performance => "性能优先",
        RoutingMode.Manual => "手动模式",
        RoutingMode.Single => "单代理模式",
        _ => mode.ToString(),
    };

    private async Task ApplyModeAsync(ProfileService service, Profile current, string name, string effort, bool enabled, WorkerSource source, int maxWorkers, RoutingMode routingMode)
    {
        var updated = current with
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
        await service.SaveAsync(updated);
    }

    private async Task ImportAsync()
    {
        var paths = App.Services.GetRequiredService<AppDataPaths>();
        var source = Path.Combine(paths.Root, "imports", "profile.json");
        if (!File.Exists(source))
        {
            CurrentProfileBar.Severity = InfoBarSeverity.Warning;
            CurrentProfileBar.Title = "等待导入文件";
            CurrentProfileBar.Message = $"请将方案放入 {source} 后再次点击导入。";
            return;
        }

        try
        {
            var service = App.Services.GetRequiredService<ProfileService>();
            var imported = service.Import(await File.ReadAllTextAsync(source)) with { IsDefault = false };
            await service.SaveAsync(imported);
            await RefreshAsync();
            CurrentProfileBar.Severity = InfoBarSeverity.Success;
            CurrentProfileBar.Title = "方案已导入";
            CurrentProfileBar.Message = imported.Name;
        }
        catch (Exception exception)
        {
            ShowError("导入方案失败", exception.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        CurrentProfileBar.Severity = InfoBarSeverity.Error;
        CurrentProfileBar.Title = title;
        CurrentProfileBar.Message = message;
    }

    private bool EnsureOperable(ProfileListItemViewModel? profile)
    {
        if (profile is null)
        {
            return false;
        }

        if (!profile.RequiresRepair)
        {
            return true;
        }

        ShowError("该方案需要修复", profile.RepairMessage);
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
