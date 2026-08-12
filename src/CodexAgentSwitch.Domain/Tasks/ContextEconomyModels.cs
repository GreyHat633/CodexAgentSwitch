namespace CodexAgentSwitch.Domain.Tasks;

/// <summary>A bounded, deterministic hand-off snapshot for context compaction or rollover.</summary>
public sealed class CompactCheckpoint
{
    public CompactCheckpoint(
        IEnumerable<string>? completed,
        IEnumerable<string>? remaining,
        IEnumerable<string>? stableInterfaces,
        IEnumerable<string>? necessaryFiles,
        string testStatus,
        string nextPhaseEntry,
        string sourceThreadId,
        string sourceTaskId,
        DateTimeOffset timestamp)
    {
        Completed = Copy(completed);
        Remaining = Copy(remaining);
        StableInterfaces = Copy(stableInterfaces);
        NecessaryFiles = Copy(necessaryFiles);
        TestStatus = Require(testStatus, nameof(testStatus));
        NextPhaseEntry = Require(nextPhaseEntry, nameof(nextPhaseEntry));
        SourceThreadId = Require(sourceThreadId, nameof(sourceThreadId));
        SourceTaskId = Require(sourceTaskId, nameof(sourceTaskId));
        Timestamp = timestamp;
    }

    public IReadOnlyList<string> Completed { get; }
    public IReadOnlyList<string> Remaining { get; }
    public IReadOnlyList<string> StableInterfaces { get; }
    public IReadOnlyList<string> NecessaryFiles { get; }
    public string TestStatus { get; }
    public string NextPhaseEntry { get; }
    public string SourceThreadId { get; }
    public string SourceTaskId { get; }
    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset CreatedAt => Timestamp;

    /// <summary>Renders a stable replay prompt with a hard character bound.</summary>
    public string RenderReplayText(int maxCharacters = 4_000)
    {
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var text = string.Join('\n',
            "COMPACT CHECKPOINT",
            $"source-thread: {SourceThreadId}",
            $"source-task: {SourceTaskId}",
            $"timestamp: {Timestamp:O}",
            "completed:", List(Completed),
            "remaining:", List(Remaining),
            "stable-interfaces:", List(StableInterfaces),
            "necessary-files:", List(NecessaryFiles),
            $"test-status: {TestStatus}",
            $"next-phase-entry: {NextPhaseEntry}");
        if (text.Length <= maxCharacters) return text;
        const string marker = "...[truncated]";
        return maxCharacters <= marker.Length ? marker[..maxCharacters] : text[..(maxCharacters - marker.Length)] + marker;
    }

    private static IReadOnlyList<string> Copy(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>()).Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0).ToArray();
    private static string List(IReadOnlyList<string> values) => values.Count == 0 ? "- (none)" : string.Join('\n', values.Select(value => "- " + value));
    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
}

public enum SessionContextRecommendation
{
    Continue,
    Compact,
    Rollover,
}

public sealed record SessionContextBudgetInput
{
    public SessionContextBudgetInput(TimeSpan sessionAge, int turnCount, decimal mainNormalizedCost)
    {
        if (sessionAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sessionAge));
        if (turnCount < 0) throw new ArgumentOutOfRangeException(nameof(turnCount));
        if (mainNormalizedCost < 0m) throw new ArgumentOutOfRangeException(nameof(mainNormalizedCost));
        SessionAge = sessionAge;
        TurnCount = turnCount;
        MainNormalizedCost = mainNormalizedCost;
    }

    public TimeSpan SessionAge { get; }
    public int TurnCount { get; }
    public decimal MainNormalizedCost { get; }
}

public sealed record SessionContextBudgetDecision(
    SessionContextRecommendation Recommendation,
    IReadOnlyList<string> Reasons,
    SessionContextBudgetInput Input);

public sealed class SessionContextBudgetOptions
{
    public SessionContextBudgetOptions(
        TimeSpan? compactAge = null, TimeSpan? rolloverAge = null,
        int compactTurns = 20, int rolloverTurns = 40,
        decimal compactNormalizedCost = 40m, decimal rolloverNormalizedCost = 60m)
    {
        CompactAge = compactAge ?? TimeSpan.FromMinutes(20);
        RolloverAge = rolloverAge ?? TimeSpan.FromMinutes(45);
        CompactTurns = compactTurns;
        RolloverTurns = rolloverTurns;
        CompactNormalizedCost = compactNormalizedCost;
        RolloverNormalizedCost = rolloverNormalizedCost;
        if (CompactAge < TimeSpan.Zero || RolloverAge < CompactAge) throw new ArgumentException("Age thresholds must be non-decreasing.");
        if (compactTurns < 0 || rolloverTurns < compactTurns) throw new ArgumentException("Turn thresholds must be non-decreasing.");
        if (compactNormalizedCost < 0m || rolloverNormalizedCost < compactNormalizedCost) throw new ArgumentException("Cost thresholds must be non-decreasing.");
    }

    public TimeSpan CompactAge { get; }
    public TimeSpan RolloverAge { get; }
    public int CompactTurns { get; }
    public int RolloverTurns { get; }
    public decimal CompactNormalizedCost { get; }
    public decimal RolloverNormalizedCost { get; }
    public static SessionContextBudgetOptions Default { get; } = new();
}

public enum ContextPressureSource
{
    Unavailable,
    NativeInputTokens,
    NativeRenderedTokens,
    EstimatedFromInput,
    TrendOnly,
}

public enum ContextPressureBand
{
    Normal,
    Observe,
    Candidate,
    Pending,
    HardProtection,
}

public enum ContextEconomyState
{
    Idle,
    Candidate,
    PendingSafeBoundary,
    Compacting,
    Verifying,
    Cooldown,
    CompactFailed,
    Ineffective,
    VerifyDeferred,
    ContextProtectionBlocked,
}

public enum ContextEconomyAction
{
    None,
    Observe,
    MarkCandidate,
    RequireCompaction,
    HardProtect,
}

public enum CompactionEffectiveness
{
    Unknown,
    Effective,
    Marginal,
    Ineffective,
    Deferred,
}

public enum CompactionTrigger
{
    Unknown,
    AgentSwitch,
    HostAutomatic,
    ManualUser,
}

public enum ContextEconomyTransition
{
    CandidateDetected,
    MandatoryDetected,
    HardProtectionDetected,
    SafeBoundaryReached,
    CompactionAccepted,
    CompactionCompleted,
    VerificationEffective,
    VerificationMarginal,
    VerificationIneffective,
    VerificationDeferred,
    CompactionFailed,
    RetryExhausted,
    CooldownExpired,
}

public sealed class ContextEconomyOptions
{
    public bool Enabled { get; init; } = true;
    public decimal ObservePressure { get; init; } = 0.40m;
    public decimal CandidatePressure { get; init; } = 0.55m;
    public decimal MandatoryPressure { get; init; } = 0.65m;
    public decimal HardProtectionPressure { get; init; } = 0.75m;
    public decimal GrowthRatioThreshold { get; init; } = 1.80m;
    public int GrowthConsecutiveTurns { get; init; } = 3;
    public int BaselineTurns { get; init; } = 3;
    public decimal EffectiveReduction { get; init; } = 0.40m;
    public decimal MarginalReduction { get; init; } = 0.20m;
    public int CooldownMainTurns { get; init; } = 8;
    public int MaxCompactionAttemptsPerEpisode { get; init; } = 2;
    public bool AutoRollover { get; init; }
    public TimeSpan CompactionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (ObservePressure is < 0 or > 1
            || CandidatePressure < ObservePressure
            || MandatoryPressure < CandidatePressure
            || HardProtectionPressure < MandatoryPressure
            || HardProtectionPressure > 1)
            throw new InvalidOperationException("Context pressure thresholds must be ordered within 0..1.");
        if (GrowthRatioThreshold <= 1 || GrowthConsecutiveTurns <= 0 || BaselineTurns is < 2 or > 3)
            throw new InvalidOperationException("Growth policy is invalid.");
        if (MarginalReduction is < 0 or > 1 || EffectiveReduction < MarginalReduction || EffectiveReduction > 1)
            throw new InvalidOperationException("Effectiveness thresholds are invalid.");
        if (CooldownMainTurns < 0 || MaxCompactionAttemptsPerEpisode is < 1 or > 2 || CompactionTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Compaction protection settings are invalid.");
        if (AutoRollover)
            throw new InvalidOperationException("Automatic rollover is disabled in 0.2.5.");
    }
}

public sealed record ContextTurnSample(
    long InputTokens,
    long CachedInputTokens,
    long? RenderedContextTokens = null,
    long? ContextWindowTokens = null,
    bool IsNormalMainTurn = true,
    bool IsLargeNewContext = false,
    DateTimeOffset? CapturedAt = null,
    long? NativeInputTokens = null);

public sealed record ContextPressureTelemetry(
    decimal? Pressure,
    ContextPressureSource Source,
    decimal? BaselineInput,
    decimal? GrowthRatio,
    int ConsecutiveGrowthTurns,
    long CurrentInput,
    long CurrentCachedInput);

public sealed record ContextEconomyDecision(
    ContextPressureBand Band,
    ContextEconomyAction Action,
    ContextPressureTelemetry Telemetry,
    bool CooldownSuppressed,
    string Reason);

public sealed record CompactionEffectivenessResult(
    CompactionEffectiveness Classification,
    decimal? Reduction,
    decimal? PreInputMedian,
    decimal? PostInputMedian,
    decimal? PreCachedMedian,
    decimal? PostCachedMedian,
    string Reason);

/// <summary>Durable per-thread context-economy state. Samples are retained to make a restart safe.</summary>
public sealed record ContextEconomySnapshot(
    string ThreadId,
    ContextEconomyState State,
    int Attempts,
    int CooldownRemaining,
    IReadOnlyList<ContextTurnSample> Samples,
    IReadOnlyList<ContextTurnSample> PreCompactionSamples,
    string? LastReason = null,
    IReadOnlyList<ContextTurnSample>? PostCompactionSamples = null,
    DateTimeOffset? UpdatedAt = null,
    CompactionTrigger LastCompactionTrigger = CompactionTrigger.Unknown,
    DateTimeOffset? StructuredCompactedAt = null,
    decimal? PreCompactionPressure = null,
    long? PreCompactionInput = null,
    decimal? PostCompactionPressure = null,
    long? PostCompactionInput = null,
    CompactionEffectivenessResult? LastEffectiveness = null)
{
    public ContextEconomySnapshot Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ThreadId);
        return this with
        {
            Samples = Samples?.ToArray() ?? Array.Empty<ContextTurnSample>(),
            PreCompactionSamples = PreCompactionSamples?.ToArray() ?? Array.Empty<ContextTurnSample>(),
            PostCompactionSamples = PostCompactionSamples?.ToArray() ?? Array.Empty<ContextTurnSample>(),
            Attempts = Math.Max(0, Attempts),
            CooldownRemaining = Math.Max(0, CooldownRemaining),
        };
    }
}

public sealed record ContextEconomyObservationResult(
    ContextEconomyDecision Decision,
    ContextEconomyState State,
    bool CompactionRequested,
    ContextEconomyCompactionResult? Compaction = null);

public sealed record ContextEconomyCompactionResult(
    bool Succeeded,
    bool RequestAcknowledged,
    bool TerminalLifecycleObserved,
    int Attempt,
    ContextEconomyState State,
    CompactionEffectivenessResult? Effectiveness,
    string Reason);

public sealed record StructuredCompactionObservation(
    string ThreadId,
    CompactionTrigger Trigger,
    DateTimeOffset CompactedAt,
    ContextEconomyState State,
    bool DuplicateSuppressed,
    string Reason);
