namespace CodexAgentSwitch.Application.Credentials;

public interface ICredentialStore
{
    Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default);

    Task SaveAsync(string referenceId, string secret, CancellationToken cancellationToken = default);

    Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default);
}
