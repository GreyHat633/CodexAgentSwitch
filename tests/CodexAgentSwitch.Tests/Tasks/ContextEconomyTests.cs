using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ContextEconomyTests
{
    [Fact]
    public void Checkpoint_replay_is_deterministic_and_bounded()
    {
        var checkpoint = new CompactCheckpoint(
            ["done"], ["next"], ["IMainAgentSession"], ["Tasks.cs"],
            "28 tests passed", "resume Tasks", "thread-1", "task-1",
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

        var replay = checkpoint.RenderReplayText(256);
        Assert.Equal(replay, checkpoint.RenderReplayText(256));
        Assert.True(replay.Length <= 256);
        Assert.Contains("source-thread: thread-1", replay, StringComparison.Ordinal);
        Assert.Contains("COMPACT CHECKPOINT", replay, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0, SessionContextRecommendation.Continue)]
    [InlineData(20, 0, 0, SessionContextRecommendation.Compact)]
    [InlineData(45, 0, 0, SessionContextRecommendation.Rollover)]
    [InlineData(0, 20, 0, SessionContextRecommendation.Compact)]
    [InlineData(0, 40, 0, SessionContextRecommendation.Rollover)]
    [InlineData(0, 0, 40, SessionContextRecommendation.Compact)]
    [InlineData(0, 0, 60, SessionContextRecommendation.Rollover)]
    public void Budget_recommendation_uses_observable_thresholds(
        int ageMinutes, int turns, double normalizedCost, SessionContextRecommendation expected)
    {
        var decision = new SessionContextBudget().Evaluate(
            new SessionContextBudgetInput(TimeSpan.FromMinutes(ageMinutes), turns, (decimal)normalizedCost));
        Assert.Equal(expected, decision.Recommendation);
        Assert.NotEmpty(decision.Reasons);
    }
}
