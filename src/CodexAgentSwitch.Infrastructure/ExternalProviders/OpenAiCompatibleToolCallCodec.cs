using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAgentSwitch.Domain.ExternalAgents;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

/// <summary>Protocol error raised when an OpenAI-compatible tool payload is malformed.</summary>
public sealed class OpenAiCompatibleProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Serialization and response parsing for structured chat tool calls.</summary>
public static class OpenAiCompatibleToolCallCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement SerializeMessage(ExternalAgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMessage(message);
        var node = new JsonObject
        {
            ["role"] = RoleName(message.Role),
        };
        if (message.Content is not null)
        {
            node["content"] = message.Content;
        }

        if (message.Role == ExternalAgentMessageRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            node["tool_calls"] = new JsonArray(message.ToolCalls.Select(SerializeToolCallNode).ToArray());
        }

        if (message.Role == ExternalAgentMessageRole.Tool)
        {
            node["tool_call_id"] = message.ToolCallId;
            if (message.Name is not null)
            {
                node["name"] = message.Name;
            }
        }

        return JsonSerializer.SerializeToElement(node, JsonOptions);
    }

    public static JsonElement SerializeToolDefinition(ExternalAgentToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new OpenAiCompatibleProtocolException("Tool definition name is required.");
        }

        if (definition.Parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new OpenAiCompatibleProtocolException($"Tool '{definition.Name}' parameters are required.");
        }

        var function = new JsonObject
        {
            ["name"] = definition.Name,
            ["parameters"] = JsonNode.Parse(definition.Parameters.GetRawText()),
        };
        if (definition.Description is not null)
        {
            function["description"] = definition.Description;
        }

        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "function",
            ["function"] = function,
        }, JsonOptions);
    }

    public static string SerializeRequest(ExternalAgentChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new OpenAiCompatibleProtocolException("Model ID is required.");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new OpenAiCompatibleProtocolException("At least one chat message is required.");
        }

        var messages = new JsonArray(request.Messages.Select(message =>
            JsonNode.Parse(SerializeMessage(message).GetRawText())!).ToArray());
        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = false,
        };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JsonArray(request.Tools.Select(tool =>
                JsonNode.Parse(SerializeToolDefinition(tool).GetRawText())!).ToArray());
        }

        return payload.ToJsonString(JsonOptions);
    }

    public static string SerializeRequest(
        string modelId,
        IReadOnlyList<ExternalAgentMessage> messages,
        IReadOnlyList<ExternalAgentToolDefinition>? tools = null) =>
        SerializeRequest(new ExternalAgentChatRequest(modelId, messages, tools));

    public static ExternalAgentCompletion ParseResponse(JsonElement root, Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
        {
            throw new OpenAiCompatibleProtocolException("Provider response is missing choices[0].message.");
        }

        string? content = null;
        if (message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind is not JsonValueKind.Null)
        {
            if (contentElement.ValueKind != JsonValueKind.String)
            {
                throw new OpenAiCompatibleProtocolException("choices[0].message.content must be a string or null.");
            }

            content = contentElement.GetString();
        }

        var toolCalls = ParseToolCalls(message);
        if (content is null && toolCalls.Count == 0)
        {
            throw new OpenAiCompatibleProtocolException("Provider response must contain message.content or message.tool_calls.");
        }

        string? responseModel = null;
        if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind is not JsonValueKind.Null)
        {
            if (modelElement.ValueKind != JsonValueKind.String)
            {
                throw new OpenAiCompatibleProtocolException("Provider response model must be a string.");
            }

            responseModel = modelElement.GetString();
        }

        string? finishReason = null;
        if (choices[0].TryGetProperty("finish_reason", out var finishElement)
            && finishElement.ValueKind is not JsonValueKind.Null)
        {
            if (finishElement.ValueKind != JsonValueKind.String)
            {
                throw new OpenAiCompatibleProtocolException("choices[0].finish_reason must be a string or null.");
            }

            finishReason = finishElement.GetString();
        }

        return new ExternalAgentCompletion(
            content,
            toolCalls,
            responseModel,
            ParseUsage(root),
            requestUri,
            root.Clone(),
            finishReason);
    }

    public static ExternalAgentCompletion ParseResponse(string json, Uri requestUri)
    {
        using var document = JsonDocument.Parse(json);
        return ParseResponse(document.RootElement, requestUri);
    }

    private static IReadOnlyList<ExternalAgentToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var callsElement)
            || callsElement.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (callsElement.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiCompatibleProtocolException("message.tool_calls must be an array.");
        }

        var calls = new List<ExternalAgentToolCall>(callsElement.GetArrayLength());
        foreach (var call in callsElement.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object
                || !TryRequiredString(call, "id", out var id)
                || !TryRequiredString(call, "type", out var type)
                || !string.Equals(type, "function", StringComparison.Ordinal)
                || !call.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object
                || !TryRequiredString(function, "name", out var name)
                || !TryRequiredString(function, "arguments", out var arguments))
            {
                throw new OpenAiCompatibleProtocolException("Each tool call requires id, type=function, and function.name/function.arguments strings.");
            }

            calls.Add(new ExternalAgentToolCall(id, name, arguments));
        }

        return calls;
    }

    private static JsonNode SerializeToolCallNode(ExternalAgentToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
        {
            throw new OpenAiCompatibleProtocolException("Tool call id and name are required.");
        }

        if (string.IsNullOrWhiteSpace(call.Arguments))
        {
            throw new OpenAiCompatibleProtocolException($"Tool call '{call.Id}' arguments are required.");
        }

        return new JsonObject
        {
            ["id"] = call.Id,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = call.Name,
                ["arguments"] = call.Arguments,
            },
        };
    }

    private static void ValidateMessage(ExternalAgentMessage message)
    {
        if (message.Role is ExternalAgentMessageRole.System or ExternalAgentMessageRole.User
            && message.Content is null)
        {
            throw new OpenAiCompatibleProtocolException($"{message.Role} messages require content.");
        }

        if (message.Role == ExternalAgentMessageRole.Tool && string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            throw new OpenAiCompatibleProtocolException("Tool messages require tool_call_id.");
        }

        if (message.Role == ExternalAgentMessageRole.Tool && message.Content is null)
        {
            throw new OpenAiCompatibleProtocolException("Tool messages require content.");
        }

        if (message.Role != ExternalAgentMessageRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            throw new OpenAiCompatibleProtocolException("Only assistant messages may contain tool_calls.");
        }

        if (message.Role == ExternalAgentMessageRole.Assistant
            && message.Content is null
            && (message.ToolCalls is null || message.ToolCalls.Count == 0))
        {
            throw new OpenAiCompatibleProtocolException("Assistant messages require content or tool_calls.");
        }
    }

    private static string RoleName(ExternalAgentMessageRole role) => role switch
    {
        ExternalAgentMessageRole.System => "system",
        ExternalAgentMessageRole.User => "user",
        ExternalAgentMessageRole.Assistant => "assistant",
        ExternalAgentMessageRole.Tool => "tool",
        _ => throw new OpenAiCompatibleProtocolException($"Unsupported message role: {role}."),
    };

    private static bool TryRequiredString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static ProviderUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ProviderUsage(
            ReadInt64(usage, "prompt_tokens", "input_tokens"),
            ReadInt64(usage, "completion_tokens", "output_tokens"),
            ReadInt64(usage, "total_tokens"));
    }

    private static long? ReadInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number))
            {
                return number;
            }
        }

        return null;
    }
}
