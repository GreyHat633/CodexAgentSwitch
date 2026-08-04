using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Domain.Projects;

namespace CodexAgentSwitch.Application.Projects;

public sealed class ProjectService(IProjectRepository repository, IClock clock)
{
    public Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default) =>
        repository.ListAsync(cancellationToken);

    public Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        repository.GetAsync(id, cancellationToken);

    public async Task<AgentProject> CreateAsync(
        string name,
        string workingDirectory,
        Guid? defaultProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateName(name);
        var normalizedDirectory = ValidateWorkingDirectory(workingDirectory);
        await EnsureNameAvailableAsync(normalizedName, null, cancellationToken);
        await EnsureDirectoryAvailableAsync(normalizedDirectory, null, cancellationToken);
        var now = clock.UtcNow;
        var project = new AgentProject(
            Guid.NewGuid().ToString("D"), normalizedName, normalizedDirectory, false, now, now, defaultProfileId);
        await repository.UpsertAsync(project, cancellationToken);
        return project;
    }

    public Task<AgentProject> RenameAsync(string id, string name, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, ValidateName(name), null, cancellationToken);

    public Task<AgentProject> ChangeWorkingDirectoryAsync(
        string id,
        string workingDirectory,
        CancellationToken cancellationToken = default) => ChangeWorkingDirectoryCoreAsync(id, ValidateWorkingDirectory(workingDirectory), cancellationToken);

    public async Task<AgentProject> SetDefaultProfileAsync(
        string id,
        Guid? profileId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetRequiredAsync(id, cancellationToken);
        var updated = current with { DefaultProfileId = profileId, UpdatedAt = clock.UtcNow };
        await repository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<AgentProject> RecordNativeCodexAdaptationAsync(
        string id,
        NativeCodexProjectAdaptation adaptation,
        CancellationToken cancellationToken = default)
    {
        var current = await GetRequiredAsync(id, cancellationToken);
        var updated = current with { NativeCodexAdaptation = adaptation, UpdatedAt = clock.UtcNow };
        await repository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<AgentProject> ClearNativeCodexAdaptationAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var current = await GetRequiredAsync(id, cancellationToken);
        var updated = current with { NativeCodexAdaptation = null, UpdatedAt = clock.UtcNow };
        await repository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    public Task<AgentProject> ArchiveAsync(string id, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(id, true, cancellationToken);

    public Task<AgentProject> UnarchiveAsync(string id, CancellationToken cancellationToken = default) =>
        SetArchivedAsync(id, false, cancellationToken);

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _ = await GetRequiredAsync(id, cancellationToken);
        await repository.DeleteAsync(id, cancellationToken);
    }

    private async Task<AgentProject> UpdateAsync(
        string id,
        string? name,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAsync(id, cancellationToken);
        if (name is not null)
        {
            await EnsureNameAvailableAsync(name, current.Id, cancellationToken);
        }

        var updated = current with
        {
            Name = name ?? current.Name,
            WorkingDirectory = workingDirectory ?? current.WorkingDirectory,
            UpdatedAt = clock.UtcNow,
        };
        await repository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<AgentProject> ChangeWorkingDirectoryCoreAsync(
        string id,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        await EnsureDirectoryAvailableAsync(workingDirectory, id, cancellationToken);
        return await UpdateAsync(id, null, workingDirectory, cancellationToken);
    }

    private async Task<AgentProject> SetArchivedAsync(
        string id,
        bool isArchived,
        CancellationToken cancellationToken)
    {
        var current = await GetRequiredAsync(id, cancellationToken);
        var updated = current with { IsArchived = isArchived, UpdatedAt = clock.UtcNow };
        await repository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<AgentProject> GetRequiredAsync(string id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken)
        ?? throw new KeyNotFoundException($"项目 {id} 不存在。");

    private async Task EnsureNameAvailableAsync(
        string name,
        string? currentId,
        CancellationToken cancellationToken)
    {
        var duplicate = (await repository.ListAsync(cancellationToken))
            .Any(project => !string.Equals(project.Id, currentId, StringComparison.Ordinal)
                && string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException($"项目名称“{name}”已经存在。");
        }
    }

    private async Task EnsureDirectoryAvailableAsync(
        string workingDirectory,
        string? currentId,
        CancellationToken cancellationToken)
    {
        var duplicate = (await repository.ListAsync(cancellationToken))
            .Any(project => !string.Equals(project.Id, currentId, StringComparison.Ordinal)
                && string.Equals(project.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException($"工作目录已作为现有项目添加：{workingDirectory}");
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("项目名称不能为空。", nameof(name));
        }

        var normalized = name.Trim();
        return normalized.Length <= 120
            ? normalized
            : throw new ArgumentException("项目名称不能超过 120 个字符。", nameof(name));
    }

    private static string ValidateWorkingDirectory(string workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("工作目录不能为空。", nameof(workingDirectory))
            : Directory.Exists(workingDirectory.Trim())
                ? Path.GetFullPath(workingDirectory.Trim())
                : throw new DirectoryNotFoundException($"工作目录不存在：{workingDirectory}");
}
