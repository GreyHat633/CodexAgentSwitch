namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class JsonRpcException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}
