using System.Text.Json;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed record CodexModelOption(string Id, string DisplayName, bool IsDefault);

public sealed record CodexModelResolution(
    string RequestedModelId,
    string ModelId,
    string? CompatibilityNotice);

public interface ICodexModelResolver
{
    Task<CodexModelResolution> ResolveAsync(
        CodexAppServerClient client,
        string requestedModelId,
        CancellationToken cancellationToken = default);

    Task<CodexModelResolution> ResolveAsync(
        CodexCommand command,
        string requestedModelId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates the exact Agent Switch model requested by a profile against the
/// catalog exposed by the currently signed-in Codex client. This class never
/// remaps an unavailable role to a different model: the caller receives a
/// clear error containing the discovered catalog instead.
/// </summary>
public sealed class CodexModelResolver : ICodexModelResolver
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<CodexModelOption>? cachedModels;

    public async Task<CodexModelResolution> ResolveAsync(
        CodexAppServerClient client,
        string requestedModelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedModelId);
        var models = await GetModelsAsync(client, cancellationToken);
        return Resolve(requestedModelId, models);
    }

    public async Task<CodexModelResolution> ResolveAsync(
        CodexCommand command,
        string requestedModelId,
        CancellationToken cancellationToken = default)
    {
        await using var client = new CodexAppServerClient(command);
        return await ResolveAsync(client, requestedModelId, cancellationToken);
    }

    public static CodexModelResolution Resolve(
        string requestedModelId,
        IReadOnlyList<CodexModelOption> models)
    {
        var exact = models.FirstOrDefault(model =>
            string.Equals(model.Id, requestedModelId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new CodexModelResolution(requestedModelId, exact.Id, null);
        }

        var available = models.Count == 0
            ? "（当前账户未返回可用模型）"
            : string.Join("、", models.Select(model => model.Id));
        throw new InvalidOperationException($"当前 Codex 账户不支持模型 {requestedModelId}。可用模型：{available}");
    }

    private async Task<IReadOnlyList<CodexModelOption>> GetModelsAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        if (cachedModels is not null)
        {
            return cachedModels;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cachedModels is not null)
            {
                return cachedModels;
            }

            var response = await client.RequestAsync(
                "model/list",
                new { cursor = (string?)null, limit = 100, includeHidden = false },
                cancellationToken);
            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Codex App Server did not return a model catalog.");
            }

            var models = new List<CodexModelOption>();
            foreach (var item in data.EnumerateArray())
            {
                var id = ReadString(item, "id") ?? ReadString(item, "model");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                models.Add(new CodexModelOption(
                    id,
                    ReadString(item, "displayName") ?? id,
                    item.TryGetProperty("isDefault", out var isDefault)
                    && isDefault.ValueKind == JsonValueKind.True));
            }

            cachedModels = models;
            return cachedModels;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
