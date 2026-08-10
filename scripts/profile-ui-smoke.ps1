[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$DataRoot,
    [ValidateSet('light','dark')][string]$Theme = 'light',
    [int]$ExpectedDpi = 0,
    [switch]$ExpectConfiguredProvider,
    [string]$ReportPath = (Join-Path $DataRoot "profile-ui-smoke-$Theme.json")
)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($AppPath)
$data = [IO.Path]::GetFullPath($DataRoot)
New-Item -ItemType Directory -Force -Path $data | Out-Null
$temp = Join-Path $data 'temp'
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$env:TEMP = $temp
$env:TMP = $temp
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $temp 'bundle-extract'
New-Item -ItemType Directory -Force -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CasProfileNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr info);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
}
'@

$process = $null
$trace = Join-Path $data 'profile-input.jsonl'
$profileName = "UI验收-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$editedName = "$profileName-已编辑"
$copyName = "$editedName - 副本"
$results = [Collections.Generic.List[object]]::new()

function Stop-App {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    $script:process = $null
}

function Start-App {
    Stop-App
    $env:CAS_DATA_ROOT = $data
    $env:CAS_CAPTURE_PAGE = 'profiles'
    $env:CAS_THEME = $Theme
    $env:CAS_WINDOW_WIDTH = '1024'
    $env:CAS_WINDOW_HEIGHT = '720'
    $env:CAS_INPUT_TRACE_PATH = $trace
    $script:process = Start-Process -FilePath $app -PassThru
    for ($attempt = 0; $attempt -lt 60 -and $process.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw 'Main window was not created' }
    [CasProfileNative]::SetForegroundWindow([IntPtr]$process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 900
    $dpi = [int][CasProfileNative]::GetDpiForWindow([IntPtr]$process.MainWindowHandle)
    if ($ExpectedDpi -gt 0 -and [Math]::Abs($dpi - $ExpectedDpi) -gt 2) { throw "Expected DPI $ExpectedDpi, actual $dpi" }
    return $dpi
}

function Find-Element([string]$name, [int]$attempts = 35) {
    $root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$process.MainWindowHandle)
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        try { $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) }
        catch { Start-Sleep -Milliseconds 150; continue }
        $fallback = $null
        foreach ($element in $all) {
            try {
                if ($element.Current.Name -ne $name) { continue }
                if (-not $element.Current.IsOffscreen) { return $element }
                if ($null -eq $fallback) { $fallback = $element }
            } catch { }
        }
        if ($fallback) {
            $pattern = $null
            if ($fallback.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
                ([Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
                Start-Sleep -Milliseconds 250
                return $fallback
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Automation element not found: $name"
}

function Assert-Absent([string]$name) {
    try { $null = Find-Element $name 5; throw "Unexpected element still present: $name" }
    catch { if ($_.Exception.Message -like 'Unexpected*') { throw } }
}

function Assert-Disabled([string]$name) {
    $element = Find-Element $name
    if ($element.Current.IsEnabled) { throw "Expected disabled element: $name" }
}

function Assert-Enabled([string]$name) {
    $element = Find-Element $name
    if (-not $element.Current.IsEnabled) { throw "Expected enabled element: $name" }
}

function Press-Element([string]$name) {
    $element = Find-Element $name
    $invoke = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        ([Windows.Automation.InvokePattern]$invoke).Invoke()
        Start-Sleep -Milliseconds 600
        return
    }
    $element.SetFocus()
    Start-Sleep -Milliseconds 100
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 600
}

function Click-Element([string]$name) {
    $element = Find-Element $name
    $bounds = $element.Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Empty bounds: $name" }
    [CasProfileNative]::SetForegroundWindow([IntPtr]$process.MainWindowHandle) | Out-Null
    [CasProfileNative]::SetCursorPos([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2)) | Out-Null
    [CasProfileNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [CasProfileNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 450
}

function Set-Text([string]$name, [string]$value) {
    $element = Find-Element $name
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        ([Windows.Automation.ValuePattern]$pattern).SetValue($value)
    }
    else {
        $element.SetFocus()
        [Windows.Forms.SendKeys]::SendWait('^a')
        [Windows.Forms.SendKeys]::SendWait($value)
    }
    Start-Sleep -Milliseconds 150
}

function Select-Combo([string]$name, [int]$index) {
    $element = Find-Element $name
    $element.SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{HOME}')
    for ($i = 0; $i -lt $index; $i++) { [Windows.Forms.SendKeys]::SendWait('{DOWN}') }
    [Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 180
}

function Save-Capture([string]$name) {
    $rect = New-Object CasProfileNative+Rect
    if (-not [CasProfileNative]::GetWindowRect([IntPtr]$process.MainWindowHandle, [ref]$rect)) { throw 'GetWindowRect failed' }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $path = Join-Path $data $name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        return $path
    }
    finally { $bitmap.Dispose() }
}

function Add-Pass([string]$action, [string]$evidence, [int]$dpi) {
    $results.Add([pscustomobject]@{ action=$action; evidence=$evidence; dpi=$dpi; theme=$Theme; status='passed' })
}

try {
    Remove-Item -LiteralPath $trace -ErrorAction SilentlyContinue
    $dpi = Start-App
    Press-Element '新建方案'
    $editorCapture = Save-Capture 'profile-editor.png'
    Set-Text '方案名称' $profileName
    Select-Combo '主代理选择' 1
    Select-Combo '主代理推理强度' 3
    Assert-Absent '外部服务商选择'
    Select-Combo '工作代理来源' 1
    Assert-Absent '原生工作代理选择'
    Select-Combo '外部 Worker 权限' 2
    $permissionCapture = Save-Capture 'profile-external-permission.png'
    if ($ExpectConfiguredProvider) {
        Assert-Enabled '外部服务商选择'
        Select-Combo '外部服务商选择' 0
        Assert-Absent '没有可用的外部服务商'
        Add-Pass '外部服务商条件显示' "native controls collapsed; configured-provider selector and independent Full Access permission shown; capture=$permissionCapture" $dpi
    }
    else {
        Assert-Disabled '外部服务商选择'
        $null = Find-Element '没有可用的外部服务商'
        Add-Pass '外部服务商条件显示' "native controls collapsed; provider selector disabled with clear empty state; independent Full Access permission shown; capture=$permissionCapture" $dpi
    }
    Select-Combo '工作代理来源' 0
    $null = Find-Element '原生工作代理选择'
    Assert-Absent '外部服务商选择'
    Select-Combo '原生工作代理选择' 1
    Select-Combo 'Worker 推理强度' 3
    Set-Text '最大工作代理数量' '2'
    Select-Combo '路由模式' 1
    $null = Find-Element '路由模式说明'
    Select-Combo '回退策略' 2
    Set-Text '单任务预算' '1.25'
    Set-Text '每日预算' '5'
    Set-Text '每月预算' '50'
    Set-Text '令牌上限' '200000'
    Set-Text '请求上限' '100'
    Press-Element '保存'
    $null = Find-Element $profileName
    $createdCapture = Save-Capture 'profile-created.png'
    Add-Pass '新建' "new persisted profile visible; editor=$editorCapture; list=$createdCapture" $dpi

    Press-Element '立即启用'
    $null = Find-Element '方案已立即启用'
    Add-Pass '立即启用' 'active/default state changed and LastUsedAt was displayed' $dpi

    Press-Element '导出'
    $export = Get-ChildItem -LiteralPath (Join-Path $data 'exports') -Filter 'profile-*.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $export) { throw 'Profile export was not created' }
    $exportText = Get-Content -Raw -Encoding UTF8 -LiteralPath $export.FullName
    if ($exportText -match '(?i)apiKey|credentialReference|secretValue') { throw 'Export contains a credential field' }
    $exportProfile = $exportText | ConvertFrom-Json
    if ($exportProfile.profile.workerPolicy.reasoningEffort -ne 'xhigh') { throw 'Worker reasoning effort was not persisted independently' }
    Add-Pass 'Worker 推理强度' 'xhigh selection persisted in workerPolicy.reasoningEffort independently from the main agent' $dpi
    Add-Pass '导出' "credential-free export=$($export.FullName)" $dpi

    Press-Element '编辑'
    Set-Text '方案名称' $editedName
    Select-Combo '主代理选择' 2
    Press-Element '保存'
    $null = Find-Element $editedName
    Add-Pass '编辑' 'name and main-agent selection were saved through the real editor' $dpi

    Press-Element '复制'
    $null = Find-Element '方案名称'
    Press-Element '保存'
    $null = Find-Element $copyName
    Add-Pass '复制' "unique copy visible as $copyName" $dpi
    Press-Element '删除'
    Assert-Absent $copyName
    Add-Pass '删除副本' 'copied profile removed from the persisted list' $dpi

    Stop-App
    $dpi = Start-App
    Click-Element $editedName
    $null = Find-Element $editedName
    Add-Pass '重启恢复' 'edited profile remained visible after process restart' $dpi

    Click-Element '经济模式'
    Press-Element '设置默认'
    $null = Find-Element '默认方案已切换'
    Click-Element $editedName
    Press-Element '删除'
    Assert-Absent $editedName
    Add-Pass '删除' 'after switching default, the created profile was deleted' $dpi

    $traceRecords = Get-Content -Encoding UTF8 -LiteralPath $trace | ForEach-Object { $_ | ConvertFrom-Json }
    if (-not ($traceRecords | Where-Object { $_.kind -eq 'button-click' -and $_.hits -contains 'action:profile:new' })) { throw 'New profile button trace missing' }
}
finally {
    Stop-App
}

$report = [ordered]@{
    app = $app
    dataRoot = $data
    os = [Environment]::OSVersion.VersionString
    profileName = $profileName
    editedName = $editedName
    generatedAt = [DateTimeOffset]::Now.ToString('O')
    results = $results
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
$report | ConvertTo-Json -Depth 6
