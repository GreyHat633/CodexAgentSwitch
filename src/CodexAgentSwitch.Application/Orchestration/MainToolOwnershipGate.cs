using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Application.Orchestration;

public enum OwnershipGateDecisionKind
{
    Allowed,
    Denied,
}

public sealed record OwnershipGateDecision(
    OwnershipGateDecisionKind Kind,
    MutationClassification Classification,
    string Message)
{
    public bool Allowed => Kind == OwnershipGateDecisionKind.Allowed;
}

/// <summary>Guards Main's tools against unrecorded or duplicate implementation.</summary>
public sealed class MainToolOwnershipGate
{
    public const string MissingOwnershipFeedback = WorkPackageLease.MissingOwnershipFeedback;

    private WorkPackageLease? lease;

    public MainToolOwnershipGate(WorkPackageLease? lease = null) => this.lease = lease;

    public WorkPackageLease? Lease => lease;

    public void SetLease(WorkPackageLease? value) => lease = value;

    public OwnershipGateDecision Evaluate(string? command, string workingDirectory, string? scope = null)
    {
        var classification = MutationClassifier.Classify(command);
        if (classification.IsReadOnly)
        {
            return new(OwnershipGateDecisionKind.Allowed, classification, "Read-only operation is allowed.");
        }

        if (classification.IsUnknown)
        {
            return new(OwnershipGateDecisionKind.Denied, classification,
                "Unknown operation remains explicit; ownership cannot be inferred safely.");
        }

        if (lease is null || lease.Status is WorkPackageLeaseStatus.DISCOVERY or WorkPackageLeaseStatus.INVALID or WorkPackageLeaseStatus.COMPLETED)
        {
            return new(OwnershipGateDecisionKind.Denied, classification, MissingOwnershipFeedback);
        }

        if (lease.Owner == WorkOwner.Worker)
        {
            return new(OwnershipGateDecisionKind.Denied, classification,
                "WORKER owns this package; duplicate substantive Main work is denied.");
        }

        if (lease.Status != WorkPackageLeaseStatus.MAIN_OWNED || !lease.Covers(workingDirectory, scope))
        {
            return new(OwnershipGateDecisionKind.Denied, classification,
                "MAIN ownership lease is invalid or does not cover the requested working directory and scope.");
        }

        return new(OwnershipGateDecisionKind.Allowed, classification, "MAIN ownership lease covers this operation.");
    }

}
