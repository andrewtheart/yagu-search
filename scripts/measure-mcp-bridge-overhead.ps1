<#
.SYNOPSIS
    Phase 0c spike for PLANS/mcpserver-plan.md: measures what a per-call "shell out to Yagu.exe --cli"
    MCP bridge actually costs.

.DESCRIPTION
    Every MCP tool in the planned server is a thin bridge that starts a fresh Yagu.exe. That makes
    process-start overhead a per-CALL tax rather than a one-time cost, and it means a semantic or OCR
    call reloads its model every time (the CLI constructs and disposes its own translator per run).

    This script measures, per scenario: wall-clock latency, the child's peak working set, and the
    system-wide available-memory delta. It also verifies that cancelling a run leaves no orphaned
    worker process behind.

    Run it, then compare the numbers against the acceptance threshold below before deciding whether
    v1 needs a keep-warm host, ships NL/OCR slow, or excludes them.

.PARAMETER Exe
    Yagu.exe to measure. Defaults to the Debug build.

.PARAMETER Iterations
    Measured runs per scenario, after one discarded warm-up run. Default 3.

.PARAMETER SearchRoot
    Directory the ordinary/indexed search scenarios scan.

.PARAMETER IncludeSemantic
    Include the semantic (local AI) scenario. Off by default: it can download and load a model and
    take minutes.

.PARAMETER IncludeOcr
    Include the image-text (OCR) scenario. Off by default for the same reason.

.PARAMETER MeasureResidentModel
    Measure what a KEEP-WARM host would actually deliver. Runs `--semantic-batch`, which drives many
    natural-language queries through a SINGLE resident model, and timestamps each per-query marker on
    stderr. The first query pays the model load; the rest are inference only. That gap is the entire
    value of a keep-warm host, and it cannot be inferred from the per-call numbers above.

.EXAMPLE
    .\scripts\measure-mcp-bridge-overhead.ps1
    .\scripts\measure-mcp-bridge-overhead.ps1 -IncludeSemantic -IncludeOcr -Iterations 5
    .\scripts\measure-mcp-bridge-overhead.ps1 -MeasureResidentModel
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [int]    $Iterations = 3,
    [string] $SearchRoot,
    [string] $Pattern = 'TODO',
    [switch] $IncludeSemantic,
    [switch] $IncludeOcr,
    [switch] $MeasureResidentModel,
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $repoRoot 'src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe'
}
if (-not (Test-Path $Exe)) {
    throw "Yagu.exe not found at '$Exe'. Build it first, or pass -Exe."
}
$Exe = (Resolve-Path $Exe).Path

if (-not $SearchRoot) { $SearchRoot = Join-Path $repoRoot 'src\Yagu' }
if (-not $ReportPath) { $ReportPath = Join-Path $repoRoot 'TestResults\mcp-bridge-overhead.json' }

# ---------------------------------------------------------------------------
# v1 acceptance thresholds. The plan deliberately leaves the keep-warm decision
# open until these are measured; these are the lines the numbers are judged against.
# ---------------------------------------------------------------------------
$Thresholds = [ordered]@{
    # Pure bridge tax: a trivial command that does no real work. Anything above this and EVERY tool
    # call is paying an unacceptable fixed cost, so a keep-warm host is required regardless of feature.
    BridgeOverheadMs      = 400
    # An ordinary bounded text search over a modest scope. This is the common case.
    OrdinarySearchMs      = 2000
    # A model-backed call. Above this a synchronous MCP tool call is not usable interactively.
    ModelBackedMs         = 10000
    # Peak child working set for a bounded page.
    PeakWorkingSetMB      = 1024
}

function New-Measurement {
    param(
        [Parameter(Mandatory)] [string]   $Name,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [string] $Notes = ''
    )

    $samples = @()
    # One discarded warm-up so the reported numbers are not dominated by first-touch page faults
    # and JIT of the CLI path. The COLD cost is reported separately as the first sample.
    for ($i = 0; $i -le $Iterations; $i++) {
        $stdout = [System.IO.Path]::GetTempFileName()
        $stderr = [System.IO.Path]::GetTempFileName()

        $availableBefore = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1KB

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Exe
        foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add($a) }
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $proc = [System.Diagnostics.Process]::Start($psi)
        # Drain BOTH pipes concurrently. Reading one to completion while the other fills would
        # deadlock, which is the same hazard the real bridge has to handle.
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()

        # Poll the working set while the child lives. PeakWorkingSet64 is not readable once a process
        # has exited, so sampling is the only way to get a peak for a short-lived bridge call.
        $peakBytes = 0
        while (-not $proc.WaitForExit(10)) {
            try {
                $proc.Refresh()
                if ($proc.WorkingSet64 -gt $peakBytes) { $peakBytes = $proc.WorkingSet64 }
            } catch { }
        }
        $proc.WaitForExit()
        $sw.Stop()

        $out = $outTask.GetAwaiter().GetResult()
        $err = $errTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText($stdout, $out)
        [System.IO.File]::WriteAllText($stderr, $err)

        $peakMB = [Math]::Round($peakBytes / 1MB, 1)
        $exitCode = $proc.ExitCode
        $proc.Dispose()

        $availableAfter = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1KB

        $records = 0
        $summaryLine = $null
        foreach ($line in ($out -split "`n")) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -eq 0) { continue }
            $records++
            if ($trimmed -like '*"type":"summary"*') { $summaryLine = $trimmed }
        }

        $samples += [pscustomobject]@{
            Iteration        = $i
            IsWarmUp         = ($i -eq 0)
            ElapsedMs        = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            PeakWorkingSetMB = $peakMB
            SystemFreeDeltaMB = [Math]::Round($availableBefore - $availableAfter, 1)
            ExitCode         = $exitCode
            Records          = $records
            Summary          = $summaryLine
            StderrPreview    = ($err -split "`n" | Select-Object -First 2) -join ' | '
        }

        Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    }

    $measured = $samples | Where-Object { -not $_.IsWarmUp }
    $cold = $samples | Where-Object { $_.IsWarmUp } | Select-Object -First 1

    [pscustomobject]@{
        Scenario         = $Name
        Notes            = $Notes
        Arguments        = ($Arguments -join ' ')
        ColdMs           = $cold.ElapsedMs
        MedianWarmMs     = ([Math]::Round((($measured.ElapsedMs | Sort-Object)[[int]([Math]::Floor($measured.Count / 2))]), 1))
        MinWarmMs        = ($measured.ElapsedMs | Measure-Object -Minimum).Minimum
        MaxWarmMs        = ($measured.ElapsedMs | Measure-Object -Maximum).Maximum
        PeakWorkingSetMB = ($samples.PeakWorkingSetMB | Measure-Object -Maximum).Maximum
        ExitCodes        = (($samples.ExitCode | Sort-Object -Unique) -join ',')
        Records          = $cold.Records
        Samples          = $samples
    }
}

function Test-OrphanedWorkers {
    # A cancelled bridge request must not strand a semantic/OCR/index worker holding VRAM or a write
    # lock. Cancelling a read or awaiting exit does NOT terminate a child, which is why the plan
    # requires a kill-on-close job object around the whole process tree.
    $workerNames = @('Yagu.SemanticWorker', 'Yagu.OcrWorker', 'Yagu.IndexWorker')

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    foreach ($a in @('--cli', '--output', 'json', '--non-interactive', '--settings-source', 'global',
                     '--directory', 'C:\', '--pattern', 'zzz-unlikely-token-zzz')) {
        [void]$psi.ArgumentList.Add($a)
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Milliseconds 1500
    $proc.Kill($true)
    $proc.WaitForExit()
    $proc.Dispose()
    Start-Sleep -Milliseconds 500

    $survivors = @()
    foreach ($name in $workerNames) {
        $found = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($found) { $survivors += "$name x$($found.Count)" }
    }
    $strayCli = Get-Process -Name 'Yagu' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Exe }
    if ($strayCli) { $survivors += "Yagu.exe x$($strayCli.Count)" }

    [pscustomobject]@{
        Scenario  = 'cancellation-cleanup'
        Survivors = if ($survivors.Count -gt 0) { $survivors -join ', ' } else { 'none' }
        Clean     = ($survivors.Count -eq 0)
    }
}

function Measure-ResidentModel {
    # --semantic-batch keeps ONE translator (and therefore one loaded model) alive across every query,
    # which is exactly the state a keep-warm host would hold. Timestamping the per-query stderr marker
    # separates the one-off model load from the steady-state inference cost.
    $queries = @(
        'find large pdf reports created since January',
        'config files changed in the last week',
        'source files mentioning a database connection string',
        'log files bigger than 10 MB modified today',
        'markdown notes about installers written this year',
        'test files that reference a timeout'
    )

    $batchFile = Join-Path $env:TEMP 'yagu-semantic-batch.txt'
    Set-Content -Path $batchFile -Value $queries -Encoding UTF8

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    foreach ($a in @('--cli', '--non-interactive', '--settings-source', 'global',
                     '--semantic-batch', $batchFile, '--directory', $SearchRoot)) {
        [void]$psi.ArgumentList.Add($a)
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    Write-Host "Measuring resident-model per-query latency ($($queries.Count) queries, one loaded model)..."

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()

    $marks = @()
    $peakBytes = 0
    while (-not $proc.StandardError.EndOfStream) {
        $line = $proc.StandardError.ReadLine()
        if ($null -eq $line) { break }
        if ($line -match '^\[(\d+)/(\d+)\]') {
            $marks += [pscustomobject]@{ Index = [int]$Matches[1]; AtMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
            Write-Host ("  query {0}/{1} started at {2:N0} ms" -f $Matches[1], $Matches[2], $sw.Elapsed.TotalMilliseconds)
        }
        try {
            $proc.Refresh()
            if ($proc.WorkingSet64 -gt $peakBytes) { $peakBytes = $proc.WorkingSet64 }
        } catch { }
    }

    $proc.WaitForExit()
    $sw.Stop()
    $totalMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
    $exitCode = $proc.ExitCode
    [void]$outTask.GetAwaiter().GetResult()
    $proc.Dispose()

    # Per-query durations: the gap between consecutive start marks, and the tail for the final query.
    $durations = @()
    for ($i = 0; $i -lt $marks.Count; $i++) {
        $end = if ($i + 1 -lt $marks.Count) { $marks[$i + 1].AtMs } else { $totalMs }
        $durations += [pscustomobject]@{
            Query      = $marks[$i].Index
            DurationMs = [Math]::Round($end - $marks[$i].AtMs, 1)
        }
    }

    $steady = $durations | Where-Object { $_.Query -gt 1 }
    $steadyMs = if ($steady) {
        [Math]::Round((($steady.DurationMs | Sort-Object)[[int]([Math]::Floor($steady.Count / 2))]), 1)
    } else { -1 }

    [pscustomobject]@{
        Scenario          = 'semantic (resident model)'
        Queries           = $marks.Count
        TotalMs           = $totalMs
        FirstQueryMs      = if ($durations.Count -gt 0) { $durations[0].DurationMs } else { -1 }
        MedianSteadyMs    = $steadyMs
        MinSteadyMs       = if ($steady) { ($steady.DurationMs | Measure-Object -Minimum).Minimum } else { -1 }
        MaxSteadyMs       = if ($steady) { ($steady.DurationMs | Measure-Object -Maximum).Maximum } else { -1 }
        PeakWorkingSetMB  = [Math]::Round($peakBytes / 1MB, 1)
        ExitCode          = $exitCode
        PerQuery          = $durations
    }
}

Write-Host "Yagu       : $Exe"
Write-Host "Search root: $SearchRoot"
Write-Host "Iterations : $Iterations (plus one discarded warm-up)"
Write-Host ''

$common = @('--cli', '--output', 'json', '--non-interactive', '--settings-source', 'global')

$results = @()
$results += New-Measurement -Name 'bridge-floor (--calc)' -Arguments ($common + @('--calc', '2+2')) `
    -Notes 'Pure process-start tax. Every MCP tool call pays at least this.'
$results += New-Measurement -Name 'read (--read 1-200)' `
    -Arguments ($common + @('--read', (Join-Path $repoRoot 'HELP.md'), '--lines', '1-200')) `
    -Notes 'Cheapest real tool: bounded file window.'
$results += New-Measurement -Name 'index-status' `
    -Arguments ($common + @('--index-status', $SearchRoot)) `
    -Notes 'Metadata-only index command.'
$results += New-Measurement -Name 'search (live scan)' `
    -Arguments ($common + @('--directory', $SearchRoot, '--pattern', $Pattern, '--max-results', '200', '--no-index')) `
    -Notes 'Ordinary bounded text search, index explicitly bypassed.'
$results += New-Measurement -Name 'search (indexed)' `
    -Arguments ($common + @('--directory', $SearchRoot, '--pattern', $Pattern, '--max-results', '200', '--use-index')) `
    -Notes 'Same search allowed to use the content index.'

if ($IncludeSemantic) {
    $results += New-Measurement -Name 'semantic (local AI)' `
        -Arguments ($common + @('--semantic-pattern', "files mentioning $Pattern changed this year",
                                '--directory', $SearchRoot, '--max-results', '50')) `
        -Notes 'Loads and disposes a model PER CALL. This is the keep-warm decision.'
}

if ($IncludeOcr) {
    $results += New-Measurement -Name 'image-text (OCR)' `
        -Arguments ($common + @('--directory', $SearchRoot, '--pattern', $Pattern,
                                '--image-text', '--max-results', '50')) `
        -Notes 'Starts an out-of-process OCR worker per call.'
}

$cleanup = Test-OrphanedWorkers

$resident = $null
if ($MeasureResidentModel) {
    $resident = Measure-ResidentModel
}

Write-Host 'Results'
Write-Host '-------'
$results |
    Select-Object Scenario, ColdMs, MedianWarmMs, MinWarmMs, MaxWarmMs, PeakWorkingSetMB, ExitCodes, Records |
    Format-Table -AutoSize

Write-Host 'Acceptance thresholds'
Write-Host '---------------------'
$verdicts = @()
foreach ($r in $results) {
    $limit = switch -Wildcard ($r.Scenario) {
        'bridge-floor*'  { $Thresholds.BridgeOverheadMs; break }
        'read*'          { $Thresholds.BridgeOverheadMs; break }
        'index-status*'  { $Thresholds.BridgeOverheadMs; break }
        'semantic*'      { $Thresholds.ModelBackedMs; break }
        'image-text*'    { $Thresholds.ModelBackedMs; break }
        default          { $Thresholds.OrdinarySearchMs }
    }
    $verdicts += [pscustomobject]@{
        Scenario    = $r.Scenario
        MedianWarmMs = $r.MedianWarmMs
        LimitMs     = $limit
        Verdict     = if ($r.MedianWarmMs -le $limit) { 'PASS' } else { 'OVER BUDGET' }
        MemoryVerdict = if ($r.PeakWorkingSetMB -le $Thresholds.PeakWorkingSetMB) { 'PASS' } else { 'OVER BUDGET' }
    }
}
$verdicts | Format-Table -AutoSize

Write-Host 'Cancellation cleanup'
Write-Host '--------------------'
Write-Host ("  surviving workers after a killed run: {0} ({1})" -f $cleanup.Survivors, $(if ($cleanup.Clean) { 'PASS' } else { 'LEAK' }))
Write-Host ''

if ($resident) {
    Write-Host 'Resident model (what a keep-warm host delivers)'
    Write-Host '----------------------------------------------'
    $resident | Select-Object Queries, TotalMs, FirstQueryMs, MedianSteadyMs, MinSteadyMs, MaxSteadyMs, PeakWorkingSetMB, ExitCode |
        Format-Table -AutoSize
    Write-Host ''
}

$report = [pscustomobject]@{
    CapturedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    Exe          = $Exe
    SearchRoot   = $SearchRoot
    Iterations   = $Iterations
    Thresholds   = $Thresholds
    Results      = $results
    Verdicts     = $verdicts
    Cleanup      = $cleanup
    ResidentModel = $resident
}

$reportDir = Split-Path -Parent $ReportPath
if (-not (Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $ReportPath -Encoding UTF8
Write-Host "Report written to $ReportPath"
