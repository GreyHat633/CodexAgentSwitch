using CodexAgentSwitch.Application.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class OnboardingPage : Page, IContentActionHandler
{
    private int step = 2;

    public OnboardingPage()
    {
        InitializeComponent();
        UpdateStep();
    }

    public async Task HandleContentActionAsync(string action, Button source)
    {
        if (action == "onboarding:back")
        {
            step = Math.Max(1, step - 1);
            UpdateStep();
            return;
        }

        if (action != "onboarding:next")
        {
            return;
        }

        if (step < 5)
        {
            step++;
            UpdateStep();
            return;
        }

        var repository = App.Services.GetRequiredService<IProfileRepository>();
        var current = await repository.GetDefaultAsync();
        if (current is not null)
        {
            await repository.UpsertAsync(current with
            {
                LastUsedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        OnboardingActionBar.Severity = InfoBarSeverity.Success;
        OnboardingActionBar.Title = "首次启动配置已完成";
        OnboardingActionBar.Message = "默认方案已确认；重新启动后会恢复此配置。";
        OnboardingActionBar.IsOpen = true;
        NextButton.IsEnabled = false;
        NextButton.Content = "已完成";
    }

    private void UpdateStep()
    {
        var titles = new[] { "检查环境", "选择主代理", "选择 Worker", "配置 Provider", "确认设置" };
        var descriptions = new[]
        {
            "检查 Windows App SDK Runtime 与 Codex CLI。",
            "推理强度会根据当前 Codex 的真实能力动态显示。",
            "选择最多三个边界明确、可独立验收的 Worker。",
            "可选：配置外部 Provider，密钥只进入 Windows Credential Manager。",
            "确认配置并保存到本地数据库。",
        };
        OnboardingProgress.Value = step;
        OnboardingProgress.SetValue(AutomationProperties.NameProperty, $"首次启动向导，第 {step} 步，共 5 步");
        StepTitleText.Text = titles[step - 1];
        StepDescriptionText.Text = descriptions[step - 1];
        BackButton.IsEnabled = step > 1;
        NextButton.Content = step == 5 ? "完成并启用" : "下一步";
        OnboardingActionBar.IsOpen = false;
    }
}
