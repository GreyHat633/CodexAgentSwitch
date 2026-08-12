using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Tests.Usage;

public sealed class MainCostGuardTests
{
    [Fact]
    public void Initial_window_triggers_at_twenty_five_credits_not_before()
    {
        var guard = new MainCostGuard();

        guard.AcceptUsage(Sample(24_900_000, 0, 0, 0));
        Assert.False(guard.Telemetry.GuardHit);
        Assert.Equal(24.9m, guard.Telemetry.CurrentWindowCredits);

        guard.AcceptUsage(Sample(25_000_000, 0, 0, 0));
        Assert.True(guard.Telemetry.GuardHit);
        Assert.Equal(25m, guard.Telemetry.CurrentWindowCredits);
        Assert.Equal(1, guard.Telemetry.GuardHitCount);
    }

    [Fact]
    public void Main_investigation_checkpoint_advances_backoff_and_resets_window()
    {
        var guard = new MainCostGuard();
        guard.AcceptUsage(Sample(25_000_000, 0, 0, 0));

        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.INVESTIGATION_UNRESOLVED);
        Assert.Equal(40m, guard.Telemetry.CurrentThreshold);
        Assert.Equal(0m, guard.Telemetry.CurrentWindowCredits);
        Assert.Equal(1, guard.Telemetry.BackoffStage);

        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.INVESTIGATION_UNRESOLVED);
        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.INVESTIGATION_UNRESOLVED);
        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.INVESTIGATION_UNRESOLVED);
        Assert.Equal(60m, guard.Telemetry.CurrentThreshold);
        Assert.Equal(2, guard.Telemetry.BackoffStage);
    }

    [Fact]
    public void Invalid_checkpoint_reason_is_rejected_without_mutating_state()
    {
        var guard = new MainCostGuard();
        guard.AcceptUsage(Sample(10_000_000, 0, 0, 0));
        var before = guard.Telemetry;

        Assert.Throws<ArgumentException>(() =>
            guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.BOUNDED_IMPLEMENTATION));

        Assert.Equal(before, guard.Telemetry);
    }

    [Fact]
    public void New_main_package_and_worker_completion_return_to_initial_window()
    {
        var guard = new MainCostGuard();
        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.INVESTIGATION_UNRESOLVED);
        Assert.Equal(40m, guard.CurrentThreshold);

        guard.StartPackage();
        Assert.Equal(25m, guard.CurrentThreshold);
        Assert.Equal(0m, guard.CurrentWindowCredits);

        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.INVESTIGATION_UNRESOLVED);
        guard.RecordCheckpoint(WorkOwner.Worker, RepartitionReasonCode.BOUNDED_IMPLEMENTATION);
        Assert.Equal(25m, guard.CurrentThreshold);
        Assert.Equal(0m, guard.CurrentWindowCredits);
    }

    [Fact]
    public void Legal_main_checkpoint_resets_window_without_advancing_for_non_investigation_reason()
    {
        var guard = new MainCostGuard();
        guard.AcceptUsage(Sample(10_000_000, 0, 0, 0));

        guard.RecordCheckpoint(WorkOwner.Main, RepartitionReasonCode.REVIEW_REQUIRED);

        Assert.Equal(0m, guard.CurrentWindowCredits);
        Assert.Equal(25m, guard.CurrentThreshold);
        Assert.Equal(0, guard.BackoffStage);
    }

    [Fact]
    public void Coordinator_isolates_normalized_directory_and_exact_session()
    {
        var coordinator = new MainCostGuardCoordinator();
        var first = coordinator.Resolve("E:/Project-A", "session-1");
        first.AcceptUsage(Sample("session-1", "E:/Project-A", 25_000_000));

        Assert.True(first.IsGuardHit);
        Assert.False(coordinator.Resolve("E:/Project-A", "session-2").IsGuardHit);
        Assert.False(coordinator.Resolve("E:/Project-B", "session-1").IsGuardHit);
    }

    [Fact]
    public void Rates_are_separate_reasoning_is_reported_only_and_deltas_are_monotonic()
    {
        var options = new MainCostGuardOptions(
            [25m, 40m, 60m],
            new NormalizedCostWeights(2m, 3m, 4m));
        var guard = new MainCostGuard(options);

        var first = guard.AcceptUsage(Sample(1_000_000, 500_000, 250_000, 10));
        Assert.Equal(3.5m, first.NormalizedCredits); // 0.5 * 2 + 0.5 * 3 + 0.25 * 4
        Assert.Equal(10, first.ReasoningTokens);

        var duplicate = guard.AcceptUsage(Sample(1_000_000, 500_000, 250_000, 10));
        Assert.Equal(0m, duplicate.NormalizedCredits);

        var reset = guard.AcceptUsage(Sample(100, 50, 25, 1));
        Assert.Equal(0.00035m, reset.NormalizedCredits);
        Assert.True(guard.Telemetry.SessionCumulativeNormalizedCredits >= first.NormalizedCredits);
    }

    private static NativeUsageRecord Sample(long input, long cached, long output, long reasoning) =>
        new(
            "session-1",
            null,
            null,
            "model",
            "high",
            "Sol",
            1,
            input,
            cached,
            Math.Max(0, input - cached),
            output,
            reasoning,
            input + output,
            null,
            null,
            "synthetic",
            "test");

    private static NativeUsageRecord Sample(string sessionId, string cwd, long input) =>
        new(sessionId, cwd, null, "model", "high", "Sol", 1, input, 0, input, 0, 0, input, null, null, "synthetic", "test");
}
