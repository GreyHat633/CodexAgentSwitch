namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed record CodexCommand(string Executable, IReadOnlyList<string> PrefixArguments)
{
    public static CodexCommand Direct(string executable) => new(executable, []);
}
