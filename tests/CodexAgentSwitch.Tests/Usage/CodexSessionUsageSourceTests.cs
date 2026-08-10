using CodexAgentSwitch.Infrastructure.Usage;

namespace CodexAgentSwitch.Tests.Usage;

public sealed class CodexSessionUsageSourceTests
{
    [Fact]
    public void Reads_deltas_and_never_double_counts_cumulative_totals()
    {
        var root = Path.Combine(Path.GetTempPath(), "cas-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"), """
{"type":"session_meta","payload":{"id":"s1","cwd":"E:/p","model":"gpt-5.6-sol"}}
{"type":"turn_context","payload":{"model":"gpt-5.6-terra","effort":"high"}}
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":10,"reasoning_tokens":4,"total_tokens":110}}}}
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":150,"cached_input_tokens":30,"output_tokens":18,"reasoning_tokens":6,"total_tokens":168}}}}
bad json
""");
            var record = Assert.Single(new CodexSessionUsageSource(root).Read());
            Assert.Equal(250, record.InputTokens);
            Assert.Equal(50, record.CachedInputTokens);
            Assert.Equal(200, record.UncachedInputTokens);
            Assert.Equal(28, record.OutputTokens);
            Assert.Equal(10, record.ReasoningTokens);
            Assert.Equal(2, record.Calls);
            Assert.Equal(278, record.TotalTokens);
            Assert.Equal("Terra", record.AgentRole);
            Assert.Equal("high", record.ReasoningEffort);
            Assert.Equal("cwd", record.Attribution);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Subagent_role_wins_over_model_mapping()
    {
        var root = Path.Combine(Path.GetTempPath(), "cas-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { File.WriteAllText(Path.Combine(root, "s.jsonl"), "{\"type\":\"session_meta\",\"payload\":{\"id\":\"s\",\"source\":{\"subagent\":{\"thread_spawn\":{\"agent_role\":\"cas_luna_worker\"}}}}}\n{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-luna\",\"effort\":\"high\"}}\n{\"type\":\"event_msg\",\"payload\":{\"info\":{\"last_token_usage\":{\"input_tokens\":1,\"output_tokens\":2}}}}\n"); Assert.Equal("cas_luna_worker", Assert.Single(new CodexSessionUsageSource(root).Read()).AgentRole); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_directory_is_safe()
    {
        Assert.Empty(new CodexSessionUsageSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Read());
    }
}
