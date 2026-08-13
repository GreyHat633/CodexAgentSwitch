using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexAgentSwitch.Application.Tasks;

namespace CodexAgentSwitch.Infrastructure.Common;

public sealed class JsonlContinuationCorrelationSink : IContinuationCorrelationSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<ContinuationCorrelationEvent> queue = Channel.CreateBounded<ContinuationCorrelationEvent>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly string path;
    private readonly Task writer;

    public JsonlContinuationCorrelationSink(AppDataPaths paths)
    {
        paths.EnsureCreated();
        path = Path.Combine(paths.LogsDirectory, "continuation-correlation.jsonl");
        writer = WriteAsync();
    }

    public void Record(ContinuationCorrelationEvent activity) => queue.Writer.TryWrite(activity);

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writer.ConfigureAwait(false); }
        catch { }
    }

    private async Task WriteAsync()
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new StreamWriter(stream, new UTF8Encoding(false));
            await foreach (var activity in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(activity, JsonOptions)).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Correlation logging is best-effort and must remain behavior-neutral.
        }
    }
}
