using System.Text.Json;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Tasks;

public sealed class ManagedContextDiagnosticsServiceTests
{
    [Fact]
    public async Task Diagnostics_project_only_sanitized_managed_state_and_never_the_root_or_lease()
    {
        var now = DateTimeOffset.Parse("2026-08-22T10:00:00Z");
        var project = new AgentProject("project-a", "Managed A", "E:\\Secret\\Project", false, now, now);
        var binding = new ManagedContextSession(
            project.Id,
            project.WorkingDirectory,
            "thread-a",
            "session-a",
            "task-a",
            "app-server-secret",
            "lease-secret",
            ManagedContextOwnershipState.Verifying,
            LastSafeBoundaryAt: now,
            LastCompactionRequestedAt: now.AddSeconds(-2),
            LastCompactionStartedAt: now.AddSeconds(-1),
            LastCompactionCompletedAt: now);
        var context = new ContextEconomySnapshot(
            "thread-a",
            ContextEconomyState.Verifying,
            1,
            8,
            [new ContextTurnSample(800, 300, ContextWindowTokens: 1000)],
            [],
            LastReason: "must not leak a model response",
            PreCompactionPressure: 0.8m,
            LastEffectiveness: new CompactionEffectivenessResult(
                CompactionEffectiveness.Effective, 0.5m, 800, 400, 300, 100, "internal"));
        var service = new ManagedContextDiagnosticsService(
            new SessionStore(binding),
            new ContextStore(context),
            new ProjectStore(project));

        var result = await service.GetAsync();

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Managed A", entry.ProjectName);
        Assert.Equal("thread-a", entry.ThreadId);
        Assert.Equal("session-a", entry.SessionId);
        Assert.Equal(800, entry.LatestInputTokens);
        Assert.Equal(CompactionEffectiveness.Effective, entry.Effectiveness);
        Assert.Equal(12, entry.CanonicalRootFingerprint.Length);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(project.WorkingDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lease-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("app-server-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("model response", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lost_binding_has_an_explicit_reason_without_requiring_context_state()
    {
        var binding = new ManagedContextSession(
            "project-missing", "E:\\managed", "thread-lost", "session-lost", "task-lost",
            "app-server", "lease", ManagedContextOwnershipState.Lost);
        var service = new ManagedContextDiagnosticsService(
            new SessionStore(binding),
            new ContextStore(null),
            new ProjectStore(null));

        var result = await service.GetAsync();

        Assert.Equal(1, result.LostCount);
        Assert.Equal("OWNERSHIP_LOST", Assert.Single(result.Entries).ReasonCode);
    }

    private sealed class SessionStore(ManagedContextSession binding) : IManagedContextSessionStore
    {
        public Task<ManagedContextSession?> LoadByTaskSessionAsync(string taskSessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedContextSession?>(taskSessionId == binding.TaskSessionId ? binding : null);
        public Task<ManagedContextSession?> LoadByThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedContextSession?>(threadId == binding.ThreadId ? binding : null);
        public Task<IReadOnlyList<ManagedContextSession>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedContextSession>>([binding]);
        public Task UpsertAsync(ManagedContextSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryUpdateLeaseAsync(ManagedContextSession session, string expectedOwnershipLeaseId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DeleteAsync(string taskSessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ContextStore(ContextEconomySnapshot? snapshot) : IMainContextEconomyStateStore
    {
        public Task<ContextEconomySnapshot?> LoadAsync(string threadId, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task SaveAsync(ContextEconomySnapshot value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProjectStore(AgentProject? project) : IProjectRepository
    {
        public Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentProject>>(project is null ? [] : [project]);
        public Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(project is not null && project.Id == id ? project : null);
        public Task UpsertAsync(AgentProject value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
