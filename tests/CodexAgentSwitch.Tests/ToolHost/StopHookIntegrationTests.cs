using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexAgentSwitch.Tests.ToolHost;

public sealed class StopHookIntegrationTests
{
    [Fact]
    public async Task Scheduler_success_returns_supported_stop_response()
    {
        var root = CreateTestDirectory("success");
        var pipeName = "cas-stop-success-" + Guid.NewGuid().ToString("N");
        try
        {
            string? requestLine = null;
            var server = RunServerAsync(pipeName, line =>
            {
                requestLine = line;
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    result = new
                    {
                        threadId = "session-success",
                        bindingAccepted = true,
                        telemetryAvailable = true,
                        state = 0,
                        compactionRequested = false,
                        compactionSucceeded = false,
                        reason = "Observed.",
                    },
                });
            });

            var result = await RunToolHostAsync(pipeName, root,
                "{\"session_id\":\"session-success\",\"cwd\":\"E:\\\\AISPace\\\\hook-success\"}");
            await server;

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"hookEventName\":\"Stop\"", result.StandardOutput);
            using var request = JsonDocument.Parse(requestLine!);
            Assert.Equal("mainContextBoundary", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("session-success", request.RootElement.GetProperty("payload").GetProperty("threadId").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Scheduler_failure_is_diagnosed_and_stop_remains_fail_open()
    {
        var root = CreateTestDirectory("failure");
        var pipeName = "cas-stop-failure-" + Guid.NewGuid().ToString("N");
        try
        {
            var server = RunServerAsync(pipeName, _ => JsonSerializer.Serialize(new
            {
                ok = false,
                error = "simulated scheduler failure",
            }));

            var result = await RunToolHostAsync(pipeName, root,
                "{\"session_id\":\"session-failure\",\"cwd\":\"E:\\\\AISPace\\\\hook-failure\",\"prompt\":\"TOP-SECRET-PROMPT\"}");
            await server;

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"hookEventName\":\"Stop\"", result.StandardOutput);
            var path = Path.Combine(root, "logs", "context-economy-stop-hook.jsonl");
            var diagnostic = File.ReadAllText(path);
            Assert.Contains("\"Hook\":\"Stop\"", diagnostic);
            Assert.Contains("\"SessionId\":\"session-failure\"", diagnostic);
            Assert.Contains("\"PipeName\":\"" + pipeName + "\"", diagnostic);
            Assert.Contains("\"Stage\":\"scheduler-send\"", diagnostic);
            Assert.Contains("\"ExceptionType\":\"InvalidOperationException\"", diagnostic);
            Assert.Contains("simulated scheduler failure", diagnostic);
            Assert.DoesNotContain("TOP-SECRET-PROMPT", diagnostic);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RunServerAsync(string pipeName, Func<string, string> respond)
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var request = await reader.ReadLineAsync() ?? throw new IOException("ToolHost did not send a request.");
        await writer.WriteLineAsync(respond(request));
    }

    private static async Task<ProcessResult> RunToolHostAsync(string pipeName, string dataRoot, string input)
    {
        var executable = FindToolHostExecutable();
        var start = new ProcessStartInfo(executable)
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
        return Directory.EnumerateFiles(bin, "CodexAgentSwitch.ToolHost.exe", SearchOption.AllDirectories)
            .Single();
    }

    private static string CreateTestDirectory(string name)
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"stop-hook-{name}-{Guid.NewGuid():N}");
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
