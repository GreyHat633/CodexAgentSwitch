using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.App.ViewModels;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ProvidersPage : Page, IContentActionHandler
{
    private const string ProviderId = "deepseek-default";
    private const string CredentialReference = "provider/deepseek-default";
    private const string OpenCodeZenProviderId = "opencode-zen";
    private bool workerTestInProgress;

    public ProvidersPage()
    {
        InitializeComponent();
        ModelSelectionComboBox.ItemsSource = DeepSeekV4Catalog.Models;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshDeepSeekAsync();
        await RefreshOpenCodeZenAsync();
    }

    private async void RefreshOpenCodeZenModels(object sender, RoutedEventArgs e) => await RefreshOpenCodeZenAsync();

    private async void SaveOpenCodeZen(object sender, RoutedEventArgs e)
    {
        var selection = OpenCodeZenModelComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selection))
        {
            ShowOpenCodeZen(InfoBarSeverity.Warning, "Model selection is missing", "Refresh models and choose an OpenCode Zen model before saving.");
            return;
        }

        var repository = App.Services.GetRequiredService<IProviderRepository>();
        var existing = await repository.GetAsync(OpenCodeZenProviderId) ?? ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow);
        await repository.UpsertAsync(existing with { ModelId = selection, UpdatedAt = DateTimeOffset.UtcNow });
        ShowOpenCodeZen(InfoBarSeverity.Success, "OpenCode Zen selection saved", $"Selected model: {selection}. Test the CLI before enabling it.");
        await RefreshDeepSeekAsync();
    }

    private async void TestOpenCodeZen(object sender, RoutedEventArgs e)
    {
        var provider = await App.Services.GetRequiredService<IProviderRepository>().GetAsync(OpenCodeZenProviderId);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ModelId))
        {
            ShowOpenCodeZen(InfoBarSeverity.Warning, "Model selection is missing", "Refresh models and choose a model first.");
            return;
        }

        var result = await App.Services.GetRequiredService<IExternalProviderClient>().TestConnectionAsync(provider);
        if (result.Succeeded)
        {
            await App.Services.GetRequiredService<IProviderRepository>().UpsertAsync(provider with { IsEnabled = true, UpdatedAt = DateTimeOffset.UtcNow });
        }
        ShowOpenCodeZen(result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Succeeded ? "OpenCode Zen catalog ready" : "OpenCode Zen test failed", result.Message);
    }

    private async Task RefreshOpenCodeZenAsync()
    {
        try
        {
            var client = App.Services.GetRequiredService<IExternalProviderClient>();
            var provider = await App.Services.GetRequiredService<IProviderRepository>().GetAsync(OpenCodeZenProviderId)
                ?? ProviderConfiguration.OpenCodeZenPreset(DateTimeOffset.UtcNow);
            var models = await client.ListModelsAsync(provider);
            OpenCodeZenModelComboBox.ItemsSource = models;
            OpenCodeZenModelComboBox.SelectedItem = provider.ModelId;
            if (string.IsNullOrWhiteSpace(provider.ModelId))
            {
                ShowOpenCodeZen(InfoBarSeverity.Warning, "Model selection is missing", "Choose a discovered model before running a Worker.");
            }
            else if (!models.Contains(provider.ModelId, StringComparer.Ordinal))
            {
                ShowOpenCodeZen(InfoBarSeverity.Warning, "Saved model is no longer available", $"{provider.ModelId} disappeared from the refreshed Zen catalog. Reselect a model; the saved selection was preserved.");
            }
        }
        catch (Exception exception)
        {
            ShowOpenCodeZen(InfoBarSeverity.Error, "OpenCode Zen model refresh failed", exception.Message);
        }
    }

    private void ShowOpenCodeZen(InfoBarSeverity severity, string title, string message)
    {
        OpenCodeZenInfoBar.Severity = severity;
        OpenCodeZenInfoBar.Title = title;
        OpenCodeZenInfoBar.Message = message;
        OpenCodeZenInfoBar.IsOpen = true;
    }

    private void OpenDeepSeekConfiguration(object sender, RoutedEventArgs e) => ProviderEditor.IsExpanded = true;

    private void ModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelSelectionComboBox.SelectedItem is ProviderModelDefinition model)
        {
            ProviderResultBar.IsOpen = false;
            if (!model.Supports(ProviderProtocol.CodexWorker))
            {
                ProviderResultBar.Severity = InfoBarSeverity.Warning;
                ProviderResultBar.Title = model.DisplayName;
                ProviderResultBar.Message = model.WorkerUnavailableReason ?? DeepSeekV4Catalog.UnsupportedWorkerReason;
                ProviderResultBar.IsOpen = true;
            }
        }
    }

    private async void SaveProvider(object sender, RoutedEventArgs e) => await SaveOrTestProviderAsync(testConnection: false);

    private async void TestAndSaveProvider(object sender, RoutedEventArgs e) => await SaveOrTestProviderAsync(testConnection: true);

    private async Task SaveOrTestProviderAsync(bool testConnection)
    {
        ProviderResultBar.IsOpen = false;
        TestConnectionButton.IsEnabled = false;
        SaveProviderButton.IsEnabled = false;
        try
        {
            var credentials = App.Services.GetRequiredService<ICredentialStore>();
            var repository = App.Services.GetRequiredService<IProviderRepository>();
            var validator = App.Services.GetRequiredService<ProviderConfigurationValidator>();
            var client = App.Services.GetRequiredService<IExternalProviderClient>();
            var existing = await repository.GetAsync(ProviderId);

            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                await credentials.SaveAsync(CredentialReference, ApiKeyBox.Password);
            }

            var provider = BuildProvider(existing);
            var validation = await validator.ValidateAsync(provider);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join(" ", validation.Errors));
            }

            if (testConnection)
            {
                var result = await client.TestConnectionAsync(provider);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(result.Message);
                }

                // Keep the selected catalog model. A server may return an alias in
                // response.model, which must not silently change the selection.
                provider = provider with { IsEnabled = true };
                await repository.UpsertAsync(provider);
                ProviderResultBar.Severity = InfoBarSeverity.Success;
                ProviderResultBar.Title = "连接成功并已启用";
                ProviderResultBar.Message = $"模型：{provider.ModelId}；延迟：{result.Latency.TotalMilliseconds:0} 毫秒；用量：{(result.Usage is null ? "不可取得" : $"{result.Usage.TotalTokens ?? 0} 个令牌")}。";
            }
            else
            {
                await repository.UpsertAsync(provider);
                ProviderResultBar.Severity = InfoBarSeverity.Success;
                ProviderResultBar.Title = "服务商已保存";
                ProviderResultBar.Message = $"已保存模型 {provider.ModelId}；服务商保持当前启用状态。";
            }

            ApiKeyBox.Password = string.Empty;
            ProviderResultBar.IsOpen = true;
            await RefreshDeepSeekAsync();
        }
        catch (Exception exception)
        {
            ProviderResultBar.Severity = InfoBarSeverity.Error;
            ProviderResultBar.Title = "服务商操作失败";
            ProviderResultBar.Message = exception.Message;
            ProviderResultBar.IsOpen = true;
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
            SaveProviderButton.IsEnabled = true;
        }
    }

    private async void EnableDeepSeek(object sender, RoutedEventArgs e)
    {
        await SetDeepSeekEnabledAsync(true);
    }

    private async void DisableDeepSeek(object sender, RoutedEventArgs e)
    {
        await SetDeepSeekEnabledAsync(false);
    }

    private async Task SetDeepSeekEnabledAsync(bool enabled)
    {
        var repository = App.Services.GetRequiredService<IProviderRepository>();
        var provider = await repository.GetAsync(ProviderId);
        if (provider is null)
        {
            ProviderResultBar.Severity = InfoBarSeverity.Warning;
            ProviderResultBar.Title = "服务商尚未保存";
            ProviderResultBar.Message = "请先保存 API 密钥和模型选择。";
            ProviderResultBar.IsOpen = true;
            return;
        }

        var hasCredential = await App.Services.GetRequiredService<ICredentialStore>().ExistsAsync(provider.CredentialReference ?? CredentialReference);
        if (enabled && !hasCredential)
        {
            ProviderResultBar.Severity = InfoBarSeverity.Error;
            ProviderResultBar.Title = "无法启用服务商";
            ProviderResultBar.Message = "API 密钥尚未保存在 Windows 凭据管理器中。";
            ProviderResultBar.IsOpen = true;
            return;
        }

        await repository.UpsertAsync(provider with { IsEnabled = enabled, UpdatedAt = DateTimeOffset.UtcNow });
        ProviderResultBar.Severity = InfoBarSeverity.Informational;
        ProviderResultBar.Title = enabled ? "服务商已启用" : "服务商已停用";
        ProviderResultBar.Message = enabled
            ? "当前方案选择此 Provider 后，新的 Worker 请求将使用所选 DeepSeek V4 模型。"
            : "Provider 已停用且凭据仍保留；任务将严格按当前 Profile 的回退策略处理，不会静默切换 Worker。";
        ProviderResultBar.IsOpen = true;
        await RefreshDeepSeekAsync();
    }

    private async void DeleteDeepSeek(object sender, RoutedEventArgs e)
    {
        var repository = App.Services.GetRequiredService<IProviderRepository>();
        await repository.DeleteAsync(ProviderId);
        await App.Services.GetRequiredService<ICredentialStore>().DeleteAsync(CredentialReference);
        ApiKeyBox.Password = string.Empty;
        ProviderResultBar.Severity = InfoBarSeverity.Informational;
        ProviderResultBar.Title = "服务商已删除";
        ProviderResultBar.Message = "配置和 Windows 凭据管理器中的 API 密钥已清除。";
        ProviderResultBar.IsOpen = true;
        await RefreshDeepSeekAsync();
    }

    private async void ClearProviderKey(object sender, RoutedEventArgs e)
    {
        await App.Services.GetRequiredService<ICredentialStore>().DeleteAsync(CredentialReference);
        ApiKeyBox.Password = string.Empty;
        ProviderResultBar.Severity = InfoBarSeverity.Informational;
        ProviderResultBar.Title = "API 密钥已删除";
        ProviderResultBar.Message = "Windows 凭据管理器中的凭据已清除；服务商配置不含明文密钥。";
        ProviderResultBar.IsOpen = true;
        await RefreshDeepSeekAsync();
    }

    private async void ReloadProviders(object sender, RoutedEventArgs e) => await RefreshDeepSeekAsync();

    public async Task HandleContentActionAsync(string action, Button source)
    {
        if (action == "provider:add")
        {
            ProviderEditor.IsExpanded = true;
            ProviderResultBar.Severity = InfoBarSeverity.Informational;
            ProviderResultBar.Title = "服务商编辑器已打开";
            ProviderResultBar.Message = "选择模型并安全保存 API 密钥；当前版本提供经过验证的 DeepSeek V4 预设。";
            ProviderResultBar.IsOpen = true;
            ApiKeyBox.Focus(FocusState.Programmatic);
            return;
        }

        if (action == "provider:test-native")
        {
            var state = await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync();
            ProviderEditor.IsExpanded = true;
            ProviderResultBar.Severity = state.Installed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ProviderResultBar.Title = state.Installed ? "原生 Codex 检测成功" : "原生 Codex 尚不可用";
            ProviderResultBar.Message = state.Message;
            ProviderResultBar.IsOpen = true;
            return;
        }

        if (action == "provider:force-worker") await ForceTestCurrentWorkerAsync(source);
    }

    private async Task DetectNativeAsync()
    {
        var state = await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync();
        ProviderEditor.IsExpanded = true;
        ProviderResultBar.Severity = state.Installed ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ProviderResultBar.Title = state.Installed ? "原生 Codex 检测成功" : "原生 Codex 尚不可用";
        ProviderResultBar.Message = state.Message;
        ProviderResultBar.IsOpen = true;
    }

    private async Task ForceTestCurrentWorkerAsync(Button? source = null)
    {
        if (workerTestInProgress)
        {
            return;
        }

        workerTestInProgress = true;
        var originalContent = source?.Content;
        if (source is not null)
        {
            source.IsEnabled = false;
            source.Content = "测试中…";
        }

        ProviderResultBar.Severity = InfoBarSeverity.Informational;
        ProviderResultBar.Title = "正在测试当前 Worker";
        ProviderResultBar.Message = "正在通过当前方案选择的 Provider 发起一次显式连接测试。";
        ProviderResultBar.IsOpen = true;
        try
        {
            var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync()
                ?? throw new InvalidOperationException("尚未设置当前配置方案。");
            if (!profile.WorkerPolicy.Enabled || profile.WorkerPolicy.Source != WorkerSource.ExternalProvider)
            {
                throw new InvalidOperationException("当前方案未启用外部 Worker；请先在配置方案中选择 DeepSeek Provider。");
            }

            if (!string.Equals(profile.WorkerPolicy.PreferredProviderId, ProviderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("当前方案选择的不是此 Provider；请在当前方案中确认外部 Provider 后再测试。");
            }

            var provider = await App.Services.GetRequiredService<IProviderRepository>().GetAsync(ProviderId)
                ?? throw new InvalidOperationException("当前方案引用的 DeepSeek Provider 不存在。");
            if (!provider.IsEnabled)
            {
                throw new InvalidOperationException("当前 DeepSeek Provider 已停用。");
            }

            var result = await App.Services.GetRequiredService<IExternalProviderClient>().TestConnectionAsync(provider);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }

            ProviderResultBar.Severity = InfoBarSeverity.Success;
            ProviderResultBar.Title = "当前外部 Worker 测试成功";
            ProviderResultBar.Message = $"Provider：{provider.Name}；Worker 模型：{provider.ModelId}；响应模型：{result.ResponseModel ?? provider.ModelId}；耗时：{result.Latency.TotalMilliseconds:0} 毫秒；Usage：{result.Usage?.TotalTokens?.ToString() ?? "不可取得"}。未启动原生 Codex 子代理。";
            ProviderResultBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ProviderResultBar.Severity = InfoBarSeverity.Error;
            ProviderResultBar.Title = "当前外部 Worker 测试失败";
            ProviderResultBar.Message = exception.Message;
            ProviderResultBar.IsOpen = true;
        }
        finally
        {
            workerTestInProgress = false;
            if (source is not null)
            {
                source.Content = originalContent;
                source.IsEnabled = true;
            }
        }
    }

    private ProviderConfiguration BuildProvider(ProviderConfiguration? existing)
    {
        if (!Uri.TryCreate(BaseUrlText.Text.Trim(), UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("基础地址格式无效。");
        }

        var model = ModelSelectionComboBox.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("请选择一个 DeepSeek V4 模型。");
        }

        var now = DateTimeOffset.UtcNow;
        return new ProviderConfiguration(
            ProviderId,
            string.IsNullOrWhiteSpace(ProviderNameText.Text) ? "DeepSeek" : ProviderNameText.Text.Trim(),
            ProviderKind.DeepSeek,
            baseUri,
            CredentialReference,
            model,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(60),
            existing?.IsEnabled ?? false,
            existing?.Pricing,
            existing?.CreatedAt ?? now,
            now);
    }

    private async Task RefreshDeepSeekAsync()
    {
        var repository = App.Services.GetRequiredService<IProviderRepository>();
        var credentials = App.Services.GetRequiredService<ICredentialStore>();
        var provider = await repository.GetAsync(ProviderId);
        var hasCredential = provider?.CredentialReference is not null
            && await credentials.ExistsAsync(provider.CredentialReference);

        ProviderNameText.Text = provider?.Name ?? "DeepSeek";
        BaseUrlText.Text = provider?.BaseUri?.AbsoluteUri ?? DeepSeekV4Catalog.BaseUrl;
        ModelSelectionComboBox.SelectedValue = provider?.ModelId ?? DeepSeekV4Catalog.FlashModelId;
        DeepSeekStatusText.Text = provider?.IsEnabled == true ? "已启用" : hasCredential ? "已停用" : "未配置";
        DeepSeekDetailText.Text = $"凭据：{(hasCredential ? "已安全配置" : "未配置")} · 模型：{provider?.ModelId ?? DeepSeekV4Catalog.FlashModelId} · 今日费用：不可取得";
        EnableDeepSeekButton.IsEnabled = provider is not null && provider.IsEnabled == false;
        await RenderProviderCardsAsync();
    }

    private async Task RenderProviderCardsAsync()
    {
        ProviderCardsPanel.Children.Clear();
        var snapshot = await App.Services.GetRequiredService<IAgentSwitchUiStateSource>().ReadAsync();
        foreach (var status in snapshot.Providers)
        {
            var details = $"凭据：{status.CredentialLabel} · 当前方案：{(status.IsUsedByCurrentProfile ? "正在使用" : "未使用")} · 模型：{status.Model} · 最近调用：{status.LastCallLabel}";
            var body = new StackPanel { Spacing = 5 };
            body.Children.Add(new TextBlock { Text = status.Name, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SectionTitleStyle"] });
            body.Children.Add(new TextBlock { Text = status.StateLabel, Foreground = UiPresentation.ToneBrush(status.Tone), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextStyle"] });
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(body);
            var actions = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (status.Kind == Domain.Providers.ProviderKind.NativeCodex)
            {
                var native = new Button { Content = "测试", Tag = "provider:test-native" };
                native.Click += async (_, _) => await DetectNativeAsync();
                actions.Children.Add(native);
            }
            else
            {
                var configure = new Button { Content = "配置", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
                configure.Click += OpenDeepSeekConfiguration;
                actions.Children.Add(configure);
                var toggle = new Button { Content = status.IsEnabled ? "停用" : "启用" };
                toggle.Click += async (_, _) => await SetDeepSeekEnabledAsync(!status.IsEnabled);
                actions.Children.Add(toggle);
                var force = new Button { Content = "测试当前 Worker", Tag = "provider:force-worker" };
                force.Click += async (_, _) => await ForceTestCurrentWorkerAsync(force);
                actions.Children.Add(force);
            }
            Grid.SetColumn(actions, 1); grid.Children.Add(actions);
            ProviderCardsPanel.Children.Add(new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CardBorderStyle"], Child = grid });
        }
    }
}
