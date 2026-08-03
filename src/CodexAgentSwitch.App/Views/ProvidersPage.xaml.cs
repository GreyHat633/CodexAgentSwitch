using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Domain.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class ProvidersPage : Page
{
    public ProvidersPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshDeepSeekAsync();

    private void OpenDeepSeekConfiguration(object sender, RoutedEventArgs e) => ProviderEditor.IsExpanded = true;

    private async void TestAndSaveProvider(object sender, RoutedEventArgs e)
    {
        ProviderResultBar.IsOpen = false;
        TestConnectionButton.IsEnabled = false;
        var credentials = App.Services.GetRequiredService<ICredentialStore>();
        var repository = App.Services.GetRequiredService<IProviderRepository>();
        var validator = App.Services.GetRequiredService<ProviderConfigurationValidator>();
        var client = App.Services.GetRequiredService<IExternalProviderClient>();
        const string providerId = "deepseek-default";
        const string credentialReference = "provider/deepseek-default";
        var existing = await repository.GetAsync(providerId);
        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                await credentials.SaveAsync(credentialReference, ApiKeyBox.Password);
            }

            if (!Uri.TryCreate(BaseUrlText.Text.Trim(), UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("Base URL 格式无效。");
            }

            var now = DateTimeOffset.UtcNow;
            var provider = new ProviderConfiguration(
                providerId,
                string.IsNullOrWhiteSpace(ProviderNameText.Text) ? "DeepSeek" : ProviderNameText.Text.Trim(),
                ProviderKind.DeepSeek,
                baseUri,
                credentialReference,
                string.IsNullOrWhiteSpace(ModelIdText.Text) ? null : ModelIdText.Text.Trim(),
                new Dictionary<string, string>(),
                TimeSpan.FromSeconds(60),
                false,
                existing?.Pricing,
                existing?.CreatedAt ?? now,
                now);
            var validation = await validator.ValidateAsync(provider);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join(" ", validation.Errors));
            }

            var result = await client.TestConnectionAsync(provider);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }

            await repository.UpsertAsync(provider with { IsEnabled = true, ModelId = result.ResponseModel ?? provider.ModelId });
            ApiKeyBox.Password = string.Empty;
            ProviderResultBar.Severity = InfoBarSeverity.Success;
            ProviderResultBar.Title = "连接成功并已启用";
            ProviderResultBar.Message = $"模型：{result.ResponseModel ?? provider.ModelId}；延迟：{result.Latency.TotalMilliseconds:0} ms；Usage：{(result.Usage is null ? "不可取得" : $"{result.Usage.TotalTokens ?? 0} tokens")}。";
            ProviderResultBar.IsOpen = true;
            await RefreshDeepSeekAsync();
        }
        catch (Exception exception)
        {
            ProviderResultBar.Severity = InfoBarSeverity.Error;
            ProviderResultBar.Title = "Provider 未启用";
            ProviderResultBar.Message = exception.Message;
            ProviderResultBar.IsOpen = true;
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private async void DisableDeepSeek(object sender, RoutedEventArgs e)
    {
        var repository = App.Services.GetRequiredService<IProviderRepository>();
        var provider = await repository.GetAsync("deepseek-default");
        if (provider is not null)
        {
            await repository.UpsertAsync(provider with { IsEnabled = false, UpdatedAt = DateTimeOffset.UtcNow });
        }

        ProviderResultBar.Severity = InfoBarSeverity.Informational;
        ProviderResultBar.Title = "已停用外部 API";
        ProviderResultBar.Message = "新任务将回退到原生 Luna；凭据未删除，运行中任务需要用户单独决定继续或取消。";
        ProviderResultBar.IsOpen = true;
        await RefreshDeepSeekAsync();
    }

    private async void ClearProviderKey(object sender, RoutedEventArgs e)
    {
        await App.Services.GetRequiredService<ICredentialStore>().DeleteAsync("provider/deepseek-default");
        ApiKeyBox.Password = string.Empty;
        ProviderResultBar.Severity = InfoBarSeverity.Informational;
        ProviderResultBar.Title = "API Key 已删除";
        ProviderResultBar.Message = "Windows Credential Manager 中的凭据已清除；Provider 配置不含明文 Key。";
        ProviderResultBar.IsOpen = true;
        await RefreshDeepSeekAsync();
    }

    private async Task RefreshDeepSeekAsync()
    {
        var provider = await App.Services.GetRequiredService<IProviderRepository>().GetAsync("deepseek-default");
        var hasCredential = provider?.CredentialReference is not null
            && await App.Services.GetRequiredService<ICredentialStore>().ExistsAsync(provider.CredentialReference);
        DeepSeekStatusText.Text = provider?.IsEnabled == true ? "已启用" : hasCredential ? "已停用" : "未配置";
        DeepSeekDetailText.Text = $"凭据：{(hasCredential ? "已安全配置" : "未配置")} · Model：{provider?.ModelId ?? "未选择"} · 今日费用：不可取得";
        DisableDeepSeekButton.IsEnabled = provider?.IsEnabled == true;
    }
}
