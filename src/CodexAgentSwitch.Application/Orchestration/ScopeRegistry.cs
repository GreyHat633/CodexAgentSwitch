using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Orchestration;

public sealed class ScopeRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, DelegatedScope> scopes = new(StringComparer.Ordinal);

    public IReadOnlyList<DelegatedScope> Active
    {
        get
        {
            lock (sync)
            {
                return scopes.Values.Where(item => item.Status == DelegationScopeStatus.Active).ToArray();
            }
        }
    }

    public ScopeAccessDecision CanRegister(WorkerScope candidate)
    {
        lock (sync)
        {
            var conflicts = scopes.Values
                .Where(item => item.Status == DelegationScopeStatus.Active)
                .Where(item => ScopeOverlap(item.Scope, candidate))
                .Where(item => HasWrite(item.Scope) || HasWrite(candidate))
                .Select(item => item.JobId)
                .ToArray();
            return conflicts.Length == 0
                ? new ScopeAccessDecision(ScopeAccessDecisionKind.Allowed, "Scope 可注册。", [])
                : new ScopeAccessDecision(ScopeAccessDecisionKind.Blocked, "文件范围重叠且至少一方包含写入，禁止并发。", conflicts);
        }
    }

    public void Register(DelegatedScope scope)
    {
        lock (sync)
        {
            if (scopes.ContainsKey(scope.JobId))
            {
                throw new InvalidOperationException($"Scope 已存在：{scope.JobId}");
            }

            var decision = CanRegister(scope.Scope);
            if (decision.Kind == ScopeAccessDecisionKind.Blocked)
            {
                throw new InvalidOperationException(decision.Message);
            }

            scopes.Add(scope.JobId, scope);
        }
    }

    public void Complete(string jobId, DelegationScopeStatus status)
    {
        if (status == DelegationScopeStatus.Active)
        {
            throw new ArgumentException("Terminal Scope status is required.", nameof(status));
        }

        lock (sync)
        {
            if (!scopes.TryGetValue(jobId, out var current))
            {
                throw new KeyNotFoundException($"Scope 不存在：{jobId}");
            }

            scopes[jobId] = current with { Status = status };
        }
    }

    public ScopeAccessDecision CheckMainAgentAccess(WorkerScope proposed, ScopeAccessIntent intent, bool rejectedForFullTakeover = false)
    {
        lock (sync)
        {
            var conflicts = scopes.Values
                .Where(item => item.Status == DelegationScopeStatus.Active && ScopeOverlap(item.Scope, proposed))
                .Select(item => item.JobId)
                .ToArray();
            if (conflicts.Length == 0)
            {
                return new ScopeAccessDecision(ScopeAccessDecisionKind.Allowed, "未与活动委派重叠。", []);
            }

            if (intent == ScopeAccessIntent.DirectedSpotCheck)
            {
                return new ScopeAccessDecision(ScopeAccessDecisionKind.Allowed, "允许定向抽查；需记录为审查，不计为完整重复。", conflicts);
            }

            if (intent == ScopeAccessIntent.FullTakeover)
            {
                return rejectedForFullTakeover
                    ? new ScopeAccessDecision(ScopeAccessDecisionKind.Allowed, "Worker 结果已 rejected，允许完整接管。", conflicts)
                    : new ScopeAccessDecision(ScopeAccessDecisionKind.Blocked, "只有 rejected 后才能完整接管委派范围。", conflicts);
            }

            if (intent == ScopeAccessIntent.Modify || proposed.Operations.Contains(ScopeOperation.Modify))
            {
                return new ScopeAccessDecision(ScopeAccessDecisionKind.Blocked, "委派范围存在活动 Worker，主代理写入会造成文件冲突。", conflicts);
            }

            return new ScopeAccessDecision(ScopeAccessDecisionKind.WarningRequiresConfirmation, "操作与已委派范围重叠；仅允许等待、定向抽查、取消接管或记录重复原因。", conflicts);
        }
    }

    public static bool ScopeOverlap(WorkerScope left, WorkerScope right)
    {
        var fileOverlap = left.Files.Any(leftPath => right.Files.Any(rightPath => PathOverlap(leftPath, rightPath)));
        var moduleOverlap = left.Modules.Any(leftModule => right.Modules.Any(rightModule =>
            string.Equals(leftModule.Trim(), rightModule.Trim(), StringComparison.OrdinalIgnoreCase)));
        return fileOverlap || moduleOverlap;
    }

    private static bool PathOverlap(string left, string right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
            || normalizedLeft.StartsWith(normalizedRight + '/', StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(normalizedLeft + '/', StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    private static bool HasWrite(WorkerScope scope) => scope.Operations.Contains(ScopeOperation.Modify);
}
