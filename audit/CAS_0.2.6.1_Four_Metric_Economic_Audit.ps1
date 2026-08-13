param(
    [string]$Repo = 'E:\AISPace\主模型项目区\state\worktrees\cas-026-impl',
    [string]$SessionsRoot = 'E:\AI\CODEX\.codex\sessions\2026\08\13',
    [string]$BaseRef = 'v0.2.6',
    [string]$Round1Ref = 'a381a04',
    [string]$FinalRef = 'v0.2.6.1',
    [datetime]$Round1Start = '2026-08-13 08:33:00',
    [datetime]$Round1End   = '2026-08-13 09:00:00',
    [datetime]$Round2Start = '2026-08-13 09:00:00',
    [datetime]$Round2End   = '2026-08-13 09:30:00'
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Codex Agent Switch 0.2.6.1 Economic Audit
# Windows PowerShell 5.1 compatible
#
# Metrics:
#   1. Actual Cost
#   2. Sol Displacement
#   3. Delegation Coverage (mechanical changed-file / changed-line proxy)
#   4. Adoption Efficiency (retained / direct / Main-adjusted / reverted proxy)
#
# Important:
# - Metrics 1-2 are token/cost accounting metrics.
# - Metrics 3-4 are mechanical proxies derived from Git + session mutation evidence.
#   They do NOT pretend to measure semantic difficulty or code quality.
# -----------------------------------------------------------------------------

function Read-SharedJsonl {
    param([string]$Path)

    $fs = New-Object System.IO.FileStream(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite
    )
    $sr = New-Object System.IO.StreamReader($fs)

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

function Get-RoundName {
    param([datetime]$Time)

    if ($Time -ge $Round1Start -and $Time -lt $Round1End) { return 'R1' }
    if ($Time -ge $Round2Start -and $Time -le $Round2End) { return 'R2' }
    return $null
}

function Get-ModelRates {
    param([string]$Model)

    if ($Model -eq 'gpt-5.6-sol') {
        return [pscustomobject]@{
            Input = 125.0
            Cached = 12.5
            Output = 750.0
            SolMultiplier = 1.0
        }
    }

    if ($Model -eq 'gpt-5.6-luna') {
        return [pscustomobject]@{
            Input = 25.0
            Cached = 2.5
            Output = 150.0
            SolMultiplier = 5.0
        }
    }

    return $null
}

function Get-NormalizedCost {
    param(
        [string]$Model,
        [long]$Uncached,
        [long]$Cached,
        [long]$Output
    )

    $rates = Get-ModelRates -Model $Model
    if ($null -eq $rates) { return $null }

    return (
        ($Uncached * $rates.Input / 1000000.0) +
        ($Cached * $rates.Cached / 1000000.0) +
        ($Output * $rates.Output / 1000000.0)
    )
}

function Normalize-RepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $p = $Path.Trim().Trim('"').Trim("'")
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
    param([string]$Line)

    $results = New-Object System.Collections.Generic.List[string]

    # apply_patch style
    foreach ($m in [regex]::Matches($Line, '\*\*\*\s+(?:Update|Add|Delete)\s+File:\s*([^\\r\\n"]+)')) {
        $results.Add($m.Groups[1].Value)
    }

    # JSON path/file fields
    foreach ($m in [regex]::Matches($Line, '"(?:path|file|filePath|filepath)"\s*:\s*"([^"]+)"')) {
        $results.Add(($m.Groups[1].Value -replace '\\\\', '\'))
    }

    # Common repo-relative paths embedded in command text / patch text.
    foreach ($m in [regex]::Matches(
        $Line,
        '(?i)(?<![A-Za-z0-9_.-])((?:src|tests)/[A-Za-z0-9_./\\() +-]+\.(?:cs|xaml|csproj|props|targets|json|md|ps1|toml|yml|yaml))'
    )) {
        $results.Add($m.Groups[1].Value)
    }

    # Windows absolute paths under this project, if present in tool args.
    $escapedRepoLeaf = [regex]::Escape('cas-026-impl')
    foreach ($m in [regex]::Matches(
        $Line,
        '(?i)([A-Z]:\\\\[^"]*?' + $escapedRepoLeaf + '\\\\(?:src|tests)\\\\[^"]+\.(?:cs|xaml|csproj|props|targets|json|md|ps1|toml|yml|yaml))'
    )) {
        $results.Add(($m.Groups[1].Value -replace '\\\\', '\'))
    }

    $normalized = foreach ($item in $results) {
        $n = Normalize-RepoPath -Path $item
        if ($n -and (Test-DevelopmentFile -Path $n)) { $n }
    }

    return @($normalized | Sort-Object -Unique)
}

function Get-GitNumStat {
    param(
        [string]$FromRef,
        [string]$ToRef
    )

    $rows = @()
    $lines = & git -C $Repo diff --numstat $FromRef $ToRef -- 2>$null

    foreach ($line in $lines) {
        if ($line -match '^(\d+|-)\s+(\d+|-)\s+(.+)$') {
            $add = if ($matches[1] -eq '-') { 0 } else { [int]$matches[1] }
            $del = if ($matches[2] -eq '-') { 0 } else { [int]$matches[2] }
            $path = Normalize-RepoPath -Path $matches[3]

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

    return $rows
}

function Measure-Percent {
    param(
        [double]$Part,
        [double]$Whole
    )

    if ($Whole -le 0) { return $null }
    return [math]::Round(($Part / $Whole) * 100.0, 1)
}

if (-not (Test-Path -LiteralPath $Repo)) {
    throw "Repo not found: $Repo"
}
if (-not (Test-Path -LiteralPath $SessionsRoot)) {
    throw "Sessions root not found: $SessionsRoot"
}

# Verify refs first.
& git -C $Repo rev-parse --verify $BaseRef *> $null
& git -C $Repo rev-parse --verify $Round1Ref *> $null
& git -C $Repo rev-parse --verify $FinalRef *> $null

$reportDir = Join-Path $Repo 'audit'
if (-not (Test-Path -LiteralPath $reportDir)) {
    New-Item -ItemType Directory -Path $reportDir | Out-Null
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $reportDir ("CAS_0.2.6.1_Economic_Audit_{0}.json" -f $stamp)
$mdPath   = Join-Path $reportDir ("CAS_0.2.6.1_Economic_Audit_{0}.md" -f $stamp)

# -----------------------------------------------------------------------------
# 1) Parse relevant sessions and token deltas.
# -----------------------------------------------------------------------------

$sessionFiles = Get-ChildItem -LiteralPath $SessionsRoot -Filter '*.jsonl' -File

$sessionMeta = @()
$tokenDeltas = @()
$balanceEvents = @()
$mutationEvents = @()

foreach ($f in $sessionFiles) {
    $allLines = New-Object System.Collections.Generic.List[string]
    $model = $null
    $firstTs = $null
    $lastTs = $null
    $relevanceHit = $false

    foreach ($line in (Read-SharedJsonl -Path $f.FullName)) {
        $allLines.Add($line)

        if (
            $line -match 'cas-026-impl' -or
            $line -match '0\.2\.6\.1' -or
            $line -match 'fix/0\.2\.6\.1-stability' -or
            $line -match 'a381a04' -or
            $line -match '729d0b4'
        ) {
            $relevanceHit = $true
        }

        try {
            $o = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }

        if ($o.timestamp) {
            try {
                $ts = ([datetimeoffset]$o.timestamp).LocalDateTime
                if ($null -eq $firstTs -or $ts -lt $firstTs) { $firstTs = $ts }
                if ($null -eq $lastTs -or $ts -gt $lastTs) { $lastTs = $ts }
            }
            catch {}
        }

        if ($o.payload -and $o.payload.model) {
            $candidate = [string]$o.payload.model
            if ($candidate -match '^gpt-5\.6-(sol|luna)$') {
                $model = $candidate
            }
        }
    }

    if ($null -eq $firstTs -or $null -eq $lastTs) { continue }

    $overlapsAuditWindow = (
        $firstTs -le $Round2End -and
        $lastTs -ge $Round1Start
    )

    if (-not $overlapsAuditWindow) { continue }

    # Keep model sessions that explicitly reference this worktree/version.
    # Also keep Luna sessions inside the audit window because native worker
    # child sessions may contain only bounded TaskPacket context.
    $keep = $relevanceHit -or ($model -eq 'gpt-5.6-luna')
    if (-not $keep) { continue }
    if ($model -notmatch '^gpt-5\.6-(sol|luna)$') { continue }

    $sessionMeta += [pscustomobject]@{
        File = $f.FullName
        Name = $f.Name
        Model = $model
        Start = $firstTs
        End = $lastTs
        RelevantByText = $relevanceHit
    }

    $prevInput = 0L
    $prevCached = 0L
    $prevOutput = 0L
    $prevReasoning = 0L

    foreach ($line in $allLines) {
        try {
            $o = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            continue
        }

        if (
            $o.type -eq 'event_msg' -and
            $o.payload.type -eq 'token_count' -and
            $o.payload.info -and
            $o.payload.info.total_token_usage
        ) {
            $ts = ([datetimeoffset]$o.timestamp).LocalDateTime
            $round = Get-RoundName -Time $ts

            $usage = $o.payload.info.total_token_usage
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

            if ($round) {
                $dUncached = $dInput - $dCached
                if ($dUncached -lt 0) { $dUncached = 0 }

                $cost = Get-NormalizedCost -Model $model -Uncached $dUncached -Cached $dCached -Output $dOutput
                $rates = Get-ModelRates -Model $model
                $solEq = $cost * $rates.SolMultiplier

                $tokenDeltas += [pscustomobject]@{
                    Round = $round
                    Time = $ts
                    Model = $model
                    Session = $f.Name
                    Input = $dInput
                    Cached = $dCached
                    Uncached = $dUncached
                    Output = $dOutput
                    Reasoning = $dReasoning
                    ActualCost = $cost
                    SolEquivalent = $solEq
                }
            }

            $credits = $o.payload.rate_limits.credits
            if ($round -and $credits -and $credits.has_credits -eq $true -and $null -ne $credits.balance) {
                $balanceEvents += [pscustomobject]@{
                    Round = $round
                    Time = $ts
                    Balance = [decimal]$credits.balance
                    Session = $f.Name
                }
            }
        }

        # Mutation evidence. We intentionally require a mutation-ish line so that
        # paths merely mentioned in TaskPackets/prompts are less likely to count.
        if (
            $line -match '(?i)apply_patch|fileChange|write_file|writefile|Set-Content|Add-Content|Out-File|\*\*\*\s+(Update|Add|Delete)\s+File:'
        ) {
            if ($o.timestamp) {
                try {
                    $ts2 = ([datetimeoffset]$o.timestamp).LocalDateTime
                    $round2 = Get-RoundName -Time $ts2
                    if ($round2) {
                        $paths = Get-PathsFromMutationLine -Line $line
                        foreach ($p in $paths) {
                            $mutationEvents += [pscustomobject]@{
                                Round = $round2
                                Time = $ts2
                                Model = $model
                                Session = $f.Name
                                Path = $p
                            }
                        }
                    }
                }
                catch {}
            }
        }
    }
}

# -----------------------------------------------------------------------------
# 2) Per-round cost accounting.
# -----------------------------------------------------------------------------

$costRows = @()

foreach ($round in @('R1', 'R2')) {
    $items = @($tokenDeltas | Where-Object { $_.Round -eq $round })

    $solItems = @($items | Where-Object { $_.Model -eq 'gpt-5.6-sol' })
    $lunaItems = @($items | Where-Object { $_.Model -eq 'gpt-5.6-luna' })

    $solActual = if ($solItems.Count -gt 0) {
        ($solItems | Measure-Object -Property ActualCost -Sum).Sum
    } else { 0.0 }

    $lunaActual = if ($lunaItems.Count -gt 0) {
        ($lunaItems | Measure-Object -Property ActualCost -Sum).Sum
    } else { 0.0 }

    $lunaSolEq = if ($lunaItems.Count -gt 0) {
        ($lunaItems | Measure-Object -Property SolEquivalent -Sum).Sum
    } else { 0.0 }

    $actual = $solActual + $lunaActual
    $allSol = $solActual + $lunaSolEq
    $displacement = if ($allSol -gt 0) { ($lunaSolEq / $allSol) * 100.0 } else { 0.0 }
    $saving = if ($allSol -gt 0) { (1.0 - ($actual / $allSol)) * 100.0 } else { 0.0 }

    $costRows += [pscustomobject]@{
        Round = $round
        SolActual = [math]::Round($solActual, 2)
        LunaActual = [math]::Round($lunaActual, 2)
        ActualCost = [math]::Round($actual, 2)
        LunaSolEquivalent = [math]::Round($lunaSolEq, 2)
        AllSolEquivalent = [math]::Round($allSol, 2)
        SolDisplacementPct = [math]::Round($displacement, 1)
        SavingPct = [math]::Round($saving, 1)
    }
}

$totalSolActual = ($costRows | Measure-Object -Property SolActual -Sum).Sum
$totalLunaActual = ($costRows | Measure-Object -Property LunaActual -Sum).Sum
$totalActual = ($costRows | Measure-Object -Property ActualCost -Sum).Sum
$totalLunaSolEq = ($costRows | Measure-Object -Property LunaSolEquivalent -Sum).Sum
$totalAllSol = ($costRows | Measure-Object -Property AllSolEquivalent -Sum).Sum

$totalCost = [pscustomobject]@{
    Round = 'TOTAL'
    SolActual = [math]::Round($totalSolActual, 2)
    LunaActual = [math]::Round($totalLunaActual, 2)
    ActualCost = [math]::Round($totalActual, 2)
    LunaSolEquivalent = [math]::Round($totalLunaSolEq, 2)
    AllSolEquivalent = [math]::Round($totalAllSol, 2)
    SolDisplacementPct = if ($totalAllSol -gt 0) { [math]::Round(($totalLunaSolEq / $totalAllSol) * 100.0, 1) } else { 0 }
    SavingPct = if ($totalAllSol -gt 0) { [math]::Round((1.0 - ($totalActual / $totalAllSol)) * 100.0, 1) } else { 0 }
}

# -----------------------------------------------------------------------------
# 3) Paid point observation (account-level, not model pricing).
# -----------------------------------------------------------------------------

$pointRows = @()

foreach ($round in @('R1', 'R2')) {
    $events = @(
        $balanceEvents |
        Where-Object { $_.Round -eq $round } |
        Sort-Object Time
    )

    $decrease = 0.0
    $increase = 0.0

    if ($events.Count -gt 1) {
        $prev = [decimal]$events[0].Balance
        for ($i = 1; $i -lt $events.Count; $i++) {
            $cur = [decimal]$events[$i].Balance
            $delta = $cur - $prev

            if ($delta -lt 0) {
                $decrease += [double](-$delta)
            }
            elseif ($delta -gt 0) {
                $increase += [double]$delta
            }

            $prev = $cur
        }
    }

    $pointRows += [pscustomobject]@{
        Round = $round
        BalanceEvents = $events.Count
        ObservedDecrease = [math]::Round($decrease, 6)
        ObservedIncrease = [math]::Round($increase, 6)
        TopupOrStaleBalanceDetected = ($increase -gt 0.000001)
    }
}

# -----------------------------------------------------------------------------
# 4) Delegation Coverage and Adoption Efficiency.
# -----------------------------------------------------------------------------

$roundGit = @{
    R1 = Get-GitNumStat -FromRef $BaseRef -ToRef $Round1Ref
    R2 = Get-GitNumStat -FromRef $Round1Ref -ToRef $FinalRef
}

$coverageRows = @()
$adoptionRows = @()

foreach ($round in @('R1', 'R2')) {
    $gitRows = @($roundGit[$round])
    $devRows = @($gitRows | Where-Object { $_.IsDevelopment })
    $coreRows = @($gitRows | Where-Object { $_.IsCore })

    $workerMut = @(
        $mutationEvents |
        Where-Object { $_.Round -eq $round -and $_.Model -eq 'gpt-5.6-luna' }
    )
    $mainMut = @(
        $mutationEvents |
        Where-Object { $_.Round -eq $round -and $_.Model -eq 'gpt-5.6-sol' }
    )

    $workerFiles = @($workerMut | Select-Object -ExpandProperty Path -Unique)
    $mainFiles = @($mainMut | Select-Object -ExpandProperty Path -Unique)

    $devFileSet = @{}
    foreach ($r in $devRows) { $devFileSet[$r.Path.ToLowerInvariant()] = $r }

    $coreFileSet = @{}
    foreach ($r in $coreRows) { $coreFileSet[$r.Path.ToLowerInvariant()] = $r }

    $workerDevFiles = @(
        $workerFiles |
        Where-Object { $devFileSet.ContainsKey($_.ToLowerInvariant()) }
    )
    $workerCoreFiles = @(
        $workerFiles |
        Where-Object { $coreFileSet.ContainsKey($_.ToLowerInvariant()) }
    )

    $totalDevLines = if ($devRows.Count -gt 0) {
        ($devRows | Measure-Object -Property Changed -Sum).Sum
    } else { 0 }

    $totalCoreLines = if ($coreRows.Count -gt 0) {
        ($coreRows | Measure-Object -Property Changed -Sum).Sum
    } else { 0 }

    $workerDevLines = 0
    foreach ($p in $workerDevFiles) {
        $workerDevLines += $devFileSet[$p.ToLowerInvariant()].Changed
    }

    $workerCoreLines = 0
    foreach ($p in $workerCoreFiles) {
        $workerCoreLines += $coreFileSet[$p.ToLowerInvariant()].Changed
    }

    $coverageRows += [pscustomobject]@{
        Round = $round
        ChangedDevFiles = $devRows.Count
        WorkerTouchedDevFiles = $workerDevFiles.Count
        DevFileCoveragePct = Measure-Percent -Part $workerDevFiles.Count -Whole $devRows.Count
        ChangedDevLines = $totalDevLines
        WorkerFootprintDevLines = $workerDevLines
        DevLineCoveragePct = Measure-Percent -Part $workerDevLines -Whole $totalDevLines
        ChangedCoreFiles = $coreRows.Count
        WorkerTouchedCoreFiles = $workerCoreFiles.Count
        CoreFileCoveragePct = Measure-Percent -Part $workerCoreFiles.Count -Whole $coreRows.Count
        ChangedCoreLines = $totalCoreLines
        WorkerFootprintCoreLines = $workerCoreLines
        CoreLineCoveragePct = Measure-Percent -Part $workerCoreLines -Whole $totalCoreLines
        CoverageConfidence = if ($workerFiles.Count -gt 0) { 'Medium-High' } else { 'Low/NoMutationEvidence' }
    }

    # Adoption proxy:
    # - Retained: worker-touched file still exists in final round diff.
    # - Direct: retained and no Sol mutation evidence on same file after worker's
    #   last mutation of that file.
    # - MainAdjusted: retained and Sol touched it after worker.
    # - Reverted: worker touched it, but it does not survive in round diff.
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

    $adoptionRows += [pscustomobject]@{
        Round = $round
        WorkerTouchedFiles = $workerFiles.Count
        RetainedFiles = $retained
        RetentionPct = Measure-Percent -Part $retained -Whole $workerFiles.Count
        DirectAdoptFiles = $direct
        DirectAdoptionPct = Measure-Percent -Part $direct -Whole $workerFiles.Count
        MainAdjustedFiles = $adjusted
        MainAdjustedPct = Measure-Percent -Part $adjusted -Whole $workerFiles.Count
        RevertedFiles = $reverted
        RevertedPct = Measure-Percent -Part $reverted -Whole $workerFiles.Count
        AdoptionConfidence = if ($workerFiles.Count -gt 0) { 'Medium' } else { 'Low/NoMutationEvidence' }
    }
}

# -----------------------------------------------------------------------------
# 5) Continuation tax proxy.
# -----------------------------------------------------------------------------

# R2 Sol cost before first observed R2 Luna token event is a useful mechanical
# proxy for re-localization / continuation overhead. It is NOT semantic proof.
$r2LunaFirst = (
    $tokenDeltas |
    Where-Object { $_.Round -eq 'R2' -and $_.Model -eq 'gpt-5.6-luna' } |
    Measure-Object -Property Time -Minimum
).Minimum

$r2ContinuationTax = $null
if ($r2LunaFirst) {
    $r2EarlySol = @(
        $tokenDeltas |
        Where-Object {
            $_.Round -eq 'R2' -and
            $_.Model -eq 'gpt-5.6-sol' -and
            $_.Time -lt $r2LunaFirst
        }
    )

    $r2ContinuationTax = if ($r2EarlySol.Count -gt 0) {
        [math]::Round(($r2EarlySol | Measure-Object -Property ActualCost -Sum).Sum, 2)
    }
    else { 0.0 }
}

# -----------------------------------------------------------------------------
# 6) Build report objects.
# -----------------------------------------------------------------------------

$allCostRows = @($costRows + $totalCost)

$result = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    Repo = $Repo
    SessionsRoot = $SessionsRoot
    BaseRef = $BaseRef
    Round1Ref = $Round1Ref
    FinalRef = $FinalRef
    Windows = [pscustomobject]@{
        R1 = "$Round1Start -> $Round1End"
        R2 = "$Round2Start -> $Round2End"
    }
    RelevantSessions = $sessionMeta
    Cost = $allCostRows
    PaidPointsObserved = $pointRows
    DelegationCoverage = $coverageRows
    AdoptionEfficiency = $adoptionRows
    R2ContinuationTaxProxy = $r2ContinuationTax
    Notes = @(
        'ActualCost / SolDisplacement are token-price accounting metrics.',
        'Paid point balance is account-level evidence and is not assumed to equal normalized cost.',
        'DelegationCoverage is a mechanical Git footprint proxy, not semantic workload percentage.',
        'AdoptionEfficiency is a retention/direct-adjustment proxy; MainAdjusted does not mean Worker failure.',
        'R2ContinuationTaxProxy is Sol cost before first observed R2 Luna token event; it is only a relocalization proxy.'
    )
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

# -----------------------------------------------------------------------------
# Markdown report
# -----------------------------------------------------------------------------

$md = New-Object System.Collections.Generic.List[string]
$md.Add('# Codex Agent Switch 0.2.6.1 Economic Audit')
$md.Add('')
$md.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$md.Add('')
$md.Add('## Executive Summary')
$md.Add('')
$md.Add('| Round | Actual Cost | All-Sol Equivalent | Sol Displacement | Saving |')
$md.Add('|---|---:|---:|---:|---:|')
foreach ($r in $allCostRows) {
    $md.Add("| $($r.Round) | $($r.ActualCost) | $($r.AllSolEquivalent) | $($r.SolDisplacementPct)% | $($r.SavingPct)% |")
}
$md.Add('')

$md.Add('## 1. Actual Cost')
$md.Add('')
$md.Add('| Round | Sol Actual | Luna Actual | Actual Total | Observed Paid-Point Decrease | Balance Increase Detected |')
$md.Add('|---|---:|---:|---:|---:|---|')
foreach ($r in @('R1','R2')) {
    $c = $costRows | Where-Object { $_.Round -eq $r }
    $p = $pointRows | Where-Object { $_.Round -eq $r }
    $md.Add("| $r | $($c.SolActual) | $($c.LunaActual) | $($c.ActualCost) | $($p.ObservedDecrease) | $($p.TopupOrStaleBalanceDetected) |")
}
$md.Add('')

$md.Add('## 2. Sol Displacement')
$md.Add('')
$md.Add('| Round | Luna Sol-Equivalent | All-Sol Equivalent | Displacement | Theoretical Saving |')
$md.Add('|---|---:|---:|---:|---:|')
foreach ($r in $allCostRows) {
    $md.Add("| $($r.Round) | $($r.LunaSolEquivalent) | $($r.AllSolEquivalent) | $($r.SolDisplacementPct)% | $($r.SavingPct)% |")
}
$md.Add('')

$md.Add('## 3. Delegation Coverage')
$md.Add('')
$md.Add('Mechanical proxy: Worker-touched files / final changed files, plus the changed-line footprint of those files.')
$md.Add('')
$md.Add('| Round | Dev File Coverage | Dev Line Footprint | Core File Coverage | Core Line Footprint | Confidence |')
$md.Add('|---|---:|---:|---:|---:|---|')
foreach ($r in $coverageRows) {
    $md.Add("| $($r.Round) | $($r.DevFileCoveragePct)% ($($r.WorkerTouchedDevFiles)/$($r.ChangedDevFiles)) | $($r.DevLineCoveragePct)% | $($r.CoreFileCoveragePct)% ($($r.WorkerTouchedCoreFiles)/$($r.ChangedCoreFiles)) | $($r.CoreLineCoveragePct)% | $($r.CoverageConfidence) |")
}
$md.Add('')

$md.Add('## 4. Adoption Efficiency')
$md.Add('')
$md.Add('Mechanical proxy: whether Worker-touched files survive the round diff, and whether Sol later touched the same file.')
$md.Add('')
$md.Add('| Round | Worker Files | Retained | Direct Adopt | Main Adjusted | Reverted | Confidence |')
$md.Add('|---|---:|---:|---:|---:|---:|---|')
foreach ($r in $adoptionRows) {
    $md.Add("| $($r.Round) | $($r.WorkerTouchedFiles) | $($r.RetentionPct)% | $($r.DirectAdoptionPct)% | $($r.MainAdjustedPct)% | $($r.RevertedPct)% | $($r.AdoptionConfidence) |")
}
$md.Add('')

$md.Add('## R2 Continuation Tax Proxy')
$md.Add('')
if ($null -ne $r2ContinuationTax) {
    $md.Add("Sol normalized cost before the first observed R2 Luna token event: **$r2ContinuationTax**.")
}
else {
    $md.Add('Unavailable: no R2 Luna token event was detected.')
}
$md.Add('')
$md.Add('This is only a mechanical relocalization/continuation proxy, not proof that every early Sol token was overhead.')
$md.Add('')

$md.Add('## Method / Limits')
$md.Add('')
$md.Add('- Actual Cost and Sol Displacement use incremental `token_count` deltas, so a single Sol session spanning R1/R2 is split correctly by time.')
$md.Add('- Luna is normalized at 1/5 of Sol pricing; reasoning tokens are already included in output and are not double-charged.')
$md.Add('- Paid-point balances are account-level observations; top-ups or stale balance events are flagged instead of silently treated as usage.')
$md.Add('- Delegation Coverage is a Git/session mutation footprint proxy. It does not claim that one changed line equals one unit of difficulty.')
$md.Add('- Adoption Efficiency distinguishes retained, directly retained, Main-adjusted, and reverted Worker-touched files. Main adjustment is not automatically a failure.')
$md.Add('- No model call is used by this audit.')

$md | Set-Content -LiteralPath $mdPath -Encoding UTF8

# -----------------------------------------------------------------------------
# Console summary
# -----------------------------------------------------------------------------

Write-Host ''
Write-Host '=============================================================='
Write-Host ' Codex Agent Switch 0.2.6.1 ECONOMIC AUDIT'
Write-Host '=============================================================='
Write-Host ''

Write-Host '=== 1 + 2: COST / SOL DISPLACEMENT ==='
$allCostRows | Format-Table Round,SolActual,LunaActual,ActualCost,LunaSolEquivalent,AllSolEquivalent,SolDisplacementPct,SavingPct -AutoSize

Write-Host ''
Write-Host '=== 3: DELEGATION COVERAGE ==='
$coverageRows | Format-Table Round,ChangedDevFiles,WorkerTouchedDevFiles,DevFileCoveragePct,DevLineCoveragePct,ChangedCoreFiles,WorkerTouchedCoreFiles,CoreFileCoveragePct,CoreLineCoveragePct,CoverageConfidence -AutoSize

Write-Host ''
Write-Host '=== 4: ADOPTION EFFICIENCY ==='
$adoptionRows | Format-Table Round,WorkerTouchedFiles,RetentionPct,DirectAdoptionPct,MainAdjustedPct,RevertedPct,AdoptionConfidence -AutoSize

Write-Host ''
Write-Host '=== PAID POINT OBSERVATION ==='
$pointRows | Format-Table -AutoSize

Write-Host ''
Write-Host ("R2 continuation-tax proxy: {0}" -f $(if ($null -ne $r2ContinuationTax) { $r2ContinuationTax } else { 'N/A' }))
Write-Host ''
Write-Host "Markdown report: $mdPath"
Write-Host "JSON report:     $jsonPath"
Write-Host ''
Write-Host 'Done. No Sol/Luna/DeepSeek call was made by this audit.'
