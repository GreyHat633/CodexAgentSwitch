using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Usage;

public sealed class CostCalculator
{
    public MeasuredDecimal Calculate(
        ProviderPricing? pricing,
        long? inputTokens,
        long? outputTokens,
        decimal? providerReportedCost = null)
    {
        if (providerReportedCost is not null)
        {
            return new MeasuredDecimal(providerReportedCost, EvidenceKind.Actual);
        }

        if (pricing?.InputPerMillionTokens is null
            || pricing.OutputPerMillionTokens is null
            || inputTokens is null
            || outputTokens is null)
        {
            return new MeasuredDecimal(null, EvidenceKind.Unavailable);
        }

        var cost = inputTokens.Value / 1_000_000m * pricing.InputPerMillionTokens.Value
            + outputTokens.Value / 1_000_000m * pricing.OutputPerMillionTokens.Value;
        return new MeasuredDecimal(decimal.Round(cost, 6, MidpointRounding.AwayFromZero), EvidenceKind.Estimated);
    }
}
