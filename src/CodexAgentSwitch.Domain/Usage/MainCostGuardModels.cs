using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Domain.Usage;

/// <summary>
/// Configurable normalized-credit weights.  Rates are credits per million
/// tokens, and are deliberately independent of a provider or model.
/// </summary>
public sealed class NormalizedCostWeights
{
    public NormalizedCostWeights()
        : this(1m, 1m, 1m)
    {
    }

    public NormalizedCostWeights(
        decimal uncachedInputPerMillion,
        decimal cachedInputPerMillion,
        decimal outputPerMillion)
    {
        if (uncachedInputPerMillion < 0m) throw new ArgumentOutOfRangeException(nameof(uncachedInputPerMillion));
        if (cachedInputPerMillion < 0m) throw new ArgumentOutOfRangeException(nameof(cachedInputPerMillion));
        if (outputPerMillion < 0m) throw new ArgumentOutOfRangeException(nameof(outputPerMillion));
        UncachedInputPerMillion = uncachedInputPerMillion;
        CachedInputPerMillion = cachedInputPerMillion;
        OutputPerMillion = outputPerMillion;
    }

    public decimal UncachedInputPerMillion { get; }
    public decimal CachedInputPerMillion { get; }
    public decimal OutputPerMillion { get; }

    public static NormalizedCostWeights Default { get; } = new();
}

/// <summary>Policy for the bounded MAIN cost guard.</summary>
public sealed class MainCostGuardOptions
{
    public MainCostGuardOptions()
        : this(null, null)
    {
    }

    public MainCostGuardOptions(
        IReadOnlyList<decimal>? windowThresholds,
        NormalizedCostWeights? costWeights)
    {
        var thresholds = (windowThresholds ?? [25m, 40m, 60m]).ToArray();
        if (thresholds.Length == 0) throw new ArgumentException("At least one cost window threshold is required.", nameof(windowThresholds));
        if (thresholds.Any(value => value <= 0m)) throw new ArgumentOutOfRangeException(nameof(windowThresholds));
        if (thresholds.Select((value, index) => (value, index)).Any(item => item.index > 0 && item.value < thresholds[item.index - 1]))
        {
            throw new ArgumentException("Cost window thresholds must be non-decreasing.", nameof(windowThresholds));
        }

        WindowThresholds = thresholds;
        CostWeights = costWeights ?? NormalizedCostWeights.Default;
    }

    public IReadOnlyList<decimal> WindowThresholds { get; }
    public NormalizedCostWeights CostWeights { get; }
    public decimal InitialThreshold => WindowThresholds[0];

    public static MainCostGuardOptions Default { get; } = new();
}

/// <summary>Monotonic token delta accepted by the cost guard.</summary>
public sealed record MainCostGuardUsageDelta(
    string SessionId,
    long UncachedInputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long Calls,
    decimal NormalizedCredits)
{
    public long InputTokens => UncachedInputTokens + CachedInputTokens;
    public long TotalChargedTokens => InputTokens + OutputTokens;
}

/// <summary>Model-visible state of the MAIN cost guard.</summary>
public sealed record MainCostGuardTelemetry(
    decimal CurrentWindowCredits,
    decimal CurrentThreshold,
    int BackoffStage,
    decimal SessionCumulativeNormalizedCredits,
    int GuardHitCount,
    string? LastCheckpoint,
    RepartitionReasonCode? LastReason,
    bool GuardHit);
