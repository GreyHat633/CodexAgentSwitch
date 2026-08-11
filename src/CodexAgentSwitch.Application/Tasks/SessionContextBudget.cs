using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

/// <summary>Age/turn/normalized-cost policy; deliberately makes no token-size claim.</summary>
public sealed class SessionContextBudget
{
    private readonly SessionContextBudgetOptions options;
    public SessionContextBudget(SessionContextBudgetOptions? options = null) => this.options = options ?? SessionContextBudgetOptions.Default;

    public SessionContextBudgetDecision Evaluate(SessionContextBudgetInput input)
    {
        var rollover = new List<string>();
        var compact = new List<string>();
        if (input.SessionAge >= options.RolloverAge) rollover.Add($"session-age >= {options.RolloverAge}");
        else if (input.SessionAge >= options.CompactAge) compact.Add($"session-age >= {options.CompactAge}");
        if (input.TurnCount >= options.RolloverTurns) rollover.Add($"turn-count >= {options.RolloverTurns}");
        else if (input.TurnCount >= options.CompactTurns) compact.Add($"turn-count >= {options.CompactTurns}");
        if (input.MainNormalizedCost >= options.RolloverNormalizedCost) rollover.Add($"main-normalized-cost >= {options.RolloverNormalizedCost:0.####}");
        else if (input.MainNormalizedCost >= options.CompactNormalizedCost) compact.Add($"main-normalized-cost >= {options.CompactNormalizedCost:0.####}");
        var recommendation = rollover.Count > 0 ? SessionContextRecommendation.Rollover : compact.Count > 0 ? SessionContextRecommendation.Compact : SessionContextRecommendation.Continue;
        var reasons = (IReadOnlyList<string>)(rollover.Count > 0 ? rollover : compact.Count > 0 ? compact : new List<string> { "all observable session metrics are below configured thresholds" });
        return new SessionContextBudgetDecision(recommendation, reasons, input);
    }
}
