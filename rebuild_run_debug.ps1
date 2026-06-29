#Requires -Version 5.1
<#
.SYNOPSIS
  Rebuilds Yagu in Debug mode (with the Rust profiling profile) and launches it.

.DESCRIPTION
  Default (no switch): stop running Yagu, build, revert version churn, then launch.

  1. Stops any running Yagu instance so the locked exe can be overwritten.
  2. Builds Yagu/Yagu.csproj in Debug with -p:RustProfile=profiling (symbol-rich native binary).
  3. Reverts the auto-incremented build-version files so the working tree stays clean.
  4. Launches the freshly built Debug Yagu.exe.

.PARAMETER BuildOnly
  Stop + build + revert version churn, but do NOT launch the binary.

.PARAMETER RunOnly
  Launch the existing Debug Yagu.exe only; skip stopping and rebuilding.

.EXAMPLE
  .\rebuild_run_debug.ps1            # stop + build + launch (default)
  .\rebuild_run_debug.ps1 -BuildOnly # stop + build, no launch
  .\rebuild_run_debug.ps1 -RunOnly   # just launch the current binary
#>
[CmdletBinding()]
param(
    [switch]$BuildOnly,
    [switch]$RunOnly
)
$ErrorActionPreference = 'Stop'

if ($BuildOnly -and $RunOnly) {
    throw 'Pass either -BuildOnly or -RunOnly, not both.'
}

$repoRoot    = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'Yagu\Yagu.csproj'
$tfm         = 'net10.0-windows10.0.19041.0'
$exePath     = Join-Path $repoRoot "Yagu\bin\Debug\$tfm\Yagu.exe"

if (-not $RunOnly) {
    Write-Host 'Stopping any running Yagu instance...' -ForegroundColor Cyan
    Get-Process -Name Yagu -ErrorAction SilentlyContinue | Stop-Process -Force

    Write-Host 'Building Yagu (Debug, Rust profiling profile)...' -ForegroundColor Cyan
    dotnet build $projectPath -c Debug -p:RustProfile=profiling
    if ($LASTEXITCODE -ne 0) { throw "Debug build failed (exit code $LASTEXITCODE)." }

    # Revert the build-version churn the build emits, keeping git clean (best effort).
    try { git -C $repoRoot checkout -- 'Yagu/Properties/build-version.txt' 'Yagu/Properties/AppInfo.g.cs' 2>$null } catch { }
}

if ($BuildOnly) {
    Write-Host 'Build complete (-BuildOnly: not launching).' -ForegroundColor Green
    return
}

if (-not (Test-Path -LiteralPath $exePath)) { throw "Yagu.exe not found at: $exePath" }

Write-Host "Launching $exePath" -ForegroundColor Green
Start-Process -FilePath $exePath
