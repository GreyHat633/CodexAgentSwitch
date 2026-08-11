using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Presentation;

public enum UiStatusTone
{
    Neutral,
    Success,
    Info,
    Warning,
    Error,
}

public sealed record AgentSwitchUiSnapshot(
    SchedulerState State,
    string StateLabel,
    string StateDetail,
    UiStatusTone Tone,
    int ActiveTaskCount,
    int ActiveProjectCount,
    IReadOnlyList<ProjectUiStatus> Projects,
    IReadOnlyList<WorkerTaskUiStatus> Tasks,
    UsageUiSummary Usage,
    IReadOnlyList<ProviderUiStatus> Providers,
    string? FaultMessage);

public sealed record ProjectUiStatus(
    string Id,
    string Name,
    string WorkingDirectory,
    bool IsConfigured,
    string ProfileName,
    string MainAgent,
    string Worker,
    string RoutingMode,
    string StateLabel,
    UiStatusTone Tone,
    DateTimeOffset? AppliedAt,
    int ActiveTaskCount,
    string? ActivePhase);

public sealed record WorkerTaskUiStatus(
    string TaskId,
    string ProjectId,
    string ProjectName,
    string Title,
    string ProfileName,
    string WorkerKind,
    string WorkerName,
    string? ProviderId,
    string StateLabel,
    UiStatusTone Tone,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    bool IsActive);

public sealed record UsageUiSummary(
    string AvailabilityLabel,
    UiStatusTone Tone,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    decimal? Cost,
    string Currency,
    int TodayExternalCalls,
    string LatestWorkerKind,
    string LatestCallStatus,
    DateTimeOffset? LatestCallAt,
    string NativeUsageMessage,
    string EvidenceMessage)
{
    public NativeUsageBreakdown Sol { get; init; } = NativeUsageBreakdown.Empty("暂无 Sol 原生会话数据");
    public NativeUsageBreakdown LunaNativeWorker { get; init; } = NativeUsageBreakdown.Empty("暂无 Luna/Native Worker 原生会话数据");
    public NativeUsageBreakdown NativeTotal { get; init; } = NativeUsageBreakdown.Empty("暂无原生会话数据");
    public int NativeExcludedCount { get; init; }
    public string NativeFilterMessage { get; init; } = "仅统计当前未归档项目目录下的原生会话。";
    public bool NativeReadFailed { get; init; }
    public decimal? ExternalBudget { get; init; }
    public long? NativeTokenLimit { get; init; }
}

public sealed record NativeUsageBreakdown(long? InputTokens, long? CachedTokens, long? UncachedTokens, long? OutputTokens, long? ReasoningTokens, long? TotalTokens, long? Calls, string Reason)
{
    public static NativeUsageBreakdown Empty(string reason) => new(null, null, null, null, null, null, null, reason);
}

public sealed record ProviderUiStatus(
    string Id,
    string Name,
    ProviderKind Kind,
    bool IsEnabled,
    bool IsCredentialConfigured,
    bool IsUsedByCurrentProfile,
    string Model,
    string StateLabel,
    UiStatusTone Tone,
    string CredentialLabel,
    string LastCallLabel,
    DateTimeOffset? LastCallAt);

public interface IAgentSwitchUiStateSource
{
    Task<AgentSwitchUiSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only projection over the existing scheduler, project, provider and usage stores.
/// It never dispatches, retries, adopts or otherwise mutates runtime state.
/// </summary>
public sealed class AgentSwitchUiStateProjection(
    IWorkerScheduler scheduler,
    ProjectService projects,
    IProfileRepository profiles,
    IProviderRepository providers,
    ICredentialStore credentials,
    IUsageLedgerRepository usage,
    IUsageSource nativeUsage,
    IClock clock) : IAgentSwitchUiStateSource
{
    public async Task<AgentSwitchUiSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var schedulerSnapshot = scheduler.Snapshot;
        var projectList = await projects.ListAsync(cancellationToken);
        var taskList = await scheduler.ListAsync(cancellationToken);
        var defaultProfile = await profiles.GetDefaultAsync(cancellationToken);
        var providerList = await providers.ListAsync(cancellationToken);
        var usageSnapshots = await ReadUsageAsync(usage, cancellationToken);
        var projectMap = projectList.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var taskItems = taskList
            .OrderByDescending(task => task.UpdatedAt)
            .Select(task => ProjectTask(task, projectMap.GetValueOrDefault(task.Packet.ProjectId)))
            .ToArray();
        var openTasks = taskItems.Where(task => task.IsActive).ToArray();
        var projectItems = projectList
            .Where(project => !project.IsArchived)
            .OrderByDescending(project => project.NativeCodexAdaptation?.AppliedAt)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(project => Project(project, openTasks.Where(task => task.ProjectId == project.Id).ToArray()))
            .ToArray();
        var activeProjectCount = openTasks.Select(task => task.ProjectId).Distinct(StringComparer.Ordinal).Count();
        var stateLabel = schedulerSnapshot.State switch
        {
            SchedulerState.Ready => "Agent Switch 已就绪",
            SchedulerState.Working => "Agent Switch 正在工作",
            SchedulerState.Paused => "Agent Switch 已暂停",
            SchedulerState.Faulted => "Agent Switch 运行异常",
            _ => "Agent Switch 未启动",
        };
        var stateDetail = schedulerSnapshot.State switch
        {
            SchedulerState.Faulted => schedulerSnapshot.FaultMessage ?? "后台组件发生异常。",
            SchedulerState.Paused => openTasks.Length == 0
                ? "不会接受新的 Worker 委派。"
                : $"不会接受新的 Worker 委派；仍有 {openTasks.Length} 个任务未结束。",
            SchedulerState.Working => $"{openTasks.Length} 个活动任务 · {activeProjectCount} 个项目",
            SchedulerState.Ready when openTasks.Length > 0 => $"{openTasks.Length} 个任务正在等待结果或审查。",
            SchedulerState.Ready => "当前没有活动任务。",
            _ => "后台尚未开始接收 Worker 委派。",
        };

        return new AgentSwitchUiSnapshot(
            schedulerSnapshot.State,
            stateLabel,
            stateDetail,
            Tone(schedulerSnapshot.State),
            openTasks.Length,
            activeProjectCount,
            projectItems,
            taskItems,
            await BuildUsageAsync(usageSnapshots, taskList, projectList, defaultProfile, nativeUsage, clock.UtcNow, cancellationToken),
            await BuildProvidersAsync(providerList, defaultProfile, taskList, usageSnapshots, cancellationToken),
            schedulerSnapshot.FaultMessage);
    }

    private async Task<IReadOnlyList<ProviderUiStatus>> BuildProvidersAsync(
        IReadOnlyList<ProviderConfiguration> providerList,
        Profile? defaultProfile,
        IReadOnlyList<ScheduledDelegation> tasks,
        IReadOnlyList<UsageSnapshot> usageSnapshots,
        CancellationToken cancellationToken)
    {
        var result = new List<ProviderUiStatus>(providerList.Count);
        foreach (var provider in providerList)
        {
            var credentialConfigured = provider.Kind == ProviderKind.NativeCodex
                || (!string.IsNullOrWhiteSpace(provider.CredentialReference)
                    && await credentials.ExistsAsync(provider.CredentialReference, cancellationToken));
            var lastTask = tasks
                .Where(task => string.Equals(task.Result?.ProviderId, provider.Id, StringComparison.Ordinal)
                    || string.Equals(task.Packet.WorkerId, provider.Id, StringComparison.Ordinal))
                .OrderByDescending(task => task.UpdatedAt)
                .FirstOrDefault();
            var lastUsage = usageSnapshots
                .Where(item => string.Equals(item.ProviderId, provider.Id, StringComparison.Ordinal))
                .OrderByDescending(item => item.CapturedAt)
                .FirstOrDefault();
            var lastAt = Max(lastTask?.UpdatedAt, lastUsage?.CapturedAt);
            var lastCallLabel = lastTask is null && lastUsage is null
                ? "暂无调用记录"
                : lastTask?.State == DelegationState.Failed
                    ? $"失败 · {FormatTime(lastAt)}"
                    : $"成功 · {FormatTime(lastAt)}";
            var usedByProfile = provider.Kind == ProviderKind.NativeCodex
                ? defaultProfile is not null
                : string.Equals(defaultProfile?.WorkerPolicy.PreferredProviderId, provider.Id, StringComparison.Ordinal);
            var usable = provider.IsEnabled && credentialConfigured;
            var stateLabel = provider.Kind == ProviderKind.NativeCodex
                ? "已配置"
                : !provider.IsEnabled
                    ? "已停用"
                    : credentialConfigured ? "已启用" : "需要凭据";
            result.Add(new ProviderUiStatus(
                provider.Id,
                provider.Name,
                provider.Kind,
                provider.IsEnabled,
                credentialConfigured,
                usedByProfile,
                ProviderModel(provider),
                stateLabel,
                provider.Kind == ProviderKind.NativeCodex || usable ? UiStatusTone.Success : UiStatusTone.Warning,
                provider.Kind == ProviderKind.NativeCodex ? "Codex Desktop 认证" : credentialConfigured ? "已安全配置" : "未配置",
                lastCallLabel,
                lastAt));
        }

        return result;
    }

    private static async Task<IReadOnlyList<UsageSnapshot>> ReadUsageAsync(
        IUsageLedgerRepository repository,
        CancellationToken cancellationToken)
    {
        var result = new List<UsageSnapshot>();
        foreach (var ledger in await repository.ListTaskGroupsAsync(cancellationToken))
        {
            result.AddRange(await repository.ListUsageAsync(ledger.Id, cancellationToken));
        }

        return result;
    }

    private static ProjectUiStatus Project(AgentProject project, IReadOnlyList<WorkerTaskUiStatus> activeTasks)
    {
        var snapshot = project.NativeCodexAdaptation?.AppliedSnapshot;
        var adaptation = project.NativeCodexAdaptation;
        var active = activeTasks.OrderByDescending(task => task.UpdatedAt).FirstOrDefault();
        var configured = snapshot is not null;
        return new ProjectUiStatus(
            project.Id,
            project.Name,
            project.WorkingDirectory,
            configured,
            snapshot?.ProfileName ?? "尚未应用方案",
            snapshot is null ? "未配置" : $"{Model(snapshot.MainModel)} · {Reasoning(snapshot.MainReasoningEffort)}",
            snapshot is null ? "未配置" : Worker(snapshot),
            snapshot is null ? "未配置" : Routing(snapshot.RoutingMode),
            active?.StateLabel ?? (configured ? "已就绪" : "尚未配置"),
            active?.Tone ?? (configured ? UiStatusTone.Success : UiStatusTone.Neutral),
            adaptation?.AppliedAt,
            activeTasks.Count,
            active?.StateLabel);
    }

    private static WorkerTaskUiStatus ProjectTask(ScheduledDelegation task, AgentProject? project)
    {
        var snapshot = project?.NativeCodexAdaptation?.AppliedSnapshot;
        var (stateLabel, tone, active) = TaskState(task.State);
        var workerName = task.Result?.ModelId
            ?? snapshot?.WorkerModel
            ?? task.Packet.WorkerId;
        return new WorkerTaskUiStatus(
            task.Packet.TaskId,
            task.Packet.ProjectId,
            project?.Name ?? FolderName(task.Packet.WorkingDirectory),
            ReadableTitle(task.Packet.Goal, task.Packet.TaskId),
            snapshot?.ProfileName ?? "方案快照暂不可取得",
            task.Transport == WorkerTransport.NativeCustomAgent ? "原生 Worker" : "外部 Worker",
            Model(workerName),
            task.Result?.ProviderId ?? (task.Transport == WorkerTransport.ExternalProvider ? snapshot?.ProviderId : "native-codex"),
            stateLabel,
            tone,
            task.CreatedAt,
            task.StartedAt,
            task.UpdatedAt,
            task.CompletedAt,
            task.FailureReason ?? task.Result?.FailureReason,
            active);
    }

    private static async Task<UsageUiSummary> BuildUsageAsync(
        IReadOnlyList<UsageSnapshot> snapshots,
        IReadOnlyList<ScheduledDelegation> tasks,
        IReadOnlyList<AgentProject> projects,
        Profile? profile,
        IUsageSource nativeUsage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        snapshots = snapshots.Where(item => !string.Equals(item.ProviderId, "native-codex", StringComparison.OrdinalIgnoreCase)).ToArray();
        var actual = snapshots.Where(item => item.TotalTokens.Evidence == EvidenceKind.Actual).ToArray();
        var latest = snapshots.OrderByDescending(item => item.CapturedAt).FirstOrDefault();
        var latestTask = tasks.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        var costs = snapshots.Where(item => item.Cost.Value is not null).ToArray();
        var actualCosts = costs.Where(item => item.Cost.Evidence == EvidenceKind.Actual).ToArray();
        var selectedCosts = actualCosts.Length > 0 ? actualCosts : costs;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var todayCalls = snapshots.Count(item => DateOnly.FromDateTime(item.CapturedAt.LocalDateTime) == today
            && !string.Equals(item.ProviderId, "native-codex", StringComparison.Ordinal));
        var latestStatus = latestTask is null
            ? "暂无数据"
            : latestTask.State == DelegationState.Failed ? "失败" : "成功";
        var summary = new UsageUiSummary(
            snapshots.Count == 0 ? "暂无数据" : actual.Length > 0 ? "可取得" : "暂不可取得",
            snapshots.Count == 0 ? UiStatusTone.Neutral : actual.Length > 0 ? UiStatusTone.Success : UiStatusTone.Warning,
            Sum(actual, item => item.InputTokens.Value),
            Sum(actual, item => item.OutputTokens.Value),
            Sum(actual, item => item.TotalTokens.Value),
            selectedCosts.Length == 0 ? null : selectedCosts.Sum(item => item.Cost.Value ?? 0m),
            selectedCosts.FirstOrDefault()?.Currency ?? "CNY",
            todayCalls,
            latestTask is null ? "暂无任务" : latestTask.Transport == WorkerTransport.NativeCustomAgent ? "原生 Worker" : "外部 Worker",
            latestStatus,
            latest?.CapturedAt ?? latestTask?.UpdatedAt,
            "Luna 独立 Token Usage：当前 Codex Native Worker 接口未提供。",
            snapshots.Count == 0
                ? "尚无 External Worker 调用记录。"
                : actual.Length > 0 ? "仅汇总 Provider 返回的实际 Token Usage。" : "当前记录没有可靠的 Token 字段，未生成估算值。"
        );
        IReadOnlyList<NativeUsageRecord> nativeRecords;
        try { nativeRecords = await Task.Run(() => nativeUsage.Read(cancellationToken), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return summary with { NativeReadFailed = true, NativeFilterMessage = $"原生 Usage 读取失败：{ex.Message}" }; }
        var dirs = projects.Where(p => !p.IsArchived && p.NativeCodexAdaptation?.AppliedSnapshot is not null).Select(p => Path.GetFullPath(p.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToArray();
        var included = nativeRecords.Where(r => IsInProject(r, dirs)).ToArray();
        var excluded = nativeRecords.Count - included.Length;
        var sol = Aggregate(included.Where(r => string.Equals(r.AgentRole, "Sol", StringComparison.OrdinalIgnoreCase)));
        var luna = Aggregate(included.Where(r => r.AgentRole.Contains("luna", StringComparison.OrdinalIgnoreCase) || r.AgentRole.Contains("native", StringComparison.OrdinalIgnoreCase)));
        var total = Aggregate(included);
        return summary with { Sol = sol, LunaNativeWorker = luna, NativeTotal = total, NativeExcludedCount = excluded,
            NativeFilterMessage = excluded > 0 ? $"已排除 {excluded} 条无法匹配当前项目目录的原生会话。" : "仅统计当前未归档项目目录下的原生会话。",
            ExternalBudget = profile?.Budget.Daily, NativeTokenLimit = profile?.Budget.TokenLimit };
    }

    private static bool IsInProject(NativeUsageRecord record, IReadOnlyList<string> dirs)
    {
        var path = record.Project ?? record.Cwd;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); return dirs.Any(d => string.Equals(full, d, StringComparison.OrdinalIgnoreCase) || full.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static NativeUsageBreakdown Aggregate(IEnumerable<NativeUsageRecord> records)
    {
        var a = records.ToArray();
        return a.Length == 0 ? NativeUsageBreakdown.Empty("当前项目目录下暂无匹配会话") : new(a.Sum(x => x.InputTokens), a.Sum(x => x.CachedInputTokens), a.Sum(x => x.UncachedInputTokens), a.Sum(x => x.OutputTokens), a.Sum(x => x.ReasoningTokens), a.Sum(x => x.TotalTokens), a.Sum(x => x.Calls), "来自原生 Codex 会话记录");
    }

    private static long? Sum(IEnumerable<UsageSnapshot> snapshots, Func<UsageSnapshot, long?> selector)
    {
        var values = snapshots.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Sum();
    }

    private static (string Label, UiStatusTone Tone, bool Active) TaskState(DelegationState state) => state switch
    {
        DelegationState.Created => ("等待执行", UiStatusTone.Neutral, true),
        DelegationState.Delegated => ("已委派", UiStatusTone.Info, true),
        DelegationState.Running => ("Worker 执行中", UiStatusTone.Info, true),
        DelegationState.ResultReceived => ("Worker 已返回", UiStatusTone.Info, true),
        DelegationState.Reviewing => ("主代理审查中", UiStatusTone.Info, true),
        DelegationState.Adopted => ("已完成", UiStatusTone.Success, false),
        DelegationState.Failed => ("失败", UiStatusTone.Error, false),
        DelegationState.Cancelled => ("已取消", UiStatusTone.Warning, false),
        _ => (state.ToString(), UiStatusTone.Neutral, false),
    };

    private static UiStatusTone Tone(SchedulerState state) => state switch
    {
        SchedulerState.Ready => UiStatusTone.Success,
        SchedulerState.Working => UiStatusTone.Info,
        SchedulerState.Paused => UiStatusTone.Warning,
        SchedulerState.Faulted => UiStatusTone.Error,
        _ => UiStatusTone.Neutral,
    };

    private static string Worker(NativeCodexAppliedSnapshot snapshot) => snapshot.WorkerKind switch
    {
        "NativeAgent" => $"{Model(snapshot.WorkerModel ?? snapshot.WorkerRole ?? "Native Worker")} · {Reasoning(snapshot.WorkerReasoningEffort)}",
        "ExternalAgent" => $"{Model(snapshot.WorkerModel ?? snapshot.ProviderId ?? "External Worker")} · {snapshot.ProviderId}",
        _ => "未启用",
    };

    private static string ProviderModel(ProviderConfiguration provider) => provider.Kind == ProviderKind.NativeCodex
        ? "Sol / Terra / Luna"
        : Model(provider.ModelId ?? "未选择模型");

    private static string Model(string model) => model switch
    {
        "gpt-5.6-sol" => "GPT-5.6 Sol",
        "gpt-5.6-terra" => "GPT-5.6 Terra",
        "gpt-5.6-luna" => "GPT-5.6 Luna",
        DeepSeekV4Catalog.FlashModelId => "DeepSeek V4 Flash 0731",
        DeepSeekV4Catalog.ProModelId => "DeepSeek V4 Pro",
        _ => model,
    };

    private static string Reasoning(string effort) => effort switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" or "max" => "Max",
        _ => effort,
    };

    private static string Routing(string routing) => routing switch
    {
        "Economic" => "经济优先",
        "Balanced" => "平衡模式",
        "Performance" => "性能优先",
        "Manual" => "手动模式",
        "Single" => "单代理模式",
        _ => routing,
    };

    private static string ReadableTitle(string goal, string fallback)
    {
        var line = goal.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return fallback;
        }

        return line.Length <= 72 ? line : $"{line[..69]}…";
    }

    private static string FolderName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("HH:mm") ?? "时间不可取得";

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;
}
