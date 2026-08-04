using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Domain.Projects;

namespace CodexAgentSwitch.Tests.Projects;

public sealed class ProjectServiceTests
{
    private readonly string workingDirectory = Directory.GetCurrentDirectory();

    [Fact]
    public async Task Crud_lifecycle_updates_project_and_rejects_invalid_input()
    {
        var repository = new InMemoryProjectRepository();
        var service = new ProjectService(repository, new FixedClock());

        var project = await service.CreateAsync("Alpha", workingDirectory);
        var renamed = await service.RenameAsync(project.Id, "Beta");
        var archived = await service.ArchiveAsync(project.Id);
        var restored = await service.UnarchiveAsync(project.Id);

        Assert.Equal("Beta", renamed.Name);
        Assert.True(archived.IsArchived);
        Assert.False(restored.IsArchived);
        await service.DeleteAsync(project.Id);
        Assert.Null(await service.GetAsync(project.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(" ", workingDirectory));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.CreateAsync("Gamma", "E:\\does-not-exist"));
    }

    [Fact]
    public async Task Names_are_unique_case_insensitively_and_limited()
    {
        var service = new ProjectService(new InMemoryProjectRepository(), new FixedClock());
        await service.CreateAsync("Alpha", workingDirectory);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(" alpha ", workingDirectory));

        Assert.Contains("已经存在", duplicate.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(new string('a', 121), workingDirectory));
    }

    [Fact]
    public async Task Default_profile_is_saved_per_project_without_changing_existing_project_identity()
    {
        var service = new ProjectService(new InMemoryProjectRepository(), new FixedClock());
        var firstProfile = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();

        var project = await service.CreateAsync("Profile scoped", workingDirectory, firstProfile);
        var updated = await service.SetDefaultProfileAsync(project.Id, secondProfile);

        Assert.Equal(project.Id, updated.Id);
        Assert.Equal(secondProfile, updated.DefaultProfileId);
        Assert.Equal(project.WorkingDirectory, updated.WorkingDirectory);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class InMemoryProjectRepository : IProjectRepository
    {
        private readonly Dictionary<string, AgentProject> projects = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentProject>>(projects.Values.ToList());

        public Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects.GetValueOrDefault(id));

        public Task UpsertAsync(AgentProject project, CancellationToken cancellationToken = default)
        {
            projects[project.Id] = project;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            projects.Remove(id);
            return Task.CompletedTask;
        }
    }
}
