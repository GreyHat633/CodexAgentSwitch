using CodexAgentSwitch.Application.Abstractions;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed record CodexRuntimeState(
    bool Installed,
    bool AppServerRunning,
    string? Version,
    ProtocolSchemaSnapshot? Schema,
    string Message);

public sealed class CodexRuntimeManager(
    CodexCommandLocator locator,
    CodexSchemaCache schemaCache,
    IClock clock) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CodexAppServerClient? client;

    public NativeCodexWorkerAdapter? Adapter { get; private set; }

    public CodexRuntimeState State { get; private set; } = new(false, false, null, null, "尚未检测 Codex。");

    public async Task<CodexRuntimeState> DetectAsync(CancellationToken cancellationToken = default)
    {
        var discovery = await locator.LocateAsync(cancellationToken);
        State = discovery.IsAvailable
            ? new CodexRuntimeState(true, client is not null, discovery.Version, State.Schema, "Codex CLI 已检测。")
            : new CodexRuntimeState(false, false, null, null, discovery.Status);
        return State;
    }

    public async Task<CodexRuntimeState> StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (client is not null)
            {
                return State;
            }

            var discovery = await locator.LocateAsync(cancellationToken);
            if (discovery.Command is null || discovery.Version is null)
            {
                State = new CodexRuntimeState(false, false, null, null, discovery.Status);
                return State;
            }

            var schema = await schemaCache.GenerateAsync(discovery.Command, discovery.Version, cancellationToken);
            var newClient = new CodexAppServerClient(discovery.Command);
            try
            {
                await newClient.StartAsync(cancellationToken);
                client = newClient;
                Adapter = new NativeCodexWorkerAdapter(newClient, clock);
                State = new CodexRuntimeState(true, true, discovery.Version, schema, "Codex App Server 已启动并完成协议握手。");
            }
            catch
            {
                await newClient.DisposeAsync();
                throw;
            }

            return State;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync();
                client = null;
                Adapter = null;
                State = State with { AppServerRunning = false, Message = "Codex App Server 已停止。" };
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        gate.Dispose();
    }
}
