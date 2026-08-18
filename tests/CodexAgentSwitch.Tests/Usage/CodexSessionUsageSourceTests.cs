using CodexAgentSwitch.Infrastructure.Usage;

namespace CodexAgentSwitch.Tests.Usage;

public sealed class CodexSessionUsageSourceTests
{
    [Theory]
    [InlineData("258400", 258400L)]
    [InlineData("null", null)]
    [InlineData("\"258400\"", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    [InlineData("true", null)]
    public void Model_context_window_accepts_only_json_numbers(string value, long? expected)
    {
        var root = CreateTestDirectory("context-window");
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"s\",\"cwd\":\"E:/p\",\"source\":\"vscode\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"info\":{\"model_context_window\":" + value + ",\"last_token_usage\":{\"input_tokens\":10}}}}\n");

            var record = Assert.Single(new CodexSessionUsageSource(root).Read());
            Assert.Equal(expected, record.ContextWindowTokens);
            Assert.Equal(10, record.LatestInputTokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_model_context_window_is_no_value_and_other_usage_is_preserved()
    {
        var root = CreateTestDirectory("missing-context-window");
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"), """
{"type":"session_meta","payload":{"id":"s","cwd":"E:/p","source":"vscode"}}
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":10,"output_tokens":2}}}}
""");

            var record = Assert.Single(new CodexSessionUsageSource(root).Read());
            Assert.Null(record.ContextWindowTokens);
            Assert.Equal(10, record.InputTokens);
            Assert.Equal(2, record.OutputTokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Nullable_and_wrong_type_token_fields_are_ignored_without_losing_valid_fields()
    {
        var root = CreateTestDirectory("nullable-tokens");
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"), """
{"type":"session_meta","payload":{"id":"s","cwd":"E:/p","source":"vscode"}}
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":12,"cached_input_tokens":null,"output_tokens":"4","reasoning_tokens":{},"total_tokens":[]}}}}
{"type":"event_msg","payload":{"nested":{"model_context_window":{},"last_token_usage":{"input_tokens":8,"cached_input_tokens":3,"output_tokens":2,"reasoning_tokens":true,"total_tokens":10}}}}
""");

            var record = Assert.Single(new CodexSessionUsageSource(root).Read());
            Assert.Equal(20, record.InputTokens);
            Assert.Equal(3, record.CachedInputTokens);
            Assert.Equal(2, record.OutputTokens);
            Assert.Equal(0, record.ReasoningTokens);
            Assert.Equal(22, record.TotalTokens);
            Assert.Null(record.ContextWindowTokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Bad_event_is_skipped_and_later_good_event_is_read_with_sanitized_diagnostic()
    {
        var root = CreateTestDirectory("bad-event");
        var diagnostics = Path.Combine(CreateTestDirectory("bad-event-diagnostics"), "usage.jsonl");
        try
        {
            File.WriteAllText(Path.Combine(root, "session.jsonl"), """
{"type":"session_meta","payload":{"id":"s","cwd":"E:/p","source":"vscode"}}
not-json TOP-SECRET-PROMPT
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":17,"output_tokens":3}}}}
""");

            var source = new CodexSessionUsageSource(root, diagnostics);
            var record = Assert.Single(source.Read());

            Assert.Equal(17, record.LatestInputTokens);
            Assert.Equal(1, source.LastScanMetrics.EventsSkipped);
            Assert.Equal(0, source.LastScanMetrics.FilesFailed);
            Assert.Equal(1, source.LastScanMetrics.RecordsProduced);
            var diagnostic = File.ReadAllText(diagnostics);
            Assert.Contains("event-parse", diagnostic);
            Assert.Contains("\"lineIndex\":2", diagnostic);
            Assert.Contains("JsonReaderException", diagnostic);
            Assert.DoesNotContain("TOP-SECRET-PROMPT", diagnostic);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(Path.GetDirectoryName(diagnostics)!, true);
        }
    }

    [Fact]
    public void Bad_old_session_does_not_hide_good_current_session()
    {
        var root = CreateTestDirectory("bad-old-good-current");
        try
        {
            File.WriteAllText(Path.Combine(root, "old.jsonl"), """
{"type":"session_meta","payload":{"id":"old","cwd":"E:/old","source":"vscode"}}
not-json old-session-content
""");
            File.WriteAllText(Path.Combine(root, "current.jsonl"), """
{"type":"session_meta","payload":{"id":"current","cwd":"E:/current","source":"vscode"}}
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":23}}}}
""");

            var source = new CodexSessionUsageSource(root);
            var current = Assert.Single(source.Read(), item => item.SessionId == "current");

            Assert.Equal(23, current.LatestInputTokens);
            Assert.Equal(1, source.LastScanMetrics.EventsSkipped);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unreadable_file_does_not_prevent_other_files_from_being_scanned()
    {
        var root = CreateTestDirectory("bad-file-good-file");
        var diagnostics = Path.Combine(CreateTestDirectory("bad-file-diagnostics"), "usage.jsonl");
        try
        {
            var badPath = Path.Combine(root, "locked.jsonl");
            File.WriteAllText(badPath, "locked");
            File.WriteAllText(Path.Combine(root, "good.jsonl"), """
{"type":"session_meta","payload":{"id":"good","cwd":"E:/good","source":"vscode"}}
{"type":"event_msg","payload":{"info":{"last_token_usage":{"input_tokens":31}}}}
""");

            using var locked = new FileStream(badPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var source = new CodexSessionUsageSource(root, diagnostics);
            var good = Assert.Single(source.Read(), item => item.SessionId == "good");

            Assert.Equal(31, good.LatestInputTokens);
            Assert.Equal(2, source.LastScanMetrics.FilesScanned);
            Assert.Equal(1, source.LastScanMetrics.FilesFailed);
            Assert.Equal(1, source.LastScanMetrics.RecordsProduced);
            Assert.Contains("file-read", File.ReadAllText(diagnostics));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(Path.GetDirectoryName(diagnostics)!, true);
        }
    }

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

    private static string CreateTestDirectory(string name)
    {
        var testRoot = Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory.");
        var root = Path.Combine(testRoot, $"usage-source-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
