[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.2',
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
dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.App\CodexAgentSwitch.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:EnableMsixTooling=true -p:IncludeAllContentForSelfExtract=true -p:Version=$Version -o $portable --nologo
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
$portableZip = Join-Path $resolvedRelease 'CodexAgentSwitch-win10-x64.zip'
New-ZipArchive $portable $portableZip
$portableHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $portableZip).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($portableZip + '.sha256') -Value "$portableHash  $([IO.Path]::GetFileName($portableZip))" -Encoding ASCII

$setupPublish = Join-Path $resolvedRelease 'setup-publish'
dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.Setup\CodexAgentSwitch.Setup.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $setupPublish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }
$setupBundle = Join-Path $resolvedRelease 'setup-bundle'
New-Item -ItemType Directory -Force -Path $setupBundle | Out-Null
Copy-Item -LiteralPath (Join-Path $setupPublish 'CodexAgentSwitch.Setup.exe') -Destination $setupBundle
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
    dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.App\CodexAgentSwitch.App.csproj') -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=false -p:Version=$Version -o $compact --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Compact publish failed.' }
    $appPri = Join-Path $repo 'src\CodexAgentSwitch.App\bin\Release\net8.0-windows10.0.22621.0\win-x64\CodexAgentSwitch.App.pri'
    if (-not (Test-Path -LiteralPath $appPri)) { throw 'Compact publish did not generate the application PRI resource index.' }
    Copy-Item -LiteralPath $appPri -Destination $compact -Force
    $bootstrapPublish = Join-Path $resolvedRelease 'bootstrap-publish'
    dotnet publish (Join-Path $repo 'src\CodexAgentSwitch.Bootstrapper\CodexAgentSwitch.Bootstrapper.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $bootstrapPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper publish failed.' }
    Copy-Item -LiteralPath (Join-Path $bootstrapPublish 'CodexAgentSwitch.Bootstrapper.exe') -Destination $compact
    $runtimeDirectory = Join-Path $compact 'RuntimeInstaller'
    New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
    $runtimeInstaller = Join-Path $runtimeDirectory 'WindowsAppRuntimeInstall-x64.exe'
    Save-WindowsAppRuntimeInstaller $runtimeInstaller
    $compactZip = Join-Path $resolvedRelease 'CodexAgentSwitch-compact-runtime-win10-x64.zip'
    New-ZipArchive $compact $compactZip
    $runtimeSupport = Join-Path $setupBundle 'RuntimeSupport'
    New-Item -ItemType Directory -Force -Path $runtimeSupport | Out-Null
    Copy-Item -LiteralPath (Join-Path $bootstrapPublish 'CodexAgentSwitch.Bootstrapper.exe') -Destination $runtimeSupport
    Copy-Item -LiteralPath $runtimeDirectory -Destination $runtimeSupport -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'docs\runtime-deployment.md') -Destination $runtimeSupport
}

$setupZip = Join-Path $resolvedRelease 'CodexAgentSwitch-Setup-Bundle-win10-x64.zip'
New-ZipArchive $setupBundle $setupZip

$files = Get-ChildItem -LiteralPath $resolvedRelease -File | ForEach-Object {
    [pscustomobject]@{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{
    version = $Version
    target = 'Windows 10 22H2 x64 primary; Windows 11 compatible'
    generatedAt = [DateTimeOffset]::Now.ToString('O')
    runtimeInstallerBundled = [bool]$IncludeRuntimeInstaller
    runtimeInstallerSource = if ($IncludeRuntimeInstaller) { 'https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe' } else { $null }
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
