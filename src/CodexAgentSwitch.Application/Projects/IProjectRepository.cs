using CodexAgentSwitch.Domain.Projects;

namespace CodexAgentSwitch.Application.Projects;

public interface IProjectRepository
{
    Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default);

    Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task UpsertAsync(AgentProject project, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
