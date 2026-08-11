using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.ExternalAgents;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Infrastructure.ExternalAgents;

namespace CodexAgentSwitch.Infrastructure.ExternalProviders;

public sealed class ExternalWorkerAdapterFactory(
    OpenAiCompatibleClient client,
    IClock clock,
    IExternalToolHost? toolHost = null,
    IOpenCodeProcessRunner? openCodeProcessRunner = null) : IExternalWorkerAdapterFactory
{
    public IWorkerAdapter Create(ProviderConfiguration provider)
    {
        if (provider.Kind == ProviderKind.OpenCodeZen)
        {
            return new OpenCodeZenWorkerAdapter(provider, client, openCodeProcessRunner ?? new OpenCodeZenProcessRunner(), clock);
        }

        return new OpenAiCompatibleWorkerAdapter(
            provider,
            client,
            clock,
            new OpenAiCompatibleExternalAgentRuntime(client, toolHost ?? new LocalExternalToolHost()));
    }
}
