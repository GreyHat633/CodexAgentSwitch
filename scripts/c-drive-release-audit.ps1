[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.10',
    [string]$ReleaseDirectory,
    [int]$StartupSeconds = 6
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $repo "artifacts\release\$Version"
}
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $release)) { throw "Release directory does not exist: $release" }

# The audit observes C: without using it. Build/test artifacts and runtime extraction remain on E:.
if ([IO.Path]::GetPathRoot($env:TEMP) -eq 'C:\') { throw "TEMP must be redirected off C: for this audit; detected $env:TEMP" }
if ([IO.Path]::GetPathRoot($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) -eq 'C:\') { throw "DOTNET_BUNDLE_EXTRACT_BASE_DIR must be redirected off C:; detected $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR" }

$auditRoot = Join-Path $repo ('.tmp\c-drive-release-audit-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$dataRoot = Join-Path $auditRoot 'data'
$startMenuRoot = Join-Path $auditRoot 'start-menu'
$installTarget = Join-Path $auditRoot 'installed-app'
New-Item -ItemType Directory -Force -Path $auditRoot,$dataRoot,$startMenuRoot | Out-Null

function Get-PhysicalSnapshot {
    $roots = @(
        (Join-Path $env:USERPROFILE '.nuget'),
        (Join-Path $env:USERPROFILE '.dotnet'),
        (Join-Path $env:TEMP '.net'),
        (Join-Path $env:TEMP 'VBCSCompiler'),
        (Join-Path $env:TEMP 'NuGetScratch')
    )
    $entries = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { $pending.Push([IO.Path]::GetFullPath($root)) }
    }
    while ($pending.Count -gt 0) {
        $path = $pending.Pop()
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $entries.Add("J|$($item.FullName)|$($item.Target -join ';')")
            continue
        }
        if (-not $item.PSIsContainer) {
            $entries.Add("F|$($item.FullName)|$($item.Length)|$($item.LastWriteTimeUtc.Ticks)")
            continue
        }
        foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) { $pending.Push($child.FullName) }
    }
    return @($entries | Sort-Object)
}

function Test-DotNetBundle([string]$path) {
    [byte[]]$marker = 0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6
    [byte[]]$bytes = [IO.File]::ReadAllBytes($path)
    for ($index = 0; $index -le $bytes.Length - $marker.Length; $index++) {
        $match = $true
        for ($offset = 0; $offset -lt $marker.Length; $offset++) {
            if ($bytes[$index + $offset] -ne $marker[$offset]) { $match = $false; break }
        }
        if ($match) {
            if ($index -lt 8) { return $true }
            return [BitConverter]::ToInt64($bytes, $index - 8) -ne 0
        }
    }
    return $false
}

function Assert-MultiFileEntryPoint([string]$directory, [string]$assemblyName) {
    $exe = Join-Path $directory "$assemblyName.exe"
    foreach ($path in @($exe,(Join-Path $directory "$assemblyName.dll"),(Join-Path $directory "$assemblyName.deps.json"),(Join-Path $directory "$assemblyName.runtimeconfig.json"))) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required sidecar is missing: $path" }
    }
    if (Test-DotNetBundle $exe) { throw "Self-extracting .NET bundle marker found: $exe" }
    return $exe
}

function Stop-ExactProcess([Diagnostics.Process]$process, [string]$expectedPath) {
    if ($process.HasExited) { return }
    $actual = (Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)").ExecutablePath
    if (-not [string]::Equals([IO.Path]::GetFullPath($actual), [IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to stop unexpected process $($process.Id): $actual"
    }
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit(10000) | Out-Null
}

function Test-GuiStartup([string]$executable, [string]$label) {
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds $StartupSeconds
    if ($process.HasExited -and $process.ExitCode -ne 0) { throw "$label exited with code $($process.ExitCode)." }
    Stop-ExactProcess $process $executable
    [pscustomobject]@{ entryPoint = $label; executable = $executable; started = $true }
}

$portable = Join-Path $release 'portable'
$setupBundle = Join-Path $release 'setup-bundle'
$compact = Join-Path $release 'compact-runtime'
$appExe = Assert-MultiFileEntryPoint $portable 'CodexAgentSwitch.App'
$setupExe = Assert-MultiFileEntryPoint $setupBundle 'CodexAgentSwitch.Setup'
$bootstrapExe = Assert-MultiFileEntryPoint $compact 'CodexAgentSwitch.Bootstrapper'
$null = Assert-MultiFileEntryPoint (Join-Path $compact 'App') 'CodexAgentSwitch.App'
$null = Assert-MultiFileEntryPoint (Join-Path $setupBundle 'RuntimeSupport') 'CodexAgentSwitch.Bootstrapper'
$null = Assert-MultiFileEntryPoint (Join-Path $setupBundle 'RuntimeSupport\App') 'CodexAgentSwitch.App'

$before = Get-PhysicalSnapshot
$priorDataRoot = $env:CAS_DATA_ROOT
$priorStartMenuRoot = $env:CAS_START_MENU_ROOT
try {
    $env:CAS_DATA_ROOT = $dataRoot
    $env:CAS_START_MENU_ROOT = $startMenuRoot
    $launches = @()
    $launches += Test-GuiStartup $appExe 'portable-app'

    $payload = Join-Path $release 'CodexAgentSwitch-win10-x64.zip'
    $setup = Start-Process -FilePath $setupExe -WorkingDirectory $setupBundle -ArgumentList @('--install','--payload',("`"$payload`""),'--target',("`"$installTarget`"")) -PassThru -Wait
    if ($setup.ExitCode -ne 0) { throw "Setup CLI exited with code $($setup.ExitCode)." }
    $installedExe = Assert-MultiFileEntryPoint $installTarget 'CodexAgentSwitch.App'
    $launches += Test-GuiStartup $installedExe 'installed-app'
    $launches += Test-GuiStartup $bootstrapExe 'runtime-bootstrapper'
}
finally {
    $env:CAS_DATA_ROOT = $priorDataRoot
    $env:CAS_START_MENU_ROOT = $priorStartMenuRoot
}
$after = Get-PhysicalSnapshot
$changes = @(Compare-Object -ReferenceObject $before -DifferenceObject $after)
$result = [ordered]@{
    version = $Version
    temp = $env:TEMP
    bundleExtractOverride = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
    monitoredPhysicalEntriesBefore = $before.Count
    monitoredPhysicalEntriesAfter = $after.Count
    physicalChanges = @($changes)
    launches = @($launches)
    passed = $changes.Count -eq 0
}
$resultPath = Join-Path $auditRoot 'result.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
if ($changes.Count -ne 0) { throw "Physical C-drive writes were detected. See $resultPath" }
Write-Output "C_DRIVE_RELEASE_AUDIT=PASSED"
Write-Output "RESULT=$resultPath"
