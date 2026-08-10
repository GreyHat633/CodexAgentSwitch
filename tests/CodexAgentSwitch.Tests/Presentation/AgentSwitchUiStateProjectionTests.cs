using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Credentials;
using CodexAgentSwitch.Application.Presentation;
using CodexAgentSwitch.Application.Profiles;
using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Providers;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Tests.Presentation;

public sealed class AgentSwitchUiStateProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Native_usage_is_project_filtered_and_external_ledger_stays_separate()
    {
        var configuredPath = @"E:\configured-project";
        var profile = Profile.CreateDefault(Now);
        var project = ConfiguredProject("configured", configuredPath, profile);
        var unconfigured = new AgentProject("plain", "Plain", @"E:\plain-project", false, Now, Now);
        var native = new FixedUsageSource(
        [
            Native("sol", configuredPath, "Sol", 10, 4, 3, 2, 13, 1),
            Native("luna", Path.Combine(configuredPath, "src"), "cas_luna_worker", 5, 1, 2, 1, 7, 1),
            Native("excluded", unconfigured.WorkingDirectory, "Sol", 100, 0, 20, 10, 120, 1),
        ]);
        var projection = CreateProjection(profile, [project, unconfigured], native, CreateUsageRepository());

        var usage = (await projection.ReadAsync()).Usage;

        Assert.Equal(5, usage.InputTokens);
        Assert.Equal(2, usage.OutputTokens);
        Assert.Equal(7, usage.TotalTokens);
        Assert.Equal(13, usage.Sol.TotalTokens);
        Assert.Equal(7, usage.LunaNativeWorker.TotalTokens);
        Assert.Equal(20, usage.NativeTotal.TotalTokens);
        Assert.Equal(3, usage.NativeTotal.ReasoningTokens);
        Assert.Equal(1, usage.NativeExcludedCount);
        Assert.Equal(profile.Budget.TokenLimit, usage.NativeTokenLimit);
    }

    [Fact]
    public async Task Native_collector_failure_does_not_hide_external_usage()
    {
        var profile = Profile.CreateDefault(Now);
        var projection = CreateProjection(
            profile,
            [ConfiguredProject("configured", @"E:\configured-project", profile)],
            new ThrowingUsageSource(),
            CreateUsageRepository());

        var usage = (await projection.ReadAsync()).Usage;

        Assert.True(usage.NativeReadFailed);
        Assert.Equal(7, usage.TotalTokens);
        Assert.Equal(0.25m, usage.Cost);
        Assert.Contains("读取失败", usage.NativeFilterMessage, StringComparison.Ordinal);
    }

    private static AgentSwitchUiStateProjection CreateProjection(
        Profile profile,
        IReadOnlyList<AgentProject> projects,
        IUsageSource native,
        IUsageLedgerRepository usage) => new(
            new EmptyScheduler(),
            new ProjectService(new ProjectRepository(projects), new FixedClock()),
            new ProfileRepository(profile),
            new EmptyProviderRepository(),
            new EmptyCredentialStore(),
            usage,
            native,
            new FixedClock());

    private static AgentProject ConfiguredProject(string id, string path, Profile profile) => new(
        id,
        id,
        path,
        false,
        Now,
        Now,
        profile.Id,
        new NativeCodexProjectAdaptation(
            profile.Id,
            profile.Name,
            Path.Combine(path, ".codex", "config.toml"),
            null,
            Now,
            "applied",
            false,
            new NativeCodexAppliedSnapshot(
                profile.Id,
                profile.Name,
                profile.MainAgent.ModelId,
                profile.MainAgent.ReasoningEffort,
                "NativeAgent",
                "cas_luna_worker",
                "gpt-5.6-luna",
                "native-codex",
                profile.WorkerPolicy.ReasoningEffort,
                1,
                "Economic",
                "Supported",
                "fingerprint")));

    private static NativeUsageRecord Native(
        string id,
        string cwd,
        string role,
        long input,
        long cached,
        long output,
        long reasoning,
        long total,
        long calls) => new(
            id, cwd, null, role == "Sol" ? "gpt-5.6-sol" : "gpt-5.6-luna", "medium", role,
            calls, input, cached, input - cached, output, reasoning, total, Now, Now, $"{id}.jsonl", "cwd");

    private static IUsageLedgerRepository CreateUsageRepository()
    {
        var external = new UsageSnapshot(
            Guid.NewGuid(), "group", null, "deepseek-default", "deepseek-v4", Now,
            new MeasuredLong(5, EvidenceKind.Actual),
            new MeasuredLong(2, EvidenceKind.Actual),
            new MeasuredLong(7, EvidenceKind.Actual),
            new MeasuredLong(1, EvidenceKind.Actual),
            new MeasuredDecimal(0.25m, EvidenceKind.Actual), "CNY", null, []);
        var nativeLedger = external with
        {
            Id = Guid.NewGuid(),
            ProviderId = "native-codex",
            InputTokens = new MeasuredLong(100, EvidenceKind.Actual),
            OutputTokens = new MeasuredLong(20, EvidenceKind.Actual),
            TotalTokens = new MeasuredLong(120, EvidenceKind.Actual),
            Cost = new MeasuredDecimal(null, EvidenceKind.Unavailable),
        };
        return new UsageRepository([external, nativeLedger]);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedUsageSource(IReadOnlyList<NativeUsageRecord> records) : IUsageSource
    {
        public IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default) => records;
    }

    private sealed class ThrowingUsageSource : IUsageSource
    {
        public IReadOnlyList<NativeUsageRecord> Read(CancellationToken cancellationToken = default) =>
            throw new InvalidDataException("bad native log");
    }

    private sealed class ProjectRepository(IReadOnlyList<AgentProject> projects) : IProjectRepository
    {
        public Task<IReadOnlyList<AgentProject>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(projects);
        public Task<AgentProject?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(projects.FirstOrDefault(item => item.Id == id));
        public Task UpsertAsync(AgentProject project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProfileRepository(Profile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Profile>>([profile]);
        public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(id == profile.Id ? profile : null);
        public Task<Profile?> GetDefaultAsync(CancellationToken cancellationToken = default) => Task.FromResult<Profile?>(profile);
        public Task UpsertAsync(Profile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyProviderRepository : IProviderRepository
    {
        public Task<IReadOnlyList<ProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProviderConfiguration>>([]);
        public Task<ProviderConfiguration?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ProviderConfiguration?>(null);
        public Task UpsertAsync(ProviderConfiguration provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyCredentialStore : ICredentialStore
    {
        public Task<bool> ExistsAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveAsync(string referenceId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string referenceId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string referenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UsageRepository(IReadOnlyList<UsageSnapshot> usage) : IUsageLedgerRepository
    {
        private readonly TaskGroupLedger ledger = new("group", "main", "gpt-5.6-sol", "high", Now, Now, [], Now);
        public Task UpsertTaskGroupAsync(TaskGroupLedger value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TaskGroupLedger?> GetTaskGroupAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<TaskGroupLedger?>(id == ledger.Id ? ledger : null);
        public Task<IReadOnlyList<TaskGroupLedger>> ListTaskGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskGroupLedger>>([ledger]);
        public Task AppendUsageAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsageSnapshot>> ListUsageAsync(string taskGroupId, CancellationToken cancellationToken = default) => Task.FromResult(usage);
    }

    private sealed class EmptyScheduler : IWorkerScheduler
    {
        public event EventHandler<SchedulerSnapshot>? SnapshotChanged { add { } remove { } }
        public SchedulerSnapshot Snapshot { get; } = new(SchedulerState.Ready, 0, [], null);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(bool force, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkerResultPacket> DispatchAsync(TaskPacket packet, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkerResultPacket> ReportNativeResultAsync(WorkerResultPacket result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkerResultPacket> MarkReviewingAsync(string taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkerResultPacket> MarkAdoptedAsync(string taskId, string summary, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScheduledDelegation>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
