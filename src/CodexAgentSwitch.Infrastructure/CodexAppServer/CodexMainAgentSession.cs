using System.Collections.Concurrent;
using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class CodexMainAgentSession : IMainAgentSession
{
    private readonly CodexAppServerClient client;
    private readonly ICodexModelResolver modelResolver;
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> request;
    private readonly ConcurrentDictionary<string, TurnRuntime> turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingApproval> approvals = new(StringComparer.Ordinal);

    public CodexMainAgentSession(
        CodexAppServerClient client,
        ICodexModelResolver? modelResolver = null,
        Func<string, object?, CancellationToken, Task<JsonElement>>? request = null)
    {
        this.client = client;
        this.modelResolver = modelResolver ?? new CodexModelResolver();
        this.request = request ?? ((method, parameters, cancellationToken) => client.RequestAsync(method, parameters, cancellationToken));
        client.NotificationReceived += OnNotificationAsync;
        client.ServerRequestReceived += OnServerRequestAsync;
    }

    public event Func<MainAgentEvent, Task>? EventReceived;

    public async Task<MainAgentCompactionHandle> CompactThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId)) throw new ArgumentException("A thread id is required.", nameof(threadId));
        // The acknowledgement only means the server accepted the request. The
        // eventual compaction result remains notification/event driven.
        var response = await request("thread/compact/start", new { threadId }, cancellationToken);
        return new MainAgentCompactionHandle(threadId, true, response.Clone());
    }

    public async Task<MainThreadBindingResult> BindExistingThreadAsync(
        string threadId,
        string expectedSessionId,
        string expectedSource,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var response = await request("thread/read", new { threadId, includeTurns = false }, cancellationToken);
        var thread = RequireBoundThread(response, threadId, expectedSessionId, expectedSource, workingDirectory);
        var status = ReadStatus(thread) ?? "unknown";
        var resumed = false;
        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested Main thread is active; this is not a safe compaction boundary.");
        if (string.Equals(status, "notLoaded", StringComparison.OrdinalIgnoreCase))
        {
            response = await request("thread/resume", new { threadId, cwd = workingDirectory, excludeTurns = true }, cancellationToken);
            thread = RequireBoundThread(response, threadId, expectedSessionId, expectedSource, workingDirectory);
            status = ReadStatus(thread) ?? "unknown";
            resumed = true;
        }
        if (!string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The requested Main thread is not idle after binding (status={status}).");
        return new(threadId, expectedSessionId, expectedSource, Path.GetFullPath(workingDirectory), status, resumed, thread.Clone());
    }

    private static JsonElement RequireBoundThread(
        JsonElement response,
        string threadId,
        string expectedSessionId,
        string expectedSource,
        string workingDirectory)
    {
        if (!response.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The App Server did not return thread metadata.");
        var actualId = ReadString(thread, "id");
        var sessionId = ReadString(thread, "sessionId");
        var source = ReadString(thread, "source");
        var cwd = ReadString(thread, "cwd");
        if (!string.Equals(actualId, threadId, StringComparison.Ordinal)
            || !string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(source, expectedSource, StringComparison.OrdinalIgnoreCase)
            || cwd is null
            || !string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(workingDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The App Server thread metadata does not match the explicit Main binding.");
        return thread;
    }

    public async Task<MainAgentRolloverResult> RolloverThreadAsync(
        string previousThreadId,
        CompactCheckpoint checkpoint,
        string modelId,
        string reasoningEffort,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
        bool startFirstTurn = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasoningEffort);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!string.Equals(checkpoint.SourceThreadId, previousThreadId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Checkpoint provenance does not match the previous thread.", nameof(checkpoint));
        }
        var newThreadId = await CreateThreadAsync(modelId, workingDirectory, approvalMode, cancellationToken);
        if (string.Equals(previousThreadId, newThreadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rollover must create a fresh thread; the previous thread was reused.");
        }

        MainAgentTurnHandle? firstTurn = null;
        if (startFirstTurn)
        {
            firstTurn = await StartTurnAsync(newThreadId, checkpoint.RenderReplayText(), modelId, reasoningEffort, workingDirectory, approvalMode, cancellationToken);
        }

        return new MainAgentRolloverResult(previousThreadId, newThreadId, checkpoint, firstTurn);
    }

    public async Task<string> CreateThreadAsync(
        string modelId,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
        CancellationToken cancellationToken = default)
    {
        var model = await modelResolver.ResolveAsync(client, modelId, cancellationToken);
        var response = await request(
            "thread/start",
            new
            {
                model = model.ModelId,
                cwd = workingDirectory,
                approvalPolicy = ApprovalPolicy(approvalMode),
                sandbox = SandboxMode(approvalMode),
                ephemeral = false,
                serviceName = "codex-agent-switch",
            },
            cancellationToken);
        return response.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidDataException("thread/start did not return a thread id.");
    }

    public async Task ResumeThreadAsync(
        string threadId,
        string modelId,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
        CancellationToken cancellationToken = default)
    {
        var model = await modelResolver.ResolveAsync(client, modelId, cancellationToken);
        await request(
            "thread/resume",
            new
            {
                threadId,
                model = model.ModelId,
                cwd = workingDirectory,
                approvalPolicy = ApprovalPolicy(approvalMode),
                sandbox = SandboxMode(approvalMode),
            },
            cancellationToken);
    }

    public async Task<MainAgentTurnHandle> StartTurnAsync(
        string threadId,
        string prompt,
        string modelId,
        string reasoningEffort,
        string workingDirectory,
        ExecutionApprovalMode approvalMode,
        CancellationToken cancellationToken = default)
    {
        var model = await modelResolver.ResolveAsync(client, modelId, cancellationToken);
        var response = await request(
            "turn/start",
            new
            {
                threadId,
                input = new[] { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } },
                cwd = workingDirectory,
                approvalPolicy = ApprovalPolicy(approvalMode),
                sandboxPolicy = TurnSandboxPolicy(approvalMode, workingDirectory),
                model = model.ModelId,
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
        var response = await request("thread/read", new { threadId, includeTurns = true }, cancellationToken);
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
        await request("turn/interrupt", new { threadId, turnId }, cancellationToken);
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
        // Native compaction is represented as a normal item lifecycle event;
        // it is not a separate thread/compaction notification stream.
        if (threadId is not null
            && method is ("item/started" or "item/completed")
            && parameters.TryGetProperty("item", out var compactionItem)
            && compactionItem.ValueKind == JsonValueKind.Object
            && compactionItem.TryGetProperty("type", out var compactionType)
            && compactionType.GetString() == "contextCompaction")
        {
            var kind = method == "item/completed"
                ? MainAgentEventKind.CompactionCompleted
                : MainAgentEventKind.CompactionStarted;
            if (EventReceived is not null)
            {
                await EventReceived.Invoke(new MainAgentEvent(kind, threadId, turnId ?? string.Empty, null, ReadStatus(compactionItem), parameters.Clone()));
            }
            return;
        }
        if (threadId is null || turnId is null || !turns.TryGetValue(Key(threadId, turnId), out var runtime))
        {
            return;
        }

        MainAgentEvent? activity = method switch
        {
            "turn/started" => new(MainAgentEventKind.TurnStarted, threadId, turnId, null, "running", parameters.Clone()),
            "item/agentMessage/delta" => new(MainAgentEventKind.OutputDelta, threadId, turnId, ReadDelta(parameters), null, parameters.Clone()),
            "item/started" => CreateTraceEvent(threadId, turnId, parameters, started: true),
            "item/completed" => CreateTraceEvent(threadId, turnId, parameters, started: false),
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

    private static MainAgentEvent? CreateTraceEvent(string threadId, string turnId, JsonElement parameters, bool started)
    {
        if (!parameters.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type is "agentMessage" or "contextCompaction")
        {
            return null;
        }

        var kind = type switch
        {
            "fileChange" => TaskMessageKind.FileChange,
            "patch" or "diff" => TaskMessageKind.Diff,
            _ => TaskMessageKind.ToolCall,
        };
        var summary = type switch
        {
            "commandExecution" => ReadString(item, "command") ?? "命令执行完成",
            "fileChange" => ReadString(item, "path") ?? "文件修改完成",
            "mcpToolCall" => $"工具调用：{ReadString(item, "tool") ?? ReadString(item, "name") ?? "未命名工具"}",
            "collabToolCall" => $"工作代理活动：{ReadString(item, "tool") ?? ReadString(item, "name") ?? "任务更新"}",
            null => "工具活动完成",
            _ => $"{type} 已完成",
        };
        return new MainAgentEvent(
            started ? MainAgentEventKind.TraceItemStarted : MainAgentEventKind.TraceItem,
            threadId,
            turnId,
            summary,
            ReadStatus(item),
            parameters.Clone(),
            kind);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    private static string ApprovalPolicy(ExecutionApprovalMode mode) =>
        ExecutionApprovalPolicy.Resolve(mode).ApprovalPolicy;

    private static string SandboxMode(ExecutionApprovalMode mode) =>
        ExecutionApprovalPolicy.Resolve(mode).SandboxMode;

    private static object TurnSandboxPolicy(ExecutionApprovalMode mode, string workingDirectory) => mode switch
    {
        ExecutionApprovalMode.Safe => new { type = "readOnly" },
        ExecutionApprovalMode.FullAuto => new { type = "dangerFullAccess" },
        _ => new
        {
            type = "workspaceWrite",
            writableRoots = new[] { workingDirectory },
            networkAccess = false,
        },
    };

    private sealed class TurnRuntime(string threadId, string turnId)
    {
        public string ThreadId { get; } = threadId;
        public string TurnId { get; } = turnId;
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingApproval(string Method, JsonElement RequestId, JsonElement Parameters);
}
