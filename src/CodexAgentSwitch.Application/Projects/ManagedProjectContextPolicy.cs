using CodexAgentSwitch.Domain.Projects;
using CodexAgentSwitch.Domain.Tasks;
using CodexAgentSwitch.Application.Tasks;

namespace CodexAgentSwitch.Application.Projects;

public enum ManagedContextRejectionCode
{
    None,
    ProjectNotFound,
    ProjectMismatch,
    ProjectArchived,
    AppliedSnapshotMissing,
    ProjectPathInvalid,
    TaskPathOutsideManagedRoot,
    ThreadNotBound,
    ThreadIdentityMissing,
    SessionMismatch,
    AppServerInstanceMismatch,
    OwnershipLeaseMissing,
    OwnershipStateNotControllable,
}

public sealed record ManagedContextAccessDecision(
    bool Allowed,
    ManagedContextRejectionCode Code,
    string Message,
    string? CanonicalProjectRoot = null);

/// <summary>
/// Fail-closed policy for the 0.2.7.0 managed context boundary. This policy
/// performs no telemetry, persistence, subscription, or RPC work.
/// </summary>
public sealed class ManagedProjectContextPolicy
{
    public ManagedContextAccessDecision EvaluateEnrollment(
        AgentProject? project,
        ControlledTaskSession task)
    {
        if (project is null)
        {
            return Reject(ManagedContextRejectionCode.ProjectNotFound, "项目未登记到 CAS。");
        }

        if (!string.Equals(task.ProjectId, project.Id, StringComparison.Ordinal))
        {
            return Reject(ManagedContextRejectionCode.ProjectMismatch, "任务没有通过该 CAS 项目的受管入口创建。");
        }

        if (project.IsArchived)
        {
            return Reject(ManagedContextRejectionCode.ProjectArchived, "项目已归档。");
        }

        if (project.NativeCodexAdaptation?.AppliedSnapshot is null)
        {
            return Reject(ManagedContextRejectionCode.AppliedSnapshotMissing, "项目没有有效的 Applied Snapshot。");
        }

        string projectRoot;
        string taskRoot;
        try
        {
            projectRoot = ManagedProjectPath.CanonicalizeExistingDirectory(project.WorkingDirectory);
            taskRoot = ManagedProjectPath.CanonicalizeExistingDirectory(task.WorkingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Reject(ManagedContextRejectionCode.ProjectPathInvalid, $"项目路径无法规范化：{exception.Message}");
        }

        return ManagedProjectPath.Equals(projectRoot, taskRoot)
            || ManagedProjectPath.IsDescendant(projectRoot, taskRoot)
            ? new(true, ManagedContextRejectionCode.None, "任务满足受管项目纳管边界。", projectRoot)
            : Reject(ManagedContextRejectionCode.TaskPathOutsideManagedRoot, "任务目录不属于项目允许的管理范围。", projectRoot);
    }

    public ManagedContextAccessDecision EvaluateControl(
        AgentProject? project,
        ControlledTaskSession task,
        ManagedContextSession binding,
        string appServerInstanceId,
        MainAgentThreadIdentity? threadIdentity)
    {
        var enrollment = EvaluateEnrollment(project, task);
        if (!enrollment.Allowed)
        {
            return enrollment;
        }

        if (string.IsNullOrWhiteSpace(task.MainThreadId)
            || !string.Equals(task.MainThreadId, binding.ThreadId, StringComparison.Ordinal))
        {
            return Reject(ManagedContextRejectionCode.ThreadNotBound, "任务 thread 与受管绑定不一致。", enrollment.CanonicalProjectRoot);
        }

        if (threadIdentity is null
            || !string.Equals(threadIdentity.ThreadId, binding.ThreadId, StringComparison.Ordinal)
            || !string.Equals(threadIdentity.SessionId, binding.SessionId, StringComparison.Ordinal))
        {
            return Reject(ManagedContextRejectionCode.ThreadIdentityMissing, "App Server thread/session identity 与受管绑定不一致。", enrollment.CanonicalProjectRoot);
        }

        if (!string.Equals(task.Id, binding.TaskSessionId, StringComparison.Ordinal)
            || !string.Equals(task.ProjectId, binding.ProjectId, StringComparison.Ordinal))
        {
            return Reject(ManagedContextRejectionCode.SessionMismatch, "项目、任务会话与受管绑定不一致。", enrollment.CanonicalProjectRoot);
        }

        bool sameCanonicalRoot;
        try
        {
            sameCanonicalRoot = ManagedProjectPath.Equals(enrollment.CanonicalProjectRoot!, binding.CanonicalProjectRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Reject(ManagedContextRejectionCode.ProjectPathInvalid, "受管绑定中的项目根无效。", enrollment.CanonicalProjectRoot);
        }

        if (!sameCanonicalRoot)
        {
            return Reject(ManagedContextRejectionCode.ProjectPathInvalid, "受管绑定中的项目根已漂移。", enrollment.CanonicalProjectRoot);
        }

        bool threadPathAllowed;
        try
        {
            threadPathAllowed = ManagedProjectPath.Equals(enrollment.CanonicalProjectRoot!, threadIdentity.WorkingDirectory)
                || ManagedProjectPath.IsDescendant(enrollment.CanonicalProjectRoot!, threadIdentity.WorkingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Reject(ManagedContextRejectionCode.ProjectPathInvalid, "App Server thread 工作目录无效。", enrollment.CanonicalProjectRoot);
        }

        if (!threadPathAllowed)
        {
            return Reject(ManagedContextRejectionCode.ProjectPathInvalid, "App Server thread 工作目录不属于受管项目。", enrollment.CanonicalProjectRoot);
        }

        if (string.IsNullOrWhiteSpace(appServerInstanceId)
            || !string.Equals(binding.AppServerInstanceId, appServerInstanceId, StringComparison.Ordinal))
        {
            return Reject(ManagedContextRejectionCode.AppServerInstanceMismatch, "App Server 控制连接不属于该绑定。", enrollment.CanonicalProjectRoot);
        }

        if (string.IsNullOrWhiteSpace(binding.OwnershipLeaseId))
        {
            return Reject(ManagedContextRejectionCode.OwnershipLeaseMissing, "受管绑定没有 ownership lease。", enrollment.CanonicalProjectRoot);
        }

        return binding.OwnershipState is ManagedContextOwnershipState.Owned or ManagedContextOwnershipState.Idle
            ? new(true, ManagedContextRejectionCode.None, "受管会话可进入压缩决策。", enrollment.CanonicalProjectRoot)
            : Reject(ManagedContextRejectionCode.OwnershipStateNotControllable, "当前 ownership 状态不允许进入压缩决策。", enrollment.CanonicalProjectRoot);
    }

    public AgentProject? ResolveOwner(
        IReadOnlyCollection<AgentProject> projects,
        string workingDirectory)
    {
        string taskRoot;
        try
        {
            taskRoot = ManagedProjectPath.CanonicalizeExistingDirectory(workingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return projects
            .Where(project => !project.IsArchived
                && project.NativeCodexAdaptation?.AppliedSnapshot is not null)
            .Select(project => TryCanonicalize(project, taskRoot))
            .Where(candidate => candidate is not null)
            .OrderByDescending(candidate => candidate!.Value.Root.Length)
            .Select(candidate => candidate!.Value.Project)
            .FirstOrDefault();
    }

    private static (AgentProject Project, string Root)? TryCanonicalize(AgentProject project, string taskRoot)
    {
        try
        {
            var root = ManagedProjectPath.CanonicalizeExistingDirectory(project.WorkingDirectory);
            var matches = ManagedProjectPath.Equals(root, taskRoot)
                || ManagedProjectPath.IsDescendant(root, taskRoot);
            return matches ? (project, root) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static ManagedContextAccessDecision Reject(
        ManagedContextRejectionCode code,
        string message,
        string? canonicalProjectRoot = null) => new(false, code, message, canonicalProjectRoot);
}

internal static class ManagedProjectPath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string CanonicalizeExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("路径缺少根目录。", nameof(path));
        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var segment in remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException($"目录不存在：{current}");
            }

            if (directory.LinkTarget is not null)
            {
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"无法解析目录链接：{current}");
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    public static bool Equals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    public static bool IsDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !string.Equals(relative, ".", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
