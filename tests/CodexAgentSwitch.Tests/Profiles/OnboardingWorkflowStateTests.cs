using CodexAgentSwitch.Application.Onboarding;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class OnboardingWorkflowStateTests
{
    [Fact]
    public void Five_steps_have_distinct_titles_and_required_controls()
    {
        var workflow = new OnboardingWorkflowState();
        var definitions = new List<OnboardingStepDefinition> { workflow.Definition };

        while (workflow.CanGoNext)
        {
            workflow.Next();
            definitions.Add(workflow.Definition);
        }

        Assert.Equal(5, definitions.Count);
        Assert.Equal(5, definitions.Select(item => item.Title).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, definitions.Select(item => item.RequiredControlName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { "EnvironmentChecksPanel", "MainAgentCards", "WorkerSourceSelector", "ProviderConfigurationPanel", "ProfileSummaryPanel" },
            definitions.Select(item => item.RequiredControlName));
    }

    [Fact]
    public void Back_and_next_change_only_the_active_step()
    {
        var workflow = new OnboardingWorkflowState();

        workflow.Next();
        workflow.Next();
        Assert.Equal(OnboardingStep.Worker, workflow.Current);

        workflow.Back();
        Assert.Equal(OnboardingStep.MainAgent, workflow.Current);
        workflow.Next();
        Assert.Equal(OnboardingStep.Worker, workflow.Current);
    }
}
