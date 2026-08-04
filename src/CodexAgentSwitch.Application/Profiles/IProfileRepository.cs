using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Application.Profiles;

public interface IProfileRepository
{
    Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default);

    Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(Profile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> HasBeenInitializedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task MarkInitializedAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
