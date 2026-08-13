using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Usage;

public sealed class BudgetPolicy
{
    public BudgetAssessment Evaluate(BudgetLimits limits, BudgetConsumption consumption)
    {
        var ratios = new List<(decimal Ratio, string Reason)>();
        AddRatio(ratios, consumption.TaskCost, limits.PerTask, "单任务费用");
        AddRatio(ratios, consumption.DailyCost, limits.Daily, "每日费用");
        AddRatio(ratios, consumption.MonthlyCost, limits.Monthly, "每月费用");
        AddUnknown(ratios, consumption.TaskCostUnknown, limits.PerTask, "单任务费用未知");
        AddUnknown(ratios, consumption.DailyCostUnknown, limits.Daily, "每日费用未知");
        AddUnknown(ratios, consumption.MonthlyCostUnknown, limits.Monthly, "每月费用未知");
        AddRatio(ratios, consumption.Tokens, limits.TokenLimit, "Token");
        AddRatio(ratios, consumption.Requests, limits.RequestLimit, "请求次数");
        var highest = ratios.Count == 0 ? 0m : ratios.Max(item => item.Ratio);
        var checkpoints = Enum.GetValues<BudgetCheckpoint>()
            .Where(checkpoint => highest >= (int)checkpoint / 100m)
            .ToArray();
        var blocked = highest >= 1m;
        var reasons = ratios
            .Where(item => item.Ratio >= 0.25m)
            .OrderByDescending(item => item.Ratio)
            .Select(item => $"{item.Reason}已使用 {item.Ratio:P0}。")
            .ToArray();
        return new BudgetAssessment(!blocked, highest, checkpoints, reasons);
    }

    private static void AddRatio(List<(decimal Ratio, string Reason)> ratios, decimal consumed, decimal? limit, string reason)
    {
        if (limit is null)
        {
            return;
        }

        ratios.Add((limit.Value == 0m ? 1m : Math.Max(0m, consumed / limit.Value), reason));
    }

    private static void AddRatio(List<(decimal Ratio, string Reason)> ratios, long consumed, long? limit, string reason)
    {
        if (limit is null)
        {
            return;
        }

        ratios.Add((limit.Value == 0 ? 1m : Math.Max(0m, (decimal)consumed / limit.Value), reason));
    }

    private static void AddRatio(List<(decimal Ratio, string Reason)> ratios, int consumed, int? limit, string reason)
    {
        if (limit is null)
        {
            return;
        }

        ratios.Add((limit.Value == 0 ? 1m : Math.Max(0m, (decimal)consumed / limit.Value), reason));
    }

    private static void AddUnknown(List<(decimal Ratio, string Reason)> ratios, bool unknown, decimal? limit, string reason)
    {
        // An unknown monetary amount cannot be treated as zero when a corresponding
        // limit is configured. Block explicitly while preserving token/request checks.
        if (unknown && limit is not null)
        {
            ratios.Add((1m, reason));
        }
    }
}
