using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ControlledTaskContextEconomyIntegrationTests
{
    [Fact]
    public async Task Legacy_rollover_budget_no_longer_creates_a_second_main_thread()
    {
        var now = DateTimeOffset.UtcNow;
        var main = new RecordingMainAgent();
        var usage = new MemoryUsageLedger();
        var budget = new SessionContextBudget(new SessionContextBudgetOptions(
            compactAge: TimeSpan.FromDays(1), rolloverAge: TimeSpan.FromDays(2),
            compactTurns: 1, rolloverTurns: 2));
        var harness = CreateHarness(now, main, usage, budget);

        var started = await harness.Service.StartAsync("first", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        await harness.Service.ContinueAsync(first.Id, "second", useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);

        Assert.Equal("thread-old", completed.MainThreadId);
        Assert.Empty(main.RolloverCalls);
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-old" && turn.Prompt.Contains("second", StringComparison.Ordinal));

        await harness.Service.ContinueAsync(completed.Id, "third", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, completed.Id, minimumTurns: 3);
        Assert.Empty(main.RolloverCalls);
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-old" && turn.Prompt.Contains("third", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_turn_budget_no_longer_triggers_automatic_compaction()
    {
        var main = new RecordingMainAgent();
        var budget = new SessionContextBudget(new SessionContextBudgetOptions(
            compactAge: TimeSpan.FromDays(1), rolloverAge: TimeSpan.FromDays(2),
            compactTurns: 2, rolloverTurns: 100));
        var harness = CreateHarness(DateTimeOffset.UtcNow, main, new MemoryUsageLedger(), budget);
        var started = await harness.Service.StartAsync("first", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        await harness.Service.ContinueAsync(first.Id, "second", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);
        Assert.Empty(main.CompactedThreads);
        Assert.Empty(main.RolloverCalls);
        Assert.Contains(main.StartedTurns, turn => turn.ThreadId == "thread-old" && turn.Prompt.Contains("second", StringComparison.Ordinal));
        await harness.Service.ContinueAsync(first.Id, "third", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 3);
        Assert.Empty(main.CompactedThreads);
    }

    [Fact]
    public async Task Continue_below_budget_does_not_call_context_apis()
    {
        var main = new RecordingMainAgent();
        var harness = CreateHarness(DateTimeOffset.UtcNow, main, new MemoryUsageLedger(), new SessionContextBudget());
        var started = await harness.Service.StartAsync("first", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        await harness.Service.ContinueAsync(first.Id, "second", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);
        Assert.Empty(main.CompactedThreads);
        Assert.Empty(main.RolloverCalls);
    }

    [Fact]
    public async Task Unmanaged_task_creates_no_binding_and_subscribes_to_no_context_coordinator()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;

        var started = await harness.Service.StartAsync("ordinary", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Empty(harness.ManagedSessions.Values);
        Assert.Empty(harness.ContextEconomy.BoundThreads);
        Assert.Empty(harness.ContextEconomy.Observations);
    }

    [Fact]
    public async Task Managed_project_persists_official_session_and_connection_identity_before_binding()
    {
        var harness = CreateManagedHarness();

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "managed", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal(completed.Id, binding.TaskSessionId);
        Assert.Equal("thread-old", binding.ThreadId);
        Assert.Equal("app-session-a", binding.SessionId);
        Assert.Equal("app-server-a", binding.AppServerInstanceId);
        Assert.Equal(ManagedContextOwnershipState.Idle, binding.OwnershipState);
        Assert.NotNull(binding.LastSafeBoundaryAt);
        Assert.Equal(["thread-old"], harness.ContextEconomy.BoundThreads);
    }

    [Fact]
    public async Task Registered_project_without_an_applied_configuration_remains_zero_intervention()
    {
        var harness = CreateManagedHarness(managedProjectApplied: false);
        harness.Runtime.RecordingMain.EmitTokenUsage = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "disabled", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Empty(harness.ManagedSessions.Values);
        Assert.Empty(harness.ContextEconomy.BoundThreads);
        Assert.Empty(harness.ContextEconomy.Observations);
    }

    [Fact]
    public async Task Ownership_persistence_failure_disables_context_control_without_stopping_user_task()
    {
        var harness = CreateManagedHarness();
        harness.ManagedSessions.ThrowOnUpsert = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "continue safely", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        Assert.Empty(harness.ManagedSessions.Values);
        Assert.Empty(harness.ContextEconomy.BoundThreads);
    }

    [Fact]
    public async Task App_server_restart_invalidates_old_lease_before_acquiring_a_new_connection_identity()
    {
        var harness = CreateManagedHarness();
        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "first connection", harness.WorkingDirectory, useWorker: false);
        var first = await WaitForTerminalAsync(harness.Tasks, started.Id);
        var oldLease = Assert.Single(harness.ManagedSessions.Values.Values).OwnershipLeaseId;

        harness.Runtime.AppServerInstanceId = "app-server-b";
        await harness.Service.ContinueAsync(first.Id, "after restart", useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, first.Id, minimumTurns: 2);

        var current = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal("app-server-b", current.AppServerInstanceId);
        Assert.NotEqual(oldLease, current.OwnershipLeaseId);
        Assert.Contains(harness.ManagedSessions.History, item =>
            item.AppServerInstanceId == "app-server-a"
            && item.OwnershipLeaseId == oldLease
            && item.OwnershipState == ManagedContextOwnershipState.Lost);
    }

    [Fact]
    public async Task Deleting_completed_conversation_releases_managed_ownership_without_erasing_audit_record()
    {
        var harness = CreateManagedHarness();
        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "delete me", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);

        await harness.Service.DeleteConversationAsync(started.Id);

        Assert.Null(await harness.Tasks.GetAsync(started.Id));
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal(ManagedContextOwnershipState.Released, binding.OwnershipState);
    }

    [Fact]
    public async Task Managed_task_consumes_realtime_usage_and_marks_only_the_completed_turn_as_safe()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "usage", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);

        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.NotNull(binding.LastTokenUsageAt);
        Assert.NotNull(binding.LastSafeBoundaryAt);
        Assert.Equal(3, harness.ContextEconomy.Observations.Count);
        Assert.False(harness.ContextEconomy.Observations[0].SafeBoundary);
        Assert.False(harness.ContextEconomy.Observations[1].SafeBoundary);
        Assert.True(harness.ContextEconomy.Observations[2].SafeBoundary);
        Assert.Equal("turn-1", harness.ContextEconomy.Observations[2].Sample.TurnId);
        Assert.Equal(800, harness.ContextEconomy.Observations[2].Sample.InputTokens);
        Assert.Equal(1000, harness.ContextEconomy.Observations[2].Sample.ContextWindowTokens);
    }

    [Fact]
    public async Task Incomplete_tool_item_blocks_safe_boundary_without_stopping_the_user_turn()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.LeaveToolItemRunning = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "unsafe boundary", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Null(binding.LastSafeBoundaryAt);
        Assert.All(harness.ContextEconomy.Observations, value => Assert.False(value.SafeBoundary));
    }

    [Fact]
    public async Task Pending_approval_blocks_safe_boundary_even_if_the_turn_reports_terminal()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.LeaveApprovalPending = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "approval boundary", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Null(binding.LastSafeBoundaryAt);
        Assert.All(harness.ContextEconomy.Observations, value => Assert.False(value.SafeBoundary));
    }

    [Fact]
    public async Task Context_monitor_failure_faults_control_without_stopping_the_user_turn()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.ContextEconomy.ThrowOnObserve = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "monitor failure", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal(ManagedContextOwnershipState.Faulted, binding.OwnershipState);
        Assert.Null(binding.LastSafeBoundaryAt);
    }

    [Fact]
    public async Task Lease_change_before_safe_boundary_is_rejected_without_overwriting_the_new_owner()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.BeforeIdle = () =>
        {
            var current = Assert.Single(harness.ManagedSessions.Values.Values);
            harness.ManagedSessions.ReplaceForTest(current with { OwnershipLeaseId = "lease-from-another-controller" });
        };

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "lease race", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal("lease-from-another-controller", binding.OwnershipLeaseId);
        Assert.Equal(ManagedContextOwnershipState.Owned, binding.OwnershipState);
        var validation = Assert.Single(harness.ContextEconomy.ControlValidations);
        Assert.False(validation.Allowed);
        Assert.Contains("lease changed", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Idle_status_arriving_after_turn_completion_still_forms_the_safe_boundary()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.DelayIdleUntilAfterWait = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "late idle", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);

        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.NotNull(binding.LastSafeBoundaryAt);
        Assert.True(harness.ContextEconomy.Observations[^1].SafeBoundary);
    }

    [Fact]
    public async Task Completed_native_compaction_projects_verifying_state_and_timestamp_to_the_binding()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.ContextEconomy.ReturnSuccessfulCompaction = true;

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "compact", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);

        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal(ManagedContextOwnershipState.Verifying, binding.OwnershipState);
        Assert.NotNull(binding.LastSafeBoundaryAt);
        Assert.NotNull(binding.LastCompactionAt);
        Assert.NotNull(binding.LastCompactionRequestedAt);
        Assert.NotNull(binding.LastCompactionStartedAt);
        Assert.NotNull(binding.LastCompactionCompletedAt);
        Assert.Equal("request-fixture", binding.LastCompactionRequestId);
    }

    [Fact]
    public async Task Startup_recovery_marks_old_managed_lease_lost_without_reacquiring_control()
    {
        var harness = CreateManagedHarness();
        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "before restart", harness.WorkingDirectory, useWorker: false);
        await WaitForTerminalAsync(harness.Tasks, started.Id);
        var lease = Assert.Single(harness.ManagedSessions.Values.Values).OwnershipLeaseId;

        await harness.Service.RecoverAsync();

        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal(lease, binding.OwnershipLeaseId);
        Assert.Equal(ManagedContextOwnershipState.Lost, binding.OwnershipState);
        Assert.Single(harness.ContextEconomy.BoundThreads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Session_or_working_directory_drift_before_safe_boundary_is_rejected(bool sessionDrift)
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.BeforeIdle = () =>
        {
            if (sessionDrift)
                harness.Runtime.RecordingMain.ThreadSessionId = "session-from-other-controller";
            else
                harness.Runtime.RecordingMain.ThreadWorkingDirectory = Path.GetTempPath();
        };

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "identity drift", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Equal(ManagedContextOwnershipState.Lost, binding.OwnershipState);
        var validation = Assert.Single(harness.ContextEconomy.ControlValidations);
        Assert.False(validation.Allowed);
        Assert.Contains(
            sessionDrift ? "ThreadIdentityMissing" : "ProjectPathInvalid",
            validation.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_archived_before_safe_boundary_loses_control_without_compaction()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.BeforeIdle = () =>
            harness.Projects.Current = harness.Project with { IsArchived = true };

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "archive race", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        Assert.Equal(
            ManagedContextOwnershipState.Lost,
            Assert.Single(harness.ManagedSessions.Values.Values).OwnershipState);
        var validation = Assert.Single(harness.ContextEconomy.ControlValidations);
        Assert.False(validation.Allowed);
        Assert.Contains("ProjectArchived", validation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_loaded_thread_status_never_becomes_a_safe_boundary()
    {
        var harness = CreateManagedHarness();
        harness.Runtime.RecordingMain.EmitTokenUsage = true;
        harness.Runtime.RecordingMain.TerminalThreadStatus = "notLoaded";

        var started = await harness.Service.StartInProjectAsync(
            harness.Project.Id, "not loaded", harness.WorkingDirectory, useWorker: false);
        var completed = await WaitForTerminalAsync(harness.Tasks, started.Id);

        Assert.Equal(ControlledTaskStatus.Completed, completed.Status);
        var binding = Assert.Single(harness.ManagedSessions.Values.Values);
        Assert.Null(binding.LastSafeBoundaryAt);
        Assert.All(harness.ContextEconomy.Observations, value => Assert.False(value.SafeBoundary));
    }

    private static Harness CreateHarness(DateTimeOffset now, RecordingMainAgent main, MemoryUsageLedger usage, SessionContextBudget budget)
    {
        var profile = new Profile(Guid.NewGuid(), "test", new AgentSelection("model", "medium"),
            new WorkerPolicy(false, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
            new BudgetLimits(null, null, null, null, null, "CNY"), true, now, now, now);
        var profiles = new MemoryProfiles(profile);
        var tasks = new MemoryTasks();
        var clock = new FixedClock(now);
        var runtime = new FakeRuntime(main);
        var service = new ControlledTaskService(tasks, profiles, runtime,
            new TaskProfileSnapshotFactory(new EmptyProviders(), clock), new DelegationDecisionService(clock),
            new WorkerOrchestrator(new RejectingExternalFactory(), runtime, new ExternalProviderResolver()),
            usage, new WorkerUsageCollector(new CostCalculator()), clock, contextBudget: budget);
        var root = Environment.CurrentDirectory;
        return new Harness(service, tasks, root);
    }

    private static ManagedHarness CreateManagedHarness(bool managedProjectApplied = true)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new Profile(Guid.NewGuid(), "managed", new AgentSelection("model", "medium"),
            new WorkerPolicy(false, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.SingleAgent),
            new BudgetLimits(null, null, null, null, null, "CNY"), true, now, now, now);
        var root = Environment.CurrentDirectory;
        var snapshot = new NativeCodexAppliedSnapshot(
            profile.Id, profile.Name, "model", "medium", "NativeAgent", "cas_luna_worker",
            "gpt-5.6-luna", "openai", "high", 1, "Economic", "Supported", "fixture");
        var adaptation = managedProjectApplied
            ? new NativeCodexProjectAdaptation(
                profile.Id, profile.Name, Path.Combine(root, ".codex", "config.toml"), null,
                now, "managed", false, snapshot)
            : null;
        var project = new AgentProject(
            "project-managed", "Managed", root, false, now, now, profile.Id,
            adaptation);
        var profiles = new MemoryProfiles(profile);
        var tasks = new MemoryTasks();
        var clock = new FixedClock(now);
        var main = new RecordingMainAgent();
        var runtime = new FakeRuntime(main);
        var projects = new MemoryProjects(project);
        var managedSessions = new MemoryManagedSessions();
        var coordinator = new RecordingContextEconomyCoordinator();
        var service = new ControlledTaskService(
            tasks,
            profiles,
            runtime,
            new TaskProfileSnapshotFactory(new EmptyProviders(), clock),
            new DelegationDecisionService(clock),
            new WorkerOrchestrator(new RejectingExternalFactory(), runtime, new ExternalProviderResolver()),
            new MemoryUsageLedger(),
            new WorkerUsageCollector(new CostCalculator()),
            clock,
            projectRepository: projects,
            contextEconomy: coordinator,
            managedContextSessions: managedSessions,
            managedContextPolicy: new ManagedProjectContextPolicy());
        return new(service, tasks, root, project, runtime, projects, managedSessions, coordinator);
    }

    private static async Task<ControlledTaskSession> WaitForTerminalAsync(MemoryTasks tasks, string id, int minimumTurns = 1)
    {
        for (var i = 0; i < 300; i++)
        {
            var value = await tasks.GetAsync(id);
            if (value is not null && value.Turns.Count >= minimumTurns && value.Status == ControlledTaskStatus.Completed) return value;
            await Task.Delay(10);
        }
        throw new TimeoutException();
    }

    private sealed record Harness(ControlledTaskService Service, MemoryTasks Tasks, string WorkingDirectory);
    private sealed record ManagedHarness(
        ControlledTaskService Service,
        MemoryTasks Tasks,
        string WorkingDirectory,
        AgentProject Project,
        FakeRuntime Runtime,
        MemoryProjects Projects,
        MemoryManagedSessions ManagedSessions,
        RecordingContextEconomyCoordinator ContextEconomy);
    private sealed class FixedClock(DateTimeOffset value) : IClock { public DateTimeOffset UtcNow => value; }
    private sealed class MemoryTasks : IControlledTaskRepository
    {
        private readonly Dictionary<string, ControlledTaskSession> values = new();
        public Task UpsertAsync(ControlledTaskSession task, CancellationToken cancellationToken = default) { values[task.Id] = task; return Task.CompletedTask; }
        public Task<ControlledTaskSession?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(values.TryGetValue(id, out var value) ? value : null);
        public Task<IReadOnlyList<ControlledTaskSession>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ControlledTaskSession>>(values.Values.ToArray());
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) { values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class MemoryProfiles(Profile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Profile>>([profile]);
        public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(id == profile.Id ? profile : null);
        public Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(profile);
        public Task UpsertAsync(Profile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class EmptyProviders : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderConfiguration>>([]);
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ProviderConfiguration?>(null);
        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class MemoryUsageLedger(IEnumerable<UsageSnapshot>? initial = null) : IUsageLedgerRepository
    {
        private readonly List<UsageSnapshot> values = initial?.ToList() ?? [];
        public Task UpsertTaskGroupAsync(TaskGroupLedger ledger, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TaskGroupLedger?> GetTaskGroupAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<TaskGroupLedger?>(null);
        public Task<IReadOnlyList<TaskGroupLedger>> ListTaskGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskGroupLedger>>([]);
        public Task AppendUsageAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) { values.Add(snapshot); return Task.CompletedTask; }
        public Task<IReadOnlyList<UsageSnapshot>> ListUsageAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UsageSnapshot>>(values);
    }
    private sealed class FakeRuntime(RecordingMainAgent main) : IControlledTaskRuntime
    {
        public string? AppServerInstanceId { get; set; } = "app-server-a";
        public RecordingMainAgent RecordingMain => main;
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IMainAgentSession MainAgent => main;
        public IWorkerAdapter NativeWorker => throw new NotSupportedException();
    }
    private sealed class RejectingExternalFactory : IExternalWorkerAdapterFactory { public IWorkerAdapter Create(CodexAgentSwitch.Domain.Providers.ProviderConfiguration provider) => throw new NotSupportedException(); }

    private sealed class RecordingMainAgent : IMainAgentSession
    {
        public event Func<MainAgentEvent, Task>? EventReceived;
        public List<(string ThreadId, string Prompt)> StartedTurns { get; } = [];
        public List<string> CompactedThreads { get; } = [];
        public List<(string PreviousThreadId, CompactCheckpoint Checkpoint)> RolloverCalls { get; } = [];
        public bool EmitTokenUsage { get; set; }
        public bool LeaveToolItemRunning { get; set; }
        public bool LeaveApprovalPending { get; set; }
        public Action? BeforeIdle { get; set; }
        public bool DelayIdleUntilAfterWait { get; set; }
        public string TerminalThreadStatus { get; set; } = "idle";
        private int turn;
        public string ThreadSessionId { get; set; } = "app-session-a";
        public string ThreadWorkingDirectory { get; set; } = Environment.CurrentDirectory;
        public MainAgentThreadIdentity? GetThreadIdentity(string threadId) =>
            new(threadId, ThreadSessionId, ThreadWorkingDirectory);
        public Task<string> CreateThreadAsync(string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.FromResult("thread-old");
        public Task ResumeThreadAsync(string threadId, string modelId, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MainAgentTurnHandle> StartTurnAsync(string threadId, string prompt, string modelId, string reasoningEffort, string workingDirectory, ExecutionApprovalMode approvalMode, CancellationToken cancellationToken = default)
        { _ = EventReceived; var id = $"turn-{++turn}"; StartedTurns.Add((threadId, prompt)); return Task.FromResult(new MainAgentTurnHandle(threadId, id)); }
        public async Task<MainAgentTurnResult> WaitForTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default)
        {
            await EmitAsync(new(MainAgentEventKind.StatusChanged, threadId, string.Empty, null, "active", null));
            await EmitAsync(new(MainAgentEventKind.TurnStarted, threadId, turnId, null, "running", null));
            if (EmitTokenUsage)
            {
                await EmitAsync(new(
                    MainAgentEventKind.TokenUsageUpdated, threadId, turnId, null, null, null,
                    TokenUsage: new MainAgentTokenUsage(600, 200, 20, 10, 620, 1000)));
                await EmitAsync(new(
                    MainAgentEventKind.TokenUsageUpdated, threadId, turnId, null, null, null,
                    TokenUsage: new MainAgentTokenUsage(800, 300, 40, 20, 840, 1000)));
            }
            if (LeaveToolItemRunning)
            {
                await EmitAsync(new(
                    MainAgentEventKind.TraceItemStarted,
                    threadId,
                    turnId,
                    "commandExecution",
                    "inProgress",
                    JsonSerializer.SerializeToElement(new { item = new { id = "tool-running", type = "commandExecution" } }),
                    TaskMessageKind.ToolCall));
            }
            if (LeaveApprovalPending)
            {
                await EmitAsync(new(
                    MainAgentEventKind.ApprovalRequested,
                    threadId,
                    turnId,
                    "item/commandExecution/requestApproval",
                    "waitingForApproval",
                    null));
            }
            BeforeIdle?.Invoke();
            if (!DelayIdleUntilAfterWait)
                await EmitAsync(new(MainAgentEventKind.StatusChanged, threadId, string.Empty, null, TerminalThreadStatus, null));
            await EmitAsync(new(MainAgentEventKind.TurnCompleted, threadId, turnId, "ok", "completed", null));
            if (DelayIdleUntilAfterWait)
                _ = EmitIdleAfterWaitAsync(threadId);
            return new MainAgentTurnResult(threadId, turnId, ControlledTaskStatus.Completed, "ok", null, JsonSerializer.SerializeToElement(new { status = "completed" }));
        }
        public Task<MainAgentTurnResult> ReadTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => WaitForTurnAsync(threadId, turnId, cancellationToken);
        public Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToApprovalAsync(string threadId, string turnId, bool approve, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MainAgentCompactionHandle> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default) { CompactedThreads.Add(threadId); return Task.FromResult(new MainAgentCompactionHandle(threadId, true, default)); }
        public Task<MainAgentRolloverResult> RolloverThreadAsync(string previousThreadId, CompactCheckpoint checkpoint, string modelId, string reasoningEffort, string workingDirectory, ExecutionApprovalMode approvalMode, bool startFirstTurn = true, CancellationToken cancellationToken = default)
        { RolloverCalls.Add((previousThreadId, checkpoint)); var first = new MainAgentTurnHandle("thread-new", "replay"); StartedTurns.Add(("thread-new", checkpoint.RenderReplayText())); return Task.FromResult(new MainAgentRolloverResult(previousThreadId, "thread-new", checkpoint, first)); }
        private Task EmitAsync(MainAgentEvent value) => EventReceived?.Invoke(value) ?? Task.CompletedTask;
        private async Task EmitIdleAfterWaitAsync(string threadId)
        {
            await Task.Delay(20);
            await EmitAsync(new(MainAgentEventKind.StatusChanged, threadId, string.Empty, null, TerminalThreadStatus, null));
        }
    }

    private sealed class MemoryProjects(AgentProject project) : IProjectRepository
    {
        public AgentProject Current { get; set; } = project;
        public Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentProject>>([Current]);
        public Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentProject?>(id == Current.Id ? Current : null);
        public Task UpsertAsync(AgentProject value, CancellationToken cancellationToken = default)
        {
            Current = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryManagedSessions : IManagedContextSessionStore
    {
        public Dictionary<string, ManagedContextSession> Values { get; } = new(StringComparer.Ordinal);
        public List<ManagedContextSession> History { get; } = [];
        public bool ThrowOnUpsert { get; set; }
        public Task<ManagedContextSession?> LoadByTaskSessionAsync(string taskSessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(taskSessionId));
        public Task<ManagedContextSession?> LoadByThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.Values.FirstOrDefault(value => value.ThreadId == threadId));
        public Task<IReadOnlyList<ManagedContextSession>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedContextSession>>(Values.Values.ToArray());
        public Task UpsertAsync(ManagedContextSession session, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpsert) throw new IOException("persistence unavailable");
            Values[session.TaskSessionId] = session;
            History.Add(session);
            return Task.CompletedTask;
        }
        public Task<bool> TryUpdateLeaseAsync(
            ManagedContextSession session,
            string expectedOwnershipLeaseId,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpsert) throw new IOException("persistence unavailable");
            if (!Values.TryGetValue(session.TaskSessionId, out var current)
                || !string.Equals(current.OwnershipLeaseId, expectedOwnershipLeaseId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }
            Values[session.TaskSessionId] = session;
            History.Add(session);
            return Task.FromResult(true);
        }
        public Task DeleteAsync(string taskSessionId, CancellationToken cancellationToken = default)
        {
            Values.Remove(taskSessionId);
            return Task.CompletedTask;
        }
        public void ReplaceForTest(ManagedContextSession session)
        {
            Values[session.TaskSessionId] = session;
            History.Add(session);
        }
    }

    private sealed class RecordingContextEconomyCoordinator : IMainContextEconomyCoordinator
    {
        public List<string> BoundThreads { get; } = [];
        public List<(string ThreadId, ContextTurnSample Sample, bool SafeBoundary)> Observations { get; } = [];
        public List<ContextControlValidation> ControlValidations { get; } = [];
        public bool ThrowOnObserve { get; set; }
        public bool ReturnSuccessfulCompaction { get; set; }
        private readonly Dictionary<string, Func<CancellationToken, Task<ContextControlValidation>>> controlGuards = new(StringComparer.Ordinal);
        public Task BindThreadAsync(
            string threadId,
            IMainAgentSession session,
            Func<CancellationToken, Task<ContextControlValidation>>? controlGuard = null,
            CancellationToken cancellationToken = default)
        {
            BoundThreads.Add(threadId);
            if (controlGuard is not null) controlGuards[threadId] = controlGuard;
            return Task.CompletedTask;
        }
        public async Task<ContextEconomyObservationResult> ObserveTurnAsync(string threadId, ContextTurnSample sample, bool safeBoundary = false, CancellationToken cancellationToken = default)
        {
            if (ThrowOnObserve) throw new IOException("context monitor unavailable");
            Observations.Add((threadId, sample, safeBoundary));
            if (safeBoundary && controlGuards.TryGetValue(threadId, out var guard))
                ControlValidations.Add(await guard(cancellationToken));
            var telemetry = new ContextPressureTelemetry(null, ContextPressureSource.Unavailable, null, null, 0, sample.InputTokens, sample.CachedInputTokens);
            var decision = new ContextEconomyDecision(ContextPressureBand.Normal, ContextEconomyAction.None, telemetry, false, "fixture");
            var compaction = safeBoundary && ReturnSuccessfulCompaction
                ? new ContextEconomyCompactionResult(
                    true,
                    true,
                    true,
                    1,
                    ContextEconomyState.Verifying,
                    null,
                    "fixture compaction completed",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    "request-fixture")
                : null;
            return new ContextEconomyObservationResult(
                decision,
                compaction?.State ?? ContextEconomyState.Idle,
                compaction is not null,
                compaction);
        }
        public Task<ContextEconomyCompactionResult?> CompactAtSafeBoundaryAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContextEconomyCompactionResult?>(null);
        public Task<StructuredCompactionObservation> ObserveStructuredCompactionAsync(
            string threadId,
            CompactionTrigger trigger,
            DateTimeOffset compactedAt,
            IReadOnlyList<ContextTurnSample>? preCompactionSamples = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContextEconomySnapshot?> GetSnapshotAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ContextEconomySnapshot?>(null);
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
