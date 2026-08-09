#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$SkipBuild,
    [switch]$SkipManaged,
    [switch]$SkipBenchmarks,
    [switch]$SkipRust,
    [switch]$AllowRunningYagu,
    [switch]$AllowPartial,
    [int]$SemanticTimeoutMinutes = 180,
    [string]$RustCoverageToolchain = 'nightly-2026-08-01',
    [string[]]$ExistingManagedJson = @(),
    [string[]]$ExistingRustJson = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runsettings = Join-Path $repoRoot 'tests/Yagu.Tests/workspace-coverage.runsettings'
$analyzer = Join-Path $repoRoot 'scripts/analyze_coverage.py'

if (-not $OutputDirectory) {
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $OutputDirectory = Join-Path $repoRoot "TestResults/WorkspaceCoverage/$stamp"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$statusPath = Join-Path $OutputDirectory 'runner-status.json'

$status = [ordered]@{
    started_utc = [DateTime]::UtcNow.ToString('o')
    completed_utc = $null
    output_directory = $OutputDirectory
    authoritative = $true
    preflight = [ordered]@{}
    runners = [ordered]@{}
    analysis = [ordered]@{ status = 'pending'; exit_code = $null }
}
$managedReports = [System.Collections.Generic.List[string]]::new()
$rustReports = [System.Collections.Generic.List[string]]::new()

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host "==== $Text ====" -ForegroundColor Cyan
}

function Save-Status {
    $json = $status | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($statusPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Assert-Command([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) { throw "Required command was not found: $Name" }
    return $command.Source
}

function Invoke-LoggedNative {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$LogPath
    )

    & $FilePath @ArgumentList 2>&1 | Tee-Object -FilePath $LogPath | Out-Host
    return $LASTEXITCODE
}

function Find-CoverageArtifact([string]$Directory, [string]$Name) {
    $found = Get-ChildItem -Path $Directory -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

function Get-TestOutcome([string]$TrxPath, [string]$NamePattern) {
    if (-not $TrxPath -or -not (Test-Path $TrxPath)) { return 'Missing' }
    [xml]$trx = Get-Content -Path $TrxPath -Raw
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") |
        Where-Object { [string]$_.testName -like "*$NamePattern*" })
    if ($results.Count -eq 0) { return 'Missing' }
    if (@($results | Where-Object { [string]$_.outcome -eq 'Failed' }).Count -gt 0) { return 'Failed' }
    if (@($results | Where-Object { [string]$_.outcome -ne 'Passed' }).Count -eq 0) { return 'Passed' }
    return [string]$results[0].outcome
}

function Invoke-ManagedCoverageRun {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$ResultsDirectory,
        [Parameter(Mandatory)][string]$LogPath,
        [string[]]$AdditionalArguments = @()
    )

    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    $trxName = "$Name.trx"
    $arguments = @(
        'test', $Project,
        '-c', 'Debug',
        '--collect:XPlat Code Coverage',
        '--settings', $runsettings,
        '--results-directory', $ResultsDirectory,
        "--logger:trx;LogFileName=$trxName",
        '--nologo'
    ) + $AdditionalArguments
    $exitCode = Invoke-LoggedNative -FilePath 'dotnet' -ArgumentList $arguments -LogPath $LogPath
    $coverageJson = Find-CoverageArtifact -Directory $ResultsDirectory -Name 'coverage.json'
    $coverageXml = Find-CoverageArtifact -Directory $ResultsDirectory -Name 'coverage.cobertura.xml'
    $trx = Find-CoverageArtifact -Directory $ResultsDirectory -Name $trxName
    if ($coverageJson) { $managedReports.Add($coverageJson) }
    $result = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
    if (-not $coverageJson) { $result = 'incomplete-no-coverage' }
    $status.runners[$Name] = [ordered]@{
        status = $result
        exit_code = $exitCode
        coverage_json = $coverageJson
        cobertura = $coverageXml
        trx = $trx
        log = $LogPath
    }
    if ($exitCode -ne 0 -or -not $coverageJson) { $status.authoritative = $false }
    Save-Status
    return $trx
}

function Invoke-ManagedTestRun {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$ResultsDirectory,
        [Parameter(Mandatory)][string]$LogPath,
        [string[]]$AdditionalArguments = @()
    )

    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    $trxName = "$Name.trx"
    $arguments = @(
        'test', $Project,
        '-c', 'Debug',
        '--results-directory', $ResultsDirectory,
        "--logger:trx;LogFileName=$trxName",
        '--nologo'
    ) + $AdditionalArguments
    $exitCode = Invoke-LoggedNative -FilePath 'dotnet' -ArgumentList $arguments -LogPath $LogPath
    $trx = Find-CoverageArtifact -Directory $ResultsDirectory -Name $trxName
    $status.runners[$Name] = [ordered]@{
        status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
        exit_code = $exitCode
        trx = $trx
        log = $LogPath
    }
    if ($exitCode -ne 0 -or -not $trx) { $status.authoritative = $false }
    Save-Status
    return $trx
}

function Invoke-RustCoverage {
    $runner = [ordered]@{
        status = 'running'
        test_exit_code = $null
        coverage_exit_code = $null
        coverage_json = $null
        test_log = Join-Path $OutputDirectory 'rust-tests.log'
        coverage_log = Join-Path $OutputDirectory 'rust-coverage.log'
    }
    $status.runners.rust = $runner
    Save-Status

    $core = Join-Path $repoRoot 'src/yagu-core'
    Push-Location $core
    try {
        $runner.test_exit_code = Invoke-LoggedNative -FilePath 'cargo' -ArgumentList @(
            'test', '--all-features', '--no-fail-fast'
        ) -LogPath $runner.test_log
        if ($runner.test_exit_code -ne 0) { $status.authoritative = $false }

        $toolCheckLog = Join-Path $OutputDirectory 'rust-toolchain-check.log'
        $toolExit = Invoke-LoggedNative -FilePath 'rustup' -ArgumentList @(
            'run', $RustCoverageToolchain, 'cargo', 'llvm-cov', '--version'
        ) -LogPath $toolCheckLog
        if ($toolExit -ne 0) {
            throw "cargo-llvm-cov is unavailable under $RustCoverageToolchain."
        }

        $coverageTarget = Join-Path $core 'target/llvm-cov-target'
        $expectedTarget = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'src/yagu-core/target/llvm-cov-target'))
        if ([System.IO.Path]::GetFullPath($coverageTarget) -ne $expectedTarget) {
            throw "Refusing to clean unexpected Rust coverage target: $coverageTarget"
        }
        if (Test-Path $coverageTarget) {
            Remove-Item -LiteralPath $coverageTarget -Recurse -Force
        }

        $runner.coverage_exit_code = Invoke-LoggedNative -FilePath 'rustup' -ArgumentList @(
            'run', $RustCoverageToolchain, 'cargo', 'llvm-cov',
            '--lib', '--all-features', '--branch', '--no-report'
        ) -LogPath $runner.coverage_log
        if ($runner.coverage_exit_code -ne 0) {
            throw "Rust coverage execution failed with exit code $($runner.coverage_exit_code)."
        }

        $sysroot = (& rustup run $RustCoverageToolchain rustc --print sysroot | Select-Object -First 1).Trim()
        $hostLine = & rustup run $RustCoverageToolchain rustc -vV |
            Where-Object { $_ -match '^host:\s+' } |
            Select-Object -First 1
        if (-not $sysroot -or -not $hostLine) {
            throw "Could not resolve LLVM tools for $RustCoverageToolchain."
        }
        $hostName = ($hostLine -replace '^host:\s+', '').Trim()
        $llvmTools = Join-Path $sysroot "lib/rustlib/$hostName/bin"
        $llvmProfdata = Join-Path $llvmTools 'llvm-profdata.exe'
        $llvmCov = Join-Path $llvmTools 'llvm-cov.exe'
        if (-not (Test-Path $llvmProfdata) -or -not (Test-Path $llvmCov)) {
            throw "llvm-tools-preview is missing under $llvmTools."
        }

        $rawProfiles = @(Get-ChildItem -Path $coverageTarget -Recurse -Filter '*.profraw' -File)
        $objects = @(Get-ChildItem -Path $coverageTarget -Recurse -Filter 'yagu_core-*.exe' -File)
        if ($rawProfiles.Count -eq 0) { throw "No Rust .profraw files found under $coverageTarget." }
        if ($objects.Count -ne 1) {
            throw "Expected one Rust unit-test executable under $coverageTarget; found $($objects.Count)."
        }

        $profdata = Join-Path $OutputDirectory 'rust-coverage.profdata'
        & $llvmProfdata merge -sparse $rawProfiles.FullName -o $profdata | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "llvm-profdata merge failed with exit code $LASTEXITCODE." }

        $rustJson = Join-Path $OutputDirectory 'rust-coverage.json'
        $rustErrors = Join-Path $OutputDirectory 'rust-llvm-cov.stderr.log'
        $jsonLines = & $llvmCov export "--instr-profile=$profdata" $objects[0].FullName 2> $rustErrors
        if ($LASTEXITCODE -ne 0) {
            throw "llvm-cov export failed with exit code $LASTEXITCODE."
        }
        [System.IO.File]::WriteAllText(
            $rustJson,
            ($jsonLines -join [Environment]::NewLine),
            [System.Text.UTF8Encoding]::new($false))
        $rustReports.Add($rustJson)
        $runner.coverage_json = $rustJson
        $runner.status = if ($runner.test_exit_code -eq 0) { 'passed' } else { 'tests-failed' }
    }
    catch {
        $runner.status = 'failed'
        $runner.error = $_.Exception.Message
        $status.authoritative = $false
        Write-Warning $_.Exception.Message
    }
    finally {
        Pop-Location
        Save-Status
    }
}

Write-Section 'Preflight'
$status.preflight.dotnet = Assert-Command 'dotnet'
$status.preflight.python = Assert-Command 'python'
if (-not $SkipRust) {
    $status.preflight.cargo = Assert-Command 'cargo'
    $status.preflight.rustup = Assert-Command 'rustup'
}
if (-not (Test-Path $runsettings)) { throw "Runsettings not found: $runsettings" }
if (-not (Test-Path $analyzer)) { throw "Coverage analyzer not found: $analyzer" }
$status.preflight.user_interactive = [Environment]::UserInteractive
$golden = Join-Path $repoRoot 'tests/Yagu.Tests/TestData/SemanticEval/expected-plans.json'
$status.preflight.semantic_golden_exists = Test-Path $golden
$status.preflight.semantic_timeout_minutes = $SemanticTimeoutMinutes
$status.preflight.foundry_cli_available = [bool](Get-Command foundry -ErrorAction SilentlyContinue)

$runningYagu = @(
    Get-Process -Name Yagu -ErrorAction SilentlyContinue |
        ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = '<unavailable>' }
            [ordered]@{ id = $_.Id; path = $path; title = $_.MainWindowTitle }
        }
)
$status.preflight.running_yagu = $runningYagu
if ($runningYagu.Count -gt 0 -and -not $AllowRunningYagu) {
    Save-Status
    $details = ($runningYagu | ForEach-Object { "PID $($_.id): $($_.path)" }) -join [Environment]::NewLine
    throw "Running Yagu instances would invalidate filename/headed tests. Close them or explicitly use -AllowRunningYagu:`n$details"
}
if ($runningYagu.Count -gt 0) { $status.authoritative = $false }
foreach ($existing in $ExistingManagedJson) {
    $resolved = (Resolve-Path $existing).Path
    $managedReports.Add($resolved)
}
foreach ($existing in $ExistingRustJson) {
    $resolved = (Resolve-Path $existing).Path
    $rustReports.Add($resolved)
}
Save-Status

if ($SkipBuild) {
    $status.runners.build = [ordered]@{ status = 'skipped' }
    $status.authoritative = $false
}
else {
    Write-Section 'Debug app build'
    $buildLog = Join-Path $OutputDirectory 'build.log'
    $buildExit = Invoke-LoggedNative -FilePath 'dotnet' -ArgumentList @(
        'build', (Join-Path $repoRoot 'src/Yagu/Yagu.csproj'),
        '-c', 'Debug',
        '-p:RustProfile=profiling',
        '-p:SkipYaguVersionIncrement=true',
        '--nologo'
    ) -LogPath $buildLog
    $status.runners.build = [ordered]@{ status = if ($buildExit -eq 0) { 'passed' } else { 'failed' }; exit_code = $buildExit; log = $buildLog }
    if ($buildExit -ne 0) {
        $status.authoritative = $false
        Save-Status
        throw "Debug app build failed with exit code $buildExit."
    }
    Save-Status
}

$oldMatchNav = [Environment]::GetEnvironmentVariable('YAGU_RUN_UI_REGRESSION', 'Process')
$oldSemanticTimeout = [Environment]::GetEnvironmentVariable('YAGU_SEMANTIC_GOLDEN_TIMEOUT_MIN', 'Process')
$oldBenchmarkCoverageMode = [Environment]::GetEnvironmentVariable('YAGU_BENCHMARK_COVERAGE_MODE', 'Process')
try {
    $env:YAGU_RUN_UI_REGRESSION = '1'
    $env:YAGU_SEMANTIC_GOLDEN_TIMEOUT_MIN = [string]$SemanticTimeoutMinutes

    if ($SkipManaged) {
        $status.runners.yagu_tests = [ordered]@{ status = 'skipped' }
        $status.authoritative = $false
    }
    else {
        Write-Section 'Yagu.Tests full coverage'
        $testResults = Join-Path $OutputDirectory 'managed/Yagu.Tests'
        $trx = Invoke-ManagedCoverageRun `
            -Name 'Yagu.Tests' `
            -Project (Join-Path $repoRoot 'tests/Yagu.Tests/Yagu.Tests.csproj') `
            -ResultsDirectory $testResults `
            -LogPath (Join-Path $OutputDirectory 'Yagu.Tests.log') `
            -AdditionalArguments @('-p:RustProfile=profiling')
        $special = [ordered]@{
            semantic_golden = Get-TestOutcome -TrxPath $trx -NamePattern 'SemanticPlans_MatchGolden'
            match_nav = Get-TestOutcome -TrxPath $trx -NamePattern 'MatchNav_DiverseCorpus_PaginatesAndBoxesOnlyTheActiveTerm'
            multiline_gui = Get-TestOutcome -TrxPath $trx -NamePattern 'MultilineToggles_BehaveCorrectlyInTheGui'
        }
        $status.runners.'Yagu.Tests'.special_tests = $special
        if ($special.semantic_golden -ne 'Passed' -or
            $special.match_nav -ne 'Passed' -or
            $special.multiline_gui -ne 'Passed') {
            $status.authoritative = $false
        }
        Save-Status
    }

    if ($SkipBenchmarks) {
        $status.runners.'Yagu.Benchmarks.Validation' = [ordered]@{ status = 'skipped' }
        $status.runners.'Yagu.Benchmarks' = [ordered]@{ status = 'skipped' }
        $status.authoritative = $false
    }
    else {
        Write-Section 'Yagu.Benchmarks uninstrumented validation'
        $env:YAGU_BENCHMARK_COVERAGE_MODE = $null
        Invoke-ManagedTestRun `
            -Name 'Yagu.Benchmarks.Validation' `
            -Project (Join-Path $repoRoot 'tests/Yagu.Benchmarks/Yagu.Benchmarks.csproj') `
            -ResultsDirectory (Join-Path $OutputDirectory 'validation/Yagu.Benchmarks') `
            -LogPath (Join-Path $OutputDirectory 'Yagu.Benchmarks.Validation.log') | Out-Null

        Write-Section 'Yagu.Benchmarks full coverage'
        $env:YAGU_BENCHMARK_COVERAGE_MODE = '1'
        Invoke-ManagedCoverageRun `
            -Name 'Yagu.Benchmarks' `
            -Project (Join-Path $repoRoot 'tests/Yagu.Benchmarks/Yagu.Benchmarks.csproj') `
            -ResultsDirectory (Join-Path $OutputDirectory 'managed/Yagu.Benchmarks') `
            -LogPath (Join-Path $OutputDirectory 'Yagu.Benchmarks.log') | Out-Null
    }
}
finally {
    [Environment]::SetEnvironmentVariable('YAGU_RUN_UI_REGRESSION', $oldMatchNav, 'Process')
    [Environment]::SetEnvironmentVariable('YAGU_SEMANTIC_GOLDEN_TIMEOUT_MIN', $oldSemanticTimeout, 'Process')
    [Environment]::SetEnvironmentVariable('YAGU_BENCHMARK_COVERAGE_MODE', $oldBenchmarkCoverageMode, 'Process')
}

if ($SkipRust) {
    $status.runners.rust = [ordered]@{ status = 'skipped' }
    $status.authoritative = $false
    Save-Status
}
else {
    Write-Section 'Rust tests and coverage'
    Invoke-RustCoverage
}

Write-Section 'Aggregate reports'
Save-Status
$analysisArguments = @(
    $analyzer,
    '--repo-root', $repoRoot,
    '--output-dir', $OutputDirectory,
    '--status-json', $statusPath
)
foreach ($report in $managedReports) { $analysisArguments += @('--managed-json', $report) }
foreach ($report in $rustReports) { $analysisArguments += @('--rust-json', $report) }
$analysisExit = Invoke-LoggedNative -FilePath 'python' -ArgumentList $analysisArguments -LogPath (Join-Path $OutputDirectory 'coverage-analysis.log')
$status.analysis.status = if ($analysisExit -eq 0) { 'passed' } else { 'failed' }
$status.analysis.exit_code = $analysisExit
if ($analysisExit -ne 0) { $status.authoritative = $false }
$status.completed_utc = [DateTime]::UtcNow.ToString('o')
Save-Status

Write-Section 'Result'
Write-Host "Coverage report: $(Join-Path $OutputDirectory 'coverage-summary.md')"
Write-Host "Authoritative: $($status.authoritative)"
if (-not $status.authoritative -and -not $AllowPartial) { exit 1 }
if ($analysisExit -ne 0) { exit $analysisExit }
exit 0