using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using Microsoft.UI.Xaml;
using DomainRoutingMode = CodexAgentSwitch.Domain.Profiles.RoutingMode;
using DomainWorkerSource = CodexAgentSwitch.Domain.Profiles.WorkerSource;

namespace CodexAgentSwitch.App.ViewModels;

public sealed record SelectionOption<T>(T Value, string DisplayName, string Description = "");

public sealed record ProviderSelectionOption(string Id, string DisplayName);

public sealed class ProfileListItemViewModel(Profile profile)
{
    internal Profile Value { get; } = profile;

    public Guid Id => Value.Id;

    public string Name => Value.Name;

    public string KindLabel => Value.KindLabel;

    public string DefaultLabel => Value.DefaultLabel;

    public bool IsDefault => Value.IsDefault;

    public string ApprovalModeLabel => Value.ApprovalMode switch
    {
        ExecutionApprovalMode.Safe => "安全批准",
        ExecutionApprovalMode.FullAuto => "完全自动",
        _ => "自动批准",
    };
}

public sealed class ProfileEditorViewModel : INotifyPropertyChanged
{
    private readonly Profile? _source;
    private readonly bool _isNew;
    private string _name;
    private string _mainAgentSlot;
    private string _reasoningStrength;
    private ExecutionApprovalMode _approvalMode;
    private bool _workerEnabled;
    private WorkerSource _workerSource;
    private string _nativeWorkerSlot;
    private string _externalProviderId;
    private string _workerCount;
    private RoutingMode _routingMode;
    private FallbackAction _fallbackAction;
    private string _perTaskBudget;
    private string _dailyBudget;
    private string _monthlyBudget;
    private string _tokenLimit;
    private string _requestLimit;
    private string _currency;
    private IReadOnlyList<string> _mainAgentSlots = ["Sol", "Terra", "Luna"];
    private IReadOnlyList<string> _nativeWorkerSlots = ["Sol", "Terra", "Luna"];

    private ProfileEditorViewModel(
        Profile? source,
        bool isNew,
        string initialName,
        IReadOnlyList<ProviderSelectionOption> externalProviders)
    {
        _source = source;
        _isNew = isNew;
        var profile = source ?? Profile.CreateDefault(DateTimeOffset.UtcNow);
        _name = isNew ? initialName : profile.Name;
        _mainAgentSlot = SlotFromModel(profile.MainAgent.ModelId);
        _reasoningStrength = profile.MainAgent.ReasoningEffort is "low" or "medium" or "high" or "xhigh"
            ? profile.MainAgent.ReasoningEffort
            : "high";
        _approvalMode = profile.ApprovalMode;
        _workerEnabled = profile.WorkerPolicy.Enabled;
        _workerSource = profile.WorkerPolicy.Source == WorkerSource.ExternalProvider
            ? WorkerSource.ExternalProvider
            : WorkerSource.NativeCodex;
        _nativeWorkerSlot = SlotFromWorkerId(profile.WorkerPolicy.PreferredProviderId);
        _externalProviderId = profile.WorkerPolicy.Source == WorkerSource.ExternalProvider
            ? profile.WorkerPolicy.PreferredProviderId ?? string.Empty
            : string.Empty;
        ExternalProviders = EnsureSelectedProvider(externalProviders, _externalProviderId);
        _workerCount = profile.WorkerPolicy.MaxWorkers.ToString(CultureInfo.InvariantCulture);
        _routingMode = profile.WorkerPolicy.RoutingMode;
        _fallbackAction = profile.WorkerPolicy.FallbackAction;
        _perTaskBudget = Format(profile.Budget.PerTask);
        _dailyBudget = Format(profile.Budget.Daily);
        _monthlyBudget = Format(profile.Budget.Monthly);
        _tokenLimit = profile.Budget.TokenLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _requestLimit = profile.Budget.RequestLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _currency = profile.Budget.Currency;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ProfileEditorViewModel ForNew(Profile template, IReadOnlyList<ProviderSelectionOption> externalProviders) =>
        new(template, isNew: true, initialName: string.Empty, externalProviders);

    public static ProfileEditorViewModel ForCopy(Profile template, string uniqueName, IReadOnlyList<ProviderSelectionOption> externalProviders) =>
        new(template, isNew: true, initialName: uniqueName, externalProviders);

    public static ProfileEditorViewModel ForEdit(Profile profile, IReadOnlyList<ProviderSelectionOption> externalProviders) =>
        new(profile, isNew: false, initialName: profile.Name, externalProviders);

    public string DialogTitle => _isNew ? "新建方案" : "编辑方案";

    public IReadOnlyList<string> MainAgentSlots => _mainAgentSlots;

    public IReadOnlyList<SelectionOption<string>> ReasoningStrengthOptions { get; } =
    [
        new("low", "低", "适合简单、明确的任务"),
        new("medium", "中", "兼顾速度与分析深度"),
        new("high", "高", "适合复杂开发与审查"),
        new("xhigh", "极高", "用于最复杂且允许更高耗时的任务"),
    ];

    public IReadOnlyList<SelectionOption<ExecutionApprovalMode>> ApprovalModeOptions { get; } =
    [
        new(ExecutionApprovalMode.Safe, "安全模式", "只读沙箱；非可信命令与任何越界写入都必须经过批准。"),
        new(ExecutionApprovalMode.Automatic, "自动模式", "允许在项目工作区内正常读写；风险操作由代理请求批准。"),
        new(ExecutionApprovalMode.FullAuto, "完全自动", "不请求批准并使用不受限访问。仅应在完全可信的项目和任务中使用。"),
    ];

    public IReadOnlyList<SelectionOption<WorkerSource>> WorkerSourceOptions { get; } =
    [
        new(WorkerSource.NativeCodex, "原生工作代理", "使用 Codex 的 Sol、Terra 或 Luna"),
        new(WorkerSource.ExternalProvider, "外部服务商", "使用已配置并启用的外部服务商"),
    ];

    public IReadOnlyList<string> NativeWorkerSlots => _nativeWorkerSlots;

    public Visibility MainAgentUnavailableVisibility =>
        MainAgentSlots.Contains(MainAgentSlot, StringComparer.Ordinal) ? Visibility.Collapsed : Visibility.Visible;

    public string MainAgentUnavailableMessage =>
        $"当前 Codex 账户不支持主代理 {MainAgentSlot}。请改选可用主代理后再保存或启动。";

    public Visibility NativeWorkerUnavailableVisibility =>
        WorkerEnabled
        && WorkerSource == DomainWorkerSource.NativeCodex
        && !NativeWorkerSlots.Contains(NativeWorkerSlot, StringComparer.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string NativeWorkerUnavailableMessage =>
        $"当前 Codex 账户不支持原生 Worker {NativeWorkerSlot}。请改选可用 Worker，或改用外部服务商。";

    public IReadOnlyList<SelectionOption<RoutingMode>> RoutingModeOptions { get; } =
    [
        new(RoutingMode.Economic, "经济优先", "最多使用 1 个工作代理，优先控制额度和重复劳动。"),
        new(RoutingMode.Balanced, "平衡模式", "最多使用 2 个工作代理，在速度、质量与成本之间取平衡。"),
        new(RoutingMode.Performance, "性能优先", "最多使用 3 个工作代理，优先并行速度与结果覆盖。"),
        new(RoutingMode.Manual, "手动模式", "仅按用户明确指定的边界和数量调用工作代理。"),
        new(RoutingMode.Single, "单代理模式", "不启用工作代理，所有工作由主代理独立完成。"),
    ];

    public IReadOnlyList<SelectionOption<FallbackAction>> FallbackActionOptions { get; } =
    [
        new(FallbackAction.NativeLuna, "回退到原生 Luna"),
        new(FallbackAction.SingleAgent, "由主代理接管"),
        new(FallbackAction.AskUser, "询问用户"),
        new(FallbackAction.StopDelegation, "停止委派"),
    ];

    public IReadOnlyList<ProviderSelectionOption> ExternalProviders { get; }

    public Visibility WorkerSettingsVisibility => WorkerEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WorkerDisabledHintVisibility => WorkerEnabled ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NativeWorkerVisibility =>
        WorkerEnabled && WorkerSource == DomainWorkerSource.NativeCodex ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExternalProviderVisibility =>
        WorkerEnabled && WorkerSource == DomainWorkerSource.ExternalProvider ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExternalProviderEmptyVisibility =>
        ExternalProviderVisibility == Visibility.Visible && ExternalProviders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool HasExternalProviders => ExternalProviders.Count > 0;

    public string RoutingModeDescription =>
        RoutingModeOptions.First(option => option.Value == RoutingMode).Description;

    public SelectionOption<WorkerSource> SelectedWorkerSourceOption
    {
        get => WorkerSourceOptions.First(option => option.Value == WorkerSource);
        set
        {
            if (value is not null)
            {
                WorkerSource = value.Value;
            }
        }
    }

    public SelectionOption<string> SelectedReasoningStrengthOption
    {
        get => ReasoningStrengthOptions.First(option => option.Value == ReasoningStrength);
        set
        {
            if (value is not null)
            {
                ReasoningStrength = value.Value;
            }
        }
    }

    public SelectionOption<RoutingMode> SelectedRoutingModeOption
    {
        get => RoutingModeOptions.First(option => option.Value == RoutingMode);
        set
        {
            if (value is not null)
            {
                RoutingMode = value.Value;
            }
        }
    }

    public SelectionOption<ExecutionApprovalMode> SelectedApprovalModeOption
    {
        get => ApprovalModeOptions.First(option => option.Value == ApprovalMode);
        set
        {
            if (value is not null)
            {
                ApprovalMode = value.Value;
            }
        }
    }

    public SelectionOption<FallbackAction> SelectedFallbackActionOption
    {
        get => FallbackActionOptions.First(option => option.Value == FallbackAction);
        set
        {
            if (value is not null)
            {
                FallbackAction = value.Value;
            }
        }
    }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string MainAgentSlot
    {
        get => _mainAgentSlot;
        set
        {
            if (Set(ref _mainAgentSlot, value))
            {
                OnPropertyChanged(nameof(MainAgentUnavailableVisibility));
                OnPropertyChanged(nameof(MainAgentUnavailableMessage));
            }
        }
    }
    public ExecutionApprovalMode ApprovalMode
    {
        get => _approvalMode;
        set
        {
            if (Set(ref _approvalMode, value))
            {
                OnPropertyChanged(nameof(SelectedApprovalModeOption));
                OnPropertyChanged(nameof(ApprovalModeDescription));
                OnPropertyChanged(nameof(FullAutoWarningVisibility));
            }
        }
    }

    public string ApprovalModeDescription =>
        ApprovalModeOptions.First(option => option.Value == ApprovalMode).Description;

    public Visibility FullAutoWarningVisibility =>
        ApprovalMode == ExecutionApprovalMode.FullAuto ? Visibility.Visible : Visibility.Collapsed;
    public string ReasoningStrength
    {
        get => _reasoningStrength;
        set
        {
            if (Set(ref _reasoningStrength, value))
            {
                OnPropertyChanged(nameof(SelectedReasoningStrengthOption));
            }
        }
    }

    public bool WorkerEnabled
    {
        get => _workerEnabled;
        set
        {
            if (!Set(ref _workerEnabled, value))
            {
                return;
            }

            if (!value)
            {
                WorkerCount = "0";
                if (RoutingMode != DomainRoutingMode.Single)
                {
                    RoutingMode = DomainRoutingMode.Single;
                }
            }
            else
            {
                if (RoutingMode == DomainRoutingMode.Single)
                {
                    RoutingMode = DomainRoutingMode.Economic;
                }

                if (WorkerCount == "0")
                {
                    WorkerCount = "1";
                }
            }

            OnPropertyChanged(nameof(WorkerSettingsVisibility));
            OnPropertyChanged(nameof(WorkerDisabledHintVisibility));
            OnPropertyChanged(nameof(NativeWorkerVisibility));
            OnPropertyChanged(nameof(NativeWorkerUnavailableVisibility));
            OnPropertyChanged(nameof(ExternalProviderVisibility));
            OnPropertyChanged(nameof(ExternalProviderEmptyVisibility));
            OnPropertyChanged(nameof(SelectedWorkerSourceOption));
        }
    }

    public WorkerSource WorkerSource
    {
        get => _workerSource;
        set
        {
            if (!Set(ref _workerSource, value))
            {
                return;
            }

            OnPropertyChanged(nameof(NativeWorkerVisibility));
            OnPropertyChanged(nameof(ExternalProviderVisibility));
            OnPropertyChanged(nameof(ExternalProviderEmptyVisibility));
            OnPropertyChanged(nameof(SelectedWorkerSourceOption));
            if (value == DomainWorkerSource.ExternalProvider
                && string.IsNullOrWhiteSpace(ExternalProviderId)
                && ExternalProviders.Count > 0)
            {
                ExternalProviderId = ExternalProviders[0].Id;
            }
        }
    }
    public string NativeWorkerSlot
    {
        get => _nativeWorkerSlot;
        set
        {
            if (Set(ref _nativeWorkerSlot, value))
            {
                OnPropertyChanged(nameof(NativeWorkerUnavailableVisibility));
                OnPropertyChanged(nameof(NativeWorkerUnavailableMessage));
            }
        }
    }
    public string ExternalProviderId { get => _externalProviderId; set => Set(ref _externalProviderId, value); }
    public string WorkerCount { get => _workerCount; set => Set(ref _workerCount, value); }
    public RoutingMode RoutingMode
    {
        get => _routingMode;
        set
        {
            if (Set(ref _routingMode, value))
            {
                OnPropertyChanged(nameof(RoutingModeDescription));
                OnPropertyChanged(nameof(SelectedRoutingModeOption));
                if (value == DomainRoutingMode.Single)
                {
                    WorkerEnabled = false;
                }
            }
        }
    }
    public FallbackAction FallbackAction
    {
        get => _fallbackAction;
        set
        {
            if (Set(ref _fallbackAction, value))
            {
                OnPropertyChanged(nameof(SelectedFallbackActionOption));
            }
        }
    }
    public string PerTaskBudget { get => _perTaskBudget; set => Set(ref _perTaskBudget, value); }
    public string DailyBudget { get => _dailyBudget; set => Set(ref _dailyBudget, value); }
    public string MonthlyBudget { get => _monthlyBudget; set => Set(ref _monthlyBudget, value); }
    public string TokenLimit { get => _tokenLimit; set => Set(ref _tokenLimit, value); }
    public string RequestLimit { get => _requestLimit; set => Set(ref _requestLimit, value); }
    public string Currency { get => _currency; set => Set(ref _currency, value); }

    public Profile BuildProfile(DateTimeOffset now)
    {
        if (!MainAgentSlots.Contains(MainAgentSlot, StringComparer.Ordinal))
        {
            throw new FormatException(MainAgentUnavailableMessage);
        }

        if (WorkerEnabled
            && WorkerSource == DomainWorkerSource.NativeCodex
            && !NativeWorkerSlots.Contains(NativeWorkerSlot, StringComparer.Ordinal))
        {
            throw new FormatException(NativeWorkerUnavailableMessage);
        }

        var id = _isNew ? Guid.NewGuid() : _source!.Id;
        var source = WorkerEnabled ? this.WorkerSource : DomainWorkerSource.Disabled;
        var preferredWorker = !WorkerEnabled
            ? null
            : source == DomainWorkerSource.NativeCodex
                ? NativeWorkerIdForSlot(NativeWorkerSlot)
                : string.IsNullOrWhiteSpace(ExternalProviderId) ? null : ExternalProviderId.Trim();

        return new Profile(
            id,
            Name,
            new AgentSelection(ModelForSlot(MainAgentSlot), ReasoningStrength.Trim()),
            new WorkerPolicy(
                WorkerEnabled,
                source,
                preferredWorker,
                _source?.WorkerPolicy.FallbackProviderId,
                ParseInt(WorkerCount, "工作代理数量"),
                RoutingMode,
                FallbackAction),
            new BudgetLimits(
                ParseDecimal(PerTaskBudget, "单任务预算"),
                ParseDecimal(DailyBudget, "每日预算"),
                ParseDecimal(MonthlyBudget, "每月预算"),
                ParseLong(TokenLimit, "令牌上限"),
                ParseIntOrNull(RequestLimit, "请求上限"),
                Currency.Trim()),
            _isNew ? false : _source!.IsDefault,
            _isNew ? now : _source!.CreatedAt,
            now,
            _isNew ? null : _source!.LastUsedAt)
        {
            IsBuiltIn = _isNew ? false : _source!.IsBuiltIn,
            ApprovalMode = this.ApprovalMode,
        };
    }

    private static string SlotFromModel(string? modelId) => modelId switch
    {
        "gpt-5.6-terra" => "Terra",
        "gpt-5.6-luna" => "Luna",
        _ => "Sol",
    };

    private static string SlotFromWorkerId(string? workerId) => workerId switch
    {
        "native-terra" or "gpt-5.6-terra" => "Terra",
        "native-luna" or "gpt-5.6-luna" => "Luna",
        _ => "Sol",
    };

    private static string ModelForSlot(string slot) => slot switch
    {
        "Terra" => "gpt-5.6-terra",
        "Luna" => "gpt-5.6-luna",
        _ => "gpt-5.6-sol",
    };

    private static string NativeWorkerIdForSlot(string slot) => slot switch
    {
        "Terra" => "native-terra",
        "Luna" => "native-luna",
        _ => "native-sol",
    };

    public void SetAvailableNativeRoles(IEnumerable<string> roles)
    {
        var allowed = roles
            .Where(role => role is "Sol" or "Terra" or "Luna")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _mainAgentSlots = allowed;
        _nativeWorkerSlots = allowed;
        OnPropertyChanged(nameof(MainAgentSlots));
        OnPropertyChanged(nameof(NativeWorkerSlots));
        OnPropertyChanged(nameof(MainAgentUnavailableVisibility));
        OnPropertyChanged(nameof(MainAgentUnavailableMessage));
        OnPropertyChanged(nameof(NativeWorkerUnavailableVisibility));
        OnPropertyChanged(nameof(NativeWorkerUnavailableMessage));
    }

    private static string Format(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

    private static IReadOnlyList<ProviderSelectionOption> EnsureSelectedProvider(
        IReadOnlyList<ProviderSelectionOption> providers,
        string selectedProviderId)
    {
        if (string.IsNullOrWhiteSpace(selectedProviderId)
            || providers.Any(provider => string.Equals(provider.Id, selectedProviderId, StringComparison.Ordinal)))
        {
            return providers;
        }

        return providers
            .Append(new ProviderSelectionOption(selectedProviderId, $"{selectedProviderId}（已停用或已删除）"))
            .ToArray();
    }

    private static decimal? ParseDecimal(string value, string field) => ParseNullable<decimal>(value, field, TryParseDecimal);

    private static long? ParseLong(string value, string field) => ParseNullable<long>(value, field, TryParseLong);

    private static int ParseInt(string value, string field) => ParseIntOrNull(value, field) ?? 0;

    private static int? ParseIntOrNull(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{field}必须是数字。");
    }

    private static T? ParseNullable<T>(string value, string field, TryParseHandler<T> parser)
        where T : struct
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (parser(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{field}必须是数字。");
    }

    private delegate bool TryParseHandler<T>(string value, NumberStyles style, IFormatProvider provider, out T result);

    private static bool TryParseDecimal(string value, NumberStyles style, IFormatProvider provider, out decimal result) =>
        decimal.TryParse(value, style, provider, out result);

    private static bool TryParseLong(string value, NumberStyles style, IFormatProvider provider, out long result) =>
        long.TryParse(value, style, provider, out result);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
