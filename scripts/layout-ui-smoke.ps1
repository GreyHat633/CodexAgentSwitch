[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$DataRoot,
    [ValidateSet('light','dark')][string]$Theme = 'light',
    [ValidateRange(1024,1920)][int]$WindowWidth = 1024,
    [switch]$LongText,
    [int]$ExpectedDpi = 0,
    [string]$ReportPath = (Join-Path $DataRoot "layout-ui-smoke-$Theme-$WindowWidth.json")
)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($AppPath)
$data = [IO.Path]::GetFullPath($DataRoot)
New-Item -ItemType Directory -Force -Path $data | Out-Null
$capture = Join-Path $data "environment-$Theme-$WindowWidth.png"
$layoutTrace = Join-Path $data "environment-layout-$Theme-$WindowWidth.json"
$temp = Join-Path $data 'temp'
New-Item -ItemType Directory -Force -Path $temp | Out-Null
$env:TEMP = $temp
$env:TMP = $temp
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $temp 'bundle-extract'
New-Item -ItemType Directory -Force -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null
$env:CAS_DATA_ROOT = $data
$env:CAS_CAPTURE_PAGE = 'dashboard'
$env:CAS_CAPTURE_PATH = $capture
$env:CAS_CAPTURE_EXIT = '0'
$env:CAS_THEME = $Theme
$env:CAS_WINDOW_WIDTH = [string]$WindowWidth
$env:CAS_WINDOW_HEIGHT = '720'
$env:CAS_UI_TEST_LONG_STATUS = $(if ($LongText) { '1' } else { '0' })
$env:CAS_LAYOUT_TRACE_PATH = $layoutTrace

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CasLayoutNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
}
'@

$process = $null
function Find-Element([string]$name) {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $fallback = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        }
        catch {
            Start-Sleep -Milliseconds 150
            continue
        }
        foreach ($element in $all) {
            try {
                if ($element.Current.ProcessId -eq $process.Id -and $element.Current.Name -eq $name) {
                    if (-not $element.Current.IsOffscreen) { return $element }
                    if ($null -eq $fallback) { $fallback = $element }
                }
            } catch { }
        }
        if ($fallback) {
            $pattern = $null
            if ($fallback.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
                ([Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
                Start-Sleep -Milliseconds 300
                return $fallback
            }
        }
        Start-Sleep -Milliseconds 150
    }
    if ($fallback) { return $fallback }
    throw "Automation element not found: $name"
}

function Save-WindowCapture([string]$path) {
    $rect = New-Object CasLayoutNative+Rect
    if (-not [CasLayoutNative]::GetWindowRect([IntPtr]$process.MainWindowHandle, [ref]$rect)) { throw 'GetWindowRect failed' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap $width,$height
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

try {
    $process = Start-Process -FilePath $app -PassThru
    for ($attempt = 0; $attempt -lt 60 -and $process.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw 'Main window was not created' }
    [CasLayoutNative]::SetForegroundWindow([IntPtr]$process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 1300
    $dpi = [int][CasLayoutNative]::GetDpiForWindow([IntPtr]$process.MainWindowHandle)
    if ($ExpectedDpi -gt 0 -and [Math]::Abs($dpi - $ExpectedDpi) -gt 2) { throw "Expected DPI $ExpectedDpi, actual $dpi" }

    for ($attempt = 0; $attempt -lt 30 -and -not (Test-Path -LiteralPath $layoutTrace); $attempt++) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path -LiteralPath $layoutTrace)) { throw 'In-app layout trace was not created' }
    $layout = Get-Content -Raw -Encoding UTF8 -LiteralPath $layoutTrace | ConvertFrom-Json
    $rows = foreach ($row in $layout.rows) {
        $icon = $row.icon
        $text = $row.text
        $iconRight = $icon.x + $icon.width
        $iconBottom = $icon.y + $icon.height
        $textRight = $text.x + $text.width
        $textBottom = $text.y + $text.height
        $overlapWidth = [Math]::Max(0, [Math]::Min($iconRight, $textRight) - [Math]::Max($icon.x, $text.x))
        $overlapHeight = [Math]::Max(0, [Math]::Min($iconBottom, $textBottom) - [Math]::Max($icon.y, $text.y))
        $overlapArea = $overlapWidth * $overlapHeight
        $gap = $text.x - $iconRight
        if ($icon.width -le 0 -or $text.width -le 0 -or $text.height -le 0) { throw "Empty layout bounds for $($row.name)" }
        if ($overlapArea -gt 0) { throw "Icon/text overlap for $($row.name): $overlapArea" }
        if ($gap -lt 8) { throw "Insufficient icon/text gap for $($row.name): $gap" }
        [pscustomobject]@{
            name = $row.name
            iconBoundsDip = @{ left=$icon.x; top=$icon.y; width=$icon.width; height=$icon.height }
            textBoundsDip = @{ left=$text.x; top=$text.y; width=$text.width; height=$text.height }
            gapDip = $gap
            overlapArea = $overlapArea
            textWrapped = ($text.height -gt 24)
        }
    }
    $null = Find-Element '凭据状态文字'
    Start-Sleep -Milliseconds 250
    Save-WindowCapture $capture
    if (-not (Test-Path -LiteralPath $capture) -or (Get-Item -LiteralPath $capture).Length -eq 0) { throw 'Dashboard screenshot was not captured' }
    $report = [ordered]@{
        app = $app
        os = [Environment]::OSVersion.VersionString
        theme = $Theme
        requestedWidthDip = $WindowWidth
        actualDpi = $dpi
        actualScale = $dpi / 96.0
        xamlRasterizationScale = $layout.rasterizationScale
        longTextEnabled = [bool]$LongText
        screenshot = $capture
        rows = @($rows)
        result = 'passed'
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    $report | ConvertTo-Json -Depth 6
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    Remove-Item Env:CAS_UI_TEST_LONG_STATUS -ErrorAction SilentlyContinue
    Remove-Item Env:CAS_CAPTURE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:CAS_CAPTURE_EXIT -ErrorAction SilentlyContinue
    Remove-Item Env:CAS_LAYOUT_TRACE_PATH -ErrorAction SilentlyContinue
}

