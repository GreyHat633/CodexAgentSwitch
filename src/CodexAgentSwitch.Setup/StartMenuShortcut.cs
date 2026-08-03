using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexAgentSwitch.Setup;

public interface IStartMenuShortcut
{
    string Create(string targetDirectory);
    void Remove();
}

public sealed class WindowsStartMenuShortcut(string? programsDirectory = null) : IStartMenuShortcut
{
    private readonly string programsDirectory = programsDirectory
        ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public string Create(string targetDirectory)
    {
        var app = Path.GetFullPath(Path.Combine(targetDirectory, "CodexAgentSwitch.App.exe"));
        if (!File.Exists(app)) throw new FileNotFoundException("无法为不存在的主程序创建快捷方式。", app);
        var icon = Path.GetFullPath(Path.Combine(targetDirectory, "AppIcon.ico"));
        var folder = Path.Combine(programsDirectory, "Codex Agent Switch");
        Directory.CreateDirectory(folder);
        var shortcutPath = Path.Combine(folder, "Codex Agent Switch.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows Script Host 不可用，无法创建开始菜单快捷方式。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var shortcutType = shortcut!.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [app]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [targetDirectory]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["Codex Agent Switch"]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                [File.Exists(icon) ? icon : $"{app},0"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return shortcutPath;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Remove()
    {
        var shortcutPath = Path.Combine(programsDirectory, "Codex Agent Switch", "Codex Agent Switch.lnk");
        if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
    }
}
