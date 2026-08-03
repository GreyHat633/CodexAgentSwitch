using System.Text.Json;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Usage;

public sealed record WorkerUsageContext(
    string ProviderId,
    string ModelId,
    string Currency,
    ProviderPricing? Pricing,
    decimal? ProviderReportedCost = null,
    string? QuotaWindow = null);

public interface IWorkerUsageCollector
{
    UsageSnapshot Capture(string taskGroupId, string jobId, WorkerResult? result, WorkerUsageContext context);
}

public sealed class WorkerUsageCollector(CostCalculator costCalculator) : IWorkerUsageCollector
{
    public UsageSnapshot Capture(string taskGroupId, string jobId, WorkerResult? result, WorkerUsageContext context)
    {
        var usage = result?.RawResult is JsonElement raw ? FindUsage(raw) : null;
        var input = ReadLong(usage, "prompt_tokens", "input_tokens", "inputTokens");
        var output = ReadLong(usage, "completion_tokens", "output_tokens", "outputTokens");
        var total = ReadLong(usage, "total_tokens", "totalTokens") ?? (input is not null && output is not null ? input + output : null);
        var known = usage is not null;
        var cost = costCalculator.Calculate(context.Pricing, input, output, context.ProviderReportedCost);
        return new UsageSnapshot(
            Guid.NewGuid(),
            taskGroupId,
            jobId,
            context.ProviderId,
            context.ModelId,
            DateTimeOffset.UtcNow,
            new MeasuredLong(input, known && input is not null ? EvidenceKind.Actual : EvidenceKind.Unavailable),
            new MeasuredLong(output, known && output is not null ? EvidenceKind.Actual : EvidenceKind.Unavailable),
            new MeasuredLong(total, known && total is not null ? EvidenceKind.Actual : EvidenceKind.Unavailable),
            new MeasuredLong(result is null ? null : 1, result is null ? EvidenceKind.Unavailable : EvidenceKind.Actual),
            cost,
            context.Currency,
            context.QuotaWindow,
            known ? [] : ["Worker final result did not expose token usage; marked unavailable."]);
    }

    private static JsonElement? FindUsage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("usage", out var direct) && direct.ValueKind == JsonValueKind.Object)
            {
                return direct;
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindUsage(property.Value);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindUsage(item);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement? element, params string[] names)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.Value.TryGetProperty(name, out var value) && value.TryGetInt64(out var number))
            {
                return number;
            }
        }

        return null;
    }
}
