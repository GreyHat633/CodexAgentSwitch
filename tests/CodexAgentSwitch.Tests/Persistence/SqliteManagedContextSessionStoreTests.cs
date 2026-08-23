using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Tests.Persistence;

public sealed class SqliteManagedContextSessionStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("CAS_TEST_ROOT")
            ?? throw new InvalidOperationException("CAS_TEST_ROOT must point to an E-drive test directory."),
        "managed-context-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Session_round_trips_by_task_and_thread_with_auditable_identity()
    {
        var store = await CreateStoreAsync();
        var session = NewSession("task-a", "thread-a", "lease-a") with
        {
            LastTokenUsageAt = DateTimeOffset.Parse("2026-08-22T02:00:00Z"),
            LastSafeBoundaryAt = DateTimeOffset.Parse("2026-08-22T02:01:00Z"),
        };

        await store.UpsertAsync(session);

        var byTask = await store.LoadByTaskSessionAsync("task-a");
        var byThread = await store.LoadByThreadAsync("thread-a");
        Assert.Equal(session with { UpdatedAt = byTask!.UpdatedAt }, byTask);
        Assert.Equal(byTask, byThread);
        Assert.NotNull(byTask.UpdatedAt);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Same_task_can_advance_state_without_changing_identity()
    {
        var store = await CreateStoreAsync();
        var owned = NewSession("task-a", "thread-a", "lease-a");
        await store.UpsertAsync(owned);

        await store.UpsertAsync(owned with
        {
            OwnershipState = ManagedContextOwnershipState.Idle,
            LastSafeBoundaryAt = DateTimeOffset.UtcNow,
        });

        var loaded = await store.LoadByTaskSessionAsync("task-a");
        Assert.Equal(ManagedContextOwnershipState.Idle, loaded!.OwnershipState);
        Assert.Equal("lease-a", loaded.OwnershipLeaseId);
    }

    [Fact]
    public async Task Conditional_update_cannot_overwrite_a_replaced_ownership_lease()
    {
        var store = await CreateStoreAsync();
        var first = NewSession("task-a", "thread-a", "lease-a");
        await store.UpsertAsync(first);
        await store.UpsertAsync(first with { OwnershipLeaseId = "lease-b" });

        var staleWrite = await store.TryUpdateLeaseAsync(
            first with { OwnershipState = ManagedContextOwnershipState.Idle },
            expectedOwnershipLeaseId: "lease-a");

        Assert.False(staleWrite);
        var loaded = await store.LoadByTaskSessionAsync("task-a");
        Assert.Equal("lease-b", loaded!.OwnershipLeaseId);
        Assert.Equal(ManagedContextOwnershipState.Owned, loaded.OwnershipState);
    }

    [Fact]
    public async Task Thread_and_lease_cannot_be_owned_by_two_task_sessions()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(NewSession("task-a", "thread-a", "lease-a"));

        await Assert.ThrowsAsync<SqliteException>(
            () => store.UpsertAsync(NewSession("task-b", "thread-a", "lease-b")));
        await Assert.ThrowsAsync<SqliteException>(
            () => store.UpsertAsync(NewSession("task-b", "thread-b", "lease-a")));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Delete_releases_only_the_exact_task_session_record()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(NewSession("task-a", "thread-a", "lease-a"));
        await store.UpsertAsync(NewSession("task-b", "thread-b", "lease-b"));

        await store.DeleteAsync("task-a");

        Assert.Null(await store.LoadByTaskSessionAsync("task-a"));
        Assert.NotNull(await store.LoadByTaskSessionAsync("task-b"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<SqliteManagedContextSessionStore> CreateStoreAsync()
    {
        var database = new SqliteDatabase(Path.Combine(directory, "state.db"));
        await database.InitializeAsync();
        return new SqliteManagedContextSessionStore(database);
    }

    private static ManagedContextSession NewSession(string taskSessionId, string threadId, string leaseId) => new(
        "project-a",
        "E:\\managed",
        threadId,
        "app-session-a",
        taskSessionId,
        "app-server-a",
        leaseId,
        ManagedContextOwnershipState.Owned);
}
