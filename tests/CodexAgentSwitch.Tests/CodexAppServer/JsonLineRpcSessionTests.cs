using System.Text;
using CodexAgentSwitch.Infrastructure.CodexAppServer;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class JsonLineRpcSessionTests
{
    [Fact]
    public async Task Request_is_json_line_and_response_is_correlated()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"ok\":true}}\n"));
        var output = new MemoryStream();
        await using var session = new JsonLineRpcSession(input, output);

        var result = await session.SendRequestAsync("model/list", new { limit = 10 });

        Assert.True(result.GetProperty("ok").GetBoolean());
        output.Position = 0;
        var request = await new StreamReader(output).ReadToEndAsync();
        Assert.Contains("\"method\":\"model/list\"", request, StringComparison.Ordinal);
        Assert.Contains("\"id\":1", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rpc_error_preserves_code_without_logging_payload()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1,\"error\":{\"code\":-32000,\"message\":\"denied\"}}\n"));
        var output = new MemoryStream();
        await using var session = new JsonLineRpcSession(input, output);

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => session.SendRequestAsync("thread/start", new { }));

        Assert.Equal(-32000, exception.Code);
        Assert.Equal("denied", exception.Message);
    }
}
