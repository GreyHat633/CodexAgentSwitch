using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
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
                var method = root.GetProperty("method").GetString() ?? string.Empty;
                var payload = root.TryGetProperty("payload", out var value) ? value : default;
                object? result = method switch
                {
                    "delegationPreflight" => await scheduler.DelegationPreflightAsync(payload.Deserialize<DelegationPreflightRequest>(JsonOptions)
                        ?? throw new InvalidDataException("DelegationPreflightRequest invalid."), cancellationToken),
                    "dispatch" => await scheduler.DispatchAsync(payload.Deserialize<TaskPacket>(JsonOptions)
                        ?? throw new InvalidDataException("TaskPacket 无效。"), cancellationToken),
                    "reportResult" => await scheduler.ReportNativeResultAsync(payload.Deserialize<WorkerResultPacket>(JsonOptions)
                        ?? throw new InvalidDataException("WorkerResultPacket 无效。"), cancellationToken),
                    "review" => await scheduler.MarkReviewingAsync(payload.GetProperty("taskId").GetString() ?? string.Empty, cancellationToken),
                    "adopt" => await scheduler.MarkAdoptedAsync(
                        payload.GetProperty("taskId").GetString() ?? string.Empty,
                        payload.TryGetProperty("summary", out var summary) ? summary.GetString() ?? string.Empty : string.Empty,
                        cancellationToken),
                    "recordRepartition" => await RecordRepartitionAsync(payload, cancellationToken),
                    "preToolUse" => await scheduler.EvaluatePreToolUseAsync(new PreToolUseRequest(
                        payload.GetProperty("sessionId").GetString() ?? string.Empty,
                        payload.GetProperty("workingDirectory").GetString() ?? string.Empty,
                        payload.GetProperty("toolName").GetString() ?? string.Empty,
                        payload.TryGetProperty("toolInput", out var toolInput)
                            ? toolInput.ValueKind == JsonValueKind.String ? toolInput.GetString() : toolInput.GetRawText()
                            : null), cancellationToken),
                    "completePackage" => await scheduler.CompletePackageAsync(
                        payload.GetProperty("packageId").GetString() ?? string.Empty,
                        payload.GetProperty("workingDirectory").GetString() ?? string.Empty,
                        cancellationToken),
                    "listRepartitions" => await scheduler.ListRepartitionsAsync(
                        payload.GetProperty("taskGroupId").GetString() ?? string.Empty,
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

    private async Task<RepartitionTelemetry> RecordRepartitionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var taskGroupId = payload.GetProperty("taskGroupId").GetString() ?? string.Empty;
        var trigger = ParseEnum<RepartitionTrigger>(payload, "trigger");
        var decision = ParseEnum<WorkOwner>(payload, "decision");
        var reason = ParseEnum<RepartitionReasonCode>(payload, "reason");
        var workSummary = payload.GetProperty("workSummary").GetString() ?? string.Empty;
        var workerIdentity = payload.TryGetProperty("workerIdentity", out var worker) ? worker.GetString() : null;
        var result = payload.TryGetProperty("result", out var resultValue) ? resultValue.GetString() : null;
        var packageId = payload.TryGetProperty("packageId", out var package) ? package.GetString() : null;
        var workingDirectory = payload.TryGetProperty("workingDirectory", out var cwd) ? cwd.GetString() : null;
        var packageKind = payload.TryGetProperty("packageKind", out var kind) ? kind.GetString() : null;
        var declaredScopes = payload.TryGetProperty("declaredScopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
            ? scopes.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
            : null;
        var costWindowIndex = payload.TryGetProperty("costWindowIndex", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetInt32() : (int?)null;
        return await scheduler.RecordRepartitionAsync(
            taskGroupId,
            trigger,
            decision,
            reason,
            workSummary,
            workerIdentity,
            result,
            packageId,
            workingDirectory,
            packageKind,
            declaredScopes,
            costWindowIndex,
            cancellationToken);
    }

    private static T ParseEnum<T>(JsonElement payload, string name) where T : struct, Enum
    {
        var value = payload.GetProperty(name).GetString();
        return value is not null && Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"{name} is invalid.");
    }
}
