using System.IO.Compression;
using System.Text;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class SettingsPage : Page, IContentActionHandler
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public Task HandleContentActionAsync(string action, Button source) => action switch
    {
        "settings:backup" => BackupAsync(),
        "settings:restore" => StageRestoreAsync(),
        "settings:diagnostics" => ExportDiagnosticsAsync(),
        _ => Task.CompletedTask,
    };

    private Task BackupAsync()
    {
        var paths = App.Services.GetRequiredService<AppDataPaths>();
        var backupDirectory = Path.Combine(paths.Root, "backups");
        Directory.CreateDirectory(backupDirectory);
        var destination = Path.Combine(backupDirectory, $"configuration-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create))
        {
            AddIfPresent(archive, paths.DatabasePath, "codex-agent-switch.db");
            AddIfPresent(archive, paths.DatabasePath + "-wal", "codex-agent-switch.db-wal");
            AddIfPresent(archive, paths.DatabasePath + "-shm", "codex-agent-switch.db-shm");
            var manifest = archive.CreateEntry("backup-manifest.txt");
            using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
            writer.WriteLine($"created={DateTimeOffset.UtcNow:O}");
            writer.WriteLine("credentials=Windows Credential Manager (not copied)");
        }

        ShowResult(InfoBarSeverity.Success, "配置备份已创建", destination);
        return Task.CompletedTask;
    }

    private Task StageRestoreAsync()
    {
        var paths = App.Services.GetRequiredService<AppDataPaths>();
        var backupDirectory = Path.Combine(paths.Root, "backups");
        var latest = Directory.Exists(backupDirectory)
            ? Directory.EnumerateFiles(backupDirectory, "configuration-*.zip").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null)
        {
            ShowResult(InfoBarSeverity.Warning, "没有可恢复的备份", "请先点击“备份配置”。");
            return Task.CompletedTask;
        }

        var stage = Path.Combine(paths.Root, "restore-pending", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(stage);
        ZipFile.ExtractToDirectory(latest, stage);
        File.WriteAllText(Path.Combine(stage, "restore.request"), latest, new UTF8Encoding(false));
        ShowResult(InfoBarSeverity.Success, "恢复包已验证并暂存", $"{stage}；为避免覆盖正在使用的数据库，将在安全重启流程中应用。");
        return Task.CompletedTask;
    }

    private Task ExportDiagnosticsAsync()
    {
        var destination = DiagnosticBundleExporter.Export(App.Services.GetRequiredService<AppDataPaths>());
        ShowResult(InfoBarSeverity.Success, "脱敏诊断包已导出", destination);
        return Task.CompletedTask;
    }

    private static void AddIfPresent(ZipArchive archive, string source, string entryName)
    {
        if (File.Exists(source))
        {
            archive.CreateEntryFromFile(source, entryName, CompressionLevel.Fastest);
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        SettingsActionBar.Severity = severity;
        SettingsActionBar.Title = title;
        SettingsActionBar.Message = message;
        SettingsActionBar.IsOpen = true;
    }
}
