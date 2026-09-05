using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class NativeCodexRoleCatalogTests
{
    [Fact]
    public void New_profile_defaults_to_Astra_main_and_Luna_worker()
    {
        var profile = Profile.CreateDefault(DateTimeOffset.UtcNow);

        Assert.Equal("gpt-6-astra", profile.MainAgent.ModelId);
        Assert.Equal("high", profile.MainAgent.ReasoningEffort);
        Assert.Equal("native-luna", profile.WorkerPolicy.PreferredProviderId);
    }

    [Fact]
    public void Product_roles_have_stable_order_and_complete_native_mappings()
    {
        Assert.Equal(["Astra", "Sol", "Terra", "Luna"], NativeCodexRoleCatalog.All.Select(role => role.SlotName));
        var astra = NativeCodexRoleCatalog.FindByModel("gpt-6-astra");

        Assert.NotNull(astra);
        Assert.Equal("native-astra", astra.WorkerId);
        Assert.Equal("cas_astra_worker", astra.AgentRole);
        Assert.Equal("agents/cas-astra-worker.toml", astra.ConfigFile);
    }

    [Fact]
    public void Unknown_values_are_not_mapped_to_another_role()
    {
        Assert.Null(NativeCodexRoleCatalog.FindByModel("gpt-5.5"));
        Assert.Null(NativeCodexRoleCatalog.FindByWorker("native-future"));
    }
}
