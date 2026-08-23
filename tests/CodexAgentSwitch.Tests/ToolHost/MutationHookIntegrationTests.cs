using System.Diagnostics;
using System.IO.Pipes;

namespace CodexAgentSwitch.Tests.ToolHost;

public sealed class MutationHookIntegrationTests
{
    [Theory]
    [InlineData("pre-tool-use", "PreToolUse")]
    [InlineData("post-tool-use", "PostToolUse")]
    public async Task Historical_mutation_hook_is_no_op_and_never_connects_scheduler(string hook, string eventName)
    {
        var pipeName = "cas-mutation-frozen-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var waitCancellation = new CancellationTokenSource();
        var connection = pipe.WaitForConnectionAsync(waitCancellation.Token);

        var result = await RunToolHostAsync(hook, pipeName,
            "{\"session_id\":\"session-1\",\"agent_type\":\"main_turn\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"A.cs\"}}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"hookEventName\":\"{eventName}\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(pipe.IsConnected);
        waitCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection);
    }

    private static async Task<ProcessResult> RunToolHostAsync(string hook, string pipeName, string input)
    {
        var start = new ProcessStartInfo(FindToolHostExecutable())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--hook");
        start.ArgumentList.Add(hook);
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start ToolHost.");
        await process.StandardInput.WriteLineAsync(input);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return new(process.ExitCode, output, error);
    }

    private static string FindToolHostExecutable()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var bin = Path.Combine(root, "src", "CodexAgentSwitch.ToolHost", "bin", configuration);
        return Directory.EnumerateFiles(bin, "CodexAgentSwitch.ToolHost.exe", SearchOption.AllDirectories).Single();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CodexAgentSwitch.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Unable to locate CodexAgentSwitch.sln.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
