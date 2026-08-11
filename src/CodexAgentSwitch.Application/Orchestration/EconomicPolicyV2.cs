using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Application.Orchestration;

public sealed class EconomicPolicyV2
{
    public const int DefaultMaxActiveWorkers = 1;

    private const int DelegationMajority = 4;

    public EconomicPolicyDecision Evaluate(TaskRiskLevel riskLevel) => riskLevel switch
    {
        TaskRiskLevel.Low => Decision(
            riskLevel,
            workerOwnsClosedLoop: true,
            solLeads: false,
            ReviewBudget.Minimal,
            ReviewLevel.R0,
            "LOW 工作包由 Worker 完成实现、构建、测试和范围内修复；Sol 只做最小验收。"),
        TaskRiskLevel.Medium => Decision(
            riskLevel,
            workerOwnsClosedLoop: true,
            solLeads: false,
            ReviewBudget.Focused,
            ReviewLevel.R1,
            "MEDIUM 工作包由 Worker 完整闭环；Sol 聚焦接口、状态流、关键 diff 和验证证据。"),
        _ => Decision(
            TaskRiskLevel.High,
            workerOwnsClosedLoop: false,
            solLeads: true,
            ReviewBudget.Deep,
            ReviewLevel.R2,
            "HIGH 工作包由 Sol 主导，Worker 仅承担明确、隔离且可独立复核的子包。"),
    };

    /// <summary>
    /// Re-evaluate ownership for the package in front of us.  Risk is read
    /// from this package on every call; a previous package's decision is never
    /// carried forward.  Four of the seven positive signals are sufficient,
    /// except that trivial-overhead and high-risk work always remains with
    /// MAIN.
    /// </summary>
    public RepartitionDecision EvaluateRepartition(RepartitionWorkPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        package.Validate();

        var count = package.PositiveConditionCount;
        var workerPreferred =
            package.RiskLevel is TaskRiskLevel.Low or TaskRiskLevel.Medium &&
            !package.TrivialOverhead &&
            count >= DelegationMajority;

        var owner = workerPreferred ? WorkOwner.Worker : WorkOwner.Main;
        var reason = workerPreferred
            ? package.PreferredWorkerReason
            : MainReason(package, count);
        return new RepartitionDecision(
            owner,
            package.RiskLevel,
            count,
            workerPreferred,
            reason);
    }

    public RepartitionDecision EvaluateRepartition(
        CurrentWorkState currentWork,
        RepartitionWorkPackage package) => EvaluateRepartition(package);

    private static RepartitionReasonCode MainReason(RepartitionWorkPackage package, int count)
    {
        if (package.TrivialOverhead)
        {
            return RepartitionReasonCode.TOO_SMALL_TO_DELEGATE;
        }

        if (!package.Capable)
        {
            return RepartitionReasonCode.WORKER_CAPABILITY_MISSING;
        }

        if (package.RiskLevel == TaskRiskLevel.High)
        {
            return RepartitionReasonCode.REVIEW_REQUIRED;
        }

        if (!package.Stable)
        {
            return RepartitionReasonCode.ARCHITECTURE_UNRESOLVED;
        }

        if (!package.Bounded)
        {
            return RepartitionReasonCode.INVESTIGATION_UNRESOLVED;
        }

        if (!package.NonOverlapping)
        {
            return RepartitionReasonCode.CROSS_MODULE_DECISION;
        }

        return count < DelegationMajority
            ? RepartitionReasonCode.FINAL_INTEGRATION
            : RepartitionReasonCode.REVIEW_REQUIRED;
    }

    public WorkerEscalation Escalate(
        string taskId,
        WorkerEscalationKind kind,
        string reason,
        IReadOnlyList<string> evidence,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Task ID 必填。", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Escalation 必须说明原因。", nameof(reason));
        }

        return new WorkerEscalation(taskId.Trim(), kind, reason.Trim(), evidence, now);
    }

    public SolContextCheckpoint CreateCheckpoint(
        string head,
        IReadOnlyList<string> completed,
        IReadOnlyList<string> pending,
        IReadOnlyList<string> architectureDecisions,
        IReadOnlyList<string> knownRisks,
        string nextStep,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(head) || string.IsNullOrWhiteSpace(nextStep))
        {
            throw new ArgumentException("Checkpoint 必须包含 HEAD 与 Next Step。");
        }

        return new SolContextCheckpoint(
            head.Trim(),
            completed,
            pending,
            architectureDecisions,
            knownRisks,
            nextStep.Trim(),
            now);
    }

    private static EconomicPolicyDecision Decision(
        TaskRiskLevel riskLevel,
        bool workerOwnsClosedLoop,
        bool solLeads,
        ReviewBudget reviewBudget,
        ReviewLevel reviewLevel,
        string reason) => new(
            riskLevel,
            workerOwnsClosedLoop,
            solLeads,
            reviewBudget,
            reviewLevel,
            DefaultMaxActiveWorkers,
            CompactResultRequired: true,
            DuplicateImplementationAllowed: false,
            reason);
}
