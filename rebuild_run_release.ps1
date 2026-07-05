#Requires -Version 5.1
<#
.SYNOPSIS
  Rebuilds Yagu in Release mode and launches it.

.DESCRIPTION
  1. Stops any running Yagu instance so the locked exe can be overwritten.
  2. Builds src/Yagu/Yagu.csproj in Release (normal profile, no Rust profiling).
  3. Reverts the auto-incremented build-version files so the working tree stays clean.
  4. Launches the freshly built Release Yagu.exe.
#>
$ErrorActionPreference = 'Stop'

$repoRoot    = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\Yagu\Yagu.csproj'
$tfm         = 'net10.0-windows10.0.19041.0'
$exePath     = Join-Path $repoRoot "src\Yagu\bin\Release\$tfm\Yagu.exe"

Write-Host 'Stopping any running Yagu instance...' -ForegroundColor Cyan
Get-Process -Name Yagu -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host 'Building Yagu (Release)...' -ForegroundColor Cyan
dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build failed (exit code $LASTEXITCODE)." }

# Revert the build-version churn the build emits, keeping git clean (best effort).
try { git -C $repoRoot checkout -- 'Yagu/Properties/build-version.txt' 'Yagu/Properties/AppInfo.g.cs' 2>$null } catch { }

if (-not (Test-Path -LiteralPath $exePath)) { throw "Yagu.exe not found at: $exePath" }

Write-Host "Launching $exePath" -ForegroundColor Green
Start-Process -FilePath $exePath
