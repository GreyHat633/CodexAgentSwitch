using System.Collections.Concurrent;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Profiles;
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
    private readonly IProviderRepository providers;
    private readonly IExternalWorkerAdapterFactory externalWorkers;
    private readonly IControlledTaskRuntime runtime;
    private readonly IUsageLedgerRepository usageLedger;
    private readonly IWorkerUsageCollector usageCollector;
    private readonly IClock clock;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim updateGate = new(1, 1);

    public ControlledTaskService(
        IControlledTaskRepository tasks,
        IProfileRepository profiles,
        IProviderRepository providers,
        IExternalWorkerAdapterFactory externalWorkers,
        IControlledTaskRuntime runtime,
        IUsageLedgerRepository usageLedger,
        IWorkerUsageCollector usageCollector,
        IClock clock)
    {
        this.tasks = tasks;
        this.profiles = profiles;
        this.providers = providers;
        this.externalWorkers = externalWorkers;
        this.runtime = runtime;
        this.usageLedger = usageLedger;
        this.usageCollector = usageCollector;
        this.clock = clock;
    }

    public event Func<ControlledTaskSession, Task>? TaskChanged;

    public Task<IReadOnlyList<ControlledTaskSession>> ListAsync(CancellationToken cancellationToken = default) =>
        tasks.ListAsync(cancellationToken);

    public Task<ControlledTaskSession?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        tasks.GetAsync(id, cancellationToken);

    public async Task<ControlledTaskSession> StartAsync(
        string input,
        string workingDirectory,
        bool? useWorker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory.Trim());
        if (!Directory.Exists(fullWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{fullWorkingDirectory}");
        }

        var profile = await profiles.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("尚未设置当前配置方案。");
        var now = clock.UtcNow;
        var localTurnId = Guid.NewGuid().ToString("D");
        var session = new ControlledTaskSession(
            Guid.NewGuid().ToString("D"),
            profile.Id,
            profile.Name,
            CreateTitle(input),
            fullWorkingDirectory,
            profile.MainAgent.ModelId,
            profile.MainAgent.ReasoningEffort,
            null,
            ControlledTaskStatus.Queued,
            [NewTurn(localTurnId, input, now)],
            now,
            now,
            null,
            null);
        await SaveAndPublishAsync(session, cancellationToken);
        StartBackground(session.Id, localTurnId, profile, ShouldUseWorker(profile, useWorker));
        return session;
    }

    public async Task<ControlledTaskSession> ContinueAsync(
        string taskId,
        string input,
        bool? useWorker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var session = await RequireAsync(taskId, cancellationToken);
        if (active.ContainsKey(taskId) || IsRunning(session.Status))
        {
            throw new InvalidOperationException("该任务仍在运行，请等待完成或先取消。");
        }

        var profile = await profiles.GetAsync(session.ProfileId, cancellationToken)
            ?? await profiles.GetDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("任务对应的配置方案已不存在，且没有默认方案可用。");
        var now = clock.UtcNow;
        var localTurnId = Guid.NewGuid().ToString("D");
        session = session with
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            MainModelId = profile.MainAgent.ModelId,
            MainReasoningEffort = profile.MainAgent.ReasoningEffort,
            Status = ControlledTaskStatus.Queued,
            Turns = session.Turns.Append(NewTurn(localTurnId, input, now)).ToArray(),
            UpdatedAt = now,
            CompletedAt = null,
            ErrorMessage = null,
        };
        await SaveAndPublishAsync(session, cancellationToken);
        StartBackground(session.Id, localTurnId, profile, ShouldUseWorker(profile, useWorker));
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
                await runtime.MainAgent.ResumeThreadAsync(session.MainThreadId, session.MainModelId, session.WorkingDirectory, cancellationToken);
                var result = await runtime.MainAgent.ReadTurnAsync(session.MainThreadId, turn.ServerTurnId, cancellationToken);
                await CompleteMainTurnAsync(session.Id, turn.Id, result, cancellationToken);
            }
            catch (Exception exception)
            {
                await MarkRecoveryRequiredAsync(session, $"恢复 Thread 失败：{exception.Message}", cancellationToken);
            }
        }
    }

    private void StartBackground(string taskId, string localTurnId, Profile profile, bool useWorker)
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
                await ExecuteAsync(taskId, localTurnId, profile, useWorker, cancellation.Token);
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

    private async Task ExecuteAsync(string taskId, string localTurnId, Profile profile, bool useWorker, CancellationToken cancellationToken)
    {
        await runtime.EnsureStartedAsync(cancellationToken);
        var session = await RequireAsync(taskId, cancellationToken);
        var mainThreadId = session.MainThreadId;
        if (mainThreadId is null)
        {
            mainThreadId = await runtime.MainAgent.CreateThreadAsync(profile.MainAgent.ModelId, session.WorkingDirectory, cancellationToken);
            session = session with { MainThreadId = mainThreadId, UpdatedAt = clock.UtcNow };
            await SaveAndPublishAsync(session, cancellationToken);
        }
        else
        {
            await runtime.MainAgent.ResumeThreadAsync(mainThreadId, profile.MainAgent.ModelId, session.WorkingDirectory, cancellationToken);
        }

        var ledger = await EnsureLedgerAsync(session, cancellationToken);
        string? workerSummary = null;
        if (useWorker)
        {
            try
            {
                var workerExecution = await ExecuteWorkerAsync(session, localTurnId, profile, ledger, forceNative: false, cancellationToken);
                workerSummary = workerExecution.Summary;
                ledger = workerExecution.Ledger;
                if (!workerExecution.Succeeded
                    && profile.WorkerPolicy.FallbackAction == FallbackAction.NativeLuna
                    && profile.WorkerPolicy.Source == WorkerSource.ExternalProvider)
                {
                    var fallback = await ExecuteWorkerAsync(session, localTurnId, profile, ledger, forceNative: true, cancellationToken);
                    workerSummary = fallback.Summary;
                    ledger = fallback.Ledger;
                }
                else if (!workerExecution.Succeeded && profile.WorkerPolicy.FallbackAction == FallbackAction.AskUser)
                {
                    throw new InvalidOperationException("工作代理失败；当前方案要求先询问用户，任务已停止在主代理执行前。");
                }
            }
            catch (Exception exception) when (profile.WorkerPolicy.FallbackAction is FallbackAction.SingleAgent or FallbackAction.StopDelegation)
            {
                workerSummary = $"工作代理未能启动或完成，已按当前方案由主代理接管。原因：{exception.Message}";
            }
        }

        await UpdateStatusAsync(taskId, localTurnId, ControlledTaskStatus.MainAgentRunning, null, cancellationToken);
        session = await RequireAsync(taskId, cancellationToken);
        var userInput = session.Turns.Single(turn => turn.Id == localTurnId).UserInput;
        var prompt = BuildMainPrompt(userInput, profile, workerSummary);
        var handle = await runtime.MainAgent.StartTurnAsync(
            mainThreadId,
            prompt,
            profile.MainAgent.ModelId,
            profile.MainAgent.ReasoningEffort,
            session.WorkingDirectory,
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
            new WorkerUsageContext("native-codex", profile.MainAgent.ModelId, profile.Budget.Currency, null));
        await usageLedger.AppendUsageAsync(mainUsage, cancellationToken);
        ledger = ledger with { CompletedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        await usageLedger.UpsertTaskGroupAsync(ledger, cancellationToken);
    }

    private async Task<(string? Summary, TaskGroupLedger Ledger, bool Succeeded)> ExecuteWorkerAsync(
        ControlledTaskSession session,
        string localTurnId,
        Profile profile,
        TaskGroupLedger ledger,
        bool forceNative,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveWorkerAsync(profile, forceNative, cancellationToken);
        try
        {
            var workerTask = new WorkerTask(
            session.Id,
            $"{session.Id}-W1",
            "为主代理提供独立、可审查的工作结果",
            BuildWorkerPrompt(session.Turns.Single(turn => turn.Id == localTurnId).UserInput),
            session.WorkingDirectory,
            resolved.ModelId,
            resolved.ReasoningEffort,
            new WorkerScope([session.WorkingDirectory], [], [ScopeOperation.Read, ScopeOperation.Search]),
            ["简明工作结果", "风险和未决项"],
            ["结果与用户任务直接相关", "不冒充主代理最终回答"],
            ["需要扩大权限或修改范围时停止"]);
            var job = await resolved.Adapter.SpawnAsync(workerTask, cancellationToken);
            var run = new ControlledWorkerRun(
            job.JobId,
            job.ThreadId,
            job.TurnId,
            job.AdapterId,
            resolved.ModelId,
            resolved.ReasoningEffort,
            job.Status,
            job.StartedAt,
            null,
            null,
            job.StatusMessage);
            await AddWorkerAsync(session.Id, localTurnId, run, cancellationToken);
            await UpdateStatusAsync(session.Id, localTurnId, ControlledTaskStatus.WorkerRunning, null, cancellationToken);

            var workerLedger = new WorkerLedgerEntry(
            job.JobId,
            job.ThreadId,
            job.AdapterId,
            resolved.ModelId,
            resolved.ReasoningEffort,
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

            var result = await resolved.Adapter.WaitAsync(job.JobId, TimeSpan.FromHours(2), cancellationToken)
                ?? throw new TimeoutException("Worker 在等待期限内没有返回终态。");
            var finalJob = await resolved.Adapter.ReadStatusAsync(job.JobId, cancellationToken);
            await CompleteWorkerAsync(session.Id, localTurnId, finalJob, result, cancellationToken);
            var snapshot = usageCollector.Capture(
            session.Id,
            job.JobId,
            result,
            new WorkerUsageContext(resolved.ProviderId, resolved.ModelId, profile.Budget.Currency, resolved.Pricing));
            await usageLedger.AppendUsageAsync(snapshot, cancellationToken);
            ledger = ledger with
            {
                Workers = ledger.Workers.Select(worker => worker.JobId == job.JobId
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
        finally
        {
            if (resolved.OwnedAdapter is not null)
            {
                await resolved.OwnedAdapter.DisposeAsync();
            }
        }
    }

    private async Task<ResolvedWorker> ResolveWorkerAsync(Profile profile, bool forceNative, CancellationToken cancellationToken)
    {
        if (forceNative || profile.WorkerPolicy.Source == WorkerSource.NativeCodex)
        {
            var modelId = profile.WorkerPolicy.PreferredProviderId switch
            {
                "native-sol" => "gpt-5.6-sol",
                "native-terra" => "gpt-5.6-terra",
                _ => "gpt-5.6-luna",
            };
            return new ResolvedWorker(runtime.NativeWorker, modelId, "medium", "native-codex", null, null);
        }

        if (profile.WorkerPolicy.Source == WorkerSource.ExternalProvider)
        {
            var providerId = profile.WorkerPolicy.PreferredProviderId
                ?? throw new InvalidOperationException("当前方案没有选择外部 Provider。");
            var provider = await providers.GetAsync(providerId, cancellationToken)
                ?? throw new InvalidOperationException($"Provider {providerId} 不存在。");
            if (!provider.IsEnabled)
            {
                throw new InvalidOperationException($"Provider {provider.Name} 已停用。");
            }

            var adapter = externalWorkers.Create(provider);
            var modelId = provider.ModelId ?? throw new InvalidOperationException($"Provider {provider.Name} 没有选择模型。");
            return new ResolvedWorker(adapter, modelId, "medium", provider.Id, provider.Pricing, adapter as IAsyncDisposable);
        }

        throw new InvalidOperationException("当前方案未启用 Worker。");
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
                            job.JobId)).ToArray(),
                }
                : turn).ToArray(),
            UpdatedAt = clock.UtcNow,
        }, cancellationToken);
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

    private static ControlledTaskTurn NewTurn(string id, string input, DateTimeOffset now) => new(
        id,
        null,
        input.Trim(),
        ControlledTaskStatus.Queued,
        [],
        [new ControlledTaskMessage(Guid.NewGuid(), id, TaskMessageActor.User, input.Trim(), now, true)],
        now,
        null,
        null);

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

    private static bool ShouldUseWorker(Profile profile, bool? requested) =>
        profile.WorkerPolicy.Enabled
        && profile.WorkerPolicy.Source != WorkerSource.Disabled
        && profile.WorkerPolicy.RoutingMode != RoutingMode.Single
        && (profile.WorkerPolicy.RoutingMode != RoutingMode.Manual || requested == true)
        && requested != false;

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

    private static string BuildWorkerPrompt(string input) => $"""
        你是受控工作代理。请对下面的用户任务进行一次独立、简明、可核验的分析，输出可供主代理 Sol 最终审查的工作结果。
        不要声称自己是最终回答者，不要扩大文件或权限范围；如果任务需要修改、执行或网络权限，只列出建议和风险并停止。

        用户任务：
        {input}
        """;

    private static string BuildMainPrompt(string input, Profile profile, string? workerSummary)
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
            你是 Codex Agent Switch 当前方案“{profile.Name}”指定的主代理。请直接完成用户任务并给出最终结果。
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

    private sealed record ResolvedWorker(
        IWorkerAdapter Adapter,
        string ModelId,
        string ReasoningEffort,
        string ProviderId,
        ProviderPricing? Pricing,
        IAsyncDisposable? OwnedAdapter);
}
