using System.Text.Json;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed record CodexModelOption(
    string Id,
    string DisplayName,
    bool IsDefault,
    IReadOnlyList<string>? SupportedReasoningEfforts = null,
    string? DefaultReasoningEffort = null);

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

    async Task<CodexModelResolution> ResolveAsync(
        CodexAppServerClient client,
        string requestedModelId,
        string requestedReasoningEffort,
        CancellationToken cancellationToken = default) =>
        await ResolveAsync(client, requestedModelId, cancellationToken);

    async Task<CodexModelResolution> ResolveAsync(
        CodexCommand command,
        string requestedModelId,
        string requestedReasoningEffort,
        CancellationToken cancellationToken = default) =>
        await ResolveAsync(command, requestedModelId, cancellationToken);
}

/// <summary>
/// Validates the exact Agent Switch model requested by a profile against the
/// catalog exposed by the currently signed-in Codex client. This class never
/// remaps an unavailable role to a different model: the caller receives a
/// clear error containing the discovered catalog instead.
/// </summary>
public sealed class CodexModelResolver : ICodexModelResolver
{
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
        CodexAppServerClient client,
        string requestedModelId,
        string requestedReasoningEffort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedReasoningEffort);
        var models = await GetModelsAsync(client, cancellationToken);
        return Resolve(requestedModelId, requestedReasoningEffort, models);
    }

    public async Task<CodexModelResolution> ResolveAsync(
        CodexCommand command,
        string requestedModelId,
        CancellationToken cancellationToken = default)
    {
        await using var client = new CodexAppServerClient(command);
        return await ResolveAsync(client, requestedModelId, cancellationToken);
    }

    public async Task<CodexModelResolution> ResolveAsync(
        CodexCommand command,
        string requestedModelId,
        string requestedReasoningEffort,
        CancellationToken cancellationToken = default)
    {
        await using var client = new CodexAppServerClient(command);
        return await ResolveAsync(client, requestedModelId, requestedReasoningEffort, cancellationToken);
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

    public static CodexModelResolution Resolve(
        string requestedModelId,
        string requestedReasoningEffort,
        IReadOnlyList<CodexModelOption> models)
    {
        var resolution = Resolve(requestedModelId, models);
        var model = models.First(option =>
            string.Equals(option.Id, resolution.ModelId, StringComparison.OrdinalIgnoreCase));
        if (model.SupportedReasoningEfforts is null || model.SupportedReasoningEfforts.Count == 0)
        {
            throw new InvalidDataException($"Codex 模型目录没有返回 {model.Id} 的推理强度能力，已拒绝启动。");
        }

        if (!model.SupportedReasoningEfforts.Contains(requestedReasoningEffort, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"当前 Codex 账户的模型 {model.Id} 不支持推理强度 {requestedReasoningEffort}。可用强度：{string.Join("、", model.SupportedReasoningEfforts)}");
        }

        return resolution;
    }

    private async Task<IReadOnlyList<CodexModelOption>> GetModelsAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
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

            var efforts = new List<string>();
            if (item.TryGetProperty("supportedReasoningEfforts", out var supported)
                && supported.ValueKind == JsonValueKind.Array)
            {
                foreach (var effort in supported.EnumerateArray())
                {
                    var value = effort.ValueKind == JsonValueKind.String
                        ? effort.GetString()
                        : ReadString(effort, "reasoningEffort") ?? ReadString(effort, "effort");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        efforts.Add(value);
                    }
                }
            }

            models.Add(new CodexModelOption(
                id,
                ReadString(item, "displayName") ?? id,
                item.TryGetProperty("isDefault", out var isDefault)
                && isDefault.ValueKind == JsonValueKind.True,
                efforts,
                ReadString(item, "defaultReasoningEffort")));
        }

        return models;
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
