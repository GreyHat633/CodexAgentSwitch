using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexAgentSwitch.Domain.Profiles;
using DomainRoutingMode = CodexAgentSwitch.Domain.Profiles.RoutingMode;
using DomainWorkerSource = CodexAgentSwitch.Domain.Profiles.WorkerSource;

namespace CodexAgentSwitch.App.ViewModels;

public sealed class ProfileListItemViewModel(Profile profile)
{
    internal Profile Value { get; } = profile;

    public Guid Id => Value.Id;

    public string Name => Value.Name;

    public string KindLabel => Value.KindLabel;

    public string DefaultLabel => Value.DefaultLabel;

    public bool IsDefault => Value.IsDefault;
}

public sealed class ProfileEditorViewModel : INotifyPropertyChanged
{
    private readonly Profile? _source;
    private readonly bool _isNew;
    private string _name;
    private string _mainAgentSlot;
    private string _reasoningStrength;
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

    private ProfileEditorViewModel(Profile? source, bool isNew, string initialName)
    {
        _source = source;
        _isNew = isNew;
        var profile = source ?? Profile.CreateDefault(DateTimeOffset.UtcNow);
        _name = isNew ? initialName : profile.Name;
        _mainAgentSlot = SlotFromModel(profile.MainAgent.ModelId);
        _reasoningStrength = profile.MainAgent.ReasoningEffort is "low" or "medium" or "high" or "xhigh"
            ? profile.MainAgent.ReasoningEffort
            : "high";
        _workerEnabled = profile.WorkerPolicy.Enabled;
        _workerSource = profile.WorkerPolicy.Source == WorkerSource.ExternalProvider
            ? WorkerSource.ExternalProvider
            : WorkerSource.NativeCodex;
        _nativeWorkerSlot = SlotFromWorkerId(profile.WorkerPolicy.PreferredProviderId);
        _externalProviderId = profile.WorkerPolicy.Source == WorkerSource.ExternalProvider
            ? profile.WorkerPolicy.PreferredProviderId ?? string.Empty
            : string.Empty;
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

    public static ProfileEditorViewModel ForNew(Profile template) => new(template, isNew: true, initialName: string.Empty);

    public static ProfileEditorViewModel ForCopy(Profile template, string uniqueName) => new(template, isNew: true, initialName: uniqueName);

    public static ProfileEditorViewModel ForEdit(Profile profile) => new(profile, isNew: false, initialName: profile.Name);

    public string DialogTitle => _isNew ? "新建方案" : "编辑方案";

    public IReadOnlyList<string> MainAgentSlots { get; } = ["Sol", "Terra", "Luna"];

    public IReadOnlyList<string> ReasoningStrengths { get; } = ["low", "medium", "high", "xhigh"];

    public IReadOnlyList<WorkerSource> WorkerSources { get; } = [WorkerSource.NativeCodex, WorkerSource.ExternalProvider];

    public IReadOnlyList<string> NativeWorkerSlots { get; } = ["Sol", "Terra", "Luna"];

    public IReadOnlyList<RoutingMode> RoutingModes { get; } = Enum.GetValues<RoutingMode>();

    public IReadOnlyList<FallbackAction> FallbackActions { get; } = Enum.GetValues<FallbackAction>();

    public string Name { get => _name; set => Set(ref _name, value); }
    public string MainAgentSlot { get => _mainAgentSlot; set => Set(ref _mainAgentSlot, value); }
    public string ReasoningStrength { get => _reasoningStrength; set => Set(ref _reasoningStrength, value); }

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
        }
    }

    public WorkerSource WorkerSource { get => _workerSource; set => Set(ref _workerSource, value); }
    public string NativeWorkerSlot { get => _nativeWorkerSlot; set => Set(ref _nativeWorkerSlot, value); }
    public string ExternalProviderId { get => _externalProviderId; set => Set(ref _externalProviderId, value); }
    public string WorkerCount { get => _workerCount; set => Set(ref _workerCount, value); }
    public RoutingMode RoutingMode
    {
        get => _routingMode;
        set
        {
            if (Set(ref _routingMode, value) && value == DomainRoutingMode.Single)
            {
                WorkerEnabled = false;
            }
        }
    }
    public FallbackAction FallbackAction { get => _fallbackAction; set => Set(ref _fallbackAction, value); }
    public string PerTaskBudget { get => _perTaskBudget; set => Set(ref _perTaskBudget, value); }
    public string DailyBudget { get => _dailyBudget; set => Set(ref _dailyBudget, value); }
    public string MonthlyBudget { get => _monthlyBudget; set => Set(ref _monthlyBudget, value); }
    public string TokenLimit { get => _tokenLimit; set => Set(ref _tokenLimit, value); }
    public string RequestLimit { get => _requestLimit; set => Set(ref _requestLimit, value); }
    public string Currency { get => _currency; set => Set(ref _currency, value); }

    public Profile BuildProfile(DateTimeOffset now)
    {
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
                ParseInt(WorkerCount, "Worker 数量"),
                RoutingMode,
                FallbackAction),
            new BudgetLimits(
                ParseDecimal(PerTaskBudget, "单任务预算"),
                ParseDecimal(DailyBudget, "每日预算"),
                ParseDecimal(MonthlyBudget, "每月预算"),
                ParseLong(TokenLimit, "Token 上限"),
                ParseIntOrNull(RequestLimit, "请求上限"),
                Currency.Trim()),
            _isNew ? false : _source!.IsDefault,
            _isNew ? now : _source!.CreatedAt,
            now,
            _isNew ? null : _source!.LastUsedAt)
        {
            IsBuiltIn = _isNew ? false : _source!.IsBuiltIn,
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

    private static string Format(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

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
}
