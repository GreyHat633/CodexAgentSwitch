using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.Scheduling;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.Scheduling;

public sealed class SchedulerIpcServerTests
{
    [Fact]
    public async Task Delegation_preflight_resolves_registered_child_and_fills_identity()
    {
        var project = Project("deepseek-default");
        var guard = new AppliedProjectWorkerGuard(new ProjectRepository(project));
        var resolved = await guard.ResolveAsync(new TaskPacket("preflight-child", "", "E:\\AISPace\\TestSpace\\child", "", "goal", ["src"], [], [], ["ok"], [], "out"));
        Assert.Equal(project.Id, resolved.ProjectId);
        Assert.Equal("deepseek-default", resolved.WorkerId);
    }

    [Fact]
    public async Task Delegation_preflight_reports_exact_resolution_and_worker_reasons()
    {
        var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var profile = new Profile(Guid.NewGuid(), "native", new AgentSelection("model", "medium"),
            new WorkerPolicy(true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            new BudgetLimits(null, null, null, null, null, "CNY"), true, now, now, null);
        var native = Project("native-codex") with
        {
            NativeCodexAdaptation = Project("native-codex").NativeCodexAdaptation! with
            {
                AppliedSnapshot = Project("native-codex").NativeCodexAdaptation!.AppliedSnapshot! with
                {
                    ProfileId = profile.Id, WorkerKind = nameof(EffectiveWorkerKind.NativeAgent), WorkerRole = "cas_luna_worker",
                    ProviderId = null, ConfigurationFingerprint = "fingerprint", ValidationStatus = "SchedulerRequired"
                }
            }
        };
        var preflight = new DelegationPreflight(new ProjectRepository(native), new ProfileRepository(profile), [new NativeWorkerExecutor()],
            schedulerState: () => SchedulerState.Ready, activeTaskCount: () => 0,
            capabilities: new FixedPreflightCapabilities(new(true, true, true)));
        var ready = await preflight.EvaluateAsync(new DelegationPreflightRequest("E:\\AISPace\\TestSpace\\child"));
        Assert.Equal("READY", ready.ReasonCode);
        Assert.Equal(profile.Id.ToString(), ready.ProfileId);
        Assert.True(ready.DispatchReady);

        var unavailable = new DelegationPreflight(new ProjectRepository(native), new ProfileRepository(profile), [new NativeWorkerExecutor()],
            schedulerState: () => SchedulerState.Ready, activeTaskCount: () => 1,
            capabilities: new FixedPreflightCapabilities(new(true, true, true)));
        Assert.Equal("WORKER_SLOT_UNAVAILABLE", (await unavailable.EvaluateAsync(new DelegationPreflightRequest("E:\\AISPace\\TestSpace"))).ReasonCode);
    }

    [Fact]
    public async Task Delegation_preflight_is_exposed_over_scheduler_ipc()
    {
        var pipeName = $"CAS-preflight-{Guid.NewGuid():N}";
        var project = Project("deepseek-default");
        var guard = new AppliedProjectWorkerGuard(new ProjectRepository(project));
        var preflight = new DelegationPreflight(new ProjectRepository(project), new ProfileRepository(ProfileFor(project)), [new EchoExecutor()], guard,
            schedulerState: () => SchedulerState.Ready, activeTaskCount: () => 0);
        await using var scheduler = new WorkerScheduler([new EchoExecutor()], new MemoryRepository(), new FixedClock(), preflight: preflight);
        await scheduler.StartAsync();
        await using var server = new SchedulerIpcServer(scheduler, pipeName);
        await server.StartAsync();
        var response = await SendRequestAsync(pipeName, "{\"method\":\"delegationPreflight\",\"payload\":{\"workingDirectory\":\"E:\\\\AISPace\\\\TestSpace\\\\child\"}}");
        Assert.Contains("project-1", response, StringComparison.Ordinal);
        Assert.DoesNotContain("WORKER_CAPABILITY_MISSING", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegation_preflight_covers_mapping_profile_worker_and_native_failure_codes()
    {
        var root = "E:\\AISPace\\TestSpace";
        var parent = Project("outer", workingDirectory: root, projectId: "outer");
        var child = Project("inner", workingDirectory: root + "\\nested", projectId: "inner");
        var ambiguous = new AppliedProjectWorkerGuard(new ProjectRepository(parent with { WorkingDirectory = root + "\\same" }, child with { WorkingDirectory = root + "\\same" }));
        var ambiguousResolution = await ambiguous.ResolveProjectAsync(root + "\\same\\child", null);
        Assert.Equal("AMBIGUOUS_PROJECT_MAPPING", ambiguousResolution.Source);
        Assert.Equal(["outer", "inner"], ambiguousResolution.CandidateProjectIds);
        Assert.All(ambiguousResolution.Candidates, candidate => Assert.StartsWith("E:\\", candidate.NormalizedRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("PROJECT_ID_MISMATCH", (await ambiguous.ResolveProjectAsync(root + "\\other", "inner")).Source);

        var noProfile = parent with { NativeCodexAdaptation = null };
        var missing = new DelegationPreflight(new ProjectRepository(noProfile), new ProfileRepository(ProfileFor(parent)), [new EchoExecutor()]);
        Assert.Equal("PROFILE_NOT_APPLIED", (await missing.EvaluateAsync(new DelegationPreflightRequest(root))).ReasonCode);

        var nativeSnapshot = parent.NativeCodexAdaptation!.AppliedSnapshot! with { WorkerKind = nameof(EffectiveWorkerKind.NativeAgent), WorkerRole = null, ProviderId = null };
        var nativeProject = parent with { NativeCodexAdaptation = parent.NativeCodexAdaptation! with { AppliedSnapshot = nativeSnapshot } };
        var roleMissing = new DelegationPreflight(new ProjectRepository(nativeProject), new ProfileRepository(ProfileFor(parent)), [new NativeWorkerExecutor()]);
        Assert.Equal("WORKER_ROLE_MISSING", (await roleMissing.EvaluateAsync(new DelegationPreflightRequest(root))).ReasonCode);

        var unavailableProject = nativeProject with { NativeCodexAdaptation = nativeProject.NativeCodexAdaptation! with { AppliedSnapshot = nativeSnapshot with { WorkerRole = "cas_luna_worker" } } };
        var unavailable = new DelegationPreflight(new ProjectRepository(unavailableProject), new ProfileRepository(ProfileFor(parent)), []);
        Assert.Equal("WORKER_REGISTRATION_FAILED", (await unavailable.EvaluateAsync(new DelegationPreflightRequest(root))).ReasonCode);
        Assert.DoesNotContain("WORKER_CAPABILITY_MISSING", (await unavailable.EvaluateAsync(new DelegationPreflightRequest(root))).Reasons);

        var unsupportedProject = unavailableProject with { NativeCodexAdaptation = unavailableProject.NativeCodexAdaptation! with { AppliedSnapshot = unavailableProject.NativeCodexAdaptation!.AppliedSnapshot! with { ValidationStatus = "Unsupported" } } };
        var unsupported = new DelegationPreflight(new ProjectRepository(unsupportedProject), new ProfileRepository(ProfileFor(parent)), [new NativeWorkerExecutor()]);
        Assert.Equal("NATIVE_AGENT_UNAVAILABLE", (await unsupported.EvaluateAsync(new DelegationPreflightRequest(root))).ReasonCode);

        var noFingerprint = unsupportedProject with { NativeCodexAdaptation = unsupportedProject.NativeCodexAdaptation! with { AppliedSnapshot = unsupportedProject.NativeCodexAdaptation!.AppliedSnapshot! with { ValidationStatus = "Supported", ConfigurationFingerprint = "" } } };
        var spawnFailed = new DelegationPreflight(new ProjectRepository(noFingerprint), new ProfileRepository(ProfileFor(parent)), [new NativeWorkerExecutor()]);
        Assert.Equal("NATIVE_SPAWN_FAILED", (await spawnFailed.EvaluateAsync(new DelegationPreflightRequest(root))).ReasonCode);

        var disabledProject = parent with { NativeCodexAdaptation = parent.NativeCodexAdaptation! with { AppliedSnapshot = parent.NativeCodexAdaptation!.AppliedSnapshot! with { WorkerKind = nameof(EffectiveWorkerKind.None), WorkerRole = null, ProviderId = null } } };
        var disabled = new DelegationPreflight(new ProjectRepository(disabledProject), new ProfileRepository(ProfileFor(parent)), [new EchoExecutor()]);
        Assert.Equal("WORKER_DISABLED", (await disabled.EvaluateAsync(new DelegationPreflightRequest(root))).ReasonCode);
    }

    [Fact]
    public async Task External_git_worktree_resolves_registered_canonical_project()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRoot) || !Path.GetFullPath(testRoot).StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) return;
        var temp = Path.Combine(testRoot, "cas-git-preflight-" + Guid.NewGuid().ToString("N"));
        var canonical = Path.Combine(temp, "canonical");
        var worktree = Path.Combine(temp, "worktree");
        Directory.CreateDirectory(Path.Combine(canonical, ".git"));
        Directory.CreateDirectory(Path.Combine(canonical, ".git", "worktrees", "fixture"));
        Directory.CreateDirectory(worktree);
        await File.WriteAllTextAsync(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(canonical, ".git", "worktrees", "fixture")}");
        await File.WriteAllTextAsync(Path.Combine(canonical, ".git", "worktrees", "fixture", "commondir"), "../..");
        try
        {
            var resolved = await new AppliedProjectWorkerGuard(new ProjectRepository(Project("deepseek-default", workingDirectory: canonical))).ResolveProjectAsync(Path.Combine(worktree, "src"), null);
            Assert.Equal("git-worktree", resolved.Source);
            Assert.Equal("project-1", resolved.Project?.Id);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Applied_external_worker_is_resolved_when_codex_tool_omits_worker_id()
    {
        var pipeName = $"CAS-test-{Guid.NewGuid():N}";
        var executor = new EchoExecutor();
        var projects = new ProjectRepository(Project("deepseek-default"));
        var resolver = new AppliedProjectWorkerGuard(projects);
        await using var scheduler = new WorkerScheduler(
            [executor],
            new MemoryRepository(),
            new FixedClock(),
            resolvers: [resolver],
            guards: [resolver]);
        await scheduler.StartAsync();
        await using var server = new SchedulerIpcServer(scheduler, pipeName);
        await server.StartAsync();
        const string request = """
            {"method":"dispatch","payload":{"taskId":"task-013","projectId":"project-1","workingDirectory":"E:\\AISPace\\TestSpace","goal":"CAS-DS-013-WORKER-RESOLVE-381527","scope":["src/Foo.cs"],"allowedReadScope":["src/Foo.cs"],"allowedWriteScope":[],"acceptanceCriteria":["return nonce"],"constraints":["no session scan"],"outputContract":"Return exact nonce"}}
            """;

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(request);
        var response = await reader.ReadLineAsync();

        Assert.NotNull(response);
        Assert.Contains("CAS-DS-013-WORKER-RESOLVE-381527", response, StringComparison.Ordinal);
        Assert.Equal("CAS-DS-013-WORKER-RESOLVE-381527", executor.ReceivedGoal);
        Assert.Equal("deepseek-default", executor.ReceivedWorkerId);
    }

    [Fact]
    public async Task Explicit_non_applied_worker_remains_rejected()
    {
        var projects = new ProjectRepository(Project("deepseek-default"));
        var resolver = new AppliedProjectWorkerGuard(projects);
        await using var scheduler = new WorkerScheduler(
            [new EchoExecutor()],
            new MemoryRepository(),
            new FixedClock(),
            resolvers: [resolver],
            guards: [resolver]);
        await scheduler.StartAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.DispatchAsync(new TaskPacket(
            "task-rejected", "project-1", "E:\\AISPace\\TestSpace", "deepseek",
            "CAS-DS-013-WORKER-RESOLVE-381527", ["src/Foo.cs"], ["src/Foo.cs"], [],
            ["return nonce"], ["no session scan"], "Return exact nonce")));

        Assert.Contains("项目已应用 Worker 为 deepseek-default", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TaskPacket 请求的是 deepseek", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nested_working_directory_resolves_applied_parent_worker()
    {
        var resolver = new AppliedProjectWorkerGuard(new ProjectRepository(Project("deepseek-default")));
        var packet = new TaskPacket(
            "task-nested", string.Empty, "E:\\AISPace\\TestSpace\\state\\acceptance\\fixture", string.Empty,
            "Implement fixture", ["src"], ["src"], ["src"], ["tests pass"], [], "Return result");

        var resolved = await resolver.ResolveAsync(packet);

        Assert.Equal("deepseek-default", resolved.WorkerId);
    }

    [Fact]
    public async Task Multiple_registered_ancestry_projects_are_ambiguous()
    {
        var projects = new ProjectRepository(
            Project("outer-worker", workingDirectory: "E:\\AISPace\\TestSpace", projectId: "outer"),
            Project("inner-worker", workingDirectory: "E:\\AISPace\\TestSpace\\nested", projectId: "inner"));
        var resolver = new AppliedProjectWorkerGuard(projects);
        var packet = new TaskPacket(
            "task-inner", string.Empty, "E:\\AISPace\\TestSpace\\nested\\fixture", string.Empty,
            "Implement fixture", ["src"], ["src"], ["src"], ["tests pass"], [], "Return result");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(packet));
        Assert.Equal("PROJECT_MAPPING_AMBIGUOUS", exception.Message);
    }

    [Fact]
    public async Task Similar_directory_prefix_does_not_resolve_applied_project()
    {
        var resolver = new AppliedProjectWorkerGuard(new ProjectRepository(Project("deepseek-default")));
        var packet = new TaskPacket(
            "task-sibling", string.Empty, "E:\\AISPace\\TestSpace-other\\fixture", string.Empty,
            "Implement fixture", ["src"], ["src"], ["src"], ["tests pass"], [], "Return result");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(packet));

        Assert.Equal("PROJECT_NOT_RESOLVED", exception.Message);
    }

    [Fact]
    public async Task Repartition_telemetry_round_trips_over_ipc_and_rejects_undefined_enum()
    {
        var pipeName = $"CAS-test-{Guid.NewGuid():N}";
        var repository = new MemoryRepository();
        await using var scheduler = new WorkerScheduler([new EchoExecutor()], repository, new FixedClock());
        await scheduler.StartAsync();
        await using var server = new SchedulerIpcServer(scheduler, pipeName);
        await server.StartAsync();

        var recordResponse = await SendRequestAsync(pipeName, """
            {"method":"recordRepartition","payload":{"taskGroupId":"group-ipc","trigger":"PHASE_CHANGE","decision":"Main","reason":"REVIEW_REQUIRED","workSummary":"Review worker result","workerIdentity":"worker-a","result":"pending"}}
            """);
        Assert.Contains("\"ok\":true", recordResponse, StringComparison.Ordinal);
        Assert.Contains("\"sequence\":1", recordResponse, StringComparison.Ordinal);
        Assert.Contains("\"recordedAt\":\"2026-08-09T00:00:00+00:00\"", recordResponse, StringComparison.Ordinal);

        var listResponse = await SendRequestAsync(pipeName, """
            {"method":"listRepartitions","payload":{"taskGroupId":"group-ipc"}}
            """);
        Assert.Contains("group-ipc", listResponse, StringComparison.Ordinal);
        Assert.Contains("\"reason\":5", listResponse, StringComparison.Ordinal);
        Assert.Single(await scheduler.ListRepartitionsAsync("group-ipc"));

        var invalidResponse = await SendRequestAsync(pipeName, """
            {"method":"recordRepartition","payload":{"taskGroupId":"group-ipc","trigger":"999","decision":"Main","reason":"REVIEW_REQUIRED","workSummary":"must reject"}}
            """);
        Assert.Contains("\"ok\":false", invalidResponse, StringComparison.Ordinal);
        Assert.Single(await scheduler.ListRepartitionsAsync("group-ipc"));
    }

    [Fact]
    public async Task PreToolUse_ipc_denies_definite_mutation_and_leaves_unknown_to_policy()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CAS_TEST_ROOT") ?? Path.GetTempPath(), "pretool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new SqliteDatabase(Path.Combine(root, "pretool.db"));
            await database.InitializeAsync();
            var leases = new SqliteWorkPackageLeaseRepository(database);
            await using var scheduler = new WorkerScheduler([new EchoExecutor()], new MemoryRepository(), new FixedClock(), leaseRepository: leases);
            await scheduler.StartAsync();
            await scheduler.RecordRepartitionAsync("group", RepartitionTrigger.PHASE_CHANGE, WorkOwner.Worker, RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "worker", null, null, "pkg", root, "Implementation", [root], 0);
            var pipeName = $"CAS-pretool-{Guid.NewGuid():N}";
            await using var server = new SchedulerIpcServer(scheduler, pipeName);
            await server.StartAsync();
            var denied = await SendRequestAsync(pipeName, $"{{\"method\":\"preToolUse\",\"payload\":{{\"sessionId\":\"s\",\"workingDirectory\":\"{root.Replace("\\", "\\\\")}\",\"toolName\":\"apply_patch\",\"toolInput\":{{\"patch\":\"x\"}}}}}}");
            Assert.Contains("\"allowed\":false", denied, StringComparison.OrdinalIgnoreCase);
            var unknown = await SendRequestAsync(pipeName, $"{{\"method\":\"preToolUse\",\"payload\":{{\"sessionId\":\"s\",\"workingDirectory\":\"{root.Replace("\\", "\\\\")}\",\"toolName\":\"shell\",\"toolInput\":\"mystery-command\"}}}}");
            Assert.Contains("\"allowed\":true", unknown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RequiresSafetyPolicy", unknown, StringComparison.OrdinalIgnoreCase);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<string> SendRequestAsync(string pipeName, string request)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(request);
        return await reader.ReadLineAsync() ?? throw new InvalidOperationException("Scheduler IPC returned no response.");
    }

    [Fact]
    public async Task Resolved_applied_worker_reaches_fake_external_worker_adapter_with_plaintext_nonce()
    {
        var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var profileId = Guid.NewGuid();
        var profile = new Profile(
            profileId, "Sol + DeepSeek", new AgentSelection("gpt-5.6-sol", "high"),
            new WorkerPolicy(true, WorkerSource.ExternalProvider, "deepseek-default", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            new BudgetLimits(3, null, null, null, null, "CNY"), true, now, now, null);
        var provider = new ProviderConfiguration(
            "deepseek-default", "DeepSeek", ProviderKind.DeepSeek, new Uri("https://api.deepseek.com"),
            "provider/deepseek-default", DeepSeekV4Catalog.FlashModelId, new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30), true, new ProviderPricing(1, 2, "CNY", null), now, now);
        var projects = new ProjectRepository(Project("deepseek-default", profileId));
        var adapter = new RecordingExternalAdapter();
        var orchestrator = new WorkerOrchestrator(
            new RecordingExternalFactory(adapter), new FakeRuntime(adapter), new ExternalProviderResolver());
        var executor = new ExternalWorkerExecutor(
            projects,
            new ProfileRepository(profile),
            new TaskProfileSnapshotFactory(new ProviderRepository(provider), new FixedClock()),
            orchestrator,
            new UsageRepository(),
            new BudgetPolicy());
        var resolver = new AppliedProjectWorkerGuard(projects);
        await using var scheduler = new WorkerScheduler(
            [executor], new MemoryRepository(), new FixedClock(), resolvers: [resolver], guards: [resolver]);
        await scheduler.StartAsync();

        var result = await scheduler.DispatchAsync(new TaskPacket(
            "task-external-adapter", "project-1", "E:\\AISPace\\TestSpace", string.Empty,
            "CAS-DS-013-WORKER-RESOLVE-381527", ["src/Foo.cs"], ["src/Foo.cs"], [],
            ["return nonce"], ["no session scan"], "Return exact nonce"));

        Assert.NotNull(adapter.LastTask);
        Assert.Contains("CAS-DS-013-WORKER-RESOLVE-381527", adapter.LastTask!.Prompt, StringComparison.Ordinal);
        Assert.Equal(DeepSeekV4Catalog.FlashModelId, adapter.LastTask.ModelId);
        Assert.Equal([ScopeOperation.Read, ScopeOperation.Search], adapter.LastTask.Scope.Operations);
        Assert.Empty(adapter.LastTask.AllowedWriteScope);
        Assert.Equal(profile.Budget, adapter.LastTask.BudgetSnapshot);
        Assert.Equal(2, result.ProviderTurns);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(1, result.LeaseExtensionCount);
        Assert.Equal("provider-turn-limit", result.HardLimitReason);
        Assert.Equal(profile.Budget, result.ConfiguredTaskBudgetSnapshot);
        Assert.True(result.CostVerified);
        Assert.True(result.FinalizationAttempted);
        Assert.True(result.FinalizationSucceeded);
    }

    [Fact]
    public async Task External_executor_sums_known_daily_and_monthly_costs_and_honors_limits()
    {
        var usage = new UsageRepository();
        var now = DateTimeOffset.Now;
        SeedUsage(usage, now, [1m, 2m]);
        // Unknown cost outside both windows must not poison current-window evaluation.
        SeedUsage(usage, now.AddMonths(-1), [null]);

        var allowed = CreateExternalExecutor(usage, new BudgetLimits(null, 4m, 4m, null, null, "CNY"));
        var allowedResult = await allowed.ExecuteAsync(Packet("known-allowed"));
        Assert.Equal(DelegationState.ResultReceived, allowedResult.State);

        var blocked = CreateExternalExecutor(usage, new BudgetLimits(null, 2m, null, null, null, "CNY"));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.ExecuteAsync(Packet("known-blocked")));
        Assert.Contains("预算", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_executor_blocks_configured_window_when_known_and_unknown_costs_are_mixed()
    {
        foreach (var budget in new[]
        {
            new BudgetLimits(null, 10m, null, null, null, "CNY"),
            new BudgetLimits(null, null, 10m, null, null, "CNY"),
        })
        {
            var usage = new UsageRepository();
            var now = DateTimeOffset.Now;
            SeedUsage(usage, now, [1m, null]);
            var executor = CreateExternalExecutor(usage, budget);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(Packet($"unknown-{Guid.NewGuid():N}")));
            Assert.Contains("预算", exception.Message, StringComparison.Ordinal);
        }
    }

    private static ExternalWorkerExecutor CreateExternalExecutor(UsageRepository usage, BudgetLimits budget)
    {
        var project = Project("deepseek-default");
        var profile = ProfileFor(project) with { Budget = budget };
        var now = DateTimeOffset.UtcNow;
        var provider = new ProviderConfiguration(
            "deepseek-default", "DeepSeek", ProviderKind.DeepSeek, new Uri("https://api.deepseek.com"),
            "provider/deepseek-default", DeepSeekV4Catalog.FlashModelId, new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30), true, new ProviderPricing(1m, 2m, "CNY", null), now, now);
        var adapter = new RecordingExternalAdapter();
        var orchestrator = new WorkerOrchestrator(
            new RecordingExternalFactory(adapter), new FakeRuntime(adapter), new ExternalProviderResolver());
        return new ExternalWorkerExecutor(
            new ProjectRepository(project),
            new ProfileRepository(profile),
            new TaskProfileSnapshotFactory(new ProviderRepository(provider), new FixedClock()),
            orchestrator,
            usage,
            new BudgetPolicy());
    }

    private static TaskPacket Packet(string id) => new(
        id, "project-1", "E:\\AISPace\\TestSpace", "deepseek-default", "goal",
        ["src"], ["src"], [], ["return nonce"], [], "Return exact nonce");

    private static void SeedUsage(UsageRepository repository, DateTimeOffset capturedAt, IReadOnlyList<decimal?> costs)
    {
        var groupId = $"usage-{Guid.NewGuid():N}";
        repository.Groups.Add(new TaskGroupLedger(groupId, "main", "model", "medium", capturedAt, capturedAt, [], capturedAt));
        repository.Usage[groupId] = costs.Select((cost, index) => new UsageSnapshot(
            Guid.NewGuid(), groupId, $"job-{index}", "deepseek-default", "deepseek-chat", capturedAt,
            new MeasuredLong(10, EvidenceKind.Actual),
            new MeasuredLong(5, EvidenceKind.Actual),
            new MeasuredLong(15, EvidenceKind.Actual),
            new MeasuredLong(1, EvidenceKind.Actual),
            new MeasuredDecimal(cost, cost is null ? EvidenceKind.Unavailable : EvidenceKind.Estimated),
            "CNY", null, [])).ToList();
    }

    private sealed class EchoExecutor : IWorkerExecutor
    {
        public WorkerTransport Transport => WorkerTransport.ExternalProvider;
        public string? ReceivedGoal { get; private set; }
        public string? ReceivedWorkerId { get; private set; }
        public bool CanExecute(TaskPacket packet) => true;
        public Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default)
        {
            ReceivedGoal = packet.Goal;
            ReceivedWorkerId = packet.WorkerId;
            return Task.FromResult(new WorkerResultPacket(packet.TaskId, DelegationState.ResultReceived, packet.Goal, [], [], [], []));
        }
    }

    private static Profile ProfileFor(AgentProject project)
    {
        var id = project.NativeCodexAdaptation?.AppliedSnapshot?.ProfileId ?? Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        return new Profile(id, "fixture", new AgentSelection("model", "medium"),
            new WorkerPolicy(true, WorkerSource.ExternalProvider, "deepseek-default", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation),
            new BudgetLimits(null, null, null, null, null, "CNY"), true, now, now, null);
    }

    private sealed class FixedPreflightCapabilities(DelegationPreflightCapabilities value) : IDelegationPreflightCapabilities
    {
        public DelegationPreflightCapabilities Current => value;
    }

    private static AgentProject Project(
        string workerId,
        Guid? appliedProfileId = null,
        string workingDirectory = "E:\\AISPace\\TestSpace",
        string projectId = "project-1")
    {
        var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        var profileId = appliedProfileId ?? Guid.Parse("a8a9a9d4-1f72-4824-a9bd-21c26b701301");
        var snapshot = new NativeCodexAppliedSnapshot(
            profileId, "Sol + DeepSeek", "gpt-5.6-sol", "high",
            nameof(EffectiveWorkerKind.ExternalAgent), null, null, workerId, "medium", 1,
            "Economic", "SchedulerRequired", "fixture");
        return new AgentProject(
            projectId, "TestSpace", workingDirectory, false, now, now, profileId,
            new NativeCodexProjectAdaptation(profileId, "Sol + DeepSeek", ".codex/config.toml", null, now, "fixture", false, snapshot));
    }

    private sealed class ProjectRepository(params AgentProject[] values) : IProjectRepository
    {
        private readonly IReadOnlyList<AgentProject> values = values;
        public Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(values);
        public Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(values.FirstOrDefault(item => item.Id == id));
        public Task UpsertAsync(AgentProject project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProfileRepository(Profile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Profile>>([profile]);
        public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(id == profile.Id ? profile : null);
        public Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(profile);
        public Task UpsertAsync(Profile value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ProviderRepository(ProviderConfiguration provider) : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderConfiguration>>([provider]);
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ProviderConfiguration?>(id == provider.Id ? provider : null);
        public Task UpsertAsync(ProviderConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UsageRepository : IUsageLedgerRepository
    {
        public List<TaskGroupLedger> Groups { get; } = [];
        public Dictionary<string, List<UsageSnapshot>> Usage { get; } = [];
        public Task UpsertTaskGroupAsync(TaskGroupLedger ledger, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TaskGroupLedger?> GetTaskGroupAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<TaskGroupLedger?>(null);
        public Task<IReadOnlyList<TaskGroupLedger>> ListTaskGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskGroupLedger>>(Groups);
        public Task AppendUsageAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsageSnapshot>> ListUsageAsync(string taskGroupId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UsageSnapshot>>(Usage.GetValueOrDefault(taskGroupId) ?? []);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeRuntime(IWorkerAdapter native) : IControlledTaskRuntime
    {
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IMainAgentSession MainAgent => null!;
        public IWorkerAdapter NativeWorker => native;
    }

    private sealed class RecordingExternalFactory(RecordingExternalAdapter adapter) : IExternalWorkerAdapterFactory
    {
        public IWorkerAdapter Create(ProviderConfiguration provider) => adapter;
    }

    private sealed class RecordingExternalAdapter : IWorkerAdapter
    {
        public WorkerTask? LastTask { get; private set; }
        public string AdapterId => "external:deepseek-default";
        public Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkerCapabilities(AdapterId, true, [], 1, []));
        public Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default)
        {
            LastTask = task;
            return Task.FromResult(new WorkerJob(AdapterId, "job", "thread", "turn", task.TaskId, WorkerJobStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed"));
        }
        public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult(new WorkerJob(AdapterId, jobId, "thread", "turn", LastTask!.TaskId, WorkerJobStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "completed"));
        public Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default) => Task.FromResult<WorkerResult?>(new WorkerResult(LastTask!.TaskId, WorkerJobStatus.Completed, "done", null, [], [], "deepseek-default", "DeepSeek", new Uri("https://api.deepseek.com/chat/completions"), DeepSeekV4Catalog.FlashModelId, new ProviderUsage(1, 1, 2))
        {
            ProviderTurns = 2,
            ToolCalls = 1,
            LeaseExtensionCount = 1,
            HardLimitReason = "provider-turn-limit",
            BudgetSnapshot = LastTask.BudgetSnapshot,
            CostVerified = true,
            FinalizationAttempted = true,
            FinalizationSucceeded = true,
        });
        public Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryRepository : ISchedulerTaskRepository
    {
        private readonly Dictionary<string, ScheduledDelegation> items = [];
        private readonly List<RepartitionTelemetry> repartitions = [];
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) => Task.FromResult(items.GetValueOrDefault(taskId));
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>(items.Values.ToArray());
        public Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default) { items[task.Packet.TaskId] = task; return Task.CompletedTask; }
        public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RepartitionTelemetry>>(repartitions.Where(item => item.TaskGroupId == taskGroupId).OrderBy(item => item.Sequence).ToArray());
        public Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default) { repartitions.Add(telemetry); return Task.CompletedTask; }
    }

}
