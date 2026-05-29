<#
.SYNOPSIS
    Pre-flight cleanup for the Memory Profiling Loop (Step 0).
.DESCRIPTION
    Stops any lingering VSDiagnostics sessions (with /output to temp so they actually stop),
    removes diagnostic temp directories, clears the Yagu log, and ensures Everything is running.
    Also kills any existing Yagu process (by PID, never by name).

    This MUST be run before every profiling iteration.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$VSDiag = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Team Tools\DiagnosticsHub\Collector\VSDiagnostics.exe"
$cleanupDir = "$env:TEMP\vsdiag-cleanup"

# ── 1. Stop all VSDiagnostics sessions (1-6) ──────────────────────────
# CRITICAL: VSDiagnostics `stop` requires /output:<path> — without it, the session
# stays running and subsequent `start` calls fail with "The file exists (0x80070050)".
# Strategy: Just attempt stop on all session IDs. Non-existent sessions fail harmlessly.
Write-Host "Stopping VSDiag sessions 1-6..."
if (!(Test-Path $cleanupDir)) { New-Item -ItemType Directory -Path $cleanupDir -Force | Out-Null }

$stoppedCount = 0
1..6 | ForEach-Object {
    $outFile = "$cleanupDir\cleanup-session-$_.diagsession"
    # Remove previous cleanup output if it exists (stop fails if output file already exists)
    Remove-Item $outFile -Force -ErrorAction SilentlyContinue
    $result = & $VSDiag stop $_ /output:"$outFile" 2>&1 | Out-String
    if ($result -match 'Stopped') {
        $stoppedCount++
        Write-Host "  Stopped session $_"
    }
}
if ($stoppedCount -eq 0) {
    Write-Host "  No active sessions found."
} else {
    Write-Host "  Stopped $stoppedCount session(s)."
}

# ── 2. Remove diagnostic temp directories ─────────────────────────────
# VSDiag creates GUID-named dirs like "0197E42F-003D-4F91-A845-6404CF289E84" and ".scratch"
# The pattern for session IDs 1-10 is: {XX}97E42F-003D-4F91-A845-6404CF289E84
Write-Host "Cleaning temp directories..."
$removedCount = 0
Get-ChildItem $env:TEMP -Directory -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match 'diagsession|VSDiag|DiagnosticsHub|DiagHub|97E42F-003D-4F91-A845-6404CF289E84'
} | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    $removedCount++
    Write-Host "  Removed: $($_.Name)"
}

# Also clean up the cleanup dir itself (from previous runs)
if (Test-Path $cleanupDir) {
    Remove-Item $cleanupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed: vsdiag-cleanup"
}

if ($removedCount -eq 0) {
    Write-Host "  No temp directories to clean."
}

# ── 3. Aggressive disk cleanup — TestResults artifacts ─────────────────
# Removes large extracted ETL folders, .diagsession archives, screenshots,
# stale reports, and .analysis caches that bloat D:\ between iterations.
$testResults = "C:\src\Yagu\TestResults"
$freedMB = 0

if (Test-Path $testResults) {
    Write-Host "Cleaning TestResults artifacts..."

    # Extracted ETL folders (4-8 GB each)
    Get-ChildItem "$testResults\PerfTraces\extracted-*" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $size = (Get-ChildItem $_.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        Remove-Item $_.FullName -Recurse -Force
        $freedMB += [math]::Round($size / 1MB)
        Write-Host "  Removed: $($_.Name) ($([math]::Round($size/1GB, 1)) GB)"
    }

    # .diagsession files in PerfTraces
    Get-ChildItem "$testResults\PerfTraces\*.diagsession" -ErrorAction SilentlyContinue | ForEach-Object {
        $freedMB += [math]::Round($_.Length / 1MB)
        Remove-Item $_.FullName -Force
        Write-Host "  Removed: $($_.Name)"
    }

    # TaskManagerScreenshots
    $shots = Get-ChildItem "$testResults\TaskManagerScreenshots\*" -ErrorAction SilentlyContinue
    if ($shots) {
        $shotSize = ($shots | Measure-Object -Property Length -Sum).Sum
        $freedMB += [math]::Round($shotSize / 1MB)
        Remove-Item "$testResults\TaskManagerScreenshots\*" -Force
        Write-Host "  Cleared TaskManagerScreenshots ($($shots.Count) files)"
    }

    # MatchNavScreenshots
    if (Test-Path "$testResults\MatchNavScreenshots") {
        $size = (Get-ChildItem "$testResults\MatchNavScreenshots" -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ($size -gt 0) {
            $freedMB += [math]::Round($size / 1MB)
            Remove-Item "$testResults\MatchNavScreenshots" -Recurse -Force
            Write-Host "  Removed: MatchNavScreenshots ($([math]::Round($size/1MB)) MB)"
        }
    }

    # Old report directories (Report* older than current day)
    Get-ChildItem "$testResults\Report*" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $size = (Get-ChildItem $_.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ($size -gt 100MB) {
            $freedMB += [math]::Round($size / 1MB)
            Remove-Item $_.FullName -Recurse -Force
            Write-Host "  Removed: $($_.Name) ($([math]::Round($size/1MB)) MB)"
        }
    }
}

# ResultStore temp data (can grow to 25+ GB between runs)
$resultStoreTmp = "D:\Temp\Yagu"
if (Test-Path $resultStoreTmp) {
    $size = (Get-ChildItem $resultStoreTmp -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($size -gt 0) {
        Remove-Item $resultStoreTmp -Recurse -Force -ErrorAction SilentlyContinue
        $freedMB += [math]::Round($size / 1MB)
        Write-Host "  Removed: D:\Temp\Yagu ($([math]::Round($size/1GB, 1)) GB)"
    }
}

# .analysis cache at repo root
if (Test-Path "C:\src\Yagu\.analysis") {
    $size = (Get-ChildItem "C:\src\Yagu\.analysis" -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    $freedMB += [math]::Round($size / 1MB)
    Remove-Item "C:\src\Yagu\.analysis" -Recurse -Force
    Write-Host "  Removed: .analysis ($([math]::Round($size/1MB)) MB)"
}

if ($freedMB -gt 0) {
    Write-Host "  Freed ~$([math]::Round($freedMB/1024, 1)) GB total."
} else {
    Write-Host "  Nothing to clean."
}
Write-Host "  D:\ free: $([math]::Round((Get-PSDrive D).Free/1GB, 1)) GB"

# ── 4. Clear old Yagu log ─────────────────────────────────────────────
$yaguLog = "$env:APPDATA\Yagu\yagu.log"
if (Test-Path $yaguLog) {
    Remove-Item $yaguLog -Force -ErrorAction SilentlyContinue
    Write-Host "Cleared Yagu log."
} else {
    Write-Host "No Yagu log to clear."
}

# ── 5. Ensure Everything is running ───────────────────────────────────
$everythingRunning = Get-Process -Name Everything -ErrorAction SilentlyContinue
if (-not $everythingRunning) {
    Start-Process "C:\Program Files\Everything\Everything.exe" -ArgumentList "-startup"
    Start-Sleep -Seconds 2
    Write-Host "Started Everything."
} else {
    Write-Host "Everything is already running."
}

# ── 6. Kill any existing Yagu process (by PID) ────────────────────────
$yaguProc = Get-Process -Name Yagu -ErrorAction SilentlyContinue | Select-Object -First 1
if ($yaguProc) {
    Stop-Process -Id $yaguProc.Id -Force
    Write-Host "Killed existing Yagu (PID: $($yaguProc.Id))."
    Start-Sleep -Seconds 2
} else {
    Write-Host "No existing Yagu process."
}

# ── 7. Quick verification — just check sessions 1-3 (the ones profile-monitor uses) ──
$anyActive = $false
1..3 | ForEach-Object {
    $result = & $VSDiag status $_ 2>&1 | Out-String
    if ($result -match 'Running|Paused') {
        Write-Warning "Session $_ is STILL active after cleanup!"
        $anyActive = $true
    }
}
if (-not $anyActive) {
    Write-Host "`nPre-flight cleanup complete. All VSDiag sessions clear."
} else {
    Write-Warning "Some sessions still active — restarting VSStandardCollectorService150..."
    Restart-Service 'VSStandardCollectorService150' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    Write-Host "Service restarted. Pre-flight cleanup complete."
}
