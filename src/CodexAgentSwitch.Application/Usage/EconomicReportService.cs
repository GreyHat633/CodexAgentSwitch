using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Usage;

public sealed class EconomicReportService
{
    public TaskEconomicReport Create(TaskGroupLedger ledger, IReadOnlyList<UsageSnapshot> usage)
    {
        var actualCosts = usage.Where(item => item.Cost.Evidence == EvidenceKind.Actual && item.Cost.Value is not null).ToArray();
        var estimatedCosts = usage.Where(item => item.Cost.Evidence == EvidenceKind.Estimated && item.Cost.Value is not null).ToArray();
        var cost = actualCosts.Length > 0
            ? new MeasuredDecimal(actualCosts.Sum(item => item.Cost.Value!.Value), EvidenceKind.Actual)
            : estimatedCosts.Length > 0
                ? new MeasuredDecimal(estimatedCosts.Sum(item => item.Cost.Value!.Value), EvidenceKind.Estimated)
                : new MeasuredDecimal(null, EvidenceKind.Unavailable);
        var knownTokens = usage.Where(item => item.TotalTokens.Value is not null).ToArray();
        var tokens = knownTokens.Length == 0
            ? new MeasuredLong(null, EvidenceKind.Unavailable)
            : new MeasuredLong(
                knownTokens.Sum(item => item.TotalTokens.Value!.Value),
                knownTokens.All(item => item.TotalTokens.Evidence == EvidenceKind.Actual) ? EvidenceKind.Actual : EvidenceKind.Estimated);
        var adopted = ledger.Workers.Count(worker => worker.AdoptionStatus is AdoptionStatus.Adopted or AdoptionStatus.PartiallyAdopted);
        var duplicate = ledger.Workers.Any(worker => worker.DuplicateWork);
        var conclusion = adopted == 0
            ? EconomicConclusion.PossiblyIncreased
            : duplicate
                ? EconomicConclusion.CannotDetermine
                : EconomicConclusion.PossiblySaved;
        var reason = conclusion switch
        {
            EconomicConclusion.PossiblySaved => "存在已采用 Worker 成果和实际跳过工作；没有对照实验，因此只判断为可能节省。",
            EconomicConclusion.PossiblyIncreased => "没有 Worker 成果被采用，额外请求可能增加了成本。",
            _ => "存在重复劳动或证据不足，无法判断经济收益。",
        };
        return new TaskEconomicReport(
            ledger.Id,
            $"{ledger.MainModelId} {ledger.MainReasoningEffort}",
            ledger.Workers.Select(worker => $"{worker.AdapterId} / {worker.ModelId}: {worker.AdoptionStatus}").ToArray(),
            ledger.Workers.Where(worker => !string.IsNullOrWhiteSpace(worker.ActualSkippedWork)).Select(worker => worker.ActualSkippedWork!).ToArray(),
            duplicate,
            cost,
            tokens,
            conclusion,
            reason);
    }
}
