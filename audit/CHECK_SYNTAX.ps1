param(
    [Parameter(Mandatory=$true)]
    [string]$Target
)

$ErrorActionPreference = 'Stop'

$tokens = $null
$errors = $null

[System.Management.Automation.Language.Parser]::ParseFile(
    $Target,
    [ref]$tokens,
    [ref]$errors
) | Out-Null

if ($errors.Count -gt 0) {
    Write-Host ''
    Write-Host 'PowerShell syntax check failed:'
    foreach ($e in $errors) {
        Write-Host ('- ' + $e.Message)
        if ($e.Extent -and $e.Extent.Text) {
            Write-Host ('  Near: ' + $e.Extent.Text)
        }
    }
    exit 1
}

Write-Host 'PowerShell syntax check passed.'
exit 0
