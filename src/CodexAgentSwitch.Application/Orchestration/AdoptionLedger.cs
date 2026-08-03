using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Domain.Orchestration;

namespace CodexAgentSwitch.Application.Orchestration;

public sealed class AdoptionLedger(IClock clock)
{
    private readonly Dictionary<string, AdoptionRecord> records = new(StringComparer.Ordinal);

    public AdoptionRecord Start(string jobId, string plannedSkippedWork, ReviewLevel reviewLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plannedSkippedWork);
        if (records.ContainsKey(jobId))
        {
            throw new InvalidOperationException($"Adoption record already exists: {jobId}");
        }

        var record = new AdoptionRecord(jobId, AdoptionStatus.Pending, plannedSkippedWork, null, false, null, reviewLevel, null, clock.UtcNow);
        records.Add(jobId, record);
        return record;
    }

    public AdoptionRecord Decide(
        string jobId,
        AdoptionStatus status,
        string actualSkippedWork,
        string? rejectionReason = null)
    {
        if (status == AdoptionStatus.Pending)
        {
            throw new ArgumentException("A terminal adoption decision is required.", nameof(status));
        }

        if (status == AdoptionStatus.Rejected && string.IsNullOrWhiteSpace(rejectionReason))
        {
            throw new ArgumentException("Rejected result requires a reason.", nameof(rejectionReason));
        }

        var current = Get(jobId);
        if (current.Status != AdoptionStatus.Pending)
        {
            throw new InvalidOperationException("Adoption decision is immutable after review.");
        }

        var updated = current with
        {
            Status = status,
            ActualSkippedWork = actualSkippedWork,
            RejectionReason = rejectionReason,
            UpdatedAt = clock.UtcNow,
        };
        records[jobId] = updated;
        return updated;
    }

    public AdoptionRecord RecordDuplicate(string jobId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var current = Get(jobId);
        var updated = current with { DuplicateWork = true, DuplicateReason = reason, UpdatedAt = clock.UtcNow };
        records[jobId] = updated;
        return updated;
    }

    public bool CanPerformFullTakeover(string jobId) => Get(jobId).Status == AdoptionStatus.Rejected;

    public AdoptionRecord Get(string jobId) => records.TryGetValue(jobId, out var record)
        ? record
        : throw new KeyNotFoundException($"Adoption record not found: {jobId}");
}
