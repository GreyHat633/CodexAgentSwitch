namespace CodexAgentSwitch.Application.Onboarding;

public enum OnboardingStep
{
    Environment = 1,
    MainAgent = 2,
    Worker = 3,
    Provider = 4,
    Confirm = 5,
}

public sealed record OnboardingStepDefinition(
    OnboardingStep Step,
    string Title,
    string Description,
    string RequiredControlName);

public sealed class OnboardingWorkflowState
{
    private static readonly IReadOnlyDictionary<OnboardingStep, OnboardingStepDefinition> Definitions =
        new Dictionary<OnboardingStep, OnboardingStepDefinition>
        {
            [OnboardingStep.Environment] = new(OnboardingStep.Environment, "环境检查", "检测 Codex CLI、图形桌面应用、App Server、配置目录和运行环境。", "EnvironmentChecksPanel"),
            [OnboardingStep.MainAgent] = new(OnboardingStep.MainAgent, "选择主代理", "选择当前账户可用的主代理与实际支持的推理强度。", "MainAgentCards"),
            [OnboardingStep.Worker] = new(OnboardingStep.Worker, "配置工作代理", "选择是否启用 Worker、来源、数量、路由和回退动作。", "WorkerSourceSelector"),
            [OnboardingStep.Provider] = new(OnboardingStep.Provider, "配置服务商", "配置外部 Provider、模型、API Key 并执行连接测试。", "ProviderConfigurationPanel"),
            [OnboardingStep.Confirm] = new(OnboardingStep.Confirm, "确认并启用", "确认完整方案，然后保存为真实 Profile 并设为默认。", "ProfileSummaryPanel"),
        };

    public OnboardingStep Current { get; private set; } = OnboardingStep.Environment;

    public OnboardingStepDefinition Definition => Definitions[Current];

    public bool CanGoBack => Current > OnboardingStep.Environment;

    public bool CanGoNext => Current < OnboardingStep.Confirm;

    public void Back()
    {
        if (CanGoBack)
        {
            Current--;
        }
    }

    public void Next()
    {
        if (CanGoNext)
        {
            Current++;
        }
    }
}
