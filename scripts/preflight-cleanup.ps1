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

# Helper: Best-effort delete that retries after stopping the StandardCollector service
# if a file handle is still held. Returns $true if the path is gone after the call.
function Remove-WithRetry {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    return -not (Test-Path -LiteralPath $Path)
}

# -- 1. Stop all VSDiagnostics sessions (1-6) --------------------------
# CRITICAL: VSDiagnostics `stop` requires /output:<path> - without it, the session
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

# -- 2. Remove diagnostic temp directories -----------------------------
# VSDiag creates GUID-named dirs like "0197E42F-003D-4F91-A845-6404CF289E84" and ".scratch"
# The pattern for session IDs 1-10 is: {XX}97E42F-003D-4F91-A845-6404CF289E84
#
# CRITICAL: StandardCollector.Service holds an open handle on `<GUID>\lock` for every
# session it has ever hosted. While the service is running, Remove-Item silently
# fails to delete that lock file (the directory then partially survives, the dir
# LWT updates but the lock file lingers). On the next `vsdiag start N`, VSDiag
# tries to (re)create the lock file and fails with "The file exists (HRESULT
# 0x80070050)" - which is exactly the FileIO session-3 attach failure we observed.
#
# Fix: stop VSStandardCollectorService150 BEFORE attempting deletion. The service
# auto-restarts on the next `vsdiag start`, so this is safe. We do NOT restart the
# service manually here (see step 7's comment about kernel ETW DACLs - that only
# matters after sessions are attached, not during pre-attach cleanup).
$svc = Get-Service -Name VSStandardCollectorService150 -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Host "Stopping VSStandardCollectorService150 to release session lock files..."
    Stop-Service -Name VSStandardCollectorService150 -Force -ErrorAction SilentlyContinue
    # Wait for the StandardCollector.Service process to actually exit so handles release.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Name StandardCollector.Service -ErrorAction SilentlyContinue
        if (-not $proc) { break }
        Start-Sleep -Milliseconds 200
    }
    $proc = Get-Process -Name StandardCollector.Service -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Warning "  StandardCollector.Service did not exit cleanly; forcing kill (PID $($proc.Id))"
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    Write-Host "  Service stopped (will auto-restart on next vsdiag start)."
}

Write-Host "Cleaning temp directories..."
$removedCount = 0
$survivors = @()
Get-ChildItem $env:TEMP -Directory -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match 'diagsession|VSDiag|DiagnosticsHub|DiagHub|97E42F-003D-4F91-A845-6404CF289E84'
} | ForEach-Object {
    $full = $_.FullName
    $name = $_.Name
    if (Remove-WithRetry -Path $full) {
        $removedCount++
        Write-Host "  Removed: $name"
    } else {
        # Likely a held lock file. Surface it so we can investigate further.
        $survivors += $full
        $kids = Get-ChildItem $full -Force -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
        Write-Warning "  Could NOT fully remove: $name (still contains: $($kids -join ', '))"
    }
}

# Also clean up the cleanup dir itself (from previous runs)
if (Test-Path $cleanupDir) {
    Remove-Item $cleanupDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed: vsdiag-cleanup"
}

if ($removedCount -eq 0) {
    Write-Host "  No temp directories to clean."
}

# -- 3. Aggressive disk cleanup - TestResults artifacts -----------------
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

# ResultStore temp data (can grow to 25+ GB between runs).
# Yagu writes `yagu-results-*.tmp` files via ResultStore. Possible locations:
#   - C:\Temp\Yagu          (current default - see ChooseResultStoreTempDir)
#   - $env:TEMP\Yagu        (profiling override via YAGU_RESULTSTORE_TEMP)
#   - D:\Temp\Yagu          (legacy default)
#   - $env:TEMP             (Path.GetTempPath() fallback if no tempDirectory passed)
$resultStoreDirs = @(
    'C:\Temp\Yagu',
    'D:\Temp\Yagu',
    (Join-Path $env:TEMP 'Yagu')
) | Select-Object -Unique

foreach ($dir in $resultStoreDirs) {
    if (Test-Path $dir) {
        $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ($size -gt 0 -or (Get-ChildItem $dir -Force -ErrorAction SilentlyContinue)) {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            $freedMB += [math]::Round($size / 1MB)
            Write-Host "  Removed: $dir ($([math]::Round($size/1GB, 2)) GB)"
        }
    }
}

# Orphaned yagu-results-*.tmp files left directly in %TEMP% (matches ResultStore.TempFileSearchPattern)
$orphans = Get-ChildItem -Path $env:TEMP -Filter 'yagu-results-*.tmp' -File -ErrorAction SilentlyContinue
if ($orphans) {
    $orphanSize = ($orphans | Measure-Object -Property Length -Sum).Sum
    $orphans | Remove-Item -Force -ErrorAction SilentlyContinue
    $freedMB += [math]::Round($orphanSize / 1MB)
    Write-Host "  Removed: $($orphans.Count) orphaned yagu-results-*.tmp file(s) in `$env:TEMP ($([math]::Round($orphanSize/1GB, 2)) GB)"
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

# -- 4. Clear old Yagu log ---------------------------------------------
$yaguLog = "$env:APPDATA\Yagu\yagu.log"
if (Test-Path $yaguLog) {
    Remove-Item $yaguLog -Force -ErrorAction SilentlyContinue
    Write-Host "Cleared Yagu log."
} else {
    Write-Host "No Yagu log to clear."
}

# -- 5. Ensure Everything is running -----------------------------------
$everythingRunning = Get-Process -Name Everything -ErrorAction SilentlyContinue
if (-not $everythingRunning) {
    Start-Process "C:\Program Files\Everything\Everything.exe" -ArgumentList "-startup"
    Start-Sleep -Seconds 2
    Write-Host "Started Everything."
} else {
    Write-Host "Everything is already running."
}

# -- 6. Kill any existing Yagu processes from this workspace (by PID) --
$repoRoot = Split-Path -Parent $PSScriptRoot
$yaguBinRoot = Join-Path $repoRoot 'src\Yagu\bin'
$yaguProcs = @(Get-CimInstance Win32_Process -Filter "Name = 'Yagu.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($yaguBinRoot, [System.StringComparison]::OrdinalIgnoreCase) })
if ($yaguProcs.Count -gt 0) {
    foreach ($yaguProc in $yaguProcs) {
        Stop-Process -Id $yaguProc.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "Killed existing workspace Yagu (PID: $($yaguProc.ProcessId), Path: $($yaguProc.ExecutablePath))."
    }
    Start-Sleep -Seconds 2
} else {
    Write-Host "No existing workspace Yagu processes."
}

# -- 7. Quick verification - just check sessions 1-3 (the ones profile-monitor uses) --
#
# History: previously, if any session reported "Running" we restarted
# VSStandardCollectorService150. That breaks the FileIO session (session 3) on the
# NEXT stop: the FileIO agent uses a kernel-mode ETW logger whose DACL is tied to
# the original service-instance token. After the service restart, the new instance
# can stop the user-mode CPU/DotNet sessions (re-attached from on-disk GUIDs) but
# `ICollectionSession.Stop()` on the kernel-mode logger fails with E_ACCESSDENIED
# even when running elevated, because the new token isn't on the kernel session's
# ACL. We now drop straight to `logman -ets` to terminate any stuck kernel/user
# ETW sessions directly, without restarting the controller service.
$activeSessions = @()
foreach ($sessionId in 1..3) {
    $result = & $VSDiag status $sessionId 2>&1 | Out-String
    if ($result -match 'Running|Paused') {
        Write-Warning "Session $sessionId is STILL active after cleanup!"
        $activeSessions += $sessionId
    }
}
if ($activeSessions.Count -eq 0) {
    Write-Host "`nPre-flight cleanup complete. All VSDiag sessions clear."
} else {
    Write-Warning "Some sessions still active - terminating ETW sessions via logman -ets..."
    # logman speaks directly to the ETW kernel API (StopTrace) and bypasses the
    # collector service entirely. It can stop both user-mode and kernel-mode
    # loggers as long as the caller is elevated (which the loop requires).
    $logmanList = & logman query -ets 2>&1 | Out-String
    $stoppedAny = $false
    foreach ($line in $logmanList -split "`r?`n") {
        # Session names from the DiagnosticsHub collector typically look like
        # "VSDiagnostics_<GUID>" or "DiagnosticsHub_<GUID>" or "Microsoft-VSDiag-*".
        if ($line -match '^\s*(VSDiagnostics_\S+|DiagnosticsHub_\S+|Microsoft-VSDiag\S*)\s') {
            $sessionName = $Matches[1]
            $r = & logman stop $sessionName -ets 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0 -or $r -match 'success') {
                Write-Host "  logman stopped: $sessionName"
                $stoppedAny = $true
            } else {
                Write-Warning "  logman could not stop $sessionName : $($r.Trim())"
            }
        }
    }
    if (-not $stoppedAny) {
        Write-Warning "  No matching ETW sessions found via logman. Sessions may be ghost entries in VSDiag's session table only."
    }
    Start-Sleep -Seconds 1
    Write-Host "Pre-flight cleanup complete (ETW fallback path)."
}
