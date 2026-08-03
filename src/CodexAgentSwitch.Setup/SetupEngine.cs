using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAgentSwitch.Setup;

public sealed record InstallResult(string TargetDirectory, string? BackupDirectory, string Message, string? ShortcutPath = null);

public sealed class SetupEngine(IStartMenuShortcut? startMenuShortcut = null)
{
    public async Task<InstallResult> InstallAsync(
        string payloadZip,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var payload = Path.GetFullPath(payloadZip);
        var target = ValidateTarget(targetDirectory);
        if (!File.Exists(payload))
        {
            throw new FileNotFoundException("安装负载不存在。", payload);
        }

        await VerifyPayloadAsync(payload, cancellationToken);
        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("安装目录必须有父目录。");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, $".cas-install-stage-{Guid.NewGuid():N}");
        var backup = Directory.Exists(target)
            ? target + ".backup-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
            : null;
        string? shortcutPath = null;
        try
        {
            progress?.Report("正在解压并检查安装负载…");
            ZipFile.ExtractToDirectory(payload, stage);
            if (!File.Exists(Path.Combine(stage, "CodexAgentSwitch.App.exe")))
            {
                throw new InvalidDataException("安装负载缺少 CodexAgentSwitch.App.exe。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (backup is not null)
            {
                progress?.Report("正在备份现有安装…");
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(stage, target);
                var backupData = backup is null ? null : Path.Combine(backup, "data");
                if (backupData is not null && Directory.Exists(backupData))
                {
                    Directory.Move(backupData, Path.Combine(target, "data"));
                }

                var manifest = new
                {
                    installedAt = DateTimeOffset.Now,
                    payload = Path.GetFileName(payload),
                    payloadSha256 = await Sha256Async(payload, cancellationToken),
                    backup,
                };
                await File.WriteAllTextAsync(
                    Path.Combine(target, "install.json"),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken);
                shortcutPath = startMenuShortcut?.Create(target);
            }
            catch
            {
                if (Directory.Exists(target))
                {
                    Directory.Move(target, target + ".failed-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
                }

                if (backup is not null && Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                    startMenuShortcut?.Create(target);
                }
                else startMenuShortcut?.Remove();

                throw;
            }

            return new InstallResult(target, backup, backup is null ? "安装完成。" : $"升级完成；旧版本保留在 {backup}。", shortcutPath);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }
        }
    }

    public InstallResult Uninstall(string targetDirectory, bool deleteUserData = false)
    {
        var target = ValidateTarget(targetDirectory);
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException("安装目录不存在。");
        }

        if (deleteUserData)
        {
            var data = Path.Combine(target, "data");
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }

        var recovery = target + ".removed-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        Directory.Move(target, recovery);
        startMenuShortcut?.Remove();
        var dataMessage = deleteUserData ? "本地 data 已按明确选择删除；Windows 凭据仍未自动删除。" : "本地 data 和凭据均未自动删除。";
        return new InstallResult(target, recovery, $"已移出安装目录；可从 {recovery} 恢复或手动删除。{dataMessage}");
    }

    public static string DefaultTarget()
    {
        var configured = Environment.GetEnvironmentVariable("CAS_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Directory.Exists("E:\\")
            ? @"E:\Apps\Codex Agent Switch"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Agent Switch");
    }

    private static async Task VerifyPayloadAsync(string payload, CancellationToken cancellationToken)
    {
        var checksumPath = payload + ".sha256";
        if (!File.Exists(checksumPath))
        {
            throw new FileNotFoundException("缺少安装负载 SHA-256 文件。", checksumPath);
        }

        var expected = (await File.ReadAllTextAsync(checksumPath, cancellationToken)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var actual = await Sha256Async(payload, cancellationToken);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安装负载 SHA-256 不匹配，安装已停止。");
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ValidateTarget(string targetDirectory)
    {
        var target = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("禁止把驱动器根目录作为安装目标。", nameof(targetDirectory));
        }

        return target;
    }
}
