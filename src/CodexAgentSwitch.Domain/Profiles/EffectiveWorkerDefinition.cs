namespace CodexAgentSwitch.Domain.Profiles;

/// <summary>
/// The exact worker identity a profile asks a Codex host to configure.  This
/// is deliberately separate from WorkerPolicy: a policy expresses intent,
/// while this definition makes the concrete agent role and its capability
/// explicit so the UI cannot describe one worker while Codex runs another.
/// </summary>
public enum EffectiveWorkerKind
{
    None,
    NativeAgent,
    ExternalAgent,
}

public enum WorkerExecutionCapability
{
    Supported,
    Unsupported,
    Unverified,
    Misconfigured,
}

public sealed record EffectiveWorkerDefinition(
    EffectiveWorkerKind Kind,
    string? AgentRole,
    string? ModelId,
    string? ReasoningEffort,
    string? ProviderId,
    int MaxWorkers,
    RoutingMode RoutingMode,
    string? ConfigFile,
    WorkerExecutionCapability Capability,
    string CapabilityMessage)
{
    public bool CanRunInNativeCodex => Capability == WorkerExecutionCapability.Supported;

    public static EffectiveWorkerDefinition Resolve(WorkerPolicy policy)
    {
        if (!policy.Enabled || policy.Source == WorkerSource.Disabled)
        {
            return new(
                EffectiveWorkerKind.None, null, null, null, null, 0, policy.RoutingMode, null,
                WorkerExecutionCapability.Supported,
                "未启用 Worker。");
        }

        if (policy.Source == WorkerSource.ExternalProvider)
        {
            // Current native Codex collaboration encrypts the delegated task
            // payload.  The custom OpenAI-compatible provider receives the
            // call but not the model-visible bounded task.  Agent Switch is
            // only a configuration host on this path and has no supported
            // plaintext bridge, so never advertise it as runnable.
            return new(
                EffectiveWorkerKind.ExternalAgent,
                "cas_external_worker",
                null,
                null,
                policy.PreferredProviderId,
                Math.Max(1, policy.MaxWorkers),
                policy.RoutingMode,
                null,
                WorkerExecutionCapability.Unsupported,
                "当前 Codex Native 模式无法可靠地将委派任务正文传递给外部 Provider Worker；请在 CodexAgentSwitch 模式中使用外部 Worker。");
        }

        (string Role, string Model, string File)? native = policy.PreferredProviderId switch
        {
            "native-sol" => ("cas_sol_worker", "gpt-5.6-sol", "agents/cas-sol-worker.toml"),
            "native-terra" => ("cas_terra_worker", "gpt-5.6-terra", "agents/cas-terra-worker.toml"),
            "native-luna" => ("cas_luna_worker", "gpt-5.6-luna", "agents/cas-luna-worker.toml"),
            _ => null,
        };

        return native is null
            ? new(
                EffectiveWorkerKind.NativeAgent, null, null, null, "native-codex", Math.Max(1, policy.MaxWorkers),
                policy.RoutingMode, null, WorkerExecutionCapability.Misconfigured,
                "原生 Worker 标识无效，未生成默认或回退 Worker。")
            : new(
                EffectiveWorkerKind.NativeAgent,
                native.Value.Role,
                native.Value.Model,
                NormalizeReasoning(policy.ReasoningEffort),
                "openai",
                Math.Max(1, policy.MaxWorkers),
                policy.RoutingMode,
                native.Value.File,
                WorkerExecutionCapability.Supported,
                "通过项目级自定义 Agent 角色执行。");
    }

    private static string NormalizeReasoning(string? effort) => effort?.Trim() switch
    {
        "low" or "medium" or "high" or "xhigh" => effort.Trim(),
        _ => "medium",
    };
}
