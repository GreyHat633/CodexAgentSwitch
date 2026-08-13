using System.Text.Json;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Infrastructure.Common;

namespace CodexAgentSwitch.Tests.Common;

public sealed class JsonlContinuationCorrelationSinkTests
{
    [Fact]
    public async Task Sink_writes_background_jsonl_without_task_or_model_side_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cas-correlation-{Guid.NewGuid():N}");
        var paths = new AppDataPaths(root);
        try
        {
            await using (var sink = new JsonlContinuationCorrelationSink(paths))
            {
                sink.Record(new ContinuationCorrelationEvent(
                    "task:local", "task", "local", "thread", "turn", "exec",
                    ContinuationCorrelationStage.ExecRequestObserved,
                    new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero)));
            }

            var lines = await File.ReadAllLinesAsync(Path.Combine(paths.LogsDirectory, "continuation-correlation.jsonl"));
            var activity = JsonSerializer.Deserialize<ContinuationCorrelationEvent>(lines.Single(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(activity);
            Assert.Equal(ContinuationCorrelationStage.ExecRequestObserved, activity.Stage);
            Assert.Equal("task:local", activity.CorrelationId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
