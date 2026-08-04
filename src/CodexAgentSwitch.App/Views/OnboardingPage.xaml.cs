using System.Text;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Onboarding;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexAgentSwitch.App.Views;

public sealed partial class OnboardingPage : Page, IContentActionHandler
{
    private readonly OnboardingWorkflowState workflow = new();
    private readonly Dictionary<string, IReadOnlyList<string>> reasoningEfforts = new(StringComparer.Ordinal);
    private HashSet<string> availableMainModels = new(StringComparer.Ordinal);
    private Profile? seedProfile;
    private bool environmentReady;
    private bool providerConnectionVerified;
    private string? verifiedProviderId;

    public OnboardingPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        UpdateStep();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        try
        {
            seedProfile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync()
                ?? Profile.CreateDefault(DateTimeOffset.UtcNow);
            PopulateFromProfile(seedProfile);
            await LoadProvidersAsync();
            await RefreshEnvironmentAsync();
            UpdateStep();
            UpdateAgentCardsLayout(ActualWidth);
        }
        catch (Exception exception)
        {
            ShowError("首次启动向导加载失败", exception.Message);
        }
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        try
        {
            if (action == "onboarding:back")
            {
                workflow.Back();
                UpdateStep();
                return;
            }

            if (action != "onboarding:next")
            {
                return;
            }

            if (!await ValidateCurrentStepAsync())
            {
                return;
            }

            if (workflow.CanGoNext)
            {
                workflow.Next();
                UpdateStep();
                return;
            }

            await CompleteAsync();
        }
        catch (Exception exception)
        {
            ShowError("无法继续", exception.Message);
        }
    }

    private async void RefreshEnvironment(object sender, RoutedEventArgs args)
    {
        try
        {
            await RefreshEnvironmentAsync();
            OnboardingActionBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            ShowError("环境检测失败", exception.Message);
        }
    }

    private async Task RefreshEnvironmentAsync()
    {
        var runtimeManager = App.Services.GetRequiredService<CodexRuntimeManager>();
        var runtime = await runtimeManager.DetectAsync();
        var desktop = await App.Services.GetRequiredService<CodexAgentSwitch.Application.NativeCodex.ICodexDesktopLauncher>().DetectAsync();
        var paths = App.Services.GetRequiredService<AppDataPaths>();
        paths.EnsureCreated();

        CliStatusText.Text = runtime.Installed
            ? $"Codex CLI：已检测（{runtime.Version ?? "版本未知"}）"
            : $"Codex CLI：未检测（{runtime.Message}）";
        DesktopStatusText.Text = desktop.IsAvailable
            ? $"Codex 桌面应用：已检测（{desktop.AppUserModelId ?? desktop.ExecutablePath}）"
            : $"Codex 桌面应用：未检测（{desktop.Status}）";
        AppServerStatusText.Text = runtime.AppServerRunning
            ? "Codex App Server：已连接"
            : "Codex App Server：将在 CodexAgentSwitch 模式发送任务时按需启动";
        ConfigDirectoryText.Text = $"应用配置目录：{paths.Root}";
        RuntimeStatusText.Text = $"运行环境：Windows {Environment.OSVersion.Version} · {Environment.Is64BitProcess switch { true => "x64", false => "x86" }}";
        environmentReady = runtime.Installed;

        availableMainModels.Clear();
        reasoningEfforts.Clear();
        try
        {
            var taskRuntime = App.Services.GetRequiredService<IControlledTaskRuntime>();
            await taskRuntime.EnsureStartedAsync();
            var capabilities = await taskRuntime.NativeWorker.GetCapabilitiesAsync();
            foreach (var model in capabilities.Models.Where(model => model.Id is "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna"))
            {
                availableMainModels.Add(model.Id);
                reasoningEfforts[model.Id] = model.SupportedReasoningEfforts;
            }

            MainAgentAvailabilityText.Text = "已从当前 Codex 账户读取主代理和推理强度目录。不可用模型会保留提示，不会被后台映射。";
        }
        catch (Exception exception)
        {
            MainAgentAvailabilityText.Text = $"暂时无法读取当前账户目录：{exception.Message}。请稍后重新检测；不会擅自替换所选模型。";
        }

        UpdateAgentAvailability();
    }

    private void PopulateFromProfile(Profile profile)
    {
        SetRadioForModelInstance(profile.MainAgent.ModelId);
        SetWorkerSource(profile.WorkerPolicy.Source);
        MaxWorkersBox.Value = Math.Max(1, profile.WorkerPolicy.MaxWorkers);
        SelectComboTag(RoutingModeComboBox, profile.WorkerPolicy.RoutingMode.ToString());
        SelectComboTag(FallbackComboBox, profile.WorkerPolicy.FallbackAction.ToString());
        SelectComboTag(NativeWorkerComboBox, profile.WorkerPolicy.PreferredProviderId ?? "native-luna");
        ProfileNameBox.Text = string.IsNullOrWhiteSpace(profile.Name) ? "首次启动方案" : profile.Name + "（首次配置）";
    }

    private async Task LoadProvidersAsync()
    {
        var providers = (await App.Services.GetRequiredService<IProviderRepository>().ListAsync())
            .Where(provider => provider.Kind != ProviderKind.NativeCodex)
            .OrderBy(provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!providers.Any(provider => provider.Id == "deepseek-default"))
        {
            providers.Add(ProviderConfiguration.DeepSeekPreset(DateTimeOffset.UtcNow));
        }

        ExternalProviderComboBox.ItemsSource = providers;
        ExternalProviderComboBox.DisplayMemberPath = nameof(ProviderConfiguration.Name);
        ProviderSetupComboBox.ItemsSource = providers;
        ProviderSetupComboBox.DisplayMemberPath = nameof(ProviderConfiguration.Name);
        var preferred = seedProfile?.WorkerPolicy.PreferredProviderId;
        ExternalProviderComboBox.SelectedItem = providers.FirstOrDefault(provider => provider.Id == preferred) ?? providers.FirstOrDefault();
        ProviderSetupComboBox.SelectedItem = ExternalProviderComboBox.SelectedItem;
        ApplyProviderToEditor(ProviderSetupComboBox.SelectedItem as ProviderConfiguration);
    }

    private void MainAgentChecked(object sender, RoutedEventArgs args)
    {
        if (sender is RadioButton { Tag: string modelId })
        {
            PopulateReasoningEfforts(modelId, null);
        }
    }

    private void ReasoningEffortChanged(object sender, SelectionChangedEventArgs args) => UpdateConfirmationSummary();

    private void WorkerSourceChecked(object sender, RoutedEventArgs args)
    {
        NativeWorkerPanel.Visibility = NativeWorkerRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ExternalWorkerPanel.Visibility = ExternalWorkerRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InvalidateProviderVerification();
        UpdateProviderStepVisibility();
        UpdateConfirmationSummary();
    }

    private void ExternalProviderSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ExternalProviderComboBox.SelectedItem is ProviderConfiguration selected)
        {
            ProviderSetupComboBox.SelectedItem = selected;
            ApplyProviderToEditor(selected);
        }

        InvalidateProviderVerification();
        UpdateConfirmationSummary();
    }

    private void ProviderSetupSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProviderSetupComboBox.SelectedItem is ProviderConfiguration selected)
        {
            ExternalProviderComboBox.SelectedItem = selected;
            ApplyProviderToEditor(selected);
        }

        InvalidateProviderVerification();
    }

    private async void TestProviderConnection(object sender, RoutedEventArgs args)
    {
        TestProviderButton.IsEnabled = false;
        try
        {
            var existing = ProviderSetupComboBox.SelectedItem as ProviderConfiguration
                ?? throw new InvalidOperationException("请选择外部 Provider。");
            if (!Uri.TryCreate(ProviderBaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("Base URL 格式无效。");
            }

            var modelId = SelectedTag(ProviderModelComboBox)
                ?? throw new InvalidOperationException("请选择 Provider 模型。");
            if (existing.Kind == ProviderKind.DeepSeek && modelId == DeepSeekV4Catalog.ProModelId)
            {
                throw new InvalidOperationException("DeepSeek V4 Pro 不支持当前 Worker 协议；请选择 DeepSeek V4 Flash 0731。");
            }

            var credentials = App.Services.GetRequiredService<ICredentialStore>();
            var credentialReference = existing.CredentialReference ?? $"provider/{existing.Id}";
            if (!string.IsNullOrWhiteSpace(ProviderApiKeyBox.Password))
            {
                await credentials.SaveAsync(credentialReference, ProviderApiKeyBox.Password);
            }

            var provider = existing with
            {
                BaseUri = baseUri,
                CredentialReference = credentialReference,
                ModelId = modelId,
                IsEnabled = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var validation = await App.Services.GetRequiredService<ProviderConfigurationValidator>().ValidateAsync(provider);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join(" ", validation.Errors));
            }

            var result = await App.Services.GetRequiredService<IExternalProviderClient>().TestConnectionAsync(provider);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }

            await App.Services.GetRequiredService<IProviderRepository>().UpsertAsync(provider);
            ProviderApiKeyBox.Password = string.Empty;
            ProviderTestResultText.Text = $"连接成功：{provider.Name} / {provider.ModelId} · 响应模型：{result.ResponseModel ?? provider.ModelId} · Usage：{result.Usage?.TotalTokens?.ToString() ?? "不可取得"}";
            await LoadProvidersAsync();
            ExternalProviderComboBox.SelectedItem = (ExternalProviderComboBox.ItemsSource as IEnumerable<ProviderConfiguration>)?.FirstOrDefault(item => item.Id == provider.Id);
            providerConnectionVerified = true;
            verifiedProviderId = provider.Id;
        }
        catch (Exception exception)
        {
            InvalidateProviderVerification();
            ProviderTestResultText.Text = "连接测试失败：" + exception.Message;
        }
        finally
        {
            TestProviderButton.IsEnabled = true;
        }
    }

    private async Task<bool> ValidateCurrentStepAsync()
    {
        await Task.CompletedTask;
        switch (workflow.Current)
        {
            case OnboardingStep.Environment:
                if (!environmentReady)
                {
                    ShowError("Codex CLI 尚未就绪", "请先修复 Codex CLI 检测；CodexAppServer 依赖该组件。桌面应用缺失不会被静默改用 CLI。");
                    return false;
                }

                return true;
            case OnboardingStep.MainAgent:
            {
                var modelId = SelectedMainModel();
                if (modelId is null)
                {
                    ShowError("请选择主代理", "Sol、Terra 和 Luna 均会保留显示；请选择当前账户实际可用的模型。");
                    return false;
                }

                if (availableMainModels.Count > 0 && !availableMainModels.Contains(modelId))
                {
                    ShowError("主代理不可用", "当前 Codex 账户未提供所选模型；系统不会自动映射到其他模型。");
                    return false;
                }

                if (SelectedTag(ReasoningEffortComboBox) is null)
                {
                    ShowError("请选择推理强度", "推理强度必须来自当前模型支持的列表。");
                    return false;
                }

                return true;
            }
            case OnboardingStep.Worker:
                if (ExternalWorkerRadio.IsChecked == true && ExternalProviderComboBox.SelectedItem is not ProviderConfiguration)
                {
                    ShowError("请选择外部 Provider", "外部 Worker 必须关联一个已配置的 Provider。");
                    return false;
                }

                return true;
            case OnboardingStep.Provider:
                if (ExternalWorkerRadio.IsChecked == true &&
                    (!providerConnectionVerified ||
                     !string.Equals(verifiedProviderId, (ExternalProviderComboBox.SelectedItem as ProviderConfiguration)?.Id, StringComparison.Ordinal)))
                {
                    ShowError("需要连接测试", "外部 Worker 必须先在本步骤通过真实 Provider 连接测试。");
                    return false;
                }

                return true;
            case OnboardingStep.Confirm:
                if (string.IsNullOrWhiteSpace(ProfileNameBox.Text))
                {
                    ShowError("方案名称不能为空", "请为首次启动方案输入一个名称。");
                    return false;
                }

                return true;
            default:
                return false;
        }
    }

    private void InvalidateProviderVerification()
    {
        providerConnectionVerified = false;
        verifiedProviderId = null;
    }

    private async Task CompleteAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var source = SelectedWorkerSource();
        var enabled = source != WorkerSource.Disabled;
        var providerId = source switch
        {
            WorkerSource.NativeCodex => SelectedTag(NativeWorkerComboBox),
            WorkerSource.ExternalProvider => (ExternalProviderComboBox.SelectedItem as ProviderConfiguration)?.Id,
            _ => null,
        };
        var profile = new Profile(
            Guid.Empty,
            ProfileNameBox.Text.Trim(),
            new AgentSelection(SelectedMainModel()!, SelectedTag(ReasoningEffortComboBox)!),
            new WorkerPolicy(
                enabled,
                source,
                providerId,
                null,
                enabled ? (int)Math.Round(MaxWorkersBox.Value) : 0,
                Enum.Parse<RoutingMode>(SelectedTag(RoutingModeComboBox)!, true),
                Enum.Parse<FallbackAction>(SelectedTag(FallbackComboBox)!, true)),
            seedProfile?.Budget ?? new BudgetLimits(null, null, null, null, null, "CNY"),
            true,
            now,
            now,
            now)
        {
            ApprovalMode = seedProfile?.ApprovalMode ?? ExecutionApprovalMode.Automatic,
        };
        var profileService = App.Services.GetRequiredService<ProfileService>();
        var created = await profileService.CreateAsync(profile, makeDefault: true);
        await profileService.ActivateAsync(created.Id);

        var paths = App.Services.GetRequiredService<AppDataPaths>();
        paths.EnsureCreated();
        var statePath = Path.Combine(paths.Root, "onboarding.completed.json");
        await File.WriteAllTextAsync(statePath, $"{{\"completedAt\":\"{DateTimeOffset.UtcNow:O}\",\"profileId\":\"{created.Id:D}\"}}", new UTF8Encoding(false));
        OnboardingActionBar.Severity = InfoBarSeverity.Success;
        OnboardingActionBar.Title = "首次启动配置已完成";
        OnboardingActionBar.Message = $"已创建并启用真实 Profile“{created.Name}”。重启后不会自动再次显示向导，可随时从导航栏重新打开。";
        OnboardingActionBar.IsOpen = true;
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        NextButton.Content = "已完成";
    }

    private void UpdateStep()
    {
        var definition = workflow.Definition;
        OnboardingProgress.Value = (int)workflow.Current;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OnboardingProgress, $"首次启动向导，第 {(int)workflow.Current} 步，共 5 步");
        StepTitleText.Text = definition.Title;
        StepDescriptionText.Text = definition.Description;
        EnvironmentStepPanel.Visibility = workflow.Current == OnboardingStep.Environment ? Visibility.Visible : Visibility.Collapsed;
        MainAgentStepPanel.Visibility = workflow.Current == OnboardingStep.MainAgent ? Visibility.Visible : Visibility.Collapsed;
        WorkerStepPanel.Visibility = workflow.Current == OnboardingStep.Worker ? Visibility.Visible : Visibility.Collapsed;
        ProviderStepPanel.Visibility = workflow.Current == OnboardingStep.Provider ? Visibility.Visible : Visibility.Collapsed;
        ConfirmStepPanel.Visibility = workflow.Current == OnboardingStep.Confirm ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = workflow.CanGoBack;
        NextButton.Content = workflow.CanGoNext ? "下一步" : "完成并启用";
        SetStepVisual(Step1Text, workflow.Current == OnboardingStep.Environment);
        SetStepVisual(Step2Text, workflow.Current == OnboardingStep.MainAgent);
        SetStepVisual(Step3Text, workflow.Current == OnboardingStep.Worker);
        SetStepVisual(Step4Text, workflow.Current == OnboardingStep.Provider);
        SetStepVisual(Step5Text, workflow.Current == OnboardingStep.Confirm);
        UpdateProviderStepVisibility();
        if (workflow.Current == OnboardingStep.Confirm)
        {
            UpdateConfirmationSummary();
        }
    }

    private void UpdateProviderStepVisibility()
    {
        var external = ExternalWorkerRadio.IsChecked == true;
        NoProviderRequiredPanel.IsOpen = !external;
        ProviderConfigurationPanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAgentAvailability()
    {
        UpdateAgentAvailability(SolRadio, SolAvailabilityText, "gpt-5.6-sol");
        UpdateAgentAvailability(TerraRadio, TerraAvailabilityText, "gpt-5.6-terra");
        UpdateAgentAvailability(LunaRadio, LunaAvailabilityText, "gpt-5.6-luna");
        var selected = SelectedMainModel();
        if (selected is not null)
        {
            if (availableMainModels.Count > 0 && !availableMainModels.Contains(selected))
            {
                SolRadio.IsChecked = false;
                TerraRadio.IsChecked = false;
                LunaRadio.IsChecked = false;
                ReasoningEffortComboBox.Items.Clear();
                MainAgentAvailabilityText.Text = "此前方案选择的主代理不在当前账户目录中。系统没有替换或后台映射，请明确选择可用模型。";
                return;
            }

            PopulateReasoningEfforts(selected, SelectedTag(ReasoningEffortComboBox));
        }
    }

    private void UpdateAgentAvailability(RadioButton radio, TextBlock detail, string modelId)
    {
        var resolved = availableMainModels.Count == 0 || availableMainModels.Contains(modelId);
        radio.IsEnabled = resolved;
        detail.Text = availableMainModels.Count == 0
            ? "尚未完成账户能力读取；保存前会再次校验。"
            : resolved ? "当前账户可用" : "当前账户不可用；不会自动映射到其他模型。";
    }

    private void PopulateReasoningEfforts(string modelId, string? preferred)
    {
        var efforts = reasoningEfforts.TryGetValue(modelId, out var live)
            ? live
            : new[] { "low", "medium", "high", "xhigh" };
        ReasoningEffortComboBox.Items.Clear();
        foreach (var effort in efforts)
        {
            ReasoningEffortComboBox.Items.Add(new ComboBoxItem { Content = ReasoningLabel(effort), Tag = effort });
        }

        SelectComboTag(ReasoningEffortComboBox, preferred is not null && efforts.Contains(preferred) ? preferred : efforts.FirstOrDefault());
    }

    private void UpdateConfirmationSummary()
    {
        if (ProfileSummaryText is null)
        {
            return;
        }

        var source = SelectedWorkerSource();
        var worker = source switch
        {
            WorkerSource.Disabled => "未启用 Worker",
            WorkerSource.NativeCodex => $"原生 Worker：{SelectedTag(NativeWorkerComboBox) ?? "未选择"}",
            WorkerSource.ExternalProvider => $"外部 Worker：{(ExternalProviderComboBox.SelectedItem as ProviderConfiguration)?.Name ?? "未选择"} / {SelectedTag(ProviderModelComboBox) ?? "未选择模型"}",
            _ => "配置异常",
        };
        ProfileSummaryText.Text = $"主代理：{SelectedMainModel() ?? "未选择"} · 推理强度：{ReasoningLabel(SelectedTag(ReasoningEffortComboBox) ?? "未选择")}\n{worker}\n路由：{SelectedTag(RoutingModeComboBox) ?? "未选择"} · 最大 Worker：{(source == WorkerSource.Disabled ? 0 : (int)Math.Round(MaxWorkersBox.Value))} · 回退：{SelectedTag(FallbackComboBox) ?? "未选择"}";
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args) => UpdateAgentCardsLayout(args.NewSize.Width);

    private void UpdateAgentCardsLayout(double width)
    {
        if (width >= 1080)
        {
            AgentColumnOne.Width = new GridLength(1, GridUnitType.Star);
            AgentColumnTwo.Width = new GridLength(1, GridUnitType.Star);
            AgentColumnThree.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(TerraCard, 0); Grid.SetColumn(TerraCard, 1); Grid.SetColumnSpan(TerraCard, 1);
            Grid.SetRow(LunaCard, 0); Grid.SetColumn(LunaCard, 2); Grid.SetColumnSpan(LunaCard, 1);
        }
        else if (width >= 760)
        {
            AgentColumnOne.Width = new GridLength(1, GridUnitType.Star);
            AgentColumnTwo.Width = new GridLength(1, GridUnitType.Star);
            AgentColumnThree.Width = new GridLength(0);
            Grid.SetRow(TerraCard, 0); Grid.SetColumn(TerraCard, 1); Grid.SetColumnSpan(TerraCard, 1);
            Grid.SetRow(LunaCard, 1); Grid.SetColumn(LunaCard, 0); Grid.SetColumnSpan(LunaCard, 2);
        }
        else
        {
            AgentColumnOne.Width = new GridLength(1, GridUnitType.Star);
            AgentColumnTwo.Width = new GridLength(0);
            AgentColumnThree.Width = new GridLength(0);
            Grid.SetRow(TerraCard, 1); Grid.SetColumn(TerraCard, 0); Grid.SetColumnSpan(TerraCard, 3);
            Grid.SetRow(LunaCard, 2); Grid.SetColumn(LunaCard, 0); Grid.SetColumnSpan(LunaCard, 3);
        }
    }

    private static void SetStepVisual(TextBlock text, bool active)
    {
        text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        text.Opacity = active ? 1d : 0.58d;
    }

    private void SetWorkerSource(WorkerSource source)
    {
        NoWorkerRadio.IsChecked = source == WorkerSource.Disabled;
        NativeWorkerRadio.IsChecked = source == WorkerSource.NativeCodex;
        ExternalWorkerRadio.IsChecked = source == WorkerSource.ExternalProvider;
    }

    private void SetRadioForModelInstance(string modelId)
    {
        SolRadio.IsChecked = modelId == "gpt-5.6-sol";
        TerraRadio.IsChecked = modelId == "gpt-5.6-terra";
        LunaRadio.IsChecked = modelId == "gpt-5.6-luna";
    }

    private static void SelectComboTag(ComboBox comboBox, string? tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SelectedTag(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private string? SelectedMainModel() => new[] { SolRadio, TerraRadio, LunaRadio }
        .FirstOrDefault(radio => radio.IsChecked == true)?.Tag as string;

    private WorkerSource SelectedWorkerSource() => ExternalWorkerRadio.IsChecked == true
        ? WorkerSource.ExternalProvider
        : NativeWorkerRadio.IsChecked == true ? WorkerSource.NativeCodex : WorkerSource.Disabled;

    private void ApplyProviderToEditor(ProviderConfiguration? provider)
    {
        if (provider is null)
        {
            return;
        }

        ProviderBaseUrlBox.Text = provider.BaseUri?.AbsoluteUri ?? DeepSeekV4Catalog.BaseUrl;
        SelectComboTag(ProviderModelComboBox, provider.ModelId ?? DeepSeekV4Catalog.FlashModelId);
    }

    private void ShowError(string title, string message)
    {
        OnboardingActionBar.Severity = InfoBarSeverity.Error;
        OnboardingActionBar.Title = title;
        OnboardingActionBar.Message = message;
        OnboardingActionBar.IsOpen = true;
    }

    private static string ReasoningLabel(string effort) => effort switch
    {
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        _ => effort,
    };
}
