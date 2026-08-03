[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $repo '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.nuget\http-cache'
$env:TEMP = Join-Path $repo '.tmp'
$env:TMP = $env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $env:TEMP 'bundle-extract'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH,$env:TEMP,$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null

dotnet restore (Join-Path $repo 'CodexAgentSwitch.sln') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
dotnet test (Join-Path $repo 'tests\CodexAgentSwitch.Tests\CodexAgentSwitch.Tests.csproj') -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
dotnet test (Join-Path $repo 'tests\CodexAgentSwitch.Bootstrapper.Tests\CodexAgentSwitch.Bootstrapper.Tests.csproj') -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper tests failed.' }
dotnet build (Join-Path $repo 'CodexAgentSwitch.sln') -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
