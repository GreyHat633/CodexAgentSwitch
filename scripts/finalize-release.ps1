[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\release'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $Version))
if (-not $releaseRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release path escaped the repository artifacts directory.'
}

function Copy-DirectoryContents([string]$sourceDirectory, [string]$destinationDirectory) {
    $source = [IO.Path]::GetFullPath($sourceDirectory)
    $destination = [IO.Path]::GetFullPath($destinationDirectory)
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing staged directory: $source"
    }

    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
    }
}

$portableZip = Join-Path $releaseRoot 'CodexAgentSwitch-win10-x64.zip'
$portableHash = "$portableZip.sha256"
$compact = Join-Path $releaseRoot 'compact-runtime'
$setupBundle = Join-Path $releaseRoot 'setup-bundle'
$runtimeSupport = Join-Path $setupBundle 'RuntimeSupport'
$runtimeInstaller = Join-Path $compact 'RuntimeInstaller\WindowsAppRuntimeInstall-x64.exe'
$setupZip = Join-Path $releaseRoot 'CodexAgentSwitch-Setup-Bundle-win10-x64.zip'

foreach ($path in @($portableZip, $portableHash, $compact, $setupBundle, $runtimeInstaller)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required staged release item is missing: $path"
    }
}

Copy-DirectoryContents $compact $runtimeSupport
Copy-Item -LiteralPath (Join-Path $repo 'docs\runtime-deployment.md') -Destination $runtimeSupport -Force
if (-not (Test-Path -LiteralPath (Join-Path $runtimeSupport 'RuntimeInstaller\WindowsAppRuntimeInstall-x64.exe'))) {
    throw 'Runtime support staging verification failed.'
}

if (Test-Path -LiteralPath $setupZip) {
    Remove-Item -LiteralPath $setupZip -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($setupBundle, $setupZip, [IO.Compression.CompressionLevel]::Fastest, $false)
if (-not (Test-Path -LiteralPath $setupZip) -or (Get-Item -LiteralPath $setupZip).Length -lt 1MB) {
    throw 'Setup bundle archive was not created correctly.'
}

$files = Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'release-manifest.json') } |
    ForEach-Object {
        [pscustomobject]@{
            name = $_.Name
            size = $_.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    }
$checksumFile = 'SHA256SUMS.txt'
($files | ForEach-Object { "$($_.sha256)  $($_.name)" }) |
    Set-Content -LiteralPath (Join-Path $releaseRoot $checksumFile) -Encoding ASCII

$manifest = [ordered]@{
    version = $Version
    target = 'Windows 10 22H2 x64 primary; Windows 11 compatible'
    generatedAt = [DateTimeOffset]::Now.ToString('O')
    runtimeInstallerBundled = $true
    runtimeInstallerSource = 'https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe'
    checksumFile = $checksumFile
    branding = [ordered]@{
        source = 'Branding/AppIcon.svg'
        ico = 'Branding/AppIcon.ico'
        pngSizes = @(16,20,24,32,40,44,48,64,96,128,150,256,310,512)
        entryPoints = @('app-window','app-executable','portable','setup','runtime-bootstrapper','start-menu-shortcut')
    }
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding UTF8

Get-ChildItem -LiteralPath $releaseRoot -File | Select-Object Name, Length
