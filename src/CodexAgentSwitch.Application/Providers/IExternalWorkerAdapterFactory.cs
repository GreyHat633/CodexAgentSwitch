using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Providers;

namespace CodexAgentSwitch.Application.Providers;

public interface IExternalWorkerAdapterFactory
{
    IWorkerAdapter Create(ProviderConfiguration provider);
}
