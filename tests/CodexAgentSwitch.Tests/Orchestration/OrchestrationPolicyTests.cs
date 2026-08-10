using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Tests.Orchestration;

public sealed class OrchestrationPolicyTests
{
    [Fact]
    public void Fuzzy_task_without_replaceable_work_or_scope_is_rejected()
    {
        var gate = new DelegationGate(new ScopeRegistry());
        var request = Request() with
        {
            Objective = "",
            SolWillSkip = "",
            Scope = new WorkerScope(["."], [], []),
            Deliverables = [],
            AcceptanceCriteria = [],
        };

        var result = gate.Validate(request, Context());

        Assert.False(result.CanDelegate);
        Assert.Contains(result.Issues, issue => issue.Code == "delegation.objective.required");
        Assert.Contains(result.Issues, issue => issue.Code == "delegation.skip.required");
        Assert.Contains(result.Issues, issue => issue.Code == "delegation.scope.too_broad");
    }

    [Fact]
    public void Economic_mode_allows_only_one_worker_and_requires_budget_and_provider()
    {
        var gate = new DelegationGate(new ScopeRegistry());
        Assert.True(gate.Validate(Request(), Context()).CanDelegate);

        var denied = gate.Validate(
            Request() with { RequestedWorkers = 2 },
            Context() with { ProviderAvailable = false, WithinBudget = false });

        Assert.False(denied.CanDelegate);
        Assert.Contains(denied.Issues, issue => issue.Code == "delegation.workers.limit");
        Assert.Contains(denied.Issues, issue => issue.Code == "delegation.provider.unavailable");
        Assert.Contains(denied.Issues, issue => issue.Code == "delegation.budget.exceeded");
    }

    [Fact]
    public void Default_active_worker_limit_is_one_even_when_routing_mode_allows_more()
    {
        var gate = new DelegationGate(new ScopeRegistry());
        var context = Context() with { RoutingMode = RoutingMode.Balanced, ProfileMaxWorkers = 2 };

        var denied = gate.Validate(Request() with { RequestedWorkers = 2 }, context);
        var explicitlyRaised = gate.Validate(
            Request() with { RequestedWorkers = 2 },
            context with { MaxActiveWorkers = 2 });

        Assert.False(denied.CanDelegate);
        Assert.True(explicitlyRaised.CanDelegate);
    }

    [Theory]
    [InlineData(TaskRiskLevel.Low, true, false, ReviewBudget.Minimal, ReviewLevel.R0)]
    [InlineData(TaskRiskLevel.Medium, true, false, ReviewBudget.Focused, ReviewLevel.R1)]
    [InlineData(TaskRiskLevel.High, false, true, ReviewBudget.Deep, ReviewLevel.R2)]
    public void Economic_v2_maps_risk_to_worker_ownership_and_review_budget(
        TaskRiskLevel risk,
        bool workerOwns,
        bool solLeads,
        ReviewBudget budget,
        ReviewLevel review)
    {
        var decision = new EconomicPolicyV2().Evaluate(risk);

        Assert.Equal(workerOwns, decision.WorkerOwnsClosedLoop);
        Assert.Equal(solLeads, decision.SolLeads);
        Assert.Equal(budget, decision.ReviewBudget);
        Assert.Equal(review, decision.ReviewLevel);
        Assert.Equal(1, decision.MaxActiveWorkers);
        Assert.True(decision.CompactResultRequired);
        Assert.False(decision.DuplicateImplementationAllowed);
    }

    [Fact]
    public void Escalation_and_context_checkpoint_require_explicit_reason_and_next_step()
    {
        var policy = new EconomicPolicyV2();
        var now = DateTimeOffset.UtcNow;

        var escalation = policy.Escalate(
            "task-1",
            WorkerEscalationKind.SharedProtocolChangeRequired,
            "需要越过冻结边界",
            ["repro"],
            now);
        var checkpoint = policy.CreateCheckpoint(
            "abc123",
            ["Phase 1"],
            ["Phase 9"],
            ["transport frozen"],
            [],
            "run focused tests",
            now);

        Assert.Equal("task-1", escalation.TaskId);
        Assert.Equal("abc123", checkpoint.Head);
        Assert.Equal("run focused tests", checkpoint.NextStep);
        Assert.Throws<ArgumentException>(() => policy.Escalate("task-1", WorkerEscalationKind.ScopeExpansionRequired, "", [], now));
    }

    [Fact]
    public void Overlapping_write_scopes_are_blocked_case_insensitively()
    {
        var registry = new ScopeRegistry();
        registry.Register(new DelegatedScope(
            "job-1",
            "Luna-1",
            new WorkerScope(["src/Feature"], [], [ScopeOperation.Modify]),
            DateTimeOffset.UtcNow,
            DelegationScopeStatus.Active));

        var decision = registry.CanRegister(new WorkerScope(["SRC\\FEATURE\\View.cs"], [], [ScopeOperation.Read]));

        Assert.Equal(ScopeAccessDecisionKind.Blocked, decision.Kind);
        Assert.Equal(["job-1"], decision.ConflictingJobIds);
    }

    [Fact]
    public void Parallel_read_is_allowed_but_main_full_repeat_warns_and_write_blocks()
    {
        var registry = new ScopeRegistry();
        var scope = new WorkerScope(["docs/audit.md"], [], [ScopeOperation.Read]);
        registry.Register(new DelegatedScope("job-1", "Terra", scope, DateTimeOffset.UtcNow, DelegationScopeStatus.Active));

        Assert.Equal(ScopeAccessDecisionKind.Allowed, registry.CanRegister(scope).Kind);
        Assert.Equal(ScopeAccessDecisionKind.Allowed, registry.CheckMainAgentAccess(scope, ScopeAccessIntent.DirectedSpotCheck).Kind);
        Assert.Equal(ScopeAccessDecisionKind.WarningRequiresConfirmation, registry.CheckMainAgentAccess(scope, ScopeAccessIntent.Read).Kind);
        Assert.Equal(
            ScopeAccessDecisionKind.Blocked,
            registry.CheckMainAgentAccess(
                new WorkerScope(["docs/audit.md"], [], [ScopeOperation.Modify]),
                ScopeAccessIntent.Modify).Kind);
    }

    [Fact]
    public void Full_takeover_is_allowed_only_after_rejection()
    {
        var ledger = new AdoptionLedger(new FakeClock());
        ledger.Start("job-1", "skip original scan", ReviewLevel.R1);
        Assert.False(ledger.CanPerformFullTakeover("job-1"));

        var partial = ledger.Decide("job-1", AdoptionStatus.PartiallyAdopted, "skip parsed files");
        Assert.Equal(AdoptionStatus.PartiallyAdopted, partial.Status);
        Assert.False(ledger.CanPerformFullTakeover("job-1"));

        var second = new AdoptionLedger(new FakeClock());
        second.Start("job-2", "skip build", ReviewLevel.R2);
        second.Decide("job-2", AdoptionStatus.Rejected, "nothing", "result did not match Task ID");
        Assert.True(second.CanPerformFullTakeover("job-2"));
    }

    [Theory]
    [InlineData(7, false, false, false, EconomicCheckpointDecision.NotDue)]
    [InlineData(8, true, false, false, EconomicCheckpointDecision.Continue)]
    [InlineData(9, true, true, false, EconomicCheckpointDecision.Refine)]
    [InlineData(9, false, false, true, EconomicCheckpointDecision.CancelAndTakeOver)]
    public void Economic_checkpoint_is_deterministic(
        int minutes,
        bool progress,
        bool drift,
        bool wrongTarget,
        EconomicCheckpointDecision expected)
    {
        var result = new EconomicCheckpointPolicy().Evaluate(new EconomicCheckpointInput(
            TimeSpan.FromMinutes(minutes),
            0.5m,
            progress,
            drift,
            wrongTarget));

        Assert.Equal(expected, result.Decision);
    }

    [Fact]
    public void Budget_exhaustion_stops_worker_even_before_time_checkpoint()
    {
        var result = new EconomicCheckpointPolicy().Evaluate(new EconomicCheckpointInput(
            TimeSpan.FromMinutes(2),
            1m,
            true,
            false,
            false));

        Assert.Equal(EconomicCheckpointDecision.CancelAndTakeOver, result.Decision);
    }

    [Fact]
    public async Task Unavailable_external_worker_falls_back_once_to_native_worker()
    {
        var primary = new FakeAdapter("external:deepseek", false);
        var fallback = new FakeAdapter("native-codex", true);

        var result = await new WorkerRoutingService().LaunchAsync(WorkerTask(), primary, fallback, allowFallback: true);

        Assert.True(result.UsedFallback);
        Assert.Equal("native-codex", result.SelectedAdapterId);
        Assert.Equal(0, primary.SpawnCount);
        Assert.Equal(1, fallback.SpawnCount);
    }

    private static DelegationRequest Request() => new(
        "group-1",
        "group-1-L1",
        "Inspect a specific profile file.",
        "Sol will skip parsing that file.",
        new WorkerScope(["profiles/server.sparkprofile"], [], [ScopeOperation.Read]),
        ["structured summary"],
        ["evidence is traceable"],
        ["format is unsupported"],
        PreferredWorkerType.ReadHeavy);

    private static DelegationGateContext Context() => new(
        RoutingMode.Economic,
        1,
        0,
        true,
        true,
        false);

    private static WorkerTask WorkerTask() => new(
        "group-1",
        "group-1-L1",
        "test",
        "test prompt",
        Environment.CurrentDirectory,
        "model",
        "medium",
        new WorkerScope(["a.cs"], [], [ScopeOperation.Read]),
        ["result"],
        ["valid"],
        []);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeAdapter(string adapterId, bool available) : IWorkerAdapter
    {
        public string AdapterId { get; } = adapterId;

        public int SpawnCount { get; private set; }

        public Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkerCapabilities(AdapterId, available, [], 1, available ? [] : ["unavailable"]));

        public Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default)
        {
            SpawnCount++;
            return Task.FromResult(new WorkerJob(AdapterId, "job", "thread", "turn", task.TaskId, WorkerJobStatus.Running, DateTimeOffset.UtcNow, null, null));
        }

        public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
