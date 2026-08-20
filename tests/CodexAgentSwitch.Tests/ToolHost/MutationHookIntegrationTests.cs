using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.Common;
using CodexAgentSwitch.Infrastructure.Scheduling;

namespace CodexAgentSwitch.Tests.ToolHost;

public sealed class MutationHookIntegrationTests
{
    [Fact]
    public async Task PreToolUse_scheduler_failure_is_fail_open()
    {
        var pipeName = "cas-pre-fail-open-" + Guid.NewGuid().ToString("N");
        var server = RunServerAsync(pipeName, _ => JsonSerializer.Serialize(new { ok = false, error = "fixture failure" }));

        var result = await RunToolHostAsync("pre-tool-use", pipeName,
            "{\"session_id\":\"session-1\",\"cwd\":\"E:\\\\AISPace\\\\Hook\",\"agent_type\":\"main_turn\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"A.cs\"}}");
        await server;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"hookEventName\":\"PreToolUse\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("permissionDecision", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreToolUse_explicit_enforcement_denial_uses_supported_deny_shape()
    {
        var pipeName = "cas-pre-deny-" + Guid.NewGuid().ToString("N");
        var server = RunServerAsync(pipeName, _ => JsonSerializer.Serialize(new
        {
            ok = true,
            result = new { allowed = false, denied = true, reason = "exact touched path" },
        }));

        var result = await RunToolHostAsync("pre-tool-use", pipeName,
            "{\"session_id\":\"session-1\",\"cwd\":\"E:\\\\AISPace\\\\Hook\",\"agent_type\":\"main_turn\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"A.cs\"}}");
        await server;

        Assert.Contains("\"permissionDecision\":\"deny\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("exact touched path", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostToolUse_forwards_exact_actor_and_mutation_payload()
    {
        var pipeName = "cas-post-forward-" + Guid.NewGuid().ToString("N");
        string? requestLine = null;
        var server = RunServerAsync(pipeName, line =>
        {
            requestLine = line;
            return JsonSerializer.Serialize(new { ok = true, result = new { recorded = true } });
        });

        var result = await RunToolHostAsync("post-tool-use", pipeName,
            "{\"session_id\":\"session-1\",\"turn_id\":\"turn-1\",\"agent_id\":\"agent-1\",\"agent_type\":\"delegated_subagent\",\"tool_use_id\":\"tool-1\",\"cwd\":\"E:\\\\AISPace\\\\Hook\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"A.cs\"},\"tool_response\":{\"success\":true}}");
        await server;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"hookEventName\":\"PostToolUse\"", result.StandardOutput, StringComparison.Ordinal);
        using var request = JsonDocument.Parse(requestLine!);
        Assert.Equal("postToolUse", request.RootElement.GetProperty("method").GetString());
        var payload = request.RootElement.GetProperty("payload");
        Assert.Equal("delegated_subagent", payload.GetProperty("agentType").GetString());
        Assert.Equal("agent-1", payload.GetProperty("agentId").GetString());
        Assert.Contains("file_path", payload.GetProperty("toolInput").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_toolhost_pipe_and_scheduler_chain_records_shadow_conflict_and_clears_at_terminal()
    {
        var pipeName = "cas-shadow-chain-" + Guid.NewGuid().ToString("N");
        const string root = "E:\\AISPace\\HookShadowChain";
        const string taskId = "CAS-HOOK-SHADOW-CHAIN";
        var tasks = new MemoryTaskRepository();
        var leases = new MemoryLeaseRepository();
        await using var scheduler = new WorkerScheduler(
            [new NativeWorkerExecutor()], tasks, new SystemClock(), leaseRepository: leases);
        await scheduler.StartAsync();
        await scheduler.RecordRepartitionAsync(taskId, RepartitionTrigger.ARCHITECTURE_RESOLVED,
            WorkOwner.Worker, RepartitionReasonCode.BOUNDED_IMPLEMENTATION, "shadow chain", "native-luna", null,
            taskId, root, "Implementation", [root], null);
        var packet = new TaskPacket(taskId, "project-shadow", root, "native-luna", "exercise shadow chain",
            ["src/Target.cs"], [root], [root], ["shadow event recorded"], ["no model call"], "Return result.");
        await scheduler.DispatchAsync(packet);
        await using var ipc = new SchedulerIpcServer(scheduler, pipeName);
        await ipc.StartAsync();

        var post = await RunToolHostAsync("post-tool-use", pipeName,
            "{\"session_id\":\"session-1\",\"agent_id\":\"agent-1\",\"agent_type\":\"delegated_subagent\",\"cwd\":\"E:\\\\AISPace\\\\HookShadowChain\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"src\\\\Target.cs\"},\"tool_response\":{\"success\":true}}");
        var shadow = await RunToolHostAsync("pre-tool-use", pipeName,
            "{\"session_id\":\"session-1\",\"agent_type\":\"main_turn\",\"cwd\":\"E:\\\\AISPace\\\\HookShadowChain\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"src\\\\Target.cs\"}}");

        Assert.Equal(0, post.ExitCode);
        Assert.Equal(0, shadow.ExitCode);
        Assert.DoesNotContain("permissionDecision", shadow.StandardOutput, StringComparison.Ordinal);
        var beforeTerminal = (await scheduler.GetRuntimeDiagnosticsAsync()).Hooks!;
        Assert.Equal(1, beforeTerminal.PostToolUseSeenCount);
        Assert.Equal(1, beforeTerminal.HardGateShadowEvaluatedCount);
        Assert.Equal(1, beforeTerminal.HardGateWouldDenyCount);
        Assert.True(beforeTerminal.LastHardGateEvent!.WouldDeny);
        Assert.False(beforeTerminal.LastHardGateEvent.Denied);

        await scheduler.ReportNativeResultAsync(new WorkerResultPacket(
            taskId, DelegationState.ResultReceived, "done", [], ["src/Target.cs"], ["pass"], []));
        var after = await RunToolHostAsync("pre-tool-use", pipeName,
            "{\"session_id\":\"session-1\",\"agent_type\":\"main_turn\",\"cwd\":\"E:\\\\AISPace\\\\HookShadowChain\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"src\\\\Target.cs\"}}");
        Assert.DoesNotContain("permissionDecision", after.StandardOutput, StringComparison.Ordinal);
        var afterTerminal = (await scheduler.GetRuntimeDiagnosticsAsync()).Hooks!;
        Assert.Equal(1, afterTerminal.HardGateShadowEvaluatedCount);
        Assert.Equal(1, afterTerminal.HardGateWouldDenyCount);
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

    private sealed class MemoryTaskRepository : ISchedulerTaskRepository
    {
        private readonly Dictionary<string, ScheduledDelegation> tasks = new(StringComparer.Ordinal);
        private readonly List<RepartitionTelemetry> repartitions = [];
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tasks.GetValueOrDefault(taskId));
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledDelegation>>(tasks.Values.ToArray());
        public Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default)
        {
            tasks[task.Packet.TaskId] = task;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(string taskGroupId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RepartitionTelemetry>>(repartitions.Where(item => item.TaskGroupId == taskGroupId).ToArray());
        public Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default)
        {
            repartitions.Add(telemetry);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryLeaseRepository : IWorkPackageLeaseRepository
    {
        private readonly List<WorkPackageLease> leases = [];
        public Task<WorkPackageLease?> GetActiveAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(leases.LastOrDefault(item => item.PackageId == packageId
                && item.Covers(workingDirectory) && item.Status is not (WorkPackageLeaseStatus.INVALID or WorkPackageLeaseStatus.COMPLETED)));
        public Task<WorkPackageLease?> GetActiveForWorkingDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(leases.LastOrDefault(item => item.Covers(workingDirectory)
                && item.Status is not (WorkPackageLeaseStatus.INVALID or WorkPackageLeaseStatus.COMPLETED)));
        public Task<IReadOnlyList<WorkPackageLease>> ListAsync(string? packageId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkPackageLease>>(leases.Where(item => packageId is null || item.PackageId == packageId).ToArray());
        public Task SaveAsync(WorkPackageLease lease, CancellationToken cancellationToken = default)
        {
            if (!leases.Contains(lease)) leases.Add(lease);
            return Task.CompletedTask;
        }
    }
}
