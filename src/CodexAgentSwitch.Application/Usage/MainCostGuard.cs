using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Usage;

/// <summary>
/// Model-agnostic, bounded state machine for MAIN normalized-credit windows.
/// Native usage records are cumulative; only the positive delta from the last
/// accepted baseline is charged.  Reasoning tokens remain observable but are
/// never charged a second time.
/// </summary>
public class MainCostGuard
{
    private readonly MainCostGuardOptions options;
    private readonly Dictionary<string, CumulativeUsage> baselines = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private decimal windowCredits;
    private decimal sessionCumulativeCredits;
    private int backoffStage;
    private int guardHitCount;
    private bool guardHit;
    private string? lastCheckpoint;
    private RepartitionReasonCode? lastReason;

    public MainCostGuard(MainCostGuardOptions? options = null)
    {
        this.options = options ?? MainCostGuardOptions.Default;
    }

    public MainCostGuardTelemetry Telemetry
    {
        get
        {
            lock (gate)
            {
                return new(windowCredits, CurrentThreshold, backoffStage, sessionCumulativeCredits,
                    guardHitCount, lastCheckpoint, lastReason, guardHit);
            }
        }
    }

    public bool IsGuardHit { get { lock (gate) return guardHit; } }
    public decimal CurrentWindowCredits { get { lock (gate) return windowCredits; } }
    public decimal CurrentThreshold { get { lock (gate) return options.WindowThresholds[Math.Min(backoffStage, options.WindowThresholds.Count - 1)]; } }
    public int BackoffStage { get { lock (gate) return backoffStage; } }

    /// <summary>Accept one cumulative native session sample and charge its delta.</summary>
    public MainCostGuardUsageDelta AcceptUsage(NativeUsageRecord usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (gate)
        {
            var current = CumulativeUsage.From(usage);
            baselines.TryGetValue(usage.SessionId, out var baseline);

            // A provider/session counter can reset. Treat the new sample as a new
            // baseline rather than manufacturing a negative charge.
            var delta = current.DeltaFrom(baseline);
            baselines[usage.SessionId] = current;

            var credits = CalculateCredits(delta);
            windowCredits += credits;
            sessionCumulativeCredits += credits;
            if (!guardHit && windowCredits >= CurrentThreshold)
            {
                guardHit = true;
                guardHitCount++;
            }

            return new MainCostGuardUsageDelta(
                usage.SessionId,
                delta.UncachedInputTokens,
                delta.CachedInputTokens,
                delta.OutputTokens,
                delta.ReasoningTokens,
                delta.Calls,
                credits);
        }
    }

    /// <summary>Reset to the initial window for a newly started MAIN package.</summary>
    public void StartPackage()
    {
        lock (gate)
        {
            ResetWindow(0, "START_PACKAGE", null);
        }
    }

    /// <summary>Reset to the initial window after a Worker-owned package closes.</summary>
    public void CompleteWorkerPackage()
    {
        lock (gate)
        {
            ResetWindow(0, "WORKER_PACKAGE_COMPLETED", null);
        }
    }

    /// <summary>
    /// Close a MAIN investigation window and advance 25 -> 40 -> 60 -> 60.
    /// Worker completion is explicitly reset to the initial stage.
    /// </summary>
    public void RecordCheckpoint(WorkOwner owner, RepartitionReasonCode reason)
    {
        lock (gate)
        {
            if (!RepartitionReasons.IsAllowed(owner, reason))
                throw new ArgumentException("The checkpoint reason does not match the owner.", nameof(reason));

            if (owner == WorkOwner.Main)
            {
                // Every legal MAIN checkpoint closes the observable window;
                // only an unresolved investigation earns the next backoff stage.
                if (reason == RepartitionReasonCode.INVESTIGATION_UNRESOLVED)
                    backoffStage = Math.Min(backoffStage + 1, options.WindowThresholds.Count - 1);
                ResetWindow(backoffStage, "MAIN", reason);
                return;
            }

            CompleteWorkerPackage();
            lastCheckpoint = "WORKER";
            lastReason = reason;
        }
    }

    public void RecordCheckpoint(RepartitionTrigger trigger, WorkOwner owner, RepartitionReasonCode reason)
    {
        lock (gate)
        {
            RecordCheckpoint(owner, reason);
            lastCheckpoint = trigger.ToString();
        }
    }

    public void RecordCheckpoint(RepartitionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            RecordCheckpoint(record.Decision, record.Reason);
            lastCheckpoint = record.Trigger.ToString();
        }
    }

    private void ResetWindow(int stage, string checkpoint, RepartitionReasonCode? reason)
    {
        backoffStage = Math.Clamp(stage, 0, options.WindowThresholds.Count - 1);
        windowCredits = 0m;
        guardHit = false;
        lastCheckpoint = checkpoint;
        lastReason = reason;
    }

    private decimal CalculateCredits(MainCostGuardUsageDelta delta) =>
        delta.UncachedInputTokens / 1_000_000m * options.CostWeights.UncachedInputPerMillion
        + delta.CachedInputTokens / 1_000_000m * options.CostWeights.CachedInputPerMillion
        + delta.OutputTokens / 1_000_000m * options.CostWeights.OutputPerMillion;

    private readonly record struct CumulativeUsage(
        long UncachedInputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long ReasoningTokens,
        long Calls)
    {
        public static CumulativeUsage From(NativeUsageRecord usage) => new(
            Math.Max(0L, usage.UncachedInputTokens > 0
                ? usage.UncachedInputTokens
                : usage.InputTokens - usage.CachedInputTokens),
            Math.Max(0L, usage.CachedInputTokens),
            Math.Max(0L, usage.OutputTokens),
            Math.Max(0L, usage.ReasoningTokens),
            Math.Max(0L, usage.Calls));

        public MainCostGuardUsageDelta DeltaFrom(CumulativeUsage previous) => new(
            string.Empty,
            PositiveDelta(UncachedInputTokens, previous.UncachedInputTokens),
            PositiveDelta(CachedInputTokens, previous.CachedInputTokens),
            PositiveDelta(OutputTokens, previous.OutputTokens),
            PositiveDelta(ReasoningTokens, previous.ReasoningTokens),
            PositiveDelta(Calls, previous.Calls),
            0m);

        private static long PositiveDelta(long current, long previous) => current >= previous ? current - previous : current;
    }
}
