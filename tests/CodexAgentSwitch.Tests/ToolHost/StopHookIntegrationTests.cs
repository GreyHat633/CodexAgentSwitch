using System.Diagnostics;
using System.IO.Pipes;

namespace CodexAgentSwitch.Tests.ToolHost;

public sealed class StopHookIntegrationTests
{
    [Fact]
    public async Task Historical_stop_hook_is_fail_open_without_connecting_scheduler_or_writing_diagnostics()
    {
        var root = CreateTestDirectory();
        var pipeName = "cas-stop-frozen-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var waitCancellation = new CancellationTokenSource();
            var connection = pipe.WaitForConnectionAsync(waitCancellation.Token);

            var result = await RunToolHostAsync(pipeName, root,
                "{\"session_id\":\"session-frozen\",\"cwd\":\"E:\\\\AISPace\\\\hook-frozen\",\"prompt\":\"TOP-SECRET-PROMPT\"}");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"hookEventName\":\"Stop\"", result.StandardOutput, StringComparison.Ordinal);
            Assert.False(pipe.IsConnected);
            Assert.False(File.Exists(Path.Combine(root, "logs", "context-economy-stop-hook.jsonl")));
            waitCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connection);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<ProcessResult> RunToolHostAsync(string pipeName, string dataRoot, string input)
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
        start.ArgumentList.Add("stop");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.Environment["CAS_DATA_ROOT"] = dataRoot;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start ToolHost.");
        await process.StandardInput.WriteLineAsync(input);
        process.StandardInput.Close();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string FindToolHostExecutable()
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var bin = Path.Combine(root, "src", "CodexAgentSwitch.ToolHost", "bin", configuration);
        return Directory.EnumerateFiles(bin, "CodexAgentSwitch.ToolHost.exe", SearchOption.AllDirectories).Single();
    }

    private static string CreateTestDirectory()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"stop-hook-frozen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CodexAgentSwitch.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Unable to locate CodexAgentSwitch.sln.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
