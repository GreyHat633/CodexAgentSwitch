using System.Text.Json;
using CodexAgentSwitch.Domain.ExternalAgents;
using CodexAgentSwitch.Infrastructure.ExternalProviders;

namespace CodexAgentSwitch.Tests.ExternalProviders;

public sealed class OpenAiCompatibleToolCallCodecTests
{
    [Fact]
    public void Request_serializes_all_message_roles_tools_and_tool_call_id()
    {
        using var parameters = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}");
        var request = new ExternalAgentChatRequest(
            "model-1",
            [
                ExternalAgentMessage.System("You are concise."),
                ExternalAgentMessage.User("Read the file."),
                ExternalAgentMessage.Assistant(toolCalls: [new("call-1", "read_file", "{\"path\":\"a.txt\"}")]),
                ExternalAgentMessage.Tool("call-1", "contents", "read_file"),
            ],
            [new ExternalAgentToolDefinition("read_file", parameters.RootElement.Clone(), "Read a file")]);

        using var document = JsonDocument.Parse(OpenAiCompatibleToolCallCodec.SerializeRequest(request));
        var root = document.RootElement;
        Assert.Equal("model-1", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("call-1", root.GetProperty("messages")[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call-1", root.GetProperty("messages")[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("read_file", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public void Response_parses_text_and_multiple_tool_calls_with_metadata()
    {
        using var document = JsonDocument.Parse("""
            {"model":"model-response","choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[
              {"id":"call-1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}},
              {"id":"call-2","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"b.txt\"}"}}
            ]}}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
            """);

        var result = OpenAiCompatibleToolCallCodec.ParseResponse(document.RootElement, new Uri("https://provider.test/v1/chat/completions"));

        Assert.Null(result.Content);
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal("call-2", result.ToolCalls[1].Id);
        Assert.Equal("model-response", result.ResponseModel);
        Assert.Equal(5, result.Usage?.TotalTokens);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal("model-response", result.RawResponse.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"missing-function\"}]}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"call-1\",\"type\":\"custom\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{}\"}}]}}]}")]
    [InlineData("{\"choices\":[{\"message\":{}}]}")]
    public void Missing_or_malformed_fields_raise_explicit_protocol_error(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<OpenAiCompatibleProtocolException>(() =>
            OpenAiCompatibleToolCallCodec.ParseResponse(document.RootElement, new Uri("https://provider.test/chat/completions")));

        Assert.NotEmpty(exception.Message);
    }
}
