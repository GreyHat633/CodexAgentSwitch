using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

public sealed class ContextPressureEstimator(ContextEconomyOptions? options = null)
{
    private readonly ContextEconomyOptions options = Validate(options ?? new ContextEconomyOptions());

    public ContextPressureTelemetry Estimate(
        ContextTurnSample current,
        IReadOnlyList<ContextTurnSample>? baselineTurns = null,
        IReadOnlyList<ContextTurnSample>? recentTurns = null)
    {
        decimal? pressure = null;
        var source = ContextPressureSource.Unavailable;
        if (current.RenderedContextTokens is > 0 && current.ContextWindowTokens is > 0)
        {
            pressure = Ratio(current.RenderedContextTokens.Value, current.ContextWindowTokens.Value);
            source = ContextPressureSource.NativeRenderedTokens;
        }
        else if (current.InputTokens > 0 && current.ContextWindowTokens is > 0)
        {
            pressure = Ratio(current.InputTokens, current.ContextWindowTokens.Value);
            source = ContextPressureSource.EstimatedFromInput;
        }

        var baseline = NormalInputs(baselineTurns).Take(options.BaselineTurns).ToArray();
        decimal? baselineMedian = baseline.Length >= 2 ? Median(baseline) : null;
        decimal? growthRatio = baselineMedian is > 0 && current.InputTokens > 0
            ? current.InputTokens / baselineMedian.Value
            : null;
        var growthCount = baselineMedian is > 0
            ? (recentTurns ?? [])
                .Reverse()
                .TakeWhile(sample => sample.IsNormalMainTurn
                    && !sample.IsLargeNewContext
                    && sample.InputTokens > baselineMedian.Value * options.GrowthRatioThreshold)
                .Count()
            : 0;
        if (source == ContextPressureSource.Unavailable && baselineMedian is not null)
            source = ContextPressureSource.TrendOnly;

        return new(
            pressure,
            source,
            baselineMedian,
            growthRatio,
            growthCount,
            Math.Max(0, current.InputTokens),
            Math.Max(0, current.CachedInputTokens));
    }

    private static IEnumerable<long> NormalInputs(IReadOnlyList<ContextTurnSample>? samples) =>
        (samples ?? []).Where(sample => sample.IsNormalMainTurn && !sample.IsLargeNewContext && sample.InputTokens > 0)
            .Select(sample => sample.InputTokens);

    internal static decimal Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2m : ordered[middle];
    }

    private static decimal Ratio(long value, long window) => Math.Clamp(value / (decimal)window, 0m, 1m);
    private static ContextEconomyOptions Validate(ContextEconomyOptions value) { value.Validate(); return value; }
}

public sealed class ContextEconomyPolicy(ContextEconomyOptions? options = null)
{
    private readonly ContextEconomyOptions options = Validate(options ?? new ContextEconomyOptions());

    public ContextEconomyDecision Evaluate(ContextPressureTelemetry telemetry, int cooldownRemaining = 0)
    {
        var band = telemetry.Pressure switch
        {
            var value when value >= options.HardProtectionPressure => ContextPressureBand.HardProtection,
            var value when value >= options.MandatoryPressure => ContextPressureBand.Pending,
            var value when value >= options.CandidatePressure => ContextPressureBand.Candidate,
            var value when value >= options.ObservePressure => ContextPressureBand.Observe,
            _ => ContextPressureBand.Normal,
        };
        if (band < ContextPressureBand.Candidate
            && telemetry.ConsecutiveGrowthTurns >= options.GrowthConsecutiveTurns)
            band = ContextPressureBand.Candidate;

        var action = band switch
        {
            ContextPressureBand.HardProtection => ContextEconomyAction.HardProtect,
            ContextPressureBand.Pending => ContextEconomyAction.RequireCompaction,
            ContextPressureBand.Candidate => ContextEconomyAction.MarkCandidate,
            ContextPressureBand.Observe => ContextEconomyAction.Observe,
            _ => ContextEconomyAction.None,
        };
        var suppressed = cooldownRemaining > 0 && band is not ContextPressureBand.HardProtection;
        if (suppressed) action = ContextEconomyAction.None;
        var reason = telemetry.Pressure is not null
            ? $"pressure={telemetry.Pressure:0.####}; source={telemetry.Source}"
            : telemetry.ConsecutiveGrowthTurns >= options.GrowthConsecutiveTurns
                ? $"growth={telemetry.GrowthRatio:0.####}; consecutive={telemetry.ConsecutiveGrowthTurns}"
                : $"pressure unavailable; source={telemetry.Source}";
        return new(band, action, telemetry, suppressed, reason);
    }

    private static ContextEconomyOptions Validate(ContextEconomyOptions value) { value.Validate(); return value; }
}

public sealed class CompactionEffectivenessEvaluator(ContextEconomyOptions? options = null)
{
    private readonly ContextEconomyOptions options = Validate(options ?? new ContextEconomyOptions());

    public CompactionEffectivenessResult Evaluate(
        IReadOnlyList<ContextTurnSample> pre,
        IReadOnlyList<ContextTurnSample> post)
    {
        if (post.Any(sample => sample.IsLargeNewContext))
            return new(CompactionEffectiveness.Deferred, null, null, null, null, null,
                "Post-compaction verification includes large new context.");

        var preNormal = pre.Where(IsUsable).TakeLast(3).ToArray();
        var postNormal = post.Where(IsUsable).Take(3).ToArray();
        if (preNormal.Length < 2 || postNormal.Length < 2)
            return new(CompactionEffectiveness.Unknown, null, null, null, null, null,
                "At least two normal pre and post turns are required.");

        var preInput = ContextPressureEstimator.Median(preNormal.Select(sample => sample.InputTokens));
        var postInput = ContextPressureEstimator.Median(postNormal.Select(sample => sample.InputTokens));
        var preCached = ContextPressureEstimator.Median(preNormal.Select(sample => sample.CachedInputTokens));
        var postCached = ContextPressureEstimator.Median(postNormal.Select(sample => sample.CachedInputTokens));
        var reduction = preInput <= 0 ? 0 : Math.Clamp((preInput - postInput) / preInput, -1m, 1m);
        var classification = reduction >= options.EffectiveReduction
            ? CompactionEffectiveness.Effective
            : reduction >= options.MarginalReduction
                ? CompactionEffectiveness.Marginal
                : CompactionEffectiveness.Ineffective;
        return new(classification, reduction, preInput, postInput, preCached, postCached,
            $"input reduction={reduction:P1}");
    }

    private static bool IsUsable(ContextTurnSample sample) =>
        sample.IsNormalMainTurn && !sample.IsLargeNewContext && sample.InputTokens > 0;
    private static ContextEconomyOptions Validate(ContextEconomyOptions value) { value.Validate(); return value; }
}

public sealed class CompactionStateMachine
{
    public ContextEconomyState Transition(ContextEconomyState current, ContextEconomyTransition transition) =>
        (current, transition) switch
        {
            (_, ContextEconomyTransition.HardProtectionDetected) => ContextEconomyState.PendingSafeBoundary,
            (ContextEconomyState.Idle, ContextEconomyTransition.CandidateDetected) => ContextEconomyState.Candidate,
            (ContextEconomyState.Idle or ContextEconomyState.Candidate, ContextEconomyTransition.MandatoryDetected) => ContextEconomyState.PendingSafeBoundary,
            (ContextEconomyState.Candidate, ContextEconomyTransition.SafeBoundaryReached) => ContextEconomyState.Compacting,
            (ContextEconomyState.PendingSafeBoundary, ContextEconomyTransition.SafeBoundaryReached) => ContextEconomyState.Compacting,
            (ContextEconomyState.Compacting, ContextEconomyTransition.CompactionAccepted) => ContextEconomyState.Compacting,
            (ContextEconomyState.Compacting, ContextEconomyTransition.CompactionCompleted) => ContextEconomyState.Verifying,
            (ContextEconomyState.Verifying, ContextEconomyTransition.VerificationEffective) => ContextEconomyState.Cooldown,
            (ContextEconomyState.Verifying, ContextEconomyTransition.VerificationMarginal) => ContextEconomyState.Cooldown,
            (ContextEconomyState.Verifying, ContextEconomyTransition.VerificationIneffective) => ContextEconomyState.Ineffective,
            (ContextEconomyState.Verifying, ContextEconomyTransition.VerificationDeferred) => ContextEconomyState.VerifyDeferred,
            (ContextEconomyState.Compacting, ContextEconomyTransition.CompactionFailed) => ContextEconomyState.CompactFailed,
            (ContextEconomyState.CompactFailed or ContextEconomyState.Ineffective, ContextEconomyTransition.RetryExhausted) => ContextEconomyState.ContextProtectionBlocked,
            (ContextEconomyState.Cooldown, ContextEconomyTransition.CooldownExpired) => ContextEconomyState.Idle,
            _ => current,
        };
}
