[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppExe,
    [Parameter(Mandatory)][string]$SetupExe,
    [Parameter(Mandatory)][string]$BootstrapperExe,
    [Parameter(Mandatory)][string]$IcoPath,
    [string]$ExpectedPng,
    [string]$ShortcutPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$ReportPath = (Join-Path $EvidenceDirectory 'icon-smoke.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$ico = [IO.Path]::GetFullPath($IcoPath)
$expectedPngPath = if ([string]::IsNullOrWhiteSpace($ExpectedPng)) {
    Join-Path (Split-Path -Parent $ico) 'png\AppIcon-32.png'
} else { [IO.Path]::GetFullPath($ExpectedPng) }
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

function Get-IconEntries([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) { throw 'Invalid ICO header' }
    $count = [BitConverter]::ToUInt16($bytes, 4)
    for ($index = 0; $index -lt $count; $index++) {
        $offset = 6 + (16 * $index)
        $width = if ($bytes[$offset] -eq 0) { 256 } else { [int]$bytes[$offset] }
        $height = if ($bytes[$offset + 1] -eq 0) { 256 } else { [int]$bytes[$offset + 1] }
        [pscustomobject]@{ width=$width; height=$height; bits=[BitConverter]::ToUInt16($bytes, $offset + 6) }
    }
}

function Get-MeanPixelDifference([Drawing.Bitmap]$expected, [Drawing.Bitmap]$actual) {
    $sum = 0L
    for ($y = 0; $y -lt 32; $y++) {
        for ($x = 0; $x -lt 32; $x++) {
            $a = $expected.GetPixel($x, $y)
            $b = $actual.GetPixel($x, $y)
            $sum += [Math]::Abs([int]$a.A - [int]$b.A)
            $sum += [Math]::Abs([int]$a.R - [int]$b.R)
            $sum += [Math]::Abs([int]$a.G - [int]$b.G)
            $sum += [Math]::Abs([int]$a.B - [int]$b.B)
        }
    }
    return $sum / (32.0 * 32.0 * 4.0)
}

$expectedSizes = @(16,20,24,32,40,48,64,128,256)
$entries = @(Get-IconEntries $ico)
foreach ($size in $expectedSizes) {
    if (-not ($entries | Where-Object { $_.width -eq $size -and $_.height -eq $size })) { throw "ICO entry missing: $size" }
}

$sourceBitmap = [Drawing.Bitmap]::new($expectedPngPath)
try {
    $sourceBitmap.Save((Join-Path $evidence 'expected-32.png'), [Drawing.Imaging.ImageFormat]::Png)
    $executables = [ordered]@{ app=$AppExe; setup=$SetupExe; bootstrapper=$BootstrapperExe }
    $results = foreach ($name in $executables.Keys) {
        $exe = [IO.Path]::GetFullPath($executables[$name])
        if (-not (Test-Path -LiteralPath $exe)) { throw "Executable missing: $exe" }
        $extracted = [Drawing.Icon]::ExtractAssociatedIcon($exe)
        if ($null -eq $extracted) { throw "Embedded icon missing: $exe" }
        try {
            $bitmap = $extracted.ToBitmap()
            try {
                $normalized = [Drawing.Bitmap]::new($bitmap, 32, 32)
                try {
                    $png = Join-Path $evidence ("{0}-embedded-32.png" -f $name)
                    $normalized.Save($png, [Drawing.Imaging.ImageFormat]::Png)
                    $difference = Get-MeanPixelDifference $sourceBitmap $normalized
                    if ($difference -gt 16) { throw "Embedded icon differs from source for $name (mean difference $difference)" }
                    [pscustomobject]@{ entry=$name; executable=$exe; extractedPng=$png; meanPixelDifference=$difference; passed=$true }
                }
                finally { $normalized.Dispose() }
            }
            finally { $bitmap.Dispose() }
        }
        finally { $extracted.Dispose() }
    }
}
finally {
    $sourceBitmap.Dispose()
}

$shortcut = $null
if (-not [string]::IsNullOrWhiteSpace($ShortcutPath)) {
    $shortcutFile = [IO.Path]::GetFullPath($ShortcutPath)
    if (-not (Test-Path -LiteralPath $shortcutFile)) { throw "Shortcut missing: $shortcutFile" }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($shortcutFile)
        $shortcut = [ordered]@{ path=$shortcutFile; target=$link.TargetPath; iconLocation=$link.IconLocation }
        if (-not (Test-Path -LiteralPath $link.TargetPath)) { throw 'Shortcut target does not exist' }
        if ($link.IconLocation -notmatch 'AppIcon\.ico|CodexAgentSwitch\.App\.exe') { throw "Unexpected shortcut icon: $($link.IconLocation)" }
    }
    finally {
        if ($shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    }
}

$report = [ordered]@{
    os = [Environment]::OSVersion.VersionString
    sourceIco = $ico
    expectedPng = $expectedPngPath
    icoEntries = $entries
    executables = @($results)
    shortcut = $shortcut
    result = 'passed'
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
$report | ConvertTo-Json -Depth 6
