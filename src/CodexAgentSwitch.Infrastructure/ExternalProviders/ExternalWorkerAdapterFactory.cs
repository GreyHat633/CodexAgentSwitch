using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed class ExternalWorkerAdapterFactory(OpenAiCompatibleClient client, IClock clock) : IExternalWorkerAdapterFactory
{
    public IWorkerAdapter Create(ProviderConfiguration provider) => new OpenAiCompatibleWorkerAdapter(provider, client, clock);
}
