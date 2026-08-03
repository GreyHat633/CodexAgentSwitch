using CodexAgentSwitch.Domain.Common;

namespace CodexAgentSwitch.Application.Profiles;

public sealed class ProfileValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception(string.Join(Environment.NewLine, issues.Select(issue => issue.Message)))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}
