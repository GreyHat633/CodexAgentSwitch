using CodexAgentSwitch.Application.Workers;

namespace CodexAgentSwitch.Application.Tasks;

public interface IControlledTaskRuntime
{
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    string? AppServerInstanceId => null;

    IMainAgentSession MainAgent { get; }

    IWorkerAdapter NativeWorker { get; }
}
