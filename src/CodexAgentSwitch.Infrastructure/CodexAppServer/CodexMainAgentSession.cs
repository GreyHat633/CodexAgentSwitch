using System.Collections.Concurrent;
using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class CodexMainAgentSession : IMainAgentSession
{
    private readonly CodexAppServerClient client;
    private readonly ConcurrentDictionary<string, TurnRuntime> turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingApproval> approvals = new(StringComparer.Ordinal);

    public CodexMainAgentSession(CodexAppServerClient client)
    {
        this.client = client;
        client.NotificationReceived += OnNotificationAsync;
        client.ServerRequestReceived += OnServerRequestAsync;
    }

    public event Func<MainAgentEvent, Task>? EventReceived;

    public async Task<string> CreateThreadAsync(string modelId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var response = await client.RequestAsync(
            "thread/start",
            new
            {
                model = modelId,
                cwd = workingDirectory,
                approvalPolicy = "on-request",
                sandbox = "workspace-write",
                ephemeral = false,
                serviceName = "codex-agent-switch",
            },
            cancellationToken);
        return response.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidDataException("thread/start did not return a thread id.");
    }

    public async Task ResumeThreadAsync(string threadId, string modelId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        await client.RequestAsync(
            "thread/resume",
            new
            {
                threadId,
                model = modelId,
                cwd = workingDirectory,
                approvalPolicy = "on-request",
                sandbox = "workspace-write",
            },
            cancellationToken);
    }

    public async Task<MainAgentTurnHandle> StartTurnAsync(
        string threadId,
        string prompt,
        string modelId,
        string reasoningEffort,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var response = await client.RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = new[] { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } },
                cwd = workingDirectory,
                approvalPolicy = "on-request",
                sandboxPolicy = new
                {
                    type = "workspaceWrite",
                    writableRoots = new[] { workingDirectory },
                    networkAccess = false,
                },
                model = modelId,
                effort = reasoningEffort,
                summary = "concise",
            },
            cancellationToken);
        var turnId = response.GetProperty("turn").GetProperty("id").GetString()
            ?? throw new InvalidDataException("turn/start did not return a turn id.");
        turns.TryAdd(Key(threadId, turnId), new TurnRuntime(threadId, turnId));
        return new MainAgentTurnHandle(threadId, turnId);
    }

    public async Task<MainAgentTurnResult> WaitForTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        var runtime = turns.GetOrAdd(Key(threadId, turnId), _ => new TurnRuntime(threadId, turnId));
        await runtime.Completion.Task.WaitAsync(cancellationToken);
        return await ReadTurnAsync(threadId, turnId, cancellationToken);
    }

    public async Task<MainAgentTurnResult> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        var response = await client.RequestAsync("thread/read", new { threadId, includeTurns = true }, cancellationToken);
        if (!response.TryGetProperty("thread", out var thread)
            || !thread.TryGetProperty("turns", out var threadTurns))
        {
            throw new InvalidDataException("thread/read did not return turns.");
        }

        foreach (var turn in threadTurns.EnumerateArray())
        {
            if (!turn.TryGetProperty("id", out var id) || id.GetString() != turnId)
            {
                continue;
            }

            var statusText = turn.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            var status = statusText switch
            {
                "completed" => ControlledTaskStatus.Completed,
                "failed" => ControlledTaskStatus.Failed,
                "interrupted" => ControlledTaskStatus.Interrupted,
                "inProgress" or "running" => ControlledTaskStatus.MainAgentRunning,
                _ => ControlledTaskStatus.UnknownRecoverable,
            };
            var error = turn.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.Object
                && errorElement.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
            return new MainAgentTurnResult(threadId, turnId, status, ExtractAgentText(turn), error, turn.Clone());
        }

        throw new KeyNotFoundException($"Turn {turnId} was not found in Thread {threadId}.");
    }

    public async Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default)
    {
        await client.RequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken);
    }

    public async Task RespondToApprovalAsync(
        string threadId,
        string turnId,
        bool approve,
        CancellationToken cancellationToken = default)
    {
        if (!approvals.TryRemove(Key(threadId, turnId), out var pending))
        {
            throw new InvalidOperationException("该任务没有待处理的审批请求。");
        }

        await client.RespondAsync(
            pending.RequestId,
            pending.Method == "item/permissions/requestApproval"
                ? new { permissions = approve ? pending.Parameters.TryGetProperty("permissions", out var value) ? value : JsonSerializer.SerializeToElement(Array.Empty<object>()) : JsonSerializer.SerializeToElement(Array.Empty<object>()) }
                : new { decision = approve ? "accept" : "decline" },
            cancellationToken);
    }

    private async Task OnNotificationAsync(string method, JsonElement parameters)
    {
        var threadId = parameters.TryGetProperty("threadId", out var threadElement) ? threadElement.GetString() : null;
        var turn = parameters.TryGetProperty("turn", out var turnElement) ? turnElement : default;
        var turnId = parameters.TryGetProperty("turnId", out var turnIdElement)
            ? turnIdElement.GetString()
            : turn.ValueKind == JsonValueKind.Object && turn.TryGetProperty("id", out var nestedTurnId) ? nestedTurnId.GetString() : null;
        if (threadId is null || turnId is null || !turns.TryGetValue(Key(threadId, turnId), out var runtime))
        {
            return;
        }

        MainAgentEvent? activity = method switch
        {
            "turn/started" => new(MainAgentEventKind.TurnStarted, threadId, turnId, null, "running", parameters.Clone()),
            "item/agentMessage/delta" => new(MainAgentEventKind.OutputDelta, threadId, turnId, ReadDelta(parameters), null, parameters.Clone()),
            "thread/status/changed" => new(MainAgentEventKind.StatusChanged, threadId, turnId, null, ReadStatus(parameters), parameters.Clone()),
            "turn/completed" => new(MainAgentEventKind.TurnCompleted, threadId, turnId, ExtractAgentText(turn), ReadStatus(turn), parameters.Clone()),
            _ => null,
        };
        if (activity is not null && EventReceived is not null)
        {
            await EventReceived.Invoke(activity);
        }

        if (method == "turn/completed")
        {
            runtime.Completion.TrySetResult(turn.Clone());
        }
    }

    private async Task OnServerRequestAsync(string method, JsonElement requestId, JsonElement parameters)
    {
        var threadId = parameters.TryGetProperty("threadId", out var threadElement) ? threadElement.GetString() : null;
        var turnId = parameters.TryGetProperty("turnId", out var turnElement) ? turnElement.GetString() : null;
        if (threadId is null || turnId is null || !turns.ContainsKey(Key(threadId, turnId)))
        {
            return;
        }

        approvals[Key(threadId, turnId)] = new PendingApproval(method, requestId.Clone(), parameters.Clone());
        if (EventReceived is not null)
        {
            await EventReceived.Invoke(new MainAgentEvent(
                MainAgentEventKind.ApprovalRequested,
                threadId,
                turnId,
                method,
                "waitingForApproval",
                parameters.Clone()));
        }
    }

    private static string? ReadDelta(JsonElement parameters) =>
        parameters.TryGetProperty("delta", out var delta) ? delta.GetString() : null;

    private static string? ReadStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("status", out var status))
        {
            if (status.ValueKind == JsonValueKind.String)
            {
                return status.GetString();
            }

            if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("type", out var type))
            {
                return type.GetString();
            }
        }

        return null;
    }

    private static string? ExtractAgentText(JsonElement turn)
    {
        if (turn.ValueKind != JsonValueKind.Object || !turn.TryGetProperty("items", out var items))
        {
            return null;
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

        return null;
    }

    private static string Key(string threadId, string turnId) => $"{threadId}:{turnId}";

    private sealed class TurnRuntime(string threadId, string turnId)
    {
        public string ThreadId { get; } = threadId;
        public string TurnId { get; } = turnId;
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingApproval(string Method, JsonElement RequestId, JsonElement Parameters);
}

