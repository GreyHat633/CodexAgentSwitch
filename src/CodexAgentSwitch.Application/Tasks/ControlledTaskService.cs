using System.Collections.Concurrent;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Application.Tasks;

public sealed class ControlledTaskService
{
    private readonly IControlledTaskRepository tasks;
    private readonly IProfileRepository profiles;
    private readonly IControlledTaskRuntime runtime;
    private readonly TaskProfileSnapshotFactory snapshotFactory;
    private readonly DelegationDecisionService delegationDecisions;
    private readonly WorkerOrchestrator workerOrchestrator;
    private readonly IUsageLedgerRepository usageLedger;
    private readonly IWorkerUsageCollector usageCollector;
    private readonly IClock clock;
    private readonly IProjectRepository? projectRepository;
    // Retained only for manual/backward-compatible recovery helpers. The
    // 0.2.5 execution path does not call the legacy age/cost budget rollover.
    private readonly SessionContextBudget contextBudget;
    private readonly MainCostGuardCoordinator mainCostGuards;
    private readonly IMainContextEconomyCoordinator? contextEconomy;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MainContextEpoch> contextEpochs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim updateGate = new(1, 1);

    public ControlledTaskService(
        IControlledTaskRepository tasks,
        IProfileRepository profiles,
        IControlledTaskRuntime runtime,
        TaskProfileSnapshotFactory snapshotFactory,
        DelegationDecisionService delegationDecisions,
        WorkerOrchestrator workerOrchestrator,
        IUsageLedgerRepository usageLedger,
        IWorkerUsageCollector usageCollector,
        IClock clock,
        IProjectRepository? projectRepository = null,
        SessionContextBudget? contextBudget = null,
        MainCostGuardCoordinator? mainCostGuards = null,
        IMainContextEconomyCoordinator? contextEconomy = null)
    {
        this.tasks = tasks;
        this.profiles = profiles;
        this.runtime = runtime;
        this.snapshotFactory = snapshotFactory;
        this.delegationDecisions = delegationDecisions;
        this.workerOrchestrator = workerOrchestrator;
        this.usageLedger = usageLedger;
        this.usageCollector = usageCollector;
        this.clock = clock;
        this.projectRepository = projectRepository;
        this.contextBudget = contextBudget ?? new SessionContextBudget();
        this.mainCostGuards = mainCostGuards ?? new MainCostGuardCoordinator();
        this.contextEconomy = contextEconomy;
    }

    public event Func<ControlledTaskSession, Task>? TaskChanged;

    public Task<IReadOnlyList<ControlledTaskSession>> ListAsync(CancellationToken cancellationToken = default) =>
        tasks.ListAsync(cancellationToken);

    public Task<ControlledTaskSession?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        tasks.GetAsync(id, cancellationToken);

    public async Task<IReadOnlyList<ControlledTaskSession>> ListProjectConversationsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return (await tasks.ListAsync(cancellationToken))
            .Where(task => string.Equals(task.ProjectId, projectId, StringComparison.Ordinal))
            .OrderByDescending(task => task.UpdatedAt)
            .ToArray();
    }

    public async Task<ControlledTaskSession> RenameConversationAsync(
        string taskId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 120)
        {
            throw new InvalidOperationException("对话名称不能超过 120 个字符。");
        }

        await MutateAsync(taskId, session => session with
        {
            Title = normalized,
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);
        return await RequireAsync(taskId, cancellationToken);
    }

    public async Task DeleteConversationAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(taskId, cancellationToken);
        if (active.ContainsKey(taskId) || IsRunning(session.Status))
        {
            throw new InvalidOperationException("运行中的对话不能删除，请先停止生成。");
        }

        await tasks.DeleteAsync(taskId, cancellationToken);
    }

    public async Task DeleteProjectConversationsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        foreach (var conversation in await ListProjectConversationsAsync(projectId, cancellationToken))
        {
            await DeleteConversationAsync(conversation.Id, cancellationToken);
        }
    }

    public async Task<ControlledTaskSession> RetryLastTurnAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(taskId, cancellationToken);
        var turn = session.Turns.LastOrDefault()
            ?? throw new InvalidOperationException("该对话没有可重试的内容。");
        if (IsRunning(session.Status))
        {
            throw new InvalidOperationException("当前对话仍在生成，不能重试。");
        }

        return await ContinueAsync(
            taskId,
            turn.UserInput,
            turn.Delegation?.Kind == DelegationDecisionKind.InvokeWorker,
            cancellationToken);
    }

    public Task<ControlledTaskSession> StartAsync(
        string input,
        string workingDirectory,
        bool? useWorker = null,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(null, input, workingDirectory, useWorker, cancellationToken);

    public Task<ControlledTaskSession> StartInProjectAsync(
        string projectId,
        string input,
        string workingDirectory,
        bool? useWorker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return StartCoreAsync(projectId, input, workingDirectory, useWorker, cancellationToken);
    }

    public async Task<ControlledTaskSession> CreateConversationAsync(
        string projectId,
        string workingDirectory,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{cwd}");
        }

        var profile = await ResolveProfileForProjectAsync(projectId, cancellationToken);
        var initialSnapshot = await snapshotFactory.CaptureAsync(profile, cancellationToken);
        var now = clock.UtcNow;
        var session = new ControlledTaskSession(
            Guid.NewGuid().ToString("D"),
            profile.Id,
            profile.Name,
            string.IsNullOrWhiteSpace(title) ? "新对话" : title.Trim(),
            cwd,
            profile.MainAgent.ModelId,
            profile.MainAgent.ReasoningEffort,
            null,
            ControlledTaskStatus.Completed,
            [],
            now,
            now,
            null,
            null,
            projectId,
            false,
            initialSnapshot);
        await SaveAndPublishAsync(session, cancellationToken);
        return session;
    }

    public async Task<ControlledTaskSession> ApplyProfileFromNextTurnAsync(
        string taskId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(taskId, cancellationToken);
        if (IsRunning(session.Status))
        {
            throw new InvalidOperationException("对话正在生成，不能在当前轮中更换方案。");
        }

        var profile = await profiles.GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("所选方案不存在。" );
        var snapshot = await snapshotFactory.CaptureAsync(profile, cancellationToken);
        await MutateAsync(taskId, current => current with
        {
            ProfileId = snapshot.ProfileId,
            ProfileName = snapshot.ProfileName,
            MainModelId = snapshot.MainAgent.ModelId,
            MainReasoningEffort = snapshot.MainAgent.ReasoningEffort,
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);
        return await RequireAsync(taskId, cancellationToken);
    }

    public async Task ApplyProjectProfileFromNextTurnAsync(
        string projectId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        foreach (var conversation in await ListProjectConversationsAsync(projectId, cancellationToken))
        {
            if (!IsRunning(conversation.Status))
            {
                await ApplyProfileFromNextTurnAsync(conversation.Id, profileId, cancellationToken);
            }
        }
    }

    private async Task<ControlledTaskSession> StartCoreAsync(
        string? projectId,
        string input,
        string workingDirectory,
        bool? useWorker,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory.Trim());
        if (!Directory.Exists(fullWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{fullWorkingDirectory}");
        }

        var profile = await ResolveProfileForProjectAsync(projectId, cancellationToken);
        var snapshot = await snapshotFactory.CaptureAsync(profile, cancellationToken);
        var decision = delegationDecisions.Decide(snapshot, useWorker);
        var now = clock.UtcNow;
        var localTurnId = Guid.NewGuid().ToString("D");
        var session = new ControlledTaskSession(
            Guid.NewGuid().ToString("D"),
            snapshot.ProfileId,
            snapshot.ProfileName,
            CreateTitle(input),
            fullWorkingDirectory,
            snapshot.MainAgent.ModelId,
            snapshot.MainAgent.ReasoningEffort,
            null,
            ControlledTaskStatus.Queued,
            [NewTurn(localTurnId, input, now, snapshot, decision)],
            now,
            now,
            null,
            null,
            projectId,
            false,
            snapshot);
        await SaveAndPublishAsync(session, cancellationToken);
        StartBackground(session.Id, localTurnId, snapshot, decision);
        return session;
    }

    private async Task<Profile> ResolveProfileForProjectAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(projectId) && projectRepository is not null)
        {
            var project = await projectRepository.GetAsync(projectId, cancellationToken);
            if (project?.DefaultProfileId is { } profileId)
            {
                return await profiles.GetAsync(profileId, cancellationToken)
                    ?? throw new InvalidOperationException("项目默认方案已被删除；请重新选择项目默认方案。");
            }
        }

        return await profiles.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("尚未设置当前配置方案。");
    }

    public async Task<ControlledTaskSession> ContinueAsync(
        string taskId,
        string input,
        bool? useWorker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var session = await RequireAsync(taskId, cancellationToken);
        if (!IsRunning(session.Status) && active.TryRemove(taskId, out var completedRun))
            completedRun.Dispose();
        if (active.ContainsKey(taskId) || IsRunning(session.Status))
        {
            throw new InvalidOperationException("该任务仍在运行，请等待完成或先取消。");
        }

        var profile = await profiles.GetAsync(session.ProfileId, cancellationToken)
            ?? await profiles.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("任务对应的配置方案已不存在，且没有默认方案可用。");
        var snapshot = await snapshotFactory.CaptureAsync(profile, cancellationToken);
        var decision = delegationDecisions.Decide(snapshot, useWorker);
        var now = clock.UtcNow;
        var localTurnId = Guid.NewGuid().ToString("D");
        session = session with
        {
            ProfileId = snapshot.ProfileId,
            ProfileName = snapshot.ProfileName,
            MainModelId = snapshot.MainAgent.ModelId,
            MainReasoningEffort = snapshot.MainAgent.ReasoningEffort,
            Status = ControlledTaskStatus.Queued,
            Turns = session.Turns.Append(NewTurn(localTurnId, input, now, snapshot, decision)).ToArray(),
            UpdatedAt = now,
            CompletedAt = null,
            ErrorMessage = null,
        };
        await SaveAndPublishAsync(session, cancellationToken);
        StartBackground(session.Id, localTurnId, snapshot, decision);
        return session;
    }

    public async Task<ControlledTaskSession> ForceTestCurrentWorkerAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(taskId, cancellationToken);
        if (active.ContainsKey(taskId) || IsRunning(session.Status))
        {
            throw new InvalidOperationException("当前对话仍在运行，请等待完成或先停止生成。");
        }

        var profile = await profiles.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("尚未设置当前配置方案。");
        var snapshot = await snapshotFactory.CaptureAsync(profile, cancellationToken);
        var decision = delegationDecisions.Decide(snapshot, requested: true, forced: true);
        var now = clock.UtcNow;
        var localTurnId = Guid.NewGuid().ToString("D");
        const string input = "强制测试当前 Worker：请仅返回当前 Provider 和模型可用性的简短确认。";
        session = session with
        {
            ProfileId = snapshot.ProfileId,
            ProfileName = snapshot.ProfileName,
            MainModelId = snapshot.MainAgent.ModelId,
            MainReasoningEffort = snapshot.MainAgent.ReasoningEffort,
            Status = ControlledTaskStatus.Queued,
            Turns = session.Turns.Append(NewTurn(localTurnId, input, now, snapshot, decision)).ToArray(),
            UpdatedAt = now,
            CompletedAt = null,
            ErrorMessage = null,
        };
        await SaveAndPublishAsync(session, cancellationToken);
        StartWorkerTestBackground(session.Id, localTurnId, snapshot);
        return session;
    }

    public async Task CancelAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(taskId, cancellationToken);
        var turn = session.Turns.LastOrDefault();
        if (session.MainThreadId is not null && turn?.ServerTurnId is not null && IsRunning(session.Status))
        {
            await runtime.EnsureStartedAsync(cancellationToken);
            await runtime.MainAgent.InterruptTurnAsync(session.MainThreadId, turn.ServerTurnId, cancellationToken);
        }

        if (active.TryGetValue(taskId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    public async Task RespondToApprovalAsync(string taskId, bool approve, CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(taskId, cancellationToken);
        var turn = session.Turns.LastOrDefault()
            ?? throw new InvalidOperationException("任务没有可审批的 Turn。");
        if (session.MainThreadId is null || turn.ServerTurnId is null)
        {
            throw new InvalidOperationException("任务尚未建立主代理 Turn。");
        }

        await runtime.EnsureStartedAsync(cancellationToken);
        await runtime.MainAgent.RespondToApprovalAsync(session.MainThreadId, turn.ServerTurnId, approve, cancellationToken);
        await UpdateStatusAsync(taskId, turn.Id, ControlledTaskStatus.MainAgentRunning, null, cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var session in await tasks.ListAsync(cancellationToken))
        {
            if (!IsRunning(session.Status))
            {
                continue;
            }

            var turn = session.Turns.LastOrDefault();
            if (session.MainThreadId is null || turn?.ServerTurnId is null)
            {
                await MarkRecoveryRequiredAsync(session, "应用关闭时 Worker 尚未完成；该运行状态无法安全重建。", cancellationToken);
                continue;
            }

            try
            {
                await runtime.EnsureStartedAsync(cancellationToken);
                await runtime.MainAgent.ResumeThreadAsync(
                    session.MainThreadId,
                    session.MainModelId,
                    session.WorkingDirectory,
                    turn.ProfileSnapshot?.ApprovalMode ?? ExecutionApprovalMode.Automatic,
                    cancellationToken);
                var result = await runtime.MainAgent.ReadTurnAsync(session.MainThreadId, turn.ServerTurnId, cancellationToken);
                await CompleteMainTurnAsync(session.Id, turn.Id, result, cancellationToken);
            }
            catch (Exception exception)
            {
                await MarkRecoveryRequiredAsync(session, $"恢复 Thread 失败：{exception.Message}", cancellationToken);
            }
        }
    }

    private void StartBackground(
        string taskId,
        string localTurnId,
        TaskProfileSnapshot snapshot,
        DelegationDecision decision)
    {
        var cancellation = new CancellationTokenSource();
        if (!active.TryAdd(taskId, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("任务已经在运行。");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(taskId, localTurnId, snapshot, decision, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                await FailAsync(taskId, localTurnId, ControlledTaskStatus.Interrupted, "任务已取消。", CancellationToken.None);
            }
            catch (Exception exception)
            {
                await FailAsync(taskId, localTurnId, ControlledTaskStatus.Failed, exception.Message, CancellationToken.None);
            }
            finally
            {
                active.TryRemove(taskId, out _);
                cancellation.Dispose();
            }
        });
    }

    private void StartWorkerTestBackground(
        string taskId,
        string localTurnId,
        TaskProfileSnapshot snapshot)
    {
        var cancellation = new CancellationTokenSource();
        if (!active.TryAdd(taskId, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("任务已经在运行。");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (snapshot.WorkerPolicy.Source == WorkerSource.NativeCodex)
                {
                    await runtime.EnsureStartedAsync(cancellation.Token);
                }

                var session = await RequireAsync(taskId, cancellation.Token);
                var ledger = await EnsureLedgerAsync(session, cancellation.Token);
                var worker = await ExecuteWorkerAsync(session, localTurnId, snapshot, ledger, forceNative: false, delegation: null, cancellationToken: cancellation.Token);
                await CompleteWorkerOnlyTurnAsync(
                    taskId,
                    localTurnId,
                    worker.Succeeded,
                    worker.Summary,
                    cancellation.Token);
                await usageLedger.UpsertTaskGroupAsync(worker.Ledger with
                {
                    CompletedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow,
                }, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                await FailAsync(taskId, localTurnId, ControlledTaskStatus.Interrupted, "Worker 测试已取消。", CancellationToken.None);
            }
            catch (Exception exception)
            {
                await FailAsync(taskId, localTurnId, ControlledTaskStatus.Failed, exception.Message, CancellationToken.None);
            }
            finally
            {
                active.TryRemove(taskId, out _);
                cancellation.Dispose();
            }
        });
    }

    private async Task ExecuteAsync(
        string taskId,
        string localTurnId,
        TaskProfileSnapshot snapshot,
        DelegationDecision decision,
        CancellationToken cancellationToken)
    {
        await runtime.EnsureStartedAsync(cancellationToken);
        var session = await RequireAsync(taskId, cancellationToken);
        var mainThreadId = session.MainThreadId;
        if (mainThreadId is null)
        {
            mainThreadId = await runtime.MainAgent.CreateThreadAsync(
                snapshot.MainAgent.ModelId,
                session.WorkingDirectory,
                snapshot.ApprovalMode,
                cancellationToken);
            session = session with { MainThreadId = mainThreadId, UpdatedAt = clock.UtcNow };
            await SaveAndPublishAsync(session, cancellationToken);
        }
        else
        {
            await runtime.MainAgent.ResumeThreadAsync(
                mainThreadId,
                snapshot.MainAgent.ModelId,
                session.WorkingDirectory,
                snapshot.ApprovalMode,
                cancellationToken);
        }

        var ledger = await EnsureLedgerAsync(session, cancellationToken);
        if (contextEconomy is not null)
            await contextEconomy.BindThreadAsync(mainThreadId, runtime.MainAgent, cancellationToken);

        var effectiveDecision = decision;
        if (decision.Kind == DelegationDecisionKind.InvokeWorker && !decision.Forced)
        {
            (effectiveDecision, ledger) = await ConfirmDelegationWithSolAsync(
                session,
                localTurnId,
                mainThreadId,
                snapshot,
                ledger,
                cancellationToken);
            await SetDelegationAsync(taskId, localTurnId, effectiveDecision, cancellationToken);
        }

        string? workerSummary = null;
        if (effectiveDecision.Kind == DelegationDecisionKind.InvokeWorker)
        {
            try
            {
                var workerExecution = await ExecuteWorkerAsync(session, localTurnId, snapshot, ledger, forceNative: false, delegation: effectiveDecision, cancellationToken: cancellationToken);
                workerSummary = workerExecution.Summary;
                ledger = workerExecution.Ledger;
                if (!workerExecution.Succeeded
                    && snapshot.WorkerPolicy.FallbackAction == FallbackAction.NativeLuna
                    && snapshot.WorkerPolicy.Source == WorkerSource.ExternalProvider)
                {
                    var fallback = await ExecuteWorkerAsync(session, localTurnId, snapshot, ledger, forceNative: true, delegation: effectiveDecision, cancellationToken: cancellationToken);
                    if (!fallback.Succeeded)
                    {
                        throw new InvalidOperationException($"外部 Worker 和方案指定的原生 Luna 回退均失败：{fallback.Summary}");
                    }

                    workerSummary = fallback.Summary;
                    ledger = fallback.Ledger;
                }
                else if (!workerExecution.Succeeded && snapshot.WorkerPolicy.FallbackAction == FallbackAction.AskUser)
                {
                    throw new InvalidOperationException("工作代理失败；当前方案要求先询问用户，任务已停止在主代理执行前。");
                }
                else if (!workerExecution.Succeeded && snapshot.WorkerPolicy.FallbackAction == FallbackAction.StopDelegation)
                {
                    throw new InvalidOperationException($"工作代理失败；当前方案要求停止委派链：{workerExecution.Summary}");
                }
            }
            catch (Exception exception) when (snapshot.WorkerPolicy.FallbackAction == FallbackAction.SingleAgent)
            {
                workerSummary = $"工作代理未能启动或完成，已按当前方案由主代理接管。原因：{exception.Message}";
            }
        }

        await UpdateStatusAsync(taskId, localTurnId, ControlledTaskStatus.MainAgentRunning, null, cancellationToken);
        session = await RequireAsync(taskId, cancellationToken);
        var userInput = session.Turns.Single(turn => turn.Id == localTurnId).UserInput;
        var prompt = BuildMainPrompt(userInput, snapshot, workerSummary);
        var handle = await runtime.MainAgent.StartTurnAsync(
            mainThreadId,
            prompt,
            snapshot.MainAgent.ModelId,
            snapshot.MainAgent.ReasoningEffort,
            session.WorkingDirectory,
            snapshot.ApprovalMode,
            cancellationToken);
        await SetServerTurnAsync(taskId, localTurnId, handle.TurnId, cancellationToken);

        async Task Handler(MainAgentEvent activity)
        {
            if (activity.ThreadId != mainThreadId || activity.TurnId != handle.TurnId)
            {
                return;
            }

            if (activity.Kind == MainAgentEventKind.OutputDelta && !string.IsNullOrEmpty(activity.Text))
            {
                await AppendMainOutputAsync(taskId, localTurnId, activity.Text, false, CancellationToken.None);
            }
            else if (activity.Kind == MainAgentEventKind.TraceItem && !string.IsNullOrWhiteSpace(activity.Text))
            {
                await AppendTraceAsync(
                    taskId,
                    localTurnId,
                    activity.Text,
                    activity.MessageKind ?? TaskMessageKind.ToolCall,
                    activity.Status,
                    CancellationToken.None);
            }
            else if (activity.Kind == MainAgentEventKind.ApprovalRequested)
            {
                await UpdateStatusAsync(taskId, localTurnId, ControlledTaskStatus.WaitingForApproval, activity.Text, CancellationToken.None);
            }
        }

        runtime.MainAgent.EventReceived += Handler;
        MainAgentTurnResult mainResult;
        try
        {
            mainResult = await runtime.MainAgent.WaitForTurnAsync(mainThreadId, handle.TurnId, cancellationToken);
        }
        finally
        {
            runtime.MainAgent.EventReceived -= Handler;
        }

        await CompleteMainTurnAsync(taskId, localTurnId, mainResult, cancellationToken);
        var mainUsage = usageCollector.Capture(
            taskId,
            $"main:{handle.TurnId}",
            new WorkerResult(localTurnId, ToWorkerStatus(mainResult.Status), mainResult.FinalText, mainResult.RawTurn, [], []),
            new WorkerUsageContext("native-codex", snapshot.MainAgent.ModelId, snapshot.Budget.Currency, null));
        await usageLedger.AppendUsageAsync(mainUsage, cancellationToken);
        if (contextEconomy is not null && mainUsage.InputTokens.Value is > 0)
        {
            await contextEconomy.ObserveTurnAsync(
                mainThreadId,
                new ContextTurnSample(mainUsage.InputTokens.Value.Value, 0, CapturedAt: mainUsage.CapturedAt),
                safeBoundary: true,
                cancellationToken);
        }
        ledger = ledger with { CompletedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        await usageLedger.UpsertTaskGroupAsync(ledger, cancellationToken);
    }

    private async Task<(ControlledTaskSession Session, string MainThreadId, TaskGroupLedger Ledger)> PrepareMainContextAsync(
        ControlledTaskSession session,
        string localTurnId,
        string mainThreadId,
        TaskProfileSnapshot snapshot,
        TaskGroupLedger ledger,
        CancellationToken cancellationToken)
    {
        var cumulativeNormalizedCost = mainCostGuards.ResolveForWorkingDirectory(session.WorkingDirectory)
            .Telemetry.SessionCumulativeNormalizedCredits;
        var epoch = contextEpochs.GetOrAdd(
            session.Id,
            _ => new MainContextEpoch(mainThreadId, session.CreatedAt, 0, 0m, false));
        if (!string.Equals(epoch.ThreadId, mainThreadId, StringComparison.Ordinal))
        {
            epoch = new MainContextEpoch(
                mainThreadId,
                clock.UtcNow,
                Math.Max(0, session.Turns.Count - 1),
                cumulativeNormalizedCost,
                false);
            contextEpochs[session.Id] = epoch;
        }

        var age = clock.UtcNow - epoch.StartedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        var turnCount = Math.Max(0, session.Turns.Count - epoch.StartTurnCount);
        var normalizedCost = cumulativeNormalizedCost >= epoch.StartNormalizedCost
            ? cumulativeNormalizedCost - epoch.StartNormalizedCost
            : cumulativeNormalizedCost;

        var decision = contextBudget.Evaluate(new SessionContextBudgetInput(
            age,
            turnCount,
            normalizedCost));
        if (decision.Recommendation == SessionContextRecommendation.Continue)
        {
            return (session, mainThreadId, ledger);
        }

        var checkpoint = BuildCompactCheckpoint(session, localTurnId, mainThreadId, decision, clock.UtcNow);
        if (decision.Recommendation == SessionContextRecommendation.Compact)
        {
            if (epoch.Compacted)
            {
                return (session, mainThreadId, ledger);
            }
            await runtime.MainAgent.CompactThreadAsync(mainThreadId, cancellationToken);
            contextEpochs[session.Id] = epoch with { Compacted = true };
            return (session, mainThreadId, ledger);
        }

        var rollover = await runtime.MainAgent.RolloverThreadAsync(
            mainThreadId,
            checkpoint,
            snapshot.MainAgent.ModelId,
            snapshot.MainAgent.ReasoningEffort,
            session.WorkingDirectory,
            snapshot.ApprovalMode,
            startFirstTurn: true,
            cancellationToken: cancellationToken);
        if (!string.Equals(rollover.PreviousThreadId, mainThreadId, StringComparison.Ordinal)
            || string.Equals(rollover.NewThreadId, mainThreadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Main thread rollover returned invalid thread provenance.");
        }

        session = session with
        {
            MainThreadId = rollover.NewThreadId,
            UpdatedAt = clock.UtcNow,
        };
        await SaveAndPublishAsync(session, cancellationToken);
        ledger = ledger with
        {
            MainThreadId = rollover.NewThreadId,
            UpdatedAt = clock.UtcNow,
        };
        await usageLedger.UpsertTaskGroupAsync(ledger, cancellationToken);
        contextEpochs[session.Id] = new MainContextEpoch(
            rollover.NewThreadId,
            clock.UtcNow,
            session.Turns.Count,
            cumulativeNormalizedCost,
            false);

        if (rollover.FirstTurn is null
            || !string.Equals(rollover.FirstTurn.ThreadId, rollover.NewThreadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Main thread rollover did not return a valid checkpoint replay turn.");
        }
        var replay = await runtime.MainAgent.WaitForTurnAsync(
            rollover.NewThreadId,
            rollover.FirstTurn.TurnId,
            cancellationToken);
        if (replay.Status is not ControlledTaskStatus.Completed)
        {
            throw new InvalidOperationException($"Context checkpoint replay failed: {replay.ErrorMessage ?? replay.Status.ToString()}");
        }

        return (session, rollover.NewThreadId, ledger);
    }

    private static CompactCheckpoint BuildCompactCheckpoint(
        ControlledTaskSession session,
        string localTurnId,
        string sourceThreadId,
        SessionContextBudgetDecision decision,
        DateTimeOffset timestamp)
    {
        var completed = session.Turns
            .Where(turn => turn.Status is ControlledTaskStatus.Completed)
            .Select(turn => $"{turn.Id}: {turn.UserInput}")
            .ToArray();
        var remaining = session.Turns
            .Where(turn => turn.Status is not ControlledTaskStatus.Completed)
            .Select(turn => $"{turn.Id}: {turn.UserInput}")
            .ToArray();
        var stableInterfaces = new[]
        {
            $"main-model: {session.MainModelId}",
            $"reasoning-effort: {session.MainReasoningEffort}",
        };
        var necessaryFiles = new[] { session.WorkingDirectory };
        var testStatus = $"controlled-task-status: {session.Status}; budget: {decision.Recommendation}";
        var nextPhaseEntry = $"resume pending Main work for turn {localTurnId}";
        return new CompactCheckpoint(
            completed,
            remaining,
            stableInterfaces,
            necessaryFiles,
            testStatus,
            nextPhaseEntry,
            sourceThreadId,
            session.Id,
            timestamp);
    }

    private sealed record MainContextEpoch(
        string ThreadId,
        DateTimeOffset StartedAt,
        int StartTurnCount,
        decimal StartNormalizedCost,
        bool Compacted);

    private async Task<(DelegationDecision Decision, TaskGroupLedger Ledger)> ConfirmDelegationWithSolAsync(
        ControlledTaskSession session,
        string localTurnId,
        string mainThreadId,
        TaskProfileSnapshot snapshot,
        TaskGroupLedger ledger,
        CancellationToken cancellationToken)
    {
        await UpdateStatusAsync(session.Id, localTurnId, ControlledTaskStatus.MainAgentRunning, null, cancellationToken);
        var userInput = session.Turns.Single(turn => turn.Id == localTurnId).UserInput;
        var prompt = $"""
            你是当前受控对话的主代理，只负责本轮的结构化委派判断，不要执行用户任务。

            Agent Switch 宿主负责创建和调用 Worker。若当前方案使用外部 Provider，宿主会自行通过 Provider API 调用它；你不需要、也不能在原生 Codex 模型目录中查找外部模型。不要讨论模型目录、API 可用性或“无法直接调用 DeepSeek”。

            路由模式：{snapshot.WorkerPolicy.RoutingMode}
            用户任务：{userInput}

            只输出一个合法 JSON 对象，不要 Markdown、解释或工具调用：
            JSON 字段必须为 decision（delegate 或 solo）、delegatedScope、deliverable、acceptanceCriteria（字符串数组）。solo 时后面三个字段使用空字符串或空数组。
            """;
        var handle = await runtime.MainAgent.StartTurnAsync(
            mainThreadId,
            prompt,
            snapshot.MainAgent.ModelId,
            snapshot.MainAgent.ReasoningEffort,
            session.WorkingDirectory,
            snapshot.ApprovalMode,
            cancellationToken);
        var result = await runtime.MainAgent.WaitForTurnAsync(mainThreadId, handle.TurnId, cancellationToken);
        if (result.Status != ControlledTaskStatus.Completed)
        {
            throw new InvalidOperationException($"主代理委派判定失败：{result.ErrorMessage ?? result.Status.ToString()}");
        }

        var routing = ParseDelegationDecision(result.FinalText);
        var delegated = string.Equals(routing.Decision, "delegate", StringComparison.OrdinalIgnoreCase);
        var decision = new DelegationDecision(
            delegated ? DelegationDecisionKind.InvokeWorker : DelegationDecisionKind.Skip,
            delegated ? "主代理已返回结构化委派决定。" : "主代理已返回单代理决定。",
            false,
            delegated ? snapshot.Provider?.Id ?? "native-codex" : null,
            delegated ? snapshot.Provider?.ModelId ?? NativeWorkerModel(snapshot.WorkerPolicy) : null,
            clock.UtcNow,
            delegated ? routing.DelegatedScope : null,
            delegated ? routing.Deliverable : null,
            delegated ? routing.AcceptanceCriteria : null);
        await AppendTraceAsync(
            session.Id,
            localTurnId,
            delegated
                ? $"委派决策 · 调用 Worker · 范围：{routing.DelegatedScope}"
                : "委派决策 · 不调用 Worker",
            TaskMessageKind.ToolCall,
            "委派决策",
            cancellationToken);
        var usage = usageCollector.Capture(
            session.Id,
            $"main-decision:{handle.TurnId}",
            new WorkerResult(localTurnId, ToWorkerStatus(result.Status), result.FinalText, result.RawTurn, [], []),
            new WorkerUsageContext("native-codex", snapshot.MainAgent.ModelId, snapshot.Budget.Currency, null));
        await usageLedger.AppendUsageAsync(usage, cancellationToken);
        return (decision, ledger);
    }

    private static StructuredDelegationDecision ParseDelegationDecision(string? text)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("主代理没有返回结构化委派决定；本轮未调用 Worker。");
        }

        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = normalized.IndexOf('\n');
            normalized = firstLineEnd >= 0 ? normalized[(firstLineEnd + 1)..] : normalized;
            var closingFence = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                normalized = normalized[..closingFence];
            }
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            var decision = root.TryGetProperty("decision", out var decisionElement)
                ? decisionElement.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (decision is not ("delegate" or "solo"))
            {
                throw new InvalidOperationException("字段 decision 必须为 delegate 或 solo。");
            }

            var scope = ReadRequiredString(root, "delegatedScope", decision == "delegate");
            var deliverable = ReadRequiredString(root, "deliverable", decision == "delegate");
            var criteria = root.TryGetProperty("acceptanceCriteria", out var criteriaElement)
                           && criteriaElement.ValueKind == JsonValueKind.Array
                ? criteriaElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray()
                : Array.Empty<string>();
            if (decision == "delegate" && criteria.Length == 0)
            {
                throw new InvalidOperationException("delegate 决定必须提供至少一条 acceptanceCriteria。");
            }

            return new StructuredDelegationDecision(decision, scope, deliverable, criteria);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("主代理委派判定不是合法 JSON；本轮未调用 Worker。", exception);
        }
        catch (InvalidOperationException exception) when (!exception.Message.StartsWith("主代理", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"主代理委派判定无效：{exception.Message} 本轮未调用 Worker。", exception);
        }
    }

    private static string ReadRequiredString(JsonElement root, string property, bool required)
    {
        var value = root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"字段 {property} 不能为空。");
        }

        return value ?? string.Empty;
    }

    private async Task<(string? Summary, TaskGroupLedger Ledger, bool Succeeded)> ExecuteWorkerAsync(
        ControlledTaskSession session,
        string localTurnId,
        TaskProfileSnapshot snapshot,
        TaskGroupLedger ledger,
        bool forceNative,
        DelegationDecision? delegation,
        CancellationToken cancellationToken)
    {
        var workerTask = new WorkerTask(
            session.Id,
            $"{session.Id}-W1",
            delegation?.Deliverable is { Length: > 0 }
                ? delegation.Deliverable
                : "为主代理提供独立、可审查的工作结果",
            BuildWorkerPrompt(session.Turns.Single(turn => turn.Id == localTurnId).UserInput, delegation),
            session.WorkingDirectory,
            snapshot.Provider?.ModelId ?? "pending-resolution",
            "medium",
            new WorkerScope([session.WorkingDirectory], [], [ScopeOperation.Read, ScopeOperation.Search]),
            delegation?.AcceptanceCriteria is { Count: > 0 }
                ? delegation.AcceptanceCriteria
                : ["简明工作结果", "风险和未决项"],
            ["结果与用户任务直接相关", "不冒充主代理最终回答"],
            ["需要扩大权限或修改范围时停止"],
            null,
            snapshot.ApprovalMode)
        {
            AllowedReadScope = [session.WorkingDirectory],
            AllowedWriteScope = [],
            ExternalWorkerPermission = snapshot.ExternalWorkerPermission,
            BudgetSnapshot = snapshot.WorkerPolicy.Source == WorkerSource.ExternalProvider ? snapshot.Budget : null,
        };

        var execution = await workerOrchestrator.ExecuteAsync(
            snapshot,
            workerTask,
            forceNative,
            async activity =>
            {
                var job = activity.Job;
            var run = new ControlledWorkerRun(
                job.JobId,
                job.ThreadId,
                job.TurnId,
                job.AdapterId,
                activity.ModelId,
                activity.ReasoningEffort,
                job.Status,
                job.StartedAt,
                null,
                null,
                job.StatusMessage,
                activity.ProviderId,
                activity.ProviderName);
                await AddWorkerAsync(session.Id, localTurnId, run, cancellationToken);
                await UpdateStatusAsync(session.Id, localTurnId, ControlledTaskStatus.WorkerRunning, null, cancellationToken);

            var workerLedger = new WorkerLedgerEntry(
                job.JobId,
                job.ThreadId,
                job.AdapterId,
                activity.ModelId,
                activity.ReasoningEffort,
                job.Status,
                job.StartedAt,
                null,
                AdoptionStatus.Pending,
                "为主代理提供独立分析结果",
                "主代理跳过相同的重复初步分析",
                null,
                false,
                null,
                null);
                ledger = ledger with { Workers = ledger.Workers.Append(workerLedger).ToArray(), UpdatedAt = clock.UtcNow };
                await usageLedger.UpsertTaskGroupAsync(ledger, cancellationToken);
            },
            cancellationToken);

        var result = execution.Result;
        var finalJob = execution.FinalJob;
        await CompleteWorkerAsync(session.Id, localTurnId, finalJob, result, cancellationToken);
        var usageSnapshot = usageCollector.Capture(
                session.Id,
                finalJob.JobId,
                result,
                new WorkerUsageContext(execution.ProviderId, execution.ModelId, snapshot.Budget.Currency, execution.Pricing));
        await usageLedger.AppendUsageAsync(usageSnapshot, cancellationToken);
        ledger = ledger with
        {
            Workers = ledger.Workers.Select(worker => worker.JobId == finalJob.JobId
                    ? worker with
                    {
                        Status = finalJob.Status,
                        CompletedAt = finalJob.CompletedAt,
                        AdoptionStatus = result.Status == WorkerJobStatus.Completed ? AdoptionStatus.Adopted : AdoptionStatus.Rejected,
                        ActualSkippedWork = result.Status == WorkerJobStatus.Completed ? "主代理使用 Worker 结果进行最终审查" : null,
                        ResultSummary = result.Summary,
                    }
                    : worker).ToArray(),
            UpdatedAt = clock.UtcNow,
        };
        await usageLedger.UpsertTaskGroupAsync(ledger, cancellationToken);

        var succeeded = result.Status == WorkerJobStatus.Completed;
        return (succeeded ? result.Summary : $"Worker 未成功完成：{result.Summary}", ledger, succeeded);
    }

    private async Task<TaskGroupLedger> EnsureLedgerAsync(ControlledTaskSession session, CancellationToken cancellationToken)
    {
        var existing = await usageLedger.GetTaskGroupAsync(session.Id, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var ledger = new TaskGroupLedger(
            session.Id,
            session.MainThreadId ?? string.Empty,
            session.MainModelId,
            session.MainReasoningEffort,
            session.CreatedAt,
            null,
            [],
            clock.UtcNow);
        await usageLedger.UpsertTaskGroupAsync(ledger, cancellationToken);
        return ledger;
    }

    private async Task SetServerTurnAsync(string taskId, string localTurnId, string serverTurnId, CancellationToken cancellationToken)
    {
        await MutateAsync(taskId, session => session with
        {
            Turns = session.Turns.Select(turn => turn.Id == localTurnId ? turn with { ServerTurnId = serverTurnId } : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);
    }

    private async Task AddWorkerAsync(string taskId, string localTurnId, ControlledWorkerRun worker, CancellationToken cancellationToken)
    {
        await MutateAsync(taskId, session => session with
        {
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with { Workers = turn.Workers.Append(worker).ToArray() }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);
    }

    private async Task CompleteWorkerAsync(
        string taskId,
        string localTurnId,
        WorkerJob job,
        WorkerResult result,
        CancellationToken cancellationToken)
    {
        await MutateAsync(taskId, session => session with
        {
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with
                {
                    Workers = turn.Workers.Select(worker => worker.JobId == job.JobId
                        ? worker with
                        {
                            Status = job.Status,
                            CompletedAt = job.CompletedAt,
                            ResultSummary = result.Summary,
                            StatusMessage = job.StatusMessage,
                            ProviderId = result.ProviderId ?? worker.ProviderId,
                            ProviderName = result.ProviderName ?? worker.ProviderName,
                            RequestUri = result.RequestUri?.AbsoluteUri,
                            ResponseModelId = result.ResponseModelId,
                            Usage = result.Usage,
                            FailureKind = result.FailureKind,
                            ProviderTurnsUsed = result.ProviderTurns,
                            ToolCallsUsed = result.ToolCalls,
                            LeaseExtensionCount = result.LeaseExtensionCount,
                            HardLimitReason = result.HardLimitReason,
                            ConfiguredTaskBudgetSnapshot = result.BudgetSnapshot,
                            CostVerified = result.CostVerified,
                            FinalizationAttempted = result.FinalizationAttempted,
                            FinalizationSucceeded = result.FinalizationSucceeded,
                            ChangedFiles = result.ChangedFiles,
                        }
                        : worker).ToArray(),
                    Messages = string.IsNullOrWhiteSpace(result.Summary)
                        ? turn.Messages
                        : turn.Messages.Append(new ControlledTaskMessage(
                            Guid.NewGuid(),
                            localTurnId,
                            TaskMessageActor.Worker,
                            result.Summary,
                            clock.UtcNow,
                            true,
                            job.JobId,
                            TaskMessageKind.WorkerProgress,
                            true,
                            result.ProviderId is null
                                ? null
                                : $"Provider={result.ProviderName ?? result.ProviderId}; Model={result.ResponseModelId ?? workerModel(turn, job.JobId)}; Endpoint={result.RequestUri}"))
                            .ToArray(),
                }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);

        static string? workerModel(ControlledTaskTurn turn, string jobId) =>
            turn.Workers.FirstOrDefault(worker => worker.JobId == jobId)?.ModelId;
    }

    private async Task AppendMainOutputAsync(
        string taskId,
        string localTurnId,
        string text,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        await MutateAsync(taskId, session => session with
        {
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with { Messages = UpsertMainMessage(turn.Messages, localTurnId, text, isFinal, clock.UtcNow) }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);
    }

    private Task AppendTraceAsync(
        string taskId,
        string localTurnId,
        string text,
        TaskMessageKind kind,
        string? metadata,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, session => session with
        {
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with
                {
                    Messages = turn.Messages.Append(new ControlledTaskMessage(
                        Guid.NewGuid(),
                        localTurnId,
                        TaskMessageActor.System,
                        text,
                        clock.UtcNow,
                        true,
                        null,
                        kind,
                        true,
                        metadata)).ToArray(),
                }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);

    private async Task CompleteMainTurnAsync(
        string taskId,
        string localTurnId,
        MainAgentTurnResult result,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await MutateAsync(taskId, session => session with
        {
            Status = result.Status,
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with
                {
                    ServerTurnId = result.TurnId,
                    Status = result.Status,
                    Messages = string.IsNullOrWhiteSpace(result.FinalText)
                        ? turn.Messages
                        : ReplaceMainMessage(turn.Messages, localTurnId, result.FinalText, now),
                    CompletedAt = now,
                    ErrorMessage = result.ErrorMessage,
                }
                : turn).ToArray(),
            UpdatedAt = now,
            CompletedAt = result.Status == ControlledTaskStatus.Completed ? now : null,
            ErrorMessage = result.ErrorMessage,
        }, cancellationToken);
    }

    private Task CompleteWorkerOnlyTurnAsync(
        string taskId,
        string localTurnId,
        bool succeeded,
        string? summary,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, session =>
        {
            var now = clock.UtcNow;
            var status = succeeded ? ControlledTaskStatus.Completed : ControlledTaskStatus.Failed;
            var error = succeeded ? null : summary ?? "Worker 测试失败。";
            return session with
            {
                Status = status,
                Turns = session.Turns.Select(turn => turn.Id == localTurnId
                    ? turn with
                    {
                        Status = status,
                        CompletedAt = now,
                        ErrorMessage = error,
                    }
                    : turn).ToArray(),
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = error,
            };
        }, cancellationToken);

    private async Task UpdateStatusAsync(
        string taskId,
        string localTurnId,
        ControlledTaskStatus status,
        string? message,
        CancellationToken cancellationToken)
    {
        await MutateAsync(taskId, session => session with
        {
            Status = status,
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with { Status = status, ErrorMessage = status == ControlledTaskStatus.Failed ? message : turn.ErrorMessage }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
            ErrorMessage = status == ControlledTaskStatus.Failed ? message : session.ErrorMessage,
        }, cancellationToken);
    }

    private Task SetDelegationAsync(
        string taskId,
        string localTurnId,
        DelegationDecision decision,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, session => session with
        {
            Turns = session.Turns.Select(turn => turn.Id == localTurnId
                ? turn with { Delegation = decision }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);

    private Task FailAsync(
        string taskId,
        string localTurnId,
        ControlledTaskStatus status,
        string error,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, session =>
        {
            var now = clock.UtcNow;
            return session with
            {
                Status = status,
                Turns = session.Turns.Select(turn => turn.Id == localTurnId
                    ? turn with { Status = status, CompletedAt = now, ErrorMessage = error }
                    : turn).ToArray(),
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = error,
            };
        }, cancellationToken);

    private Task MarkRecoveryRequiredAsync(ControlledTaskSession session, string error, CancellationToken cancellationToken) =>
        MutateAsync(session.Id, current => current with
        {
            Status = ControlledTaskStatus.UnknownRecoverable,
            UpdatedAt = clock.UtcNow,
            ErrorMessage = error,
        }, cancellationToken);

    private async Task MutateAsync(
        string taskId,
        Func<ControlledTaskSession, ControlledTaskSession> mutation,
        CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            var current = await RequireAsync(taskId, cancellationToken);
            await SaveAndPublishAsync(mutation(current), cancellationToken);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task SaveAndPublishAsync(ControlledTaskSession session, CancellationToken cancellationToken)
    {
        await tasks.UpsertAsync(session, cancellationToken);
        if (TaskChanged is not null)
        {
            await TaskChanged.Invoke(session);
        }
    }

    private async Task<ControlledTaskSession> RequireAsync(string id, CancellationToken cancellationToken) =>
        await tasks.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"任务 {id} 不存在。");

    private static ControlledTaskTurn NewTurn(
        string id,
        string input,
        DateTimeOffset now,
        TaskProfileSnapshot snapshot,
        DelegationDecision decision) => new(
        id,
        null,
        input.Trim(),
        ControlledTaskStatus.Queued,
        [],
        [new ControlledTaskMessage(Guid.NewGuid(), id, TaskMessageActor.User, input.Trim(), now, true)],
        now,
        null,
        null,
        snapshot,
        decision);

    private static IReadOnlyList<ControlledTaskMessage> UpsertMainMessage(
        IReadOnlyList<ControlledTaskMessage> messages,
        string turnId,
        string delta,
        bool isFinal,
        DateTimeOffset now)
    {
        var existing = messages.LastOrDefault(message => message.Actor == TaskMessageActor.MainAgent);
        if (existing is null)
        {
            return messages.Append(new ControlledTaskMessage(Guid.NewGuid(), turnId, TaskMessageActor.MainAgent, delta, now, isFinal)).ToArray();
        }

        return messages.Select(message => message.Id == existing.Id
            ? message with { Content = message.Content + delta, IsFinal = isFinal }
            : message).ToArray();
    }

    private static IReadOnlyList<ControlledTaskMessage> ReplaceMainMessage(
        IReadOnlyList<ControlledTaskMessage> messages,
        string turnId,
        string finalText,
        DateTimeOffset now)
    {
        var existing = messages.LastOrDefault(message => message.Actor == TaskMessageActor.MainAgent);
        if (existing is null)
        {
            return messages.Append(new ControlledTaskMessage(Guid.NewGuid(), turnId, TaskMessageActor.MainAgent, finalText, now, true)).ToArray();
        }

        return messages.Select(message => message.Id == existing.Id
            ? message with { Content = finalText, IsFinal = true }
            : message).ToArray();
    }

    private static bool IsRunning(ControlledTaskStatus status) => status is
        ControlledTaskStatus.Queued or
        ControlledTaskStatus.WorkerRunning or
        ControlledTaskStatus.MainAgentRunning or
        ControlledTaskStatus.WaitingForApproval;

    private static string CreateTitle(string input)
    {
        var normalized = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 48 ? normalized : normalized[..48] + "…";
    }

    private static string BuildWorkerPrompt(string input, DelegationDecision? delegation) => $"""
        The Agent Switch host directly owns this Worker execution. Do not inspect a native model catalog or describe external-provider availability to the end user.
        Delegated scope: {delegation?.DelegatedScope ?? "verify the current Worker and model availability"}
        Expected deliverable: {delegation?.Deliverable ?? "a concise, verifiable Worker result"}
        Acceptance criteria: {string.Join("; ", delegation?.AcceptanceCriteria ?? ["result is concise and verifiable"])}

        你是受控工作代理。请对下面的用户任务进行一次独立、简明、可核验的分析，输出可供主代理最终审查的工作结果。
        不要声称自己是最终回答者，不要扩大文件或权限范围；如果任务需要修改、执行或网络权限，只列出建议和风险并停止。

        用户任务：
        {input}
        """;

    private static string BuildMainPrompt(string input, TaskProfileSnapshot snapshot, string? workerSummary)
    {
        var workerSection = string.IsNullOrWhiteSpace(workerSummary)
            ? "本轮没有工作代理结果。请由主代理独立完成。"
            : $"""
              以下是工作代理的真实返回。它只是参考材料；你必须独立核验、承担最终责任，不得把未经核验的内容直接冒充结论：

              <worker_result>
              {workerSummary}
              </worker_result>
              """;
        return $"""
            Agent Switch host owns every external Worker call and has already injected any worker_result below into this same Thread. Do not discuss the native model catalog or claim that the system cannot delegate merely because you cannot call an external API yourself, unless the user explicitly asks for diagnostics.
            你是 Codex Agent Switch 当前方案“{snapshot.ProfileName}”指定的主代理。请直接完成用户任务并给出最终结果。
            {workerSection}

            用户任务：
            {input}
            """;
    }

    private static WorkerJobStatus ToWorkerStatus(ControlledTaskStatus status) => status switch
    {
        ControlledTaskStatus.Completed => WorkerJobStatus.Completed,
        ControlledTaskStatus.Failed => WorkerJobStatus.Failed,
        ControlledTaskStatus.Interrupted => WorkerJobStatus.Interrupted,
        ControlledTaskStatus.UnknownRecoverable => WorkerJobStatus.UnknownRecoverable,
        _ => WorkerJobStatus.Running,
    };

    private static string NativeWorkerModel(WorkerPolicy policy) => policy.PreferredProviderId switch
    {
        "native-sol" => "gpt-5.6-sol",
        "native-terra" => "gpt-5.6-terra",
        "native-luna" => "gpt-5.6-luna",
        _ => throw new InvalidOperationException($"不支持的原生 Worker：{policy.PreferredProviderId}。"),
    };

    private sealed record StructuredDelegationDecision(
        string Decision,
        string DelegatedScope,
        string Deliverable,
        IReadOnlyList<string> AcceptanceCriteria);

}
