using System.Collections.Concurrent;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Usage;

namespace CodexAgentSwitch.Application.Usage;

/// <summary>
/// Process-local guard registry. A guard is isolated by normalized project
/// directory and exact native session id; no CWD-only usage attribution is
/// permitted at the observable boundary.
/// </summary>
public sealed class MainCostGuardCoordinator
{
    private readonly ConcurrentDictionary<string, MainCostGuard> guards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly MainCostGuardOptions options;
    private MainCostGuard? initialGuard;

    public MainCostGuardCoordinator(MainCostGuardOptions? options = null, MainCostGuard? initialGuard = null)
    {
        this.options = options ?? MainCostGuardOptions.Default;
        this.initialGuard = initialGuard;
    }

    public MainCostGuard Resolve(string workingDirectory, string sessionId)
    {
        var cwd = WorkPackageLease.NormalizePath(workingDirectory);
        var session = sessionId?.Trim() ?? string.Empty;
        if (activeSessions.TryGetValue(cwd, out var pending) && pending.Length == 0
            && guards.TryRemove(Key(cwd, string.Empty), out var pendingGuard))
        {
            guards[Key(cwd, session)] = pendingGuard;
        }
        activeSessions[cwd] = session;
        return guards.GetOrAdd(Key(cwd, session), _ => TakeInitialOrCreate());
    }

    public MainCostGuard ResolveForWorkingDirectory(string workingDirectory)
    {
        var cwd = WorkPackageLease.NormalizePath(workingDirectory);
        var session = activeSessions.TryGetValue(cwd, out var value) ? value : string.Empty;
        return guards.GetOrAdd(Key(cwd, session), _ => TakeInitialOrCreate());
    }

    private MainCostGuard TakeInitialOrCreate() => Interlocked.Exchange(ref initialGuard, null) ?? new MainCostGuard(options);

    private static string Key(string cwd, string session) => $"{cwd}\u001f{session}";
}
