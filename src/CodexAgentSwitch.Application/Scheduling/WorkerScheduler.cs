using System.Collections.Concurrent;
using System.Threading.Channels;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;

namespace CodexAgentSwitch.Application.Scheduling;

public sealed class WorkerScheduler(
    IEnumerable<IWorkerExecutor> executors,
    ISchedulerTaskRepository repository,
    IClock clock,
    IEnumerable<ITaskPacketResolver>? resolvers = null,
    IEnumerable<IDelegationPolicyGuard>? guards = null,
    IEnumerable<ISchedulerResultObserver>? observers = null) : IWorkerScheduler
{
    private readonly IReadOnlyList<IWorkerExecutor> executors = executors.ToArray();
    private readonly IReadOnlyList<ITaskPacketResolver> resolvers = resolvers?.ToArray() ?? [];
    private readonly IReadOnlyList<IDelegationPolicyGuard> guards = guards?.ToArray() ?? [];
    private readonly IReadOnlyList<ISchedulerResultObserver> observers = observers?.ToArray() ?? [];
    private readonly Channel<QueuedWork> queue = Channel.CreateUnbounded<QueuedWork>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly ConcurrentDictionary<string, ScheduledDelegation> tasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim repartitionTelemetryLock = new(1, 1);
    private CancellationTokenSource? workerCancellation;
    private Task? workerLoop;
    private SchedulerState state = SchedulerState.Stopped;
    private string? faultMessage;

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

    public async Task<RepartitionTelemetry> RecordRepartitionAsync(
        string taskGroupId,
        RepartitionTrigger trigger,
        WorkOwner decision,
        RepartitionReasonCode reason,
        string workSummary,
        string? workerIdentity = null,
        string? result = null,
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

        var record = new RepartitionRecord(0, trigger, decision, reason, workSummary, workerIdentity, result);
        record.Validate();

        await repartitionTelemetryLock.WaitAsync(cancellationToken);
        try
        {
            var history = await repository.ListRepartitionsAsync(taskGroupId, cancellationToken);
            var sequence = history.Count == 0 ? 1 : history.Max(item => item.Sequence) + 1;
            var telemetry = new RepartitionTelemetry(
                taskGroupId,
                sequence,
                clock.UtcNow.ToUniversalTime(),
                record.Trigger,
                record.Decision,
                record.Reason,
                record.WorkSummary,
                record.WorkerIdentity,
                record.Result);
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

    private sealed record QueuedWork(TaskPacket Packet, IWorkerExecutor Executor, TaskCompletionSource<WorkerResultPacket> Completion);
}
