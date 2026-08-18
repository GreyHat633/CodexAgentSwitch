using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

var pipeName = ReadArgument(args, "--pipe") ?? SchedulerEndpoint.PipeName;
var hook = ReadArgument(args, "--hook");
// Tool registration is immutable for one ToolHost process. MCP advertises
// listChanged=false, so one session cache is safe and a process/version change
// mechanically invalidates it.
var cachedToolDefinitions = ToolDefinitions();
var toolDiscoveryCount = 0;
if (string.Equals(hook, "pre-tool-use", StringComparison.OrdinalIgnoreCase))
{
    await RunPreToolUseHookAsync(pipeName);
}
else if (string.Equals(hook, "stop", StringComparison.OrdinalIgnoreCase))
{
    await RunStopHookAsync(pipeName);
}
else
{
while (await Console.In.ReadLineAsync() is { } line)
{
    line = line.TrimStart('\uFEFF');
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonElement? id = null;
    try
    {
        using var requestDocument = JsonDocument.Parse(line);
        var request = requestDocument.RootElement;
        id = request.TryGetProperty("id", out var requestId) ? requestId.Clone() : null;
        var method = request.GetProperty("method").GetString();
        if (method == "notifications/initialized")
        {
            continue;
        }

        object result = method switch
        {
            "initialize" => new
            {
                protocolVersion = request.TryGetProperty("params", out var initializeParams)
                    && initializeParams.TryGetProperty("protocolVersion", out var requestedProtocol)
                        ? requestedProtocol.GetString() ?? "2025-06-18"
                        : "2025-06-18",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "codex-agent-switch", version = "0.2.6.3" },
            },
            "ping" => new { },
            "tools/list" => ListTools(),
            "tools/call" => await CallToolAsync(request.GetProperty("params"), pipeName),
            _ => throw new InvalidOperationException($"Unsupported MCP method: {method}"),
        };
        await WriteResponseAsync(new { jsonrpc = "2.0", id, result });
    }
    catch (Exception exception)
    {
        await WriteResponseAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = -32603, message = exception.Message },
        });
    }
}
}

object ListTools()
{
    toolDiscoveryCount++;
    return new
    {
        tools = cachedToolDefinitions,
        discovery = new { count = toolDiscoveryCount, cacheHit = toolDiscoveryCount > 1 },
    };
}

static async Task RunStopHookAsync(string pipeName)
{
    var line = await Console.In.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(line)) return;
    var sessionId = string.Empty;
    var cwd = Environment.CurrentDirectory;
    var stage = "parse-input";
    try
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        sessionId = ReadJsonString(root, "session_id") ?? ReadJsonString(root, "sessionId") ?? string.Empty;
        cwd = ReadJsonString(root, "cwd") ?? ReadJsonString(root, "workingDirectory") ?? Environment.CurrentDirectory;
        stage = "scheduler-send";
        var result = await SendAsync(pipeName, "mainContextBoundary", new
        {
            sessionId,
            threadId = sessionId,
            workingDirectory = cwd,
            source = "vscode",
            boundary = "stop",
        });
        var bindingAccepted = ReadJsonBool(result, "bindingAccepted");
        var compactionRequested = ReadJsonBool(result, "compactionRequested");
        var compactionSucceeded = ReadJsonBool(result, "compactionSucceeded");
        if (bindingAccepted == false || compactionRequested == true || compactionSucceeded == true)
            WriteStopHookDiagnostic(pipeName, sessionId, cwd, "scheduler-result", null, result);
    }
    catch (Exception exception)
    {
        WriteStopHookDiagnostic(pipeName, sessionId, cwd, stage, exception);
    }

    await WriteResponseAsync(new { hookSpecificOutput = new { hookEventName = "Stop" } });
}

static void WriteStopHookDiagnostic(
    string pipeName,
    string sessionId,
    string workingDirectory,
    string stage,
    Exception? exception,
    JsonElement? result = null)
{
    try
    {
        var dataRoot = Environment.GetEnvironmentVariable("CAS_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
        var logsDirectory = Path.Combine(Path.GetFullPath(dataRoot), "logs");
        Directory.CreateDirectory(logsDirectory);
        var path = Path.Combine(logsDirectory, "context-economy-stop-hook.jsonl");
        var boundary = result.GetValueOrDefault();
        var record = new Dictionary<string, object?>
        {
            ["Timestamp"] = DateTimeOffset.UtcNow,
            ["Hook"] = "Stop",
            ["SessionId"] = sessionId,
            ["WorkingDirectory"] = workingDirectory,
            ["PipeName"] = pipeName,
            ["Stage"] = stage,
            ["ExceptionType"] = exception?.GetType().Name,
            ["Message"] = SanitizeDiagnosticMessage(exception?.Message ?? ReadJsonString(boundary, "reason")),
            ["BindingAccepted"] = ReadJsonBool(boundary, "bindingAccepted"),
            ["TelemetryAvailable"] = ReadJsonBool(boundary, "telemetryAvailable"),
            ["State"] = ReadJsonScalar(boundary, "state"),
            ["Reason"] = SanitizeDiagnosticMessage(ReadJsonString(boundary, "reason")),
            ["CompactionRequested"] = ReadJsonBool(boundary, "compactionRequested"),
            ["CompactionSucceeded"] = ReadJsonBool(boundary, "compactionSucceeded"),
        };
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var output = new StreamWriter(stream, new UTF8Encoding(false));
        output.WriteLine(JsonSerializer.Serialize(record));
    }
    catch
    {
        // Diagnostics are best-effort; Stop must remain fail-open even if logging fails.
    }
}

static bool? ReadJsonBool(JsonElement element, string name) =>
    element.ValueKind == JsonValueKind.Object
    && element.TryGetProperty(name, out var value)
    && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean()
        : null;

static string? ReadJsonScalar(JsonElement element, string name)
{
    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
}

static string? SanitizeDiagnosticMessage(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return sanitized.Length <= 512 ? sanitized : sanitized[..512];
}

static async Task RunPreToolUseHookAsync(string pipeName)
{
    var line = await Console.In.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(line)) return;
    try
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var sessionId = ReadJsonString(root, "session_id") ?? ReadJsonString(root, "sessionId") ?? string.Empty;
        var cwd = ReadJsonString(root, "cwd") ?? ReadJsonString(root, "workingDirectory") ?? Environment.CurrentDirectory;
        var toolName = ReadJsonString(root, "tool_name") ?? ReadJsonString(root, "toolName") ?? string.Empty;
        var input = root.TryGetProperty("tool_input", out var toolInput) ? toolInput.GetRawText() : root.TryGetProperty("toolInput", out var inputValue) ? inputValue.GetRawText() : null;
        var result = await SendAsync(pipeName, "preToolUse", new { sessionId, workingDirectory = cwd, toolName, toolInput = input });
        var denied = result.TryGetProperty("allowed", out var allowed) && !allowed.GetBoolean();
        if (denied)
        {
            await WriteResponseAsync(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    permissionDecision = "deny",
                    permissionDecisionReason = result.TryGetProperty("reason", out var reason) ? reason.GetString() : "Agent Switch ownership gate denied the mutation.",
                },
            });
        }
        else
        {
            await WriteResponseAsync(new { hookSpecificOutput = new { hookEventName = "PreToolUse" } });
        }
    }
    catch (Exception)
    {
        // Codex does not support "ask" for PreToolUse hooks. Scheduler or
        // malformed-input failures therefore deny this narrowly matched hook.
        await WriteResponseAsync(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = "deny",
                permissionDecisionReason = "Agent Switch ownership gate is unavailable; retry after Scheduler recovery.",
            },
        });
    }
}

static string? ReadJsonString(JsonElement element, string name) =>
    element.ValueKind == JsonValueKind.Object
    && element.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static async Task<object> CallToolAsync(JsonElement parameters, string pipeName)
{
    var name = parameters.GetProperty("name").GetString();
    var arguments = parameters.TryGetProperty("arguments", out var value) ? value : default;
    var (method, payload) = name switch
    {
        "delegation_preflight" => ("delegationPreflight", (object)ReadPreflight(arguments)),
        "delegate_worker" => ("dispatch", (object)ReadTaskPacket(arguments)),
        "consume_worker_result" => ("consumeResult", new { taskId = Required(arguments, "taskId") }),
        "report_worker_result" => ("reportResult", ReadWorkerResult(arguments)),
        "begin_worker_review" => ("review", new { taskId = Required(arguments, "taskId") }),
        "adopt_worker_result" => ("adopt", new { taskId = Required(arguments, "taskId"), summary = Optional(arguments, "summary") }),
        "complete_package" => ("completePackage", new { packageId = Required(arguments, "packageId"), workingDirectory = Required(arguments, "workingDirectory") }),
        "record_repartition" => ("recordRepartition", ReadRepartition(arguments)),
        "queue_repartition" => ("queueRepartition", new { taskGroupId = Required(arguments, "taskGroupId"), workingDirectory = Required(arguments, "workingDirectory"), workSummary = Required(arguments, "workSummary"), triggers = RequiredStrings(arguments, "triggers") }),
        "list_repartitions" => ("listRepartitions", new { taskGroupId = Required(arguments, "taskGroupId") }),
        "scheduler_status" => ("status", new { }),
        _ => throw new InvalidOperationException($"Unknown tool: {name}"),
    };
    var schedulerResult = await SendAsync(pipeName, method, payload);
    if (string.Equals(name, "delegation_preflight", StringComparison.Ordinal)
        && schedulerResult.TryGetProperty("dispatchReady", out var dispatchReady)
        && dispatchReady.GetBoolean())
    {
        var worker = schedulerResult.TryGetProperty("workerId", out var workerId) ? workerId.GetString() : null;
        schedulerResult = JsonSerializer.SerializeToElement(new
        {
            DispatchReady = true,
            Backend = worker?.StartsWith("cas_", StringComparison.Ordinal) == true
                || worker?.StartsWith("native-", StringComparison.Ordinal) == true ? "Native" : "External",
            Worker = worker,
        });
    }
    else if (string.Equals(name, "consume_worker_result", StringComparison.Ordinal)
        || string.Equals(name, "begin_worker_review", StringComparison.Ordinal)
        || string.Equals(name, "adopt_worker_result", StringComparison.Ordinal))
    {
        schedulerResult = CompactTerminalPacket(schedulerResult);
    }
    else if (string.Equals(name, "record_repartition", StringComparison.Ordinal))
    {
        // Successful repartition responses are intentionally compact; callers
        // already supplied the full package and summary payload.
        schedulerResult = JsonSerializer.SerializeToElement(new
        {
            RepartitionRecorded = true,
            Decision = schedulerResult.TryGetProperty("decision", out var decision)
                ? decision.ValueKind == JsonValueKind.Number
                    ? (decision.GetInt32() == 1 ? "WORKER" : "MAIN")
                    : decision.GetString()
                : null,
            PendingTriggersCleared = schedulerResult.TryGetProperty("pendingTriggersCleared", out var cleared) ? cleared.GetInt32() : 0,
            Lease = schedulerResult.TryGetProperty("leaseActive", out var leaseActive) && leaseActive.GetBoolean() ? "ACTIVE" : "NONE",
        });
    }
    else if (string.Equals(name, "queue_repartition", StringComparison.Ordinal))
    {
        schedulerResult = JsonSerializer.SerializeToElement(new
        {
            RepartitionQueued = true,
            PendingTriggerCount = schedulerResult.GetProperty("pendingTriggerCount").GetInt32(),
        });
    }
    return new
    {
        content = new[] { new { type = "text", text = schedulerResult.GetRawText() } },
        structuredContent = schedulerResult,
        isError = false,
    };
}

static JsonElement CompactTerminalPacket(JsonElement result)
{
    static string? TextValue(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static bool? BoolValue(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    static string[] StringsValue(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];
    return JsonSerializer.SerializeToElement(new
    {
        taskId = TextValue(result, "taskId"),
        state = result.TryGetProperty("state", out var state) ? state.Clone() : default,
        summary = TextValue(result, "summary"),
        scope = StringsValue(result, "scope"),
        changedFiles = StringsValue(result, "changes"),
        validation = StringsValue(result, "validation"),
        risks = StringsValue(result, "risks"),
        terminalReason = TextValue(result, "hardLimitReason") ?? TextValue(result, "failureReason"),
        recoveryAttempted = BoolValue(result, "recoveryAttempted"),
        retryAttempted = BoolValue(result, "retryAttempted"),
        recentFailureSummary = TextValue(result, "recentFailureSummary"),
    });
}

static TaskPacket ReadTaskPacket(JsonElement arguments) => new(
    Required(arguments, "taskId"),
    Optional(arguments, "projectId"),
    Required(arguments, "workingDirectory"),
    Optional(arguments, "workerId"),
    Required(arguments, "goal"),
    Strings(arguments, "scope"),
    Strings(arguments, "allowedReadScope"),
    Strings(arguments, "allowedWriteScope"),
    Strings(arguments, "acceptanceCriteria"),
    Strings(arguments, "constraints"),
    Required(arguments, "outputContract"));

static DelegationPreflightRequest ReadPreflight(JsonElement arguments) => new(
    Required(arguments, "workingDirectory"),
    OptionalNullable(arguments, "projectId"),
    OptionalNullable(arguments, "workerId"),
    OptionalNullable(arguments, "taskId"));

static WorkerResultPacket ReadWorkerResult(JsonElement arguments)
{
    var succeeded = !arguments.TryGetProperty("succeeded", out var succeededValue) || succeededValue.GetBoolean();
    return new WorkerResultPacket(
        Required(arguments, "taskId"),
        succeeded ? DelegationState.ResultReceived : DelegationState.Failed,
        Required(arguments, "summary"),
        Strings(arguments, "evidence"),
        Strings(arguments, "changes"),
        Strings(arguments, "validation"),
        Strings(arguments, "risks"),
        FailureReason: succeeded ? null : Optional(arguments, "failureReason"));
}

static object ReadRepartition(JsonElement arguments) => new
{
    taskGroupId = Required(arguments, "taskGroupId"),
    trigger = ParseEnum<RepartitionTrigger>(arguments, "trigger").ToString(),
    decision = ParseEnum<WorkOwner>(arguments, "decision").ToString(),
    reason = ParseEnum<RepartitionReasonCode>(arguments, "reason").ToString(),
    workSummary = Required(arguments, "workSummary"),
    workerIdentity = OptionalNullable(arguments, "workerIdentity"),
    result = OptionalNullable(arguments, "result"),
    packageId = Required(arguments, "packageId"),
    workingDirectory = Required(arguments, "workingDirectory"),
    packageKind = Required(arguments, "packageKind"),
    declaredScopes = RequiredStrings(arguments, "declaredScopes"),
};

static async Task<JsonElement> SendAsync(string pipeName, string method, object payload)
{
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        await pipe.ConnectAsync(timeout.Token);
    }
    catch (OperationCanceledException)
    {
        throw new InvalidOperationException("Agent Switch Scheduler 未运行；请启动 Agent Switch 后重试。");
    }

    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync(JsonSerializer.Serialize(new { method, payload }, ToolHostJson.Options));
    var responseLine = await reader.ReadLineAsync() ?? throw new IOException("Scheduler 未返回响应。");
    using var responseDocument = JsonDocument.Parse(responseLine);
    var response = responseDocument.RootElement;
    if (!response.GetProperty("ok").GetBoolean())
    {
        throw new InvalidOperationException(response.GetProperty("error").GetString() ?? "Scheduler 请求失败。");
    }

    return response.GetProperty("result").Clone();
}

static object[] ToolDefinitions() =>
[
    new
    {
        name = "delegation_preflight",
        description = "Model-free scheduler/project/profile/worker readiness check. Resolves omitted projectId and workerId before dispatch.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["workingDirectory"] = StringSchema("Absolute project working directory."),
                ["projectId"] = StringSchema("Optional registered project id."),
                ["workerId"] = StringSchema("Optional applied worker id."),
                ["taskId"] = StringSchema("Optional task id for slot diagnostics."),
            },
            required = new[] { "workingDirectory" },
            additionalProperties = false,
        },
    },
    new
    {
        name = "delegate_worker",
        description = "Send one explicit plaintext bounded TaskPacket to the Agent Switch scheduler. Do not duplicate the delegated work while its state is DELEGATED or RUNNING.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["taskId"] = StringSchema("Unique stable task id."),
                ["projectId"] = StringSchema("Agent Switch project id when known."),
                ["workingDirectory"] = StringSchema("Absolute project working directory."),
                ["workerId"] = StringSchema("Optional. When omitted, Agent Switch resolves the current project's applied Worker."),
                ["goal"] = StringSchema("Bounded goal in plaintext."),
                ["scope"] = StringArraySchema("Specific files or modules."),
                ["allowedReadScope"] = StringArraySchema("Allowed read scope."),
                ["allowedWriteScope"] = StringArraySchema("Allowed write scope."),
                ["acceptanceCriteria"] = StringArraySchema("Verifiable acceptance criteria."),
                ["constraints"] = StringArraySchema("Execution constraints."),
                ["outputContract"] = StringSchema("Expected result format."),
            },
            required = new[] { "taskId", "workingDirectory", "goal", "scope", "acceptanceCriteria", "outputContract" },
            additionalProperties = false,
        },
    },
    new
    {
        name = "consume_worker_result",
        description = "Consume one persisted External Worker terminal packet exactly once at a natural Main boundary. Do not poll while the task is DELEGATED or RUNNING.",
        inputSchema = TaskIdSchema(),
    },
    new
    {
        name = "report_worker_result",
        description = "Report a Native Custom Agent result to its existing Scheduler task.",
        inputSchema = ResultSchema(),
    },
    new
    {
        name = "begin_worker_review",
        description = "Move a delivered RESULT_RECEIVED, BLOCKED, or FAILED task to REVIEWING before bounded review.",
        inputSchema = TaskIdSchema(),
    },
    new
    {
        name = "adopt_worker_result",
        description = "Mark a reviewed result ADOPTED. Only call after bounded review.",
        inputSchema = new { type = "object", properties = new { taskId = StringSchema("Task id."), summary = StringSchema("Adoption summary.") }, required = new[] { "taskId" }, additionalProperties = false },
    },
    new
    {
        name = "complete_package",
        description = "Mark a usable or review lease COMPLETED after the package is complete.",
        inputSchema = new { type = "object", properties = new { packageId = StringSchema("Package id."), workingDirectory = StringSchema("Package working directory.") }, required = new[] { "packageId", "workingDirectory" }, additionalProperties = false },
    },
    new
    {
        name = "scheduler_status",
        description = "Read Scheduler state and active task count without starting model work.",
        inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
    },
    new
    {
        name = "record_repartition",
        description = "Persist one Main-reported repartition decision. The Scheduler validates owner/reason consistency but does not infer semantic meaning.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["taskGroupId"] = StringSchema("Task-group identifier."),
                ["trigger"] = EnumSchema<RepartitionTrigger>("Semantic repartition trigger."),
                ["decision"] = EnumSchema<WorkOwner>("Current work owner."),
                ["reason"] = EnumSchema<RepartitionReasonCode>("Owner-specific reason code."),
                ["workSummary"] = StringSchema("Short current-work summary."),
                ["workerIdentity"] = StringSchema("Optional worker identity."),
                ["result"] = StringSchema("Optional result or remaining-work summary."),
                ["packageId"] = StringSchema("Durable package id."),
                ["workingDirectory"] = StringSchema("Package working directory."),
                ["packageKind"] = StringSchema("Package kind."),
                ["declaredScopes"] = StringArraySchema("Declared ownership scopes."),
            },
            required = new[] { "taskGroupId", "trigger", "decision", "reason", "workSummary", "packageId", "workingDirectory", "packageKind", "declaredScopes" },
            additionalProperties = false,
        },
    },
    new
    {
        name = "queue_repartition",
        description = "Queue one or more Main-reported semantic triggers without creating an ownership lease.",
        inputSchema = new { type = "object", properties = new Dictionary<string, object> { ["taskGroupId"] = StringSchema("Task group."), ["workingDirectory"] = StringSchema("Working directory."), ["workSummary"] = StringSchema("Summary."), ["triggers"] = EnumArraySchema<RepartitionTrigger>() }, required = new[] { "taskGroupId", "workingDirectory", "workSummary", "triggers" }, additionalProperties = false },
    },
    new
    {
        name = "list_repartitions",
        description = "Read persisted repartition decisions for one task group in sequence order.",
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object> { ["taskGroupId"] = StringSchema("Task-group identifier.") },
            required = new[] { "taskGroupId" },
            additionalProperties = false,
        },
    },
];

static object ResultSchema() => new
{
    type = "object",
    properties = new Dictionary<string, object>
    {
        ["taskId"] = StringSchema("Task id."),
        ["succeeded"] = new { type = "boolean" },
        ["summary"] = StringSchema("Concise result summary."),
        ["evidence"] = StringArraySchema("Evidence."),
        ["changes"] = StringArraySchema("Changed files or actions."),
        ["validation"] = StringArraySchema("Validation performed."),
        ["risks"] = StringArraySchema("Remaining risks."),
        ["failureReason"] = StringSchema("Failure reason when unsuccessful."),
    },
    required = new[] { "taskId", "summary" },
    additionalProperties = false,
};

static object TaskIdSchema() => new { type = "object", properties = new { taskId = StringSchema("Task id.") }, required = new[] { "taskId" }, additionalProperties = false };
static object StringSchema(string description) => new { type = "string", description };
static object EnumSchema<T>(string description) where T : struct, Enum => new { type = "string", description, @enum = Enum.GetNames<T>() };
static object EnumArraySchema<T>() where T : struct, Enum => new { type = "array", items = new { type = "string", @enum = Enum.GetNames<T>() } };
static object StringArraySchema(string description) => new { type = "array", items = new { type = "string" }, description };
static string Required(JsonElement element, string name) => element.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"{name} is required.");
static string Optional(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
static string? OptionalNullable(JsonElement element, string name) => element.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
static T ParseEnum<T>(JsonElement element, string name) where T : struct, Enum => element.TryGetProperty(name, out var value) && Enum.TryParse<T>(value.GetString(), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new InvalidDataException($"{name} is invalid.");
static IReadOnlyList<string> Strings(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray() : [];
static IReadOnlyList<string> RequiredStrings(JsonElement element, string name)
{
    var values = Strings(element, name);
    return values.Count > 0 ? values : throw new InvalidDataException($"{name} is required and cannot be empty.");
}
static string? ReadArgument(string[] arguments, string name) { var index = Array.IndexOf(arguments, name); return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null; }
static Task WriteResponseAsync(object response) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, ToolHostJson.Options));

internal static class ToolHostJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
