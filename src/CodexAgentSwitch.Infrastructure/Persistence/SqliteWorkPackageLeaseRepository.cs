using System.Globalization;
using System.Text.Json;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Orchestration;
using Microsoft.Data.Sqlite;

namespace CodexAgentSwitch.Infrastructure.Persistence;

/// <summary>Append-only durable lease history; active selection is deterministic.</summary>
public sealed class SqliteWorkPackageLeaseRepository(SqliteDatabase database) : IWorkPackageLeaseRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkPackageLease?> GetActiveAsync(string packageId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var cwd = WorkPackageLease.NormalizePath(workingDirectory);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM work_package_leases
            WHERE package_id = $package_id AND working_directory = $cwd
            ORDER BY rowid DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$package_id", packageId);
        command.Parameters.AddWithValue("$cwd", cwd);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return ActiveOrNull(value);
    }

    public async Task<WorkPackageLease?> GetActiveForWorkingDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var cwd = WorkPackageLease.NormalizePath(workingDirectory);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM work_package_leases
            WHERE working_directory = $cwd
            ORDER BY rowid DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$cwd", cwd);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return ActiveOrNull(value);
    }

    public async Task<IReadOnlyList<WorkPackageLease>> ListAsync(string? packageId = null, CancellationToken cancellationToken = default)
    {
        var result = new List<WorkPackageLease>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = packageId is null
            ? "SELECT payload_json FROM work_package_leases ORDER BY created_at ASC, lease_id ASC"
            : "SELECT payload_json FROM work_package_leases WHERE package_id = $package_id ORDER BY created_at ASC, lease_id ASC";
        if (packageId is not null) command.Parameters.AddWithValue("$package_id", packageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize(reader.GetString(0)));
        return result;
    }

    public async Task SaveAsync(WorkPackageLease lease, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_package_leases(lease_id, package_id, working_directory, status, created_at, payload_json)
            VALUES($id, $package_id, $cwd, $status, $created_at, $payload)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$package_id", lease.PackageId);
        command.Parameters.AddWithValue("$cwd", lease.WorkingDirectory);
        command.Parameters.AddWithValue("$status", (int)lease.Status);
        command.Parameters.AddWithValue("$created_at", lease.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(lease, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WorkPackageLease Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<LeaseDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Stored work-package lease JSON is invalid.");
        var lease = new WorkPackageLease(dto.PackageId, dto.TaskGroupId, dto.WorkingDirectory, dto.Owner,
            dto.PackageKind, dto.Reason, dto.Trigger, dto.CreatedAt, dto.CostWindowIndex,
            dto.DeclaredScopes ?? [], dto.Status is WorkPackageLeaseStatus.DISCOVERY or WorkPackageLeaseStatus.MAIN_OWNED or WorkPackageLeaseStatus.WORKER_OWNED
                ? dto.Status : dto.Owner == WorkOwner.Worker ? WorkPackageLeaseStatus.WORKER_OWNED : WorkPackageLeaseStatus.MAIN_OWNED);
        if (dto.Status == WorkPackageLeaseStatus.REVIEW && lease.Owner == WorkOwner.Worker)
        {
            lease.OnWorkerTerminalResult();
        }
        else if (dto.Status == WorkPackageLeaseStatus.INVALID)
        {
            lease.Invalidate(dto.InvalidReason ?? "Restored invalid lease.");
        }
        else if (dto.Status == WorkPackageLeaseStatus.COMPLETED)
        {
            lease.OnPackageComplete();
        }
        return lease;
    }

    private static WorkPackageLease? ActiveOrNull(object? value)
    {
        if (value is not string json) return null;
        var lease = Deserialize(json);
        return lease.Status is WorkPackageLeaseStatus.INVALID or WorkPackageLeaseStatus.COMPLETED ? null : lease;
    }

    private sealed record LeaseDto(
        string PackageId,
        string TaskGroupId,
        string WorkingDirectory,
        WorkOwner Owner,
        string PackageKind,
        RepartitionReasonCode Reason,
        RepartitionTrigger Trigger,
        DateTimeOffset CreatedAt,
        int CostWindowIndex,
        IReadOnlyList<string>? DeclaredScopes,
        WorkPackageLeaseStatus Status,
        string? InvalidReason);
}
