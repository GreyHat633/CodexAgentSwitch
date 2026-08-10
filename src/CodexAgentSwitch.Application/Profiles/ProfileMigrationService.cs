using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Application.Profiles;

public sealed record ProfileMigrationSummary(int Migrated, int NeedsRepair);

/// <summary>
/// Normalizes persisted profile payloads once, before any view model binds to
/// them. This deliberately keeps an irrecoverable payload visible as a repair
/// item instead of letting a legacy enum or null object crash the editor.
/// </summary>
public sealed class ProfileMigrationService(
    IProfileRepository repository,
    IClock clock)
{
    public async Task<ProfileMigrationSummary> MigrateAllAsync(CancellationToken cancellationToken = default)
    {
        var migrated = 0;
        var needsRepair = 0;
        foreach (var profile in await repository.ListAsync(cancellationToken))
        {
            var result = ProfileDataMigration.Migrate(profile, clock.UtcNow);
            if (result.Profile.RequiresRepair)
            {
                needsRepair++;
                continue;
            }

            if (!result.Changed)
            {
                continue;
            }

            await repository.UpsertAsync(result.Profile, cancellationToken);
            migrated++;
        }

        return new ProfileMigrationSummary(migrated, needsRepair);
    }
}

public sealed record ProfileMigrationResult(Profile Profile, bool Changed);

public static class ProfileDataMigration
{
    public static ProfileMigrationResult Migrate(Profile profile, DateTimeOffset now)
    {
        if (profile.RequiresRepair)
        {
            return new ProfileMigrationResult(profile, false);
        }

        if (profile.Id == Guid.Empty)
        {
            return Repair(profile, "该方案的唯一标识无效，无法安全迁移。请删除后重新创建。", now);
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return Repair(profile, "该方案缺少名称，无法安全迁移。请删除后重新创建。", now);
        }

        var mainAgent = profile.MainAgent ?? new AgentSelection("gpt-5.6-sol", "high");
        var model = NormalizeModel(mainAgent.ModelId);
        var effort = NormalizeReasoning(mainAgent.ReasoningEffort);

        var worker = profile.WorkerPolicy ?? new WorkerPolicy(
            false,
            WorkerSource.Disabled,
            null,
            null,
            0,
            RoutingMode.Single,
            FallbackAction.SingleAgent);
        var workerEffort = NormalizeReasoning(worker.ReasoningEffort);
        var source = NormalizeSource(worker.Source, worker.Enabled, profile.SchemaVersion);
        var enabled = worker.Enabled && source != WorkerSource.Disabled;
        var maxWorkers = Math.Clamp(worker.MaxWorkers, 0, 3);
        if (enabled && maxWorkers == 0)
        {
            maxWorkers = 1;
        }

        if (!enabled)
        {
            source = WorkerSource.Disabled;
            maxWorkers = 0;
        }

        var preferredProvider = source switch
        {
            WorkerSource.NativeCodex => NormalizeNativeWorker(worker.PreferredProviderId),
            WorkerSource.ExternalProvider when !string.IsNullOrWhiteSpace(worker.PreferredProviderId) => worker.PreferredProviderId.Trim(),
            _ => null,
        };
        if (source == WorkerSource.ExternalProvider && string.IsNullOrWhiteSpace(preferredProvider))
        {
            // A legacy external selection without a Provider cannot be executed.
            // Preserve the profile by making it a valid single-agent profile.
            enabled = false;
            source = WorkerSource.Disabled;
            maxWorkers = 0;
        }

        var routing = NormalizeRouting(worker.RoutingMode, enabled);
        var fallback = Enum.IsDefined(worker.FallbackAction)
            ? worker.FallbackAction
            : FallbackAction.SingleAgent;
        var budget = profile.Budget ?? new BudgetLimits(null, null, null, null, null, "CNY");
        var normalizedBudget = new BudgetLimits(
            NonNegative(budget.PerTask),
            NonNegative(budget.Daily),
            NonNegative(budget.Monthly),
            NonNegative(budget.TokenLimit),
            NonNegative(budget.RequestLimit),
            string.IsNullOrWhiteSpace(budget.Currency) ? "CNY" : budget.Currency.Trim());
        var approval = Enum.IsDefined(profile.ApprovalMode)
            ? profile.ApprovalMode
            : ExecutionApprovalMode.Automatic;
        var externalWorkerPermission = Enum.IsDefined(profile.ExternalWorkerPermission)
            ? profile.ExternalWorkerPermission
            : ExternalWorkerPermissionMode.WorkspaceFullAccess;

        var normalized = profile with
        {
            Name = profile.Name.Trim(),
            MainAgent = new AgentSelection(model, effort),
            WorkerPolicy = new WorkerPolicy(
                enabled,
                source,
                preferredProvider,
                worker.FallbackProviderId,
                maxWorkers,
                routing,
                fallback,
                workerEffort),
            Budget = normalizedBudget,
            ApprovalMode = approval,
            ExternalWorkerPermission = externalWorkerPermission,
            SchemaVersion = Profile.CurrentSchemaVersion,
            RepairMessage = null,
            UpdatedAt = profile.SchemaVersion == Profile.CurrentSchemaVersion ? profile.UpdatedAt : now,
        };
        return new ProfileMigrationResult(normalized, !Equals(normalized, profile));
    }

    private static ProfileMigrationResult Repair(Profile profile, string message, DateTimeOffset now) =>
        new(profile with
        {
            SchemaVersion = Profile.CurrentSchemaVersion,
            RepairMessage = message,
            UpdatedAt = now,
        }, true);

    private static string NormalizeModel(string? modelId) => modelId?.Trim() switch
    {
        "sol" => "gpt-5.6-sol",
        "terra" => "gpt-5.6-terra",
        "luna" => "gpt-5.6-luna",
        "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" => modelId.Trim(),
        _ => "gpt-5.6-sol",
    };

    private static string NormalizeReasoning(string? effort) => effort?.Trim() switch
    {
        "low" or "medium" or "high" or "xhigh" => effort.Trim(),
        _ => "high",
    };

    private static WorkerSource NormalizeSource(WorkerSource source, bool enabled, int schemaVersion)
    {
        if (!enabled)
        {
            return WorkerSource.Disabled;
        }

        if (!Enum.IsDefined(source))
        {
            return WorkerSource.Disabled;
        }

        // The earliest payloads encoded an enabled native worker as zero.
        return schemaVersion == 0 && source == WorkerSource.Disabled
            ? WorkerSource.NativeCodex
            : source;
    }

    private static string NormalizeNativeWorker(string? workerId) => workerId?.Trim() switch
    {
        "native-sol" or "gpt-5.6-sol" or "sol" => "native-sol",
        "native-terra" or "gpt-5.6-terra" or "terra" => "native-terra",
        "native-luna" or "gpt-5.6-luna" or "luna" => "native-luna",
        _ => "native-luna",
    };

    private static RoutingMode NormalizeRouting(RoutingMode mode, bool workerEnabled)
    {
        if (!workerEnabled)
        {
            return RoutingMode.Single;
        }

        return Enum.IsDefined(mode) && mode != RoutingMode.Single
            ? mode
            : RoutingMode.Economic;
    }

    private static decimal? NonNegative(decimal? value) => value is < 0 ? null : value;

    private static long? NonNegative(long? value) => value is < 0 ? null : value;

    private static int? NonNegative(int? value) => value is < 0 ? null : value;
}
