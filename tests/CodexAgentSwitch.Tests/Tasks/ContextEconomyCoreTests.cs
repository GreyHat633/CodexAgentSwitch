using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ContextEconomyCoreTests
{
    [Theory]
    [InlineData(0.39, ContextPressureBand.Normal, ContextEconomyAction.None)]
    [InlineData(0.40, ContextPressureBand.Observe, ContextEconomyAction.Observe)]
    [InlineData(0.55, ContextPressureBand.Candidate, ContextEconomyAction.MarkCandidate)]
    [InlineData(0.65, ContextPressureBand.Pending, ContextEconomyAction.RequireCompaction)]
    [InlineData(0.75, ContextPressureBand.HardProtection, ContextEconomyAction.HardProtect)]
    public void Pressure_thresholds_have_exact_initial_behavior(
        double pressure,
        ContextPressureBand expectedBand,
        ContextEconomyAction expectedAction)
    {
        var telemetry = new ContextPressureTelemetry((decimal)pressure, ContextPressureSource.NativeRenderedTokens,
            null, null, 0, 1, 1);
        var result = new ContextEconomyPolicy().Evaluate(telemetry);
        Assert.Equal(expectedBand, result.Band);
        Assert.Equal(expectedAction, result.Action);
    }

    [Fact]
    public void Native_context_wins_and_input_estimate_is_explicit()
    {
        var estimator = new ContextPressureEstimator();
        var native = estimator.Estimate(new ContextTurnSample(90, 70, 60, 100));
        Assert.Equal(0.60m, native.Pressure);
        Assert.Equal(ContextPressureSource.NativeRenderedTokens, native.Source);

        var estimated = estimator.Estimate(new ContextTurnSample(55, 40, null, 100));
        Assert.Equal(0.55m, estimated.Pressure);
        Assert.Equal(ContextPressureSource.EstimatedFromInput, estimated.Source);
    }

    [Fact]
    public void Native_jsonl_input_and_window_have_priority_over_estimates()
    {
        var telemetry = new ContextPressureEstimator().Estimate(
            new ContextTurnSample(
                218_856,
                180_000,
                RenderedContextTokens: null,
                ContextWindowTokens: 258_400,
                NativeInputTokens: 218_856));

        Assert.Equal(ContextPressureSource.NativeInputTokens, telemetry.Source);
        Assert.Equal(218_856m / 258_400m, telemetry.Pressure);
        Assert.Equal(ContextPressureBand.HardProtection, new ContextEconomyPolicy().Evaluate(telemetry).Band);
    }

    [Fact]
    public void Trend_only_never_fabricates_a_percentage()
    {
        var baseline = new[] { Turn(100), Turn(110), Turn(90) };
        var telemetry = new ContextPressureEstimator().Estimate(Turn(190), baseline, [Turn(190), Turn(200), Turn(210)]);
        Assert.Null(telemetry.Pressure);
        Assert.Equal(ContextPressureSource.TrendOnly, telemetry.Source);
        Assert.Equal(100m, telemetry.BaselineInput);
        Assert.Equal(3, telemetry.ConsecutiveGrowthTurns);
        Assert.Equal(ContextEconomyAction.MarkCandidate, new ContextEconomyPolicy().Evaluate(telemetry).Action);
    }

    [Fact]
    public void Single_spike_and_large_new_context_do_not_trigger_growth()
    {
        var baseline = new[] { Turn(100), Turn(100), Turn(100) };
        var estimator = new ContextPressureEstimator();
        var spike = estimator.Estimate(Turn(200), baseline, [Turn(100), Turn(200)]);
        Assert.Equal(1, spike.ConsecutiveGrowthTurns);
        Assert.Equal(ContextEconomyAction.None, new ContextEconomyPolicy().Evaluate(spike).Action);

        var large = estimator.Estimate(Turn(220), baseline,
            [Turn(190), new ContextTurnSample(500, 0, IsLargeNewContext: true), Turn(220)]);
        Assert.Equal(1, large.ConsecutiveGrowthTurns);
    }

    [Fact]
    public void Cooldown_suppresses_normal_compaction_but_not_hard_protection()
    {
        var policy = new ContextEconomyPolicy();
        var candidate = policy.Evaluate(Telemetry(0.65m), 8);
        Assert.True(candidate.CooldownSuppressed);
        Assert.Equal(ContextEconomyAction.None, candidate.Action);

        var hard = policy.Evaluate(Telemetry(0.75m), 8);
        Assert.False(hard.CooldownSuppressed);
        Assert.Equal(ContextEconomyAction.HardProtect, hard.Action);
    }

    [Theory]
    [InlineData(100, 60, CompactionEffectiveness.Effective)]
    [InlineData(100, 70, CompactionEffectiveness.Marginal)]
    [InlineData(100, 85, CompactionEffectiveness.Ineffective)]
    public void Effectiveness_uses_pre_and_post_input_medians(
        long pre,
        long post,
        CompactionEffectiveness expected)
    {
        var result = new CompactionEffectivenessEvaluator().Evaluate(
            [Turn(pre), Turn(pre), Turn(pre)],
            [Turn(post), Turn(post), Turn(post)]);
        Assert.Equal(expected, result.Classification);
    }

    [Fact]
    public void Large_post_input_defers_verification()
    {
        var result = new CompactionEffectivenessEvaluator().Evaluate(
            [Turn(100), Turn(100)],
            [Turn(50), new ContextTurnSample(500, 400, IsLargeNewContext: true)]);
        Assert.Equal(CompactionEffectiveness.Deferred, result.Classification);
    }

    [Fact]
    public void State_machine_reaches_cooldown_and_blocked_protection()
    {
        var machine = new CompactionStateMachine();
        var state = machine.Transition(ContextEconomyState.Idle, ContextEconomyTransition.MandatoryDetected);
        state = machine.Transition(state, ContextEconomyTransition.SafeBoundaryReached);
        state = machine.Transition(state, ContextEconomyTransition.CompactionCompleted);
        state = machine.Transition(state, ContextEconomyTransition.VerificationEffective);
        Assert.Equal(ContextEconomyState.Cooldown, state);

        state = machine.Transition(ContextEconomyState.Compacting, ContextEconomyTransition.CompactionFailed);
        state = machine.Transition(state, ContextEconomyTransition.RetryExhausted);
        Assert.Equal(ContextEconomyState.ContextProtectionBlocked, state);
    }

    [Fact]
    public void Automatic_rollover_cannot_be_enabled()
    {
        var options = new ContextEconomyOptions { AutoRollover = true };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static ContextTurnSample Turn(long input) => new(input, input / 2);
    private static ContextPressureTelemetry Telemetry(decimal pressure) =>
        new(pressure, ContextPressureSource.NativeRenderedTokens, null, null, 0, 1, 1);
}
