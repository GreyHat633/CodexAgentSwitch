using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Scheduling;

public static class NativeCustomAgentInvocationPolicy
{
    public static NativeWorkerInvocation Create(TaskPacket packet, string agentRole, string? parentAgentRole = null)
    {
        if (string.IsNullOrWhiteSpace(agentRole))
        {
            throw new InvalidOperationException("Native Custom Worker 缺少 AgentRole。");
        }

        const string forkTurns = "none";
        return new NativeWorkerInvocation(
            agentRole,
            forkTurns,
            packet,
            $"MUST call spawn_agent with agent_type=\"{agentRole}\" and fork_turns=\"{forkTurns}\". fork_turns is a required actual tool argument for a managed Native Custom Worker; never omit it and never use fork_turns=\"all\". Only send this TaskPacket; do not copy full parent-thread history. Then call report_worker_result.");
    }
}

public sealed class NativeWorkerExecutor : IWorkerExecutor
{
    public WorkerTransport Transport => WorkerTransport.NativeCustomAgent;

    public bool CanExecute(TaskPacket packet) => packet.WorkerId.StartsWith("cas_", StringComparison.Ordinal)
        || packet.WorkerId.StartsWith("native-", StringComparison.Ordinal);

    public Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default)
    {
        var role = packet.WorkerId switch
        {
            "native-sol" => "cas_sol_worker",
            "native-terra" => "cas_terra_worker",
            "native-luna" => "cas_luna_worker",
            _ => packet.WorkerId,
        };
        var invocation = NativeCustomAgentInvocationPolicy.Create(packet, role);
        return Task.FromResult(new WorkerResultPacket(
            packet.TaskId,
            DelegationState.Delegated,
            "已登记 Native Custom Worker；等待官方 Codex 按明确角色执行。",
            [], [], [], [],
            ProviderId: "native-codex",
            ModelId: null,
            NativeInvocation: invocation));
    }
}

public sealed class ExternalWorkerExecutor(
    IProjectRepository projects,
    IProfileRepository profiles,
    TaskProfileSnapshotFactory snapshots,
    WorkerOrchestrator orchestrator,
    IUsageLedgerRepository usage,
    BudgetPolicy budgets) : IWorkerExecutor
{
    public WorkerTransport Transport => WorkerTransport.ExternalProvider;

    public bool CanExecute(TaskPacket packet) => !packet.WorkerId.StartsWith("cas_", StringComparison.Ordinal)
        && !packet.WorkerId.StartsWith("native-", StringComparison.Ordinal);

    public async Task<WorkerResultPacket> ExecuteAsync(TaskPacket packet, CancellationToken cancellationToken = default)
    {
        var project = !string.IsNullOrWhiteSpace(packet.ProjectId)
            ? await projects.GetAsync(packet.ProjectId, cancellationToken)
            : (await projects.ListAsync(cancellationToken)).FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.WorkingDirectory), Path.GetFullPath(packet.WorkingDirectory), StringComparison.OrdinalIgnoreCase));
        var profileId = project?.NativeCodexAdaptation?.AppliedSnapshot?.ProfileId
            ?? project?.DefaultProfileId
            ?? throw new InvalidOperationException("项目没有可用于 Scheduler 的已应用方案。");
        var profile = await profiles.GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("项目引用的 Profile 不存在。");
        var applied = project?.NativeCodexAdaptation?.AppliedSnapshot;
        var effectivePolicy = applied is null
            ? profile.WorkerPolicy
            : profile.WorkerPolicy with
            {
                Enabled = true,
                Source = WorkerSource.ExternalProvider,
                PreferredProviderId = applied.ProviderId,
                MaxWorkers = applied.MaxWorkers,
                RoutingMode = Enum.TryParse<RoutingMode>(applied.RoutingMode, true, out var routing) ? routing : profile.WorkerPolicy.RoutingMode,
            };
        if (effectivePolicy.Source != WorkerSource.ExternalProvider)
        {
            throw new InvalidOperationException("项目当前已应用方案不是 External Worker。");
        }

        var effectiveProfile = applied is null
            ? profile
            : profile with
            {
                MainAgent = new AgentSelection(applied.MainModel, applied.MainReasoningEffort),
                WorkerPolicy = effectivePolicy,
            };
        var snapshot = await snapshots.CaptureAsync(effectiveProfile, cancellationToken);
        var assessment = budgets.Evaluate(effectiveProfile.Budget, await ConsumptionAsync(cancellationToken));
        if (!assessment.AllowNewRequests)
        {
            throw new InvalidOperationException($"预算已阻止新的 External Worker 请求：{string.Join("；", assessment.Reasons)}");
        }
        var operations = new List<ScopeOperation> { ScopeOperation.Read, ScopeOperation.Search };
        if (packet.AllowedWriteScope.Count > 0)
        {
            operations.AddRange([ScopeOperation.Modify, ScopeOperation.Execute, ScopeOperation.Test]);
        }
        var scope = new WorkerScope(packet.Scope, [], operations);
        var workerTask = new WorkerTask(
            packet.TaskId,
            packet.TaskId,
            packet.Goal,
            BuildPrompt(packet),
            packet.WorkingDirectory,
            snapshot.Provider?.ModelId ?? string.Empty,
            "medium",
            scope,
            [packet.OutputContract],
            packet.AcceptanceCriteria,
            packet.Constraints,
            ApprovalMode: effectiveProfile.ApprovalMode)
        {
            AllowedReadScope = packet.AllowedReadScope,
            AllowedWriteScope = packet.AllowedWriteScope,
            ExternalWorkerPermission = snapshot.ExternalWorkerPermission,
            BudgetSnapshot = snapshot.Budget,
        };
        var execution = await orchestrator.ExecuteAsync(snapshot, workerTask, cancellationToken: cancellationToken);
        return new WorkerResultPacket(
            packet.TaskId,
            execution.Result.Status == WorkerJobStatus.Completed ? DelegationState.ResultReceived : DelegationState.Failed,
            execution.Result.Summary ?? execution.FinalJob.StatusMessage ?? "External Worker 未返回摘要。",
            execution.Result.RawResult is null ? [] : [execution.Result.RawResult.Value.GetRawText()],
            execution.Result.ChangedFiles,
            execution.Result.Status == WorkerJobStatus.Completed ? ["External Agent Runtime completed."] : [],
            execution.Result.Risks,
            execution.ProviderId,
            execution.Result.ResponseModelId ?? execution.ModelId,
            execution.Result.Usage,
            FailureReason: execution.Result.FailureKind,
            ProviderTurns: execution.Result.ProviderTurns,
            ToolCalls: execution.Result.ToolCalls,
            FailedToolCalls: execution.Result.FailedToolCalls,
            DeniedToolCalls: execution.Result.DeniedToolCalls,
            DurationMilliseconds: execution.Result.Duration?.TotalMilliseconds);
    }

    private async Task<BudgetConsumption> ConsumptionAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        decimal daily = 0;
        decimal monthly = 0;
        long tokens = 0;
        var requests = 0;
        foreach (var group in await usage.ListTaskGroupsAsync(cancellationToken))
        {
            foreach (var item in await usage.ListUsageAsync(group.Id, cancellationToken))
            {
                if (item.CapturedAt.ToLocalTime().Date == now.Date) daily += item.Cost.Value ?? 0;
                var local = item.CapturedAt.ToLocalTime();
                if (local.Year == now.Year && local.Month == now.Month) monthly += item.Cost.Value ?? 0;
                tokens += item.TotalTokens.Value ?? 0;
                requests += (int)(item.Requests.Value ?? 0);
            }
        }

        return new BudgetConsumption(0, daily, monthly, tokens, requests);
    }

    private static string BuildPrompt(TaskPacket packet) => $$"""
        TaskId: {{packet.TaskId}}
        Goal: {{packet.Goal}}
        Scope: {{string.Join("; ", packet.Scope)}}
        Allowed read scope: {{string.Join("; ", packet.AllowedReadScope)}}
        Allowed write scope: {{string.Join("; ", packet.AllowedWriteScope)}}
        Acceptance criteria: {{string.Join("; ", packet.AcceptanceCriteria)}}
        Constraints: {{string.Join("; ", packet.Constraints)}}
        Output contract: {{packet.OutputContract}}
        """;
}

public sealed class AppliedProjectWorkerGuard(IProjectRepository projects, IProfileRepository? profiles = null) : ITaskPacketResolver, IDelegationPolicyGuard
{
    public async Task<TaskPacket> ResolveAsync(TaskPacket packet, CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveProjectAsync(packet.WorkingDirectory, packet.ProjectId, cancellationToken);
        var project = resolution.Project;
        if (project is null)
            throw new InvalidOperationException(resolution.Source switch
            {
                "PROJECT_NOT_FOUND" => "PROJECT_NOT_RESOLVED",
                "AMBIGUOUS_PROJECT_MAPPING" => "PROJECT_MAPPING_AMBIGUOUS",
                _ => resolution.Source,
            });
        if (profiles is not null && project.NativeCodexAdaptation?.AppliedSnapshot is { } applied
            && await profiles.GetAsync(applied.ProfileId, cancellationToken) is null)
            throw new InvalidOperationException("PROFILE_NOT_APPLIED");
        var expected = GetAppliedWorker(project);
        if (!string.IsNullOrWhiteSpace(packet.WorkerId))
        {
            return string.Equals(packet.ProjectId, project.Id, StringComparison.Ordinal)
                ? packet
                : packet with { ProjectId = project.Id };
        }

        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidOperationException("未能从项目已应用方案解析 Worker；请先应用包含 Worker 的 Profile，或显式提交与已应用 Worker 完全一致的 WorkerId。");
        }

        return packet with { ProjectId = project.Id, WorkerId = expected };
    }

    public async Task ValidateAsync(TaskPacket packet, CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveProjectAsync(packet.WorkingDirectory, packet.ProjectId, cancellationToken);
        var project = resolution.Project;
        if (project is null)
            throw new InvalidOperationException(resolution.Source switch
            {
                "PROJECT_NOT_FOUND" => "PROJECT_NOT_RESOLVED",
                "AMBIGUOUS_PROJECT_MAPPING" => "PROJECT_MAPPING_AMBIGUOUS",
                _ => resolution.Source,
            });
        if (profiles is not null && project.NativeCodexAdaptation?.AppliedSnapshot is { } applied
            && await profiles.GetAsync(applied.ProfileId, cancellationToken) is null)
            throw new InvalidOperationException("PROFILE_NOT_APPLIED");
        var expected = GetAppliedWorker(project);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        if (!string.Equals(expected, packet.WorkerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"项目已应用 Worker 为 {expected}，TaskPacket 请求的是 {packet.WorkerId}；已拒绝不一致委派。");
        }
    }

    public async Task<ProjectResolution> ResolveProjectAsync(string workingDirectory, string? projectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new(null, null, "MISSING_WORKING_DIRECTORY", []);
        }

        var normalized = NormalizePath(workingDirectory);
        var all = await projects.ListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var explicitProject = await projects.GetAsync(projectId, cancellationToken);
            if (explicitProject is null) return new(null, null, "PROJECT_NOT_FOUND", []);
            var explicitRoot = NormalizePath(explicitProject.WorkingDirectory);
            if (Contains(explicitRoot, normalized)) return new(explicitProject, explicitRoot, "registered-root", []);
            var gitRoot = GitWorktreeMetadata.TryResolveCanonicalRoot(normalized);
            if (gitRoot is not null && Contains(explicitRoot, gitRoot)) return new(explicitProject, gitRoot, "git-worktree", []);
            var candidates = all.Where(item => Contains(NormalizePath(item.WorkingDirectory), normalized)).ToArray();
            if (candidates.Length == 0 && gitRoot is not null)
            {
                candidates = all.Where(item =>
                {
                    var root = NormalizePath(item.WorkingDirectory);
                    return Contains(root, gitRoot) || Contains(gitRoot, root);
                }).ToArray();
            }
            return new(null, null, "PROJECT_ID_MISMATCH", candidates.Select(Candidate).ToArray());
        }

        var direct = all
            .Select(project => new { Project = project, Root = NormalizePath(project.WorkingDirectory) })
            .Where(candidate => Contains(candidate.Root, normalized))
            .OrderByDescending(candidate => candidate.Root.Length)
            .ToArray();
        if (direct.Length > 0)
        {
            return direct.Length == 1
                ? new(direct[0].Project, direct[0].Root, "registered-root", [])
                : new(null, null, "AMBIGUOUS_PROJECT_MAPPING", direct.Select(item => Candidate(item.Project)).ToArray());
        }

        var canonical = GitWorktreeMetadata.TryResolveCanonicalRoot(normalized);
        if (canonical is not null)
        {
            var matches = all
                .Select(project => new { Project = project, Root = NormalizePath(project.WorkingDirectory) })
                .Where(candidate => Contains(candidate.Root, canonical) || Contains(canonical, candidate.Root))
                .OrderByDescending(candidate => candidate.Root.Length)
                .ToArray();
            if (matches.Length > 0)
            {
                return matches.Length == 1
                    ? new(matches[0].Project, canonical, "git-worktree", [])
                    : new(null, null, "AMBIGUOUS_PROJECT_MAPPING", matches.Select(item => Candidate(item.Project)).ToArray());
            }
        }

        return new(null, null, "PROJECT_NOT_FOUND", []);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool Contains(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? GetAppliedWorker(AgentProject? project)
    {
        var snapshot = project?.NativeCodexAdaptation?.AppliedSnapshot;
        return snapshot is null
            ? null
            : string.Equals(snapshot.WorkerKind, nameof(EffectiveWorkerKind.NativeAgent), StringComparison.Ordinal)
                ? snapshot.WorkerRole
                : snapshot.ProviderId;
    }

    private static DelegationProjectCandidate Candidate(AgentProject project) =>
        new(project.Id, project.Name, NormalizePath(project.WorkingDirectory));
}

public sealed record ProjectResolution(
    AgentProject? Project,
    string? MatchedRoot,
    string Source,
    IReadOnlyList<DelegationProjectCandidate> Candidates)
{
    public IReadOnlyList<string> CandidateProjectIds => Candidates.Select(candidate => candidate.ProjectId).ToArray();
}

internal static class GitWorktreeMetadata
{
    public static string? TryResolveCanonicalRoot(string path)
    {
        try
        {
            var current = new DirectoryInfo(path);
            while (current is not null)
            {
                var gitPath = Path.Combine(current.FullName, ".git");
                if (File.Exists(gitPath))
                {
                    var line = File.ReadLines(gitPath).FirstOrDefault(item => item.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
                    if (line is null) return null;
                    var metadata = line["gitdir:".Length..].Trim();
                    metadata = Path.GetFullPath(Path.IsPathRooted(metadata) ? metadata : Path.Combine(current.FullName, metadata));
                    var common = Path.Combine(metadata, "commondir");
                    if (!File.Exists(common)) return null;
                    var commonValue = File.ReadAllText(common).Trim();
                    var commonGit = Path.GetFullPath(Path.IsPathRooted(commonValue) ? commonValue : Path.Combine(metadata, commonValue));
                    return Directory.GetParent(commonGit)?.FullName;
                }
                current = current.Parent;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
