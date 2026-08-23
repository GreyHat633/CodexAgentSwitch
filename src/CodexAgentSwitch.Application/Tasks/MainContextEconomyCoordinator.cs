using System.Collections.Concurrent;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

/// <summary>
/// Coordinates context pressure decisions for already-created main-agent threads.
/// It deliberately has no thread creation or rollover behavior.
/// </summary>
public sealed class MainContextEconomyCoordinator : IMainContextEconomyCoordinator, IAsyncDisposable
{
    private readonly ContextEconomyOptions options;
    private readonly ContextPressureEstimator estimator;
    private readonly ContextEconomyPolicy policy;
    private readonly CompactionStateMachine stateMachine = new();
    private readonly CompactionEffectivenessEvaluator evaluator;
    private readonly IMainContextEconomyStateStore? stateStore;
    private readonly ConcurrentDictionary<string, ThreadContext> threads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IMainAgentSession, Func<MainAgentEvent, Task>> subscriptions = new();

    public MainContextEconomyCoordinator(
        ContextEconomyOptions? options = null,
        IMainContextEconomyStateStore? stateStore = null)
    {
        this.options = options ?? new ContextEconomyOptions();
        this.options.Validate();
        this.stateStore = stateStore;
        estimator = new ContextPressureEstimator(this.options);
        policy = new ContextEconomyPolicy(this.options);
        evaluator = new CompactionEffectivenessEvaluator(this.options);
    }

    public MainContextEconomyCoordinator(
        IMainAgentSession session,
        ContextEconomyOptions? options = null,
        IMainContextEconomyStateStore? stateStore = null)
        : this(options, stateStore) => defaultSession = session ?? throw new ArgumentNullException(nameof(session));

    private readonly IMainAgentSession? defaultSession;

    public async Task BindThreadAsync(
        string threadId,
        IMainAgentSession session,
        Func<CancellationToken, Task<ContextControlValidation>>? controlGuard = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(session);
        var context = threads.GetOrAdd(threadId, id => new ThreadContext(id, session));
        if (!ReferenceEquals(context.Session, session))
            throw new InvalidOperationException($"Thread '{threadId}' is already bound to another session.");
        context.SetControlGuard(controlGuard);
        Subscribe(session);
        await context.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.Loaded) return;
            var loaded = stateStore is null ? null : await stateStore.LoadAsync(threadId, cancellationToken).ConfigureAwait(false);
            if (loaded is not null && string.Equals(loaded.ThreadId, threadId, StringComparison.Ordinal))
                context.Restore(loaded.Normalize());
            context.Loaded = true;
        }
        finally { context.Gate.Release(); }
    }

    public async Task<ContextEconomyObservationResult> ObserveTurnAsync(
        string threadId,
        ContextTurnSample sample,
        bool safeBoundary = false,
        CancellationToken cancellationToken = default)
    {
        var context = await RequireContextAsync(threadId, cancellationToken).ConfigureAwait(false);
        await context.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var replaceCurrentTurn = !string.IsNullOrWhiteSpace(sample.TurnId)
                && context.State.Samples.LastOrDefault()?.TurnId is { } previousTurnId
                && string.Equals(previousTurnId, sample.TurnId, StringComparison.Ordinal);
            if (context.State.CooldownRemaining > 0 && sample.IsNormalMainTurn && !replaceCurrentTurn)
            {
                context.State.CooldownRemaining--;
                if (context.State.CooldownRemaining == 0 && context.State.State == ContextEconomyState.Cooldown)
                    context.State.State = stateMachine.Transition(ContextEconomyState.Cooldown, ContextEconomyTransition.CooldownExpired);
            }

            if (replaceCurrentTurn)
                context.State.Samples[^1] = sample;
            else
                context.State.Samples.Add(sample);
            Trim(context.State.Samples);
            if (context.State.State is ContextEconomyState.Verifying or ContextEconomyState.VerifyDeferred)
            {
                var replacePostCompactionTurn = !string.IsNullOrWhiteSpace(sample.TurnId)
                    && context.State.PostCompactionSamples.LastOrDefault()?.TurnId is { } previousPostTurnId
                    && string.Equals(previousPostTurnId, sample.TurnId, StringComparison.Ordinal);
                if (replacePostCompactionTurn)
                    context.State.PostCompactionSamples[^1] = sample;
                else
                    context.State.PostCompactionSamples.Add(sample);
                Trim(context.State.PostCompactionSamples);
                // Evaluate a rolling 2-3 turn window; a one-off large context is
                // deferred until it naturally leaves that verification window.
                var result = evaluator.Evaluate(context.State.PreCompactionSamples, context.State.PostCompactionSamples.TakeLast(3).ToArray());
                if (result.Classification != CompactionEffectiveness.Unknown)
                {
                    context.State.LastEffectiveness = result;
                    context.State.PostCompactionInput = result.PostInputMedian is null ? null : (long?)decimal.ToInt64(result.PostInputMedian.Value);
                    context.State.PostCompactionPressure = sample.ContextWindowTokens is > 0 && context.State.PostCompactionInput is > 0
                        ? Math.Clamp(context.State.PostCompactionInput.Value / (decimal)sample.ContextWindowTokens.Value, 0m, 1m)
                        : null;
                    context.State.State = result.Classification switch
                    {
                        CompactionEffectiveness.Effective or CompactionEffectiveness.Marginal => context.State.State == ContextEconomyState.VerifyDeferred
                            ? ContextEconomyState.Cooldown
                            : stateMachine.Transition(context.State.State, result.Classification == CompactionEffectiveness.Effective ? ContextEconomyTransition.VerificationEffective : ContextEconomyTransition.VerificationMarginal),
                        CompactionEffectiveness.Ineffective => stateMachine.Transition(context.State.State, ContextEconomyTransition.VerificationIneffective),
                        CompactionEffectiveness.Deferred => ContextEconomyState.VerifyDeferred,
                        _ => context.State.State,
                    };
                    if (context.State.State == ContextEconomyState.Cooldown)
                    {
                        context.State.CooldownRemaining = options.CooldownMainTurns;
                        context.State.Attempts = 0;
                    }
                }
            }

            var baseline = context.State.Samples.Take(Math.Max(0, context.State.Samples.Count - 1)).TakeLast(options.BaselineTurns).ToArray();
            var telemetry = estimator.Estimate(sample, baseline, context.State.Samples);
            context.State.LastTelemetry = telemetry;
            var decision = policy.Evaluate(telemetry, context.State.CooldownRemaining);
            context.State.LastReason = decision.Reason;
            // A large post-compaction context is explicitly verification-deferred;
            // do not let its transient pressure immediately schedule another request.
            var deferPolicy = sample.IsLargeNewContext && context.State.State == ContextEconomyState.VerifyDeferred;
            var protectionBlocked = context.State.State == ContextEconomyState.ContextProtectionBlocked;
            if (!deferPolicy && !protectionBlocked)
            {
                if (decision.Band == ContextPressureBand.HardProtection)
                    context.State.State = stateMachine.Transition(context.State.State, ContextEconomyTransition.HardProtectionDetected);
                else if (decision.Action == ContextEconomyAction.RequireCompaction)
                    context.State.State = stateMachine.Transition(context.State.State, ContextEconomyTransition.MandatoryDetected);
                else if (decision.Action == ContextEconomyAction.MarkCandidate && context.State.State == ContextEconomyState.Idle)
                    context.State.State = stateMachine.Transition(context.State.State, ContextEconomyTransition.CandidateDetected);
            }

            ContextEconomyCompactionResult? compaction = null;
            var shouldCompact = safeBoundary && context.State.State is ContextEconomyState.Candidate or ContextEconomyState.PendingSafeBoundary or ContextEconomyState.CompactFailed or ContextEconomyState.Ineffective;
            if (shouldCompact && context.State.CooldownRemaining == 0 && !context.HasActiveCompaction)
                compaction = await CompactLockedAsync(context, cancellationToken).ConfigureAwait(false);
            await SaveLockedAsync(context, cancellationToken).ConfigureAwait(false);
            return new(decision, context.State.State, compaction is not null, compaction);
        }
        finally { context.Gate.Release(); }
    }

    public async Task<ContextEconomyCompactionResult?> CompactAtSafeBoundaryAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        var context = await RequireContextAsync(threadId, cancellationToken).ConfigureAwait(false);
        await context.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.State.CooldownRemaining > 0 || context.State.State is not (ContextEconomyState.Candidate or ContextEconomyState.PendingSafeBoundary or ContextEconomyState.CompactFailed or ContextEconomyState.Ineffective))
                return null;
            if (context.HasActiveCompaction) return null;
            var result = await CompactLockedAsync(context, cancellationToken).ConfigureAwait(false);
            await SaveLockedAsync(context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { context.Gate.Release(); }
    }

    public async Task<StructuredCompactionObservation> ObserveStructuredCompactionAsync(
        string threadId,
        CompactionTrigger trigger,
        DateTimeOffset compactedAt,
        IReadOnlyList<ContextTurnSample>? preCompactionSamples = null,
        CancellationToken cancellationToken = default)
    {
        if (trigger == CompactionTrigger.Unknown)
            throw new ArgumentException("A structured compaction trigger is required.", nameof(trigger));
        var context = await RequireContextAsync(threadId, cancellationToken).ConfigureAwait(false);
        var activeTransaction = context.AcceptOrQueueStructuredCompaction();
        await context.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            context.ClearQueuedStructuredCompaction();
            if (context.State.StructuredCompactedAt is { } prior && compactedAt <= prior)
                return new(threadId, context.State.LastCompactionTrigger, prior, context.State.State, true,
                    "Duplicate structured compacted event suppressed.");

            if (!activeTransaction || context.State.PreCompactionSamples.Length == 0)
                context.State.PreCompactionSamples = preCompactionSamples is null or { Count: 0 }
                    ? context.State.Samples.TakeLast(3).ToArray()
                    : preCompactionSamples.TakeLast(3).ToArray();
            var lastPreSample = preCompactionSamples?.LastOrDefault();
            var preTelemetry = lastPreSample is null
                ? context.State.LastTelemetry
                : estimator.Estimate(lastPreSample, context.State.Samples.TakeLast(options.BaselineTurns).ToArray(), context.State.Samples);
            context.State.PreCompactionInput = preTelemetry?.CurrentInput;
            context.State.PreCompactionPressure = preTelemetry?.Pressure;
            context.State.Samples.Clear();
            context.State.PostCompactionSamples.Clear();
            context.State.LastEffectiveness = null;
            context.State.PostCompactionInput = null;
            context.State.PostCompactionPressure = null;
            context.State.StructuredCompactedAt = compactedAt;
            context.State.LastCompactionCompletedAt = compactedAt;
            context.State.LastCompactionTrigger = activeTransaction ? CompactionTrigger.AgentSwitch : trigger;
            context.State.State = ContextEconomyState.Verifying;
            context.State.Attempts = 0;
            context.State.CooldownRemaining = Math.Max(1, options.CooldownMainTurns);
            context.State.LastReason = activeTransaction
                ? "Structured compacted lifecycle completed the Agent Switch transaction."
                : $"Structured compacted lifecycle observed from {trigger}.";
            await SaveLockedAsync(context, cancellationToken).ConfigureAwait(false);
            return new(threadId, context.State.LastCompactionTrigger, compactedAt, context.State.State, false, context.State.LastReason);
        }
        finally { context.Gate.Release(); }
    }

    public async Task<ContextEconomySnapshot?> GetSnapshotAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var context = await RequireContextAsync(threadId, cancellationToken).ConfigureAwait(false);
        await context.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return context.ToSnapshot().Normalize(); }
        finally { context.Gate.Release(); }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (stateStore is null) return;
        foreach (var context in threads.Values)
        {
            await context.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await SaveLockedAsync(context, cancellationToken).ConfigureAwait(false); }
            finally { context.Gate.Release(); }
        }
    }

    private async Task<ContextEconomyCompactionResult> CompactLockedAsync(ThreadContext context, CancellationToken cancellationToken)
    {
        if (context.InFlight is not null)
            return await context.InFlight.ConfigureAwait(false);
        context.InFlight = CompactWithRetriesAsync(context, cancellationToken);
        try { return await context.InFlight.ConfigureAwait(false); }
        finally { context.InFlight = null; }
    }

    private async Task<ContextEconomyCompactionResult> CompactWithRetriesAsync(ThreadContext context, CancellationToken cancellationToken)
    {
        ContextEconomyCompactionResult? result = null;
        while (context.State.Attempts < options.MaxCompactionAttemptsPerEpisode)
        {
            var control = await context.ValidateControlAsync(cancellationToken).ConfigureAwait(false);
            if (!control.Allowed)
            {
                context.State.State = ContextEconomyState.ContextProtectionBlocked;
                context.State.LastReason = $"Compaction control rejected: {control.Reason}";
                return new(false, false, false, context.State.Attempts, context.State.State, null, context.State.LastReason);
            }

            context.State.Attempts++;
            var attempt = context.State.Attempts;
            context.State.State = stateMachine.Transition(context.State.State, ContextEconomyTransition.SafeBoundaryReached);
            context.State.PreCompactionSamples = context.State.Samples.TakeLast(3).ToArray();
            context.State.PreCompactionInput = context.State.LastTelemetry?.CurrentInput;
            context.State.PreCompactionPressure = context.State.LastTelemetry?.Pressure;
            context.State.PostCompactionSamples.Clear();
            context.State.LastCompactionTrigger = CompactionTrigger.AgentSwitch;
            context.State.LastCompactionRequestAt = DateTimeOffset.UtcNow;
            context.State.LastCompactionRequestId = Guid.NewGuid().ToString("D");
            var lifecycle = context.BeginLifecycle();
            try
            {
                if (!context.TryStartCompactionRequest(lifecycle))
                {
                    result = new(true, false, true, attempt, ContextEconomyState.Verifying, null,
                        "Structured compaction arrived before the Agent Switch RPC; request cancelled.");
                }
                else
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestTimeout.CancelAfter(options.CompactionTimeout);
                    var acknowledgement = await context.Session.CompactThreadAsync(context.ThreadId, requestTimeout.Token).ConfigureAwait(false);
                    if (!acknowledgement.RequestAccepted)
                    {
                        result = Failed(context, attempt, false, false, "Compaction request was not acknowledged.");
                    }
                    else
                    {
                        var terminal = await lifecycle.WaitAsync(options.CompactionTimeout, cancellationToken).ConfigureAwait(false);
                        result = terminal
                            ? new(
                                true,
                                true,
                                true,
                                attempt,
                                ContextEconomyState.Verifying,
                                null,
                                "Compaction completed on the bound thread.",
                                context.State.LastCompactionRequestAt,
                                lifecycle.StartedAt,
                                lifecycle.CompletedAt,
                                context.State.LastCompactionRequestId)
                            : Failed(context, attempt, true, false, "Compaction acknowledgement timed out before a same-thread terminal lifecycle.");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = Failed(context, attempt, false, false, "Compaction timed out.");
            }
            catch (Exception ex)
            {
                result = Failed(context, attempt, false, false, $"Compaction failed: {ex.Message}");
            }
            finally
            {
                context.State.LastCompactionStartedAt = lifecycle.StartedAt ?? context.State.LastCompactionStartedAt;
                context.State.LastCompactionCompletedAt = lifecycle.CompletedAt ?? context.State.LastCompactionCompletedAt;
                context.EndLifecycle(lifecycle);
            }
            var finalResult = (result ?? Failed(context, attempt, false, false, "Compaction did not produce a result.")) with
            {
                RequestedAt = context.State.LastCompactionRequestAt,
                StartedAt = lifecycle.StartedAt ?? context.State.LastCompactionStartedAt,
                CompletedAt = lifecycle.CompletedAt ?? context.State.LastCompactionCompletedAt,
                RequestId = context.State.LastCompactionRequestId,
            };
            context.State.LastReason = finalResult.Reason;
            if (finalResult.Succeeded)
            {
                context.State.State = ContextEconomyState.Verifying;
                return finalResult;
            }
            if (context.State.Attempts >= options.MaxCompactionAttemptsPerEpisode)
            {
                context.State.State = stateMachine.Transition(context.State.State, ContextEconomyTransition.RetryExhausted);
                return finalResult with { State = context.State.State, Reason = finalResult.Reason + " Retry limit reached; protection is blocked." };
            }
            context.State.State = ContextEconomyState.CompactFailed;
        }
        return result ?? Failed(context, context.State.Attempts, false, false, "Compaction did not run.");
    }

    private static ContextEconomyCompactionResult Failed(ThreadContext context, int attempt, bool ack, bool terminal, string reason) =>
        new(
            false,
            ack,
            terminal,
            attempt,
            ContextEconomyState.CompactFailed,
            null,
            reason,
            context.State.LastCompactionRequestAt,
            context.State.LastCompactionStartedAt,
            context.State.LastCompactionCompletedAt,
            context.State.LastCompactionRequestId);

    private async Task<ThreadContext> RequireContextAsync(string threadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (threads.TryGetValue(threadId, out var context) && context.Loaded) return context;
        if (defaultSession is null)
            throw new InvalidOperationException($"Thread '{threadId}' is not bound. Call BindThreadAsync first.");
        await BindThreadAsync(threadId, defaultSession, cancellationToken: cancellationToken).ConfigureAwait(false);
        return threads[threadId];
    }

    private async Task SaveLockedAsync(ThreadContext context, CancellationToken cancellationToken)
    {
        if (stateStore is not null)
            await stateStore.SaveAsync(context.ToSnapshot().Normalize(), cancellationToken).ConfigureAwait(false);
    }

    private void Subscribe(IMainAgentSession session)
    {
        subscriptions.GetOrAdd(session, value =>
        {
            Func<MainAgentEvent, Task> handler = OnEventAsync;
            value.EventReceived += handler;
            return handler;
        });
    }

    private async Task OnEventAsync(MainAgentEvent value)
    {
        if (threads.TryGetValue(value.ThreadId, out var context))
        {
            var belongsToActiveRequest = context.AcceptLifecycle(value);
            if (value.Kind == MainAgentEventKind.CompactionCompleted && !belongsToActiveRequest)
            {
                await ObserveStructuredCompactionAsync(
                    value.ThreadId,
                    CompactionTrigger.HostAutomatic,
                    DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
        }
    }

    private static void Trim(List<ContextTurnSample> samples)
    {
        const int max = 64;
        if (samples.Count > max) samples.RemoveRange(0, samples.Count - max);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in subscriptions)
            pair.Key.EventReceived -= pair.Value;
        subscriptions.Clear();
        foreach (var context in threads.Values) context.Gate.Dispose();
        await Task.CompletedTask;
    }

    private sealed class ThreadContext(string threadId, IMainAgentSession session)
    {
        public string ThreadId { get; } = threadId;
        public IMainAgentSession Session { get; } = session;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public MutableState State { get; } = new();
        public bool Loaded { get; set; }
        public Task<ContextEconomyCompactionResult>? InFlight { get; set; }
        private object LifecycleSync { get; } = new();
        private LifecycleWait? Lifecycle { get; set; }
        private bool CompactionRequestStarted { get; set; }
        private bool StructuredCompactionQueued { get; set; }
        private bool CompactionActive { get; set; }
        private Func<CancellationToken, Task<ContextControlValidation>>? ControlGuard { get; set; }

        public void SetControlGuard(Func<CancellationToken, Task<ContextControlValidation>>? controlGuard)
        {
            lock (LifecycleSync) ControlGuard = controlGuard;
        }

        public async Task<ContextControlValidation> ValidateControlAsync(CancellationToken cancellationToken)
        {
            Func<CancellationToken, Task<ContextControlValidation>>? guard;
            lock (LifecycleSync) guard = ControlGuard;
            if (guard is null) return ContextControlValidation.Permit();
            try
            {
                return await guard(cancellationToken).ConfigureAwait(false)
                    ?? ContextControlValidation.Reject("Control guard returned no result.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ContextControlValidation.Reject($"Control guard failed: {exception.Message}");
            }
        }

        public LifecycleWait BeginLifecycle()
        {
            lock (LifecycleSync)
            {
                var lifecycle = new LifecycleWait();
                Lifecycle = lifecycle;
                CompactionRequestStarted = false;
                if (StructuredCompactionQueued)
                {
                    StructuredCompactionQueued = false;
                    lifecycle.AcceptStructuredCompacted();
                }
                return lifecycle;
            }
        }

        public bool TryStartCompactionRequest(LifecycleWait lifecycle)
        {
            lock (LifecycleSync)
            {
                if (!ReferenceEquals(Lifecycle, lifecycle) || lifecycle.IsCompleted) return false;
                CompactionRequestStarted = true;
                return true;
            }
        }

        public bool AcceptOrQueueStructuredCompaction()
        {
            lock (LifecycleSync)
            {
                if (Lifecycle is null)
                {
                    StructuredCompactionQueued = true;
                    return false;
                }
                Lifecycle.AcceptStructuredCompacted();
                return CompactionRequestStarted;
            }
        }

        public void ClearQueuedStructuredCompaction()
        {
            lock (LifecycleSync) StructuredCompactionQueued = false;
        }

        public bool HasActiveCompaction
        {
            get { lock (LifecycleSync) return CompactionActive; }
        }

        public bool AcceptLifecycle(MainAgentEvent value)
        {
            lock (LifecycleSync)
            {
                if (value.Kind == MainAgentEventKind.CompactionStarted) CompactionActive = true;
                else if (value.Kind == MainAgentEventKind.CompactionCompleted) CompactionActive = false;
                var belongsToActiveRequest = Lifecycle is not null;
                Lifecycle?.Accept(value);
                return belongsToActiveRequest;
            }
        }

        public void EndLifecycle(LifecycleWait lifecycle)
        {
            lock (LifecycleSync)
            {
                if (!ReferenceEquals(Lifecycle, lifecycle)) return;
                Lifecycle = null;
                CompactionRequestStarted = false;
            }
        }

        public void Restore(ContextEconomySnapshot snapshot)
        {
            State.State = snapshot.State == ContextEconomyState.Compacting
                ? ContextEconomyState.ContextProtectionBlocked
                : snapshot.State;
            State.Attempts = snapshot.Attempts;
            State.CooldownRemaining = snapshot.CooldownRemaining;
            State.Samples.AddRange(snapshot.Samples ?? []);
            State.PreCompactionSamples = snapshot.PreCompactionSamples?.ToArray() ?? [];
            State.PostCompactionSamples.AddRange(snapshot.PostCompactionSamples ?? []);
            State.LastReason = snapshot.LastReason;
            State.LastCompactionTrigger = snapshot.LastCompactionTrigger;
            State.StructuredCompactedAt = snapshot.StructuredCompactedAt;
            State.PreCompactionPressure = snapshot.PreCompactionPressure;
            State.PreCompactionInput = snapshot.PreCompactionInput;
            State.PostCompactionPressure = snapshot.PostCompactionPressure;
            State.PostCompactionInput = snapshot.PostCompactionInput;
            State.LastEffectiveness = snapshot.LastEffectiveness;
            State.LastCompactionRequestAt = snapshot.LastCompactionRequestedAt;
            State.LastCompactionStartedAt = snapshot.LastCompactionStartedAt;
            State.LastCompactionCompletedAt = snapshot.LastCompactionCompletedAt;
            State.LastCompactionRequestId = snapshot.LastCompactionRequestId;
            if (snapshot.State == ContextEconomyState.Compacting)
                State.LastReason = "Previous compaction outcome is unresolved after restart; automatic retry is blocked.";
        }

        public ContextEconomySnapshot ToSnapshot() => new(ThreadId, State.State, State.Attempts, State.CooldownRemaining,
            State.Samples.ToArray(), State.PreCompactionSamples.ToArray(), State.LastReason, State.PostCompactionSamples.ToArray(), DateTimeOffset.UtcNow,
            State.LastCompactionTrigger, State.StructuredCompactedAt, State.PreCompactionPressure, State.PreCompactionInput,
            State.PostCompactionPressure, State.PostCompactionInput, State.LastEffectiveness,
            State.LastCompactionRequestAt, State.LastCompactionStartedAt, State.LastCompactionCompletedAt,
            State.LastCompactionRequestId);
    }

    private sealed class MutableState
    {
        public ContextEconomyState State { get; set; }
        public int Attempts { get; set; }
        public int CooldownRemaining { get; set; }
        public List<ContextTurnSample> Samples { get; } = [];
        public ContextTurnSample[] PreCompactionSamples { get; set; } = [];
        public List<ContextTurnSample> PostCompactionSamples { get; } = [];
        public CompactionEffectivenessResult? LastEffectiveness { get; set; }
        public string? LastReason { get; set; }
        public ContextPressureTelemetry? LastTelemetry { get; set; }
        public CompactionTrigger LastCompactionTrigger { get; set; }
        public DateTimeOffset? StructuredCompactedAt { get; set; }
        public decimal? PreCompactionPressure { get; set; }
        public long? PreCompactionInput { get; set; }
        public decimal? PostCompactionPressure { get; set; }
        public long? PostCompactionInput { get; set; }
        public DateTimeOffset? LastCompactionRequestAt { get; set; }
        public DateTimeOffset? LastCompactionStartedAt { get; set; }
        public DateTimeOffset? LastCompactionCompletedAt { get; set; }
        public string? LastCompactionRequestId { get; set; }
    }

    private sealed class LifecycleWait
    {
        private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;

        public bool IsCompleted => completion.Task.IsCompleted;
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? CompletedAt { get; private set; }

        public void Accept(MainAgentEvent value)
        {
            if (value.Kind == MainAgentEventKind.CompactionStarted)
            {
                StartedAt ??= DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref started, 1);
            }
            else if (value.Kind == MainAgentEventKind.CompactionCompleted && Volatile.Read(ref started) == 1)
            {
                CompletedAt ??= DateTimeOffset.UtcNow;
                completion.TrySetResult(true);
            }
        }

        public void AcceptStructuredCompacted()
        {
            Interlocked.Exchange(ref started, 1);
            StartedAt ??= DateTimeOffset.UtcNow;
            CompletedAt ??= DateTimeOffset.UtcNow;
            completion.TrySetResult(true);
        }

        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try { return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        }
    }
}
