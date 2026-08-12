using System.Text.Json;
using CodexAgentSwitch.Application.Usage;

namespace CodexAgentSwitch.Infrastructure.Usage;

/// Reads native Codex JSONL session logs without opening or modifying sessions.
public sealed class CodexSessionUsageSource : IUsageSource
{
    private readonly string? _explicitRoot;

    public CodexSessionUsageSource(string? root = null) => _explicitRoot = root;

    public IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default)
    {
        var result = new List<NativeUsageRecord>();
        foreach (var path in EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { ParseFile(path, result, cancellationToken); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private IEnumerable<string> EnumerateFiles()
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_explicitRoot))
            roots.Add(_explicitRoot!);
        else
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(env)) roots.Add(env!);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile)) roots.Add(Path.Combine(profile, ".codex"));
        }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var scanRoot = Directory.Exists(Path.Combine(root, "sessions")) ? Path.Combine(root, "sessions") : root;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(scanRoot, "*.jsonl", SearchOption.AllDirectories).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private static void ParseFile(string path, List<NativeUsageRecord> output, CancellationToken token)
    {
        var sessions = new Dictionary<string, State>(StringComparer.Ordinal);
        string? activeSession = null;
        foreach (var line in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = String(root, "type");
                var payload = root.TryGetProperty("payload", out var p) ? p : root;
                if (type == "session_meta" || String(payload, "type") == "session_meta")
                {
                    var id = String(payload, "id") ?? String(payload, "session_id") ?? String(root, "session_id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var s = Get(sessions, id!);
                    activeSession = id;
                    s.SessionId = id!;
                    s.Cwd ??= String(payload, "cwd") ?? String(payload, "working_directory");
                    s.Project ??= String(payload, "project") ?? String(payload, "project_path");
                    s.Source ??= ReadSessionSource(payload) ?? ReadSessionSource(root);
                    s.Model ??= String(payload, "model") ?? String(payload, "model_id");
                    var metaRole = FindString(payload, "agent_role") ?? FindString(root, "agent_role");
                    s.Role = metaRole is not null ? (metaRole.Contains("cas_luna_worker", StringComparison.OrdinalIgnoreCase) ? "cas_luna_worker" : metaRole) : MapModelRole(s.Model);
                    s.RoleExplicit = metaRole is not null;
                    s.Started ??= Timestamp(root, payload);
                    continue;
                }
                var sessionId = String(root, "session_id") ?? String(payload, "session_id") ?? String(payload, "sessionId") ?? activeSession;
                if (sessionId is null) continue;
                var state = Get(sessions, sessionId);
                state.Source ??= ReadSessionSource(payload) ?? ReadSessionSource(root);
                state.Model = String(payload, "model") ?? String(payload, "model_id") ?? state.Model;
                state.Effort = String(payload, "reasoning_effort") ?? String(payload, "effort") ?? state.Effort;
                state.ContextWindow ??= FindInt64(payload, "model_context_window") ?? FindInt64(root, "model_context_window");
                var explicitRole = FindString(payload, "agent_role") ?? FindString(root, "agent_role");
                if (explicitRole is not null)
                {
                    state.Role = explicitRole.Contains("cas_luna_worker", StringComparison.OrdinalIgnoreCase) ? "cas_luna_worker" : explicitRole;
                    state.RoleExplicit = true;
                }
                else if (!state.RoleExplicit)
                    state.Role = MapModelRole(state.Model);
                var timestamp = Timestamp(root, payload);
                state.Started ??= timestamp; state.Ended = timestamp ?? state.Ended;
                if (string.Equals(type, "compacted", StringComparison.Ordinal)
                    || string.Equals(String(payload, "type"), "compacted", StringComparison.Ordinal))
                {
                    state.PreCompactionInput = state.Calls > 0 ? state.LatestInput : state.PreCompactionInput;
                    state.PreCompactionCached = state.Calls > 0 ? state.LatestCached : state.PreCompactionCached;
                    state.PreCompactionInputs = state.RecentInputs.Select(item => item.Input).ToArray();
                    state.PreCompactionCachedInputs = state.RecentInputs.Select(item => item.Cached).ToArray();
                    state.LastCompactedAt = timestamp ?? state.LastCompactedAt;
                    continue;
                }
                if (TryFindLastUsage(payload, out var usage))
                {
                    var current = Usage.From(usage);
                    // token_count lifecycle rows with Input=0 are not model calls
                    // and must not become post-compaction baselines.
                    if (current.Input <= 0) continue;
                    state.LatestInput = current.Input;
                    state.LatestCached = current.Cached;
                    state.RecentInputs.Add((current.Input, current.Cached));
                    if (state.RecentInputs.Count > 3) state.RecentInputs.RemoveAt(0);
                    state.Input += current.Input; state.Cached += current.Cached; state.Output += current.Output; state.Reasoning += current.Reasoning;
                    state.Total += current.Total; state.Calls++;
                }
            }
            catch (JsonException) { }
            catch (FormatException) { }
        }
        foreach (var s in sessions.Values)
        {
            var uncached = Math.Max(0, s.Input - s.Cached);
            var role = s.Role ?? "Unknown";
            var attribution = s.Project is not null ? "explicit-project" : s.Cwd is not null ? "cwd" : "unassigned";
            output.Add(new NativeUsageRecord(s.SessionId, s.Cwd, s.Project, s.Model, s.Effort, role, s.Calls,
                s.Input, s.Cached, uncached, s.Output, s.Reasoning, s.Total > 0 ? s.Total : s.Input + s.Output,
                s.Started, s.Ended, path, attribution,
                s.Calls > 0 ? s.LatestInput : null,
                s.Calls > 0 ? s.LatestCached : null,
                s.ContextWindow,
                s.Source,
                s.LastCompactedAt,
                s.PreCompactionInput,
                s.PreCompactionCached,
                s.PreCompactionInputs,
                s.PreCompactionCachedInputs));
        }
    }

    private static State Get(Dictionary<string, State> map, string id) => map.TryGetValue(id, out var s) ? s : (map[id] = new State { SessionId = id });
    private static string? String(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? ReadSessionSource(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out var source)) return null;
        if (source.ValueKind == JsonValueKind.String) return source.GetString();
        if (source.ValueKind != JsonValueKind.Object) return null;
        if (source.TryGetProperty("subAgent", out _)) return "subAgent";
        if (source.TryGetProperty("subagent", out _)) return "subAgent";
        return source.EnumerateObject().FirstOrDefault().Name;
    }
    private static long? FindInt64(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty(name, out var direct) && direct.TryGetInt64(out var value)) return value;
            foreach (var property in e.EnumerateObject())
            {
                var found = FindInt64(property.Value, name);
                if (found is not null) return found;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var found = FindInt64(item, name);
                if (found is not null) return found;
            }
        }
        return null;
    }
    private static DateTimeOffset? Timestamp(JsonElement a, JsonElement b) { var s = String(a, "timestamp") ?? String(a, "created_at") ?? String(b, "timestamp"); return DateTimeOffset.TryParse(s, out var t) ? t : null; }
    private static string? ResolveRole(JsonElement root, JsonElement payload, string? model)
    {
        var role = FindString(payload, "agent_role") ?? FindString(root, "agent_role");
        if (role is not null) return role.Contains("cas_luna_worker", StringComparison.OrdinalIgnoreCase) ? "cas_luna_worker" : role;
        return MapModelRole(model);
    }
    private static string MapModelRole(string? model) => model?.ToLowerInvariant() switch { "gpt-5.6-sol" => "Sol", "gpt-5.6-terra" => "Terra", "gpt-5.6-luna" => "Luna", _ => "Unknown" };
    private static string? FindString(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            foreach (var p in e.EnumerateObject()) { var found = FindString(p.Value, name); if (found is not null) return found; }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) { var found = FindString(x, name); if (found is not null) return found; }
        return null;
    }
    private static bool TryFindLastUsage(JsonElement e, out JsonElement usage)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject()) { if (p.NameEquals("last_token_usage")) { usage = p.Value; return usage.ValueKind == JsonValueKind.Object; } if (TryFindLastUsage(p.Value, out usage)) return true; }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) if (TryFindLastUsage(x, out usage)) return true;
        usage = default; return false;
    }

    private sealed class State { public string SessionId = ""; public string? Cwd, Project, Model, Effort, Role, Source; public bool RoleExplicit; public long Calls, Input, Cached, Output, Reasoning, Total, LatestInput, LatestCached; public long? ContextWindow, PreCompactionInput, PreCompactionCached; public DateTimeOffset? Started, Ended, LastCompactedAt; public List<(long Input, long Cached)> RecentInputs = []; public IReadOnlyList<long>? PreCompactionInputs, PreCompactionCachedInputs; }
    private readonly record struct Usage(long Input, long Cached, long Output, long Reasoning, long Total)
    { public static Usage From(JsonElement e) { var i=Read(e,"input_tokens","input","totalIn"); var o=Read(e,"output_tokens","output","totalOut"); return new(i,Read(e,"cached_input_tokens","cache_read_input_tokens","cached","cachedIn"),o,Read(e,"reasoning_tokens","reasoning_output_tokens","reasoning"),Read(e,"total_tokens","totalTokens") is var t && t > 0 ? t : i+o); } private static long Read(JsonElement e, params string[] n) { foreach(var x in n) if(e.TryGetProperty(x,out var v)&&v.TryGetInt64(out var i)) return i; return 0; } }
}
