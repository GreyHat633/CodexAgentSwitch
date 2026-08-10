using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Application.Orchestration;

public sealed class DelegationGate(ScopeRegistry scopeRegistry)
{
    public DelegationGateResult Validate(DelegationRequest request, DelegationGateContext context)
    {
        var issues = new List<GateIssue>();
        Require(request.TaskGroupId, "delegation.group.required", "Task Group ID 必填。", issues);
        Require(request.TaskId, "delegation.task.required", "Task ID 必填。", issues);
        Require(request.Objective, "delegation.objective.required", "目标必须明确，不能把模糊探索整体委派。", issues);
        Require(request.SolWillSkip, "delegation.skip.required", "必须说明 Worker 成功后主代理将跳过什么工作。", issues);
        if (request.Scope.Files.Count == 0 && request.Scope.Modules.Count == 0)
        {
            Error("delegation.scope.required", "必须提供明确文件或模块范围。", issues);
        }
        else if (request.Scope.Files.Any(IsBroadPath))
        {
            Error("delegation.scope.too_broad", "Scope 不能使用工作区根目录或全局通配符。", issues);
        }

        if (request.Scope.Operations.Count == 0)
        {
            Error("delegation.operations.required", "Scope 必须声明可观察操作类型。", issues);
        }

        if (request.Deliverables.Count == 0 || request.Deliverables.Any(string.IsNullOrWhiteSpace))
        {
            Error("delegation.deliverables.required", "至少需要一个具体交付物。", issues);
        }

        if (request.AcceptanceCriteria.Count == 0 || request.AcceptanceCriteria.Any(string.IsNullOrWhiteSpace))
        {
            Error("delegation.acceptance.required", "至少需要一个可复核验收条件。", issues);
        }

        var routingLimit = context.RoutingMode switch
        {
            RoutingMode.Economic => 1,
            RoutingMode.Balanced => 2,
            RoutingMode.Performance => 3,
            RoutingMode.Manual => 3,
            _ => 0,
        };
        var effectiveLimit = Math.Min(Math.Min(routingLimit, context.ProfileMaxWorkers), Math.Max(1, context.MaxActiveWorkers));
        if (request.RequestedWorkers < 1 || request.RequestedWorkers + context.ActiveWorkers > effectiveLimit)
        {
            Error("delegation.workers.limit", $"当前路由最多允许 {effectiveLimit} 个 Worker。", issues);
        }

        if (!context.ProviderAvailable)
        {
            Error("delegation.provider.unavailable", "首选 Provider 当前不可用。", issues);
        }

        if (!context.WithinBudget)
        {
            Error("delegation.budget.exceeded", "预算门槛不允许创建新 Worker。", issues);
        }

        if (context.HighDuplicateRisk)
        {
            Error("delegation.duplicate.high", "替代性不足，预计会产生高重复劳动。", issues);
        }

        var scopeDecision = scopeRegistry.CanRegister(request.Scope);
        if (scopeDecision.Kind == ScopeAccessDecisionKind.Blocked)
        {
            Error("delegation.scope.conflict", scopeDecision.Message, issues);
        }

        return new DelegationGateResult(issues);
    }

    private static bool IsBroadPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').TrimEnd('/');
        return normalized is "" or "." or "*" or "**" or "**/*" or "/";
    }

    private static void Require(string value, string code, string message, ICollection<GateIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Error(code, message, issues);
        }
    }

    private static void Error(string code, string message, ICollection<GateIssue> issues) => issues.Add(new GateIssue(code, message, GateSeverity.Error));
}
