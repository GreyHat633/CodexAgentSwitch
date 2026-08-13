using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Infrastructure.Persistence;

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

    [Fact]
    public async Task Adopted_exact_worker_package_closes_mechanically_but_ambiguous_package_does_not()
    {
        var leases = new LeaseMemoryRepository();
        await using var scheduler = new WorkerScheduler([new NativeWorkerExecutor()], new MemoryRepository(), new AdvancingClock(), leaseRepository: leases);
        await scheduler.StartAsync();
        var exact = Packet("CAS-EXACT-COMPLETE-026", "cas_luna_worker");
        await scheduler.RecordRepartitionAsync(exact.TaskId, RepartitionTrigger.INITIAL_LOCALIZATION_COMPLETE,
            WorkOwner.Worker, RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "exact", "cas_luna_worker", null,
            exact.TaskId, exact.WorkingDirectory, "Implementation", exact.Scope, null);
        await scheduler.DispatchAsync(exact);
        await scheduler.ReportNativeResultAsync(new WorkerResultPacket(exact.TaskId, DelegationState.ResultReceived, "done", [], [], [], []));
        await scheduler.MarkReviewingAsync(exact.TaskId);
        await scheduler.MarkAdoptedAsync(exact.TaskId, "adopted");
        Assert.Equal(WorkPackageLeaseStatus.COMPLETED,
            Assert.Single(await leases.ListAsync(), item => item.PackageId == exact.TaskId).Status);

        var ambiguous = Packet("CAS-AMBIGUOUS-COMPLETE-026", "cas_luna_worker");
        await scheduler.RecordRepartitionAsync(ambiguous.TaskId, RepartitionTrigger.PHASE_CHANGE,
            WorkOwner.Worker, RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "ambiguous", "cas_luna_worker", null,
            "different-package-id", ambiguous.WorkingDirectory, "Implementation", ambiguous.Scope, null);
        await scheduler.DispatchAsync(ambiguous);
        await scheduler.ReportNativeResultAsync(new WorkerResultPacket(ambiguous.TaskId, DelegationState.ResultReceived, "done", [], [], [], []));
        await scheduler.MarkReviewingAsync(ambiguous.TaskId);
        await scheduler.MarkAdoptedAsync(ambiguous.TaskId, "adopted");
        Assert.NotEqual(WorkPackageLeaseStatus.COMPLETED,
            Assert.Single(await leases.ListAsync(), item => item.PackageId == "different-package-id").Status);
    }

    [Fact]
    public async Task Repartition_telemetry_is_sequenced_timestamped_and_read_in_order()
    {
        var clock = new AdvancingClock();
        var repository = new MemoryRepository();
        await using var scheduler = new WorkerScheduler([new FakeExecutor(WorkerTransport.ExternalProvider)], repository, clock);

        var first = await scheduler.RecordRepartitionAsync(
            "group-1",
            RepartitionTrigger.INITIAL_LOCALIZATION_COMPLETE,
            WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED,
            "Review the worker result.");
        var second = await scheduler.RecordRepartitionAsync(
            "group-1",
            RepartitionTrigger.WORKER_REVIEW_COMPLETE,
            WorkOwner.Worker,
            RepartitionReasonCode.BOUNDED_TESTING,
            "Run the bounded scheduler tests.",
            "worker-2",
            "ready");

        var history = await scheduler.ListRepartitionsAsync("group-1");
        Assert.Equal([1L, 2L], history.Select(item => item.Sequence));
        Assert.Equal(first.Record, history[0].Record);
        Assert.Equal(second.Record, history[1].Record);
        Assert.All(history, item => Assert.Equal(TimeSpan.Zero, item.RecordedAt.Offset));
    }

    [Fact]
    public async Task Repartition_telemetry_rejects_invalid_owner_reason_and_required_fields_before_persistence()
    {
        var repository = new MemoryRepository();
        await using var scheduler = new WorkerScheduler([new FakeExecutor(WorkerTransport.ExternalProvider)], repository, new AdvancingClock());

        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RecordRepartitionAsync(
            "group-1", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.BOUNDED_FIX, "invalid"));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RecordRepartitionAsync(
            "", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, "missing group"));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RecordRepartitionAsync(
            "group-1", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, ""));
        Assert.Empty(await scheduler.ListRepartitionsAsync("group-1"));
    }

    [Fact]
    public async Task Repartition_supersedes_different_package_in_same_working_directory()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? Path.GetTempPath(), "lease-supersession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new SqliteDatabase(Path.Combine(root, "leases.db"));
            await database.InitializeAsync();
            var leases = new SqliteWorkPackageLeaseRepository(database);
            await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(), leaseRepository: leases);
            await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main, RepartitionReasonCode.FINAL_INTEGRATION, "A", null, null, "pkg-A", root, "Implementation", [root], 0);
            await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.MODULE_COMPLETE, WorkOwner.Main, RepartitionReasonCode.FINAL_INTEGRATION, "B", null, null, "pkg-B", root, "Implementation", [root], 0);

            Assert.Null(await leases.GetActiveAsync("pkg-A", root));
            Assert.Equal("pkg-B", (await leases.GetActiveForWorkingDirectoryAsync(root))!.PackageId);
            Assert.Equal(3, (await leases.ListAsync()).Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sqlite_repartition_events_round_trip_append_only_in_sequence_order_with_utc_fields()
    {
        var root = Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "cas-tests");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, $"repartition-{Guid.NewGuid():N}.db");
        try
        {
            var database = new SqliteDatabase(databasePath);
            await database.InitializeAsync();
            var repository = new SqliteSchedulerTaskRepository(database);
            await repository.AppendRepartitionAsync(new RepartitionTelemetry(
                "group-sqlite", 2, new DateTimeOffset(2026, 8, 9, 1, 0, 0, TimeSpan.Zero),
                RepartitionTrigger.WORKER_REVIEW_COMPLETE, WorkOwner.Worker,
                RepartitionReasonCode.BOUNDED_TESTING, "Run tests", "worker-2", "passed"));
            await repository.AppendRepartitionAsync(new RepartitionTelemetry(
                "group-sqlite", 1, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
                RepartitionTrigger.INITIAL_LOCALIZATION_COMPLETE, WorkOwner.Main,
                RepartitionReasonCode.REVIEW_REQUIRED, "Review scope", null, null));

            var history = await repository.ListRepartitionsAsync("group-sqlite");
            Assert.Equal([1L, 2L], history.Select(item => item.Sequence));
            Assert.Equal(WorkOwner.Main, history[0].Decision);
            Assert.Equal(RepartitionReasonCode.BOUNDED_TESTING, history[1].Reason);
            Assert.Equal("worker-2", history[1].WorkerIdentity);
            Assert.Equal("passed", history[1].Result);
            Assert.All(history, item => Assert.Equal(TimeSpan.Zero, item.RecordedAt.Offset));
        }
        finally
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Default_active_worker_limit_is_concurrency_only_after_first_task_reaches_terminal_result()
    {
        await using var scheduler = new WorkerScheduler(
            [new FakeExecutor(WorkerTransport.ExternalProvider)],
            new MemoryRepository(),
            new AdvancingClock());
        await scheduler.StartAsync();

        var first = await scheduler.DispatchAsync(Packet("CAS-SERIAL-A", "deepseek-default"));
        Assert.Equal(DelegationState.ResultReceived, first.State);
        Assert.Equal(0, scheduler.Snapshot.ActiveTaskCount);

        var second = await scheduler.DispatchAsync(Packet("CAS-SERIAL-B", "deepseek-default"));
        Assert.Equal(DelegationState.ResultReceived, second.State);
        Assert.Equal(0, scheduler.Snapshot.ActiveTaskCount);
        Assert.Equal(2, (await scheduler.ListAsync()).Count);
    }

    [Fact]
    public async Task Main_cost_guard_uses_exact_session_and_matching_cwd_then_persists_cost_checkpoint()
    {
        const string root = "E:\\AISPace\\Phase6Main";
        var leases = new LeaseMemoryRepository();
        var source = new CountingUsageSource(new NativeUsageRecord(
            "session-main", root, null, "model", "high", "Sol", 1,
            25_000_000, 0, 25_000_000, 0, 0, 25_000_000, null, null, "synthetic", "test"));
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(),
            leaseRepository: leases, usageSource: source, guardCoordinator: new MainCostGuardCoordinator());
        await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, "main", null, null, "pkg", root, "Implementation", [root], null);

        var result = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("session-main", root, "apply_patch", "patch"));

        Assert.False(result.Allowed);
        Assert.Contains("CostCheckpoint", result.Reason, StringComparison.Ordinal);
        Assert.Equal(WorkPackageLeaseStatus.INVALID, (await leases.ListAsync()).Single().Status);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Worker_owned_mutation_is_denied_before_usage_scan_and_unknown_requires_safety()
    {
        const string root = "E:\\AISPace\\Phase6Worker";
        var leases = new LeaseMemoryRepository();
        var source = new CountingUsageSource(null);
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(),
            leaseRepository: leases, usageSource: source, guardCoordinator: new MainCostGuardCoordinator());
        await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Worker,
            RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "worker", null, null, "pkg", root, "Implementation", [root], null);

        var denied = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root, "apply_patch", "patch"));
        var unknown = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root, "shell", "mystery"));

        Assert.False(denied.Allowed);
        Assert.Equal(0, source.ReadCount);
        Assert.True(unknown.Allowed);
        Assert.True(unknown.RequiresSafetyPolicy);
    }

    [Fact]
    public async Task Pending_repartition_blocks_mutation_until_one_valid_ownership_resolution()
    {
        const string root = "E:\\AISPace\\PendingRepartition";
        var leases = new LeaseMemoryRepository();
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(), leaseRepository: leases);

        await scheduler.RecordRepartitionAsync("group-other", RepartitionTrigger.PHASE_CHANGE,
            WorkOwner.Main, RepartitionReasonCode.FINAL_INTEGRATION, "other", null, null,
            "pkg-other", root + "-other", "Implementation", [root + "-other"], null);

        await scheduler.EnqueueRepartitionTriggerAsync("group-pending", [RepartitionTrigger.PHASE_CHANGE], "phase changed", root);
        await scheduler.EnqueueRepartitionTriggerAsync("group-pending", [RepartitionTrigger.MODULE_COMPLETE], "module complete", root);

        var unrelated = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root + "-other", "apply_patch", "patch"));
        Assert.True(unrelated.Allowed);

        var blocked = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root, "apply_patch", "patch"));
        Assert.False(blocked.Allowed);
        Assert.Contains("pending", blocked.Reason, StringComparison.OrdinalIgnoreCase);

        var resolved = await scheduler.RecordRepartitionAsync("group-pending", RepartitionTrigger.WORK_CONVERGED,
            WorkOwner.Main, RepartitionReasonCode.FINAL_INTEGRATION, "resolved", null, null,
            "pkg", root, "Implementation", [root], null);
        Assert.Equal(2, resolved.PendingTriggersCleared);
        Assert.Equal([RepartitionTrigger.PHASE_CHANGE, RepartitionTrigger.MODULE_COMPLETE], resolved.CoalescedTriggers);
        Assert.Equal(1, resolved.OwnershipDecisionCount);
        Assert.Equal(1, resolved.HardGateDenials);
        Assert.True(resolved.LeaseActive);

        var allowed = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root, "apply_patch", "patch"));
        Assert.True(allowed.Allowed);

        await scheduler.EnqueueRepartitionTriggerAsync("group-worker", [RepartitionTrigger.PHASE_CHANGE], "worker phase", root);
        var worker = await scheduler.RecordRepartitionAsync("group-worker", RepartitionTrigger.WORK_CONVERGED,
            WorkOwner.Worker, RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "worker resolved", "worker", null,
            "pkg-worker", root, "Implementation", [root], null);
        Assert.Equal(1, worker.PendingTriggersCleared);
        var workerBlocked = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root, "apply_patch", "patch"));
        Assert.False(workerBlocked.Allowed);
        Assert.Contains("Worker", workerBlocked.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_repartition_survives_scheduler_restart_and_resolution_removes_persistence()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? Path.GetTempPath(), "cas-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new SqliteDatabase(Path.Combine(root, "scheduler.db"));
            await database.InitializeAsync();
            var repository = new SqliteSchedulerTaskRepository(database);
            const string workingDirectory = "E:\\AISPace\\PendingRestart";
            PendingRepartitionState? persistedBeforeRestart = null;

            await using (var first = new WorkerScheduler([], repository, new AdvancingClock()))
            {
                await first.StartAsync();
                await first.EnqueueRepartitionTriggerAsync("restart-group", [RepartitionTrigger.PHASE_CHANGE], "pending", workingDirectory);
                await first.EnqueueRepartitionTriggerAsync("restart-group", [RepartitionTrigger.MODULE_COMPLETE], "pending", workingDirectory + "\\.");
                await first.EnqueueRepartitionTriggerAsync("other-group", [RepartitionTrigger.PHASE_CHANGE], "other", workingDirectory);
                await first.EnqueueRepartitionTriggerAsync("restart-group", [RepartitionTrigger.PHASE_CHANGE], "isolated", workingDirectory + "-other");
                var persisted = await repository.ListPendingRepartitionsAsync();
                Assert.Equal(3, persisted.Count);
                Assert.Equal(2, persisted.Single(item => item.TaskGroupId == "restart-group" &&
                    item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase)).PendingTriggers.Count);
                var denied = await first.EvaluatePreToolUseAsync(new PreToolUseRequest("s", workingDirectory, "apply_patch", "patch"));
                Assert.False(denied.Allowed);
                await first.StopAsync(force: true);
            }

            persistedBeforeRestart = (await repository.ListPendingRepartitionsAsync()).Single(item => item.TaskGroupId == "restart-group" &&
                item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase));

            var restartClock = new AdvancingClock();
            await using (var second = new WorkerScheduler([], repository, restartClock))
            {
                await second.StartAsync();
                var restored = (await repository.ListPendingRepartitionsAsync()).Single(item => item.TaskGroupId == "restart-group" &&
                    item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(persistedBeforeRestart!.CreatedAt, restored.CreatedAt);
                var restoredAgain = (await repository.ListPendingRepartitionsAsync()).Single(item => item.TaskGroupId == "restart-group" &&
                    item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(restored.UpdatedAt, restoredAgain.UpdatedAt);
                var restoredDenied = await second.EvaluatePreToolUseAsync(new PreToolUseRequest("s", workingDirectory, "apply_patch", "patch"));
                Assert.False(restoredDenied.Allowed);
                var afterDenial = (await repository.ListPendingRepartitionsAsync()).Single(item => item.TaskGroupId == "restart-group" &&
                    item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(2, afterDenial.HardGateDenialCount);
                Assert.Equal(2, (await repository.ListPendingRepartitionsAsync())
                    .Single(item => item.TaskGroupId == "restart-group" && item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase))
                    .HardGateDenialCount);
                var resolved = await second.RecordRepartitionAsync("restart-group", RepartitionTrigger.WORK_CONVERGED,
                    WorkOwner.Main, RepartitionReasonCode.FINAL_INTEGRATION, "resolved", null, null,
                    "pkg", workingDirectory, "Implementation", [workingDirectory], null);
                Assert.Equal(2, resolved.PendingTriggersCleared);
            }

            var remaining = await repository.ListPendingRepartitionsAsync();
            Assert.DoesNotContain(remaining, item => item.TaskGroupId == "restart-group" &&
                item.WorkingDirectory.EndsWith("PendingRestart", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, remaining.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Resolving_one_pending_group_keeps_other_group_for_same_working_directory_gated()
    {
        const string root = "E:\\AISPace\\PendingRepartitionGroups";
        var leases = new LeaseMemoryRepository();
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(), leaseRepository: leases);
        await scheduler.EnqueueRepartitionTriggerAsync("group-a", [RepartitionTrigger.PHASE_CHANGE], "a", root);
        await scheduler.EnqueueRepartitionTriggerAsync("group-b", [RepartitionTrigger.MODULE_COMPLETE], "b", root);

        await scheduler.RecordRepartitionAsync("group-a", RepartitionTrigger.WORK_CONVERGED,
            WorkOwner.Main, RepartitionReasonCode.FINAL_INTEGRATION, "a resolved", null, null,
            "pkg-a", root, "Implementation", [root], null);

        var blocked = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("s", root, "apply_patch", "patch"));
        Assert.False(blocked.Allowed);
        Assert.Contains("pending", blocked.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Main_usage_from_unrelated_cwd_is_not_charged_as_a_fallback()
    {
        const string root = "E:\\AISPace\\Phase6Cwd";
        var leases = new LeaseMemoryRepository();
        var source = new CountingUsageSource(new NativeUsageRecord(
            "session-main", "E:\\AISPace\\OtherProject", null, "model", "high", "Sol", 1,
            25_000_000, 0, 25_000_000, 0, 0, 25_000_000, null, null, "synthetic", "test"));
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(),
            leaseRepository: leases, usageSource: source, guardCoordinator: new MainCostGuardCoordinator());
        await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, "main", null, null, "pkg", root, "Implementation", [root], null);

        var result = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("session-main", root, "apply_patch", "patch"));

        Assert.True(result.Allowed);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Main_usage_from_other_session_on_matching_cwd_is_not_charged()
    {
        const string root = "E:\\AISPace\\Phase6Session";
        var leases = new LeaseMemoryRepository();
        var source = new CountingUsageSource(new NativeUsageRecord(
            "session-other", root, null, "model", "high", "Sol", 1,
            25_000_000, 0, 25_000_000, 0, 0, 25_000_000, null, null, "synthetic", "test"));
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new AdvancingClock(),
            leaseRepository: leases, usageSource: source, guardCoordinator: new MainCostGuardCoordinator());
        await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, "main", null, null, "pkg", root, "Implementation", [root], null);

        var result = await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest("session-main", root, "apply_patch", "patch"));

        Assert.True(result.Allowed);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Cost_window_index_is_derived_and_backoff_resets_for_new_package_and_worker()
    {
        const string root = "E:\\AISPace\\Phase6Index";
        var repository = new MemoryRepository();
        var leases = new LeaseMemoryRepository();
        await using var scheduler = new WorkerScheduler([], repository, new AdvancingClock(), leaseRepository: leases,
            guardCoordinator: new MainCostGuardCoordinator());

        var first = await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.REVIEW_REQUIRED, "main", null, null, "pkg-1", root, "Implementation", [root], 99);
        var second = await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.WORK_CONVERGED, WorkOwner.Main,
            RepartitionReasonCode.INVESTIGATION_UNRESOLVED, "main", null, null, "pkg-1", root, "Implementation", [root], 99);
        var third = await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.WORK_CONVERGED, WorkOwner.Main,
            RepartitionReasonCode.INVESTIGATION_UNRESOLVED, "main", null, null, "pkg-1", root, "Implementation", [root], 99);
        var fourth = await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.WORK_CONVERGED, WorkOwner.Main,
            RepartitionReasonCode.INVESTIGATION_UNRESOLVED, "main", null, null, "pkg-1", root, "Implementation", [root], 99);
        var newPackage = await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Main,
            RepartitionReasonCode.FINAL_INTEGRATION, "new", null, null, "pkg-2", root, "Implementation", [root], 99);
        var worker = await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Worker,
            RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "worker", "worker-1", null, "pkg-3", root, "Implementation", [root], 99);

        Assert.Equal([0, 1, 2, 2, 0, 0], new[] { first.CostWindowIndex, second.CostWindowIndex, third.CostWindowIndex, fourth.CostWindowIndex, newPackage.CostWindowIndex, worker.CostWindowIndex });
        Assert.Equal([0, 1, 2, 2, 0, 0], (await repository.ListRepartitionsAsync("group")).Select(item => item.CostWindowIndex).ToArray());
        Assert.Equal(0, (await leases.ListAsync()).OrderBy(item => item.PackageId, StringComparer.Ordinal).Last().CostWindowIndex);
    }

    [Fact]
    public async Task Incomplete_package_metadata_is_rejected_before_persistence_or_guard_mutation()
    {
        var repository = new MemoryRepository();
        await using var scheduler = new WorkerScheduler([], repository, new AdvancingClock(), guardCoordinator: new MainCostGuardCoordinator());

        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE,
            WorkOwner.Main, RepartitionReasonCode.REVIEW_REQUIRED, "invalid", null, null, "pkg", null, "Implementation", ["E:\\AISPace\\Phase6Invalid"], 99));

        Assert.Empty(await repository.ListRepartitionsAsync("group"));
    }

    [Fact]
    public void ToolHost_repartition_schema_requires_metadata_and_derives_cost_index()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "CodexAgentSwitch.ToolHost", "Program.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("required = new[] { \"taskGroupId\", \"trigger\", \"decision\", \"reason\", \"workSummary\", \"packageId\", \"workingDirectory\", \"packageKind\", \"declaredScopes\" }", source, StringComparison.Ordinal);
        Assert.Contains("name = \"queue_repartition\"", source, StringComparison.Ordinal);
        Assert.Contains("EnumArraySchema<RepartitionTrigger>()", source, StringComparison.Ordinal);
        Assert.Contains("decision.ValueKind == JsonValueKind.Number", source, StringComparison.Ordinal);
        Assert.Contains("leaseActive.GetBoolean()", source, StringComparison.Ordinal);
        Assert.Contains("dispatchReady.GetBoolean()", source, StringComparison.Ordinal);
        Assert.Contains("DispatchReady = true", source, StringComparison.Ordinal);
        Assert.Contains("cachedToolDefinitions", source, StringComparison.Ordinal);
        Assert.Contains("listChanged = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("costWindowIndex", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CodexAgentSwitch.ToolHost")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
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
        protected readonly List<RepartitionTelemetry> Repartitions = [];
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(Items.GetValueOrDefault(taskId));
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>(Items.Values.ToArray());
        public virtual Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default) { Items[task.Packet.TaskId] = task; return Task.CompletedTask; }
        public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RepartitionTelemetry>>(Repartitions.Where(item => item.TaskGroupId == taskGroupId).OrderBy(item => item.Sequence).ToArray());
        public Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default) { Repartitions.Add(telemetry); return Task.CompletedTask; }
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
        public DateTimeOffset LastUtcNow => new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero).AddTicks(Volatile.Read(ref ticks));
    }

    private sealed class CountingUsageSource(NativeUsageRecord? record) : IUsageSource
    {
        public int ReadCount { get; private set; }
        public IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return record is null ? [] : [record];
        }
    }

    private sealed class LeaseMemoryRepository : IWorkPackageLeaseRepository
    {
        private readonly Dictionary<string, WorkPackageLease> items = new(StringComparer.OrdinalIgnoreCase);
        public Task<WorkPackageLease?> GetActiveAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.TryGetValue(packageId, out var value) && value.Status is not WorkPackageLeaseStatus.INVALID and not WorkPackageLeaseStatus.COMPLETED ? value : null);
        public Task<WorkPackageLease?> GetActiveForWorkingDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.Values.LastOrDefault(item => string.Equals(item.WorkingDirectory, WorkPackageLease.NormalizePath(workingDirectory), StringComparison.OrdinalIgnoreCase)
                && item.Status is not WorkPackageLeaseStatus.INVALID and not WorkPackageLeaseStatus.COMPLETED));
        public Task<IReadOnlyList<WorkPackageLease>> ListAsync(string? packageId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkPackageLease>>(items.Values.ToArray());
        public Task SaveAsync(WorkPackageLease lease, CancellationToken cancellationToken = default) { items[lease.PackageId] = lease; return Task.CompletedTask; }
    }
}
