using System.Reflection;
using System.Text.Json;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Application.Tasks;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class CodexMainAgentSessionContextTests
{
    [Fact]
    public async Task Compact_uses_exact_native_rpc_and_emits_context_compaction_events_without_turn_registration()
    {
        var calls = new List<(string Method, JsonElement Parameters)>();
        var client = new CodexAppServerClient(CodexCommand.Direct("unused"));
        var session = new CodexMainAgentSession(client, request: (method, parameters, _) =>
        {
            calls.Add((method, JsonSerializer.SerializeToElement(parameters)));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
        });
        var events = new List<MainAgentEvent>();
        session.EventReceived += value => { events.Add(value); return Task.CompletedTask; };

        var handle = await session.CompactThreadAsync("thread-old");
        await InvokeNotification(session, "item/started", new { threadId = "thread-old", turnId = "turn-1", item = new { type = "contextCompaction" } });
        await InvokeNotification(session, "item/completed", new { threadId = "thread-old", turnId = "turn-1", item = new { type = "contextCompaction" } });

        Assert.True(handle.RequestAccepted);
        Assert.Single(calls);
        Assert.Equal("thread/compact/start", calls[0].Method);
        Assert.Equal("thread-old", calls[0].Parameters.GetProperty("threadId").GetString());
        Assert.Equal([MainAgentEventKind.CompactionStarted, MainAgentEventKind.CompactionCompleted], events.Select(value => value.Kind));
        Assert.All(events, value => Assert.Equal("turn-1", value.TurnId));
    }

    [Fact]
    public async Task Rollover_replays_checkpoint_on_only_the_fresh_thread_and_rejects_bad_provenance()
    {
        var calls = new List<(string Method, JsonElement Parameters)>();
        var client = new CodexAppServerClient(CodexCommand.Direct("unused"));
        var resolver = new TestResolver();
        var session = new CodexMainAgentSession(client, resolver, (method, parameters, _) =>
        {
            calls.Add((method, JsonSerializer.SerializeToElement(parameters)));
            return Task.FromResult(method == "thread/start"
                ? JsonSerializer.SerializeToElement(new { thread = new { id = "thread-new" } })
                : JsonSerializer.SerializeToElement(new { turn = new { id = "turn-new" } }));
        });
        var checkpoint = new CompactCheckpoint(["done"], ["next"], [], [], "pass", "resume", "thread-old", "task-1", DateTimeOffset.UnixEpoch);

        var result = await session.RolloverThreadAsync("thread-old", checkpoint, "model", "medium", "E:\\work", ExecutionApprovalMode.Safe);
        Assert.Equal("thread-new", result.NewThreadId);
        Assert.Equal("thread-new", result.FirstTurn!.ThreadId);
        Assert.Equal("thread-new", calls.Single(call => call.Method == "turn/start").Parameters.GetProperty("threadId").GetString());
        Assert.Contains("source-thread: thread-old", calls.Single(call => call.Method == "turn/start").Parameters.GetProperty("input")[0].GetProperty("text").GetString(), StringComparison.Ordinal);

        var mismatched = new CompactCheckpoint(checkpoint.Completed, checkpoint.Remaining, checkpoint.StableInterfaces, checkpoint.NecessaryFiles, checkpoint.TestStatus, checkpoint.NextPhaseEntry, "other-thread", checkpoint.SourceTaskId, checkpoint.Timestamp);
        await Assert.ThrowsAsync<ArgumentException>(() => session.RolloverThreadAsync("thread-old", mismatched, "model", "medium", "E:\\work", ExecutionApprovalMode.Safe));
    }

    [Fact]
    public async Task Existing_vscode_binding_resumes_only_the_same_thread_and_rejects_identity_mismatch()
    {
        var calls = new List<string>();
        var client = new CodexAppServerClient(CodexCommand.Direct("unused"));
        var status = "notLoaded";
        var session = new CodexMainAgentSession(client, request: (method, parameters, _) =>
        {
            calls.Add(method);
            if (method == "thread/resume") status = "idle";
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                thread = new
                {
                    id = "thread-vscode",
                    sessionId = "thread-vscode",
                    source = "vscode",
                    cwd = "E:\\work",
                    status = new { type = status },
                },
            }));
        });

        var binding = await session.BindExistingThreadAsync("thread-vscode", "thread-vscode", "vscode", "E:\\work");

        Assert.True(binding.Resumed);
        Assert.Equal("idle", binding.Status);
        Assert.Equal(["thread/read", "thread/resume"], calls);
        Assert.DoesNotContain("thread/start", calls);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.BindExistingThreadAsync("thread-vscode", "different-session", "vscode", "E:\\work"));
    }

    [Fact]
    public async Task Command_execution_exposes_started_and_completed_observable_boundaries()
    {
        var client = new CodexAppServerClient(CodexCommand.Direct("unused"));
        var session = new CodexMainAgentSession(client, new TestResolver(), (method, _, _) =>
            Task.FromResult(method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-exec" } })
                : JsonSerializer.SerializeToElement(new { accepted = true })));
        var events = new List<MainAgentEvent>();
        session.EventReceived += value => { events.Add(value); return Task.CompletedTask; };
        await session.StartTurnAsync("thread-exec", "continue", "model", "medium", "E:\\work", ExecutionApprovalMode.Safe);

        await InvokeNotification(session, "item/started", new
        {
            threadId = "thread-exec",
            turnId = "turn-exec",
            item = new { id = "exec-1", type = "commandExecution", command = "dotnet test", status = "running" },
        });
        await InvokeNotification(session, "item/completed", new
        {
            threadId = "thread-exec",
            turnId = "turn-exec",
            item = new { id = "exec-1", type = "commandExecution", command = "dotnet test", status = "completed" },
        });

        Assert.Equal([MainAgentEventKind.TraceItemStarted, MainAgentEventKind.TraceItem], events.Select(value => value.Kind));
        Assert.All(events, value => Assert.Equal(TaskMessageKind.ToolCall, value.MessageKind));
    }

    private static async Task InvokeNotification(CodexMainAgentSession session, string method, object parameters)
    {
        var callback = typeof(CodexMainAgentSession).GetMethod("OnNotificationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)callback.Invoke(session, [method, JsonSerializer.SerializeToElement(parameters)])!;
    }

    private sealed class TestResolver : ICodexModelResolver
    {
        public Task<CodexModelResolution> ResolveAsync(CodexAppServerClient client, string requestedModelId, CancellationToken cancellationToken = default) => Task.FromResult(new CodexModelResolution(requestedModelId, requestedModelId, null));
        public Task<CodexModelResolution> ResolveAsync(CodexCommand command, string requestedModelId, CancellationToken cancellationToken = default) => Task.FromResult(new CodexModelResolution(requestedModelId, requestedModelId, null));
    }
}
