using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class UsageBudgetPage : Page
{
    public UsageBudgetPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var repository = App.Services.GetRequiredService<IUsageLedgerRepository>();
        var ledgers = await repository.ListTaskGroupsAsync();
        var allUsage = new List<UsageSnapshot>();
        foreach (var ledger in ledgers)
        {
            allUsage.AddRange(await repository.ListUsageAsync(ledger.Id));
        }

        var costs = allUsage.Where(item => item.Cost.Value is not null).ToArray();
        var actualCosts = costs.Where(item => item.Cost.Evidence == EvidenceKind.Actual).ToArray();
        var selectedCosts = actualCosts.Length > 0 ? actualCosts : costs;
        ExternalCostText.Text = selectedCosts.Length == 0 ? "不可取得" : $"{selectedCosts.Sum(item => item.Cost.Value!.Value):0.######} {selectedCosts[0].Currency}";
        ExternalCostEvidenceText.Text = selectedCosts.Length == 0 ? "没有可取得的费用字段" : actualCosts.Length > 0 ? "外部服务返回实付" : "估算";
        var tokens = allUsage.Where(item => item.TotalTokens.Value is not null).ToArray();
        ProviderTokensText.Text = tokens.Length == 0 ? "不可取得" : tokens.Sum(item => item.TotalTokens.Value!.Value).ToString("N0");
        ProviderTokensEvidenceText.Text = tokens.Length == 0 ? "没有可取得的令牌字段" : tokens.All(item => item.TotalTokens.Evidence == EvidenceKind.Actual) ? "外部服务返回" : "包含估算";

        var profile = await App.Services.GetRequiredService<IProfileRepository>().GetDefaultAsync();
        var dailyLimit = profile?.Budget.Daily;
        var dailyCost = selectedCosts.Sum(item => item.Cost.Value ?? 0m);
        var ratio = dailyLimit is > 0 ? Math.Min(1m, dailyCost / dailyLimit.Value) : 0m;
        BudgetProgress.Value = (double)ratio;
        BudgetRatioText.Text = $"{ratio:P0}";
        DailyBudgetText.Text = dailyLimit is null ? $"{dailyCost:0.######} / 未设置" : $"{dailyCost:0.######} / {dailyLimit:0.##} {profile!.Budget.Currency}";
        TaskBudgetText.Text = profile?.Budget.PerTask is null ? "无当前任务 / 未设置" : $"无当前任务 / {profile.Budget.PerTask:0.##} {profile.Budget.Currency}";
        MonthlyBudgetText.Text = profile?.Budget.Monthly is null ? "尚无费用 / 未设置" : $"尚无费用 / {profile.Budget.Monthly:0.##} {profile.Budget.Currency}";

        if (ledgers.FirstOrDefault() is { } latest)
        {
            var report = App.Services.GetRequiredService<EconomicReportService>().Create(latest, await repository.ListUsageAsync(latest.Id));
            ReportMainAgentText.Text = $"{latest.MainModelId}（{latest.MainReasoningEffort}）";
            ReportWorkerText.Text = latest.Workers.Count == 0
                ? "未调用"
                : string.Join("、", latest.Workers.Select(worker => $"{worker.AdapterId} / {worker.ModelId}"));
            ReportAdoptionText.Text = latest.Workers.Count == 0
                ? "不适用"
                : string.Join("、", latest.Workers.Select(worker => worker.AdoptionStatus.ToString()));
            ReportEconomicText.Text = EconomicConclusionLabel(report.Conclusion);
            LatestReportBar.Title = $"经济结论：{EconomicConclusionLabel(report.Conclusion)}";
            LatestReportBar.Message = report.ConclusionReason;
            LatestReportBar.IsOpen = true;
        }
    }

    private static string EconomicConclusionLabel(EconomicConclusion conclusion) => conclusion switch
    {
        EconomicConclusion.PossiblySaved => "可能节省",
        EconomicConclusion.CannotDetermine => "无法判断",
        EconomicConclusion.PossiblyIncreased => "可能增加",
        _ => conclusion.ToString(),
    };
}
