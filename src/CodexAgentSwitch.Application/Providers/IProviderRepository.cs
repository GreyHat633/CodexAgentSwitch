using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Application.Providers;

public interface IProviderRepository
{
    Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default);

    Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
