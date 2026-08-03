using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Application.Tasks;

public interface IControlledTaskRepository
{
    Task UpsertAsync(ControlledTaskSession task, CancellationToken cancellationToken = default);

    Task<ControlledTaskSession?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ControlledTaskSession>> ListAsync(CancellationToken cancellationToken = default);
}

