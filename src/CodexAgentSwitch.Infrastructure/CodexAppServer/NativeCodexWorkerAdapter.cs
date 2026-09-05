using System.Collections.Concurrent;
using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Workers;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class NativeCodexWorkerAdapter : IWorkerAdapter
{
    private static readonly IReadOnlySet<WorkerToolCapability> NativeToolCapabilities = Enum
        .GetValues<WorkerToolCapability>()
        .ToHashSet();
    private readonly CodexAppServerClient _client;
    private readonly IClock _clock;
    private readonly ICodexModelResolver _modelResolver;
    private readonly ConcurrentDictionary<string, JobRuntime> _jobs = new(StringComparer.Ordinal);

    public NativeCodexWorkerAdapter(CodexAppServerClient client, IClock clock, ICodexModelResolver? modelResolver = null)
    {
        _client = client;
        _clock = clock;
        _modelResolver = modelResolver ?? new CodexModelResolver();
        _client.NotificationReceived += OnNotificationAsync;
        _client.ServerRequestReceived += OnServerRequestAsync;
    }

    public string AdapterId => "native-codex";

    public IReadOnlySet<WorkerToolCapability> ToolCapabilities => NativeToolCapabilities;

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.RequestAsync(
            "model/list",
            new { cursor = (string?)null, limit = 100, includeHidden = false },
            cancellationToken);
        var models = new List<WorkerModelCapability>();
        if (result.TryGetProperty("data", out var data))
        {
            foreach (var model in data.EnumerateArray())
            {
                var efforts = model.GetProperty("supportedReasoningEfforts")
                    .EnumerateArray()
                    .Select(option => option.GetProperty("reasoningEffort").GetString() ?? string.Empty)
                    .Where(effort => effort.Length > 0)
                    .ToList();
                models.Add(new WorkerModelCapability(
                    model.GetProperty("id").GetString() ?? model.GetProperty("model").GetString() ?? string.Empty,
                    model.GetProperty("displayName").GetString() ?? string.Empty,
                    efforts,
                    model.GetProperty("defaultReasoningEffort").GetString() ?? efforts.FirstOrDefault() ?? "medium",
                    model.GetProperty("isDefault").GetBoolean()));
            }
        }

        return new WorkerCapabilities(AdapterId, true, models, 3, [])
        {
            ToolCapabilities = ToolCapabilities,
        };
    }

    public async Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default)
    {
        if (_jobs.Values.Count(runtime => !IsTerminal(runtime.Job.Status)) >= 3)
        {
            throw new InvalidOperationException("Native Worker concurrency limit of 3 has been reached.");
        }

        var model = await _modelResolver.ResolveAsync(_client, task.ModelId, task.ReasoningEffort, cancellationToken);
        var resolvedTask = task with { ModelId = model.ModelId };
        var sandbox = resolvedTask.ApprovalMode switch
        {
            ExecutionApprovalMode.Safe => "read-only",
            ExecutionApprovalMode.FullAuto => "danger-full-access",
            _ => resolvedTask.Scope.Operations.Any(operation => operation is ScopeOperation.Modify or ScopeOperation.Execute or ScopeOperation.Test)
                ? "workspace-write"
                : "read-only",
        };
        var approvalPolicy = ExecutionApprovalPolicy.Resolve(resolvedTask.ApprovalMode).ApprovalPolicy;
        var threadResponse = await _client.RequestAsync(
            "thread/start",
            new
            {
                model = resolvedTask.ModelId,
                cwd = resolvedTask.WorkingDirectory,
                approvalPolicy,
                sandbox,
                ephemeral = false,
            },
            cancellationToken);
        var threadId = threadResponse.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidDataException("thread/start did not return a thread id.");
        var textInput = new { type = "text", text = resolvedTask.Prompt, text_elements = Array.Empty<object>() };
        var turnResponse = await _client.RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = new[] { textInput },
                effort = resolvedTask.ReasoningEffort,
                outputSchema = resolvedTask.OutputSchema,
            },
            cancellationToken);
        var turnId = turnResponse.GetProperty("turn").GetProperty("id").GetString()
            ?? throw new InvalidDataException("turn/start did not return a turn id.");
        var jobId = Guid.NewGuid().ToString("D");
        var job = new WorkerJob(AdapterId, jobId, threadId, turnId, resolvedTask.TaskId, WorkerJobStatus.Running, _clock.UtcNow, null, null);
        if (!_jobs.TryAdd(jobId, new JobRuntime(resolvedTask, job)))
        {
            throw new InvalidOperationException("Unable to register Worker job.");
        }

        return job;
    }

    public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Runtime(jobId).Job);
    }

    public async Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        if (wait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(wait));
        }

        var runtime = Runtime(jobId);
        if (runtime.Result is not null)
        {
            return runtime.Result;
        }

        try
        {
            return await runtime.Completion.Task.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default)
    {
        var runtime = Runtime(jobId);
        switch (request.Kind)
        {
            case WorkerSteerKind.ContinueWaiting:
                return;
            case WorkerSteerKind.AddInstruction:
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
                await _client.RequestAsync(
                    "turn/steer",
                    new
                    {
                        threadId = runtime.Job.ThreadId,
                        expectedTurnId = runtime.Job.TurnId,
                        input = new[] { new { type = "text", text = request.Message, text_elements = Array.Empty<object>() } },
                    },
                    cancellationToken);
                return;
            case WorkerSteerKind.Approve:
            case WorkerSteerKind.Decline:
                if (runtime.PendingRequestId is null || runtime.PendingRequestMethod is null)
                {
                    throw new InvalidOperationException("The job has no pending approval request.");
                }

                if (!runtime.PendingRequestMethod.Contains("requestApproval", StringComparison.Ordinal))
                {
                    throw new NotSupportedException($"Server request {runtime.PendingRequestMethod} requires a specialized response.");
                }

                await _client.RespondAsync(
                    runtime.PendingRequestId.Value,
                    new { decision = request.Kind == WorkerSteerKind.Approve ? "accept" : "decline" },
                    cancellationToken);
                runtime.PendingRequestId = null;
                runtime.PendingRequestMethod = null;
                runtime.Job = runtime.Job with { Status = WorkerJobStatus.Running, StatusMessage = null };
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var runtime = Runtime(jobId);
        if (IsTerminal(runtime.Job.Status))
        {
            return;
        }

        await _client.RequestAsync(
            "turn/interrupt",
            new { threadId = runtime.Job.ThreadId, turnId = runtime.Job.TurnId },
            cancellationToken);
        runtime.Job = runtime.Job with { Status = WorkerJobStatus.Interrupted, CompletedAt = _clock.UtcNow, StatusMessage = "Interrupted by user." };
        Complete(runtime, new WorkerResult(runtime.Task.TaskId, WorkerJobStatus.Interrupted, "Interrupted by user.", null, [], []));
    }

    public async Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var runtime = Runtime(jobId);
        if (!IsTerminal(runtime.Job.Status))
        {
            throw new InvalidOperationException("A running Worker Thread cannot be deleted.");
        }

        await _client.RequestAsync(
            "thread/read",
            new { threadId = runtime.Job.ThreadId, includeTurns = true },
            cancellationToken);
        await _client.RequestAsync("thread/delete", new { threadId = runtime.Job.ThreadId }, cancellationToken);
        runtime.Job = runtime.Job with { Status = WorkerJobStatus.Deleted, StatusMessage = "Thread deleted after final read." };
    }

    private Task OnNotificationAsync(string method, JsonElement parameters)
    {
        if (method != "turn/completed" || !parameters.TryGetProperty("turn", out var turn))
        {
            return Task.CompletedTask;
        }

        var turnId = turn.GetProperty("id").GetString();
        var runtime = _jobs.Values.SingleOrDefault(value => value.Job.TurnId == turnId);
        if (runtime is null)
        {
            return Task.CompletedTask;
        }

        // Notifications are delivered by the JSON-RPC reader loop. Reading the final
        // Thread on that same callback would block the loop that must receive the
        // thread/read response, so terminal processing runs independently.
        _ = Task.Run(() => CompleteFromTerminalNotificationAsync(runtime, turn.Clone()));
        return Task.CompletedTask;
    }

    private async Task CompleteFromTerminalNotificationAsync(JobRuntime runtime, JsonElement turn)
    {
        try
        {
            var status = turn.GetProperty("status").GetString();
            var mapped = status switch
            {
                "completed" => WorkerJobStatus.Completed,
                "failed" => WorkerJobStatus.Failed,
                "interrupted" => WorkerJobStatus.Interrupted,
                _ => WorkerJobStatus.UnknownRecoverable,
            };
            var thread = await _client.RequestAsync("thread/read", new { threadId = runtime.Job.ThreadId, includeTurns = true });
            var summary = ExtractLastAgentMessage(thread);
            runtime.Job = runtime.Job with { Status = mapped, CompletedAt = _clock.UtcNow, StatusMessage = status };
            var result = new WorkerResult(runtime.Task.TaskId, mapped, summary, thread.Clone(), [], mapped == WorkerJobStatus.UnknownRecoverable ? ["Turn terminal state was not recognized."] : []);
            Complete(runtime, result);
        }
        catch (Exception exception)
        {
            runtime.Job = runtime.Job with
            {
                Status = WorkerJobStatus.UnknownRecoverable,
                CompletedAt = _clock.UtcNow,
                StatusMessage = "Terminal state received, but the final Thread could not be read.",
            };
            Complete(
                runtime,
                new WorkerResult(
                    runtime.Task.TaskId,
                    WorkerJobStatus.UnknownRecoverable,
                    "Terminal state received, but the final Thread could not be read.",
                    null,
                    [],
                    [exception.Message]));
        }
    }

    private Task OnServerRequestAsync(string method, JsonElement requestId, JsonElement parameters)
    {
        var threadId = parameters.TryGetProperty("threadId", out var threadElement) ? threadElement.GetString() : null;
        var runtime = _jobs.Values.SingleOrDefault(value => value.Job.ThreadId == threadId);
        if (runtime is not null)
        {
            runtime.PendingRequestId = requestId.Clone();
            runtime.PendingRequestMethod = method;
            runtime.Job = runtime.Job with { Status = WorkerJobStatus.WaitingForApproval, StatusMessage = method };
        }

        return Task.CompletedTask;
    }

    private static string? ExtractLastAgentMessage(JsonElement threadReadResponse)
    {
        if (!threadReadResponse.TryGetProperty("thread", out var thread)
            || !thread.TryGetProperty("turns", out var turns))
        {
            return null;
        }

        foreach (var turn in turns.EnumerateArray().Reverse())
        {
            if (!turn.TryGetProperty("items", out var items))
            {
                continue;
            }

            foreach (var item in items.EnumerateArray().Reverse())
            {
                if (item.TryGetProperty("type", out var type)
                    && type.GetString() == "agentMessage"
                    && item.TryGetProperty("text", out var text))
                {
                    return text.GetString();
                }
            }
        }

        return null;
    }

    private static void Complete(JobRuntime runtime, WorkerResult? result)
    {
        runtime.Result = result;
        runtime.Completion.TrySetResult(result);
    }

    private JobRuntime Runtime(string jobId) =>
        _jobs.TryGetValue(jobId, out var runtime)
            ? runtime
            : throw new KeyNotFoundException($"Worker job {jobId} is not registered by this adapter.");

    private static bool IsTerminal(WorkerJobStatus status) =>
        status is WorkerJobStatus.Completed or WorkerJobStatus.Failed or WorkerJobStatus.Interrupted or WorkerJobStatus.Deleted;

    private sealed class JobRuntime(WorkerTask task, WorkerJob job)
    {
        public WorkerTask Task { get; } = task;

        public WorkerJob Job { get; set; } = job;

        public WorkerResult? Result { get; set; }

        public TaskCompletionSource<WorkerResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonElement? PendingRequestId { get; set; }

        public string? PendingRequestMethod { get; set; }
    }
}
