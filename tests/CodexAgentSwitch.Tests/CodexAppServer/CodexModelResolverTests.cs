using CodexAgentSwitch.Infrastructure.CodexAppServer;

namespace CodexAgentSwitch.Tests.CodexAppServer;

public sealed class CodexModelResolverTests
{
    private static readonly IReadOnlyList<CodexModelOption> Catalog =
    [
        new("gpt-6-astra", "GPT-6 Astra", true, ["low", "medium", "high", "xhigh", "max", "ultra"], "low"),
        new("gpt-5.6-terra", "GPT-5.6 Terra", false, ["low", "medium", "high", "xhigh", "max", "ultra"], "medium"),
        new("gpt-5.6-luna", "GPT-5.6 Luna", false, ["low", "medium", "high", "xhigh", "max"], "medium"),
    ];

    [Fact]
    public void Available_role_model_is_kept_exactly()
    {
        var result = CodexModelResolver.Resolve("gpt-5.6-luna", Catalog);

        Assert.Equal("gpt-5.6-luna", result.ModelId);
        Assert.Null(result.CompatibilityNotice);
    }

    [Fact]
    public void Unavailable_sol_role_is_rejected_with_discovered_catalog()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexModelResolver.Resolve("gpt-5.6-sol", Catalog));

        Assert.Contains("gpt-5.6-terra", exception.Message);
    }

    [Fact]
    public void Unknown_model_is_rejected_with_discovered_catalog()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexModelResolver.Resolve("not-a-model", Catalog));

        Assert.Contains("gpt-5.6-terra", exception.Message);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("ultra")]
    public void Astra_live_reasoning_effort_is_accepted(string effort)
    {
        var result = CodexModelResolver.Resolve("gpt-6-astra", effort, Catalog);

        Assert.Equal("gpt-6-astra", result.ModelId);
    }

    [Fact]
    public void Unsupported_reasoning_effort_is_rejected_without_remap()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexModelResolver.Resolve("gpt-5.6-luna", "ultra", Catalog));

        Assert.Contains("ultra", exception.Message);
        Assert.Contains("max", exception.Message);
    }
}
