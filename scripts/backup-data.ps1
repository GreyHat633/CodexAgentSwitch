[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data'),
    [string]$BackupRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\backups')
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path -LiteralPath $source)) { throw "Data root does not exist: $source" }
New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
$destination = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) ("codex-agent-switch-data-{0}.zip" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
Compress-Archive -LiteralPath $source -DestinationPath $destination -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $destination
