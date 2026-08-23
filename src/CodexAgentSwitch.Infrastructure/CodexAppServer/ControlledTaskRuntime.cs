using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Application.Workers;

namespace CodexAgentSwitch.Infrastructure.CodexAppServer;

public sealed class ControlledTaskRuntime(CodexRuntimeManager runtimeManager) : IControlledTaskRuntime
{
    public string? AppServerInstanceId => runtimeManager.InstanceId;

    public IMainAgentSession MainAgent => runtimeManager.MainAgent
        ?? throw new InvalidOperationException("Codex App Server 尚未启动。");

    public IWorkerAdapter NativeWorker => runtimeManager.Adapter
        ?? throw new InvalidOperationException("Codex App Server 尚未启动。");

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        var state = await runtimeManager.StartAsync(cancellationToken);
        if (!state.AppServerRunning || runtimeManager.MainAgent is null || runtimeManager.Adapter is null)
        {
            throw new InvalidOperationException(state.Message);
        }
    }
}
