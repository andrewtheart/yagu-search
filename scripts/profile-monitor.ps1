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
# Always kill and relaunch Task Manager fresh — stale instances have broken UIA COM proxies.
# Task Manager is single-instance (mutex). After a force-kill the mutex can linger briefly,
# causing a freshly launched taskmgr.exe to see the "existing" instance and exit immediately.
# Kill ALL instances, wait for full reap, then retry launch/detect until a windowed process appears.
Get-Process -Name Taskmgr -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
# Wait until no taskmgr remains (mutex released on full process reap)
$reapWait = 0
while ((Get-Process -Name Taskmgr -ErrorAction SilentlyContinue) -and $reapWait -lt 20) {
    Start-Sleep -Milliseconds 500
    $reapWait++
}
Start-Sleep -Seconds 2

$tmProc = $null
for ($tmLaunch = 1; $tmLaunch -le 5 -and -not $tmProc; $tmLaunch++) {
    Start-Process taskmgr.exe
    # Wait up to 10s for the process to appear with a rendered main window
    $tmWait = 0
    while ($tmWait -lt 20) {
        Start-Sleep -Milliseconds 500
        $candidate = Get-Process -Name Taskmgr -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate -and $candidate.MainWindowHandle -and $candidate.MainWindowHandle -ne [IntPtr]::Zero) {
            $tmProc = $candidate
            break
        }
        $tmWait++
    }
    if (-not $tmProc) {
        Write-Host "Task Manager launch attempt $tmLaunch did not produce a windowed process; retrying..."
        Start-Sleep -Seconds 2
    }
}
if (-not $tmProc) {
    Write-Error "Failed to launch Task Manager with a visible window after 5 attempts."
    exit 1
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
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    static uint SendKey(ushort vk, bool up) {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        INPUT[] input = new INPUT[1];
        input[0].type = 1;
        input[0].u.ki.wVk = vk;
        input[0].u.ki.wScan = scan;
        input[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        return SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void ReleaseModifiers() {
        ushort[] keys = new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C };
        foreach (ushort key in keys) {
            SendKey(key, true);
            Thread.Sleep(20);
        }
    }

    public static void TapKey(ushort vk) {
        SendKey(vk, false);
        Thread.Sleep(45);
        SendKey(vk, true);
        Thread.Sleep(90);
    }

    public static void CtrlTap(ushort vk) {
        SendKey(0x11, false);
        Thread.Sleep(80);
        TapKey(vk);
        Thread.Sleep(60);
        SendKey(0x11, true);
        Thread.Sleep(250);
    }

    public static void TypeText(string text) {
        foreach (char ch in text) {
            char upper = Char.ToUpperInvariant(ch);
            if (upper >= 'A' && upper <= 'Z') {
                TapKey((ushort)upper);
            } else if (upper >= '0' && upper <= '9') {
                TapKey((ushort)upper);
            }
        }
    }

    public static RECT GetRect(IntPtr hWnd) {
        RECT rect;
        GetWindowRect(hWnd, out rect);
        return rect;
    }

    public static bool ForceForeground(IntPtr hWnd) {
        if (IsIconic(hWnd)) {
            ShowWindow(hWnd, 9);
        } else {
            ShowWindow(hWnd, 5);
        }
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

# Maximize Task Manager so the Disk MB/s column is wide enough for reliable OCR
# (SW_MAXIMIZE = 3). Without this, Task Manager often opens narrow and OCR can
# misread the disk/CPU/memory columns or capture VS Code text underneath.
function Get-TmRectSummary {
    param([IntPtr]$Handle)
    $rect = [TmAutomate]::GetRect($Handle)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    "L=$($rect.Left) T=$($rect.Top) R=$($rect.Right) B=$($rect.Bottom) W=$width H=$height"
}

function Maximize-TaskManagerWindow {
    param([IntPtr]$Handle)

    $screen = [System.Windows.Forms.Screen]::FromHandle($Handle)
    $work = $screen.WorkingArea

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        [void][TmAutomate]::ForceForeground($Handle)
        [void][TmAutomate]::ShowWindow($Handle, 3)
        Start-Sleep -Milliseconds 600

        $rect = [TmAutomate]::GetRect($Handle)
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        $nearWorkArea = ([math]::Abs($width - $work.Width) -le 80) -and ([math]::Abs($height - $work.Height) -le 80)
        if ($nearWorkArea) {
            Write-Host "Task Manager maximized via ShowWindow: $(Get-TmRectSummary -Handle $Handle)"
            return
        }

        Write-Host "Maximize attempt $attempt left Task Manager restored-size: $(Get-TmRectSummary -Handle $Handle); work area W=$($work.Width) H=$($work.Height)"
    }

    [void][TmAutomate]::MoveWindow($Handle, $work.Left, $work.Top, $work.Width, $work.Height, $true)
    Start-Sleep -Milliseconds 600
    Write-Host "Task Manager resized to work area fallback: $(Get-TmRectSummary -Handle $Handle)"
}

Maximize-TaskManagerWindow -Handle $tmProc.MainWindowHandle

# ── Set the Task Manager "Yagu" filter, robustly ─────────────────────
# On current Windows builds Task Manager's WinUI islands may expose only
# DesktopWindowXamlSource/InputSite stubs to UIA, with no Edit/ValuePattern.
# Ctrl+F still focuses the search box. Text injection into elevated Task Manager
# requires this script to run elevated too; otherwise Windows UIPI blocks it.
$filterText = "Yagu"

function Set-TmFilterViaKeyboard {
    param(
        [IntPtr]$Handle,
        [string]$Text
    )

    [void][TmAutomate]::ForceForeground($Handle)
    Start-Sleep -Milliseconds 300
    [TmAutomate]::ReleaseModifiers()

    # Ctrl+F focuses the Task Manager search box without moving the mouse.
    [TmAutomate]::CtrlTap(0x46) # F
    Start-Sleep -Milliseconds 700

    # Replace any existing filter text.
    [TmAutomate]::ReleaseModifiers()
    [TmAutomate]::CtrlTap(0x41) # A
    [TmAutomate]::TapKey(0x08)  # Backspace
    Start-Sleep -Milliseconds 150

    [TmAutomate]::TypeText($Text)
    Start-Sleep -Milliseconds 700
    [TmAutomate]::ReleaseModifiers()
}

# Fast path for older Task Manager builds that still expose the search box to UIA.
$filterApplied = $false
try {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($tmProc.MainWindowHandle)
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Search box')
    $searchBox = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if ($searchBox) {
        try { $searchBox.SetFocus() } catch { }
        Start-Sleep -Milliseconds 200
        $vp = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue('')
        Start-Sleep -Milliseconds 100
        $vp.SetValue($filterText)
        Start-Sleep -Milliseconds 500
        Write-Host "Search filter set to '$filterText' via UIA ValuePattern."
        $filterApplied = $true
    }
} catch {
    Write-Host "UIA search filter path unavailable: $($_.Exception.Message)"
}

if (-not $filterApplied) {
    Write-Host "UIA search Edit unavailable; using Ctrl+F keyboard filter path."
    Set-TmFilterViaKeyboard -Handle $tmProc.MainWindowHandle -Text $filterText
    $filterApplied = $true
    Write-Host "Search filter typed via Ctrl+F keyboard path."
}

Start-Sleep -Milliseconds 500

# ── 2. Attach VSDiagnostics sessions ──────────────────────────────────
$VSDiag = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Team Tools\DiagnosticsHub\Collector\VSDiagnostics.exe"
$AgentConfigs = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Team Tools\DiagnosticsHub\Collector\AgentConfigs"

# Force-stop any lingering sessions first (requires /output: to actually release them)
$cleanupDir = "$env:TEMP\vsdiag-cleanup"
if (!(Test-Path $cleanupDir)) { New-Item -ItemType Directory -Path $cleanupDir -Force | Out-Null }
1..10 | ForEach-Object {
    $outFile = "$cleanupDir\cleanup-session-$_.diagsession"
    Remove-Item $outFile -Force -ErrorAction SilentlyContinue
    $stopArgs = @('stop', $_, "/output:$outFile")
    try {
        & $VSDiag @stopArgs *> $null
    } catch {
        # Some stale/nonexistent sessions report "unsupported state"; cleanup is best-effort.
    }
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
