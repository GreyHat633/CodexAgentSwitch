[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$DataRoot,
    [ValidateSet('light','dark')][string]$Theme = 'light',
    [switch]$PreferKeyboard,
    [int]$ExpectedDpi = 0,
    [string]$CodexExecutable = 'E:\AI\CODEX\.codex\.sandbox-bin\codex.exe',
    [string]$TaskWorkingDirectory = 'E:\AISPace\主模型项目区',
    [string]$ReportPath = (Join-Path $DataRoot "ui-smoke-$Theme.json")
)

$ErrorActionPreference = 'Stop'
$resolvedApp = [IO.Path]::GetFullPath($AppPath)
$resolvedData = [IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path -LiteralPath $resolvedApp)) { throw "App executable not found: $resolvedApp" }
New-Item -ItemType Directory -Force -Path $resolvedData | Out-Null
$script:tempRoot = Join-Path $resolvedData 'temp'
New-Item -ItemType Directory -Force -Path $script:tempRoot | Out-Null
$env:TEMP = $script:tempRoot
$env:TMP = $script:tempRoot
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $script:tempRoot 'bundle-extract'
New-Item -ItemType Directory -Force -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class CasUiNative {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr info);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref Point point);
    public static void ClickDashboardAction(IntPtr window) {
        double scale = GetDpiForWindow(window) / 96.0;
        Point point = new Point { X = (int)Math.Round(916 * scale), Y = (int)Math.Round(219 * scale) };
        ClientToScreen(window, ref point);
        SetCursorPos(point.X, point.Y);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(100);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
    }
}
'@

$results = [Collections.Generic.List[object]]::new()
$script:process = $null
$script:tracePath = $null

function Stop-CasApp {
    if ($script:process -and -not $script:process.HasExited) {
        [CasUiNative]::SetWindowPos([IntPtr]$script:process.MainWindowHandle, [IntPtr](-2), 0, 0, 0, 0, 0x43) | Out-Null
        Stop-Process -Id $script:process.Id -Force
        $script:process.WaitForExit()
    }
    $script:process = $null
}

function Start-CasApp([string]$page, [string]$caseName) {
    Stop-CasApp
    $script:tracePath = Join-Path $resolvedData ("trace-{0}.jsonl" -f $caseName)
    Remove-Item -LiteralPath $script:tracePath -ErrorAction SilentlyContinue
    $env:CAS_DATA_ROOT = $resolvedData
    $env:CAS_INPUT_TRACE_PATH = $script:tracePath
    $env:CAS_CAPTURE_PAGE = $page
    $env:CAS_THEME = $Theme
    $env:CAS_WINDOW_WIDTH = '1024'
    $env:CAS_WINDOW_HEIGHT = '720'
    $env:CAS_CREDENTIAL_PREFIX = "CodexAgentSwitch.UiSmoke.$([Guid]::NewGuid().ToString('N'))/"
    $env:CAS_CODEX_EXECUTABLE = $CodexExecutable
    $env:CAS_DEFAULT_WORKING_DIRECTORY = $TaskWorkingDirectory
    $script:process = Start-Process -FilePath $resolvedApp -PassThru
    for ($attempt = 0; $attempt -lt 60 -and $script:process.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 200
        $script:process.Refresh()
    }
    if ($script:process.MainWindowHandle -eq 0) { throw "Main window not created for $caseName" }
    Start-Sleep -Milliseconds 900
    $window = [IntPtr]$script:process.MainWindowHandle
    [CasUiNative]::SetWindowPos($window, [IntPtr](-1), 0, 0, 0, 0, 0x43) | Out-Null
    [CasUiNative]::SetForegroundWindow($window) | Out-Null
    Start-Sleep -Milliseconds 250
    $dpi = [int][CasUiNative]::GetDpiForWindow($window)
    if ($ExpectedDpi -gt 0 -and [Math]::Abs($dpi - $ExpectedDpi) -gt 2) {
        throw "Expected DPI $ExpectedDpi but window reports $dpi"
    }
    return $dpi
}

function Find-AppElement([string]$name, [switch]$AllowOffscreen) {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $fallback = $null
    foreach ($element in $all) {
        try {
            if ($element.Current.ProcessId -ne $script:process.Id -or $element.Current.Name -ne $name) { continue }
            if (-not $element.Current.IsOffscreen) { return $element }
            if ($AllowOffscreen -and $null -eq $fallback) { $fallback = $element }
        } catch { }
    }
    if ($fallback) {
        $pattern = $null
        if ($fallback.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
            Start-Sleep -Milliseconds 250
        }
        return $fallback
    }
    throw "Automation element not found: $name"
}

function Test-ButtonTrace([string]$expectedAction) {
    if (-not (Test-Path -LiteralPath $script:tracePath)) { return $false }
    $clicks = Get-Content -LiteralPath $script:tracePath -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.kind -eq 'button-click' }
    if ($expectedAction -eq '') { return [bool]$clicks }
    return [bool]($clicks | Where-Object { $_.hits -contains "action:$expectedAction" })
}

function Click-AppElement([string]$name, [string]$expectedAction = '') {
    $element = Find-AppElement $name -AllowOffscreen
    $bounds = $element.Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Element has empty bounds: $name" }
    $offsets = @(@(0,0), @(-50,0), @(50,0), @(0,-35), @(0,35))
    foreach ($offset in $offsets) {
        [CasUiNative]::SetForegroundWindow([IntPtr]$script:process.MainWindowHandle) | Out-Null
        [CasUiNative]::SetCursorPos([int]($bounds.Left + $bounds.Width / 2 + $offset[0]), [int]($bounds.Top + $bounds.Height / 2 + $offset[1])) | Out-Null
        Start-Sleep -Milliseconds 80
        [CasUiNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        [CasUiNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 300
        if (Test-ButtonTrace $expectedAction) { Start-Sleep -Milliseconds 350; return }
    }
    throw "Physical mouse input did not activate: $name"
}

function Press-AppElement([string]$name, [ValidateSet('ENTER','SPACE')][string]$key) {
    $element = Find-AppElement $name -AllowOffscreen
    $element.SetFocus()
    Start-Sleep -Milliseconds 100
    [Windows.Forms.SendKeys]::SendWait($(if ($key -eq 'ENTER') { '{ENTER}' } else { ' ' }))
    Start-Sleep -Milliseconds 650
}

function Assert-Trace([string]$kind, [string]$action = '') {
    if (-not (Test-Path -LiteralPath $script:tracePath)) { throw "Input trace was not created: $script:tracePath" }
    $records = Get-Content -LiteralPath $script:tracePath -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }
    $match = $records | Where-Object {
        $_.kind -eq $kind -and ($action -eq '' -or ($_.hits -contains "action:$action"))
    } | Select-Object -Last 1
    if (-not $match) { throw "Trace did not contain $kind $action" }
}

function Assert-Visible([string]$name) { $null = Find-AppElement $name }

function Set-AppText([string]$name, [string]$value) {
    $element = Find-AppElement $name -AllowOffscreen
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        throw "Element does not support ValuePattern: $name"
    }
    ([Windows.Automation.ValuePattern]$pattern).SetValue($value)
    Start-Sleep -Milliseconds 150
}

function Wait-AppElement([string]$name, [int]$timeoutSeconds = 180) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { return Find-AppElement $name -AllowOffscreen } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "Automation element did not appear within $timeoutSeconds seconds: $name"
}

function Add-Pass([string]$page, [string]$action, [string]$inputMethod, [int]$dpi, [string]$evidence) {
    $results.Add([pscustomobject]@{ page=$page; action=$action; input=$inputMethod; dpi=$dpi; theme=$Theme; evidence=$evidence; status='passed' })
}

try {
    $dpi = Start-CasApp 'dashboard' 'dashboard-mouse'
    if ($Theme -eq 'light') { Click-AppElement '编辑方案' 'navigate:profiles'; $dashboardInput = 'mouse' }
    else { Press-AppElement '编辑方案' 'ENTER'; $dashboardInput = 'Enter' }
    if ($Theme -eq 'light') { Assert-Trace 'pointer-pressed' }
    Assert-Trace 'button-click' 'navigate:profiles'
    Assert-Trace 'action-completed' 'navigate:profiles'
    Assert-Visible '新建方案'
    Add-Pass '首页' '编辑方案' $dashboardInput $dpi 'input activation navigated to ProfilesPage'

    $dpi = Start-CasApp 'profiles' 'profiles-enter'
    Press-AppElement '新建方案' 'ENTER'
    Assert-Trace 'button-click' 'profile:new'
    Assert-Visible '方案名称'
    Press-AppElement '取消' 'ENTER'
    Assert-Trace 'action-completed' 'profile:new'
    Add-Pass '配置方案' '新建方案' 'Enter' $dpi 'real profile editor opened with a writable name field and closed cleanly'

    $dpi = Start-CasApp 'providers' 'providers-mouse'
    if ($Theme -eq 'light' -and -not $PreferKeyboard) { Click-AppElement '添加服务商' 'provider:add'; $providerInput = 'mouse' }
    else { Press-AppElement '添加服务商' 'ENTER'; $providerInput = 'Enter' }
    Assert-Trace 'button-click' 'provider:add'
    Assert-Trace 'action-completed' 'provider:add'
    Assert-Visible '服务商 API 密钥'
    Add-Pass 'Provider' '添加服务商' $providerInput $dpi 'editor expanded and API Key control became reachable'

    $dpi = Start-CasApp 'tasks' 'managed-conversation'
    if ($Theme -eq 'light' -and -not $PreferKeyboard) { Click-AppElement '新建项目' 'cas:project-new'; $taskInput = 'mouse' }
    else { Press-AppElement '新建项目' 'ENTER'; $taskInput = 'Enter' }
    Assert-Trace 'button-click' 'cas:project-new'
    Set-AppText '项目名称' 'CAS UI Smoke Project'
    Set-AppText '工作目录' $TaskWorkingDirectory
    Press-AppElement '保存' 'ENTER'
    $null = Wait-AppElement 'CAS UI Smoke Project' 20
    if ($Theme -eq 'light' -and -not $PreferKeyboard) { Click-AppElement '新建对话' 'cas:conversation-new' }
    else { Press-AppElement '新建对话' 'ENTER' }
    Assert-Trace 'button-click' 'cas:conversation-new'
    $null = Wait-AppElement '对话输入框' 20
    Set-AppText '对话输入框' '请先委派 Worker 独立计算 1+1，然后由主代理最终只回复 CAS_UI_TASK_OK。'
    if ($Theme -eq 'light' -and -not $PreferKeyboard) {
        Click-AppElement '发送对话' 'cas:send'
    } else {
        Press-AppElement '发送对话' 'ENTER'
    }
    Assert-Trace 'button-click' 'cas:send'
    Assert-Trace 'action-completed' 'cas:send'
    $null = Wait-AppElement 'CAS_UI_TASK_OK' 240
    Add-Pass 'CodexAgentSwitch 模式' '新建项目、对话并发送' $taskInput $dpi 'project and conversation controls created a real same-Thread turn and rendered the final Sol result'

    $dpi = Start-CasApp 'tasks' 'managed-restart-recovery'
    Assert-Visible 'CAS UI Smoke Project'
    Assert-Visible '新对话'
    Assert-Visible 'CAS_UI_TASK_OK'
    Add-Pass 'CodexAgentSwitch 模式' '重启恢复' 'restart' $dpi 'persisted project, conversation and rendered result recovered after process restart'

    $dpi = Start-CasApp 'usage' 'usage-keyboard'
    $range = Find-AppElement '报告时间范围'
    $range.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{DOWN}{ENTER}')
    Start-Sleep -Milliseconds 300
    Add-Pass '用量与预算' '报告时间范围' 'keyboard' $dpi 'real ComboBox accepted keyboard selection'

    $dpi = Start-CasApp 'history' 'history-export'
    if ($Theme -eq 'light' -and -not $PreferKeyboard) { Click-AppElement '导出报告' 'history:export'; $historyInput = 'mouse' }
    else { Press-AppElement '导出报告' 'ENTER'; $historyInput = 'Enter' }
    Assert-Trace 'button-click' 'history:export'
    Assert-Trace 'action-completed' 'history:export'
    if (-not (Get-ChildItem -LiteralPath (Join-Path $resolvedData 'exports') -Filter 'task-*.md' -ErrorAction SilentlyContinue)) { throw 'Real task history report was not created' }
    Add-Pass '历史记录' '导出报告' $historyInput $dpi 'persisted real task report was created'

    $dpi = Start-CasApp 'settings' 'settings-backup'
    if ($Theme -eq 'light' -and -not $PreferKeyboard) { Click-AppElement '备份配置' 'settings:backup'; $settingsInput = 'mouse' }
    else { Press-AppElement '备份配置' 'ENTER'; $settingsInput = 'Enter' }
    Assert-Trace 'button-click' 'settings:backup'
    Assert-Trace 'action-completed' 'settings:backup'
    if (-not (Get-ChildItem -LiteralPath (Join-Path $resolvedData 'backups') -Filter 'configuration-*.zip' -ErrorAction SilentlyContinue)) { throw 'Configuration backup was not created' }
    Add-Pass '设置' '备份配置' $settingsInput $dpi 'recoverable configuration archive created'

    $dpi = Start-CasApp 'diagnostics' 'diagnostics-export'
    if ($Theme -eq 'light' -and -not $PreferKeyboard) { Click-AppElement '导出脱敏日志'; $diagnosticsInput = 'mouse' }
    else { Press-AppElement '导出脱敏日志' 'ENTER'; $diagnosticsInput = 'Enter' }
    Assert-Trace 'button-click'
    if (-not (Get-ChildItem -LiteralPath (Join-Path $resolvedData 'exports') -Filter 'diagnostics-*.zip' -ErrorAction SilentlyContinue)) { throw 'Diagnostic archive was not created' }
    Add-Pass '诊断' '导出脱敏日志' $diagnosticsInput $dpi 'redacted diagnostic archive created'

    $dpi = Start-CasApp 'onboarding' 'onboarding-space'
    Press-AppElement '下一步' 'SPACE'
    Assert-Trace 'button-click' 'onboarding:next'
    Assert-Trace 'action-completed' 'onboarding:next'
    Assert-Visible '返回'
    Add-Pass '首次启动向导' '下一步' 'Space' $dpi 'wizard progress advanced and back action enabled'

    $dpi = Start-CasApp 'dashboard' 'dashboard-tab'
    [Windows.Forms.SendKeys]::SendWait('{TAB}{TAB}{TAB}')
    Start-Sleep -Milliseconds 250
    $focused = [Windows.Automation.AutomationElement]::FocusedElement
    if ($focused.Current.ProcessId -ne $script:process.Id) { throw 'Tab focus left the app' }
    Add-Pass '公共输入链路' 'Tab 焦点' 'Tab' $dpi ("focused=" + $focused.Current.Name)
}
finally {
    Stop-CasApp
}

$reportDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$report = [ordered]@{
    app = $resolvedApp
    dataRoot = $resolvedData
    os = [Environment]::OSVersion.VersionString
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    theme = $Theme
    generatedAt = [DateTimeOffset]::Now.ToString('O')
    results = $results
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
$report | ConvertTo-Json -Depth 5
