using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Domain.ExternalAgents;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.ExternalProviders;

namespace CodexAgentSwitch.Infrastructure.ExternalAgents;

public enum ExternalAgentRuntimeState
{
    Completed,
    Blocked,
    Cancelled,
    Timeout,
}

/// <summary>Mechanical limits for an external worker. Soft limits extend silently up to hard limits.</summary>
public sealed record ExternalAgentRuntimeOptions
{
    public int InitialProviderTurnSoftLimit { get; init; } = 24;
    public int InitialToolCallSoftLimit { get; init; } = 48;
    public int ProviderTurnLeaseIncrement { get; init; } = 8;
    public int ToolCallLeaseIncrement { get; init; } = 16;
    public int HardProviderTurnLimit { get; init; } = 64;
    public int HardToolCallLimit { get; init; } = 128;
    public int MaxRepeatedIdenticalToolCalls { get; init; } = 3;
    /// <summary>Maximum number of recent observations retained by the no-progress guard.</summary>
    public int NoProgressWindowSize { get; init; } = 8;
    /// <summary>Age after which a no-progress observation is no longer relevant.</summary>
    public TimeSpan NoProgressWindow { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan? MaxWallClock { get; init; }

    // Compatibility aliases for callers of the 0.2.5.1 API.
    public int MaxProviderTurns { get => InitialProviderTurnSoftLimit; init => InitialProviderTurnSoftLimit = value; }
    public int MaxToolCalls { get => InitialToolCallSoftLimit; init => InitialToolCallSoftLimit = value; }

    public ExternalAgentRuntimeOptions() { }

    public ExternalAgentRuntimeOptions(int MaxProviderTurns = 24, int MaxToolCalls = 48, int MaxRepeatedIdenticalToolCalls = 3, TimeSpan? MaxWallClock = null)
    {
        InitialProviderTurnSoftLimit = MaxProviderTurns;
        InitialToolCallSoftLimit = MaxToolCalls;
        this.MaxRepeatedIdenticalToolCalls = MaxRepeatedIdenticalToolCalls;
        this.MaxWallClock = MaxWallClock;
    }

    public TimeSpan EffectiveMaxWallClock => MaxWallClock ?? TimeSpan.FromMinutes(10);
}

public sealed record ExternalAgentRuntimeResult(
    ExternalAgentRuntimeState State,
    string? Content,
    int ProviderTurns,
    int ToolCalls,
    int FailedToolCalls,
    int DeniedToolCalls,
    TimeSpan Duration,
    ProviderUsage? Usage,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ExternalToolActivity> Activity,
    IReadOnlyList<string> Risks,
    JsonElement? RawResponse)
{
    public int LeaseExtensionCount { get; init; }
    public string? HardLimitReason { get; init; }
    public BudgetLimits? BudgetSnapshot { get; init; }
    public bool CostVerified { get; init; }
    public bool FinalizationAttempted { get; init; }
    public bool FinalizationSucceeded { get; init; }
    public bool RecoveryAttempted { get; init; }
    public string? RecentFailureSummary { get; init; }
}

public sealed record ExternalToolActivity(
    int Sequence,
    string ToolCallId,
    string ToolName,
    string Arguments,
    bool Succeeded,
    bool Denied,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    IReadOnlyList<string> ChangedFiles)
{
    public DateTimeOffset OccurredAt { get; init; }
    public string ArgumentHash { get; init; } = string.Empty;
    public string ArgumentSummary { get; init; } = string.Empty;
    public string ResultHash { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string StandardOutputSummary { get; init; } = string.Empty;
    public string StandardErrorSummary { get; init; } = string.Empty;
    public bool Progress { get; init; }
    public IReadOnlyList<string> ChangedFilesDelta { get; init; } = [];
    public string? ValidationDelta { get; init; }
    public bool RecoveryInstructionInjected { get; init; }
}

public sealed class OpenAiCompatibleExternalAgentRuntime(
    OpenAiCompatibleClient client,
    IExternalToolHost toolHost,
    ExternalAgentRuntimeOptions? options = null)
{
    private const int MaximumToolContextCharacters = 12_000;
    private const int MaximumTelemetrySummaryCharacters = 512;
    private const string RecoveryInstruction = "Recovery instruction: make measurable progress toward the task. Do not repeat the same tool call or another equivalent failed/denied action; inspect the result, choose a different safe action, or report the blocker.";
    private readonly ExternalAgentRuntimeOptions options = options ?? new ExternalAgentRuntimeOptions();

    public async Task<ExternalAgentRuntimeResult> ExecuteAsync(
        ProviderConfiguration provider,
        string modelId,
        string prompt,
        ExternalToolSession session,
        CancellationToken cancellationToken = default,
        BudgetLimits? budgetSnapshot = null)
    {
        ValidateOptions();
        var stopwatch = Stopwatch.StartNew();
        using var wallClock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wallClock.CancelAfter(options.EffectiveMaxWallClock);
        var messages = new List<ExternalAgentMessage>
        {
            ExternalAgentMessage.System("You are an external coding worker. Use the provided tools when needed. Return a concise final result without revealing hidden reasoning."),
            ExternalAgentMessage.User(prompt),
        };
        var tools = ToolDefinitions(session);
        var recentNoProgress = new Queue<NoProgressObservation>();
        var recoveryInstructionIssued = false;
        ProviderUsage? totalUsage = null;
        JsonElement? lastRaw = null;
        var turns = 0;
        var toolCalls = 0;
        var failedToolCalls = 0;
        var deniedToolCalls = 0;
        var leaseExtensions = 0;
        var providerLease = Math.Min(options.InitialProviderTurnSoftLimit, options.HardProviderTurnLimit);
        var toolLease = Math.Min(options.InitialToolCallSoftLimit, options.HardToolCallLimit);
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activity = new List<ExternalToolActivity>();
        var usageKnown = true;
        var hardReason = (string?)null;
        var finalizationAttempted = false;
        var finalizationSucceeded = false;
        string? content = null;
        var state = ExternalAgentRuntimeState.Completed;
        var risks = new List<string>();

        try
        {
            while (true)
            {
                if (!CanStartRequest(turns, totalUsage, usageKnown, provider, budgetSnapshot, out var budgetReason))
                {
                    hardReason = budgetReason;
                    state = ExternalAgentRuntimeState.Blocked;
                    break;
                }

                if (turns >= providerLease)
                {
                    if (providerLease >= options.HardProviderTurnLimit)
                    {
                        hardReason = "provider-turn-limit";
                        state = ExternalAgentRuntimeState.Blocked;
                        break;
                    }

                    providerLease = Math.Min(options.HardProviderTurnLimit, providerLease + options.ProviderTurnLeaseIncrement);
                    leaseExtensions++;
                }

                var completion = await client.CompleteWithToolsAsync(provider, modelId, messages, tools, wallClock.Token);
                turns++;
                totalUsage = AddUsage(totalUsage, completion.Usage);
                usageKnown &= completion.Usage is not null;
                lastRaw = completion.RawResponse;
                if (completion.ToolCalls.Count == 0)
                {
                    content = completion.Content;
                    state = ExternalAgentRuntimeState.Completed;
                    break;
                }

                if (toolCalls + completion.ToolCalls.Count > options.HardToolCallLimit)
                {
                    hardReason = "tool-call-limit";
                    state = ExternalAgentRuntimeState.Blocked;
                    break;
                }

                while (toolCalls + completion.ToolCalls.Count > toolLease)
                {
                    if (toolLease >= options.HardToolCallLimit)
                    {
                        hardReason = "tool-call-limit";
                        state = ExternalAgentRuntimeState.Blocked;
                        break;
                    }
                    toolLease = Math.Min(options.HardToolCallLimit, toolLease + options.ToolCallLeaseIncrement);
                    leaseExtensions++;
                }
                if (hardReason is not null) break;

                messages.Add(ExternalAgentMessage.Assistant(completion.Content, completion.ToolCalls));
                var recoveryInstructionPending = false;
                foreach (var call in completion.ToolCalls)
                {
                    toolCalls++;
                    var normalizedCall = NormalizeToolCall(call.Name, call.Arguments);
                    var execution = await toolHost.ExecuteAsync(
                        session,
                        new ExternalToolExecutionRequest(call.Id, call.Name, call.Arguments),
                        wallClock.Token);
                    if (!execution.Succeeded) failedToolCalls++;
                    if (execution.Denied) deniedToolCalls++;
                    var occurredAt = DateTimeOffset.UtcNow;
                    var changedFilesDelta = execution.ChangedFiles is null
                        ? []
                        : execution.ChangedFiles.Where(file => !changedFiles.Contains(file)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var resultHash = HashResult(execution);
                    var outcome = Outcome(execution);
                    var progress = changedFilesDelta.Length > 0
                        || IsObservableResultProgress(normalizedCall, resultHash, recentNoProgress);
                    if (execution.ChangedFiles is not null) changedFiles.UnionWith(execution.ChangedFiles);
                    var activityItem = new ExternalToolActivity(
                        toolCalls,
                        call.Id,
                        call.Name,
                        Bound(call.Arguments, MaximumToolContextCharacters / 2),
                        execution.Succeeded,
                        execution.Denied,
                        execution.TimedOut,
                        execution.ExitCode,
                        Bound(execution.StandardOutput, MaximumToolContextCharacters),
                        Bound(execution.StandardError, MaximumToolContextCharacters),
                        execution.ChangedFiles ?? [])
                    {
                        OccurredAt = occurredAt,
                        ArgumentHash = Hash(NormalizeArguments(call.Arguments)),
                        ArgumentSummary = Summarize(NormalizeArguments(call.Arguments)),
                        ResultHash = resultHash,
                        Outcome = outcome,
                        StandardOutputSummary = Summarize(execution.StandardOutput),
                        StandardErrorSummary = Summarize(execution.StandardError),
                        Progress = progress,
                        ChangedFilesDelta = changedFilesDelta,
                        ValidationDelta = DetectValidationDelta(call, execution),
                    };
                    messages.Add(ExternalAgentMessage.ToolResult(call.Id, SerializeToolResult(execution), call.Name));

                    TrimNoProgressObservations(recentNoProgress, occurredAt);
                    if (progress)
                    {
                        recentNoProgress.Clear();
                    }
                    else
                    {
                        recentNoProgress.Enqueue(new NoProgressObservation(normalizedCall, outcome, resultHash, occurredAt));
                        TrimNoProgressObservations(recentNoProgress, occurredAt);
                    }

                    var blockAfterCall = false;
                    if (!progress && IsNoProgressPattern(recentNoProgress, normalizedCall, outcome, resultHash))
                    {
                        if (!recoveryInstructionIssued)
                        {
                            recoveryInstructionIssued = true;
                            recoveryInstructionPending = true;
                            activityItem = activityItem with { RecoveryInstructionInjected = true };
                        }
                        else if (!recoveryInstructionPending)
                        {
                            // The triggering call has already run and is present in activity before this terminal decision.
                            hardReason = "repeated-no-progress-tool-call";
                            state = ExternalAgentRuntimeState.Blocked;
                            blockAfterCall = true;
                        }
                    }
                    activity.Add(activityItem);
                    if (activity.Count > options.NoProgressWindowSize)
                    {
                        activity.RemoveAt(0);
                    }
                    if (blockAfterCall)
                    {
                        if (recoveryInstructionPending)
                        {
                            messages.Add(ExternalAgentMessage.User(RecoveryInstruction));
                            recoveryInstructionPending = false;
                        }
                        break;
                    }
                }
                if (hardReason is not null) break;
                if (recoveryInstructionPending)
                {
                    messages.Add(ExternalAgentMessage.User(RecoveryInstruction));
                }
            }

            if (state == ExternalAgentRuntimeState.Blocked && hardReason is not null)
            {
                (content, finalizationAttempted, finalizationSucceeded) = await TryFinalizeAsync();
                if (!finalizationSucceeded)
                {
                    risks.Add(hardReason is "repeated-identical-tool-call" or "repeated-no-progress-tool-call"
                        ? "External Agent Runtime detected a repeated identical no-progress tool call loop."
                        : $"External Agent Runtime stopped: {hardReason}.");
                }
            }
        }
        catch (ProviderRequestException exception) when (exception.Kind == ProviderErrorKind.Cancelled && wallClock.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            state = ExternalAgentRuntimeState.Timeout;
            hardReason = "wall-clock";
            risks.Add("External Agent Runtime reached MaxWallClock.");
        }
        catch (ProviderRequestException exception) when (exception.Kind == ProviderErrorKind.Cancelled && cancellationToken.IsCancellationRequested)
        {
            state = ExternalAgentRuntimeState.Cancelled;
            hardReason = "cancellation";
            risks.Add("External Agent Runtime was cancelled.");
        }
        catch (OperationCanceledException) when (wallClock.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            state = ExternalAgentRuntimeState.Timeout;
            hardReason = "wall-clock";
            risks.Add("External Agent Runtime reached MaxWallClock.");
        }
        catch (OperationCanceledException)
        {
            state = ExternalAgentRuntimeState.Cancelled;
            hardReason = "cancellation";
            risks.Add("External Agent Runtime was cancelled.");
        }

        var costVerified = usageKnown
            && provider.Pricing is not null
            && BudgetCurrencyMatches(provider.Pricing, budgetSnapshot);
        return new ExternalAgentRuntimeResult(
            state,
            content,
            turns,
            toolCalls,
            failedToolCalls,
            deniedToolCalls,
            stopwatch.Elapsed,
            totalUsage,
            changedFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            activity,
            risks,
            lastRaw)
        {
            LeaseExtensionCount = leaseExtensions,
            HardLimitReason = hardReason,
            BudgetSnapshot = budgetSnapshot,
            CostVerified = costVerified,
            FinalizationAttempted = finalizationAttempted,
            FinalizationSucceeded = finalizationSucceeded,
            RecoveryAttempted = recoveryInstructionIssued,
            RecentFailureSummary = BuildRecentFailureSummary(activity, hardReason),
        };

        async Task<(string? Content, bool Attempted, bool Succeeded)> TryFinalizeAsync()
        {
            if (finalizationAttempted || wallClock.IsCancellationRequested || !CanStartRequest(turns + 1, totalUsage, usageKnown, provider, budgetSnapshot, out _))
                return (null, false, false);
            finalizationAttempted = true;
            try
            {
                var finalMessages = messages.Append(ExternalAgentMessage.User(
                    "Runtime hard stop. Do not request or describe new work. Return only a concise status of completed work, changed files, test/build state, and remaining work.")).ToArray();
                var completion = await client.CompleteWithToolsAsync(provider, modelId, finalMessages, [], wallClock.Token);
                totalUsage = AddUsage(totalUsage, completion.Usage);
                usageKnown &= completion.Usage is not null;
                lastRaw = completion.RawResponse;
                return completion.ToolCalls.Count == 0
                    ? (completion.Content, true, true)
                    : (null, true, false);
            }
            catch (OperationCanceledException)
            {
                return (null, true, false);
            }
            catch (ProviderRequestException)
            {
                return (null, true, false);
            }
        }
    }

    private bool CanStartRequest(int turns, ProviderUsage? usage, bool usageKnown, ProviderConfiguration provider, BudgetLimits? budget, out string? reason)
    {
        reason = null;
        if (budget?.RequestLimit is { } requests && turns >= requests) { reason = "request-budget"; return false; }
        var totalTokens = usage?.TotalTokens ?? (usage?.InputTokens is { } input && usage.OutputTokens is { } output ? input + output : null);
        if (budget?.TokenLimit is { } tokens && totalTokens is { } consumedTokens && consumedTokens >= tokens) { reason = "token-budget"; return false; }
        if (budget?.PerTask is { } costLimit
            && usageKnown
            && provider.Pricing?.InputPerMillionTokens is { } inputRate
            && provider.Pricing.OutputPerMillionTokens is { } outputRate
            && BudgetCurrencyMatches(provider.Pricing, budget)
            && usage is not null)
        {
            var cost = (usage.InputTokens ?? 0) / 1_000_000m * inputRate + (usage.OutputTokens ?? 0) / 1_000_000m * outputRate;
            if (cost >= costLimit) { reason = "monetary-budget"; return false; }
        }
        return true;
    }

    private static bool BudgetCurrencyMatches(ProviderPricing pricing, BudgetLimits? budget) =>
        budget is null || string.Equals(pricing.Currency, budget.Currency, StringComparison.OrdinalIgnoreCase);

    private void ValidateOptions()
    {
        if (options.InitialProviderTurnSoftLimit <= 0 || options.InitialProviderTurnSoftLimit > options.HardProviderTurnLimit
            || options.InitialToolCallSoftLimit <= 0 || options.InitialToolCallSoftLimit > options.HardToolCallLimit
            || options.ProviderTurnLeaseIncrement <= 0 || options.ToolCallLeaseIncrement <= 0
            || options.HardProviderTurnLimit <= 0 || options.HardToolCallLimit <= 0
            || options.MaxRepeatedIdenticalToolCalls <= 0 || options.NoProgressWindowSize <= 0
            || options.NoProgressWindow <= TimeSpan.Zero || options.EffectiveMaxWallClock <= TimeSpan.Zero)
            throw new InvalidOperationException("External Agent Runtime limits must be positive and ordered.");
    }

    private static IReadOnlyList<ExternalAgentToolDefinition> ToolDefinitions(ExternalToolSession session)
    {
        var tools = new List<ExternalAgentToolDefinition> { ShellToolDefinition() };
        if (session.PermissionMode != ExternalToolPermissionMode.ReadOnly && session.AllowedWriteScope.Count > 0) tools.Add(ApplyPatchToolDefinition());
        return tools;
    }

    private static ExternalAgentToolDefinition ShellToolDefinition()
    {
        using var document = JsonDocument.Parse("""
            { "type": "object", "properties": { "command": { "type": "string" }, "cwd": { "type": "string" }, "timeout": { "type": "integer", "minimum": 1, "maximum": 300 } }, "required": ["command"], "additionalProperties": false }
            """);
        return new ExternalAgentToolDefinition("shell", document.RootElement.Clone(), "Execute one PowerShell command in the task session and return bounded output.");
    }

    private static ExternalAgentToolDefinition ApplyPatchToolDefinition()
    {
        using var document = JsonDocument.Parse("""
            { "type": "object", "properties": { "patch": { "type": "string" } }, "required": ["patch"], "additionalProperties": false }
            """);
        return new ExternalAgentToolDefinition("apply_patch", document.RootElement.Clone(), "Apply one scoped text unified patch and return changedFiles.");
    }

    private static string SerializeToolResult(ExternalToolExecutionResult result) => JsonSerializer.Serialize(new
    {
        stdout = Bound(result.StandardOutput, MaximumToolContextCharacters),
        stderr = Bound(result.StandardError, MaximumToolContextCharacters),
        exitCode = result.ExitCode,
        timedOut = result.TimedOut,
        denied = result.Denied,
        truncated = result.Truncated,
        changedFiles = result.ChangedFiles ?? [],
    });

    private static string Bound(string? value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max] + "\n[truncated]";

    private void TrimNoProgressObservations(Queue<NoProgressObservation> observations, DateTimeOffset now)
    {
        while (observations.Count > options.NoProgressWindowSize
            || (observations.TryPeek(out var oldest) && now - oldest.OccurredAt > options.NoProgressWindow))
        {
            observations.Dequeue();
        }
    }

    private bool IsNoProgressPattern(
        IEnumerable<NoProgressObservation> observations,
        string normalizedCall,
        string outcome,
        string resultHash) => observations.Count(item =>
            string.Equals(item.NormalizedCall, normalizedCall, StringComparison.Ordinal)
            && SimilarOutcome(item.Outcome, outcome)
            && (string.Equals(item.ResultHash, resultHash, StringComparison.Ordinal) || outcome != "success")) >= options.MaxRepeatedIdenticalToolCalls;

    private static bool IsObservableResultProgress(string normalizedCall, string resultHash, IEnumerable<NoProgressObservation> observations)
    {
        var previous = observations.LastOrDefault(item => string.Equals(item.NormalizedCall, normalizedCall, StringComparison.Ordinal));
        return previous is not null && !string.Equals(previous.ResultHash, resultHash, StringComparison.Ordinal);
    }

    private static bool SimilarOutcome(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || (left.StartsWith("failure", StringComparison.Ordinal) && right.StartsWith("failure", StringComparison.Ordinal))
        || (left == "denied" && right == "denied")
        || (left == "timeout" && right == "timeout");

    private static string Outcome(ExternalToolExecutionResult result) =>
        result.Denied ? "denied" : result.TimedOut ? "timeout" : result.Succeeded ? "success" : $"failure:{result.ExitCode?.ToString() ?? "unknown"}";

    private static string HashResult(ExternalToolExecutionResult result) => Hash(string.Join("\n", Outcome(result), result.ExitCode?.ToString() ?? string.Empty, result.StandardOutput ?? string.Empty, result.StandardError ?? string.Empty));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Summarize(string? value) => Bound(value, MaximumTelemetrySummaryCharacters).Replace("\r", " ").Replace("\n", " ");

    private static string? DetectValidationDelta(ExternalAgentToolCall call, ExternalToolExecutionResult result)
    {
        if (!string.Equals(call.Name, "shell", StringComparison.OrdinalIgnoreCase)) return null;
        var normalized = NormalizeArguments(call.Arguments).ToLowerInvariant();
        var validationCommand = normalized.Contains("dotnet test", StringComparison.Ordinal)
            || normalized.Contains("pytest", StringComparison.Ordinal)
            || normalized.Contains("npm test", StringComparison.Ordinal)
            || normalized.Contains("pnpm test", StringComparison.Ordinal)
            || normalized.Contains("yarn test", StringComparison.Ordinal)
            || normalized.Contains("cargo test", StringComparison.Ordinal)
            || normalized.Contains("mvn test", StringComparison.Ordinal)
            || normalized.Contains("gradle test", StringComparison.Ordinal);
        return validationCommand ? (result.Succeeded ? "passed" : "failed") : null;
    }

    private static string? BuildRecentFailureSummary(IReadOnlyList<ExternalToolActivity> activity, string? hardReason)
    {
        var lastFailure = activity.LastOrDefault(item => !item.Succeeded);
        if (lastFailure is null) return hardReason;
        var detail = string.IsNullOrWhiteSpace(lastFailure.StandardErrorSummary)
            ? lastFailure.StandardOutputSummary
            : lastFailure.StandardErrorSummary;
        var summary = $"{hardReason ?? lastFailure.Outcome}: {lastFailure.ToolName}: {detail}".Trim();
        return summary.Length <= 320 ? summary : summary[..320];
    }

    private static string NormalizeToolCall(string name, string arguments)
        => name.Trim().ToLowerInvariant() + "\n" + NormalizeArguments(arguments);

    private static string NormalizeArguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return CanonicalJson(document.RootElement);
        }
        catch (JsonException)
        {
            return string.Join(' ', arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static string CanonicalJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
            _ => element.GetRawText(),
        };
    }

    private sealed record NoProgressObservation(string NormalizedCall, string Outcome, string ResultHash, DateTimeOffset OccurredAt);

    private static ProviderUsage? AddUsage(ProviderUsage? left, ProviderUsage? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return new ProviderUsage(Add(left.InputTokens, right.InputTokens), Add(left.OutputTokens, right.OutputTokens), Add(left.TotalTokens, right.TotalTokens));
    }

    private static long? Add(long? left, long? right) => left is null && right is null ? null : (left ?? 0) + (right ?? 0);
}
