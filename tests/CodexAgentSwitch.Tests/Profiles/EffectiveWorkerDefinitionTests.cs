using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class EffectiveWorkerDefinitionTests
{
    [Theory]
    [InlineData("native-astra", "cas_astra_worker", "gpt-6-astra", "agents/cas-astra-worker.toml")]
    [InlineData("native-sol", "cas_sol_worker", "gpt-5.6-sol", "agents/cas-sol-worker.toml")]
    [InlineData("native-terra", "cas_terra_worker", "gpt-5.6-terra", "agents/cas-terra-worker.toml")]
    [InlineData("native-luna", "cas_luna_worker", "gpt-5.6-luna", "agents/cas-luna-worker.toml")]
    public void Native_worker_resolves_to_its_own_custom_role_not_a_default_subagent_model(
        string workerId,
        string role,
        string model,
        string configFile)
    {
        var definition = EffectiveWorkerDefinition.Resolve(new WorkerPolicy(
            true, WorkerSource.NativeCodex, workerId, null, 3, RoutingMode.Economic, FallbackAction.StopDelegation));

        Assert.Equal(EffectiveWorkerKind.NativeAgent, definition.Kind);
        Assert.Equal(WorkerExecutionCapability.Supported, definition.Capability);
        Assert.Equal(role, definition.AgentRole);
        Assert.Equal(model, definition.ModelId);
        Assert.Equal("medium", definition.ReasoningEffort);
        Assert.Equal(configFile, definition.ConfigFile);
        Assert.True(definition.CanRunInNativeCodex);
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    [InlineData("ultra")]
    public void Native_worker_preserves_configured_reasoning_effort(string effort)
    {
        var definition = EffectiveWorkerDefinition.Resolve(new WorkerPolicy(
            true, WorkerSource.NativeCodex, "native-luna", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation, effort));

        Assert.Equal(effort, definition.ReasoningEffort);
    }

    [Fact]
    public void External_worker_is_explicitly_unsupported_only_for_native_codex_execution()
    {
        var definition = EffectiveWorkerDefinition.Resolve(new WorkerPolicy(
            true, WorkerSource.ExternalProvider, "deepseek-default", null, 1, RoutingMode.Economic, FallbackAction.StopDelegation));

        Assert.Equal(EffectiveWorkerKind.ExternalAgent, definition.Kind);
        Assert.Equal("cas_external_worker", definition.AgentRole);
        Assert.Equal("deepseek-default", definition.ProviderId);
        Assert.Null(definition.ReasoningEffort);
        Assert.Equal(WorkerExecutionCapability.Unsupported, definition.Capability);
        Assert.False(definition.CanRunInNativeCodex);
    }
}
