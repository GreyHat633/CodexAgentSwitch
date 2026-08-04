namespace CodexAgentSwitch.Domain.Profiles;

public sealed record AgentSelection(string ModelId, string ReasoningEffort);

public enum WorkerSource
{
    Disabled,
    NativeCodex,
    ExternalProvider,
}

public enum RoutingMode
{
    Economic,
    Balanced,
    Performance,
    Manual,
    Single,
}

public enum FallbackAction
{
    NativeLuna,
    SingleAgent,
    AskUser,
    StopDelegation,
}

public enum ExecutionApprovalMode
{
    Automatic,
    Safe,
    FullAuto,
}

public sealed record ExecutionApprovalSettings(string ApprovalPolicy, string SandboxMode);

public static class ExecutionApprovalPolicy
{
    public static ExecutionApprovalSettings Resolve(ExecutionApprovalMode mode) => mode switch
    {
        ExecutionApprovalMode.Safe => new("untrusted", "read-only"),
        ExecutionApprovalMode.FullAuto => new("never", "danger-full-access"),
        _ => new("on-request", "workspace-write"),
    };
}

public sealed record WorkerPolicy(
    bool Enabled,
    WorkerSource Source,
    string? PreferredProviderId,
    string? FallbackProviderId,
    int MaxWorkers,
    RoutingMode RoutingMode,
    FallbackAction FallbackAction);

public sealed record BudgetLimits(
    decimal? PerTask,
    decimal? Daily,
    decimal? Monthly,
    long? TokenLimit,
    int? RequestLimit,
    string Currency);

public sealed record Profile(
    Guid Id,
    string Name,
    AgentSelection MainAgent,
    WorkerPolicy WorkerPolicy,
    BudgetLimits Budget,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Persisted profile payload schema. Values before 2 are normalized by the
    /// one-time profile migration before any page constructs an editor.
    /// </summary>
    public int SchemaVersion { get; init; }

    public bool IsBuiltIn { get; init; }

    public ExecutionApprovalMode ApprovalMode { get; init; } = ExecutionApprovalMode.Automatic;

    /// <summary>
    /// A localized, non-sensitive reason that prevents a malformed legacy
    /// payload from being edited or activated. It is intentionally surfaced in
    /// the UI instead of allowing a binding exception to terminate the app.
    /// </summary>
    public string? RepairMessage { get; init; }

    public bool RequiresRepair => !string.IsNullOrWhiteSpace(RepairMessage);

    public static bool IsBuiltInPresetName(string? name) =>
        string.Equals(name?.Trim(), "经济模式", StringComparison.OrdinalIgnoreCase);

    public string KindLabel => RequiresRepair ? "需要修复" : IsBuiltIn ? "内置预设" : "用户方案";

    public string DefaultLabel => IsDefault ? "当前" : string.Empty;

    public static Profile CreateDefault(DateTimeOffset now) => new(
        Guid.NewGuid(),
        "经济模式",
        new AgentSelection("gpt-5.6-sol", "high"),
        new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
        new BudgetLimits(0.5m, 3m, 30m, null, null, "CNY"),
        true,
        now,
        now,
        null)
    {
        IsBuiltIn = true,
        SchemaVersion = CurrentSchemaVersion,
    };
}
