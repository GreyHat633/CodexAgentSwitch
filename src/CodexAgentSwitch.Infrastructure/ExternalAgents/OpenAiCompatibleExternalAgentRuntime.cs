using System.Diagnostics;
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
    IReadOnlyList<string> ChangedFiles);

public sealed class OpenAiCompatibleExternalAgentRuntime(
    OpenAiCompatibleClient client,
    IExternalToolHost toolHost,
    ExternalAgentRuntimeOptions? options = null)
{
    private const int MaximumToolContextCharacters = 12_000;
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
        var repeatedCalls = new Dictionary<string, int>(StringComparer.Ordinal);
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
                foreach (var call in completion.ToolCalls)
                {
                    var key = $"{call.Name}\n{call.Arguments}";
                    repeatedCalls.TryGetValue(key, out var repeated);
                    repeated++;
                    repeatedCalls[key] = repeated;
                    if (repeated > options.MaxRepeatedIdenticalToolCalls)
                    {
                        hardReason = "repeated-identical-tool-call";
                        state = ExternalAgentRuntimeState.Blocked;
                        break;
                    }

                    toolCalls++;
                    var execution = await toolHost.ExecuteAsync(
                        session,
                        new ExternalToolExecutionRequest(call.Id, call.Name, call.Arguments),
                        wallClock.Token);
                    if (!execution.Succeeded) failedToolCalls++;
                    if (execution.Denied) deniedToolCalls++;
                    if (execution.ChangedFiles is not null) changedFiles.UnionWith(execution.ChangedFiles);
                    activity.Add(new ExternalToolActivity(
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
                        execution.ChangedFiles ?? []));
                    messages.Add(ExternalAgentMessage.ToolResult(call.Id, SerializeToolResult(execution), call.Name));
                }
                if (hardReason is not null) break;
            }

            if (state == ExternalAgentRuntimeState.Blocked && hardReason is not null)
            {
                (content, finalizationAttempted, finalizationSucceeded) = await TryFinalizeAsync();
                if (!finalizationSucceeded)
                {
                    risks.Add(hardReason == "repeated-identical-tool-call"
                        ? "External Agent Runtime detected a repeated identical tool call loop."
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
            || options.MaxRepeatedIdenticalToolCalls <= 0 || options.EffectiveMaxWallClock <= TimeSpan.Zero)
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

    private static ProviderUsage? AddUsage(ProviderUsage? left, ProviderUsage? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return new ProviderUsage(Add(left.InputTokens, right.InputTokens), Add(left.OutputTokens, right.OutputTokens), Add(left.TotalTokens, right.TotalTokens));
    }

    private static long? Add(long? left, long? right) => left is null && right is null ? null : (left ?? 0) + (right ?? 0);
}
