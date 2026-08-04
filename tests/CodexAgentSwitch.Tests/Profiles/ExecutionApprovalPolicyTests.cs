using CodexAgentSwitch.Domain.Profiles;

namespace CodexAgentSwitch.Tests.Profiles;

public sealed class ExecutionApprovalPolicyTests
{
    [Theory]
    [InlineData(ExecutionApprovalMode.Safe, "untrusted", "read-only")]
    [InlineData(ExecutionApprovalMode.Automatic, "on-request", "workspace-write")]
    [InlineData(ExecutionApprovalMode.FullAuto, "never", "danger-full-access")]
    public void Mode_maps_to_the_same_codex_policy_for_native_and_managed_execution(
        ExecutionApprovalMode mode,
        string approval,
        string sandbox)
    {
        var settings = ExecutionApprovalPolicy.Resolve(mode);

        Assert.Equal(approval, settings.ApprovalPolicy);
        Assert.Equal(sandbox, settings.SandboxMode);
    }

    [Fact]
    public void Default_profile_uses_automatic_mode()
    {
        var profile = Profile.CreateDefault(DateTimeOffset.UtcNow);

        Assert.Equal(ExecutionApprovalMode.Automatic, profile.ApprovalMode);
    }
}
