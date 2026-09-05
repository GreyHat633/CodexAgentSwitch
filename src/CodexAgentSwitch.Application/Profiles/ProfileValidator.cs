using CodexAgentSwitch.Domain.Common;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Application.Profiles;

public sealed class ProfileValidator
{
    public ValidationResult Validate(Profile profile)
    {
        var issues = new List<ValidationIssue>();
        if (profile.RequiresRepair)
        {
            issues.Add(new("profile.repair.required", profile.RepairMessage ?? "该方案需要修复后才能保存。", "Profile"));
            return new ValidationResult(issues);
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            issues.Add(new("profile.name.required", "配置方案名称不能为空。", nameof(profile.Name)));
        }
        else if (profile.Name.Trim().Length > 80)
        {
            issues.Add(new("profile.name.too_long", "配置方案名称不能超过 80 个字符。", nameof(profile.Name)));
        }

        if (string.IsNullOrWhiteSpace(profile.MainAgent.ModelId))
        {
            issues.Add(new("profile.main_agent.required", "必须选择主代理。", "MainAgent.ModelId"));
        }

        if (string.IsNullOrWhiteSpace(profile.MainAgent.ReasoningEffort))
        {
            issues.Add(new("profile.reasoning.required", "必须选择当前模型实际支持的推理强度。", "MainAgent.ReasoningEffort"));
        }

        var maxWorkers = profile.WorkerPolicy.MaxWorkers;
        if (maxWorkers is < 0 or > 3)
        {
            issues.Add(new("profile.workers.range", "Worker 数量必须在 0 到 3 之间。", "WorkerPolicy.MaxWorkers"));
        }

        if (!profile.WorkerPolicy.Enabled && (maxWorkers != 0 || profile.WorkerPolicy.Source != WorkerSource.Disabled))
        {
            issues.Add(new("profile.workers.disabled_count", "停用 Worker 时最大并发必须为 0。", "WorkerPolicy.MaxWorkers"));
        }

        if (profile.WorkerPolicy.Enabled && (maxWorkers == 0 || profile.WorkerPolicy.Source == WorkerSource.Disabled))
        {
            issues.Add(new("profile.workers.enabled_count", "启用 Worker 时最大并发至少为 1。", "WorkerPolicy.MaxWorkers"));
        }

        if (profile.WorkerPolicy.Source == WorkerSource.ExternalProvider
            && string.IsNullOrWhiteSpace(profile.WorkerPolicy.PreferredProviderId))
        {
            issues.Add(new("profile.provider.required", "外部 Worker 必须选择 Provider。", "WorkerPolicy.PreferredProviderId"));
        }

        if (profile.WorkerPolicy.Enabled
            && profile.WorkerPolicy.Source == WorkerSource.NativeCodex
            && string.IsNullOrWhiteSpace(profile.WorkerPolicy.PreferredProviderId))
        {
            issues.Add(new("profile.native_worker.required", "启用 Native Worker 时必须选择 Astra、Sol、Terra 或 Luna。", "WorkerPolicy.PreferredProviderId"));
        }

        if (profile.AutoCompactTokenLimit is not (null or 150_000 or 180_000 or 200_000))
        {
            issues.Add(new(
                "profile.auto_compact.invalid",
                "自动压缩阈值必须为默认、150K、180K 或 200K。",
                nameof(profile.AutoCompactTokenLimit)));
        }

        ValidateNonNegative(profile.Budget.PerTask, "Budget.PerTask", issues);
        ValidateNonNegative(profile.Budget.Daily, "Budget.Daily", issues);
        ValidateNonNegative(profile.Budget.Monthly, "Budget.Monthly", issues);
        ValidateNonNegative(profile.Budget.TokenLimit, "Budget.TokenLimit", issues);
        ValidateNonNegative(profile.Budget.RequestLimit, "Budget.RequestLimit", issues);
        if (string.IsNullOrWhiteSpace(profile.Budget.Currency))
        {
            issues.Add(new("profile.currency.required", "预算币种不能为空。", "Budget.Currency"));
        }

        return new ValidationResult(issues);
    }

    public ValidationResult ValidateUniqueName(Profile profile, IEnumerable<Profile> existingProfiles)
    {
        var result = Validate(profile);
        var issues = result.Issues.ToList();
        if (!string.IsNullOrWhiteSpace(profile.Name)
            && existingProfiles.Any(existing => existing.Id != profile.Id
                && string.Equals(existing.Name.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new("profile.name.duplicate", "配置方案名称已存在，请使用其他名称。", nameof(profile.Name)));
        }

        return new ValidationResult(issues);
    }

    private static void ValidateNonNegative(decimal? value, string field, ICollection<ValidationIssue> issues)
    {
        if (value is < 0)
        {
            issues.Add(new("profile.budget.negative", "预算不能为负数。", field));
        }
    }

    private static void ValidateNonNegative(long? value, string field, ICollection<ValidationIssue> issues)
    {
        if (value is < 0)
        {
            issues.Add(new("profile.budget.negative", "预算不能为负数。", field));
        }
    }

    private static void ValidateNonNegative(int? value, string field, ICollection<ValidationIssue> issues)
    {
        if (value is < 0)
        {
            issues.Add(new("profile.budget.negative", "预算不能为负数。", field));
        }
    }
}
