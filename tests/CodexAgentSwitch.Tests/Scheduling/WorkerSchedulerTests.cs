using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Scheduling;

namespace CodexAgentSwitch.Tests.Scheduling;

public sealed class WorkerSchedulerTests
{
    [Fact]
    public async Task External_plaintext_packet_reaches_executor_and_transitions_to_result()
    {
        var executor = new FakeExecutor(WorkerTransport.ExternalProvider);
        await using var scheduler = new WorkerScheduler([executor], new MemoryRepository(), new AdvancingClock());
        await scheduler.StartAsync();
        var packet = Packet("CAS-EXTERNAL-013-582941", "deepseek-default");

        var result = await scheduler.DispatchAsync(packet);

        Assert.Equal("CAS-EXTERNAL-013-582941", executor.Received!.Goal);
        Assert.Equal("CAS-EXTERNAL-013-582941", result.Summary);
        Assert.Equal(DelegationState.ResultReceived, result.State);
        Assert.Equal(SchedulerState.Ready, scheduler.Snapshot.State);
    }

    [Fact]
    public async Task Native_custom_role_always_uses_none_without_luna_special_case()
    {
        var packet = Packet("CAS-NATIVE-013", "cas_custom_terra_worker");
        var executor = new NativeWorkerExecutor();

        var result = await executor.ExecuteAsync(packet);

        Assert.Equal("cas_custom_terra_worker", result.NativeInvocation!.AgentRole);
        Assert.Equal("none", result.NativeInvocation.ForkTurns);
        Assert.Contains("agent_type=\"cas_custom_terra_worker\"", result.NativeInvocation.Instruction, StringComparison.Ordinal);
        Assert.Contains("fork_turns=\"none\"", result.NativeInvocation.Instruction, StringComparison.Ordinal);
        Assert.Contains("never omit it", result.NativeInvocation.Instruction, StringComparison.Ordinal);
        Assert.Contains("never use fork_turns=\"all\"", result.NativeInvocation.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_task_is_rejected_while_running()
    {
        var executor = new BlockingExecutor();
        await using var scheduler = new WorkerScheduler([executor], new MemoryRepository(), new AdvancingClock());
        await scheduler.StartAsync();
        var packet = Packet("CAS-DUPLICATE-013", "deepseek-default");
        var first = scheduler.DispatchAsync(packet);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.DispatchAsync(packet));
        Assert.Contains("禁止重复", exception.Message, StringComparison.Ordinal);
        executor.Complete();
        await first;
    }

    [Fact]
    public async Task Lifecycle_supports_stopped_ready_working_paused_and_exit_protection()
    {
        var executor = new BlockingExecutor();
        await using var scheduler = new WorkerScheduler([executor], new MemoryRepository(), new AdvancingClock());
        Assert.Equal(SchedulerState.Stopped, scheduler.Snapshot.State);
        await scheduler.StartAsync();
        Assert.Equal(SchedulerState.Ready, scheduler.Snapshot.State);
        await scheduler.PauseAsync();
        Assert.Equal(SchedulerState.Paused, scheduler.Snapshot.State);
        await scheduler.ResumeAsync();
        var running = scheduler.DispatchAsync(Packet("CAS-ACTIVE-013", "deepseek-default"));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SchedulerState.Working, scheduler.Snapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.StopAsync(false));
        executor.Complete();
        await running;
        await scheduler.StopAsync(false);
        Assert.Equal(SchedulerState.Stopped, scheduler.Snapshot.State);
    }

    [Fact]
    public async Task Infrastructure_loop_failure_sets_faulted()
    {
        var repository = new FailingRepository(failOnWrite: 3);
        await using var scheduler = new WorkerScheduler([new FakeExecutor(WorkerTransport.ExternalProvider)], repository, new AdvancingClock());
        await scheduler.StartAsync();
        _ = scheduler.DispatchAsync(Packet("CAS-FAULT-013", "deepseek-default"));

        await WaitUntilAsync(() => scheduler.Snapshot.State == SchedulerState.Faulted);
        Assert.Equal(SchedulerState.Faulted, scheduler.Snapshot.State);
        Assert.NotNull(scheduler.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task Worker_failure_transitions_running_task_to_failed()
    {
        await using var scheduler = new WorkerScheduler([new ThrowingExecutor()], new MemoryRepository(), new AdvancingClock());
        await scheduler.StartAsync();

        var result = await scheduler.DispatchAsync(Packet("CAS-WORKER-FAIL-013", "deepseek-default"));

        Assert.Equal(DelegationState.Failed, result.State);
        Assert.Contains("fixture worker failure", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(DelegationState.Failed, (await scheduler.ListAsync()).Single().State);
    }

    [Fact]
    public async Task Native_state_progresses_through_review_and_adoption()
    {
        await using var scheduler = new WorkerScheduler([new NativeWorkerExecutor()], new MemoryRepository(), new AdvancingClock());
        await scheduler.StartAsync();
        var packet = Packet("CAS-NATIVE-STATE-013", "cas_luna_worker");
        await scheduler.DispatchAsync(packet);
        var received = await scheduler.ReportNativeResultAsync(new WorkerResultPacket(
            packet.TaskId, DelegationState.ResultReceived, "done", ["evidence"], [], ["test"], []));
        Assert.Equal(DelegationState.ResultReceived, received.State);
        Assert.Equal(DelegationState.Reviewing, (await scheduler.MarkReviewingAsync(packet.TaskId)).State);
        Assert.Equal(DelegationState.Adopted, (await scheduler.MarkAdoptedAsync(packet.TaskId, "adopted")).State);
    }

    private static TaskPacket Packet(string nonce, string worker) => new(
        nonce,
        "project-1",
        "E:\\AISPace\\TestSpace",
        worker,
        nonce,
        ["src/Target.cs"],
        ["src/Target.cs"],
        [],
        ["nonce round-trips"],
        ["do not read session logs"],
        "Return the nonce.");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeExecutor(WorkerTransport transport) : IWorkerExecutor
    {
        public WorkerTransport Transport => transport;
        public TaskPacket? Received { get; private set; }
        public bool CanExecute(TaskPacket packet) => true;
        public Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default)
        {
            Received = packet;
            return Task.FromResult(new WorkerResultPacket(packet.TaskId, DelegationState.ResultReceived, packet.Goal, [], [], [], []));
        }
    }

    private sealed class BlockingExecutor : IWorkerExecutor
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerTransport Transport => WorkerTransport.ExternalProvider;
        public bool CanExecute(TaskPacket packet) => true;
        public async Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await completion.Task.WaitAsync(cancellationToken);
            return new WorkerResultPacket(packet.TaskId, DelegationState.ResultReceived, "done", [], [], [], []);
        }
        public void Complete() => completion.TrySetResult();
    }

    private sealed class ThrowingExecutor : IWorkerExecutor
    {
        public WorkerTransport Transport => WorkerTransport.ExternalProvider;
        public bool CanExecute(TaskPacket packet) => true;
        public Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("fixture worker failure");
    }

    private class MemoryRepository : ISchedulerTaskRepository
    {
        protected readonly Dictionary<string, ScheduledDelegation> Items = new(StringComparer.Ordinal);
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(taskId));
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>(Items.Values.ToArray());
        public virtual Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default) { Items[task.Packet.TaskId] = task; return Task.CompletedTask; }
    }

    private sealed class FailingRepository(int failOnWrite) : MemoryRepository
    {
        private int writes;
        public override Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default)
        {
            if (++writes == failOnWrite) throw new IOException("fixture persistence failure");
            return base.UpsertAsync(task, cancellationToken);
        }
    }

    private sealed class AdvancingClock : IClock
    {
        private long ticks;
        public DateTimeOffset UtcNow => new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Increment(ref ticks));
    }
}
