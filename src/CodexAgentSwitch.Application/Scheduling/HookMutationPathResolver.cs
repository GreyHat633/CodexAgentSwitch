using System.Text.Json;
using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Application.Scheduling;

internal sealed record HookMutationPathResolution(
    bool Supported,
    bool Resolved,
    IReadOnlyList<string> Paths,
    string Reason);

/// <summary>
/// Resolves only structured, exact mutation targets. Generic shell input and
/// incomplete payloads are intentionally unsupported and therefore fail open.
/// </summary>
internal static class HookMutationPathResolver
{
    public static HookMutationPathResolution Resolve(string? toolName, string? toolInput, string workingDirectory)
    {
        var tool = toolName?.Trim() ?? string.Empty;
        if (tool.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("Write", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSinglePath(toolInput, workingDirectory);
        }

        if (tool.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePatch(toolInput, workingDirectory);
        }

        return new(false, false, [], "Operation does not expose an exact structured mutation target.");
    }

    private static HookMutationPathResolution ResolveSinglePath(string? input, string workingDirectory)
    {
        if (!TryParseInput(input, out var root) || root.ValueKind != JsonValueKind.Object)
            return new(true, false, [], "Structured tool input is missing or invalid.");

        foreach (var name in new[] { "file_path", "filePath", "path" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return Normalize([value.GetString()!], workingDirectory);
        }

        return new(true, false, [], "Structured tool input has no exact path field.");
    }

    private static HookMutationPathResolution ResolvePatch(string? input, string workingDirectory)
    {
        if (!TryParseInput(input, out var root))
            return new(true, false, [], "apply_patch input is missing or invalid.");
        var patch = root.ValueKind == JsonValueKind.String ? root.GetString() : null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "patch", "input" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    patch = value.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(patch))
            return new(true, false, [], "apply_patch has no exact patch body.");

        var paths = new List<string>();
        foreach (var line in patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            foreach (var prefix in new[] { "*** Add File: ", "*** Update File: ", "*** Delete File: ", "*** Move to: " })
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var path = line[prefix.Length..].Trim();
                if (path.Length == 0) return new(true, false, [], "apply_patch contains an empty target path.");
                paths.Add(path);
                break;
            }
        }
        return paths.Count == 0
            ? new(true, false, [], "apply_patch contains no recognized exact target path.")
            : Normalize(paths, workingDirectory);
    }

    private static bool TryParseInput(string? input, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        try
        {
            using var document = JsonDocument.Parse(input);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HookMutationPathResolution Normalize(IEnumerable<string> paths, string workingDirectory)
    {
        try
        {
            var cwd = WorkPackageLease.NormalizePath(workingDirectory);
            var normalized = paths
                .Select(path => WorkPackageLease.NormalizePath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path)))
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return normalized.Length == 0
                ? new(true, false, [], "No exact target path could be normalized.")
                : new(true, true, normalized, "Exact target path resolved.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return new(true, false, [], "Target path normalization failed.");
        }
    }
}
