[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._/-]+$')][string]$ReferenceId)

$target = "CodexAgentSwitch/$ReferenceId"
if ($PSCmdlet.ShouldProcess($target, 'Delete Windows generic credential')) {
    & "$env:SystemRoot\System32\cmdkey.exe" "/delete:$target"
    if ($LASTEXITCODE -ne 0) { throw "Credential deletion failed for $target" }
}
