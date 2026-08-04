using System.IO.Compression;
using System.Text;
using CodexAgentSwitch.Application.NativeCodex;
using CodexAgentSwitch.Infrastructure.CodexAppServer;
using CodexAgentSwitch.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace CodexAgentSwitch.App.Views;

public sealed partial class SettingsPage : Page, IContentActionHandler
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        await RefreshCodexEntryStatusAsync();
    }

    private async void SaveDesktopEntry(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        try
        {
            await App.Services.GetRequiredService<ICodexDesktopLauncher>()
                .SaveManualExecutableAsync(DesktopEntryBox.Text.Trim());
            ShowResult(InfoBarSeverity.Success, "桌面应用入口已保存", "原生 Codex 模式将使用此图形桌面应用入口，且不会回退到 CLI。");
            await RefreshCodexEntryStatusAsync();
        }
        catch (Exception exception)
        {
            ShowResult(InfoBarSeverity.Error, "无法保存桌面应用入口", exception.Message);
        }
    }

    private async Task RefreshCodexEntryStatusAsync()
    {
        try
        {
            var runtime = await App.Services.GetRequiredService<CodexRuntimeManager>().DetectAsync();
            var cli = await App.Services.GetRequiredService<CodexCommandLocator>().LocateAsync();
            var desktop = await App.Services.GetRequiredService<ICodexDesktopLauncher>().DetectAsync();
            CliPathText.Text = runtime.Installed
                ? $"已检测：{cli.Command?.Executable ?? runtime.Version ?? "路径未知"}"
                : $"未检测：{runtime.Message}";
            DesktopAppStatusText.Text = desktop.IsAvailable
                ? $"已检测：{desktop.AppUserModelId ?? desktop.ExecutablePath ?? "官方桌面应用"}"
                : $"未检测：{desktop.Status}";
            DesktopEntryBox.Text = desktop.IsManualEntry ? desktop.ExecutablePath ?? string.Empty : string.Empty;
            AppServerStatusText.Text = runtime.AppServerRunning
                ? "已连接"
                : "未连接；CodexAgentSwitch 模式将在需要时启动";
        }
        catch (Exception exception)
        {
            CliPathText.Text = "状态读取失败";
            DesktopAppStatusText.Text = "状态读取失败";
            AppServerStatusText.Text = exception.Message;
        }
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
