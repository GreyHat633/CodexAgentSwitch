param(
    [string]$BaseRef = '',
    [string]$FinalRef = '',
    [string]$ConfigPath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# =============================================================================
# CAS Universal Economic Audit
# Windows PowerShell 5.1 compatible
#
# Zero-touch normal mode:
#   - automatically chooses the newest two semantic-version Git tags
#   - automatically finds the matching Codex Sol/Luna sessions
#   - generates the four economic metrics for that version interval
#
# Optional override:
#   powershell -File .\CAS_Universal_Economic_Audit.ps1 `
#       -BaseRef v0.2.6.1 -FinalRef v0.2.6.2
#
# Read-only with respect to:
#   - repository source/config
#   - Git history/index
#   - Codex session JSONL
#
# The only writes are report files under OutputRoot.
# =============================================================================

function Expand-EnvPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    return [Environment]::ExpandEnvironmentVariables($Path)
}

function Read-SharedJsonl {
    param([string]$Path)

    $fs = New-Object System.IO.FileStream -ArgumentList @(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite
    )
    $sr = New-Object System.IO.StreamReader -ArgumentList @($fs)

    try {
        while (($line = $sr.ReadLine()) -ne $null) {
            Write-Output $line
        }
    }
    finally {
        $sr.Dispose()
        $fs.Dispose()
    }
}

function Convert-ToLocalDateTime {
    param($Value)
    if ($null -eq $Value) { return $null }
    try {
        return ([datetimeoffset]$Value).LocalDateTime
    }
    catch {
        return $null
    }
}

function Normalize-RepoPath {
    param(
        [string]$Path,
        [string]$Repo
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $p = $Path.Trim().Trim('"').Trim("'")
    $p = $p -replace '\\\\', '\'
    $p = $p -replace '/', '\'
    $repoNorm = ($Repo -replace '/', '\').TrimEnd('\')

    if ($p.StartsWith($repoNorm, [System.StringComparison]::OrdinalIgnoreCase)) {
        $p = $p.Substring($repoNorm.Length).TrimStart('\')
    }

    $p = $p.TrimStart('.', '\')
    return ($p -replace '\\+', '/')
}

function Test-DevelopmentFile {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $p = $Path -replace '\\', '/'
    return (
        $p.StartsWith('src/', [System.StringComparison]::OrdinalIgnoreCase) -or
        $p.StartsWith('tests/', [System.StringComparison]::OrdinalIgnoreCase)
    )
}

function Test-CoreFile {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $p = $Path -replace '\\', '/'
    return $p.StartsWith('src/', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-PathsFromMutationLine {
    param(
        [string]$Line,
        [string]$Repo
    )

    $results = New-Object System.Collections.Generic.List[string]

    # apply_patch style.
    foreach ($m in [regex]::Matches(
        $Line,
        '\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*([^\\r\\n"]+)'
    )) {
        $results.Add($m.Groups[1].Value)
    }

    # Common JSON path/file fields.
    foreach ($m in [regex]::Matches(
        $Line,
        '"(?:path|file|filePath|filepath)"\s*:\s*"([^"]+)"'
    )) {
        $results.Add(($m.Groups[1].Value -replace '\\\\', '\'))
    }

    # Repo-relative development paths embedded in commands/patches.
    foreach ($m in [regex]::Matches(
        $Line,
        '(?i)(?<![A-Za-z0-9_.-])((?:src|tests)[/\\][A-Za-z0-9_./\\() +\-]+\.(?:cs|xaml|csproj|props|targets|json|md|ps1|toml|yml|yaml))'
    )) {
        $results.Add($m.Groups[1].Value)
    }

    $normalized = @()
    foreach ($item in $results) {
        $n = Normalize-RepoPath -Path $item -Repo $Repo
        if ($n -and (Test-DevelopmentFile -Path $n)) {
            $normalized += $n
        }
    }

    return @($normalized | Sort-Object -Unique)
}

function Get-GitNumStat {
    param(
        [string]$Repo,
        [string]$FromRef,
        [string]$ToRef
    )

    $rows = @()
    $lines = & git -C $Repo diff --numstat $FromRef $ToRef -- 2>$null

    foreach ($line in $lines) {
        if ($line -match '^(\d+|-)\s+(\d+|-)\s+(.+)$') {
            $add = if ($matches[1] -eq '-') { 0 } else { [int]$matches[1] }
            $del = if ($matches[2] -eq '-') { 0 } else { [int]$matches[2] }
            $path = Normalize-RepoPath -Path $matches[3] -Repo $Repo

            $rows += [pscustomobject]@{
                Path = $path
                Added = $add
                Deleted = $del
                Changed = $add + $del
                IsDevelopment = Test-DevelopmentFile -Path $path
                IsCore = Test-CoreFile -Path $path
            }
        }
    }

    return @($rows)
}

function Measure-Percent {
    param(
        [double]$Part,
        [double]$Whole
    )

    if ($Whole -le 0) { return $null }
    return [math]::Round(($Part / $Whole) * 100.0, 1)
}

function Get-RateInfo {
    param(
        [string]$Model,
        $RateTable
    )

    if ([string]::IsNullOrWhiteSpace($Model)) { return $null }

    $property = $RateTable.PSObject.Properties |
        Where-Object { $_.Name -eq $Model } |
        Select-Object -First 1

    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-NormalizedCost {
    param(
        [string]$Model,
        [long]$Uncached,
        [long]$Cached,
        [long]$Output,
        $RateTable
    )

    $rate = Get-RateInfo -Model $Model -RateTable $RateTable
    if ($null -eq $rate) { return $null }

    return (
        ($Uncached * [double]$rate.InputPer1M / 1000000.0) +
        ($Cached * [double]$rate.CachedInputPer1M / 1000000.0) +
        ($Output * [double]$rate.OutputPer1M / 1000000.0)
    )
}

function Get-Role {
    param(
        [string]$Model,
        $RateTable
    )

    $rate = Get-RateInfo -Model $Model -RateTable $RateTable
    if ($null -ne $rate -and $rate.Role) {
        return [string]$rate.Role
    }

    # Role fallback is useful for mutation/coverage evidence even if pricing
    # has not yet been configured for a newer model.
    if ($Model -match '(?i)-sol$') { return 'Main' }
    if ($Model -match '(?i)-luna$') { return 'Worker' }
    return 'Unknown'
}

function Test-IntervalsOverlap {
    param(
        [datetime]$AStart,
        [datetime]$AEnd,
        [datetime]$BStart,
        [datetime]$BEnd,
        [int]$BufferMinutes = 0
    )

    $bs = $BStart.AddMinutes(-1 * $BufferMinutes)
    $be = $BEnd.AddMinutes($BufferMinutes)
    return ($AStart -le $be -and $AEnd -ge $bs)
}

function Get-SafeFileName {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 'unknown' }
    return ($Text -replace '[^A-Za-z0-9._-]', '_')
}

# -----------------------------------------------------------------------------
# Load config.
# -----------------------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot 'audit.config.json'
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Config not found: $ConfigPath"
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

$Repo = Expand-EnvPath ([string]$config.Repo)
$SessionsRoot = Expand-EnvPath ([string]$config.SessionsRoot)
$OutputRoot = Expand-EnvPath ([string]$config.OutputRoot)
if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot $OutputRoot
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$RateTable = $config.ModelRates

$preHitBuffer = 10
$postCommitBuffer = 20
$workerOverlapBuffer = 10
if ($config.Audit) {
    if ($null -ne $config.Audit.PreVersionHitBufferMinutes) {
        $preHitBuffer = [int]$config.Audit.PreVersionHitBufferMinutes
    }
    if ($null -ne $config.Audit.PostFinalCommitBufferMinutes) {
        $postCommitBuffer = [int]$config.Audit.PostFinalCommitBufferMinutes
    }
    if ($null -ne $config.Audit.WorkerOverlapBufferMinutes) {
        $workerOverlapBuffer = [int]$config.Audit.WorkerOverlapBufferMinutes
    }
}

if (-not (Test-Path -LiteralPath $Repo)) {
    throw "Repo not found: $Repo"
}
if (-not (Test-Path -LiteralPath $SessionsRoot)) {
    throw "Codex sessions root not found: $SessionsRoot"
}

# -----------------------------------------------------------------------------
# Auto-select release refs.
# -----------------------------------------------------------------------------

$autoRefs = $false

if ([string]::IsNullOrWhiteSpace($FinalRef) -and [string]::IsNullOrWhiteSpace($BaseRef)) {
    $tags = @(& git -C $Repo tag --list 'v[0-9]*' --sort=-version:refname)
    if ($tags.Count -lt 2) {
        throw "Need at least two version tags. Found: $($tags.Count)"
    }

    $FinalRef = [string]$tags[0]
    $BaseRef = [string]$tags[1]
    $autoRefs = $true
}
elseif ([string]::IsNullOrWhiteSpace($FinalRef) -or [string]::IsNullOrWhiteSpace($BaseRef)) {
    throw "Provide both -BaseRef and -FinalRef, or neither."
}

& git -C $Repo rev-parse --verify $BaseRef *> $null
if ($LASTEXITCODE -ne 0) { throw "Invalid BaseRef: $BaseRef" }

& git -C $Repo rev-parse --verify $FinalRef *> $null
if ($LASTEXITCODE -ne 0) { throw "Invalid FinalRef: $FinalRef" }

$baseCommit = (& git -C $Repo rev-parse "$BaseRef^{commit}").Trim()
$finalCommit = (& git -C $Repo rev-parse "$FinalRef^{commit}").Trim()
$finalCommitShort = $finalCommit.Substring(0, [Math]::Min(8, $finalCommit.Length))

$baseTimeRaw = (& git -C $Repo log -1 --format=%cI $BaseRef).Trim()
$finalTimeRaw = (& git -C $Repo log -1 --format=%cI $FinalRef).Trim()

$baseCommitTime = ([datetimeoffset]$baseTimeRaw).LocalDateTime
$finalCommitTime = ([datetimeoffset]$finalTimeRaw).LocalDateTime

$finalVersion = $FinalRef
if ($finalVersion.StartsWith('v')) {
    $finalVersion = $finalVersion.Substring(1)
}

# This is the broad safety window. Session relevance below narrows it.
$broadStart = $baseCommitTime
$broadEnd = $finalCommitTime.AddMinutes($postCommitBuffer)

# -----------------------------------------------------------------------------
# First pass: discover candidate session metadata and explicit release hits.
# -----------------------------------------------------------------------------

Write-Host ''
Write-Host 'CAS Universal Economic Audit'
Write-Host '----------------------------'
Write-Host "Repo:       $Repo"
Write-Host "Base:       $BaseRef"
Write-Host "Final:      $FinalRef"
Write-Host "Git window: $broadStart -> $broadEnd"
Write-Host ''
Write-Host 'Scanning Codex sessions...'

$repoJsonEscaped = $Repo.Replace('\', '\\')
$candidateFiles = @(
    Get-ChildItem -LiteralPath $SessionsRoot -Recurse -Filter '*.jsonl' -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.LastWriteTime -ge $broadStart.AddHours(-2) -and
        $_.LastWriteTime -le $broadEnd.AddHours(2)
    }
)

$sessionIndex = @()

foreach ($f in $candidateFiles) {
    $model = $null
    $firstTs = $null
    $lastTs = $null
    $firstExplicitHit = $null
    $lastExplicitHit = $null
    $repoHit = $false
    $versionHit = $false
    $commitHit = $false
    $developmentMutationHint = $false

    foreach ($line in (Read-SharedJsonl -Path $f.FullName)) {
        $lineTs = $null
        $obj = $null

        try {
            $obj = $line | ConvertFrom-Json -ErrorAction Stop
            if ($obj.timestamp) {
                $lineTs = Convert-ToLocalDateTime $obj.timestamp
                if ($null -ne $lineTs) {
                    if ($null -eq $firstTs -or $lineTs -lt $firstTs) { $firstTs = $lineTs }
                    if ($null -eq $lastTs -or $lineTs -gt $lastTs) { $lastTs = $lineTs }
                }
            }

            if ($obj.payload -and $obj.payload.model) {
                $candidateModel = [string]$obj.payload.model
                if (-not [string]::IsNullOrWhiteSpace($candidateModel)) {
                    $model = $candidateModel
                }
            }
        }
        catch {}

        $lineRepoHit = (
            $line.IndexOf($Repo, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf($repoJsonEscaped, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        )

        $lineVersionHit = (
            $line.IndexOf($FinalRef, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf($finalVersion, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        )

        $lineCommitHit = (
            $line.IndexOf($finalCommit, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf($finalCommitShort, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        )

        if ($lineRepoHit) { $repoHit = $true }
        if ($lineVersionHit) { $versionHit = $true }
        if ($lineCommitHit) { $commitHit = $true }

        # Stronger development evidence: the session actually attempted to mutate
        # a committed development path under src/ or tests/. Merely mentioning the
        # target version is not enough to classify a Main session as release work.
        if (
            -not $developmentMutationHint -and
            $line -match '(?i)apply_patch|fileChange|write_file|writefile|Set-Content|Add-Content|Out-File|\*\*\*\s+(Update|Add|Delete)\s+File:'
        ) {
            $mutationPaths = Get-PathsFromMutationLine -Line $line -Repo $Repo
            if (@($mutationPaths).Count -gt 0) {
                $developmentMutationHint = $true
            }
        }

        if (($lineVersionHit -or $lineCommitHit) -and $null -ne $lineTs) {
            if ($null -eq $firstExplicitHit -or $lineTs -lt $firstExplicitHit) {
                $firstExplicitHit = $lineTs
            }
            if ($null -eq $lastExplicitHit -or $lineTs -gt $lastExplicitHit) {
                $lastExplicitHit = $lineTs
            }
        }
    }

    if ($null -eq $firstTs -or $null -eq $lastTs) { continue }

    $overlapsBroad = ($firstTs -le $broadEnd -and $lastTs -ge $broadStart)
    if (-not $overlapsBroad) { continue }

    $role = Get-Role -Model $model -RateTable $RateTable

    $sessionIndex += [pscustomobject]@{
        File = $f.FullName
        Name = $f.Name
        Model = $model
        Role = $role
        Start = $firstTs
        End = $lastTs
        RepoHit = $repoHit
        VersionHit = $versionHit
        CommitHit = $commitHit
        ExplicitHit = ($versionHit -or $commitHit)
        DevelopmentMutationHint = $developmentMutationHint
        FirstExplicitHit = $firstExplicitHit
        LastExplicitHit = $lastExplicitHit
        SelectionReason = ''
    }
}

# -----------------------------------------------------------------------------
# Select relevant sessions.
#
# Main-session rule:
#   A version-name mention alone is NOT sufficient.
#
# Strong Main anchor:
#   - final commit hash is observed, OR
#   - target version is observed AND the session has real src/tests mutation evidence.
#
# This prevents planning/troubleshooting conversations that merely mention the
# upcoming version from being charged to the release.
#
# Worker sessions may still be selected by explicit evidence or by temporal
# overlap with a strong Main anchor.
# -----------------------------------------------------------------------------

$strongMain = @(
    $sessionIndex |
    Where-Object {
        $_.Role -eq 'Main' -and (
            $_.CommitHit -or
            ($_.VersionHit -and $_.DevelopmentMutationHint)
        )
    }
)

$relevanceMode = 'StrongDevelopmentEvidence'
$selected = @()

if ($strongMain.Count -gt 0) {
    foreach ($s in $sessionIndex) {
        if ($s.Role -eq 'Main') {
            if ($s.CommitHit) {
                $s.SelectionReason = 'final-commit-anchor'
                $selected += $s
                continue
            }

            if ($s.VersionHit -and $s.DevelopmentMutationHint) {
                $s.SelectionReason = 'version+dev-mutation'
                $selected += $s
                continue
            }

            # Include a neighboring Main continuation only when it has repo +
            # development mutation evidence and overlaps a strong anchor.
            if ($s.RepoHit -and $s.DevelopmentMutationHint) {
                $overlap = $false
                foreach ($anchor in $strongMain) {
                    if (Test-IntervalsOverlap -AStart $s.Start -AEnd $s.End -BStart $anchor.Start -BEnd $anchor.End -BufferMinutes 5) {
                        $overlap = $true
                        break
                    }
                }
                if ($overlap) {
                    $s.SelectionReason = 'dev-main-overlap'
                    $selected += $s
                    continue
                }
            }
        }

        if ($s.Role -eq 'Worker') {
            if ($s.CommitHit -or ($s.VersionHit -and $s.DevelopmentMutationHint)) {
                $s.SelectionReason = 'explicit-worker-dev'
                $selected += $s
                continue
            }

            $overlap = $false
            foreach ($anchor in $strongMain) {
                if (Test-IntervalsOverlap -AStart $s.Start -AEnd $s.End -BStart $anchor.Start -BEnd $anchor.End -BufferMinutes $workerOverlapBuffer) {
                    $overlap = $true
                    break
                }
            }
            if ($overlap) {
                $s.SelectionReason = 'worker-overlap'
                $selected += $s
                continue
            }
        }
    }
}
else {
    # Conservative fallback: require repo + actual dev mutation.
    # This is intentionally stricter than the previous repo-path fallback.
    $relevanceMode = 'DevMutationFallback'
    foreach ($s in $sessionIndex) {
        if (
            $s.RepoHit -and
            $s.DevelopmentMutationHint -and
            ($s.Role -eq 'Main' -or $s.Role -eq 'Worker')
        ) {
            $s.SelectionReason = 'repo+dev-mutation-fallback'
            $selected += $s
        }
    }
}

$selected = @($selected | Sort-Object File -Unique)

if ($selected.Count -eq 0) {
    throw "No relevant Sol/Luna development sessions found for $BaseRef -> $FinalRef."
}

# Narrow audit start to the first selected development session.
$selectedStartTimes = @(
    $selected |
    Select-Object -ExpandProperty Start
)

if ($selectedStartTimes.Count -gt 0) {
    $firstSelectedStart = ($selectedStartTimes | Measure-Object -Minimum).Minimum
    $auditStart = $firstSelectedStart.AddMinutes(-1 * $preHitBuffer)
    if ($auditStart -lt $broadStart) { $auditStart = $broadStart }
}
else {
    $auditStart = $broadStart
}

$auditEnd = $broadEnd

# -----------------------------------------------------------------------------
# Second pass: token deltas, paid-point observations, mutation evidence.
# -----------------------------------------------------------------------------

$tokenDeltas = @()
$balanceEvents = @()
$mutationEvents = @()
$unknownPricedModels = @{}

foreach ($meta in $selected) {
    $prevInput = 0L
    $prevCached = 0L
    $prevOutput = 0L
    $prevReasoning = 0L

    foreach ($line in (Read-SharedJsonl -Path $meta.File)) {
        $obj = $null
        try {
            $obj = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }

        $ts = $null
        if ($obj.timestamp) {
            $ts = Convert-ToLocalDateTime $obj.timestamp
        }

        if (
            $obj.type -eq 'event_msg' -and
            $obj.payload.type -eq 'token_count' -and
            $obj.payload.info -and
            $obj.payload.info.total_token_usage
        ) {
            $usage = $obj.payload.info.total_token_usage

            $curInput = [long]$usage.input_tokens
            $curCached = [long]$usage.cached_input_tokens
            $curOutput = [long]$usage.output_tokens
            $curReasoning = [long]$usage.reasoning_output_tokens

            $dInput = $curInput - $prevInput
            $dCached = $curCached - $prevCached
            $dOutput = $curOutput - $prevOutput
            $dReasoning = $curReasoning - $prevReasoning

            # Counter reset / epoch change protection.
            if ($dInput -lt 0 -or $dCached -lt 0 -or $dOutput -lt 0 -or $dReasoning -lt 0) {
                $dInput = $curInput
                $dCached = $curCached
                $dOutput = $curOutput
                $dReasoning = $curReasoning
            }

            $prevInput = $curInput
            $prevCached = $curCached
            $prevOutput = $curOutput
            $prevReasoning = $curReasoning

            if ($null -ne $ts -and $ts -ge $auditStart -and $ts -le $auditEnd) {
                $dUncached = $dInput - $dCached
                if ($dUncached -lt 0) { $dUncached = 0 }

                $cost = Get-NormalizedCost `
                    -Model $meta.Model `
                    -Uncached $dUncached `
                    -Cached $dCached `
                    -Output $dOutput `
                    -RateTable $RateTable

                $solEq = $null
                $rate = Get-RateInfo -Model $meta.Model -RateTable $RateTable
                if ($null -ne $cost -and $null -ne $rate) {
                    $solEq = $cost * [double]$rate.SolEquivalentMultiplier
                }
                else {
                    $unknownPricedModels[$meta.Model] = $true
                }

                $tokenDeltas += [pscustomobject]@{
                    Time = $ts
                    Model = $meta.Model
                    Role = $meta.Role
                    Session = $meta.Name
                    Input = $dInput
                    Cached = $dCached
                    Uncached = $dUncached
                    Output = $dOutput
                    Reasoning = $dReasoning
                    ActualCost = $cost
                    SolEquivalent = $solEq
                }

                $credits = $obj.payload.rate_limits.credits
                if ($credits -and $credits.has_credits -eq $true -and $null -ne $credits.balance) {
                    $balanceEvents += [pscustomobject]@{
                        Time = $ts
                        Balance = [decimal]$credits.balance
                        Session = $meta.Name
                    }
                }
            }
        }

        if (
            $null -ne $ts -and
            $ts -ge $auditStart -and
            $ts -le $auditEnd -and
            $line -match '(?i)apply_patch|fileChange|write_file|writefile|Set-Content|Add-Content|Out-File|\*\*\*\s+(Update|Add|Delete)\s+File:'
        ) {
            $paths = Get-PathsFromMutationLine -Line $line -Repo $Repo
            foreach ($p in $paths) {
                $mutationEvents += [pscustomobject]@{
                    Time = $ts
                    Model = $meta.Model
                    Role = $meta.Role
                    Session = $meta.Name
                    Path = $p
                }
            }
        }
    }
}

# -----------------------------------------------------------------------------
# Cost accounting.
# -----------------------------------------------------------------------------

$mainItems = @($tokenDeltas | Where-Object { $_.Role -eq 'Main' -and $null -ne $_.ActualCost })
$workerItems = @($tokenDeltas | Where-Object { $_.Role -eq 'Worker' -and $null -ne $_.ActualCost })

$mainActual = if ($mainItems.Count -gt 0) {
    [double](($mainItems | Measure-Object -Property ActualCost -Sum).Sum)
} else { 0.0 }

$workerActual = if ($workerItems.Count -gt 0) {
    [double](($workerItems | Measure-Object -Property ActualCost -Sum).Sum)
} else { 0.0 }

$workerSolEq = if ($workerItems.Count -gt 0) {
    [double](($workerItems | Measure-Object -Property SolEquivalent -Sum).Sum)
} else { 0.0 }

$actualTotal = $mainActual + $workerActual
$allSolEquivalent = $mainActual + $workerSolEq

$displacementPct = if ($allSolEquivalent -gt 0) {
    [math]::Round(($workerSolEq / $allSolEquivalent) * 100.0, 1)
} else { $null }

$savingPct = if ($allSolEquivalent -gt 0) {
    [math]::Round((1.0 - ($actualTotal / $allSolEquivalent)) * 100.0, 1)
} else { $null }

$costSummary = [pscustomobject]@{
    MainActual = [math]::Round($mainActual, 2)
    WorkerActual = [math]::Round($workerActual, 2)
    ActualCost = [math]::Round($actualTotal, 2)
    WorkerSolEquivalent = [math]::Round($workerSolEq, 2)
    AllSolEquivalent = [math]::Round($allSolEquivalent, 2)
    SolDisplacementPct = $displacementPct
    SavingPct = $savingPct
}

# -----------------------------------------------------------------------------
# Paid-point observation.
# -----------------------------------------------------------------------------

$pointEvents = @(
    $balanceEvents |
    Sort-Object Time, Balance -Unique
)

$observedDecrease = 0.0
$observedIncrease = 0.0

if ($pointEvents.Count -gt 1) {
    $previousBalance = [decimal]$pointEvents[0].Balance

    for ($i = 1; $i -lt $pointEvents.Count; $i++) {
        $currentBalance = [decimal]$pointEvents[$i].Balance
        $delta = $currentBalance - $previousBalance

        if ($delta -lt 0) {
            $observedDecrease += [double](-$delta)
        }
        elseif ($delta -gt 0) {
            $observedIncrease += [double]$delta
        }

        $previousBalance = $currentBalance
    }
}

$pointSummary = [pscustomobject]@{
    BalanceEvents = $pointEvents.Count
    ObservedDecrease = [math]::Round($observedDecrease, 6)
    ObservedIncrease = [math]::Round($observedIncrease, 6)
    TopupOrStaleBalanceDetected = ($observedIncrease -gt 0.000001)
}

# -----------------------------------------------------------------------------
# Delegation Coverage + Adoption Efficiency.
# -----------------------------------------------------------------------------

$gitRows = @(Get-GitNumStat -Repo $Repo -FromRef $BaseRef -ToRef $FinalRef)
$devRows = @($gitRows | Where-Object { $_.IsDevelopment })
$coreRows = @($gitRows | Where-Object { $_.IsCore })

$workerMut = @($mutationEvents | Where-Object { $_.Role -eq 'Worker' })
$mainMut = @($mutationEvents | Where-Object { $_.Role -eq 'Main' })

$workerFiles = @($workerMut | Select-Object -ExpandProperty Path -Unique)

$devFileSet = @{}
foreach ($r in $devRows) {
    $devFileSet[$r.Path.ToLowerInvariant()] = $r
}

$coreFileSet = @{}
foreach ($r in $coreRows) {
    $coreFileSet[$r.Path.ToLowerInvariant()] = $r
}

$workerDevFiles = @(
    $workerFiles |
    Where-Object { $devFileSet.ContainsKey($_.ToLowerInvariant()) }
)

$workerCoreFiles = @(
    $workerFiles |
    Where-Object { $coreFileSet.ContainsKey($_.ToLowerInvariant()) }
)

$totalDevLines = if ($devRows.Count -gt 0) {
    [double](($devRows | Measure-Object -Property Changed -Sum).Sum)
} else { 0.0 }

$totalCoreLines = if ($coreRows.Count -gt 0) {
    [double](($coreRows | Measure-Object -Property Changed -Sum).Sum)
} else { 0.0 }

$workerDevLines = 0.0
foreach ($p in $workerDevFiles) {
    $workerDevLines += [double]$devFileSet[$p.ToLowerInvariant()].Changed
}

$workerCoreLines = 0.0
foreach ($p in $workerCoreFiles) {
    $workerCoreLines += [double]$coreFileSet[$p.ToLowerInvariant()].Changed
}

$coverageSummary = [pscustomobject]@{
    ChangedDevFiles = $devRows.Count
    WorkerTouchedDevFiles = $workerDevFiles.Count
    DevFileCoveragePct = Measure-Percent -Part $workerDevFiles.Count -Whole $devRows.Count
    ChangedDevLines = [int]$totalDevLines
    WorkerFootprintDevLines = [int]$workerDevLines
    DevLineCoveragePct = Measure-Percent -Part $workerDevLines -Whole $totalDevLines
    ChangedCoreFiles = $coreRows.Count
    WorkerTouchedCoreFiles = $workerCoreFiles.Count
    CoreFileCoveragePct = Measure-Percent -Part $workerCoreFiles.Count -Whole $coreRows.Count
    ChangedCoreLines = [int]$totalCoreLines
    WorkerFootprintCoreLines = [int]$workerCoreLines
    CoreLineCoveragePct = Measure-Percent -Part $workerCoreLines -Whole $totalCoreLines
    Confidence = if ($workerFiles.Count -gt 0) { 'Medium-High' } else { 'Low/NoMutationEvidence' }
}

$retained = 0
$direct = 0
$adjusted = 0
$reverted = 0

foreach ($p in $workerFiles) {
    $key = $p.ToLowerInvariant()
    $isRetained = $devFileSet.ContainsKey($key)

    if (-not $isRetained) {
        $reverted++
        continue
    }

    $retained++

    $lastWorkerTime = (
        $workerMut |
        Where-Object { $_.Path -ieq $p } |
        Measure-Object -Property Time -Maximum
    ).Maximum

    $mainAfter = @(
        $mainMut |
        Where-Object { $_.Path -ieq $p -and $_.Time -gt $lastWorkerTime }
    )

    if ($mainAfter.Count -gt 0) {
        $adjusted++
    }
    else {
        $direct++
    }
}

$adoptionSummary = [pscustomobject]@{
    WorkerTouchedFiles = $workerFiles.Count
    RetainedFiles = $retained
    RetentionPct = Measure-Percent -Part $retained -Whole $workerFiles.Count
    DirectAdoptFiles = $direct
    DirectAdoptionPct = Measure-Percent -Part $direct -Whole $workerFiles.Count
    MainAdjustedFiles = $adjusted
    MainAdjustedPct = Measure-Percent -Part $adjusted -Whole $workerFiles.Count
    RevertedFiles = $reverted
    RevertedPct = Measure-Percent -Part $reverted -Whole $workerFiles.Count
    Confidence = if ($workerFiles.Count -gt 0) { 'Medium' } else { 'Low/NoMutationEvidence' }
}

# -----------------------------------------------------------------------------
# Continuation/Main-tax proxy:
# Main normalized cost before the first observed Worker token event.
# This is a mechanical proxy only.
# -----------------------------------------------------------------------------

$firstWorkerToken = (
    $tokenDeltas |
    Where-Object { $_.Role -eq 'Worker' } |
    Measure-Object -Property Time -Minimum
).Minimum

$continuationTaxProxy = $null

if ($firstWorkerToken) {
    $earlyMain = @(
        $tokenDeltas |
        Where-Object {
            $_.Role -eq 'Main' -and
            $_.Time -lt $firstWorkerToken -and
            $null -ne $_.ActualCost
        }
    )

    if ($earlyMain.Count -gt 0) {
        $continuationTaxProxy = [math]::Round(
            [double](($earlyMain | Measure-Object -Property ActualCost -Sum).Sum),
            2
        )
    }
    else {
        $continuationTaxProxy = 0.0
    }
}

# -----------------------------------------------------------------------------
# Warnings / confidence notes.
# -----------------------------------------------------------------------------

$warnings = New-Object System.Collections.Generic.List[string]

if ($relevanceMode -eq 'DevMutationFallback') {
    $warnings.Add(
        'No strong final-commit or version+development-mutation Main anchor was found. ' +
        'Session selection fell back to repository + development-mutation evidence.'
    )
}

$unknownModels = @($unknownPricedModels.Keys | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($unknownModels.Count -gt 0) {
    $warnings.Add(
        'Pricing is not configured for model(s): ' + ($unknownModels -join ', ') +
        '. Their token/cost events are not included in normalized cost totals. ' +
        'Update audit.config.json before using cost conclusions.'
    )
}

if ($workerFiles.Count -eq 0) {
    $warnings.Add(
        'No Worker mutation evidence was detected. Delegation Coverage / Adoption Efficiency are low-confidence.'
    )
}

$gitStatus = @(& git -C $Repo status --porcelain=v1)
if ($gitStatus.Count -gt 0) {
    $warnings.Add(
        'Repository has uncommitted changes. Git-based coverage uses only ' +
        "$BaseRef..$FinalRef and ignores current uncommitted changes."
    )
}

if ($pointSummary.TopupOrStaleBalanceDetected) {
    $warnings.Add(
        'Paid-point balance increased during the window. Observed point decrease must not be treated as exact task cost.'
    )
}

# -----------------------------------------------------------------------------
# Output.
# -----------------------------------------------------------------------------

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safeFinal = Get-SafeFileName $FinalRef
$reportDir = Join-Path $OutputRoot ("{0}_{1}" -f $safeFinal, $stamp)
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

$jsonPath = Join-Path $reportDir 'Economic_Audit.json'
$mdPath = Join-Path $reportDir 'Economic_Audit.md'

$result = [pscustomobject]@{
    SchemaVersion = 1
    GeneratedAt = (Get-Date).ToString('o')
    AutoSelectedRefs = $autoRefs
    Repo = $Repo
    SessionsRoot = $SessionsRoot
    BaseRef = $BaseRef
    FinalRef = $FinalRef
    BaseCommit = $baseCommit
    FinalCommit = $finalCommit
    Window = [pscustomobject]@{
        GitBroadStart = $broadStart
        GitBroadEnd = $broadEnd
        AuditStart = $auditStart
        AuditEnd = $auditEnd
        RelevanceMode = $relevanceMode
    }
    RelevantSessions = @(
        $selected | Select-Object File,Name,Model,Role,Start,End,RepoHit,VersionHit,CommitHit,DevelopmentMutationHint,SelectionReason
    )
    Cost = $costSummary
    PaidPointsObserved = $pointSummary
    DelegationCoverage = $coverageSummary
    AdoptionEfficiency = $adoptionSummary
    MainBeforeFirstWorkerCostProxy = $continuationTaxProxy
    UnknownPricedModels = $unknownModels
    Warnings = @($warnings)
    Notes = @(
        'Actual Cost / Sol Displacement use configured token-price rates and incremental token_count deltas.',
        'Reasoning tokens are already included in output cost and are not double-charged.',
        'Paid-point balances are account-level observations and are not assumed to equal normalized task cost.',
        'Delegation Coverage is a Git/session mutation-footprint proxy, not semantic workload percentage.',
        'Adoption Efficiency is a retention/direct-adjustment proxy; MainAdjusted does not mean Worker failure.',
        'MainBeforeFirstWorkerCostProxy is a mechanical pre-worker Main-cost proxy, not proof that all of it was orchestration overhead.',
        'No model/provider call is made by this audit.'
    )
}

$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

function FmtPct($value) {
    if ($null -eq $value) { return 'N/A' }
    return ("{0}%" -f $value)
}

function FmtVal($value) {
    if ($null -eq $value) { return 'N/A' }
    return [string]$value
}

$md = New-Object System.Collections.Generic.List[string]

$md.Add("# Codex Agent Switch $FinalRef Economic Audit")
$md.Add('')
$md.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$md.Add('')
$md.Add(("**Range:** {0} -> {1}" -f $BaseRef, $FinalRef))
$md.Add('')
$md.Add("**Session selection:** $relevanceMode")
$md.Add('')
$md.Add("**Audit window:** $auditStart -> $auditEnd")
$md.Add('')

$md.Add('## Executive Summary')
$md.Add('')
$md.Add('| Actual Cost | All-Sol Equivalent | Sol Displacement | Saving |')
$md.Add('|---:|---:|---:|---:|')
$md.Add(
    "| $($costSummary.ActualCost) | $($costSummary.AllSolEquivalent) | " +
    "$(FmtPct $costSummary.SolDisplacementPct) | $(FmtPct $costSummary.SavingPct) |"
)
$md.Add('')

$md.Add('## 1. Actual Cost')
$md.Add('')
$md.Add('| Main Actual | Worker Actual | Actual Total | Paid-Point Decrease | Balance Increase Detected |')
$md.Add('|---:|---:|---:|---:|---|')
$md.Add(
    "| $($costSummary.MainActual) | $($costSummary.WorkerActual) | $($costSummary.ActualCost) | " +
    "$($pointSummary.ObservedDecrease) | $($pointSummary.TopupOrStaleBalanceDetected) |"
)
$md.Add('')

$md.Add('## 2. Sol Displacement')
$md.Add('')
$md.Add('| Worker Sol-Equivalent | All-Sol Equivalent | Displacement | Theoretical Saving |')
$md.Add('|---:|---:|---:|---:|')
$md.Add(
    "| $($costSummary.WorkerSolEquivalent) | $($costSummary.AllSolEquivalent) | " +
    "$(FmtPct $costSummary.SolDisplacementPct) | $(FmtPct $costSummary.SavingPct) |"
)
$md.Add('')

$md.Add('## 3. Delegation Coverage')
$md.Add('')
$md.Add('Mechanical proxy: Worker-touched files / final changed files, plus changed-line footprint.')
$md.Add('')
$md.Add('| Dev File Coverage | Dev Line Footprint | Core File Coverage | Core Line Footprint | Confidence |')
$md.Add('|---:|---:|---:|---:|---|')
$md.Add(
    "| $(FmtPct $coverageSummary.DevFileCoveragePct) ($($coverageSummary.WorkerTouchedDevFiles)/$($coverageSummary.ChangedDevFiles)) | " +
    "$(FmtPct $coverageSummary.DevLineCoveragePct) | " +
    "$(FmtPct $coverageSummary.CoreFileCoveragePct) ($($coverageSummary.WorkerTouchedCoreFiles)/$($coverageSummary.ChangedCoreFiles)) | " +
    "$(FmtPct $coverageSummary.CoreLineCoveragePct) | $($coverageSummary.Confidence) |"
)
$md.Add('')

$md.Add('## 4. Adoption Efficiency')
$md.Add('')
$md.Add('Mechanical proxy: Worker-touched files retained in final diff, and whether Main touched them later.')
$md.Add('')
$md.Add('| Worker Files | Retained | Direct Adopt | Main Adjusted | Reverted | Confidence |')
$md.Add('|---:|---:|---:|---:|---:|---|')
$md.Add(
    "| $($adoptionSummary.WorkerTouchedFiles) | $(FmtPct $adoptionSummary.RetentionPct) | " +
    "$(FmtPct $adoptionSummary.DirectAdoptionPct) | $(FmtPct $adoptionSummary.MainAdjustedPct) | " +
    "$(FmtPct $adoptionSummary.RevertedPct) | $($adoptionSummary.Confidence) |"
)
$md.Add('')

$md.Add('## Main-before-first-Worker Proxy')
$md.Add('')
if ($null -ne $continuationTaxProxy) {
    $md.Add("Main normalized cost before the first observed Worker token event: **$continuationTaxProxy**.")
}
else {
    $md.Add('Unavailable: no priced Worker token event was detected.')
}
$md.Add('')
$md.Add('This is only a mechanical proxy; it does not prove that all early Main cost was orchestration overhead.')
$md.Add('')

$md.Add('## Relevant Sessions')
$md.Add('')
$md.Add('| Model | Role | Start | End | Selection |')
$md.Add('|---|---|---|---|---|')
foreach ($s in $selected) {
    $md.Add("| $($s.Model) | $($s.Role) | $($s.Start) | $($s.End) | $($s.SelectionReason) |")
}
$md.Add('')

if ($warnings.Count -gt 0) {
    $md.Add('## Warnings')
    $md.Add('')
    foreach ($w in $warnings) {
        $md.Add("- $w")
    }
    $md.Add('')
}

$md.Add('## Method / Limits')
$md.Add('')
$md.Add('- Latest two semantic-version tags are selected automatically in zero-touch mode.')
$md.Add('- Main sessions require strong release evidence: final-commit hit, or target-version hit plus real `src/`/`tests/` mutation evidence.')
$md.Add('- Worker child sessions may be selected by explicit development evidence or temporal overlap with a strong Main release session.')
$md.Add('- Token cost uses incremental `token_count` deltas. Reasoning tokens are not double-charged.')
$md.Add('- Model pricing comes from `audit.config.json`; unknown models are reported instead of guessed.')
$md.Add('- Paid-point balance is account-level evidence, not a normalized-cost oracle.')
$md.Add('- Delegation Coverage and Adoption Efficiency are mechanical proxies, not semantic work-quality measurements.')
$md.Add('- Git metrics use committed `$BaseRef..$FinalRef`; uncommitted work is not silently included.')
$md.Add('- The audit is read-only except for writing this report directory.')
$md.Add('- No Sol/Luna/DeepSeek/OpenCode call is made by the audit.')

$md | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host ''
Write-Host '=============================================================='
Write-Host " CAS ECONOMIC AUDIT: $BaseRef -> $FinalRef"
Write-Host '=============================================================='
Write-Host ''
Write-Host ("Actual Cost:        {0}" -f $costSummary.ActualCost)
Write-Host ("All-Sol Equivalent: {0}" -f $costSummary.AllSolEquivalent)
Write-Host ("Sol Displacement:   {0}" -f (FmtPct $costSummary.SolDisplacementPct))
Write-Host ("Saving:             {0}" -f (FmtPct $costSummary.SavingPct))
Write-Host ''
Write-Host ("Delegation files:   {0}/{1} ({2})" -f `
    $coverageSummary.WorkerTouchedDevFiles, `
    $coverageSummary.ChangedDevFiles, `
    (FmtPct $coverageSummary.DevFileCoveragePct))
Write-Host ("Worker retention:   {0}" -f (FmtPct $adoptionSummary.RetentionPct))
Write-Host ("Direct adoption:    {0}" -f (FmtPct $adoptionSummary.DirectAdoptionPct))
Write-Host ("Main adjusted:      {0}" -f (FmtPct $adoptionSummary.MainAdjustedPct))
Write-Host ''

if ($warnings.Count -gt 0) {
    Write-Host 'WARNINGS:'
    foreach ($w in $warnings) {
        Write-Host (" - " + $w)
    }
    Write-Host ''
}

Write-Host "Markdown: $mdPath"
Write-Host "JSON:     $jsonPath"
Write-Host ''
Write-Host 'Done. No model/provider call was made.'
