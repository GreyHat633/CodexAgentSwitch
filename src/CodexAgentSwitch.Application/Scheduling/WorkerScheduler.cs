using System.Collections.Concurrent;
using System.Threading.Channels;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Scheduling;

public sealed class WorkerScheduler(
    IEnumerable<IWorkerExecutor> executors,
    ISchedulerTaskRepository repository,
    IClock clock,
    IEnumerable<ITaskPacketResolver>? resolvers = null,
    IEnumerable<IDelegationPolicyGuard>? guards = null,
    IEnumerable<ISchedulerResultObserver>? observers = null,
    IWorkPackageLeaseRepository? leaseRepository = null,
    MainCostGuard? mainCostGuard = null,
    IUsageSource? usageSource = null,
    MainCostGuardCoordinator? guardCoordinator = null,
    IDelegationPreflight? preflight = null,
    IControlledTaskRuntime? contextRuntime = null,
    IMainContextEconomyCoordinator? contextEconomy = null) : IWorkerScheduler
{
    private readonly IReadOnlyList<IWorkerExecutor> executors = executors.ToArray();
    private readonly IReadOnlyList<ITaskPacketResolver> resolvers = resolvers?.ToArray() ?? [];
    private readonly IReadOnlyList<IDelegationPolicyGuard> guards = guards?.ToArray() ?? [];
    private readonly IReadOnlyList<ISchedulerResultObserver> observers = observers?.ToArray() ?? [];
    private readonly IWorkPackageLeaseRepository? leaseRepository = leaseRepository;
    private readonly IUsageSource? usageSource = usageSource;
    private readonly MainCostGuardCoordinator guardCoordinator = guardCoordinator
        ?? new MainCostGuardCoordinator(initialGuard: mainCostGuard);
    private readonly IDelegationPreflight? preflight = preflight;
    private readonly IControlledTaskRuntime? contextRuntime = contextRuntime;
    private readonly IMainContextEconomyCoordinator? contextEconomy = contextEconomy;
    private readonly Channel<QueuedWork> queue = Channel.CreateUnbounded<QueuedWork>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly ConcurrentDictionary<string, ScheduledDelegation> tasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim repartitionTelemetryLock = new(1, 1);
    // Main-reported lifecycle triggers may arrive before the next ownership
    // boundary. Keep them coalesced by task group (and cwd for the mutation
    // gate) while retaining each event in append-only telemetry.
    private readonly ConcurrentDictionary<string, PendingRepartition> pendingRepartitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> pendingByWorkingDirectory = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? workerCancellation;
    private Task? workerLoop;
    private SchedulerState state = SchedulerState.Stopped;
    private string? faultMessage;
    private ContextEconomyRuntimeDiagnostics? lastContextEconomy;

    public event EventHandler<SchedulerSnapshot>? SnapshotChanged;

    public SchedulerSnapshot Snapshot => CreateSnapshot();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (state is SchedulerState.Ready or SchedulerState.Working or SchedulerState.Paused)
            {
                return;
            }

            foreach (var item in await repository.ListAsync(cancellationToken))
            {
                var recovered = item.State is DelegationState.Running
                    ? item with { State = DelegationState.Failed, FailureReason = "应用上次退出时任务仍在运行，未自动重放。", UpdatedAt = clock.UtcNow, CompletedAt = clock.UtcNow }
                    : item;
                tasks[item.Packet.TaskId] = recovered;
                if (!Equals(recovered, item))
                {
                    await repository.UpsertAsync(recovered, cancellationToken);
                }
            }

            workerCancellation = new CancellationTokenSource();
            state = ActiveCount() > 0 ? SchedulerState.Working : SchedulerState.Ready;
            faultMessage = null;
            workerLoop = RunAsync(workerCancellation.Token);
            Publish();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (state == SchedulerState.Stopped)
        {
            throw new InvalidOperationException("Scheduler 尚未启动。");
        }

        state = SchedulerState.Paused;
        Publish();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (state != SchedulerState.Paused)
        {
            return Task.CompletedTask;
        }

        state = ActiveCount() > 0 ? SchedulerState.Working : SchedulerState.Ready;
        Publish();
        return Task.CompletedTask;
    }

    public async Task StopAsync(bool force, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            var active = ActiveCount();
            if (active > 0 && !force)
            {
                throw new InvalidOperationException($"Scheduler 当前正在处理 {active} 个任务；请等待完成或确认立即停止。");
            }

            workerCancellation?.Cancel();
            if (workerLoop is not null)
            {
                try { await workerLoop.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
            }

            state = SchedulerState.Stopped;
            Publish();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<WorkerResultPacket> DispatchAsync(TaskPacket packet, CancellationToken cancellationToken = default)
    {
        foreach (var resolver in resolvers)
        {
            packet = await resolver.ResolveAsync(packet, cancellationToken);
        }
        packet.Validate();
        foreach (var guard in guards)
        {
            await guard.ValidateAsync(packet, cancellationToken);
        }
        if (state == SchedulerState.Stopped)
        {
            throw new InvalidOperationException("Scheduler 未启动，External Worker unavailable。");
        }

        if (state == SchedulerState.Paused)
        {
            throw new InvalidOperationException("Scheduler 已暂停，不接受新任务。");
        }

        if (state == SchedulerState.Faulted)
        {
            throw new InvalidOperationException($"Scheduler 异常：{faultMessage}");
        }

        if (tasks.TryGetValue(packet.TaskId, out var duplicate)
            && duplicate.State is DelegationState.Created or DelegationState.Delegated or DelegationState.Running or DelegationState.ResultReceived or DelegationState.Reviewing or DelegationState.Adopted)
        {
            throw new InvalidOperationException($"Task {packet.TaskId} 已处于 {duplicate.State}，禁止重复 dispatch。");
        }

        var executor = executors.FirstOrDefault(item => item.CanExecute(packet))
            ?? throw new InvalidOperationException($"没有可执行 Worker {packet.WorkerId} 的 Executor。");
        var now = clock.UtcNow;
        var created = new ScheduledDelegation(packet, executor.Transport, DelegationState.Created, now, now, null, null, null, null);
        tasks[packet.TaskId] = created;
        await repository.UpsertAsync(created, cancellationToken);
        var delegated = created with { State = DelegationState.Delegated, UpdatedAt = clock.UtcNow };
        tasks[packet.TaskId] = delegated;
        await repository.UpsertAsync(delegated, cancellationToken);
        state = SchedulerState.Working;
        Publish();

        if (executor.Transport == WorkerTransport.NativeCustomAgent)
        {
            var instruction = await executor.ExecuteAsync(packet, cancellationToken);
            tasks[packet.TaskId] = delegated with { Result = instruction, UpdatedAt = clock.UtcNow };
            await repository.UpsertAsync(tasks[packet.TaskId], cancellationToken);
            Publish();
            return instruction;
        }

        var completion = new TaskCompletionSource<WorkerResultPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new QueuedWork(packet, executor, completion), cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public Task<DelegationPreflightResult> DelegationPreflightAsync(
        DelegationPreflightRequest request,
        CancellationToken cancellationToken = default) =>
        preflight is DelegationPreflight concrete
            ? PreflightWithSchedulerAsync(concrete, request, cancellationToken)
            : preflight is null
            ? Task.FromResult(new DelegationPreflightResult(
                state is SchedulerState.Ready or SchedulerState.Working,
                false, false, false, false, false, false, false, false, false, false,
                null, null, null, null, "PREFLIGHT_UNAVAILABLE", ["PREFLIGHT_UNAVAILABLE"]))
            : preflight.EvaluateAsync(request, cancellationToken);

    public Task<DelegationPreflightResult> PreflightAsync(
        DelegationPreflightRequest request,
        CancellationToken cancellationToken = default) => DelegationPreflightAsync(request, cancellationToken);

    private Task<DelegationPreflightResult> PreflightWithSchedulerAsync(
        DelegationPreflight concrete,
        DelegationPreflightRequest request,
        CancellationToken cancellationToken)
    {
        concrete.AttachScheduler(() => state, ActiveCount);
        return concrete.EvaluateAsync(request, cancellationToken);
    }

    public async Task<WorkerResultPacket> ReportNativeResultAsync(WorkerResultPacket result, CancellationToken cancellationToken = default)
    {
        if (!tasks.TryGetValue(result.TaskId, out var existing) || existing.Transport != WorkerTransport.NativeCustomAgent)
        {
            throw new InvalidOperationException("未找到对应的 Native Worker 委派记录。");
        }

        var stateValue = result.State is DelegationState.Failed or DelegationState.Cancelled
            ? result.State
            : DelegationState.ResultReceived;
        var updated = existing with { State = stateValue, Result = result with { State = stateValue }, UpdatedAt = clock.UtcNow, CompletedAt = clock.UtcNow, FailureReason = result.FailureReason };
        tasks[result.TaskId] = updated;
        await repository.UpsertAsync(updated, cancellationToken);
        await NotifyResultAsync(updated, cancellationToken);
        if (stateValue == DelegationState.ResultReceived)
        {
            await TransitionLeaseAsync(result.TaskId, WorkPackageLifecycleEvent.WorkerTerminalResult, cancellationToken);
        }
        if (state != SchedulerState.Paused)
        {
            state = ActiveCount() > 0 ? SchedulerState.Working : SchedulerState.Ready;
        }
        Publish();
        return updated.Result!;
    }

    public Task<WorkerResultPacket> MarkReviewingAsync(string taskId, CancellationToken cancellationToken = default) =>
        TransitionResultAsync(taskId, DelegationState.Reviewing, null, cancellationToken);

    public Task<WorkerResultPacket> MarkAdoptedAsync(string taskId, string summary, CancellationToken cancellationToken = default) =>
        TransitionResultAsync(taskId, DelegationState.Adopted, summary, cancellationToken);

    public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledDelegation>>(tasks.Values.OrderByDescending(item => item.UpdatedAt).ToArray());

    public Task<RepartitionTelemetry> RecordRepartitionAsync(
        string taskGroupId,
        RepartitionTrigger trigger,
        WorkOwner decision,
        RepartitionReasonCode reason,
        string workSummary,
        string? workerIdentity = null,
        string? result = null,
        CancellationToken cancellationToken = default) =>
        RecordRepartitionAsync(taskGroupId, trigger, decision, reason, workSummary, workerIdentity, result,
            null, null, null, null, null, cancellationToken);

    public async Task<RepartitionTelemetry> EnqueueRepartitionTriggerAsync(string taskGroupId, IReadOnlyList<RepartitionTrigger> triggers, string workSummary, string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskGroupId) || triggers is null || triggers.Count == 0 || string.IsNullOrWhiteSpace(workSummary) || string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Task group, summary, and working directory are required.");
        await repartitionTelemetryLock.WaitAsync(cancellationToken);
        try
        {
            var key = $"{taskGroupId}|{WorkPackageLease.NormalizePath(workingDirectory)}";
            var pending = pendingRepartitions.GetOrAdd(key, _ => new PendingRepartition());
            foreach (var trigger in triggers) pending.Add(trigger);
            var groups = pendingByWorkingDirectory.GetOrAdd(WorkPackageLease.NormalizePath(workingDirectory), _ => new());
            groups[key] = 0;
            return new RepartitionTelemetry(taskGroupId, 0, clock.UtcNow.ToUniversalTime(), triggers[^1], WorkOwner.Main,
                RepartitionReasonCode.REVIEW_REQUIRED, workSummary, null, null, WorkingDirectory: workingDirectory,
                PendingTriggerCount: pending.Count, CoalescedTriggers: pending.Triggers.ToArray());
        }
        finally { repartitionTelemetryLock.Release(); }
    }

    public async Task<RepartitionTelemetry> RecordRepartitionAsync(
        string taskGroupId,
        RepartitionTrigger trigger,
        WorkOwner decision,
        RepartitionReasonCode reason,
        string workSummary,
        string? workerIdentity,
        string? result,
        string? packageId,
        string? workingDirectory,
        string? packageKind,
        IReadOnlyList<string>? declaredScopes,
        int? costWindowIndex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskGroupId))
        {
            throw new ArgumentException("Task group id is required.", nameof(taskGroupId));
        }

        if (string.IsNullOrWhiteSpace(workSummary))
        {
            throw new ArgumentException("Work summary is required.", nameof(workSummary));
        }

        var metadata = new object?[] { packageId, workingDirectory, packageKind, declaredScopes };
        if (metadata.Any(item => item is not null) && metadata.Any(item => item is null))
        {
            throw new ArgumentException("Complete package metadata (packageId, workingDirectory, packageKind, declaredScopes) is required.");
        }
        if (costWindowIndex is not null && metadata.All(item => item is null))
        {
            throw new ArgumentException("Cost window index cannot be supplied without complete package metadata.", nameof(costWindowIndex));
        }

        var record = new RepartitionRecord(0, trigger, decision, reason, workSummary, workerIdentity, result);
        record.Validate();

        var hasCompleteMetadata = packageId is not null && workingDirectory is not null
            && packageKind is not null && declaredScopes is not null;

        await repartitionTelemetryLock.WaitAsync(cancellationToken);
        try
        {
            var history = await repository.ListRepartitionsAsync(taskGroupId, cancellationToken);
            var sequence = history.Count == 0 ? 1 : history.Max(item => item.Sequence) + 1;
            var prior = leaseRepository is not null && workingDirectory is not null
                ? await leaseRepository.GetActiveForWorkingDirectoryAsync(workingDirectory, cancellationToken)
                : null;
            var packageGuard = workingDirectory is null
                ? null
                : guardCoordinator.ResolveForWorkingDirectory(workingDirectory);
            if (packageGuard is not null && decision == WorkOwner.Main && packageId is not null
                && (prior is null || !string.Equals(prior.PackageId, packageId, StringComparison.Ordinal)))
            {
                packageGuard.StartPackage();
            }
            if (packageGuard is not null)
            {
                packageGuard.RecordCheckpoint(trigger, decision, reason);
            }
            var derivedCostWindowIndex = packageGuard?.BackoffStage ?? costWindowIndex ?? 0;
            var clearedPending = 0;
            IReadOnlyList<RepartitionTrigger>? coalescedTriggers = null;
            var hardGateDenials = 0;
            var leaseActive = false;
            var telemetry = new RepartitionTelemetry(
                taskGroupId,
                sequence,
                clock.UtcNow.ToUniversalTime(),
                record.Trigger,
                record.Decision,
                record.Reason,
                record.WorkSummary,
                record.WorkerIdentity,
                record.Result,
                packageId,
                workingDirectory,
                packageKind,
                declaredScopes,
                derivedCostWindowIndex,
                0);
            var pendingKey = hasCompleteMetadata && workingDirectory is not null
                ? $"{taskGroupId}|{WorkPackageLease.NormalizePath(workingDirectory)}" : taskGroupId;
            if (!hasCompleteMetadata)
            {
                var legacyPending = pendingRepartitions.GetOrAdd(pendingKey, _ => new PendingRepartition());
                legacyPending.Add(trigger);
            }
            if (hasCompleteMetadata && pendingRepartitions.TryRemove(pendingKey, out var pending))
            {
                clearedPending = pending.Count;
                coalescedTriggers = pending.Triggers.ToArray();
                hardGateDenials = pending.HardGateDenials;
                if (workingDirectory is not null
                    && pendingByWorkingDirectory.TryGetValue(WorkPackageLease.NormalizePath(workingDirectory), out var groups))
                {
                    groups.TryRemove(pendingKey, out _);
                    if (groups.IsEmpty) pendingByWorkingDirectory.TryRemove(WorkPackageLease.NormalizePath(workingDirectory), out _);
                }
            }
            if (leaseRepository is not null && hasCompleteMetadata)
            {
                if (prior is not null)
                {
                    prior.Invalidate("A newer repartition superseded this lease.");
                    await leaseRepository.SaveAsync(prior, cancellationToken);
                }

                var lease = new WorkPackageLease(
                    packageId!, taskGroupId, workingDirectory!, decision, packageKind!, reason, trigger,
                    telemetry.RecordedAt, derivedCostWindowIndex, declaredScopes!,
                    decision == WorkOwner.Main ? WorkPackageLeaseStatus.MAIN_OWNED : WorkPackageLeaseStatus.WORKER_OWNED);
                await leaseRepository.SaveAsync(lease, cancellationToken);
                leaseActive = true;
            }
            if (!hasCompleteMetadata && workingDirectory is not null)
            {
                var groups = pendingByWorkingDirectory.GetOrAdd(WorkPackageLease.NormalizePath(workingDirectory), _ => new());
                groups[pendingKey] = 0;
            }
            telemetry = telemetry with { PendingTriggersCleared = clearedPending, PendingTriggerCount = 0,
                CoalescedTriggers = coalescedTriggers, OwnershipDecisionCount = 1,
                HardGateDenials = hardGateDenials, LeaseActive = leaseActive };
            await repository.AppendRepartitionAsync(telemetry, cancellationToken);
            return telemetry;
        }
        finally
        {
            repartitionTelemetryLock.Release();
        }
    }

    public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(
        string taskGroupId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskGroupId))
        {
            throw new ArgumentException("Task group id is required.", nameof(taskGroupId));
        }

        return repository.ListRepartitionsAsync(taskGroupId, cancellationToken);
    }

    public async Task<PreToolUseResult> EvaluatePreToolUseAsync(PreToolUseRequest request, CancellationToken cancellationToken = default)
    {
        var tool = request.ToolName?.Trim() ?? string.Empty;
        var command = tool.Equals("apply_patch", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("edit", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("write", StringComparison.OrdinalIgnoreCase) ? tool : request.ToolInput;
        var classification = MutationClassifier.Classify(command);
        if (classification.IsReadOnly)
        {
            return new(request.SessionId, tool, request.WorkingDirectory, classification.Kind.ToString(), true, false, "Read-only operation is allowed.");
        }
        if (classification.IsUnknown)
        {
            return new(request.SessionId, tool, request.WorkingDirectory, classification.Kind.ToString(), true, true, "Unknown operation was not classified; existing safety policy must decide.");
        }
        var lease = leaseRepository is null ? null : await leaseRepository.GetActiveForWorkingDirectoryAsync(request.WorkingDirectory, cancellationToken);
        var normalizedWorkingDirectory = WorkPackageLease.NormalizePath(request.WorkingDirectory);
        if (pendingByWorkingDirectory.TryGetValue(normalizedWorkingDirectory, out var pendingGroups) && !pendingGroups.IsEmpty)
        {
            foreach (var key in pendingGroups.Keys) if (pendingRepartitions.TryGetValue(key, out var pendingState)) pendingState.RecordHardGateDenial();
            return new(request.SessionId, tool, request.WorkingDirectory, classification.Kind.ToString(), false, false,
                "Mutation denied: a repartition decision is pending; require MAIN/WORKER ownership resolution.");
        }
        if (lease?.Status == WorkPackageLeaseStatus.WORKER_OWNED)
        {
            return new(request.SessionId, tool, request.WorkingDirectory, classification.Kind.ToString(), false, false,
                "Mutation denied: the current package is owned by Worker.");
        }

        if (lease?.Status == WorkPackageLeaseStatus.MAIN_OWNED && usageSource is not null
            && !string.IsNullOrWhiteSpace(request.SessionId))
        {
            var guard = guardCoordinator.Resolve(request.WorkingDirectory, request.SessionId);
            var requestedCwd = WorkPackageLease.NormalizePath(request.WorkingDirectory);
            var usage = usageSource.Read(cancellationToken)
                .Where(item => string.Equals(item.SessionId, request.SessionId, StringComparison.Ordinal)
                    && (string.Equals(item.AgentRole, "Sol", StringComparison.Ordinal)
                        || string.Equals(item.AgentRole, "Main", StringComparison.Ordinal))
                    && (string.Equals(NormalizeOptional(item.Cwd), requestedCwd, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(NormalizeOptional(item.Project), requestedCwd, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(item => item.EndedAt ?? item.StartedAt)
                .FirstOrDefault();
            if (usage is not null)
            {
                guard.AcceptUsage(usage);
            }

            if (guard.IsGuardHit)
            {
                if (lease.TryTransition(WorkPackageLifecycleEvent.CostCheckpoint, out _))
                {
                    await leaseRepository!.SaveAsync(lease, cancellationToken);
                }
                return new(request.SessionId, tool, request.WorkingDirectory, classification.Kind.ToString(), false, false,
                    "MAIN normalized-credit cost guard reached; ownership lease invalidated at CostCheckpoint.");
            }
        }
        var decision = new MainToolOwnershipGate(lease).Evaluate(command, request.WorkingDirectory);
        return new(request.SessionId, tool, request.WorkingDirectory, classification.Kind.ToString(), decision.Allowed, false, decision.Message);
    }

    public async Task<MainContextBoundaryResult> ObserveMainContextBoundaryAsync(
        MainContextBoundaryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (contextRuntime is null || contextEconomy is null || usageSource is null)
            return BoundaryFailure(request.ThreadId, "Context economy runtime is unavailable.");
        if (string.IsNullOrWhiteSpace(request.ThreadId)
            || !string.Equals(request.ThreadId, request.SessionId, StringComparison.Ordinal)
            || !string.Equals(request.Source, "vscode", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.Boundary, "stop", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(request.WorkingDirectory))
            return BoundaryFailure(request.ThreadId, "The explicit source=vscode Stop binding is missing or inconsistent.");
        if (tasks.Values.Any(item => item.State is DelegationState.ResultReceived or DelegationState.Reviewing))
            return BoundaryFailure(request.ThreadId, "A Worker terminal result still requires Main review; compaction boundary deferred.");

        var cwd = WorkPackageLease.NormalizePath(request.WorkingDirectory);
        var usage = usageSource.Read(cancellationToken)
            .Where(item => string.Equals(item.SessionId, request.SessionId, StringComparison.Ordinal)
                && string.Equals(item.SessionSource, "vscode", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeOptional(item.Cwd), cwd, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.EndedAt ?? item.StartedAt)
            .FirstOrDefault();
        if (usage?.LatestInputTokens is null)
            return new(request.ThreadId, false, false, ContextEconomyState.Idle, false, false,
                "No exact source=vscode usage sample is available for this thread and cwd.");

        try
        {
            await contextRuntime.EnsureStartedAsync(cancellationToken);
            await contextRuntime.MainAgent.BindExistingThreadAsync(
                request.ThreadId,
                request.SessionId,
                "vscode",
                request.WorkingDirectory,
                cancellationToken);
            await contextEconomy.BindThreadAsync(request.ThreadId, contextRuntime.MainAgent, cancellationToken);
            if (usage.LastStructuredCompactedAt is { } compactedAt)
            {
                var snapshot = await contextEconomy.GetSnapshotAsync(request.ThreadId, cancellationToken);
                if (snapshot?.StructuredCompactedAt is null || compactedAt > snapshot.StructuredCompactedAt)
                {
                    await contextEconomy.ObserveStructuredCompactionAsync(
                        request.ThreadId,
                        CompactionTrigger.HostAutomatic,
                        compactedAt,
                        BuildPreCompactionSamples(usage, compactedAt),
                        cancellationToken);
                }
            }
            var observation = await contextEconomy.ObserveTurnAsync(
                request.ThreadId,
                new ContextTurnSample(
                    usage.LatestInputTokens.Value,
                    usage.LatestCachedInputTokens ?? 0,
                    null,
                    usage.ContextWindowTokens,
                    CapturedAt: usage.EndedAt ?? usage.StartedAt,
                    NativeInputTokens: usage.LatestInputTokens.Value),
                safeBoundary: true,
                cancellationToken);
            var current = await contextEconomy.GetSnapshotAsync(request.ThreadId, cancellationToken);
            if (current is not null)
            {
                lastContextEconomy = new(
                    current.ThreadId, current.State, current.LastCompactionTrigger,
                    current.PreCompactionPressure, current.PreCompactionInput,
                    current.PostCompactionPressure, current.PostCompactionInput,
                    current.StructuredCompactedAt, current.LastEffectiveness?.Classification,
                    current.CooldownRemaining, current.LastReason ?? observation.Decision.Reason);
            }
            return new(
                request.ThreadId,
                true,
                true,
                observation.State,
                observation.CompactionRequested,
                observation.Compaction?.Succeeded == true,
                observation.Compaction?.Reason ?? observation.Decision.Reason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return BoundaryFailure(request.ThreadId, exception.Message);
        }
    }

    private static MainContextBoundaryResult BoundaryFailure(string threadId, string reason) =>
        new(threadId ?? string.Empty, false, false, ContextEconomyState.ContextProtectionBlocked, false, false, reason);

    private static IReadOnlyList<ContextTurnSample>? BuildPreCompactionSamples(NativeUsageRecord usage, DateTimeOffset compactedAt)
    {
        var inputs = usage.PreCompactionInputSamples;
        if (inputs is null or { Count: 0 })
            inputs = usage.PreCompactionInputTokens is > 0 ? [usage.PreCompactionInputTokens.Value] : null;
        if (inputs is null) return null;
        var cached = usage.PreCompactionCachedInputSamples;
        return inputs.Select((input, index) => new ContextTurnSample(
            input,
            cached is not null && index < cached.Count ? cached[index] : 0,
            ContextWindowTokens: usage.ContextWindowTokens,
            CapturedAt: compactedAt,
            NativeInputTokens: input)).ToArray();
    }

    public async Task<WorkPackageLease?> CompletePackageAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (leaseRepository is null) return null;
        var lease = await leaseRepository.GetActiveAsync(packageId, workingDirectory, cancellationToken);
        if (lease is null) return null;
        lease.OnPackageComplete();
        await leaseRepository.SaveAsync(lease, cancellationToken);
        return lease;
    }

    public async Task<SchedulerRuntimeDiagnostics> GetRuntimeDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        WorkPackageLease? lease = null;
        if (leaseRepository is not null)
        {
            lease = (await leaseRepository.ListAsync(cancellationToken: cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
        }
        var active = tasks.Values
            .Where(item => item.State is DelegationState.Created or DelegationState.Delegated or DelegationState.Running)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        var telemetry = lease is null
            ? null
            : (await repository.ListRepartitionsAsync(lease.TaskGroupId, cancellationToken))
                .Where(item => string.Equals(item.PackageId, lease.PackageId, StringComparison.Ordinal))
                .OrderByDescending(item => item.Sequence)
                .FirstOrDefault();
        var economy = lease is null
            ? new MainCostGuardTelemetry(0m, 0m, 0, 0m, 0, null, null, false)
            : guardCoordinator.ResolveForWorkingDirectory(lease.WorkingDirectory).Telemetry;
        return new SchedulerRuntimeDiagnostics(economy, lease?.Status, lease?.PackageId,
            active?.Packet.WorkerId ?? telemetry?.WorkerIdentity,
            lease?.InvalidReason ?? economy.LastReason?.ToString() ?? telemetry?.Reason.ToString(), economy.GuardHitCount,
            lastContextEconomy);
    }

    private static string NormalizeOptional(string? path) => string.IsNullOrWhiteSpace(path)
        ? string.Empty
        : WorkPackageLease.NormalizePath(path);

    public async ValueTask DisposeAsync()
    {
        if (state != SchedulerState.Stopped)
        {
            await StopAsync(true);
        }
        workerCancellation?.Dispose();
        lifecycle.Dispose();
        repartitionTelemetryLock.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(cancellationToken))
            {
                while (state == SchedulerState.Paused)
                {
                    await Task.Delay(100, cancellationToken);
                }

                var running = tasks[item.Packet.TaskId] with { State = DelegationState.Running, StartedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
                tasks[item.Packet.TaskId] = running;
                await repository.UpsertAsync(running, cancellationToken);
                state = SchedulerState.Working;
                Publish();
                try
                {
                    var result = await item.Executor.ExecuteAsync(item.Packet, cancellationToken);
                    var finalState = result.State == DelegationState.Failed ? DelegationState.Failed : DelegationState.ResultReceived;
                    var completed = running with { State = finalState, Result = result with { State = finalState }, UpdatedAt = clock.UtcNow, CompletedAt = clock.UtcNow, FailureReason = result.FailureReason };
                    tasks[item.Packet.TaskId] = completed;
                    await repository.UpsertAsync(completed, cancellationToken);
                    if (finalState == DelegationState.ResultReceived)
                    {
                        await TransitionLeaseAsync(item.Packet.TaskId, WorkPackageLifecycleEvent.WorkerTerminalResult, cancellationToken);
                    }
                    await NotifyResultAsync(completed, cancellationToken);
                    item.Completion.TrySetResult(completed.Result!);
                }
                catch (Exception exception)
                {
                    var failedResult = new WorkerResultPacket(item.Packet.TaskId, DelegationState.Failed, "Worker 执行失败。", [], [], [], [], FailureReason: exception.Message);
                    var failed = running with { State = DelegationState.Failed, Result = failedResult, UpdatedAt = clock.UtcNow, CompletedAt = clock.UtcNow, FailureReason = exception.Message };
                    tasks[item.Packet.TaskId] = failed;
                    await repository.UpsertAsync(failed, cancellationToken);
                    await NotifyResultAsync(failed, cancellationToken);
                    item.Completion.TrySetResult(failedResult);
                }
                finally
                {
                    if (state != SchedulerState.Paused)
                    {
                        state = ActiveCount() > 0 ? SchedulerState.Working : SchedulerState.Ready;
                    }
                    Publish();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            faultMessage = exception.Message;
            state = SchedulerState.Faulted;
            Publish();
        }
    }

    private async Task<WorkerResultPacket> TransitionResultAsync(string taskId, DelegationState target, string? summary, CancellationToken cancellationToken)
    {
        if (!tasks.TryGetValue(taskId, out var existing) || existing.Result is null)
        {
            throw new InvalidOperationException($"Task {taskId} 尚无可审查结果。");
        }

        if (target == DelegationState.Reviewing && existing.State != DelegationState.ResultReceived)
        {
            throw new InvalidOperationException("只有 RESULT_RECEIVED 任务可以进入 REVIEWING。");
        }

        if (target == DelegationState.Adopted && existing.State != DelegationState.Reviewing)
        {
            throw new InvalidOperationException("只有 REVIEWING 任务可以进入 ADOPTED。");
        }

        var result = existing.Result with { State = target, Summary = string.IsNullOrWhiteSpace(summary) ? existing.Result.Summary : summary };
        var updated = existing with { State = target, Result = result, UpdatedAt = clock.UtcNow };
        tasks[taskId] = updated;
        await repository.UpsertAsync(updated, cancellationToken);
        if (target == DelegationState.Adopted)
        {
            await CompleteAdoptedPackageWhenDeterministicAsync(updated, cancellationToken);
        }
        Publish();
        return result;
    }

    private int ActiveCount() => tasks.Values.Count(item => item.State is DelegationState.Created or DelegationState.Delegated or DelegationState.Running);

    private SchedulerSnapshot CreateSnapshot() => new(
        state,
        ActiveCount(),
        tasks.Values.Where(item => item.State is DelegationState.Created or DelegationState.Delegated or DelegationState.Running)
            .OrderBy(item => item.CreatedAt).ToArray(),
        faultMessage);

    private void Publish() => SnapshotChanged?.Invoke(this, CreateSnapshot());

    private async Task NotifyResultAsync(ScheduledDelegation task, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            await observer.OnResultAsync(task, cancellationToken);
        }
    }

    private async Task TransitionLeaseAsync(string taskId, WorkPackageLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        if (leaseRepository is null || !tasks.TryGetValue(taskId, out var task)) return;
        var lease = await leaseRepository.GetActiveAsync(taskId, task.Packet.WorkingDirectory, cancellationToken)
            ?? await leaseRepository.GetActiveForWorkingDirectoryAsync(task.Packet.WorkingDirectory, cancellationToken);
        if (lease is null || !lease.TryTransition(lifecycleEvent, out _)) return;
        await leaseRepository.SaveAsync(lease, cancellationToken);
    }

    private async Task CompleteAdoptedPackageWhenDeterministicAsync(ScheduledDelegation adopted, CancellationToken cancellationToken)
    {
        if (leaseRepository is null) return;
        var exact = await leaseRepository.GetActiveAsync(adopted.Packet.TaskId, adopted.Packet.WorkingDirectory, cancellationToken);
        var cwd = WorkPackageLease.NormalizePath(adopted.Packet.WorkingDirectory);
        var hasPending = pendingByWorkingDirectory.TryGetValue(cwd, out var pendingGroups) && !pendingGroups.IsEmpty;
        var hasUnresolvedResult = tasks.Values.Any(item =>
            !string.Equals(item.Packet.TaskId, adopted.Packet.TaskId, StringComparison.Ordinal)
            && string.Equals(WorkPackageLease.NormalizePath(item.Packet.WorkingDirectory), cwd, StringComparison.OrdinalIgnoreCase)
            && item.State is DelegationState.ResultReceived or DelegationState.Reviewing);
        if (exact?.Status == WorkPackageLeaseStatus.REVIEW && !hasPending && !hasUnresolvedResult)
        {
            exact.OnPackageComplete();
            await leaseRepository.SaveAsync(exact, cancellationToken);
            return;
        }

        // Ambiguous package identity or remaining review state keeps the
        // conservative lifecycle: completion still requires semantic intent.
        await TransitionLeaseAsync(adopted.Packet.TaskId, WorkPackageLifecycleEvent.WorkerReviewComplete, cancellationToken);
    }

    private sealed record QueuedWork(TaskPacket Packet, IWorkerExecutor Executor, TaskCompletionSource<WorkerResultPacket> Completion);

    private sealed class PendingRepartition
    {
        private readonly List<RepartitionTrigger> triggers = [];
        public int Count => triggers.Count;
        public IReadOnlyList<RepartitionTrigger> Triggers => triggers;
        private int hardGateDenials;
        public int HardGateDenials => Volatile.Read(ref hardGateDenials);
        public void Add(RepartitionTrigger trigger) => triggers.Add(trigger);
        public void RecordHardGateDenial() => Interlocked.Increment(ref hardGateDenials);
    }
}
