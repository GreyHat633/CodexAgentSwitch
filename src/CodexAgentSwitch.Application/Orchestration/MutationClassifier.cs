namespace CodexAgentSwitch.Application.Orchestration;

public enum MutationKind
{
    ReadOnly,
    Mutation,
    Unknown,
}

public sealed record MutationClassification(MutationKind Kind, string Input, string? Evidence = null)
{
    public bool IsReadOnly => Kind == MutationKind.ReadOnly;
    public bool IsMutation => Kind == MutationKind.Mutation;
    public bool IsUnknown => Kind == MutationKind.Unknown;
}

/// <summary>
/// Classifies only commands whose safety is unambiguous.  Unknown commands are
/// intentionally not inferred from their names or surrounding text.
/// </summary>
public static class MutationClassifier
{
    private static readonly string[] DefiniteMutations =
    [
        "apply_patch", "edit", "write", "set-content", "add-content", "out-file",
        "remove-item", "remove", "move-item", "move", "rename-item", "rename",
    ];

    private static readonly string[] DefiniteReads =
    [
        "get-content", "rg", "select-string", "get-childitem", "dotnet test",
        "git diff", "git status",
    ];

    public static MutationClassification Classify(string? command)
    {
        var input = command?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return new(MutationKind.Unknown, input, "empty command");
        }

        var lowered = input.ToLowerInvariant();
        if (ContainsMutationCommand(lowered))
        {
            return new(MutationKind.Mutation, input, "definite mutation command");
        }

        // Redirection is mutation even when the command preceding it is not
        // known; avoid treating comparison operators as shell redirection.
        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"(?<!\w)>>?\s*[^\s;&|]+"))
        {
            return new(MutationKind.Mutation, input, "shell redirection");
        }

        // A compound command is not read-only merely because its first
        // segment is a known inspection command.  Without a shell parser we
        // cannot prove the remaining segments are safe.
        if (input.Contains(';') || input.Contains("&&", StringComparison.Ordinal)
            || input.Contains("||", StringComparison.Ordinal) || input.Contains('|'))
        {
            return new(MutationKind.Unknown, input, "compound command is not safely classifiable");
        }

        if (ContainsReadOnlyCommand(lowered))
        {
            return new(MutationKind.ReadOnly, input, "definite read-only command");
        }

        return new(MutationKind.Unknown, input, "command is not safely classifiable");
    }

    private static bool ContainsMutationCommand(string input) =>
        DefiniteMutations.Any(token =>
            System.Text.RegularExpressions.Regex.IsMatch(input, $@"(?<![\w-]){System.Text.RegularExpressions.Regex.Escape(token)}(?![\w-])"));

    private static bool ContainsReadOnlyCommand(string input) =>
        DefiniteReads.Any(token =>
            token.Contains(' ')
                ? input.StartsWith(token, StringComparison.Ordinal)
                : System.Text.RegularExpressions.Regex.IsMatch(input, $@"(?<![\w-]){System.Text.RegularExpressions.Regex.Escape(token)}(?![\w-])"));
}
