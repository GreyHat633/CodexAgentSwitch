namespace CodexAgentSwitch.Domain.Tasks;

/// <summary>A bounded, deterministic hand-off snapshot for context compaction or rollover.</summary>
public sealed class CompactCheckpoint
{
    public CompactCheckpoint(
        IEnumerable<string>? completed,
        IEnumerable<string>? remaining,
        IEnumerable<string>? stableInterfaces,
        IEnumerable<string>? necessaryFiles,
        string testStatus,
        string nextPhaseEntry,
        string sourceThreadId,
        string sourceTaskId,
        DateTimeOffset timestamp)
    {
        Completed = Copy(completed);
        Remaining = Copy(remaining);
        StableInterfaces = Copy(stableInterfaces);
        NecessaryFiles = Copy(necessaryFiles);
        TestStatus = Require(testStatus, nameof(testStatus));
        NextPhaseEntry = Require(nextPhaseEntry, nameof(nextPhaseEntry));
        SourceThreadId = Require(sourceThreadId, nameof(sourceThreadId));
        SourceTaskId = Require(sourceTaskId, nameof(sourceTaskId));
        Timestamp = timestamp;
    }

    public IReadOnlyList<string> Completed { get; }
    public IReadOnlyList<string> Remaining { get; }
    public IReadOnlyList<string> StableInterfaces { get; }
    public IReadOnlyList<string> NecessaryFiles { get; }
    public string TestStatus { get; }
    public string NextPhaseEntry { get; }
    public string SourceThreadId { get; }
    public string SourceTaskId { get; }
    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset CreatedAt => Timestamp;

    /// <summary>Renders a stable replay prompt with a hard character bound.</summary>
    public string RenderReplayText(int maxCharacters = 4_000)
    {
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var text = string.Join('\n',
            "COMPACT CHECKPOINT",
            $"source-thread: {SourceThreadId}",
            $"source-task: {SourceTaskId}",
            $"timestamp: {Timestamp:O}",
            "completed:", List(Completed),
            "remaining:", List(Remaining),
            "stable-interfaces:", List(StableInterfaces),
            "necessary-files:", List(NecessaryFiles),
            $"test-status: {TestStatus}",
            $"next-phase-entry: {NextPhaseEntry}");
        if (text.Length <= maxCharacters) return text;
        const string marker = "...[truncated]";
        return maxCharacters <= marker.Length ? marker[..maxCharacters] : text[..(maxCharacters - marker.Length)] + marker;
    }

    private static IReadOnlyList<string> Copy(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>()).Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0).ToArray();
    private static string List(IReadOnlyList<string> values) => values.Count == 0 ? "- (none)" : string.Join('\n', values.Select(value => "- " + value));
    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
}

public enum SessionContextRecommendation
{
    Continue,
    Compact,
    Rollover,
}

public sealed record SessionContextBudgetInput
{
    public SessionContextBudgetInput(TimeSpan sessionAge, int turnCount, decimal mainNormalizedCost)
    {
        if (sessionAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sessionAge));
        if (turnCount < 0) throw new ArgumentOutOfRangeException(nameof(turnCount));
        if (mainNormalizedCost < 0m) throw new ArgumentOutOfRangeException(nameof(mainNormalizedCost));
        SessionAge = sessionAge;
        TurnCount = turnCount;
        MainNormalizedCost = mainNormalizedCost;
    }

    public TimeSpan SessionAge { get; }
    public int TurnCount { get; }
    public decimal MainNormalizedCost { get; }
}

public sealed record SessionContextBudgetDecision(
    SessionContextRecommendation Recommendation,
    IReadOnlyList<string> Reasons,
    SessionContextBudgetInput Input);

public sealed class SessionContextBudgetOptions
{
    public SessionContextBudgetOptions(
        TimeSpan? compactAge = null, TimeSpan? rolloverAge = null,
        int compactTurns = 20, int rolloverTurns = 40,
        decimal compactNormalizedCost = 40m, decimal rolloverNormalizedCost = 60m)
    {
        CompactAge = compactAge ?? TimeSpan.FromMinutes(20);
        RolloverAge = rolloverAge ?? TimeSpan.FromMinutes(45);
        CompactTurns = compactTurns;
        RolloverTurns = rolloverTurns;
        CompactNormalizedCost = compactNormalizedCost;
        RolloverNormalizedCost = rolloverNormalizedCost;
        if (CompactAge < TimeSpan.Zero || RolloverAge < CompactAge) throw new ArgumentException("Age thresholds must be non-decreasing.");
        if (compactTurns < 0 || rolloverTurns < compactTurns) throw new ArgumentException("Turn thresholds must be non-decreasing.");
        if (compactNormalizedCost < 0m || rolloverNormalizedCost < compactNormalizedCost) throw new ArgumentException("Cost thresholds must be non-decreasing.");
    }

    public TimeSpan CompactAge { get; }
    public TimeSpan RolloverAge { get; }
    public int CompactTurns { get; }
    public int RolloverTurns { get; }
    public decimal CompactNormalizedCost { get; }
    public decimal RolloverNormalizedCost { get; }
    public static SessionContextBudgetOptions Default { get; } = new();
}
