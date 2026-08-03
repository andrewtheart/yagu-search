#Requires -Version 5.1
<#
.SYNOPSIS
    Coverage gate for the managed content index and all handwritten Rust production code.

.DESCRIPTION
    1. Runs the Yagu.Tests suite with the content-index coverage runsettings (IncludeTestAssembly=true so the
       source-linked index files inside Yagu.Tests.dll are instrumented) and collects a Cobertura report.
    2. Parses every //class node under src/Yagu/Services/Index/**, aggregates per file (partial classes span
       multiple nodes), and enforces the thresholds.
     3. Runs every Rust integration test separately so library objects are not duplicated in the coverage
         report and attribution remains truthful.
     4. Measures the Rust library with all features and requires 100% lines, functions, regions, and real
         nightly-instrumented branches across every handwritten source file.
     5. Prints uncovered lines for any failing file and exits non-zero on any gap, so CI / a phase exit
       cannot waive the gate.

.PARAMETER UseExistingCobertura
    Path to an already-collected coverage.cobertura.xml to parse instead of running the tests again.

.EXAMPLE
    pwsh scripts/check-index-coverage.ps1
#>
[CmdletBinding()]
param(
    [double]$LineThreshold = 1.0,
    [double]$BranchThreshold = 1.0,
    [double]$AllowlistFloor = 0.90,
    [string]$Filter = 'Category!=Slow&Category!=GPU&Category!=Headed',
    [string]$UseExistingCobertura = '',
    [switch]$SkipManaged,
    [string]$RustCoverageToolchain = 'nightly-2026-08-01'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Files whose remaining uncovered branches are P/Invoke Win32-failure catch blocks or out-of-process
# launch error paths. The repo testing convention (see testing.instructions.md) forbids chasing these
# with contrived/flaky tests, so they are held to a documented floor instead of a hard 100%. Every entry
# must have a justification; keep this list SHORT and shrink it as branches become deterministically
# testable (fault injection).
$Allowlist = @{
    'UsnJournalReader.cs'             = 'P/Invoke journal reader; DeviceIoControl/Win32 error paths are defensive.'
    'FileIdentityReader.cs'           = 'P/Invoke file-id reader; Win32 open/read failure paths are defensive.'
    'WindowsJobObject.cs'             = 'P/Invoke job object; Win32-failure catch blocks are defensive.'
    'IndexWorkerClient.cs'            = 'Out-of-process worker launch; process/IO error paths are defensive.'
    'IndexWorkerQuerySource.cs'       = 'Worker query bridge; defensive failure-to-false catch.'
    'ContentIndexRootWatcher.cs'      = 'FileSystemWatcher wrapper; OS watch-failure paths are defensive.'
    'ContentIndexWatcherHintService.cs' = 'Timer/threadpool orchestration; race-only drain branches.'
    'TrigramQueryPlanner.cs'          = 'Recursive-descent regex parser: 99.6% line/93% branch. Residual = 2 required-unreachable arms (the Analyzer switch default the compiler mandates over the closed Node hierarchy; the NodeKind.None safety arm that must live-scan rather than prune-everything) plus defensive end-of-input/lone-surrogate parser edges the repo testing convention says not to chase.'
    'TrigramExpression.cs'            = 'Residual branches are compiler-mandated unreachable arms over the closed NodeKind hierarchy: the Evaluate switch default, and the Combine result.Count==0 guard that And/Or can never produce (Flatten always yields >=1 kept child). Not chased with reflection-constructed invalid nodes.'
    'TrigramQueryRpn.cs'              = 'Residual is the EncodeNode switch default (fail-open OpAll) the compiler mandates over the closed NodeKind hierarchy; unreachable because every constructed node has a valid Kind.'
    'TrigramPostingIndex.cs'         = 'Residual is the Evaluate switch default the compiler mandates over the closed NodeKind hierarchy; unreachable because every constructed query node has a valid Kind.'
    'IndexTrustDecision.cs'          = 'Residual is the DecidePath switch default the compiler mandates over the IndexPathClassification closed record hierarchy (private ctor); no external subtype can reach it.'
    'BoundedSynchronousIo.cs'        = 'Bounded synchronous I/O lane over CancelSynchronousIo P/Invoke; residual branches are OpenThread/SafeWaitHandle teardown-race ObjectDisposedException guards, the non-Windows guard, and thread-join-timeout paths that cannot be forced deterministically.'
    'BoundedIncrementalFileClassifier.cs' = 'Bounded incremental file-read lane over CancelSynchronousIo P/Invoke; residual branches are the SafeWaitHandle disposal-race ObjectDisposedException guard, ReadRequest/lane double-dispose and abandon-race returns, and the non-Windows guard that cannot be forced deterministically.'
}

function Write-Section($text) {
    Write-Host ''
    Write-Host "==== $text ====" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 1. Collect managed coverage
# ---------------------------------------------------------------------------
$coberturaPath = $UseExistingCobertura
if (-not $SkipManaged -and -not $coberturaPath) {
    $runsettings = Join-Path $repoRoot 'tests/Yagu.Tests/content-index-coverage.runsettings'
    if (-not (Test-Path $runsettings)) { throw "Runsettings not found: $runsettings" }

    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $resultsDir = Join-Path $repoRoot "TestResults/IndexCoverage/$stamp"
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

    Write-Section "Collecting coverage (filter: $Filter)"
    Write-Host "Results dir: $resultsDir"
    $proj = Join-Path $repoRoot 'tests/Yagu.Tests/Yagu.Tests.csproj'
    & dotnet test $proj -c Debug --filter $Filter `
        --collect:"XPlat Code Coverage" `
        --settings $runsettings `
        --results-directory $resultsDir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit $LASTEXITCODE) before coverage could be parsed." }

    $found = Get-ChildItem -Path $resultsDir -Recurse -Filter 'coverage.cobertura.xml' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $found) { throw "No coverage.cobertura.xml produced under $resultsDir." }
    $coberturaPath = $found.FullName
}

$managedFailures = @()
$managedRows = @()

if ($coberturaPath) {
    Write-Section "Managed index coverage ($([System.IO.Path]::GetFileName($coberturaPath)))"
    [xml]$cov = Get-Content -Path $coberturaPath -Raw

    # Group index //class nodes by source file (a partial class spans several class nodes).
    $byFile = @{}
    foreach ($cls in $cov.SelectNodes('//class')) {
        $fn = [string]$cls.filename
        if (-not $fn) { continue }
        $norm = $fn -replace '\\', '/'
        if ($norm -notmatch '/Services/Index/') { continue }
        if ($norm -match '(?i)Tests?\.cs$') { continue }
        $leaf = [System.IO.Path]::GetFileName($norm)
        if (-not $byFile.ContainsKey($leaf)) { $byFile[$leaf] = [System.Collections.Generic.List[object]]::new() }
        $byFile[$leaf].Add($cls)
    }

    foreach ($leaf in ($byFile.Keys | Sort-Object)) {
        $lineTotal = 0; $lineHit = 0; $brTotal = 0; $brHit = 0
        $uncovered = [System.Collections.Generic.List[int]]::new()
        foreach ($cls in $byFile[$leaf]) {
            foreach ($ln in $cls.SelectNodes('.//line')) {
                $lineTotal++
                $hits = [int]$ln.hits
                if ($hits -gt 0) { $lineHit++ } else { $uncovered.Add([int]$ln.number) }
                if ([string]$ln.branch -eq 'true') {
                    # condition-coverage looks like "50% (1/2)"
                    $cc = [string]$ln.'condition-coverage'
                    if ($cc -match '\((\d+)/(\d+)\)') {
                        $brHit += [int]$Matches[1]
                        $brTotal += [int]$Matches[2]
                    }
                }
            }
        }
        $lineRate = if ($lineTotal -gt 0) { $lineHit / $lineTotal } else { 1.0 }
        $branchRate = if ($brTotal -gt 0) { $brHit / $brTotal } else { 1.0 }

        $isAllow = $Allowlist.ContainsKey($leaf)
        $lineFloor = if ($isAllow) { $AllowlistFloor } else { $LineThreshold }
        $branchFloor = if ($isAllow) { $AllowlistFloor } else { $BranchThreshold }
        $pass = ($lineRate -ge $lineFloor) -and ($branchRate -ge $branchFloor)

        $managedRows += [pscustomobject]@{
            File       = $leaf
            Line       = '{0,6:P1}' -f $lineRate
            Branch     = '{0,6:P1}' -f $branchRate
            Status     = if (-not $pass) { 'FAIL' } elseif ($isAllow) { 'ALLOW' } else { 'OK' }
            Uncovered  = ($uncovered | Sort-Object) -join ','
        }
        if (-not $pass) {
            $managedFailures += [pscustomobject]@{ File = $leaf; LineRate = $lineRate; BranchRate = $branchRate; Uncovered = ($uncovered | Sort-Object); Allow = $isAllow }
        }
    }

    $managedRows | Sort-Object Status, File | Format-Table -AutoSize | Out-Host
    Write-Host ("Index files measured: {0} | allowlisted: {1} | failing: {2}" -f `
        $managedRows.Count, ($managedRows | Where-Object Status -eq 'ALLOW').Count, $managedFailures.Count)

    foreach ($f in $managedFailures) {
        $why = if ($f.Allow) { "below allowlist floor $AllowlistFloor" } else { "below required 100%" }
        Write-Host ("  FAIL {0}: line={1:P2} branch={2:P2} ({3})" -f $f.File, $f.LineRate, $f.BranchRate, $why) -ForegroundColor Red
        if ($f.Uncovered) { Write-Host ("       uncovered lines: {0}" -f (($f.Uncovered) -join ',')) -ForegroundColor DarkYellow }
    }
}

# ---------------------------------------------------------------------------
# 2. Rust integration execution and library coverage
# ---------------------------------------------------------------------------
$rustFailure = $false
Write-Section 'Rust integration tests (separate attribution gate)'
$core = Join-Path $repoRoot 'src/yagu-core'
Push-Location $core
try {
    & cargo test --all-features --tests --no-fail-fast | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Rust integration tests failed (exit $LASTEXITCODE)." }

    Write-Section "Rust coverage ($RustCoverageToolchain, all features)"
    & rustup run $RustCoverageToolchain cargo llvm-cov --version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "cargo-llvm-cov is unavailable under $RustCoverageToolchain. Install it for that toolchain before running the gate."
    }

    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $rustResultsDir = Join-Path $repoRoot "TestResults/RustCoverage/$stamp"
    New-Item -ItemType Directory -Path $rustResultsDir -Force | Out-Null
    $rustJsonPath = Join-Path $rustResultsDir 'coverage.json'

    # cargo-llvm-cov 0.8.5 can discover a stale stable-toolchain executable on Windows, or miss the
    # nightly executable after Cargo relocates it under debug/build. Start from an empty target and
    # use cargo-llvm-cov only for instrumentation/execution; the pinned nightly's LLVM tools then
    # merge and report against the one freshly built test executable.
    $rustCoverageTarget = Join-Path $core 'target/llvm-cov-target'
    $expectedCoverageTarget = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'src/yagu-core/target/llvm-cov-target'))
    if ([System.IO.Path]::GetFullPath($rustCoverageTarget) -ne $expectedCoverageTarget) {
        throw "Refusing to clean unexpected Rust coverage target: $rustCoverageTarget"
    }
    if (Test-Path $rustCoverageTarget) {
        Remove-Item -LiteralPath $rustCoverageTarget -Recurse -Force
    }

    & rustup run $RustCoverageToolchain cargo llvm-cov --lib --all-features --branch --no-report | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Rust coverage execution failed (exit $LASTEXITCODE)." }

    $rustSysroot = (& rustup run $RustCoverageToolchain rustc --print sysroot | Select-Object -First 1).Trim()
    $rustHostLine = & rustup run $RustCoverageToolchain rustc -vV |
        Where-Object { $_ -match '^host:\s+' } |
        Select-Object -First 1
    if (-not $rustSysroot -or -not $rustHostLine) {
        throw "Could not resolve sysroot/host for Rust toolchain $RustCoverageToolchain."
    }
    $rustHost = ($rustHostLine -replace '^host:\s+', '').Trim()
    $llvmToolsDir = Join-Path $rustSysroot "lib/rustlib/$rustHost/bin"
    $llvmProfdata = Join-Path $llvmToolsDir 'llvm-profdata.exe'
    $llvmCov = Join-Path $llvmToolsDir 'llvm-cov.exe'
    if (-not (Test-Path $llvmProfdata) -or -not (Test-Path $llvmCov)) {
        throw "Pinned LLVM tools are missing under $llvmToolsDir. Install llvm-tools-preview for $RustCoverageToolchain."
    }

    $rawProfiles = @(Get-ChildItem -Path $rustCoverageTarget -Recurse -Filter '*.profraw' -ErrorAction SilentlyContinue)
    if ($rawProfiles.Count -eq 0) { throw "No Rust .profraw files found under $rustCoverageTarget." }
    $rustObjects = @(Get-ChildItem -Path $rustCoverageTarget -Recurse -Filter 'yagu_core-*.exe' -ErrorAction SilentlyContinue)
    if ($rustObjects.Count -ne 1) {
        $objectPaths = ($rustObjects | ForEach-Object FullName) -join ', '
        throw "Expected exactly one freshly built Rust unit-test executable; found $($rustObjects.Count): $objectPaths"
    }

    $rustProfdataPath = Join-Path $rustResultsDir 'coverage.profdata'
    & $llvmProfdata merge -sparse $rawProfiles.FullName -o $rustProfdataPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "llvm-profdata merge failed (exit $LASTEXITCODE)." }

    $llvmCovErrors = Join-Path $rustResultsDir 'llvm-cov.stderr.txt'
    $rustJson = & $llvmCov export --summary-only "--instr-profile=$rustProfdataPath" `
        $rustObjects[0].FullName 2> $llvmCovErrors
    if ($LASTEXITCODE -ne 0) {
        $details = if (Test-Path $llvmCovErrors) { Get-Content -Path $llvmCovErrors -Raw } else { '' }
        throw "llvm-cov export failed (exit $LASTEXITCODE). $details"
    }
    [System.IO.File]::WriteAllText(
        $rustJsonPath,
        ($rustJson -join [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    if (-not (Test-Path $rustJsonPath)) { throw "Rust coverage report not found: $rustJsonPath" }

    $rustReport = Get-Content -Path $rustJsonPath -Raw | ConvertFrom-Json
    if (-not $rustReport.data -or $rustReport.data.Count -ne 1) {
        throw 'Rust coverage report does not contain exactly one LLVM coverage data set.'
    }

    $rustRows = @()
    foreach ($file in $rustReport.data[0].files) {
        $path = [string]$file.filename
        $normalized = $path -replace '\\', '/'
        if ($normalized -notmatch '/src/yagu-core/src/[^/]+\.rs$') { continue }

        $summary = $file.summary
        $metrics = @{
            Lines     = $summary.lines
            Functions = $summary.functions
            Regions   = $summary.regions
            Branches  = $summary.branches
        }
        $metricFailures = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $metrics.GetEnumerator()) {
            $count = [int64]$entry.Value.count
            $covered = [int64]$entry.Value.covered
            if ($count -le 0) {
                $metricFailures.Add("$($entry.Key)=0/0")
            }
            elseif ($covered -ne $count) {
                $metricFailures.Add("$($entry.Key)=$covered/$count")
            }
        }

        $rustRows += [pscustomobject]@{
            File      = [System.IO.Path]::GetFileName($path)
            Lines     = "$($summary.lines.covered)/$($summary.lines.count)"
            Functions = "$($summary.functions.covered)/$($summary.functions.count)"
            Regions   = "$($summary.regions.covered)/$($summary.regions.count)"
            Branches  = "$($summary.branches.covered)/$($summary.branches.count)"
            Status    = if ($metricFailures.Count -eq 0) { 'OK' } else { 'FAIL' }
            Missing   = $metricFailures -join ', '
        }
        if ($metricFailures.Count -gt 0) { $rustFailure = $true }
    }

    if ($rustRows.Count -eq 0) { throw 'Rust coverage report contained no handwritten src/yagu-core/src/*.rs files.' }
    $rustRows | Sort-Object Status, File | Format-Table -AutoSize | Out-Host
    foreach ($row in ($rustRows | Where-Object Status -eq 'FAIL')) {
        Write-Host ("  FAIL {0}: {1}" -f $row.File, $row.Missing) -ForegroundColor Red
    }
}
finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# 3. Verdict
# ---------------------------------------------------------------------------
Write-Section 'Verdict'
$failed = ($managedFailures.Count -gt 0) -or $rustFailure
if ($failed) {
    Write-Host 'INDEX COVERAGE GATE: FAIL' -ForegroundColor Red
    exit 1
}
Write-Host 'INDEX COVERAGE GATE: PASS' -ForegroundColor Green
exit 0
