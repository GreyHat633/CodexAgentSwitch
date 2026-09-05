using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Workers;
using Microsoft.UI.Xaml;
using DomainRoutingMode = CodexAgentSwitch.Domain.Profiles.RoutingMode;
using DomainWorkerSource = CodexAgentSwitch.Domain.Profiles.WorkerSource;
using DomainFallbackAction = CodexAgentSwitch.Domain.Profiles.FallbackAction;

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

    public Visibility DefaultVisibility => Value.IsDefault ? Visibility.Visible : Visibility.Collapsed;

    public bool IsDefault => Value.IsDefault;

    public string ApprovalModeLabel => Value.ApprovalMode switch
    {
        ExecutionApprovalMode.Safe => "安全批准",
        ExecutionApprovalMode.FullAuto => "完全自动",
        _ => "自动批准",
    };

    public string ExternalWorkerPermissionLabel => Value.ExternalWorkerPermission switch
    {
        ExternalWorkerPermissionMode.ReadOnly => "外部只读",
        ExternalWorkerPermissionMode.FullAccess => "外部完全访问",
        _ => "外部工作区访问",
    };

    public Visibility ExternalWorkerPermissionVisibility =>
        Value.WorkerPolicy.Enabled && Value.WorkerPolicy.Source == WorkerSource.ExternalProvider
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool RequiresRepair => Value.RequiresRepair;

    public string RepairMessage => Value.RepairMessage ?? string.Empty;
}

public sealed class ProfileEditorViewModel : INotifyPropertyChanged
{
    private readonly Profile? _source;
    private readonly bool _isNew;
    private string _name;
    private string _mainAgentSlot;
    private string _reasoningStrength;
    private ExecutionApprovalMode _approvalMode;
    private ExternalWorkerPermissionMode _externalWorkerPermission;
    private bool _workerEnabled;
    private WorkerSource _workerSource;
    private string _nativeWorkerSlot;
    private string _externalProviderId;
    private string _workerCount;
    private string _workerReasoningStrength;
    private RoutingMode _routingMode;
    private int? _autoCompactTokenLimit;
    private FallbackAction _fallbackAction;
    private string _perTaskBudget;
    private string _dailyBudget;
    private string _monthlyBudget;
    private string _tokenLimit;
    private string _requestLimit;
    private string _currency;
    private IReadOnlyList<string> _mainAgentSlots = NativeCodexRoleCatalog.All.Select(role => role.SlotName).ToArray();
    private IReadOnlyList<string> _nativeWorkerSlots = NativeCodexRoleCatalog.All.Select(role => role.SlotName).ToArray();
    private IReadOnlyList<WorkerModelCapability> _availableNativeModels = [];
    private IReadOnlyList<SelectionOption<string>> _reasoningStrengthOptions = CreateReasoningOptions(["low", "medium", "high", "xhigh"]);
    private IReadOnlyList<SelectionOption<string>> _workerReasoningStrengthOptions = CreateReasoningOptions(["low", "medium", "high", "xhigh"]);
    private bool _nativeModelCatalogLoaded;
    private string _nativeModelCatalogError = string.Empty;

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
        _reasoningStrength = string.IsNullOrWhiteSpace(profile.MainAgent.ReasoningEffort)
            ? "high"
            : profile.MainAgent.ReasoningEffort.Trim();
        _approvalMode = Enum.IsDefined(profile.ApprovalMode)
            ? profile.ApprovalMode
            : ExecutionApprovalMode.Automatic;
        _externalWorkerPermission = Enum.IsDefined(profile.ExternalWorkerPermission)
            ? profile.ExternalWorkerPermission
            : ExternalWorkerPermissionMode.WorkspaceFullAccess;
        _workerEnabled = profile.WorkerPolicy.Enabled;
        _workerSource = Enum.IsDefined(profile.WorkerPolicy.Source)
            ? profile.WorkerPolicy.Source
            : WorkerSource.Disabled;
        _nativeWorkerSlot = SlotFromWorkerId(profile.WorkerPolicy.PreferredProviderId);
        _externalProviderId = profile.WorkerPolicy.Source == WorkerSource.ExternalProvider
            ? profile.WorkerPolicy.PreferredProviderId ?? string.Empty
            : string.Empty;
        ExternalProviders = EnsureSelectedProvider(externalProviders, _externalProviderId);
        _workerCount = profile.WorkerPolicy.MaxWorkers.ToString(CultureInfo.InvariantCulture);
        _workerReasoningStrength = string.IsNullOrWhiteSpace(profile.WorkerPolicy.ReasoningEffort)
            ? "medium"
            : profile.WorkerPolicy.ReasoningEffort.Trim();
        _routingMode = Enum.IsDefined(profile.WorkerPolicy.RoutingMode)
            ? profile.WorkerPolicy.RoutingMode
            : RoutingMode.Single;
        _autoCompactTokenLimit = profile.AutoCompactTokenLimit is 150_000 or 180_000 or 200_000
            ? profile.AutoCompactTokenLimit
            : null;
        _fallbackAction = Enum.IsDefined(profile.WorkerPolicy.FallbackAction)
            ? profile.WorkerPolicy.FallbackAction
            : FallbackAction.SingleAgent;
        _perTaskBudget = Format(profile.Budget.PerTask);
        _dailyBudget = Format(profile.Budget.Daily);
        _monthlyBudget = Format(profile.Budget.Monthly);
        _tokenLimit = profile.Budget.TokenLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _requestLimit = profile.Budget.RequestLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _currency = string.IsNullOrWhiteSpace(profile.Budget.Currency) ? "CNY" : profile.Budget.Currency;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ProfileEditorViewModel ForNew(Profile template, IReadOnlyList<ProviderSelectionOption> externalProviders)
    {
        var defaults = Profile.CreateDefault(DateTimeOffset.UtcNow);
        return new(
            template with
            {
                MainAgent = defaults.MainAgent,
                WorkerPolicy = defaults.WorkerPolicy,
                AutoCompactTokenLimit = null,
            },
            isNew: true,
            initialName: string.Empty,
            externalProviders);
    }

    public static ProfileEditorViewModel ForCopy(Profile template, string uniqueName, IReadOnlyList<ProviderSelectionOption> externalProviders) =>
        new(template, isNew: true, initialName: uniqueName, externalProviders);

    public static ProfileEditorViewModel ForEdit(Profile profile, IReadOnlyList<ProviderSelectionOption> externalProviders) =>
        new(profile, isNew: false, initialName: profile.Name, externalProviders);

    public string DialogTitle => _isNew ? "新建方案" : "编辑方案";

    public IReadOnlyList<string> MainAgentSlots => _mainAgentSlots;

    public IReadOnlyList<SelectionOption<string>> ReasoningStrengthOptions => _reasoningStrengthOptions;

    public IReadOnlyList<SelectionOption<string>> WorkerReasoningStrengthOptions => _workerReasoningStrengthOptions;

    public SelectionOption<string> SelectedWorkerReasoningStrengthOption
    {
        get => WorkerReasoningStrengthOptions.FirstOrDefault(option => option.Value == WorkerReasoningStrength)
               ?? CreateReasoningOption(WorkerReasoningStrength, unavailable: true);
        set { if (value is not null) WorkerReasoningStrength = value.Value; }
    }

    public IReadOnlyList<SelectionOption<ExecutionApprovalMode>> ApprovalModeOptions { get; } =
    [
        new(ExecutionApprovalMode.Safe, "安全模式", "只读沙箱；非可信命令与任何越界写入都必须经过批准。"),
        new(ExecutionApprovalMode.Automatic, "自动模式", "允许在项目工作区内正常读写；风险操作由代理请求批准。"),
        new(ExecutionApprovalMode.FullAuto, "完全自动", "不请求批准并使用不受限访问。仅应在完全可信的项目和任务中使用。"),
    ];

    public IReadOnlyList<SelectionOption<ExternalWorkerPermissionMode>> ExternalWorkerPermissionOptions { get; } =
    [
        new(ExternalWorkerPermissionMode.ReadOnly, "只读", "可读取、搜索和查看 Git；禁止修改文件和变更型命令。"),
        new(ExternalWorkerPermissionMode.WorkspaceFullAccess, "工作区完全访问", "允许在当前项目范围内修改文件并运行受控开发命令。"),
        new(ExternalWorkerPermissionMode.FullAccess, "完全访问", "允许外部 Worker 访问项目外路径和执行完整 Shell；仅用于可信任务。"),
    ];

    public IReadOnlyList<SelectionOption<WorkerSource>> WorkerSourceOptions { get; } =
    [
        new(WorkerSource.NativeCodex, "原生工作代理", "使用 Codex 的 Astra、Sol、Terra 或 Luna"),
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

    public Visibility NativeModelCatalogErrorVisibility =>
        string.IsNullOrWhiteSpace(_nativeModelCatalogError) ? Visibility.Collapsed : Visibility.Visible;

    public string NativeModelCatalogErrorMessage => _nativeModelCatalogError;

    public bool NativeModelControlsEnabled => _nativeModelCatalogLoaded && string.IsNullOrWhiteSpace(_nativeModelCatalogError);

    public Visibility MainReasoningUnavailableVisibility =>
        IsReasoningAvailable(MainAgentSlot, ReasoningStrength) ? Visibility.Collapsed : Visibility.Visible;

    public string MainReasoningUnavailableMessage =>
        $"主代理 {MainAgentSlot} 当前不支持推理强度 {ReasoningStrength}。请改选该模型返回的可用强度。";

    public Visibility WorkerReasoningUnavailableVisibility =>
        !WorkerEnabled
        || WorkerSource != DomainWorkerSource.NativeCodex
        || IsReasoningAvailable(NativeWorkerSlot, WorkerReasoningStrength)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string WorkerReasoningUnavailableMessage =>
        $"原生 Worker {NativeWorkerSlot} 当前不支持推理强度 {WorkerReasoningStrength}。请改选该模型返回的可用强度。";

    public IReadOnlyList<SelectionOption<RoutingMode>> RoutingModeOptions { get; } =
    [
        new(RoutingMode.Economic, "经济优先", "最多使用 1 个工作代理，优先控制额度和重复劳动。"),
        new(RoutingMode.Balanced, "平衡模式", "最多使用 2 个工作代理，在速度、质量与成本之间取平衡。"),
        new(RoutingMode.Performance, "性能优先", "最多使用 3 个工作代理，优先并行速度与结果覆盖。"),
        new(RoutingMode.Manual, "手动模式", "仅按用户明确指定的边界和数量调用工作代理。"),
        new(RoutingMode.Single, "单代理模式", "不启用工作代理，所有工作由主代理独立完成。"),
    ];

    public IReadOnlyList<SelectionOption<int?>> AutoCompactOptions { get; } =
    [
        new(150_000, "节省 · 150K", "更早触发 Codex 原生自动压缩，降低长上下文成本；长任务中可能频繁压缩。"),
        new(180_000, "均衡 · 180K", "在上下文成本与连续性之间取折中。"),
        new(200_000, "连续 · 200K", "适合长时间开发与工具密集任务，减少同一任务中反复压缩。"),
        new(null, "默认 · 约218K", "不写入自定义阈值，使用 Codex 当前原生默认；当前环境约在 218K 附近触发。"),
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
        RoutingModeOptions.FirstOrDefault(option => option.Value == RoutingMode)?.Description
        ?? "该路由模式需要修复后才能使用。";

    public string AutoCompactDescription =>
        AutoCompactOptions.First(option => option.Value == AutoCompactTokenLimit).Description;

    public SelectionOption<WorkerSource> SelectedWorkerSourceOption
    {
        get => WorkerSourceOptions.FirstOrDefault(option => option.Value == WorkerSource)
               ?? WorkerSourceOptions[0];
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
        get => ReasoningStrengthOptions.FirstOrDefault(option => option.Value == ReasoningStrength)
               ?? CreateReasoningOption(ReasoningStrength, unavailable: true);
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
        get => RoutingModeOptions.FirstOrDefault(option => option.Value == RoutingMode)
               ?? RoutingModeOptions.First(option => option.Value == DomainRoutingMode.Single);
        set
        {
            if (value is not null)
            {
                RoutingMode = value.Value;
            }
        }
    }

    public SelectionOption<int?> SelectedAutoCompactOption
    {
        get => AutoCompactOptions.First(option => option.Value == AutoCompactTokenLimit);
        set
        {
            if (value is not null)
            {
                AutoCompactTokenLimit = value.Value;
            }
        }
    }

    public SelectionOption<ExecutionApprovalMode> SelectedApprovalModeOption
    {
        get => ApprovalModeOptions.FirstOrDefault(option => option.Value == ApprovalMode)
               ?? ApprovalModeOptions.First(option => option.Value == ExecutionApprovalMode.Automatic);
        set
        {
            if (value is not null)
            {
                ApprovalMode = value.Value;
            }
        }
    }

    public SelectionOption<ExternalWorkerPermissionMode> SelectedExternalWorkerPermissionOption
    {
        get => ExternalWorkerPermissionOptions.FirstOrDefault(option => option.Value == ExternalWorkerPermission)
               ?? ExternalWorkerPermissionOptions.First(option => option.Value == ExternalWorkerPermissionMode.WorkspaceFullAccess);
        set
        {
            if (value is not null)
            {
                ExternalWorkerPermission = value.Value;
            }
        }
    }

    public SelectionOption<FallbackAction> SelectedFallbackActionOption
    {
        get => FallbackActionOptions.FirstOrDefault(option => option.Value == FallbackAction)
               ?? FallbackActionOptions.First(option => option.Value == DomainFallbackAction.SingleAgent);
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
                RefreshMainReasoningOptions(selectDefaultWhenUnsupported: true);
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
        ApprovalModeOptions.FirstOrDefault(option => option.Value == ApprovalMode)?.Description
        ?? "该批准模式需要修复后才能使用。";

    public Visibility FullAutoWarningVisibility =>
        ApprovalMode == ExecutionApprovalMode.FullAuto ? Visibility.Visible : Visibility.Collapsed;
    public ExternalWorkerPermissionMode ExternalWorkerPermission
    {
        get => _externalWorkerPermission;
        set
        {
            if (Set(ref _externalWorkerPermission, value))
            {
                OnPropertyChanged(nameof(SelectedExternalWorkerPermissionOption));
                OnPropertyChanged(nameof(ExternalWorkerPermissionDescription));
            }
        }
    }

    public string ExternalWorkerPermissionDescription =>
        ExternalWorkerPermissionOptions.FirstOrDefault(option => option.Value == ExternalWorkerPermission)?.Description
        ?? "该外部 Worker 权限需要修复后才能使用。";
    public string ReasoningStrength
    {
        get => _reasoningStrength;
        set
        {
            if (Set(ref _reasoningStrength, value))
            {
                OnPropertyChanged(nameof(SelectedReasoningStrengthOption));
                OnPropertyChanged(nameof(MainReasoningUnavailableVisibility));
                OnPropertyChanged(nameof(MainReasoningUnavailableMessage));
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
            OnPropertyChanged(nameof(WorkerReasoningUnavailableVisibility));
            OnPropertyChanged(nameof(WorkerReasoningUnavailableMessage));
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
            OnPropertyChanged(nameof(NativeWorkerUnavailableVisibility));
            OnPropertyChanged(nameof(WorkerReasoningUnavailableVisibility));
            OnPropertyChanged(nameof(WorkerReasoningUnavailableMessage));
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
                RefreshWorkerReasoningOptions(selectDefaultWhenUnsupported: true);
                OnPropertyChanged(nameof(NativeWorkerUnavailableVisibility));
                OnPropertyChanged(nameof(NativeWorkerUnavailableMessage));
            }
        }
    }
    public string ExternalProviderId { get => _externalProviderId; set => Set(ref _externalProviderId, value); }
    public string WorkerCount { get => _workerCount; set => Set(ref _workerCount, value); }
    public string WorkerReasoningStrength
    {
        get => _workerReasoningStrength;
        set
        {
            if (Set(ref _workerReasoningStrength, value))
            {
                OnPropertyChanged(nameof(SelectedWorkerReasoningStrengthOption));
                OnPropertyChanged(nameof(WorkerReasoningUnavailableVisibility));
                OnPropertyChanged(nameof(WorkerReasoningUnavailableMessage));
            }
        }
    }
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
    public int? AutoCompactTokenLimit
    {
        get => _autoCompactTokenLimit;
        set
        {
            if (Set(ref _autoCompactTokenLimit, value))
            {
                OnPropertyChanged(nameof(AutoCompactDescription));
                OnPropertyChanged(nameof(SelectedAutoCompactOption));
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
        if (!NativeModelControlsEnabled)
        {
            throw new FormatException("Codex 模型目录尚未成功读取，无法保存方案。请重新打开编辑器后重试。");
        }

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

        if (!IsReasoningAvailable(MainAgentSlot, ReasoningStrength))
        {
            throw new FormatException(MainReasoningUnavailableMessage);
        }

        if (WorkerEnabled
            && WorkerSource == DomainWorkerSource.NativeCodex
            && !IsReasoningAvailable(NativeWorkerSlot, WorkerReasoningStrength))
        {
            throw new FormatException(WorkerReasoningUnavailableMessage);
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
                FallbackAction,
                WorkerReasoningStrength.Trim()),
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
            ExternalWorkerPermission = this.ExternalWorkerPermission,
            AutoCompactTokenLimit = this.AutoCompactTokenLimit,
            SchemaVersion = Profile.CurrentSchemaVersion,
            RepairMessage = null,
        };
    }

    private static string SlotFromModel(string? modelId) =>
        NativeCodexRoleCatalog.FindByModel(modelId)?.SlotName
        ?? modelId?.Trim()
        ?? "Astra";

    private static string SlotFromWorkerId(string? workerId) =>
        NativeCodexRoleCatalog.FindByWorker(workerId)?.SlotName
        ?? NativeCodexRoleCatalog.FindByModel(workerId)?.SlotName
        ?? workerId?.Trim()
        ?? "Luna";

    private static string ModelForSlot(string slot) =>
        NativeCodexRoleCatalog.FindBySlot(slot)?.ModelId ?? slot.Trim();

    private static string NativeWorkerIdForSlot(string slot) =>
        NativeCodexRoleCatalog.FindBySlot(slot)?.WorkerId ?? slot.Trim();

    public void SetAvailableNativeModels(IEnumerable<WorkerModelCapability> models)
    {
        _availableNativeModels = models
            .Where(model => NativeCodexRoleCatalog.FindByModel(model.Id) is not null)
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var availableIds = _availableNativeModels.Select(model => model.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = NativeCodexRoleCatalog.All
            .Where(role => availableIds.Contains(role.ModelId))
            .Select(role => role.SlotName)
            .ToArray();
        _mainAgentSlots = allowed;
        _nativeWorkerSlots = allowed;
        _nativeModelCatalogLoaded = true;
        _nativeModelCatalogError = string.Empty;
        RefreshMainReasoningOptions(selectDefaultWhenUnsupported: false);
        RefreshWorkerReasoningOptions(selectDefaultWhenUnsupported: false);
        OnPropertyChanged(nameof(MainAgentSlots));
        OnPropertyChanged(nameof(NativeWorkerSlots));
        OnPropertyChanged(nameof(NativeModelControlsEnabled));
        OnPropertyChanged(nameof(NativeModelCatalogErrorVisibility));
        OnPropertyChanged(nameof(NativeModelCatalogErrorMessage));
        OnPropertyChanged(nameof(MainAgentUnavailableVisibility));
        OnPropertyChanged(nameof(MainAgentUnavailableMessage));
        OnPropertyChanged(nameof(NativeWorkerUnavailableVisibility));
        OnPropertyChanged(nameof(NativeWorkerUnavailableMessage));
    }

    public void SetNativeModelCatalogFailure(string message)
    {
        _nativeModelCatalogLoaded = false;
        _nativeModelCatalogError = string.IsNullOrWhiteSpace(message) ? "未知错误" : message.Trim();
        OnPropertyChanged(nameof(NativeModelControlsEnabled));
        OnPropertyChanged(nameof(NativeModelCatalogErrorVisibility));
        OnPropertyChanged(nameof(NativeModelCatalogErrorMessage));
    }

    private void RefreshMainReasoningOptions(bool selectDefaultWhenUnsupported)
    {
        var model = FindCapability(MainAgentSlot);
        if (selectDefaultWhenUnsupported && model is not null && !ContainsEffort(model, ReasoningStrength))
        {
            _reasoningStrength = model.DefaultReasoningEffort;
        }

        _reasoningStrengthOptions = OptionsFor(model, ReasoningStrength);
        OnPropertyChanged(nameof(ReasoningStrengthOptions));
        OnPropertyChanged(nameof(SelectedReasoningStrengthOption));
        OnPropertyChanged(nameof(MainReasoningUnavailableVisibility));
        OnPropertyChanged(nameof(MainReasoningUnavailableMessage));
    }

    private void RefreshWorkerReasoningOptions(bool selectDefaultWhenUnsupported)
    {
        var model = FindCapability(NativeWorkerSlot);
        if (selectDefaultWhenUnsupported && model is not null && !ContainsEffort(model, WorkerReasoningStrength))
        {
            _workerReasoningStrength = model.DefaultReasoningEffort;
        }

        _workerReasoningStrengthOptions = OptionsFor(model, WorkerReasoningStrength);
        OnPropertyChanged(nameof(WorkerReasoningStrengthOptions));
        OnPropertyChanged(nameof(SelectedWorkerReasoningStrengthOption));
        OnPropertyChanged(nameof(WorkerReasoningUnavailableVisibility));
        OnPropertyChanged(nameof(WorkerReasoningUnavailableMessage));
    }

    private WorkerModelCapability? FindCapability(string slot)
    {
        var modelId = NativeCodexRoleCatalog.FindBySlot(slot)?.ModelId;
        return _availableNativeModels.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsReasoningAvailable(string slot, string effort)
    {
        var model = FindCapability(slot);
        return model is not null && ContainsEffort(model, effort);
    }

    private static bool ContainsEffort(WorkerModelCapability model, string effort) =>
        model.SupportedReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SelectionOption<string>> OptionsFor(WorkerModelCapability? model, string selected)
    {
        var options = CreateReasoningOptions(model?.SupportedReasoningEfforts ?? []);
        return string.IsNullOrWhiteSpace(selected)
               || options.Any(option => string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase))
            ? options
            : options.Append(CreateReasoningOption(selected, unavailable: true)).ToArray();
    }

    private static IReadOnlyList<SelectionOption<string>> CreateReasoningOptions(IEnumerable<string> efforts) =>
        efforts
            .Where(effort => !string.IsNullOrWhiteSpace(effort))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(effort => CreateReasoningOption(effort, unavailable: false))
            .ToArray();

    private static SelectionOption<string> CreateReasoningOption(string effort, bool unavailable)
    {
        var normalized = effort.Trim();
        var label = normalized.ToLowerInvariant() switch
        {
            "none" => "无 · none",
            "low" => "低 · low",
            "medium" => "中 · medium",
            "high" => "高 · high",
            "xhigh" => "极高 · xhigh",
            "max" => "最高 · max",
            "ultra" => "极限 · ultra",
            _ => normalized,
        };
        return new(normalized, unavailable ? $"{label}（不可用）" : label);
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
