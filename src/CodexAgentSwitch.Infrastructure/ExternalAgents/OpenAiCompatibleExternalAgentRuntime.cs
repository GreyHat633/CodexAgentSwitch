using System.Diagnostics;
using System.Text.Json;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Domain.ExternalAgents;
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

public sealed record ExternalAgentRuntimeOptions(
    int MaxProviderTurns = 12,
    int MaxToolCalls = 24,
    int MaxRepeatedIdenticalToolCalls = 3,
    TimeSpan? MaxWallClock = null)
{
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
    JsonElement? RawResponse);

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
    private readonly ExternalAgentRuntimeOptions options = options ?? new ExternalAgentRuntimeOptions();

    public async Task<ExternalAgentRuntimeResult> ExecuteAsync(
        ProviderConfiguration provider,
        string modelId,
        string prompt,
        ExternalToolSession session,
        CancellationToken cancellationToken = default)
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
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activity = new List<ExternalToolActivity>();

        try
        {
            while (turns < options.MaxProviderTurns)
            {
                var completion = await client.CompleteWithToolsAsync(provider, modelId, messages, tools, wallClock.Token);
                turns++;
                totalUsage = AddUsage(totalUsage, completion.Usage);
                lastRaw = completion.RawResponse;
                if (completion.ToolCalls.Count == 0)
                {
                    return Result(ExternalAgentRuntimeState.Completed, completion.Content, []);
                }

                if (toolCalls + completion.ToolCalls.Count > options.MaxToolCalls)
                {
                    return Result(ExternalAgentRuntimeState.Blocked, null, ["External Agent Runtime reached MaxToolCalls."]);
                }

                messages.Add(ExternalAgentMessage.Assistant(completion.Content, completion.ToolCalls));
                foreach (var call in completion.ToolCalls)
                {
                    var key = $"{call.Name}\n{call.Arguments}";
                    repeatedCalls.TryGetValue(key, out var repeated);
                    repeated++;
                    repeatedCalls[key] = repeated;
                    if (repeated > options.MaxRepeatedIdenticalToolCalls)
                    {
                        return Result(ExternalAgentRuntimeState.Blocked, null, ["External Agent Runtime detected a repeated identical tool call loop."]);
                    }

                    toolCalls++;
                    var execution = await toolHost.ExecuteAsync(
                        session,
                        new ExternalToolExecutionRequest(call.Id, call.Name, call.Arguments),
                        wallClock.Token);
                    if (!execution.Succeeded)
                    {
                        failedToolCalls++;
                    }
                    if (execution.Denied)
                    {
                        deniedToolCalls++;
                    }
                    if (execution.ChangedFiles is not null)
                    {
                        changedFiles.UnionWith(execution.ChangedFiles);
                    }
                    activity.Add(new ExternalToolActivity(
                        toolCalls,
                        call.Id,
                        call.Name,
                        call.Arguments,
                        execution.Succeeded,
                        execution.Denied,
                        execution.TimedOut,
                        execution.ExitCode,
                        execution.StandardOutput,
                        execution.StandardError,
                        execution.ChangedFiles ?? []));

                    messages.Add(ExternalAgentMessage.ToolResult(call.Id, SerializeToolResult(execution), call.Name));
                }
            }

            return Result(ExternalAgentRuntimeState.Blocked, null, ["External Agent Runtime reached MaxProviderTurns."]);
        }
        catch (ProviderRequestException exception) when (
            exception.Kind == ProviderErrorKind.Cancelled
            && wallClock.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Result(ExternalAgentRuntimeState.Timeout, null, ["External Agent Runtime reached MaxWallClock."]);
        }
        catch (ProviderRequestException exception) when (
            exception.Kind == ProviderErrorKind.Cancelled
            && cancellationToken.IsCancellationRequested)
        {
            return Result(ExternalAgentRuntimeState.Cancelled, null, ["External Agent Runtime was cancelled."]);
        }
        catch (OperationCanceledException) when (wallClock.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Result(ExternalAgentRuntimeState.Timeout, null, ["External Agent Runtime reached MaxWallClock."]);
        }
        catch (OperationCanceledException)
        {
            return Result(ExternalAgentRuntimeState.Cancelled, null, ["External Agent Runtime was cancelled."]);
        }

        ExternalAgentRuntimeResult Result(ExternalAgentRuntimeState state, string? content, IReadOnlyList<string> risks) => new(
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
            lastRaw);
    }

    private void ValidateOptions()
    {
        if (options.MaxProviderTurns <= 0
            || options.MaxToolCalls <= 0
            || options.MaxRepeatedIdenticalToolCalls <= 0
            || options.EffectiveMaxWallClock <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("External Agent Runtime limits must be positive.");
        }
    }

    private static IReadOnlyList<ExternalAgentToolDefinition> ToolDefinitions(ExternalToolSession session)
    {
        var tools = new List<ExternalAgentToolDefinition> { ShellToolDefinition() };
        if (session.PermissionMode != ExternalToolPermissionMode.ReadOnly && session.AllowedWriteScope.Count > 0)
        {
            tools.Add(ApplyPatchToolDefinition());
        }

        return tools;
    }

    private static ExternalAgentToolDefinition ShellToolDefinition()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "command": { "type": "string" },
                "cwd": { "type": "string" },
                "timeout": { "type": "integer", "minimum": 1, "maximum": 300 }
              },
              "required": ["command"],
              "additionalProperties": false
            }
            """);
        return new ExternalAgentToolDefinition(
            "shell",
            document.RootElement.Clone(),
            "Execute one PowerShell command in the task session and return stdout, stderr, exitCode, timeout and truncation metadata.");
    }

    private static ExternalAgentToolDefinition ApplyPatchToolDefinition()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "patch": { "type": "string" }
              },
              "required": ["patch"],
              "additionalProperties": false
            }
            """);
        return new ExternalAgentToolDefinition(
            "apply_patch",
            document.RootElement.Clone(),
            "Apply one scoped text unified patch after Agent Switch validates ProjectPath and AllowedWriteScope. Returns changedFiles.");
    }

    private static string SerializeToolResult(ExternalToolExecutionResult result) => JsonSerializer.Serialize(new
    {
        stdout = result.StandardOutput,
        stderr = result.StandardError,
        exitCode = result.ExitCode,
        timedOut = result.TimedOut,
        denied = result.Denied,
        truncated = result.Truncated,
        changedFiles = result.ChangedFiles ?? [],
    });

    private static ProviderUsage? AddUsage(ProviderUsage? left, ProviderUsage? right)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }

        return new ProviderUsage(
            Add(left.InputTokens, right.InputTokens),
            Add(left.OutputTokens, right.OutputTokens),
            Add(left.TotalTokens, right.TotalTokens));
    }

    private static long? Add(long? left, long? right) => left is null && right is null ? null : (left ?? 0) + (right ?? 0);
}
