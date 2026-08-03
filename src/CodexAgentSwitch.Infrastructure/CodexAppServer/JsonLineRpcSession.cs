using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class JsonLineRpcSession : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readerLoop;
    private long _nextId;

    public JsonLineRpcSession(Stream input, Stream output)
    {
        _reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };
    }

    public event Func<string, JsonElement, Task>? NotificationReceived;

    public event Func<string, JsonElement, JsonElement, Task>? ServerRequestReceived;

    public void Start()
    {
        _readerLoop ??= Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Duplicate JSON-RPC request id.");
        }

        Start();

        try
        {
            await WriteAsync(new { method, id, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        parameters is null
            ? WriteAsync(new { method }, cancellationToken)
            : WriteAsync(new { method, @params = parameters }, cancellationToken);

    public async Task RespondAsync(JsonElement requestId, object result, CancellationToken cancellationToken = default)
    {
        var response = new Dictionary<string, object?>
        {
            ["id"] = JsonSerializer.Deserialize<object>(requestId.GetRawText()),
            ["result"] = result,
        };
        await WriteAsync(response, cancellationToken);
    }

    private async Task WriteAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_shutdown.Token);
                if (line is null)
                {
                    throw new EndOfStreamException("Codex App Server closed stdout.");
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement)
                    && !root.TryGetProperty("method", out _)
                    && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt64(out var id)
                    && _pending.TryGetValue(id, out var pending))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        pending.TrySetException(ParseError(error));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        pending.TrySetResult(result.Clone());
                    }

                    continue;
                }

                if (!root.TryGetProperty("method", out var methodElement))
                {
                    continue;
                }

                var method = methodElement.GetString() ?? string.Empty;
                var parameters = root.TryGetProperty("params", out var paramsElement)
                    ? paramsElement.Clone()
                    : JsonSerializer.SerializeToElement(new { });
                if (root.TryGetProperty("id", out var requestId) && ServerRequestReceived is not null)
                {
                    await ServerRequestReceived.Invoke(method, requestId.Clone(), parameters);
                }
                else if (NotificationReceived is not null)
                {
                    await NotificationReceived.Invoke(method, parameters);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(exception);
            }
        }
    }

    private static JsonRpcException ParseError(JsonElement error)
    {
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "Codex App Server request failed."
            : "Codex App Server request failed.";
        int? code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
            ? value
            : null;
        return new JsonRpcException(message, code);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_readerLoop is not null)
        {
            try
            {
                await _readerLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}
