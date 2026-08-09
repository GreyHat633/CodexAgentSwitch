using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Scheduling;

namespace CodexAgentSwitch.Infrastructure.Scheduling;

public sealed class SchedulerIpcServer(IWorkerScheduler scheduler, string? pipeName = null) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private CancellationTokenSource? cancellation;
    private Task? loop;
    private readonly string endpoint = pipeName ?? SchedulerEndpoint.PipeName;

    public bool IsRunning => loop is { IsCompleted: false };

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loop = AcceptLoopAsync(cancellation.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        cancellation?.Cancel();
        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                endpoint,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            _ = ProcessConnectionAsync(pipe, cancellationToken);
        }
    }

    private async Task ProcessConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var method = root.GetProperty("method").GetString();
                var payload = root.TryGetProperty("payload", out var value) ? value : default;
                object result = method switch
                {
                    "dispatch" => await scheduler.DispatchAsync(payload.Deserialize<TaskPacket>(JsonOptions)
                        ?? throw new InvalidDataException("TaskPacket 无效。"), cancellationToken),
                    "reportResult" => await scheduler.ReportNativeResultAsync(payload.Deserialize<WorkerResultPacket>(JsonOptions)
                        ?? throw new InvalidDataException("WorkerResultPacket 无效。"), cancellationToken),
                    "review" => await scheduler.MarkReviewingAsync(payload.GetProperty("taskId").GetString() ?? string.Empty, cancellationToken),
                    "adopt" => await scheduler.MarkAdoptedAsync(
                        payload.GetProperty("taskId").GetString() ?? string.Empty,
                        payload.TryGetProperty("summary", out var summary) ? summary.GetString() ?? string.Empty : string.Empty,
                        cancellationToken),
                    "status" => scheduler.Snapshot,
                    _ => throw new InvalidOperationException($"未知 Scheduler IPC 方法：{method}"),
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, result }, JsonOptions));
            }
            catch (Exception exception)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = exception.Message }, JsonOptions));
            }
        }
    }
}
