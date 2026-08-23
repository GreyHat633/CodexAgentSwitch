using CodexAgentSwitch.Application.Projects;
using CodexAgentSwitch.Application.Tasks;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Tasks;

namespace CodexAgentSwitch.Tests.Projects;

public sealed class ManagedProjectContextPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cas-managed-policy", Guid.NewGuid().ToString("N"));

    public ManagedProjectContextPolicyTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void Unmanaged_archived_and_unapplied_projects_fail_closed()
    {
        var policy = new ManagedProjectContextPolicy();
        var task = Task("project-a", root, "thread-a");

        Assert.Equal(ManagedContextRejectionCode.ProjectNotFound, policy.EvaluateEnrollment(null, task).Code);
        Assert.Equal(ManagedContextRejectionCode.ProjectArchived,
            policy.EvaluateEnrollment(Project("project-a", root) with { IsArchived = true }, task).Code);
        Assert.Equal(ManagedContextRejectionCode.AppliedSnapshotMissing,
            policy.EvaluateEnrollment(Project("project-a", root) with { NativeCodexAdaptation = null }, task).Code);
        Assert.Equal(ManagedContextRejectionCode.ProjectMismatch,
            policy.EvaluateEnrollment(Project("project-b", root), task).Code);
    }

    [Fact]
    public void Managed_project_root_and_descendants_are_automatically_eligible()
    {
        var child = Path.Combine(root, "nested");
        Directory.CreateDirectory(child);
        var policy = new ManagedProjectContextPolicy();
        var exact = Project("project-a", root);

        Assert.True(policy.EvaluateEnrollment(exact, Task("project-a", root, "thread-a")).Allowed);
        Assert.True(policy.EvaluateEnrollment(exact, Task("project-a", child, "thread-a")).Allowed);
    }

    [Fact]
    public void Longest_registered_root_wins_for_nested_projects()
    {
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var outer = Project("outer", root);
        var inner = Project("inner", nested);

        var owner = new ManagedProjectContextPolicy().ResolveOwner([outer, inner], nested);

        Assert.Equal("inner", owner!.Id);
    }

    [Theory]
    [InlineData(ManagedContextOwnershipState.Pending, false)]
    [InlineData(ManagedContextOwnershipState.Owned, true)]
    [InlineData(ManagedContextOwnershipState.Idle, true)]
    [InlineData(ManagedContextOwnershipState.Compacting, false)]
    [InlineData(ManagedContextOwnershipState.Verifying, false)]
    [InlineData(ManagedContextOwnershipState.Released, false)]
    [InlineData(ManagedContextOwnershipState.Lost, false)]
    [InlineData(ManagedContextOwnershipState.Faulted, false)]
    public void Only_owned_or_verified_idle_bindings_can_enter_control_decisions(
        ManagedContextOwnershipState state,
        bool expected)
    {
        var project = Project("project-a", root);
        var task = Task("project-a", root, "thread-a");
        var binding = Binding(project, task, state);

        var decision = new ManagedProjectContextPolicy().EvaluateControl(
            project, task, binding, "app-server-a", Identity(task));

        Assert.Equal(expected, decision.Allowed);
    }

    [Fact]
    public void Thread_session_connection_root_and_lease_must_all_match()
    {
        var policy = new ManagedProjectContextPolicy();
        var project = Project("project-a", root);
        var task = Task("project-a", root, "thread-a");
        var binding = Binding(project, task, ManagedContextOwnershipState.Owned);

        Assert.Equal(ManagedContextRejectionCode.ThreadNotBound,
            policy.EvaluateControl(project, task, binding with { ThreadId = "thread-b" }, "app-server-a", Identity(task)).Code);
        Assert.Equal(ManagedContextRejectionCode.SessionMismatch,
            policy.EvaluateControl(project, task, binding with { TaskSessionId = "task-session-b" }, "app-server-a", Identity(task)).Code);
        Assert.Equal(ManagedContextRejectionCode.ThreadIdentityMissing,
            policy.EvaluateControl(project, task, binding with { SessionId = "session-b" }, "app-server-a", Identity(task)).Code);
        Assert.Equal(ManagedContextRejectionCode.AppServerInstanceMismatch,
            policy.EvaluateControl(project, task, binding, "app-server-b", Identity(task)).Code);
        Assert.Equal(ManagedContextRejectionCode.OwnershipLeaseMissing,
            policy.EvaluateControl(project, task, binding with { OwnershipLeaseId = "" }, "app-server-a", Identity(task)).Code);
        Assert.Equal(ManagedContextRejectionCode.ProjectPathInvalid,
            policy.EvaluateControl(project, task, binding with { CanonicalProjectRoot = Path.GetTempPath() }, "app-server-a", Identity(task)).Code);
        Assert.Equal(ManagedContextRejectionCode.ProjectPathInvalid,
            policy.EvaluateControl(project, task, binding with { CanonicalProjectRoot = "\0" }, "app-server-a", Identity(task)).Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentProject Project(string id, string directory)
    {
        var profileId = Guid.NewGuid();
        var snapshot = new NativeCodexAppliedSnapshot(
            profileId, "Managed", "gpt-5.6-sol", "high", "NativeAgent", "cas_luna_worker",
            "gpt-5.6-luna", "openai", "high", 3, "Economic", "Supported", "fixture");
        return new AgentProject(
            id,
            id,
            directory,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            profileId,
            new NativeCodexProjectAdaptation(
                profileId,
                "Managed",
                Path.Combine(directory, ".codex", "config.toml"),
                null,
                DateTimeOffset.UtcNow,
                "managed",
                false,
                snapshot));
    }

    private static ControlledTaskSession Task(string projectId, string directory, string threadId) => new(
        "session-a",
        Guid.NewGuid(),
        "Managed",
        "Managed task",
        directory,
        "gpt-5.6-sol",
        "high",
        threadId,
        ControlledTaskStatus.Completed,
        [],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        null,
        projectId);

    private static ManagedContextSession Binding(
        AgentProject project,
        ControlledTaskSession task,
        ManagedContextOwnershipState state) => new(
        project.Id,
        Path.GetFullPath(project.WorkingDirectory),
        task.MainThreadId!,
        "app-session-a",
        task.Id,
        "app-server-a",
        "lease-a",
        state);

    private static MainAgentThreadIdentity Identity(ControlledTaskSession task) => new(
        task.MainThreadId!,
        "app-session-a",
        task.WorkingDirectory);
}
