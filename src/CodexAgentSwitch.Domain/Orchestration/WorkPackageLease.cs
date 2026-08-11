namespace CodexAgentSwitch.Domain.Orchestration;

/// <summary>Lifecycle states for the package currently owned by Main or Worker.</summary>
public enum WorkPackageLeaseStatus
{
    DISCOVERY,
    MAIN_OWNED,
    WORKER_OWNED,
    REVIEW,
    INVALID,
    COMPLETED,
}

public enum WorkPackageLifecycleEvent
{
    NewUserRequest,
    WorkerTerminalResult,
    WorkerReviewComplete,
    PackageComplete,
    NewPackage,
    CostCheckpoint,
}

/// <summary>
/// An in-memory ownership lease.  Persistence and scheduling layers may
/// serialize this model, but lifecycle rules deliberately live here so every
/// caller observes the same conservative transitions.
/// </summary>
public sealed class WorkPackageLease
{
    public const string MissingOwnershipFeedback =
        "Before substantive implementation, record ownership for the current package: MAIN with a valid reason, or WORKER with a bounded TaskPacket.";

    public WorkPackageLease(
        string packageId,
        string taskGroupId,
        string workingDirectory,
        WorkOwner owner,
        string packageKind,
        RepartitionReasonCode reason,
        RepartitionTrigger trigger,
        DateTimeOffset createdAt,
        int costWindowIndex,
        IReadOnlyList<string> declaredScopes,
        WorkPackageLeaseStatus status = WorkPackageLeaseStatus.DISCOVERY)
    {
        PackageId = Required(packageId, nameof(packageId));
        TaskGroupId = Required(taskGroupId, nameof(taskGroupId));
        WorkingDirectory = NormalizePath(Required(workingDirectory, nameof(workingDirectory)));
        PackageKind = Required(packageKind, nameof(packageKind));
        if (costWindowIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(costWindowIndex));
        }

        if (!RepartitionReasons.IsAllowed(owner, reason))
        {
            throw new ArgumentException("The ownership reason does not match the owner.", nameof(reason));
        }

        Owner = owner;
        Reason = reason;
        Trigger = trigger;
        CreatedAt = createdAt;
        CostWindowIndex = costWindowIndex;
        DeclaredScopes = (declaredScopes ?? throw new ArgumentNullException(nameof(declaredScopes)))
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Status = status;
    }

    public string PackageId { get; }
    public string TaskGroupId { get; }
    public string WorkingDirectory { get; }
    public WorkOwner Owner { get; }
    public string PackageKind { get; }
    public RepartitionReasonCode Reason { get; }
    public RepartitionTrigger Trigger { get; }
    public DateTimeOffset CreatedAt { get; }
    public int CostWindowIndex { get; private set; }
    public IReadOnlyList<string> DeclaredScopes { get; }
    public WorkPackageLeaseStatus Status { get; private set; }
    public string? InvalidReason { get; private set; }

    public bool IsUsable => Status is WorkPackageLeaseStatus.MAIN_OWNED or WorkPackageLeaseStatus.WORKER_OWNED;

    /// <summary>Apply a lifecycle event and throw on an invalid transition.</summary>
    public void Transition(WorkPackageLifecycleEvent lifecycleEvent)
    {
        if (!TryTransition(lifecycleEvent, out var error))
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>Apply a lifecycle event without silently changing state.</summary>
    public bool TryTransition(WorkPackageLifecycleEvent lifecycleEvent, out string? error)
    {
        error = null;
        var next = lifecycleEvent switch
        {
            WorkPackageLifecycleEvent.NewUserRequest when Status != WorkPackageLeaseStatus.INVALID
                => WorkPackageLeaseStatus.INVALID,
            WorkPackageLifecycleEvent.WorkerTerminalResult when Status == WorkPackageLeaseStatus.WORKER_OWNED
                => WorkPackageLeaseStatus.REVIEW,
            WorkPackageLifecycleEvent.WorkerReviewComplete when Status == WorkPackageLeaseStatus.REVIEW
                => WorkPackageLeaseStatus.INVALID,
            WorkPackageLifecycleEvent.PackageComplete when IsUsable || Status == WorkPackageLeaseStatus.REVIEW
                => WorkPackageLeaseStatus.COMPLETED,
            WorkPackageLifecycleEvent.NewPackage when Status != WorkPackageLeaseStatus.INVALID
                => WorkPackageLeaseStatus.INVALID,
            WorkPackageLifecycleEvent.CostCheckpoint when IsUsable || Status == WorkPackageLeaseStatus.REVIEW
                => WorkPackageLeaseStatus.INVALID,
            _ => (WorkPackageLeaseStatus?)null,
        };

        if (next is null)
        {
            error = $"Lifecycle event {lifecycleEvent} is invalid from status {Status}.";
            return false;
        }

        Status = next.Value;
        if (lifecycleEvent == WorkPackageLifecycleEvent.CostCheckpoint)
        {
            CostWindowIndex++;
        }

        if (Status == WorkPackageLeaseStatus.INVALID)
        {
            InvalidReason = lifecycleEvent switch
            {
                WorkPackageLifecycleEvent.WorkerReviewComplete => "Worker review completed; a new ownership decision is required.",
                WorkPackageLifecycleEvent.NewPackage => "A new package superseded this package.",
                WorkPackageLifecycleEvent.CostCheckpoint => "Cost checkpoint reached; a new ownership decision is required.",
                _ => "A new user request superseded this package.",
            };
        }

        return true;
    }

    public void Invalidate(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An invalidation reason is required.", nameof(reason));
        }

        if (Status == WorkPackageLeaseStatus.COMPLETED)
        {
            throw new InvalidOperationException("A completed package cannot be invalidated.");
        }

        Status = WorkPackageLeaseStatus.INVALID;
        InvalidReason = reason.Trim();
    }

    public void OnNewUserRequest() => Transition(WorkPackageLifecycleEvent.NewUserRequest);
    public void OnWorkerTerminalResult() => Transition(WorkPackageLifecycleEvent.WorkerTerminalResult);
    public void OnWorkerReviewComplete() => Transition(WorkPackageLifecycleEvent.WorkerReviewComplete);
    public void OnPackageComplete() => Transition(WorkPackageLifecycleEvent.PackageComplete);
    public void OnNewPackage() => Transition(WorkPackageLifecycleEvent.NewPackage);
    public void OnCostCheckpoint() => Transition(WorkPackageLifecycleEvent.CostCheckpoint);

    public bool Covers(string workingDirectory, string? scope = null)
    {
        if (Status is WorkPackageLeaseStatus.INVALID or WorkPackageLeaseStatus.COMPLETED)
        {
            return false;
        }

        var cwd = NormalizePath(workingDirectory);
        if (!PathContains(WorkingDirectory, cwd))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            return true;
        }

        var requested = NormalizePath(scope);
        return DeclaredScopes.Any(declared => PathContains(declared, requested));
    }

    public static string NormalizePath(string path)
    {
        var value = path.Trim().Replace('/', '\\');
        try
        {
            value = Path.GetFullPath(value);
        }
        catch (Exception) when (value.Length > 0)
        {
            // Keep a deterministic lexical representation for paths that are
            // not valid on the current host (for example a foreign drive).
        }

        return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathContains(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
}
