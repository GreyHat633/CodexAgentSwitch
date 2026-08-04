namespace CodexAgentSwitch.Infrastructure.Common;

public sealed record AppDataPaths(string Root)
{
    public string DatabasePath => Path.Combine(Root, "codex-agent-switch.db");

    public string ProtocolCacheDirectory => Path.Combine(Root, "protocol-cache");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string NativeCodexDirectory => Path.Combine(Root, "native-codex");

    public static AppDataPaths Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("CAS_DATA_ROOT");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.GetFullPath(configured);
        return new AppDataPaths(root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProtocolCacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(NativeCodexDirectory);
    }
}
