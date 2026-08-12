using CodexAgentSwitch.Application.Orchestration;
using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Tests.Orchestration;

public sealed class WorkPackageLeaseTests
{
    private static WorkPackageLease Lease(
        WorkOwner owner = WorkOwner.Main,
        WorkPackageLeaseStatus status = WorkPackageLeaseStatus.DISCOVERY) =>
        new(
            "pkg-1",
            "group-1",
            @"E:\repo",
            owner,
            "implementation",
            owner == WorkOwner.Main ? RepartitionReasonCode.FINAL_INTEGRATION : RepartitionReasonCode.BOUNDED_IMPLEMENTATION,
            RepartitionTrigger.INITIAL_LOCALIZATION_COMPLETE,
            DateTimeOffset.UnixEpoch,
            0,
            [@"E:\repo\src"],
            status);

    [Fact]
    public void Lifecycle_transitions_are_deterministic_and_invalid_transitions_do_not_mutate()
    {
        var lease = Lease(WorkOwner.Worker);
        Assert.False(lease.TryTransition(WorkPackageLifecycleEvent.WorkerTerminalResult, out _));
        Assert.Equal(WorkPackageLeaseStatus.DISCOVERY, lease.Status);

        lease.Transition(WorkPackageLifecycleEvent.NewPackage);
        Assert.Equal(WorkPackageLeaseStatus.INVALID, lease.Status);

        // A package enters WORKER_OWNED only when a new lease is created for it.
        var workerOwned = Lease(WorkOwner.Worker, WorkPackageLeaseStatus.WORKER_OWNED);
        workerOwned.Transition(WorkPackageLifecycleEvent.WorkerTerminalResult);
        Assert.Equal(WorkPackageLeaseStatus.REVIEW, workerOwned.Status);
        workerOwned.Transition(WorkPackageLifecycleEvent.WorkerReviewComplete);
        Assert.Equal(WorkPackageLeaseStatus.INVALID, workerOwned.Status);

        var checkpoint = Lease(WorkOwner.Main, WorkPackageLeaseStatus.MAIN_OWNED);
        checkpoint.Transition(WorkPackageLifecycleEvent.CostCheckpoint);
        Assert.Equal(WorkPackageLeaseStatus.INVALID, checkpoint.Status);
        Assert.Equal(1, checkpoint.CostWindowIndex);

        var completed = Lease(WorkOwner.Main, WorkPackageLeaseStatus.MAIN_OWNED);
        completed.Transition(WorkPackageLifecycleEvent.PackageComplete);
        Assert.Equal(WorkPackageLeaseStatus.COMPLETED, completed.Status);
    }

    [Fact]
    public void Main_gate_matches_normalized_working_directory_and_scope()
    {
        var lease = Lease(WorkOwner.Main, WorkPackageLeaseStatus.MAIN_OWNED);
        var gate = new MainToolOwnershipGate(lease);

        Assert.True(gate.Evaluate("apply_patch", @"e:/repo", @"e:/repo/src/file.cs").Allowed);
        Assert.False(gate.Evaluate("apply_patch", @"e:/other", @"e:/repo/src/file.cs").Allowed);
        Assert.False(gate.Evaluate("apply_patch", @"e:/repo", @"e:/repo").Allowed);
    }

    [Theory]
    [InlineData("Get-Content foo.cs", MutationKind.ReadOnly)]
    [InlineData("rg TODO src", MutationKind.ReadOnly)]
    [InlineData("dotnet test --no-restore", MutationKind.ReadOnly)]
    [InlineData("git diff", MutationKind.ReadOnly)]
    [InlineData("apply_patch < patch.diff", MutationKind.Mutation)]
    [InlineData("Set-Content foo.cs hi", MutationKind.Mutation)]
    [InlineData("echo hi > foo.txt", MutationKind.Mutation)]
    [InlineData("echo hi >foo.txt", MutationKind.Mutation)]
    [InlineData("echo hi >>foo.txt", MutationKind.Mutation)]
    [InlineData("git status; unknown-tool", MutationKind.Unknown)]
    [InlineData("rg TODO src | custom-tool", MutationKind.Unknown)]
    [InlineData("custom-tool --do-thing", MutationKind.Unknown)]
    public void Classifier_is_conservative(string command, MutationKind expected)
    {
        Assert.Equal(expected, MutationClassifier.Classify(command).Kind);
    }

    [Fact]
    public void Missing_ownership_feedback_is_exact_and_worker_duplicate_is_denied()
    {
        var missing = new MainToolOwnershipGate().Evaluate("apply_patch", @"E:\repo");
        Assert.Equal(WorkPackageLease.MissingOwnershipFeedback, missing.Message);

        var worker = new MainToolOwnershipGate(Lease(WorkOwner.Worker, WorkPackageLeaseStatus.WORKER_OWNED))
            .Evaluate("apply_patch", @"E:\repo", @"E:\repo\src");
        Assert.False(worker.Allowed);
        Assert.Contains("WORKER owns", worker.Message);
    }
}
