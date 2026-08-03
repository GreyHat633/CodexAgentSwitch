using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Providers;

public sealed class DeepSeekV4CatalogTests
{
    [Fact]
    public void Flash_preset_has_canonical_url_and_v4_default()
    {
        var preset = ProviderConfiguration.DeepSeekPreset(DateTimeOffset.UtcNow);

        Assert.Equal(DeepSeekV4Catalog.BaseUrl, preset.BaseUri?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(DeepSeekV4Catalog.FlashModelId, preset.ModelId);
        Assert.True(DeepSeekV4Catalog.Get(DeepSeekV4Catalog.FlashModelId).Supports(ProviderProtocol.ChatCompletions));
        Assert.True(DeepSeekV4Catalog.Get(DeepSeekV4Catalog.FlashModelId).Supports(ProviderProtocol.Responses));
        Assert.True(DeepSeekV4Catalog.Get(DeepSeekV4Catalog.FlashModelId).Supports(ProviderProtocol.CodexWorker));
    }

    [Fact]
    public void Pro_is_chat_only_and_rejected_for_current_worker_protocol()
    {
        var pro = DeepSeekV4Catalog.Get(DeepSeekV4Catalog.ProModelId);

        Assert.True(pro.Supports(ProviderProtocol.ChatCompletions));
        Assert.False(pro.Supports(ProviderProtocol.Responses));
        Assert.False(pro.Supports(ProviderProtocol.CodexWorker));
        Assert.Equal(DeepSeekV4Catalog.UnsupportedWorkerReason, pro.WorkerUnavailableReason);
    }

    [Fact]
    public void Legacy_migration_is_idempotent_and_marks_reasoner_thinking_intent()
    {
        var reasoner = DeepSeekV4Migration.MigrateModel("deepseek-reasoner");
        var migrated = DeepSeekV4Migration.MigrateModel(reasoner.ModelId);

        Assert.Equal(DeepSeekV4Catalog.FlashModelId, reasoner.ModelId);
        Assert.True(reasoner.Changed);
        Assert.True(reasoner.PreserveThinkingIntent);
        Assert.False(migrated.Changed);
        Assert.DoesNotContain("deepseek-chat", DeepSeekV4Catalog.FallbackModelIds);
        Assert.DoesNotContain("deepseek-reasoner", DeepSeekV4Catalog.FallbackModelIds);
    }

    [Fact]
    public void Reasoner_profile_migration_preserves_thinking_effort()
    {
        var original = Profile.CreateDefault(DateTimeOffset.UtcNow) with
        {
            MainAgent = new AgentSelection("deepseek-reasoner", "xhigh"),
        };

        var migrated = DeepSeekV4Migration.Migrate(original);

        Assert.Equal(DeepSeekV4Catalog.FlashModelId, migrated.MainAgent.ModelId);
        Assert.Equal("xhigh", migrated.MainAgent.ReasoningEffort);
    }
}
