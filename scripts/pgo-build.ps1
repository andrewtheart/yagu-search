<#
.SYNOPSIS
    Builds yagu-core (Rust) with two-phase Profile-Guided Optimization (PGO).

.DESCRIPTION
    Phase 1 (instrument): builds yagu_core.dll with `-C profile-generate=<dir>`,
    drops it into the Yagu .NET host output, and runs a representative training
    workload via `Yagu.exe --cli` so the instrumented DLL writes `.profraw`
    files.

    Phase 2 (use): merges the raw profiles with `llvm-profdata.exe` and rebuilds
    yagu_core.dll with `-C profile-use=<merged.profdata>`. The final DLL ends
    up at <repo>\src\yagu-core\target\release\yagu_core.dll, which Yagu.csproj
    already copies to the host output on the next normal build.

.NOTES
    Requirements:
      - Rust stable with `llvm-tools` component (rustup component add llvm-tools).
      - .NET 10 SDK.
      - At least one directory with a few thousand text files for training.

    Cargo gotcha: setting the RUSTFLAGS env var REPLACES (not merges with)
    `build.rustflags` from `.cargo/config.toml`, so this script always
    re-includes `-C target-cpu=x86-64-v2` to keep the v2 baseline.

    BOLT note: LLVM BOLT is the natural follow-on after PGO, but its Windows
    PE support is still experimental as of LLVM 19. We skip BOLT here. If you
    later want to try it, run BOLT against the *PGO-optimized* yagu_core.dll
    on Linux/WSL (cross-target the Windows DLL) or wait for upstream Windows
    support to mature.

.PARAMETER TrainingDir
    Directory the instrumented binary scans during the training run.
    Defaults to the Yagu repo root, which is small/fast and exercises the
    common code paths (text files of varied sizes, some binary skips, mmap).
    For a more realistic profile, point it at C:\ or a large source tree.

.PARAMETER SkipTraining
    If set, assumes .profraw files are already present in $ProfileDir and
    only runs the merge + phase-2 build. Useful for re-running PGO with new
    rustc versions without re-collecting.

.EXAMPLE
    .\scripts\pgo-build.ps1
    .\scripts\pgo-build.ps1 -TrainingDir 'C:\src\myapp'
#>
[CmdletBinding()]
param(
    [string]$TrainingDir,
    [switch]$SkipTraining
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$RepoRoot     = Split-Path -Parent $PSScriptRoot
$RustCoreDir  = Join-Path $RepoRoot 'src\yagu-core'
$ProfileDir   = Join-Path $RustCoreDir 'target\pgo-profiles'
$MergedFile   = Join-Path $RustCoreDir 'target\merged.profdata'
$RustDll      = Join-Path $RustCoreDir 'target\release\yagu_core.dll'
$YaguCsproj   = Join-Path $RepoRoot 'src\Yagu\Yagu.csproj'
$YaguOutDir   = Join-Path $RepoRoot 'src\Yagu\bin\x64\Release\net10.0-windows10.0.19041.0'
$YaguExe      = Join-Path $YaguOutDir 'Yagu.exe'
$HostedDll    = Join-Path $YaguOutDir 'yagu_core.dll'

if (-not $TrainingDir) { $TrainingDir = $RepoRoot }
if (-not (Test-Path $TrainingDir)) {
    throw "Training dir not found: $TrainingDir"
}

# Locate llvm-profdata from the rustc llvm-tools component.
$ToolchainBin = Join-Path $env:USERPROFILE '.rustup\toolchains\stable-x86_64-pc-windows-msvc\lib\rustlib\x86_64-pc-windows-msvc\bin'
$LlvmProfdata = Join-Path $ToolchainBin 'llvm-profdata.exe'
if (-not (Test-Path $LlvmProfdata)) {
    throw "llvm-profdata.exe not found at $LlvmProfdata. Run: rustup component add llvm-tools"
}

# Always carry the v2 baseline since RUSTFLAGS env replaces config rustflags.
$BaselineFlags = '-C target-cpu=x86-64-v2'

function Invoke-CargoClean {
    Write-Host "[pgo] cargo clean -p yagu-core" -ForegroundColor DarkGray
    Push-Location $RustCoreDir
    try { cargo clean -p yagu-core --release | Out-Null } finally { Pop-Location }
}

function Build-YaguHostOnly {
    # Build the .NET host but DO NOT trigger the embedded `cargo build`,
    # otherwise it would overwrite our instrumented / PGO-optimized DLL.
    Write-Host "[pgo] building Yagu (host, /p:BuildRustCore=false)" -ForegroundColor DarkGray
    & dotnet build $YaguCsproj -c Release /p:BuildRustCore=false -v:m | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Yagu host build failed." }
}

# ---------------------------------------------------------------------------
# Phase 0: pre-flight build of host so we have a working Yagu.exe to run.
# ---------------------------------------------------------------------------
Build-YaguHostOnly

if (-not $SkipTraining) {
    # -----------------------------------------------------------------------
    # Phase 1: build instrumented yagu_core.dll
    # -----------------------------------------------------------------------
    if (Test-Path $ProfileDir) { Remove-Item -Recurse -Force $ProfileDir }
    New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null

    Invoke-CargoClean

    # The profile-generate dir must be absolute so the instrumented DLL writes
    # .profraw files there regardless of which cwd Yagu.exe is launched from.
    $ProfileDirAbs = (Resolve-Path $ProfileDir).Path
    $env:RUSTFLAGS = "$BaselineFlags -C profile-generate=$ProfileDirAbs"

    Write-Host "[pgo] phase 1: cargo build --release (instrumented)" -ForegroundColor Cyan
    Write-Host "       RUSTFLAGS=$env:RUSTFLAGS" -ForegroundColor DarkGray
    Push-Location $RustCoreDir
    try {
        cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "Phase 1 cargo build failed." }
    } finally { Pop-Location }
    $env:RUSTFLAGS = $null

    Copy-Item -Force $RustDll $HostedDll
    Write-Host "[pgo] copied instrumented DLL -> $HostedDll" -ForegroundColor DarkGray

    # -----------------------------------------------------------------------
    # Training workload. Several runs covering the main scan paths so PGO
    # has signal for both literal and regex hot loops.
    # -----------------------------------------------------------------------
    $TrainingRuns = @(
        # Common literal, case-insensitive (default).
        @{ pattern = 'TODO';       opts = @() },
        @{ pattern = 'function';   opts = @() },
        @{ pattern = 'using';      opts = @() },
        # Case-sensitive literal exercises the memmem fast path.
        @{ pattern = 'public';     opts = @('--case-sensitive') },
        # Regex path exercises grep-regex / regex crate.
        @{ pattern = '\bfn\s+\w+'; opts = @('--regex') },
        @{ pattern = '#\[derive'; opts = @('--regex') }
    )

    Write-Host "[pgo] running $($TrainingRuns.Count) training workload(s) over $TrainingDir" -ForegroundColor Cyan
    foreach ($run in $TrainingRuns) {
        $argList = @('--cli', '--directory', $TrainingDir) + $run.opts + @($run.pattern)
        Write-Host "       Yagu.exe $($argList -join ' ')" -ForegroundColor DarkGray
        # Discard stdout (matches) and stderr (summary). Yagu writes a benign
        # admin-privilege warning to stderr, which would trigger a terminating
        # error under $ErrorActionPreference='Stop'. Temporarily relax it and
        # use 2>&1 + Out-Null instead of `*> $null` so stderr is captured cleanly.
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $YaguExe @argList 2>&1 | Out-Null
        } finally {
            $ErrorActionPreference = $prev
        }
        $sw.Stop()
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "training run exited with code $LASTEXITCODE (continuing)"
        }
        Write-Host ("       -> {0:n1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
    }

    $RawCount = @(Get-ChildItem $ProfileDir -Filter *.profraw -Recurse -ErrorAction SilentlyContinue).Count
    if ($RawCount -eq 0) {
        throw "No .profraw files were produced in $ProfileDir. Instrumented DLL did not emit profile data."
    }
    Write-Host "[pgo] collected $RawCount .profraw file(s)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Merge profiles
# ---------------------------------------------------------------------------
Write-Host "[pgo] merging profiles -> $MergedFile" -ForegroundColor Cyan
& $LlvmProfdata merge -o $MergedFile (Join-Path $ProfileDir '*.profraw')
if ($LASTEXITCODE -ne 0) { throw "llvm-profdata merge failed." }

# ---------------------------------------------------------------------------
# Phase 2: optimized build using merged profile
# ---------------------------------------------------------------------------
Invoke-CargoClean

$MergedAbs = (Resolve-Path $MergedFile).Path
# `-Cprofile-use=...` plus `-Cllvm-args=-pgo-warn-missing-function` surfaces
# any function lacking profile coverage (helps tune training workload).
$env:RUSTFLAGS = "$BaselineFlags -C profile-use=$MergedAbs -C llvm-args=-pgo-warn-missing-function"

Write-Host "[pgo] phase 2: cargo build --release (PGO-optimized)" -ForegroundColor Cyan
Write-Host "       RUSTFLAGS=$env:RUSTFLAGS" -ForegroundColor DarkGray
Push-Location $RustCoreDir
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "Phase 2 cargo build failed." }
} finally { Pop-Location }
$env:RUSTFLAGS = $null

# Copy final DLL into the running host output as well so a manual smoke test
# below picks it up. The next regular VS build will also pick it up via the
# `PreserveNewest` link in Yagu.csproj.
Copy-Item -Force $RustDll $HostedDll

Write-Host ""
Write-Host "[pgo] DONE." -ForegroundColor Green
Write-Host "      Optimized DLL: $RustDll"
Write-Host "      Also copied to: $HostedDll"
Write-Host ""
Write-Host "Smoke test:" -ForegroundColor DarkGray
Write-Host "  & '$YaguExe' --cli --directory '$TrainingDir' TODO" -ForegroundColor DarkGray
