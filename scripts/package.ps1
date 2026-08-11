[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version = '0.2.4.1',
    [switch]$IncludeRuntimeInstaller
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repo "artifacts\release\$Version"
$resolvedArtifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\release'))
$resolvedRelease = [IO.Path]::GetFullPath($releaseRoot)
if (-not $resolvedRelease.StartsWith($resolvedArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release path escaped the repository artifacts directory.'
}
if (Test-Path -LiteralPath $resolvedRelease) { Remove-Item -LiteralPath $resolvedRelease -Recurse -Force }
New-Item -ItemType Directory -Force -Path $resolvedRelease | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.nuget\http-cache'
$env:TEMP = Join-Path $repo '.tmp'
$env:TMP = $env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $env:TEMP 'bundle-extract'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH,$env:TEMP,$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-ZipArchive([string]$sourceDirectory, [string]$destinationPath) {
    if (Test-Path -LiteralPath $destinationPath) { Remove-Item -LiteralPath $destinationPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $sourceDirectory,
        $destinationPath,
        [IO.Compression.CompressionLevel]::Fastest,
        $false)
}

function Assert-MultiFilePublish([string]$publishDirectory, [string]$assemblyName) {
    foreach ($fileName in @("$assemblyName.exe", "$assemblyName.dll", "$assemblyName.deps.json", "$assemblyName.runtimeconfig.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $fileName))) {
            throw "Multi-file publish is incomplete: $fileName is missing from $publishDirectory."
        }
    }
}

function Copy-PublishContents([string]$sourceDirectory, [string]$destinationDirectory) {
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $sourceRoot = [IO.Path]::GetFullPath($sourceDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse) {
        $relativePath = $item.FullName.Substring($sourceRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
        $destination = Join-Path $destinationDirectory $relativePath
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            continue
        }
        if (Test-Path -LiteralPath $destination) {
            $existing = Get-Item -LiteralPath $destination
            if ($existing.Length -ne $item.Length -or
                (Get-FileHash -Algorithm SHA256 -LiteralPath $existing.FullName).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash) {
                throw "Publish output collision has different content: $($item.Name)"
            }
            continue
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $item.FullName -Destination $destination
    }
}

function Test-MicrosoftSignedInstaller([string]$path) {
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 1MB) { return $false }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    return $signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'Microsoft'
}

function Save-WindowsAppRuntimeInstaller([string]$destinationPath) {
    $priorInstaller = Get-ChildItem -LiteralPath $resolvedArtifacts -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $resolvedRelease } |
        ForEach-Object { Join-Path $_.FullName 'compact-runtime\RuntimeInstaller\WindowsAppRuntimeInstall-x64.exe' } |
        Where-Object { Test-MicrosoftSignedInstaller $_ } |
        Select-Object -First 1

    if ($priorInstaller) {
        Copy-Item -LiteralPath $priorInstaller -Destination $destinationPath -Force
        return
    }

    $uri = 'https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe'
    $temporaryPath = "$destinationPath.download"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $temporaryPath
            if (-not (Test-MicrosoftSignedInstaller $temporaryPath)) {
                throw 'Downloaded Runtime installer is incomplete or does not have a valid Microsoft signature.'
            }
            Move-Item -LiteralPath $temporaryPath -Destination $destinationPath -Force
            return
        }
        catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release

$portable = Join-Path $resolvedRelease 'portable'
# WinUI self-contained deployment currently fails during XAML initialization on the
# Windows 10 22H2 primary acceptance host. Ship the stable framework-dependent
# app instead; the same release includes the offline runtime bootstrapper below.
dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.App\CodexAgentSwitch.App.csproj') -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -p:PublishSingleFile=false -p:EnableMsixTooling=true -p:Version=$Version -o $portable --nologo
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
Assert-MultiFilePublish $portable 'CodexAgentSwitch.App'
$toolHostPublish = Join-Path $resolvedRelease 'tool-host-publish'
dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.ToolHost\CodexAgentSwitch.ToolHost.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:Version=$Version -o $toolHostPublish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Scheduler Tool Host publish failed.' }
Assert-MultiFilePublish $toolHostPublish 'CodexAgentSwitch.ToolHost'
Copy-PublishContents $toolHostPublish (Join-Path $portable 'ToolHost')
$brokerPublish = Join-Path $resolvedRelease 'credential-broker-publish'
dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.CredentialBroker\CodexAgentSwitch.CredentialBroker.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:Version=$Version -o $brokerPublish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Credential broker publish failed.' }
Assert-MultiFilePublish $brokerPublish 'CodexAgentSwitch.CredentialBroker'
Copy-PublishContents $brokerPublish (Join-Path $portable 'NativeCredentialBroker')
$portableZip = Join-Path $resolvedRelease 'CodexAgentSwitch-win10-x64.zip'
New-ZipArchive $portable $portableZip
$portableHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $portableZip).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($portableZip + '.sha256') -Value "$portableHash  $([IO.Path]::GetFileName($portableZip))" -Encoding ASCII

$setupPublish = Join-Path $resolvedRelease 'setup-publish'
dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.Setup\CodexAgentSwitch.Setup.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $setupPublish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }
Assert-MultiFilePublish $setupPublish 'CodexAgentSwitch.Setup'
$setupBundle = Join-Path $resolvedRelease 'setup-bundle'
Copy-PublishContents $setupPublish $setupBundle
Copy-Item -LiteralPath $portableZip,($portableZip + '.sha256') -Destination $setupBundle
Copy-Item -LiteralPath (Join-Path $repo 'docs\install-and-rollback.md') -Destination $setupBundle
$brandingBundle = Join-Path $setupBundle 'Branding'
New-Item -ItemType Directory -Force -Path $brandingBundle | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'assets\branding\AppIcon.svg'),(Join-Path $repo 'assets\branding\AppIcon.ico') -Destination $brandingBundle
Copy-Item -LiteralPath (Join-Path $repo 'assets\branding\png') -Destination $brandingBundle -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'docs\branding\app-icon-design.md') -Destination $brandingBundle
$compactZip = $null
if ($IncludeRuntimeInstaller) {
    $compact = Join-Path $resolvedRelease 'compact-runtime'
    $compactApp = Join-Path $compact 'App'
    dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.App\CodexAgentSwitch.App.csproj') -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=false -p:PublishSingleFile=false -p:Version=$Version -o $compactApp --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Compact publish failed.' }
    Assert-MultiFilePublish $compactApp 'CodexAgentSwitch.App'
    Copy-PublishContents $toolHostPublish (Join-Path $compactApp 'ToolHost')
    Copy-PublishContents $brokerPublish (Join-Path $compactApp 'NativeCredentialBroker')
    $appPri = Join-Path $repo 'src\CodexAgentSwitch.App\bin\Release\net8.0-windows10.0.22621.0\win-x64\CodexAgentSwitch.App.pri'
    if (-not (Test-Path -LiteralPath $appPri)) { throw 'Compact publish did not generate the application PRI resource index.' }
    Copy-Item -LiteralPath $appPri -Destination $compactApp -Force
    $bootstrapPublish = Join-Path $resolvedRelease 'bootstrap-publish'
    dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.Bootstrapper\CodexAgentSwitch.Bootstrapper.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $bootstrapPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper publish failed.' }
    Assert-MultiFilePublish $bootstrapPublish 'CodexAgentSwitch.Bootstrapper'
    Copy-PublishContents $bootstrapPublish $compact
    $runtimeDirectory = Join-Path $compact 'RuntimeInstaller'
    New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
    $runtimeInstaller = Join-Path $runtimeDirectory 'WindowsAppRuntimeInstall-x64.exe'
    Save-WindowsAppRuntimeInstaller $runtimeInstaller
    $compactZip = Join-Path $resolvedRelease 'CodexAgentSwitch-compact-runtime-win10-x64.zip'
    New-ZipArchive $compact $compactZip
    $runtimeSupport = Join-Path $setupBundle 'RuntimeSupport'
    Copy-PublishContents $compact $runtimeSupport
    Copy-Item -LiteralPath (Join-Path $repo 'docs\runtime-deployment.md') -Destination $runtimeSupport
}

$setupZip = Join-Path $resolvedRelease 'CodexAgentSwitch-Setup-Bundle-win10-x64.zip'
New-ZipArchive $setupBundle $setupZip

$files = Get-ChildItem -LiteralPath $resolvedRelease -File | ForEach-Object {
    [pscustomobject]@{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
}
$checksumFile = 'SHA256SUMS.txt'
($files | ForEach-Object { "$($_.sha256)  $($_.name)" }) |
    Set-Content -LiteralPath (Join-Path $resolvedRelease $checksumFile) -Encoding ASCII
$manifest = [ordered]@{
    version = $Version
    target = 'Windows 10 22H2 x64 primary; Windows 11 compatible'
    generatedAt = [DateTimeOffset]::Now.ToString('O')
    runtimeInstallerBundled = [bool]$IncludeRuntimeInstaller
    runtimeInstallerSource = if ($IncludeRuntimeInstaller) { 'https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe' } else { $null }
    checksumFile = $checksumFile
    branding = [ordered]@{
        source = 'Branding/AppIcon.svg'
        ico = 'Branding/AppIcon.ico'
        pngSizes = @(16,20,24,32,40,44,48,64,96,128,150,256,310,512)
        entryPoints = @('app-window','app-executable','portable','setup','runtime-bootstrapper','start-menu-shortcut')
    }
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resolvedRelease 'release-manifest.json') -Encoding UTF8
Get-ChildItem -LiteralPath $resolvedRelease -File | Select-Object Name,Length
