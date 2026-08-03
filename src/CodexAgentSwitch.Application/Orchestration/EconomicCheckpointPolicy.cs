using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Application.Orchestration;

public sealed class EconomicCheckpointPolicy
{
    public EconomicCheckpointResult Evaluate(EconomicCheckpointInput input)
    {
        var budgetStop = input.BudgetUsedRatio is >= 1m;
        var timeDue = input.Elapsed >= TimeSpan.FromMinutes(8);
        if (!timeDue && !budgetStop)
        {
            return new EconomicCheckpointResult(EconomicCheckpointDecision.NotDue, "尚未到 8 分钟或预算检查点。");
        }

        if (budgetStop || input.TargetIsWrong)
        {
            return new EconomicCheckpointResult(
                EconomicCheckpointDecision.CancelAndTakeOver,
                budgetStop ? "任务预算已用尽，停止 Worker 并评估接管。" : "目标错误且成果不可采用，取消后接管。");
        }

        if (input.ScopeDriftDetected)
        {
            return new EconomicCheckpointResult(EconomicCheckpointDecision.Refine, "发现范围偏移，应在原 Worker 上定向纠偏。");
        }

        if (input.HasDeliverableProgress)
        {
            return new EconomicCheckpointResult(EconomicCheckpointDecision.Continue, "已有可交付进展，继续当前 Worker。");
        }

        return new EconomicCheckpointResult(EconomicCheckpointDecision.CancelAndTakeOver, "检查点仍无可交付进展，止损并接管。");
    }
}
