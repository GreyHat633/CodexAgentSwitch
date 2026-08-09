using System.Runtime.InteropServices;
using CodexAgentSwitch.Application.Scheduling;
using CodexAgentSwitch.Domain.Scheduling;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CodexAgentSwitch.App;

public sealed class TrayIconService : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 0x113;
    private const uint WmCommand = 0x0111;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconWarning = 0x00000030;
    private const int IdYes = 6;
    private const uint MenuOpen = 1001;
    private const uint MenuPauseResume = 1003;
    private const uint MenuStop = 1004;
    private const uint MenuWaitExit = 1005;
    private const uint MenuExit = 1006;

    private readonly Window window;
    private readonly IWorkerScheduler scheduler;
    private readonly nint hwnd;
    private readonly WindowProcedure windowProcedure;
    private readonly nint previousWindowProcedure;
    private nint iconHandle;
    private bool allowClose;
    private bool exitWhenIdle;
    private bool disposed;

    public TrayIconService(Window window, IWorkerScheduler scheduler)
    {
        this.window = window;
        this.scheduler = scheduler;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        windowProcedure = WindowProc;
        previousWindowProcedure = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(windowProcedure));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        iconHandle = File.Exists(iconPath) ? LoadImage(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile) : 0;
        UpdateTrayIcon(add: true);
        scheduler.SnapshotChanged += OnSnapshotChanged;
        window.AppWindow.Closing += OnWindowClosing;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        scheduler.SnapshotChanged -= OnSnapshotChanged;
        window.AppWindow.Closing -= OnWindowClosing;
        var data = CreateNotifyData();
        ShellNotifyIcon(NimDelete, ref data);
        SetWindowLongPtr(hwnd, GwlpWndProc, previousWindowProcedure);
        if (iconHandle != 0)
        {
            DestroyIcon(iconHandle);
            iconHandle = 0;
        }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CAS_CAPTURE_EXIT"), "1", StringComparison.Ordinal))
        {
            allowClose = true;
            Dispose();
            return;
        }

        if (!allowClose)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void OnSnapshotChanged(object? sender, SchedulerSnapshot snapshot) => window.DispatcherQueue.TryEnqueue(() =>
    {
        UpdateTrayIcon(add: false);
        if (exitWhenIdle && snapshot.ActiveTaskCount == 0)
        {
            _ = ExitNowAsync(skipPrompt: true);
        }
    });

    private nint WindowProc(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message == WmTray)
        {
            var eventMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (eventMessage == WmLButtonDoubleClick)
            {
                OpenWindow();
                return 0;
            }

            if (eventMessage == WmRButtonUp)
            {
                ShowContextMenu();
                return 0;
            }
        }
        else if (message == WmCommand)
        {
            var command = unchecked((uint)wParam.ToInt64()) & 0xFFFF;
            HandleMenuCommand(command);
            return 0;
        }

        return CallWindowProc(previousWindowProcedure, windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var snapshot = scheduler.Snapshot;
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString, MenuOpen, "打开 Agent Switch");
            AppendMenu(menu, MfGrayed, 0, $"Scheduler：{StateLabel(snapshot.State)} · {snapshot.ActiveTaskCount} 个活动任务");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuPauseResume, snapshot.State == SchedulerState.Paused ? "恢复调度" : "暂停接受新任务");
            AppendMenu(menu, MfString, MenuStop, "停止 Scheduler");
            AppendMenu(menu, MfString, MenuWaitExit, "等待任务完成后退出");
            AppendMenu(menu, MfString, MenuExit, "完全退出");
            GetCursorPos(out var point);
            SetForegroundWindow(hwnd);
            TrackPopupMenuEx(menu, TpmRightButton, point.X, point.Y, hwnd, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(uint command)
    {
        switch (command)
        {
            case MenuOpen: OpenWindow(); break;
            case MenuPauseResume: _ = PauseOrResumeAsync(); break;
            case MenuStop: _ = StopAsync(); break;
            case MenuWaitExit: _ = ExitWhenIdleAsync(); break;
            case MenuExit: _ = ExitNowAsync(skipPrompt: false); break;
        }
    }

    private void OpenWindow()
    {
        window.AppWindow.Show();
        window.Activate();
    }

    private async Task PauseOrResumeAsync()
    {
        if (scheduler.Snapshot.State == SchedulerState.Paused) await scheduler.ResumeAsync();
        else await scheduler.PauseAsync();
    }

    private async Task StopAsync()
    {
        var active = scheduler.Snapshot.ActiveTaskCount;
        if (active > 0 && !Confirm($"Agent Switch 当前正在处理 {active} 个任务。立即停止会中断这些任务，是否继续？", "停止 Scheduler")) return;
        await scheduler.StopAsync(active > 0);
    }

    private Task ExitWhenIdleAsync()
    {
        if (scheduler.Snapshot.ActiveTaskCount == 0) return ExitNowAsync(skipPrompt: true);
        exitWhenIdle = true;
        return scheduler.PauseAsync();
    }

    private async Task ExitNowAsync(bool skipPrompt)
    {
        var active = scheduler.Snapshot.ActiveTaskCount;
        if (!skipPrompt && active > 0 && !Confirm($"Agent Switch 当前正在处理 {active} 个任务。立即退出会中断这些任务，是否继续？", "完全退出")) return;
        await scheduler.StopAsync(active > 0);
        allowClose = true;
        Dispose();
        window.Close();
    }

    private bool Confirm(string message, string title) => MessageBox(hwnd, message, title, MbYesNo | MbIconWarning) == IdYes;

    private void UpdateTrayIcon(bool add)
    {
        var data = CreateNotifyData();
        ShellNotifyIcon(add ? NimAdd : 0x00000001, ref data);
    }

    private NotifyIconData CreateNotifyData()
    {
        var snapshot = scheduler.Snapshot;
        var text = $"Codex Agent Switch · {StateLabel(snapshot.State)} · {snapshot.ActiveTaskCount} 个任务";
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = hwnd,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = WmTray,
            IconHandle = iconHandle,
            Tip = text[..Math.Min(127, text.Length)],
        };
    }

    private static string StateLabel(SchedulerState state) => state switch
    {
        SchedulerState.Stopped => "未启动",
        SchedulerState.Ready => "已就绪",
        SchedulerState.Working => "工作中",
        SchedulerState.Paused => "已暂停",
        SchedulerState.Faulted => "异常",
        _ => state.ToString(),
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint hwnd, string text, string caption, uint type);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern nint SetWindowLong32(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);
    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value);
}
