using System.Text.Json;

namespace CodexAgentSwitch.Domain.ExternalAgents;

/// <summary>Roles supported by an OpenAI-compatible chat completion.</summary>
public enum ExternalAgentMessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>A function exposed to the model as a structured tool.</summary>
public sealed record ExternalAgentToolDefinition(
    string Name,
    JsonElement Parameters,
    string? Description = null)
{
    public ExternalAgentToolDefinition(string name, string description, JsonElement parameters)
        : this(name, parameters, description)
    {
    }
}

/// <summary>A function invocation returned by the model.</summary>
public sealed record ExternalAgentToolCall(
    string Id,
    string Name,
    string Arguments);

/// <summary>A chat message, including assistant tool calls and tool results.</summary>
public sealed record ExternalAgentMessage(
    ExternalAgentMessageRole Role,
    string? Content = null,
    IReadOnlyList<ExternalAgentToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null)
{
    public static ExternalAgentMessage System(string content) => new(ExternalAgentMessageRole.System, content);

    public static ExternalAgentMessage User(string content) => new(ExternalAgentMessageRole.User, content);

    public static ExternalAgentMessage Assistant(
        string? content = null,
        IReadOnlyList<ExternalAgentToolCall>? toolCalls = null) =>
        new(ExternalAgentMessageRole.Assistant, content, toolCalls);

    public static ExternalAgentMessage Tool(string toolCallId, string content, string? name = null) =>
        new(ExternalAgentMessageRole.Tool, content, null, toolCallId, name);

    public static ExternalAgentMessage ToolResult(string toolCallId, string content, string? name = null) =>
        Tool(toolCallId, content, name);
}

public sealed record ExternalAgentChatRequest(
    string ModelId,
    IReadOnlyList<ExternalAgentMessage> Messages,
    IReadOnlyList<ExternalAgentToolDefinition>? Tools = null);

/// <summary>Structured result for one provider chat-completion turn.</summary>
public sealed record ExternalAgentCompletion(
    string? Content,
    IReadOnlyList<ExternalAgentToolCall> ToolCalls,
    string? ResponseModel,
    Providers.ProviderUsage? Usage,
    Uri RequestUri,
    JsonElement RawResponse,
    string? FinishReason);
