using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace CodexAgentSwitch.App.Views;

public sealed partial class NativeProjectAdapterPage : Page
{
    private readonly ProjectService projects = App.Services.GetRequiredService<ProjectService>();
    private readonly IProfileRepository profiles = App.Services.GetRequiredService<IProfileRepository>();
    private readonly IProviderRepository providerRepository = App.Services.GetRequiredService<IProviderRepository>();
    private readonly ICodexDesktopLauncher desktopLauncher = App.Services.GetRequiredService<ICodexDesktopLauncher>();
    private readonly INativeCodexLauncher nativeCliLauncher = App.Services.GetRequiredService<INativeCodexLauncher>();
    private readonly List<NativeProjectItem> allItems = [];
    private Profile? activeProfile;
    private string activeWorkerText = "未启用";
    private string? newProjectParent;
    private string launchMode = "desktop";

    public ObservableCollection<NativeProjectItem> ProjectItems { get; } = [];
    public ObservableCollection<string> ResultItems { get; } = [];

    public NativeProjectAdapterPage()
    {
        InitializeComponent();
        ProjectListView.ItemsSource = ProjectItems;
        ResultItemsControl.ItemsSource = ResultItems;
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        launchMode = string.Equals(e.Parameter as string, "cli", StringComparison.OrdinalIgnoreCase) ? "cli" : "desktop";
        Header.Title = launchMode == "desktop" ? "将当前方案应用到原生 Codex 项目" : "将当前方案应用到 Codex CLI 项目";
        Header.Subtitle = launchMode == "desktop"
            ? "选择真实项目目录。仅勾选的项目会写入受 Agent Switch 管理的配置块；随后启动官方 Codex 桌面应用。"
            : "选择真实项目目录。仅勾选的项目会写入受 Agent Switch 管理的配置块；随后从第一个成功项目启动 Codex CLI。";
        ApplyButton.Content = launchMode == "desktop" ? "应用配置并启动 Codex" : "应用配置并启动 CLI";
        base.OnNavigatedTo(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            activeProfile = await profiles.GetDefaultAsync() ?? throw new InvalidOperationException("尚未设置当前配置方案。");
            activeWorkerText = await ResolveWorkerTextAsync(activeProfile);
            UpdateProfileSummary(activeProfile, activeWorkerText);
            if (activeProfile.WorkerPolicy.Enabled && activeProfile.WorkerPolicy.Source == WorkerSource.ExternalProvider)
            {
                ShowInfo(
                    "将配置原生 DeepSeek Worker",
                    "Provider 定义会安全写入用户级 CODEX_HOME；项目只写入 Worker 角色引用。API Key 继续保留在 Windows 凭据管理器，不写入项目。",
                    InfoBarSeverity.Informational);
            }
            await RefreshProjectsAsync();
        }
        catch (Exception exception)
        {
            ShowError("无法加载适配项目", exception.Message);
        }
    }

    private async Task RefreshProjectsAsync(IReadOnlySet<string>? selectedIds = null)
    {
        if (activeProfile is null)
        {
            return;
        }

        allItems.Clear();
        foreach (var project in await projects.ListAsync())
        {
            var item = NativeProjectItem.Create(project, activeProfile, activeWorkerText);
            item.IsSelected = selectedIds?.Contains(project.Id) == true;
            item.PropertyChanged += OnProjectItemPropertyChanged;
            allItems.Add(item);
        }

        ApplyProjectFilter();
    }

    private void ApplyProjectFilter()
    {
        var query = ProjectSearchBox.Text.Trim();
        var showArchived = ShowArchivedCheckBox.IsChecked == true;
        var visible = allItems.Where(item =>
                (showArchived || !item.IsArchived)
                && (query.Length == 0
                    || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || item.WorkingDirectory.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .ToArray();

        ProjectItems.Clear();
        foreach (var item in visible)
        {
            ProjectItems.Add(item);
        }

        EmptyProjectsText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionSummary();
    }

    private void OnProjectSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyProjectFilter();

    private void OnArchivedFilterChanged(object sender, RoutedEventArgs args) => ApplyProjectFilter();

    private void OnProjectItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is NativeProjectItem item)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private void OnProjectItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NativeProjectItem.IsSelected))
        {
            UpdateSelectionSummary();
        }
    }

    private void UpdateProfileSummary(Profile profile, string workerText)
    {
        ProfileNameText.Text = profile.Name;
        ProfileMainAgentText.Text = $"主模型：{profile.MainAgent.ModelId} · 推理强度：{profile.MainAgent.ReasoningEffort}";
        ProfileWorkerText.Text = profile.WorkerPolicy.Enabled
            ? $"Worker：{workerText} · 最大 {profile.WorkerPolicy.MaxWorkers} 个"
            : "Worker：未启用";
        ProfileRoutingText.Text = $"路由：{RoutingText(profile.WorkerPolicy.RoutingMode)} · 回退：{FallbackText(profile.WorkerPolicy.FallbackAction)}";
        ProfileVersionText.Text = $"应用版本：{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.10"}";
    }

    private void UpdateSelectionSummary()
    {
        var selected = allItems.Where(item => item.IsSelected).ToArray();
        SelectedCountText.Text = $"{selected.Length} 个项目";
        SelectedPathsText.Text = selected.Length == 0
            ? "尚未选择项目。"
            : string.Join(Environment.NewLine, selected.Select(item => item.WorkingDirectory));
        FooterSelectionText.Text = selected.Length == 0 ? "请选择要适配的项目" : $"已选择 {selected.Length} 个项目";
        ApplyButton.IsEnabled = selected.Length > 0 && activeProfile is not null;
    }

    private async void AddExistingFolderAsync(object sender, RoutedEventArgs args)
    {
        try
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return;
            }

            EnsureProjectDirectoryIsNotOnCDrive(folder.Path);
            await projects.CreateAsync(folder.Name, folder.Path, activeProfile?.Id);
            await RefreshProjectsAsync();
            ShowInfo("项目已加入列表", $"已添加 {folder.Path}；尚未选中，也没有写入配置。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowError("无法添加文件夹", exception.Message);
        }
    }

    private void ToggleNewProjectPanel(object sender, RoutedEventArgs args)
    {
        NewProjectPanel.Visibility = NewProjectPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (NewProjectPanel.Visibility == Visibility.Visible)
        {
            NewProjectNameBox.Focus(FocusState.Programmatic);
        }
    }

    private async void ChooseNewProjectParentAsync(object sender, RoutedEventArgs args)
    {
        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        newProjectParent = folder.Path;
        NewProjectParentText.Text = folder.Path;
        UpdateNewProjectPathPreview();
    }

    private void OnNewProjectNameChanged(object sender, TextChangedEventArgs args) => UpdateNewProjectPathPreview();

    private void UpdateNewProjectPathPreview()
    {
        var name = NewProjectNameBox.Text.Trim();
        NewProjectPathText.Text = string.IsNullOrWhiteSpace(newProjectParent) || string.IsNullOrWhiteSpace(name)
            ? "选择父目录并输入项目名称后，将显示最终路径。"
            : $"将创建：{Path.Combine(newProjectParent, name)}";
    }

    private async void CreateProjectAsync(object sender, RoutedEventArgs args)
    {
        try
        {
            var name = NewProjectNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("请输入项目名称。");
            }

            if (string.IsNullOrWhiteSpace(newProjectParent))
            {
                throw new InvalidOperationException("请选择新项目的父目录。");
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("项目名称包含无效字符。");
            }

            var directory = Path.Combine(newProjectParent, name);
            EnsureProjectDirectoryIsNotOnCDrive(directory);
            if (Directory.Exists(directory))
            {
                throw new InvalidOperationException("最终目录已经存在；请添加现有文件夹或使用其他项目名称。");
            }

            Directory.CreateDirectory(directory);
            var created = await projects.CreateAsync(name, directory, activeProfile?.Id);
            NewProjectNameBox.Text = string.Empty;
            newProjectParent = null;
            NewProjectParentText.Text = "请选择父目录";
            NewProjectPanel.Visibility = Visibility.Collapsed;
            await RefreshProjectsAsync(new HashSet<string>(StringComparer.Ordinal) { created.Id });
            ShowInfo("项目已创建", $"{created.Name} 已加入列表并被选中，尚未写入配置。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowError("无法创建项目", exception.Message);
        }
    }

    private async void ConfirmApplyAsync(object sender, RoutedEventArgs args)
    {
        var profile = activeProfile ?? throw new InvalidOperationException("当前方案未加载。");
        var selected = allItems.Where(item => item.IsSelected).Select(item => item.Project).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var changes = string.Join(Environment.NewLine, selected.Select(project => $"• {Path.Combine(project.WorkingDirectory, ".codex", "config.toml")}"));
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认应用当前方案",
            Content = new TextBlock
            {
                Text = $"当前方案：{profile.Name}{Environment.NewLine}已选项目：{selected.Length}{Environment.NewLine}{Environment.NewLine}将修改：{Environment.NewLine}{changes}",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
            },
            PrimaryButtonText = launchMode == "desktop" ? "确认应用并启动" : "确认应用并启动 CLI",
            CloseButtonText = "返回修改",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ApplyButton.IsEnabled = false;
        try
        {
            IReadOnlyList<NativeProjectAdaptationResult> projectResults;
            var started = false;
            string? launchError = null;
            if (launchMode == "desktop")
            {
                var result = await desktopLauncher.ApplyToProjectsAndLaunchAsync(profile, selected);
                projectResults = result.Projects;
                started = result.DesktopStarted;
                launchError = result.LaunchError;
            }
            else
            {
                projectResults = await desktopLauncher.ApplyToProjectsAsync(profile, selected);
                var firstSuccessful = projectResults.FirstOrDefault(item => item.Succeeded);
                if (firstSuccessful is not null)
                {
                    try
                    {
                        await nativeCliLauncher.LaunchAsync(profile, firstSuccessful.Project.WorkingDirectory);
                        started = true;
                    }
                    catch (Exception exception)
                    {
                        launchError = exception.Message;
                    }
                }
                else
                {
                    launchError = "没有项目成功适配，因此未启动 Codex CLI。";
                }
            }
            var selectedIds = selected.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in projectResults.Where(item => item.Succeeded))
            {
                var adaptation = new NativeCodexProjectAdaptation(
                    profile.Id,
                    profile.Name,
                    item.ConfigurationPath,
                    item.BackupPath,
                    DateTimeOffset.UtcNow,
                    ProfileSummary(profile),
                    item.BackupPath is not null);
                await projects.RecordNativeCodexAdaptationAsync(item.Project.Id, adaptation);
            }

            ResultItems.Clear();
            foreach (var item in projectResults)
            {
                ResultItems.Add(item.Succeeded
                    ? $"{item.Project.Name}：{(item.Changed ? "成功写入" : "无需更新")} · {item.ConfigurationPath}"
                    : $"{item.Project.Name}：失败 · {item.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(launchError))
            {
                ResultItems.Add($"{(launchMode == "desktop" ? "Codex 桌面应用" : "Codex CLI")}未启动：{launchError}");
            }

            ResultSummaryText.Text = $"成功应用：{projectResults.Count(item => item.Succeeded)} · 失败：{projectResults.Count(item => !item.Succeeded)} · {(started ? (launchMode == "desktop" ? "已启动 Codex 桌面应用" : "已启动 Codex CLI") : "未启动")}";
            ResultPanel.Visibility = Visibility.Visible;
            await RefreshProjectsAsync(selectedIds);
            ShowInfo("适配处理完成", ResultSummaryText.Text, projectResults.All(item => item.Succeeded) && started ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowError("未能应用项目配置", exception.Message);
        }
        finally
        {
            UpdateSelectionSummary();
        }
    }

    private void OpenProjectMoreMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: NativeProjectItem item } anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(MenuItem("打开目录", () => OpenDirectoryAsync(item)));
        flyout.Items.Add(MenuItem("查看配置", () => ViewConfigurationAsync(item)));
        flyout.Items.Add(MenuItem("恢复原配置", () => RestoreConfigurationAsync(item)));
        flyout.Items.Add(MenuItem("从列表移除", () => ArchiveProjectAsync(item), destructive: true));
        flyout.ShowAt(anchor);
    }

    private static Task OpenDirectoryAsync(NativeProjectItem item)
    {
        Process.Start(new ProcessStartInfo { FileName = item.WorkingDirectory, UseShellExecute = true });
        return Task.CompletedTask;
    }

    private Task ViewConfigurationAsync(NativeProjectItem item)
    {
        var path = Path.Combine(item.WorkingDirectory, ".codex", "config.toml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("该项目尚未找到 .codex/config.toml。", path);
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task RestoreConfigurationAsync(NativeProjectItem item)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "恢复原始项目配置",
            Content = $"将恢复“{item.Name}”写入前的 config.toml；这不会删除项目文件。",
            PrimaryButtonText = "恢复配置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await desktopLauncher.RestoreProjectConfigurationAsync(item.Project);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? result.Summary);
        }

        await projects.ClearNativeCodexAdaptationAsync(item.Project.Id);
        await RefreshProjectsAsync(allItems.Where(project => project.IsSelected).Select(project => project.Id).ToHashSet(StringComparer.Ordinal));
        ShowInfo("已恢复原配置", result.ConfigurationPath, InfoBarSeverity.Success);
    }

    private async Task ArchiveProjectAsync(NativeProjectItem item)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从适配列表移除",
            Content = $"将归档“{item.Name}”。不会删除磁盘目录或已有对话，可在“显示已归档项目”中恢复。",
            PrimaryButtonText = "归档项目",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await projects.ArchiveAsync(item.Project.Id);
        await RefreshProjectsAsync();
    }

    private MenuFlyoutItem MenuItem(string text, Func<Task> action, bool destructive = false)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (destructive)
        {
            item.Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception exception) { ShowError("操作失败", exception.Message); }
        };
        return item;
    }

    private async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var window = App.MainWindow ?? throw new InvalidOperationException("主窗口尚未就绪。");
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return await picker.PickSingleFolderAsync();
    }

    private void BackToDashboard(object sender, RoutedEventArgs args) => Frame.Navigate(typeof(DashboardPage));

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        ActionBar.Title = title;
        ActionBar.Message = message;
        ActionBar.Severity = severity;
        ActionBar.IsOpen = true;
    }

    private void ShowError(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Error);

    private static string ProfileSummary(Profile profile) =>
        $"{profile.MainAgent.ModelId} / {profile.MainAgent.ReasoningEffort} · {WorkerSourceText(profile.WorkerPolicy.Source)} · {RoutingText(profile.WorkerPolicy.RoutingMode)}";

    private static string WorkerSourceText(WorkerSource source) => source == WorkerSource.ExternalProvider ? "外部服务商" : "原生 Worker";

    private async Task<string> ResolveWorkerTextAsync(Profile profile)
    {
        if (!profile.WorkerPolicy.Enabled)
        {
            return "未启用";
        }

        if (profile.WorkerPolicy.Source != WorkerSource.ExternalProvider)
        {
            return WorkerSourceText(profile.WorkerPolicy.Source);
        }

        var providerId = profile.WorkerPolicy.PreferredProviderId;
        var provider = string.IsNullOrWhiteSpace(providerId)
            ? null
            : await providerRepository.GetAsync(providerId);
        return provider is null
            ? "外部 Provider（尚未选择）"
            : $"{provider.Name} · {provider.ModelId ?? "未选择模型"}";
    }

    private static string RoutingText(RoutingMode mode) => mode switch
    {
        RoutingMode.Economic => "经济",
        RoutingMode.Performance => "性能",
        RoutingMode.Single => "单人",
        RoutingMode.Manual => "手动",
        _ => "平衡",
    };

    private static string FallbackText(FallbackAction action) => action switch
    {
        FallbackAction.StopDelegation => "停止委派",
        FallbackAction.SingleAgent => "主代理接管",
        FallbackAction.AskUser => "询问用户",
        _ => "原生 Luna",
    };

    private static void EnsureProjectDirectoryIsNotOnCDrive(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (string.Equals(Path.GetPathRoot(fullPath), "C:\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("为遵守本机存储策略，不能将 C 盘目录加入原生 Codex 适配列表。请选择 E 盘或其他非 C 盘项目目录。");
        }
    }

}

public sealed class NativeProjectItem : INotifyPropertyChanged
{
    private bool isSelected;

    private NativeProjectItem(AgentProject project, Profile profile, string workerText)
    {
        Project = project;
        Name = project.Name;
        WorkingDirectory = project.WorkingDirectory;
        IsArchived = project.IsArchived;
        PlannedApplication = $"即将应用：{profile.MainAgent.ModelId} {profile.MainAgent.ReasoningEffort} + {workerText}";
        var adaptation = project.NativeCodexAdaptation;
        var detectedConfiguration = Path.Combine(project.WorkingDirectory, ".codex", "config.toml");
        var originalConfigurationExists = adaptation?.OriginalConfigurationExisted ?? File.Exists(detectedConfiguration);
        ConfigurationPathText = $"配置：{adaptation?.ConfigurationPath ?? detectedConfiguration}";
        NativeState = adaptation is null
            ? originalConfigurationExists ? "当前状态：未适配 · 原始配置：有" : "当前状态：未适配 · 原始配置：无"
            : adaptation.ProfileId == profile.Id
                ? $"当前状态：已适配当前方案“{adaptation.ProfileName}” · 原始配置：{(originalConfigurationExists ? "有" : "无")}" 
                : $"当前状态：已适配其他方案“{adaptation.ProfileName}” · 原始配置：{(originalConfigurationExists ? "有" : "无")}";
        AppliedAtText = adaptation is null ? "上次应用：无" : $"上次应用：{adaptation.AppliedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AgentProject Project { get; }
    public string Id => Project.Id;
    public string Name { get; }
    public string WorkingDirectory { get; }
    public bool IsArchived { get; }
    public string NativeState { get; }
    public string ConfigurationPathText { get; }
    public string PlannedApplication { get; }
    public string AppliedAtText { get; }
    public Brush CardBackground => isSelected ? new SolidColorBrush(Windows.UI.Color.FromArgb(20, 0, 120, 212)) : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    public Brush CardBorder => isSelected ? new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0, 120, 212)) : new SolidColorBrush(Windows.UI.Color.FromArgb(30, 127, 127, 127));

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBackground));
            OnPropertyChanged(nameof(CardBorder));
        }
    }

    public static NativeProjectItem Create(AgentProject project, Profile profile, string workerText) => new(project, profile, workerText);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
