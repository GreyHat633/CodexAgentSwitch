using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Infrastructure.Usage;

/// Reads native Codex JSONL session logs without opening or modifying sessions.
public sealed class CodexSessionUsageSource : IUsageSource
{
    private readonly string? _explicitRoot;
    private readonly string? _diagnosticPath;
    private readonly object _diagnosticLock = new();

    public CodexSessionUsageSource(string? root = null, string? diagnosticPath = null)
    {
        _explicitRoot = root;
        _diagnosticPath = diagnosticPath;
    }

    public UsageScanMetrics LastScanMetrics { get; private set; } = UsageScanMetrics.Empty;

    public IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var counters = new ScanCounters();
        var result = new List<NativeUsageRecord>();
        try
        {
            foreach (var path in EnumerateFiles(counters))
            {
                cancellationToken.ThrowIfCancellationRequested();
                counters.FilesScanned++;
                try { ParseFile(path, result, counters, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    counters.FilesFailed++;
                    WriteDiagnostic(path, null, exception, "file-read");
                }
            }
            return result;
        }
        finally
        {
            stopwatch.Stop();
            LastScanMetrics = new UsageScanMetrics(
                stopwatch.Elapsed,
                counters.FilesScanned,
                counters.EventsSkipped,
                counters.FilesFailed,
                result.Count);
        }
    }

    private IEnumerable<string> EnumerateFiles(ScanCounters counters)
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                counters.FilesFailed++;
                WriteDiagnostic(scanRoot, null, exception, "file-enumeration");
                continue;
            }
            foreach (var file in files) yield return file;
        }
    }

    private void ParseFile(string path, List<NativeUsageRecord> output, ScanCounters counters, CancellationToken token)
    {
        var sessions = new Dictionary<string, State>(StringComparer.Ordinal);
        string? activeSession = null;
        long lineIndex = 0;
        foreach (var line in ReadLinesShared(path))
        {
            lineIndex++;
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
                    s.Role = metaRole is not null ? NormalizeExplicitRole(metaRole) : MapModelRole(s.Model);
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
                    state.Role = NormalizeExplicitRole(explicitRole);
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
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                counters.EventsSkipped++;
                WriteDiagnostic(path, lineIndex, exception, "event-parse");
            }
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
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024);
        while (reader.ReadLine() is { } line) yield return line;
    }
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
            if (e.TryGetProperty(name, out var direct)
                && direct.ValueKind == JsonValueKind.Number
                && direct.TryGetInt64(out var value))
                return value;
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
        if (role is not null) return NormalizeExplicitRole(role);
        return MapModelRole(model);
    }
    private static string MapModelRole(string? model) =>
        NativeCodexRoleCatalog.FindByModel(model)?.SlotName ?? "Unknown";

    private static string NormalizeExplicitRole(string role)
    {
        var managed = NativeCodexRoleCatalog.All.FirstOrDefault(candidate =>
            role.Contains(candidate.AgentRole, StringComparison.OrdinalIgnoreCase));
        return managed?.AgentRole ?? role;
    }
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

    private void WriteDiagnostic(string path, long? lineIndex, Exception exception, string stage)
    {
        if (string.IsNullOrWhiteSpace(_diagnosticPath)) return;
        try
        {
            var directory = Path.GetDirectoryName(_diagnosticPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var diagnostic = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                file = path,
                lineIndex,
                exceptionType = exception.GetType().Name,
                stage,
                field = stage == "event-parse" ? "jsonl-event" : null,
            });
            lock (_diagnosticLock)
                File.AppendAllText(_diagnosticPath, diagnostic + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // Usage diagnostics are best-effort and must never break Stop-boundary processing.
        }
    }

    private sealed class ScanCounters { public int FilesScanned, EventsSkipped, FilesFailed; }
    private sealed class State { public string SessionId = ""; public string? Cwd, Project, Model, Effort, Role, Source; public bool RoleExplicit; public long Calls, Input, Cached, Output, Reasoning, Total, LatestInput, LatestCached; public long? ContextWindow, PreCompactionInput, PreCompactionCached; public DateTimeOffset? Started, Ended, LastCompactedAt; public List<(long Input, long Cached)> RecentInputs = []; public IReadOnlyList<long>? PreCompactionInputs, PreCompactionCachedInputs; }
    private readonly record struct Usage(long Input, long Cached, long Output, long Reasoning, long Total)
    { public static Usage From(JsonElement e) { var i=Read(e,"input_tokens","input","totalIn"); var o=Read(e,"output_tokens","output","totalOut"); return new(i,Read(e,"cached_input_tokens","cache_read_input_tokens","cached","cachedIn"),o,Read(e,"reasoning_tokens","reasoning_output_tokens","reasoning"),Read(e,"total_tokens","totalTokens") is var t && t > 0 ? t : i+o); } private static long Read(JsonElement e, params string[] n) { foreach(var x in n) if(e.ValueKind == JsonValueKind.Object && e.TryGetProperty(x,out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i; return 0; } }
}

public sealed record UsageScanMetrics(
    TimeSpan Duration,
    int FilesScanned,
    int EventsSkipped,
    int FilesFailed,
    int RecordsProduced)
{
    public static UsageScanMetrics Empty { get; } = new(TimeSpan.Zero, 0, 0, 0, 0);
}
