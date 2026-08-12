using CodexAgentSwitch.Infrastructure.Usage;

namespace CodexAgentSwitch.Tests.Usage;

public sealed class CodexSessionUsageSourceTests
{
    [Fact]
    public void Session_source_falls_back_to_root_and_later_lifecycle_payloads()
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, "usage-source-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "root.jsonl"),
                "{\"type\":\"session_meta\",\"source\":\"vscode\",\"payload\":{\"id\":\"root\",\"cwd\":\"E:/p\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"info\":{\"last_token_usage\":{\"input_tokens\":10}}}}\n");
            File.WriteAllText(Path.Combine(root, "later.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"later\",\"cwd\":\"E:/p\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"source\":\"vscode\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"info\":{\"last_token_usage\":{\"input_tokens\":10}}}}\n");

            var records = new CodexSessionUsageSource(root).Read().ToDictionary(item => item.SessionId);
            Assert.Equal("vscode", records["root"].SessionSource);
            Assert.Equal("vscode", records["later"].SessionSource);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Reads_deltas_and_never_double_counts_cumulative_totals()
    {
        var root = Path.Combine(Path.GetTempPath(), "cas-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"), """
{"type":"session_meta","payload":{"id":"s1","cwd":"E:/p","model":"gpt-5.6-sol","source":"vscode"}}
{"type":"event_msg","payload":{"type":"task_started","model_context_window":258400}}
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
            Assert.Equal(150, record.LatestInputTokens);
            Assert.Equal(30, record.LatestCachedInputTokens);
            Assert.Equal(258400, record.ContextWindowTokens);
            Assert.Equal("vscode", record.SessionSource);
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

    [Fact]
    public void Structured_compacted_is_recorded_and_zero_input_lifecycle_is_not_a_model_sample()
    {
        var root = Path.Combine(Path.GetTempPath(), "cas-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "s.jsonl"), """
{"timestamp":"2026-08-12T05:48:00Z","type":"session_meta","payload":{"id":"s","cwd":"E:/p","source":"vscode"}}
{"timestamp":"2026-08-12T05:48:10Z","type":"event_msg","payload":{"type":"token_count","info":{"model_context_window":258400,"last_token_usage":{"input_tokens":105079,"cached_input_tokens":80000}}}}
{"timestamp":"2026-08-12T05:48:20Z","type":"event_msg","payload":{"type":"token_count","info":{"model_context_window":258400,"last_token_usage":{"input_tokens":144215,"cached_input_tokens":110000}}}}
{"timestamp":"2026-08-12T05:48:30Z","type":"event_msg","payload":{"type":"token_count","info":{"model_context_window":258400,"last_token_usage":{"input_tokens":218856,"cached_input_tokens":180000}}}}
{"timestamp":"2026-08-12T05:49:07.087Z","type":"compacted","payload":{}}
{"timestamp":"2026-08-12T05:49:30Z","type":"event_msg","payload":{"type":"token_count","info":{"model_context_window":258400,"last_token_usage":{"input_tokens":65000,"cached_input_tokens":42000}}}}
{"timestamp":"2026-08-12T05:49:31Z","type":"event_msg","payload":{"type":"token_count","info":{"model_context_window":258400,"last_token_usage":{"input_tokens":0,"cached_input_tokens":0}}}}
""");

            var record = Assert.Single(new CodexSessionUsageSource(root).Read());
            Assert.Equal(65000, record.LatestInputTokens);
            Assert.Equal(42000, record.LatestCachedInputTokens);
            Assert.Equal(218856, record.PreCompactionInputTokens);
            Assert.Equal(180000, record.PreCompactionCachedInputTokens);
            Assert.Equal([105079, 144215, 218856], record.PreCompactionInputSamples);
            Assert.Equal(258400, record.ContextWindowTokens);
            Assert.Equal(DateTimeOffset.Parse("2026-08-12T05:49:07.087Z"), record.LastStructuredCompactedAt);
            Assert.Equal(4, record.Calls);
        }
        finally { Directory.Delete(root, true); }
    }
}
