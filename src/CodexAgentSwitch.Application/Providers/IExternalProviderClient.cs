using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Application.Providers;

public interface IExternalProviderClient
{
    Task<ProviderConnectionResult> TestConnectionAsync(
        ProviderConfiguration provider,
        CancellationToken cancellationToken = default);
}
