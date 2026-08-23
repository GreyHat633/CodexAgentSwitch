using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Scheduling;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Scheduling;

public sealed class MainContextBoundaryIntegrationTests
{
    [Fact]
    public async Task Legacy_stop_boundary_is_frozen_and_does_not_bind_or_compact()
    {
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new FixedClock());

        var result = await scheduler.ObserveMainContextBoundaryAsync(new(
            "session-desktop", "thread-desktop", "E:\\AISPace\\ordinary-project", "vscode", "stop"));

        Assert.False(result.BindingAccepted);
        Assert.False(result.TelemetryAvailable);
        Assert.False(result.CompactionRequested);
        Assert.False(result.CompactionSucceeded);
        Assert.Equal(ContextEconomyState.Idle, result.State);
        Assert.Contains("FrozenDisabled", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frozen_boundary_does_not_accumulate_hook_or_context_diagnostics()
    {
        await using var scheduler = new WorkerScheduler([], new MemoryRepository(), new FixedClock());

        await scheduler.ObserveMainContextBoundaryAsync(new(
            "session-1", "thread-1", "E:\\AISPace\\managed-project", "vscode", "stop"));
        var runtime = await scheduler.GetRuntimeDiagnosticsAsync();

        Assert.Null(runtime.ContextEconomy);
        var hooks = Assert.IsType<HookRuntimeDiagnostics>(runtime.Hooks);
        Assert.Equal(HookLifecycleState.FrozenDisabled, hooks.LifecycleState);
        Assert.Equal(HardGateLifecycleState.Disabled, hooks.HardGateState);
        Assert.Equal(0, hooks.StopSeenCount);
        Assert.Equal(0, hooks.ContextBoundarySeenCount);
        Assert.Equal(0, hooks.HardGateShadowEvaluatedCount);
        Assert.Equal(0, hooks.HardGateDeniedCount);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemoryRepository : ISchedulerTaskRepository
    {
        public Task<ScheduledDelegation?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ScheduledDelegation?>(null);

        public Task<IReadOnlyList<ScheduledDelegation>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledDelegation>>([]);

        public Task UpsertAsync(ScheduledDelegation task, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RepartitionTelemetry>> ListRepartitionsAsync(
            string taskGroupId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RepartitionTelemetry>>([]);

        public Task AppendRepartitionAsync(RepartitionTelemetry telemetry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
