using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class UsageBudgetPage : Page
{
    public UsageBudgetPage() { InitializeComponent(); Loaded += OnLoaded; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = await App.Services.GetRequiredService<IAgentSwitchUiStateSource>().ReadAsync();
            var usage = snapshot.Usage;
            SetMetric(InputTokensText, InputTokensDetailText, usage.InputTokens, usage.AvailabilityLabel);
            SetMetric(OutputTokensText, OutputTokensDetailText, usage.OutputTokens, usage.AvailabilityLabel);
            SetMetric(TotalTokensText, TotalTokensDetailText, usage.TotalTokens, usage.AvailabilityLabel);
            ExternalCostText.Text = usage.Cost is null
                ? usage.AvailabilityLabel == "暂无数据" ? "暂无数据" : "暂不可取得"
                : $"{usage.Cost:0.######} {usage.Currency}";
            ExternalCostEvidenceText.Text = usage.Cost is null ? usage.EvidenceMessage : "仅汇总有证据的费用记录。";
            TodayCallsText.Text = usage.AvailabilityLabel == "暂无数据" ? "暂无数据" : usage.TodayExternalCalls.ToString("N0");
            TodayCallsDetailText.Text = usage.AvailabilityLabel == "暂无数据" ? "尚无 External Worker 调用记录" : "仅统计 External Provider 调用";
            LatestWorkerText.Text = $"Worker：{usage.LatestWorkerKind}";
            LatestStatusText.Text = $"状态：{usage.LatestCallStatus}";
            LatestCallAtText.Text = $"时间：{UiPresentation.Time(usage.LatestCallAt)}";
            NativeUsageText.Text = usage.NativeUsageMessage;
            EvidenceText.Text = usage.EvidenceMessage;
            var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync();
            var daily = profile?.Budget.Daily;
            var currency = profile?.Budget.Currency ?? "CNY";
            BudgetRatioText.Text = daily is null || daily <= 0 || usage.Cost is null ? "—" : $"{Math.Min(1m, usage.Cost.Value / daily.Value):P0}";
            BudgetProgress.Value = daily is null || usage.Cost is null || daily <= 0 ? 0 : (double)Math.Min(1m, usage.Cost.Value / daily.Value);
            TaskBudgetText.Text = profile?.Budget.PerTask is decimal task ? $"未单独计量 / {task:0.##} {currency}" : "暂无数据：未设置单任务限额";
            DailyBudgetText.Text = daily is decimal limit
                ? usage.Cost is decimal cost ? $"{cost:0.######} / {limit:0.##} {currency}" : $"暂不可取得 / {limit:0.##} {currency}"
                : "暂无数据：未设置每日限额";
            MonthlyBudgetText.Text = profile?.Budget.Monthly is decimal month ? $"未单独计量 / {month:0.##} {currency}" : "暂无数据：未设置每月限额";
        }
        catch (Exception ex)
        {
            LatestReportBar.Title = "用量暂不可取得"; LatestReportBar.Message = ex.Message; LatestReportBar.IsOpen = true;
        }
    }

    private static void SetMetric(TextBlock value, TextBlock detail, long? number, string availability)
    {
        value.Text = number is long n ? n.ToString("N0") : availability == "暂无数据" ? "暂无数据" : "暂不可取得";
        detail.Text = number is long ? availability : availability == "暂无数据" ? "暂无数据" : "当前接口不支持";
    }
}
