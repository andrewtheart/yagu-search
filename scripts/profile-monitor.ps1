<#
.SYNOPSIS
    Attaches VSDiagnostics profilers to Yagu and monitors memory/IO/screenshots for 2 minutes.
.DESCRIPTION
    This script is Step 3 of the Memory Profiling Loop (PLANS/MEMORY_PROFILING_LOOP.md).
    It:
      1. Launches Task Manager and sets up the "Yagu" search filter (main thread, UI Automation)
      2. Attaches 3 VSDiag profiling sessions (CPU, memory, file I/O)
      3. Starts 3 parallel background jobs: screenshots, I/O counters, WS sampling
      4. Waits for all jobs to complete (~2 minutes)

    Task Manager foreground + search filter setup happens in the main thread because Windows
    blocks SetForegroundWindow from non-foreground threads/processes.
.PARAMETER YaguPid
    PID of the running Yagu process. If omitted, auto-detects.
#>
param(
    [int]$YaguPid
)

$ErrorActionPreference = 'Stop'

# ── Resolve Yagu PID ──────────────────────────────────────────────────
if (-not $YaguPid) {
    $yaguProc = Get-Process -Name Yagu -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $yaguProc) {
        Write-Error "No Yagu process found. Launch Yagu first or pass -YaguPid."
        exit 1
    }
    $YaguPid = $yaguProc.Id
}
Write-Host "Yagu PID: $YaguPid"

# ── 1. Set up Task Manager with Yagu filter (MAIN THREAD) ─────────────
# Always kill and relaunch Task Manager fresh — stale instances have broken UIA COM proxies
$tmProc = Get-Process -Name Taskmgr -ErrorAction SilentlyContinue | Select-Object -First 1
if ($tmProc) {
    Stop-Process -Id $tmProc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
Start-Process taskmgr.exe
Start-Sleep -Seconds 5
$tmProc = Get-Process -Name Taskmgr -ErrorAction SilentlyContinue | Select-Object -First 1

# Wait for Task Manager's WinUI to fully render (UI Automation tree needs time)
$tmWait = 0
while (-not $tmProc.MainWindowHandle -or $tmProc.MainWindowHandle -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 500
    $tmProc = Get-Process -Id $tmProc.Id -ErrorAction SilentlyContinue
    $tmWait++
    if ($tmWait -gt 20) { break }
}
Start-Sleep -Seconds 3
Write-Host "Task Manager PID: $($tmProc.Id)"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Threading;
public class TmAutomate {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);

    public static bool ForceForeground(IntPtr hWnd) {
        ShowWindow(hWnd, 9);
        Thread.Sleep(100);
        SwitchToThisWindow(hWnd, true);
        Thread.Sleep(200);

        IntPtr fg = GetForegroundWindow();
        if (fg != hWnd) {
            uint fgPid;
            uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            if (fgThread != myThread) {
                attached = AttachThreadInput(myThread, fgThread, true);
            }
            keybd_event(0x12, 0x45, 0x1, UIntPtr.Zero);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            keybd_event(0x12, 0x45, 0x3, UIntPtr.Zero);
            if (attached) { AttachThreadInput(myThread, fgThread, false); }
        }

        Thread.Sleep(300);
        return GetForegroundWindow() == hWnd;
    }
}
"@

# Bring Task Manager to foreground
$fgOk = [TmAutomate]::ForceForeground($tmProc.MainWindowHandle)
Write-Host "Task Manager foreground: $fgOk"

# Use UI Automation to click search box and set filter text (with retries for WinUI load delay)
$searchBox = $null
$maxRetries = 5
for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
    try {
        $ae = [System.Windows.Automation.AutomationElement]::FromHandle($tmProc.MainWindowHandle)
        $nameCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "Search box")
        $searchBox = $ae.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
        if ($searchBox) {
            Write-Host "Found search box on attempt $attempt"
            break
        }
        Write-Host "Search box not found on attempt $attempt, retrying..."
    } catch {
        Write-Host "UI Automation attempt $attempt failed: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 2
}

if ($searchBox) {
    try {
        $invokePattern = $searchBox.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()
    } catch {
        Write-Host "InvokePattern failed, clicking via SetFocus..."
        try { $searchBox.SetFocus() } catch { }
    }
    Start-Sleep -Milliseconds 800

    try {
        $vp = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue("Yagu")
        Write-Host "Search filter set to 'Yagu' via ValuePattern"
    } catch {
        Write-Host "ValuePattern failed, trying SendKeys fallback..."
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        Start-Sleep -Milliseconds 200
        [System.Windows.Forms.SendKeys]::SendWait("Yagu")
        Write-Host "Search filter set via SendKeys fallback"
    }
    Start-Sleep -Milliseconds 500
} else {
    # WinUI 3 controls sometimes don't expose to UIA. Fall back to keyboard:
    # Ctrl+F focuses the filter box in modern Task Manager.
    Write-Host "UIA search box not found; using Ctrl+F keyboard fallback..."
    [void][TmAutomate]::ForceForeground($tmProc.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait("^f")
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait("Yagu")
    Start-Sleep -Milliseconds 800
    Write-Host "Search filter set via Ctrl+F + SendKeys"
}

# ── 2. Attach VSDiagnostics sessions ──────────────────────────────────
$VSDiag = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Team Tools\DiagnosticsHub\Collector\VSDiagnostics.exe"
$AgentConfigs = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Team Tools\DiagnosticsHub\Collector\AgentConfigs"

# Force-stop any lingering sessions first (requires /output: to actually release them)
$cleanupDir = "$env:TEMP\vsdiag-cleanup"
if (!(Test-Path $cleanupDir)) { New-Item -ItemType Directory -Path $cleanupDir -Force | Out-Null }
1..10 | ForEach-Object {
    $outFile = "$cleanupDir\cleanup-session-$_.diagsession"
    Remove-Item $outFile -Force -ErrorAction SilentlyContinue
    & $VSDiag stop $_ /output:"$outFile" 2>&1 | Out-Null
}
# Also remove stale GUID session directories (VSDiag uses pattern XX97E42F-...)
Get-ChildItem $env:TEMP -Directory -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '97E42F-003D-4F91-A845-6404CF289E84'
} | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}

& $VSDiag start 1 /attach:$YaguPid /loadConfig:"$AgentConfigs\CpuUsageLow.json"
& $VSDiag start 2 /attach:$YaguPid /loadConfig:"$AgentConfigs\DotNetObjectAllocBase.json"
& $VSDiag start 3 /attach:$YaguPid /loadConfig:"$AgentConfigs\FileIOBase.json"
Write-Host "All 3 profiling sessions attached."

# ── 3. Launch parallel monitoring jobs ────────────────────────────────

# Job 1: Task Manager screenshots every 1s for 2 minutes (no limit)
$screenshotJob = Start-Job -ScriptBlock {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $screenshotDir = "C:\src\Yagu\TestResults\TaskManagerScreenshots"
    if (!(Test-Path $screenshotDir)) { New-Item -ItemType Directory -Path $screenshotDir -Force | Out-Null }
    $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
    $durationSec = 120
    for ($i = 1; $i -le $durationSec; $i++) {
        Start-Sleep -Seconds 1
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen(0, 0, 0, 0, $screen.Size)
        $bitmap.Save("$screenshotDir\taskmgr-$ts-t${i}s.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose(); $bitmap.Dispose()
        if ($i % 10 -eq 0) { Write-Output "Screenshot $i/$durationSec (t=${i}s)" }
    }
    Write-Output "All $durationSec screenshots saved to: $screenshotDir"
}

# Job 2: Process I/O counters every 1s for 2 minutes
$ioJob = Start-Job -ArgumentList $YaguPid -ScriptBlock {
    param($targetPid)
    $lines = @("Time     Read MB/s   Write MB/s   Total MB/s   WS(MB)", "----     ---------   ----------   ----------   ------")
    $prevRead = 0; $prevWrite = 0
    # Use WMI to get initial I/O counters (Process.ReadTransferCount is broken for mmap I/O)
    $wmiProc = Get-CimInstance Win32_Process -Filter "ProcessId = $targetPid" -ErrorAction SilentlyContinue
    if ($wmiProc) { $prevRead = $wmiProc.ReadTransferCount; $prevWrite = $wmiProc.WriteTransferCount }
    for ($i = 1; $i -le 120; $i++) {
        Start-Sleep -Seconds 1
        $p = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $p) { $lines += "Process exited at t=$i"; break }
        $wmiProc = Get-CimInstance Win32_Process -Filter "ProcessId = $targetPid" -ErrorAction SilentlyContinue
        if ($wmiProc) {
            $currRead = $wmiProc.ReadTransferCount; $currWrite = $wmiProc.WriteTransferCount
        } else {
            $currRead = $prevRead; $currWrite = $prevWrite
        }
        $readMBps = [math]::Round(($currRead - $prevRead) / 1MB, 1)
        $writeMBps = [math]::Round(($currWrite - $prevWrite) / 1MB, 1)
        $totalMBps = [math]::Round($readMBps + $writeMBps, 1)
        $wsMB = [math]::Round($p.WorkingSet64 / 1MB)
        $lines += ("{0,4}s    {1,8:N1}    {2,9:N1}    {3,9:N1}    {4,5}" -f $i, $readMBps, $writeMBps, $totalMBps, $wsMB)
        $prevRead = $currRead; $prevWrite = $currWrite
    }
    $lines += ""; $lines += "Done. Total read: $([math]::Round($currRead/1GB, 2)) GB"
    $lines | ForEach-Object { Write-Output $_ }
}

# Job 3: WS/Private memory sampling every 15s for 2 minutes
$wsJob = Start-Job -ArgumentList $YaguPid -ScriptBlock {
    param($targetPid)
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep -Seconds 15
        $p = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($p) {
            Write-Output "[$(Get-Date -Format 'HH:mm:ss')] WS: $([math]::Round($p.WorkingSet64/1MB)) MB | Private: $([math]::Round($p.PrivateMemorySize64/1MB)) MB | Handles: $($p.HandleCount)"
        } else {
            Write-Output "Process exited early!"; break
        }
    }
}

# ── 4. Wait for all jobs to finish (~2 minutes) ──────────────────────
Write-Host "Monitoring for 2 minutes (3 parallel jobs running)..."
$null = Wait-Job $screenshotJob, $ioJob, $wsJob

Write-Host "`n=== Task Manager Screenshots ==="
Receive-Job $screenshotJob

Write-Host "`n=== I/O Counters ==="
Receive-Job $ioJob

Write-Host "`n=== WS/Private Memory Samples ==="
Receive-Job $wsJob

Remove-Job $screenshotJob, $ioJob, $wsJob
Write-Host "`n2-minute profiling window complete."
