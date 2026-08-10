using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAgentSwitch.Application.ExternalAgents;

namespace CodexAgentSwitch.Infrastructure.ExternalAgents;

public sealed class LocalExternalToolHost : IExternalToolHost
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 300;
    private const int MaximumOutputCharacters = 32_000;

    public Task<ExternalToolExecutionResult> ExecuteAsync(
        ExternalToolSession session,
        ExternalToolExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        request.ToolName switch
        {
            "shell" => ExecuteShellAsync(session, request, cancellationToken),
            "apply_patch" => ExecuteApplyPatchAsync(session, request, cancellationToken),
            _ => Task.FromResult(Denied(request, $"Unsupported external tool: {request.ToolName}.")),
        };

    private static Task<ExternalToolExecutionResult> ExecuteApplyPatchAsync(
        ExternalToolSession session,
        ExternalToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (session.PermissionMode == ExternalToolPermissionMode.ReadOnly)
        {
            return Task.FromResult(Denied(request, "Read Only sessions cannot apply patches."));
        }

        ApplyPatchArguments arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<ApplyPatchArguments>(request.Arguments, JsonOptions)
                ?? throw new JsonException("Apply patch arguments are empty.");
        }
        catch (JsonException exception)
        {
            return Task.FromResult(Failed(request, $"Invalid apply_patch arguments: {exception.Message}"));
        }

        if (string.IsNullOrWhiteSpace(arguments.Patch))
        {
            return Task.FromResult(Failed(request, "Patch is required."));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(session.ProjectPath);
            if (!Directory.Exists(root))
            {
                return Task.FromResult(Denied(request, $"Project path does not exist: {root}"));
            }

            if (session.AllowedWriteScope is null || session.AllowedWriteScope.Count == 0)
            {
                return Task.FromResult(Denied(request, "apply_patch requires a non-empty AllowedWriteScope."));
            }

            var files = ParsePatch(arguments.Patch, root);
            foreach (var file in files)
            {
                if (!IsWithin(root, file.Path) || !IsAllowedWritePath(session, root, file.Path))
                {
                    return Task.FromResult(Denied(request, $"Patch path is outside the allowed write scope: {file.RelativePath}"));
                }
            }

            var originals = files.ToDictionary(file => file.Path, ReadOriginal, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (file.IsCreation && originals[file.Path].Exists)
                {
                    return Task.FromResult(Failed(request, $"Cannot create {file.RelativePath}: the target already exists."));
                }

                if (!file.IsCreation && !originals[file.Path].Exists)
                {
                    return Task.FromResult(Failed(request, $"Cannot patch {file.RelativePath}: the source target does not exist."));
                }
            }
            var updated = files.ToDictionary(file => file.Path, file => ApplyFilePatch(file, originals[file.Path]), StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            CommitFiles(files, originals, updated);
            var changed = files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            return Task.FromResult(new ExternalToolExecutionResult(
                request.ToolCallId,
                request.ToolName,
                $"Applied patch to {changed.Length} file(s).",
                string.Empty,
                0,
                TimedOut: false,
                Denied: false,
                Truncated: false,
                ChangedFiles: changed));
        }
        catch (PatchDeniedException exception)
        {
            return Task.FromResult(Denied(request, exception.Message));
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(Denied(request, $"Invalid patch path: {exception.Message}"));
        }
        catch (PatchFormatException exception)
        {
            return Task.FromResult(Failed(request, exception.Message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Failed(request, $"Failed to apply patch: {exception.Message}"));
        }
    }

    private static async Task<ExternalToolExecutionResult> ExecuteShellAsync(
        ExternalToolSession session,
        ExternalToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ShellArguments arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<ShellArguments>(request.Arguments, JsonOptions)
                ?? throw new JsonException("Shell arguments are empty.");
        }
        catch (JsonException exception)
        {
            return Denied(request, $"Invalid shell arguments: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(arguments.Command))
        {
            return Denied(request, "Shell command is required.");
        }

        string workingDirectory;
        try
        {
            workingDirectory = ResolveWorkingDirectory(session, arguments.Cwd);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Denied(request, exception.Message);
        }

        if (session.PermissionMode == ExternalToolPermissionMode.ReadOnly && !IsAllowedReadOnlyCommand(arguments.Command))
        {
            return Denied(request, "Read Only currently permits only Get-Location, pwd, git status, and git diff --stat.");
        }

        if (session.PermissionMode == ExternalToolPermissionMode.WorkspaceFullAccess
            && TryGetWorkspaceCommandDenial(session, arguments.Command, out var denial))
        {
            return Denied(request, denial);
        }

        var timeoutSeconds = Math.Clamp(arguments.Timeout ?? DefaultTimeoutSeconds, 1, MaximumTimeoutSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(arguments.Command);

        try
        {
            if (!process.Start())
            {
                return Denied(request, "Shell process did not start.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var truncated = false;
            stdout = Truncate(stdout, ref truncated);
            stderr = Truncate(stderr, ref truncated);
            return new ExternalToolExecutionResult(
                request.ToolCallId,
                request.ToolName,
                stdout,
                stderr,
                process.ExitCode,
                TimedOut: false,
                Denied: false,
                truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return new ExternalToolExecutionResult(
                request.ToolCallId,
                request.ToolName,
                string.Empty,
                $"Shell command exceeded {timeoutSeconds} seconds.",
                null,
                TimedOut: true,
                Denied: false,
                Truncated: false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static string ResolveWorkingDirectory(ExternalToolSession session, string? requested)
    {
        var root = Path.GetFullPath(session.ProjectPath);
        var candidate = string.IsNullOrWhiteSpace(requested)
            ? Path.GetFullPath(session.WorkingDirectory)
            : Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(session.WorkingDirectory, requested));
        if (session.PermissionMode != ExternalToolPermissionMode.FullAccess && !IsWithin(root, candidate))
        {
            throw new UnauthorizedAccessException($"Shell cwd is outside the project scope: {candidate}");
        }

        if (!Directory.Exists(candidate))
        {
            throw new IOException($"Shell cwd does not exist: {candidate}");
        }

        return candidate;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }

    private static bool IsAllowedReadOnlyCommand(string command)
    {
        var normalized = string.Join(' ', command.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Equals("Get-Location", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("pwd", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("git status", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("git status --short", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("git diff --stat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetWorkspaceCommandDenial(
        ExternalToolSession session,
        string command,
        out string denial)
    {
        var normalized = command.Trim();
        if (WorkspaceForbiddenSyntax.IsMatch(normalized))
        {
            denial = "Workspace Full Access permits one direct command at a time; shell composition, redirection, variables, and subexpressions are denied.";
            return true;
        }

        var commandMatch = WorkspaceCommandName.Match(normalized);
        if (!commandMatch.Success)
        {
            denial = "Workspace Full Access could not identify a direct command.";
            return true;
        }

        var commandName = commandMatch.Groups["name"].Value;
        if (commandName.StartsWith(@".\", StringComparison.Ordinal))
        {
            commandName = commandName[2..];
        }

        if (!WorkspaceCommands.Contains(commandName))
        {
            denial = $"Workspace Full Access does not permit the command '{commandName}' without Full Access.";
            return true;
        }

        if (WorkspaceParentTraversal.IsMatch(normalized))
        {
            denial = "Workspace Full Access denied a path containing parent-directory traversal.";
            return true;
        }

        if (WorkspaceAlternateRoot.IsMatch(normalized))
        {
            denial = "Workspace Full Access denied a home, drive-relative, or PowerShell provider path.";
            return true;
        }

        var root = Path.GetFullPath(session.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (Match match in WorkspaceRootedPath.Matches(normalized))
        {
            var candidateText = match.Groups["path"].Value.TrimEnd('"', '\'');
            string candidate;
            try
            {
                candidate = Path.GetFullPath(candidateText);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                denial = $"Workspace Full Access denied an invalid rooted path: {candidateText}";
                return true;
            }

            if (!IsWithin(root, candidate))
            {
                denial = $"Workspace Full Access denied a path outside the project scope: {candidate}";
                return true;
            }
        }

        denial = string.Empty;
        return false;
    }

    private static ExternalToolExecutionResult Denied(ExternalToolExecutionRequest request, string error) => new(
        request.ToolCallId,
        request.ToolName,
        string.Empty,
        error,
        null,
        TimedOut: false,
        Denied: true,
        Truncated: false);

    private static ExternalToolExecutionResult Failed(ExternalToolExecutionRequest request, string error) => new(
        request.ToolCallId,
        request.ToolName,
        string.Empty,
        error,
        1,
        TimedOut: false,
        Denied: false,
        Truncated: false);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string Truncate(string value, ref bool truncated)
    {
        if (value.Length <= MaximumOutputCharacters)
        {
            return value;
        }

        truncated = true;
        const int markerAllowance = 80;
        var half = (MaximumOutputCharacters - markerAllowance) / 2;
        return $"{value[..half]}\n... output truncated by Agent Switch ...\n{value[^half..]}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ShellArguments(string Command, string? Cwd = null, int? Timeout = null);

    private sealed record ApplyPatchArguments(string Patch);

    private sealed record PatchFile(string Path, string RelativePath, bool IsCreation, bool IsDeletion, IReadOnlyList<PatchHunk> Hunks);

    private sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<string> Lines);

    private sealed record OriginalFile(bool Exists, string Text);

    private sealed class PatchFormatException(string message) : Exception(message);

    private sealed class PatchDeniedException(string message) : Exception(message);

    private static IReadOnlyList<PatchFile> ParsePatch(string patch, string root)
    {
        if (patch.TrimStart().StartsWith("*** Begin Patch", StringComparison.Ordinal))
        {
            patch = NormalizeCodexPatch(patch, root);
        }

        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var files = new List<PatchFile>();
        for (var index = 0; index < lines.Length;)
        {
            if (!lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var oldPath = ParsePatchPath(lines[index][4..]);
            index++;
            if (index >= lines.Length || !lines[index].StartsWith("+++ ", StringComparison.Ordinal))
            {
                throw new PatchFormatException("Patch is missing the +++ file header.");
            }

            var newPath = ParsePatchPath(lines[index][4..]);
            index++;
            if (oldPath is not null && newPath is not null && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new PatchDeniedException($"Rename-style patches are not supported: {oldPath} -> {newPath}");
            }

            var hunks = new List<PatchHunk>();
            while (index < lines.Length && !lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                if (lines[index].Length == 0 || lines[index].StartsWith("diff --git ", StringComparison.Ordinal) || lines[index].StartsWith("index ", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                if (!lines[index].StartsWith("@@ ", StringComparison.Ordinal))
                {
                    throw new PatchFormatException($"Unexpected patch line: {lines[index]}");
                }

                var match = HunkHeader.Match(lines[index]);
                if (!match.Success)
                {
                    throw new PatchFormatException($"Invalid hunk header: {lines[index]}");
                }

                var oldStart = int.Parse(match.Groups[1].Value);
                var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                var newStart = int.Parse(match.Groups[3].Value);
                var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                index++;
                var hunkLines = new List<string>();
                var oldSeen = 0;
                var newSeen = 0;
                while (index < lines.Length
                    && !lines[index].StartsWith("@@ ", StringComparison.Ordinal)
                    && !lines[index].StartsWith("--- ", StringComparison.Ordinal)
                    && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal)
                    && !lines[index].StartsWith("index ", StringComparison.Ordinal))
                {
                    var line = lines[index++];
                    if (line.Length == 0
                        && (index == lines.Length
                            || lines[index].StartsWith("--- ", StringComparison.Ordinal)
                            || lines[index].StartsWith("diff --git ", StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.Length == 0 || (line[0] != ' ' && line[0] != '+' && line[0] != '-'))
                    {
                        throw new PatchFormatException("Invalid hunk line.");
                    }

                    hunkLines.Add(line);
                    if (line[0] != '+') oldSeen++;
                    if (line[0] != '-') newSeen++;
                }

                if (oldSeen != oldCount || newSeen != newCount)
                {
                    throw new PatchFormatException($"Hunk line counts do not match header at -{oldStart},{oldCount} +{newStart},{newCount}.");
                }

                hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, hunkLines));
            }

            if (hunks.Count == 0)
            {
                throw new PatchFormatException("Patch file has no hunks.");
            }

            var relative = newPath ?? oldPath ?? throw new PatchFormatException("Patch file has no path.");
            var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var canonicalRelative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            if (canonicalRelative == "." || canonicalRelative == ".." || canonicalRelative.StartsWith("../", StringComparison.Ordinal))
            {
                throw new PatchDeniedException($"Patch path escapes the project: {relative}");
            }
            if (files.Any(file => string.Equals(file.Path, absolute, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PatchFormatException($"Patch contains duplicate file path: {relative}");
            }
            files.Add(new PatchFile(absolute, canonicalRelative, oldPath is null, newPath is null, hunks));
        }

        if (files.Count == 0)
        {
            throw new PatchFormatException("Patch contains no file hunks.");
        }

        return files;
    }

    private static string NormalizeCodexPatch(string patch, string root)
    {
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var first = Array.FindIndex(lines, line => line.Length > 0);
        if (first < 0 || !string.Equals(lines[first], "*** Begin Patch", StringComparison.Ordinal))
        {
            throw new PatchFormatException("Codex patch is missing the Begin Patch marker.");
        }

        var output = new List<string>();
        var index = first + 1;
        var sawEnd = false;
        while (index < lines.Length)
        {
            if (lines[index].Length == 0)
            {
                index++;
                continue;
            }
            if (string.Equals(lines[index], "*** End Patch", StringComparison.Ordinal))
            {
                sawEnd = true;
                break;
            }

            var directive = CustomPatchFileDirective.Match(lines[index]);
            if (!directive.Success)
            {
                throw new PatchFormatException($"Unexpected Codex patch directive: {lines[index]}");
            }

            var kind = directive.Groups["kind"].Value;
            var relativePath = ParsePatchPath(directive.Groups["path"].Value)
                ?? throw new PatchFormatException("Codex patch file path is required.");
            index++;
            var body = new List<string>();
            while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
            {
                body.Add(lines[index++]);
            }

            if (kind == "Add")
            {
                if (body.Count == 0 || body.Any(line => line.Length == 0 || line[0] != '+'))
                {
                    throw new PatchFormatException($"Codex Add File requires '+' content lines: {relativePath}");
                }
                output.Add("--- /dev/null");
                output.Add($"+++ b/{relativePath}");
                output.Add($"@@ -0,0 +1,{body.Count} @@");
                output.AddRange(body);
                continue;
            }

            var absolutePath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(root, absolutePath))
            {
                throw new PatchDeniedException($"Patch path escapes the project: {relativePath}");
            }
            if (!File.Exists(absolutePath))
            {
                throw new PatchFormatException($"Cannot patch {relativePath}: the source target does not exist.");
            }

            if (kind == "Delete")
            {
                var original = ReadNormalizedLines(absolutePath);
                output.Add($"--- a/{relativePath}");
                output.Add("+++ /dev/null");
                output.Add($"@@ -1,{original.Count} +0,0 @@");
                output.AddRange(original.Select(line => $"-{line}"));
                continue;
            }

            output.Add($"--- a/{relativePath}");
            output.Add($"+++ b/{relativePath}");
            output.AddRange(NormalizeCustomUpdateHunks(relativePath, absolutePath, body));
        }

        if (!sawEnd)
        {
            throw new PatchFormatException("Codex patch is missing the End Patch marker.");
        }
        if (output.Count == 0)
        {
            throw new PatchFormatException("Codex patch contains no file operations.");
        }
        return string.Join('\n', output);
    }

    private static IReadOnlyList<string> NormalizeCustomUpdateHunks(
        string relativePath,
        string absolutePath,
        IReadOnlyList<string> body)
    {
        var output = new List<string>();
        var original = ReadNormalizedLines(absolutePath);
        var searchStart = 0;
        for (var index = 0; index < body.Count;)
        {
            var header = body[index++];
            if (!header.StartsWith("@@", StringComparison.Ordinal))
            {
                throw new PatchFormatException($"Codex Update File is missing a hunk marker: {relativePath}");
            }

            var hunkLines = new List<string>();
            while (index < body.Count && !body[index].StartsWith("@@", StringComparison.Ordinal))
            {
                var line = body[index++];
                if (line.Length == 0 || (line[0] != ' ' && line[0] != '+' && line[0] != '-'))
                {
                    throw new PatchFormatException($"Invalid Codex patch hunk line in {relativePath}.");
                }
                hunkLines.Add(line);
            }
            if (hunkLines.Count == 0)
            {
                throw new PatchFormatException($"Codex patch hunk is empty: {relativePath}");
            }

            if (HunkHeader.IsMatch(header))
            {
                output.Add(header);
                output.AddRange(hunkLines);
                continue;
            }
            if (!string.Equals(header.Trim(), "@@", StringComparison.Ordinal))
            {
                throw new PatchFormatException($"Invalid Codex patch hunk header: {header}");
            }

            var oldLines = hunkLines.Where(line => line[0] != '+').Select(line => line[1..]).ToArray();
            if (oldLines.Length == 0)
            {
                throw new PatchFormatException($"Unnumbered Codex patch hunk requires context or removed lines: {relativePath}");
            }
            var position = FindSequence(original, oldLines, searchStart);
            if (position < 0)
            {
                throw new PatchFormatException($"Codex patch context does not match {relativePath}.");
            }
            var newCount = hunkLines.Count(line => line[0] != '-');
            output.Add($"@@ -{position + 1},{oldLines.Length} +{position + 1},{newCount} @@");
            output.AddRange(hunkLines);
            searchStart = position + oldLines.Length;
        }
        return output;
    }

    private static int FindSequence(IReadOnlyList<string> source, IReadOnlyList<string> sequence, int start)
    {
        for (var index = start; index <= source.Count - sequence.Count; index++)
        {
            var matches = true;
            for (var offset = 0; offset < sequence.Count; offset++)
            {
                if (!string.Equals(source[index + offset], sequence[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return index;
        }
        return -1;
    }

    private static IReadOnlyList<string> ReadNormalizedLines(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');
    }

    private static string? ParsePatchPath(string value)
    {
        var path = value.Split('\t', 2)[0].Trim();
        if (path == "/dev/null") return null;
        if (Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new PatchDeniedException($"Patch path is absolute or rooted: {path}");
        }

        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "." || normalized.Split('/').Any(part => part == ".."))
        {
            throw new PatchDeniedException($"Patch path escapes the project: {path}");
        }

        return normalized;
    }

    private static OriginalFile ReadOriginal(PatchFile file)
    {
        if (!File.Exists(file.Path)) return new OriginalFile(false, string.Empty);
        return new OriginalFile(true, File.ReadAllText(file.Path, Encoding.UTF8));
    }

    private static string ApplyFilePatch(PatchFile file, OriginalFile original)
    {
        var hadTrailingNewline = original.Text.EndsWith('\n');
        var content = original.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var current = content.Length == 0 ? [] : content.TrimEnd('\n').Split('\n').ToList();
        var offset = 0;
        foreach (var hunk in file.Hunks)
        {
            var position = hunk.OldStart == 0 ? 0 : hunk.OldStart - 1 + offset;
            if (position < 0 || position > current.Count)
            {
                throw new PatchFormatException($"Hunk location is outside {file.RelativePath}.");
            }

            var replacement = new List<string>();
            var cursor = position;
            foreach (var line in hunk.Lines)
            {
                var value = line[1..];
                if (line[0] == ' ' || line[0] == '-')
                {
                    if (cursor >= current.Count || !string.Equals(current[cursor], value, StringComparison.Ordinal))
                    {
                        throw new PatchFormatException($"Patch context does not match {file.RelativePath}.");
                    }
                    cursor++;
                }

                if (line[0] == ' ' || line[0] == '+') replacement.Add(value);
            }

            current.RemoveRange(position, cursor - position);
            current.InsertRange(position, replacement);
            offset += replacement.Count - (cursor - position);
        }

        if (file.IsDeletion)
        {
            if (current.Count != 0) throw new PatchFormatException($"Deletion patch did not remove all content from {file.RelativePath}.");
            return string.Empty;
        }

        return string.Join('\n', current) + ((hadTrailingNewline || original.Text.Length == 0) && current.Count > 0 ? "\n" : string.Empty);
    }

    private static void CommitFiles(IReadOnlyList<PatchFile> files, IReadOnlyDictionary<string, OriginalFile> originals, IReadOnlyDictionary<string, string> updated)
    {
        var committed = new List<PatchFile>();
        try
        {
            foreach (var file in files)
            {
                committed.Add(file);
                if (file.IsDeletion)
                {
                    if (File.Exists(file.Path)) File.Delete(file.Path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                    File.WriteAllText(file.Path, updated[file.Path], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
        }
        catch
        {
            foreach (var file in committed)
            {
                var original = originals[file.Path];
                if (original.Exists) File.WriteAllText(file.Path, original.Text, new UTF8Encoding(false));
                else if (File.Exists(file.Path)) File.Delete(file.Path);
            }
            throw;
        }
    }

    private static bool IsAllowedWritePath(ExternalToolSession session, string root, string candidate)
    {
        return session.AllowedWriteScope.Any(scope =>
        {
            if (string.IsNullOrWhiteSpace(scope)) return false;
            var scopePath = Path.GetFullPath(Path.IsPathRooted(scope) ? scope : Path.Combine(root, scope));
            return string.Equals(scopePath, candidate, StringComparison.OrdinalIgnoreCase) || IsWithin(scopePath, candidate);
        });
    }

    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CustomPatchFileDirective = new(
        @"^\*\*\* (?<kind>Add|Update|Delete) File: (?<path>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WorkspaceForbiddenSyntax = new(
        @"[;|&<>`\r\n]|\$\(|\$\{|\$[A-Za-z_]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WorkspaceCommandName = new(
        @"^\s*(?:\.\\)?(?<name>[A-Za-z][A-Za-z0-9_.-]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WorkspaceParentTraversal = new(
        @"(?:^|[\s\""'=])\.\.(?:[\\/]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WorkspaceAlternateRoot = new(
        @"(?:^|[\s\""'=])(?:~[\\/]|\\(?!\\)|(?:Env|Variable|Alias|Function|Registry|HKCU|HKLM|Cert):)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex WorkspaceRootedPath = new(
        @"(?<![A-Za-z0-9_])(?<path>[A-Za-z]:[\\/](?:[^\s\""';|&<>`]+|\s+(?!-))+|\\\\[^\s\""';|&<>`]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> WorkspaceCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add-Content",
        "cargo",
        "cmake",
        "Copy-Item",
        "ctest",
        "dir",
        "dotnet",
        "gc",
        "gci",
        "Get-ChildItem",
        "Get-Content",
        "Get-Location",
        "git",
        "go",
        "gradle",
        "gradlew",
        "gradlew.bat",
        "java",
        "javac",
        "ls",
        "make",
        "Move-Item",
        "msbuild",
        "mvn",
        "mvnw",
        "mvnw.cmd",
        "New-Item",
        "ninja",
        "node",
        "npm",
        "npx",
        "nuget",
        "pnpm",
        "pwd",
        "py",
        "python",
        "python3",
        "Remove-Item",
        "Resolve-Path",
        "rg",
        "rustc",
        "Select-String",
        "Set-Content",
        "Test-Path",
        "type",
        "yarn",
    };
}
