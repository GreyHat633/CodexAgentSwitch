using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Application.Providers;

public interface IExternalProviderClient
{
    Task<IReadOnlyList<string>> ListModelsAsync(
        ProviderConfiguration provider,
        CancellationToken cancellationToken = default);

    Task<ProviderConnectionResult> TestConnectionAsync(
        ProviderConfiguration provider,
        CancellationToken cancellationToken = default);
}
