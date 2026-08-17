# UI Automation script to test match navigation centering in Yagu.
# Uses Windows UIAutomation via .NET to interact with the WinUI 3 app.

param(
    [string]$Directory = "C:",
    [string]$Query = "a",
    [string]$ScreenshotDir = "C:\src\Yagu\TestResults\MatchNavScreenshots",
    [int]$MatchIterations = 200,
    [int]$SearchWaitSeconds = 15,
    [int]$PreviewLoadSeconds = 120,
    [int]$MaxFiles = 500,
    [int]$ExpectedFiles = 0,
    [int]$UseRegex = 0,
    [int]$ExactMatch = 1,
    [int]$Multiline = 0
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class YaguInput {
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public const uint PW_RENDERFULLCONTENT = 2;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_A = 0x41;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_APPS = 0x5D;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public static void LeftClick() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); }
    public static void RightClick() { mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero); }
    public static bool OpenContextMenu(IntPtr hWnd) {
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_APPS, IntPtr.Zero);
        return PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_APPS, IntPtr.Zero);
    }
    public static void ShiftLeftClick() {
        keybd_event(VK_SHIFT, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }
    public static void ControlA() {
        keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        keybd_event(VK_A, 0, 0, IntPtr.Zero);
        keybd_event(VK_A, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }
    // Windows blocks SetForegroundWindow from a process that does not own the foreground, so a bare call
    // silently no-ops and every mouse_event click below then lands on whatever window really is focused
    // (e.g. the terminal that launched this harness). Attaching to the foreground thread's input queue
    // first is the supported way to make the activation take effect.
    public static bool Activate(IntPtr hWnd) {
        ShowWindow(hWnd, 5);
        IntPtr fg = GetForegroundWindow();
        if (fg == hWnd) return true;
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint thisThread = GetCurrentThreadId();
        bool attached = false;
        try {
            if (fgThread != 0 && fgThread != thisThread) {
                attached = AttachThreadInput(thisThread, fgThread, true);
            }
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        } finally {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
        return GetForegroundWindow() == hWnd;
    }
    public static void Maximize(IntPtr hWnd) { ShowWindow(hWnd, 3); }
    public static long LastProgressTicks = DateTime.UtcNow.Ticks;
    public static void Progress() { LastProgressTicks = DateTime.UtcNow.Ticks; }
    public static void StartStallWatchdog(int stallSeconds, string message, string logPath) {
        var t = new System.Threading.Thread(() => {
            while (true) {
                System.Threading.Thread.Sleep(2000);
                long idle = (DateTime.UtcNow.Ticks - LastProgressTicks) / TimeSpan.TicksPerSecond;
                if (idle >= stallSeconds) {
                    try { System.IO.File.AppendAllText(logPath, message + Environment.NewLine); } catch {}
                    try { Console.Out.WriteLine(message); Console.Out.Flush(); } catch {}
                    Environment.Exit(7);
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }
}
"@ -ErrorAction Stop

$ErrorActionPreference = 'Stop'

# Ensure screenshot directory exists
if (-not (Test-Path $ScreenshotDir)) {
    New-Item -ItemType Directory -Path $ScreenshotDir -Force | Out-Null
}

# Global stall watchdog: UIA calls can BLOCK (not throw) when the provider is wedged,
# which per-call try/catch cannot interrupt. This background .NET thread runs independently
# of the (possibly blocked) main runspace and force-exits with a clear message if no progress
# (successful UIA call or screenshot) happens for $StallSeconds, instead of hanging until the
# 15-minute harness timeout. Healthy phases tick [YaguInput]::Progress() continuously, so a
# trip means UIA is genuinely stuck. (Longest legit wait is the ~120s preview poll, which also
# ticks progress, so 90s is safe.)
$StallSeconds = 90
[YaguInput]::Progress()
[YaguInput]::StartStallWatchdog($StallSeconds,
    "FATAL: test-match-nav.ps1 made no UI progress for ${StallSeconds}s; UI Automation is blocked/unresponsive. Aborting early (exit 7) instead of hanging until the harness timeout. The result tree is likely too large/slow for UIA here, or the desktop session is degraded.",
    (Join-Path $ScreenshotDir "watchdog.log"))

function Activate-YaguWindow {
    if (-not $script:yaguWindow) { return }
    try {
        $h = [IntPtr]$script:yaguWindow.Current.NativeWindowHandle
        if ($h -eq [IntPtr]::Zero) { return }

        # Synthetic mouse input goes to the real foreground window, so a failed activation would send
        # every click to whichever app owns the foreground instead of Yagu. Retry, then say so loudly.
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $ok = [YaguInput]::Activate($h)
            Start-Sleep -Milliseconds 300
            if ($ok) { return }
        }
        Write-Host "  WARNING: could not bring the Yagu window to the foreground; clicks may go elsewhere."
    } catch { }
}

function Get-YaguWindowHandle {
    if (-not $script:yaguWindow) { return [IntPtr]::Zero }
    try {
        return [IntPtr]$script:yaguWindow.Current.NativeWindowHandle
    } catch {
        return [IntPtr]::Zero
    }
}

function Take-Screenshot([string]$Name, [switch]$Fast) {
    if (-not $Fast) {
        Activate-YaguWindow
        Start-Sleep -Milliseconds 200
    }

    $path = Join-Path $ScreenshotDir "$Name.png"

    # Capture the Yagu window itself, not the screen. SetForegroundWindow is blocked by the Windows
    # foreground lock when the harness runs from a background process, so a CopyFromScreen grab silently
    # captured whichever window was actually on top (VS Code) and the pixel assertions then measured the
    # wrong app. PrintWindow with PW_RENDERFULLCONTENT reads the target window even when it is occluded.
    $h = Get-YaguWindowHandle
    if ($h -ne [IntPtr]::Zero) {
        $rect = New-Object YaguInput+RECT
        if ([YaguInput]::GetWindowRect($h, [ref]$rect)) {
            $w = $rect.Right - $rect.Left
            $ht = $rect.Bottom - $rect.Top
            if ($w -gt 0 -and $ht -gt 0) {
                $bmp = [System.Drawing.Bitmap]::new($w, $ht)
                $graphics = [System.Drawing.Graphics]::FromImage($bmp)
                $hdc = $graphics.GetHdc()
                $ok = [YaguInput]::PrintWindow($h, $hdc, [YaguInput]::PW_RENDERFULLCONTENT)
                $graphics.ReleaseHdc($hdc)
                $graphics.Dispose()
                if ($ok) {
                    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
                    $bmp.Dispose()
                    [YaguInput]::Progress()
                    if (-not $Fast) { Write-Host "  Screenshot saved: $path" }
                    return
                }
                $bmp.Dispose()
            }
        }
    }

    # Fallback: whole screen, only when the window handle or PrintWindow is unavailable.
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $graphics.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [YaguInput]::Progress()
    if (-not $Fast) { Write-Host "  Screenshot saved (screen fallback): $path" }
}

# --- UI Automation health watchdog ------------------------------------------
# UIA queries (FindFirst/FindAll) can transiently throw a timeout (COMException
# 0x80131505) when the result tree is large or the app is busy. A single timeout
# should not crash the run, but if UIA is *persistently* unresponsive we fail fast
# and loud rather than grind until the 15-minute harness timeout.
$script:UiaTimeoutStreak = 0
$script:UiaTimeoutLimit  = 8   # consecutive timeouts before declaring UIA dead

function Test-IsUiaTimeout {
    param($ErrorRecord)
    $ex = $ErrorRecord.Exception
    while ($ex) {
        if ($ex -is [System.TimeoutException]) { return $true }
        if ($ex.Message -match '0x80131505|timed out|timeout') { return $true }
        $ex = $ex.InnerException
    }
    return $false
}

function Reset-UiaTimeoutStreak { $script:UiaTimeoutStreak = 0; [YaguInput]::Progress() }

function Register-UiaTimeout {
    param([string]$Where)
    $script:UiaTimeoutStreak++
    if ($script:UiaTimeoutStreak -ge $script:UiaTimeoutLimit) {
        Write-Host ""
        Write-Host "FATAL: UI Automation is unresponsive - $($script:UiaTimeoutStreak) consecutive timeouts (last at $Where)."
        Write-Host "       Aborting early instead of hanging until the harness timeout."
        Write-Host "       The result tree is likely too large/slow for UIA in this environment"
        Write-Host "       (this test searches an entire drive). Reduce the corpus or run unelevated."
        exit 3
    }
}

function Find-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Parent,
        [string]$Name = $null,
        [string]$AutomationId = $null,
        [string]$ClassName = $null,
        [System.Windows.Automation.ControlType]$ControlType = $null,
        [int]$TimeoutSeconds = 10
    )
    
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $conditions = @()
        if ($Name) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name) }
        if ($AutomationId) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId) }
        if ($ClassName) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, $ClassName) }
        if ($ControlType) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType) }
        
        $condition = if ($conditions.Count -eq 1) { $conditions[0] } 
                     else { [System.Windows.Automation.AndCondition]::new($conditions) }
        
        # FindFirst can transiently throw a UIA timeout (COMException 0x80131505) when the
        # tree is large or the app is busy. Treat that like "not found yet" and keep retrying
        # until the deadline; the watchdog bails fast if UIA is persistently unresponsive.
        try {
            $el = $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            Reset-UiaTimeoutStreak
        } catch {
            if (-not (Test-IsUiaTimeout $_)) { throw }
            Register-UiaTimeout "Find-Element"
            $el = $null
        }
        if ($el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Find-AllElements {
    param(
        [System.Windows.Automation.AutomationElement]$Parent,
        [string]$Name = $null,
        [string]$AutomationId = $null,
        [System.Windows.Automation.ControlType]$ControlType = $null,
        [int]$TimeoutSeconds = 5
    )
    
    $conditions = @()
    if ($Name) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name) }
    if ($AutomationId) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId) }
    if ($ControlType) { $conditions += [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType) }
    
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } 
                 else { [System.Windows.Automation.AndCondition]::new($conditions) }
    
    # FindAll over Descendants can transiently throw a UIA timeout (COMException 0x80131505)
    # when the tree is large or the app is busy (e.g. right after a context menu opens). Retry
    # for a few seconds before giving up; the watchdog bails fast if UIA is persistently dead.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            $result = $Parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
            Reset-UiaTimeoutStreak
            return $result
        } catch {
            if (-not (Test-IsUiaTimeout $_)) { throw }
            Register-UiaTimeout "Find-AllElements"
            if ((Get-Date) -ge $deadline) {
                Write-Host "  [warn] FindAll timed out repeatedly; returning empty set: $($_.Exception.Message)"
                return $null
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Find-YaguWindow {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [int]$LaunchedProcessId,
        [string]$ExecutablePath,
        [int]$TimeoutSeconds = 15
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $window = Find-Element -Parent $Root -Name "Yagu" -ControlType ([System.Windows.Automation.ControlType]::Window) -TimeoutSeconds 1
        if ($window) { return $window }

        $candidateProcessIds = @($LaunchedProcessId)
        try {
            $candidateProcessIds += Get-CimInstance Win32_Process -Filter "Name = 'Yagu.exe'" |
                Where-Object { $_.ExecutablePath -eq $ExecutablePath } |
                Select-Object -ExpandProperty ProcessId
        } catch { }
        $candidateProcessIds = @($candidateProcessIds | Where-Object { $_ -gt 0 } | Select-Object -Unique)

        foreach ($candidatePid in $candidateProcessIds) {
            $pidCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$candidatePid)
            $window = $Root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCondition)
            if ($window) { return $window }
        }

        $windows = $Root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window))
        foreach ($candidate in $windows) {
            try {
                if ($candidate.Current.Name -like "Yagu*") {
                    if ($candidateProcessIds.Count -eq 0 -or $candidateProcessIds -contains $candidate.Current.ProcessId) {
                        return $candidate
                    }
                }
            } catch { }
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Click-Element([System.Windows.Automation.AutomationElement]$Element) {
    $invokePattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    if ($invokePattern) {
        $invokePattern.Invoke()
        return
    }
    # Fallback: click via coordinates
    $rect = $Element.Current.BoundingRectangle
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    Activate-YaguWindow
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($x, $y)
    Start-Sleep -Milliseconds 100
    [YaguInput]::LeftClick()
}

function LeftClick-At([int]$X, [int]$Y) {
    Activate-YaguWindow
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($X, $Y)
    Start-Sleep -Milliseconds 100
    [YaguInput]::LeftClick()
}

function ShiftLeftClick-At([int]$X, [int]$Y) {
    Activate-YaguWindow
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($X, $Y)
    Start-Sleep -Milliseconds 100
    [YaguInput]::ShiftLeftClick()
}

function RightClick-At([int]$X, [int]$Y) {
    Activate-YaguWindow
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($X, $Y)
    Start-Sleep -Milliseconds 150
    [YaguInput]::RightClick()
}

function Toggle-Checkbox([System.Windows.Automation.AutomationElement]$Element) {
    $togglePattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($togglePattern) {
        $togglePattern.Toggle()
    }
}

function Get-ToggleState([System.Windows.Automation.AutomationElement]$Element) {
    $togglePattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $togglePattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}

function Set-ToggleState(
    [System.Windows.Automation.AutomationElement]$Element,
    [bool]$Desired,
    [string]$Name) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if ((Get-ToggleState $Element) -eq $Desired) { return }
        Toggle-Checkbox $Element
        Start-Sleep -Milliseconds 300
    }
    throw "Could not set $Name to $Desired."
}

function Get-InnerEdit([System.Windows.Automation.AutomationElement]$Container) {
    if ($Container.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit) {
        return $Container
    }
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    return $Container.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Set-TextValue(
    [System.Windows.Automation.AutomationElement]$Container,
    [string]$Value,
    [string]$Name) {
    $edit = Get-InnerEdit $Container
    if (-not $edit) { throw "No editable text control found inside $Name." }
    $valuePattern = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($Value)
    Start-Sleep -Milliseconds 250
    $actual = $valuePattern.Current.Value
    if ($actual -cne $Value) {
        throw "$Name readback mismatch: expected '$Value', got '$actual'."
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# MAIN
# ──────────────────────────────────────────────────────────────────────────────

Write-Host "=== Yagu Match Navigation UI Test ==="
Write-Host "Directory: $Directory"
Write-Host "Query: $Query"
Write-Host "Options: Regex=$([bool]$UseRegex), Exact=$([bool]$ExactMatch), Multiline=$([bool]$Multiline)"
Write-Host ""

if ($Multiline -ne 0 -and ($UseRegex -eq 0 -or $ExactMatch -ne 0)) {
    throw "Multiline scenarios must request Regex on and Exact off because the UI enforces that combination."
}

# 1. Launch Yagu with directory and query
Write-Host "[1] Launching Yagu..."
# Point the app at an inert editor so any accidental double-tap on a result during
# UI automation does NOT launch the user's real editor (e.g. `code`). Launching `code`
# under an elevated VS Code pops a modal "Another instance of Code is already running as
# administrator" dialog that steals focus and hangs this script until it times out.
# The exe name does not exist, so EditorLauncher.Open fails silently (no window, no dialog).
$env:YAGU_EDITOR_COMMAND = 'yagu-ui-test-noop-editor --goto "{file}:{line}"'
$env:YAGU_MATCH_NAV_CONTEXT_COMPARE = '1'
$yaguExe = "C:\src\Yagu\src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"

# The app is single-instance. Refuse to hijack an existing Debug window rather than terminating a
# developer-owned session; the caller can close it explicitly and rerun the headed test.
$existingDebugYagu = @(Get-CimInstance Win32_Process -Filter "Name = 'Yagu.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -eq $yaguExe })
if ($existingDebugYagu.Count -gt 0) {
    Write-Error "A Debug Yagu instance is already running (PID(s): $($existingDebugYagu.ProcessId -join ', ')). Close it before running this headed test."
    exit 2
}

$proc = Start-Process -FilePath $yaguExe `
    -ArgumentList "--dir `"$Directory`" --window-mode traditional" `
    -PassThru

Start-Sleep -Seconds 5

# 2. Find the main window
Write-Host "[2] Finding Yagu window..."
$root = [System.Windows.Automation.AutomationElement]::RootElement
$yaguWindow = Find-YaguWindow -Root $root -LaunchedProcessId $proc.Id -ExecutablePath $yaguExe -TimeoutSeconds 15

if (-not $yaguWindow) {
    Write-Error "Could not find Yagu window!"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "  Found window: $($yaguWindow.Current.Name)"
$script:yaguWindow = $yaguWindow
Activate-YaguWindow

# Maximize the window first thing so all subsequent UI interactions and
# screenshots have the full screen real estate available.
Write-Host "  Maximizing Yagu window..."
try {
    $hwnd = [IntPtr]$yaguWindow.Current.NativeWindowHandle
    if ($hwnd -ne [IntPtr]::Zero) {
        [YaguInput]::Maximize($hwnd)
        Start-Sleep -Milliseconds 500
    }
} catch {
    Write-Host "  Warning: failed to maximize window: $_"
}

# 3. Configure the exact GUI option combination, set the query, and start one Traditional search.
Write-Host "[3] Configuring matching options and starting search..."
$regexToggle = Find-Element -Parent $yaguWindow -AutomationId "RegexToggle" -TimeoutSeconds 5
$exactToggle = Find-Element -Parent $yaguWindow -AutomationId "ExactMatchToggle" -TimeoutSeconds 5
$multilineToggle = Find-Element -Parent $yaguWindow -AutomationId "MultilineToggle" -TimeoutSeconds 5
if (-not $regexToggle -or -not $exactToggle -or -not $multilineToggle) {
    throw "Could not find RegexToggle, ExactMatchToggle, and MultilineToggle."
}

# Multiline mutates Regex and Exact in the view model. Reset it first, set the independent
# line-mode values, then enable it last when requested and verify the resulting live state.
Set-ToggleState $multilineToggle $false "Multiline"
Set-ToggleState $regexToggle ($UseRegex -ne 0) "Regex"
Set-ToggleState $exactToggle ($ExactMatch -ne 0) "Exact"
if ($Multiline -ne 0) {
    Set-ToggleState $multilineToggle $true "Multiline"
}

$actualRegex = Get-ToggleState $regexToggle
$actualExact = Get-ToggleState $exactToggle
$actualMultiline = Get-ToggleState $multilineToggle
if ($actualRegex -ne ($UseRegex -ne 0) `
    -or $actualExact -ne ($ExactMatch -ne 0) `
    -or $actualMultiline -ne ($Multiline -ne 0)) {
    throw "Matching-option readback mismatch: Regex=$actualRegex Exact=$actualExact Multiline=$actualMultiline."
}
Write-Host "  Verified options: Regex=$actualRegex Exact=$actualExact Multiline=$actualMultiline"

$queryBox = Find-Element -Parent $yaguWindow -AutomationId "QueryBox" -TimeoutSeconds 5
if (-not $queryBox) { throw "Could not find QueryBox." }
Set-TextValue $queryBox $Query "QueryBox"

$searchAction = Find-Element -Parent $yaguWindow -AutomationId "SearchSplitButton" -TimeoutSeconds 3
if (-not $searchAction) {
    $searchAction = Find-Element -Parent $yaguWindow -AutomationId "SearchCancelButton" -TimeoutSeconds 3
}
if (-not $searchAction) { throw "Could not find an idle Search action." }
Click-Element $searchAction

# 4. MatchesFound can advance from
# progress events before SearchEvent.MatchBatch has populated ResultRows, so a fixed delay followed
# by Cancel can leave the status showing matches while the result list is empty. Wait until the
# active Cancel action disappears and at least one file-group row has materialized instead.
Write-Host "[4] Waiting for search completion and materialized results (minimum ${SearchWaitSeconds}s)..."
Start-Sleep -Seconds $SearchWaitSeconds

$searchDeadline = (Get-Date).AddSeconds(120)
$searchReady = $false
$lastVisibleGroupCount = 0
$fileGroupCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "FileGroupCheckBox")
while ((Get-Date) -lt $searchDeadline) {
    $activeCancel = Find-Element -Parent $yaguWindow -AutomationId "SearchCancelButton" -TimeoutSeconds 1
    $isSearching = $false
    if ($activeCancel) {
        try {
            $cancelRect = $activeCancel.Current.BoundingRectangle
            $isSearching = -not $activeCancel.Current.IsOffscreen `
                -and $cancelRect.Width -gt 0 `
                -and $cancelRect.Height -gt 0 `
                -and $activeCancel.Current.Name -match '^Cancel'
        } catch { }
    }

    try {
        $fileGroups = $yaguWindow.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $fileGroupCondition)
        Reset-UiaTimeoutStreak
        $lastVisibleGroupCount = $fileGroups.Count
    } catch {
        if (-not (Test-IsUiaTimeout $_)) { throw }
        Register-UiaTimeout "search readiness"
    }

    if (-not $isSearching -and $lastVisibleGroupCount -gt 0) {
        $searchReady = $true
        break
    }

    [YaguInput]::Progress()
    Start-Sleep -Milliseconds 250
}

if (-not $searchReady) {
    Take-Screenshot "00-search-readiness-timeout"
    Write-Error "Search did not finish with materialized result rows within 120s (visible file groups: $lastVisibleGroupCount)."
    exit 1
}

# Give bindings one dispatcher turn to settle before selecting result groups.
Write-Host "[4] Search finished; $lastVisibleGroupCount file-group row(s) are currently materialized."
Start-Sleep -Milliseconds 500

Take-Screenshot "01-after-search"

<# 4b. Click the Sort dropdown and select "# Matches".
Write-Host "[4b] Setting sort mode to '# Matches'..."
$sortCombo = $null
$comboBoxes = Find-AllElements -Parent $yaguWindow -ControlType ([System.Windows.Automation.ControlType]::ComboBox)
foreach ($cb in $comboBoxes) {
    try {
        $expandPattern = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if (-not $expandPattern) { continue }
        $expandPattern.Expand()
        Start-Sleep -Milliseconds 250

        $matchItem = $cb.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, "# Matches"))
        if (-not $matchItem) {
            # Some popups attach to root; search globally too
            $matchItem = $root.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::NameProperty, "# Matches"))
        }

        if ($matchItem) {
            $sortCombo = $cb
            try {
                $selPattern = $matchItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                if ($selPattern) {
                    $selPattern.Select()
                } else {
                    Click-Element $matchItem
                }
            } catch {
                Click-Element $matchItem
            }
            Write-Host "  Selected '# Matches' in sort dropdown."
            break
        }
        $expandPattern.Collapse()
    } catch { }
}
if (-not $sortCombo) {
    Write-Host "  WARNING: Could not find Sort dropdown with '# Matches' option."
}
Start-Sleep -Seconds 1
#>

# 5. Select up to $MaxFiles files via Shift+click range selection.
# Strategy: click the FIRST file checkbox, then scroll down N items, then Shift+click that
# checkbox. The OnSelectAllChecked handler in MainWindow.xaml.cs detects Shift state and
# directly iterates ViewModel.ResultGroups[lo..hi] calling SelectAll() — which COMPLETELY
# bypasses the WinUI 3 ListView container recycling problem (clicking 100 individual
# checkboxes races with virtualization and only ~28% of clicks update the model).
Write-Host "[5] Selecting up to $MaxFiles files via Shift+click range selection..."

# Find the results ListView
$resultsList = Find-Element -Parent $yaguWindow -AutomationId "ResultsList" -TimeoutSeconds 5
if (-not $resultsList) {
    Write-Host "  WARNING: ResultsList not found by AutomationId; falling back to first List."
    $resultsList = Find-Element -Parent $yaguWindow -ControlType ([System.Windows.Automation.ControlType]::List) -TimeoutSeconds 5
}
if ($resultsList) {
    Write-Host "  ResultsList found: ControlType=$($resultsList.Current.ControlType.ProgrammaticName), AutomationId=$($resultsList.Current.AutomationIdProperty)"
} else {
    Write-Host "  WARNING: ResultsList is NULL"
}

$selectedCount = 0

if ($resultsList) {
    $scrollPattern = $null
    try { $scrollPattern = $resultsList.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch {}

    $fileCbCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "FileGroupCheckBox")

    $listRect = $resultsList.Current.BoundingRectangle
    Write-Host ("  ResultsList viewport: X={0:N0} Y={1:N0} W={2:N0} H={3:N0}" -f $listRect.X, $listRect.Y, $listRect.Width, $listRect.Height)

    # Helper: get all onscreen file-level checkboxes inside the viewport, sorted top-to-bottom.
    function Get-OnscreenFileCheckboxes {
        $all = $resultsList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $fileCbCondition)
        $list = @()
        foreach ($el in $all) {
            try {
                if ($el.Current.IsOffscreen) { continue }
                $r = $el.Current.BoundingRectangle
                if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
                $cy = $r.Y + $r.Height / 2
                if ($cy -lt $listRect.Y -or $cy -gt ($listRect.Y + $listRect.Height)) { continue }
                $list += [pscustomobject]@{ Element = $el; Rect = $r; CY = $cy }
            } catch { }
        }
        return $list | Sort-Object CY
    }

    # Step A: scroll to top, click the first file checkbox.
    if ($scrollPattern -and $scrollPattern.Current.VerticallyScrollable) {
        try { $scrollPattern.SetScrollPercent(-1, 0) } catch { }
        Start-Sleep -Milliseconds 500
    }

    $firstCheckboxes = Get-OnscreenFileCheckboxes
    if (-not $firstCheckboxes -or $firstCheckboxes.Count -eq 0) {
        Write-Host "  ERROR: No file-level checkboxes visible at top of list."
    } else {
        $first = $firstCheckboxes[0]
        $fx = [int]($first.Rect.X + $first.Rect.Width / 2)
        $fy = [int]($first.CY)
        Write-Host "  Clicking FIRST checkbox at ($fx, $fy)..."
        LeftClick-At $fx $fy
        Start-Sleep -Milliseconds 400

        # Step B: scroll progressively until we've seen approximately $MaxFiles unique
        # file checkboxes, OR reached end of list. Track UNIQUE checkboxes by their
        # Name (file path) so the count reflects reality rather than a per-page
        # estimate. The previous heuristic (estPerPage * iter + visible) consistently
        # undershot — each LargeIncrement scrolls by viewport pixels which can span
        # far more file groups than were visible at the top (variable row heights
        # when matches are expanded), so the loop kept going and selected ~186.
        $targetItems = $MaxFiles
        $scrollIter = 0
        $maxScrollIter = 200
        $lastCb = $first
        $seenNames = [System.Collections.Generic.HashSet[string]]::new()
        # Parallel ordered list so we can deterministically pick the item at
        # index ($targetItems - 1) once we've overshot. HashSet iteration order
        # is undefined in .NET, so relying on it caused the script to select
        # ~2000 files instead of ~500.
        $orderedNames = New-Object System.Collections.Generic.List[string]
        # Map from file Name → onscreen checkbox record (most recent sighting),
        # so that once we know the cutoff Name we can recover its rect/CY for
        # the shift-click target without scrolling back.
        $cbByName = @{}
        # Seed the ordered list with ALL checkboxes currently onscreen at the top of the list (not
        # just the first). For a small, non-scrollable result list the scroll loop below breaks
        # immediately, so this seed is the only chance to record them — without it $orderedNames
        # stays empty and the cutoff lookup below dereferences a null key. (The previous code used
        # `if ([void]$seenNames.Add(...))`, whose [void] cast makes the condition always false, so
        # even the first checkbox was never recorded.)
        foreach ($cb in $firstCheckboxes) {
            try {
                $n = $cb.Element.Current.Name
                if ($n) {
                    if ($seenNames.Add($n)) { $orderedNames.Add($n) }
                    $cbByName[$n] = $cb
                }
            } catch { }
        }

        while ($scrollIter -lt $maxScrollIter) {
            if ($seenNames.Count -ge $targetItems) {
                Write-Host "  Seen $($seenNames.Count) unique file checkboxes (target=$targetItems); stopping scroll."
                break
            }

            $scrollPos = -1
            if ($scrollPattern -and $scrollPattern.Current.VerticallyScrollable) {
                $scrollPos = $scrollPattern.Current.VerticalScrollPercent
                if ($scrollPos -ge 99.5) {
                    Write-Host "  Reached end of list (scroll=$scrollPos%)."
                    break
                }
                try { $scrollPattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::LargeIncrement) } catch { break }
            } else { break }

            Start-Sleep -Milliseconds 40

            $visible = Get-OnscreenFileCheckboxes
            foreach ($v in $visible) {
                try {
                    $n = $v.Element.Current.Name
                    if ($seenNames.Add($n)) { $orderedNames.Add($n) }
                    $cbByName[$n] = $v
                } catch { }
            }

            $scrollIter++
        }
        Write-Host "  Scroll done: iter=$scrollIter, uniqueSeen=$($seenNames.Count), target=$targetItems"

        # Deterministically pick the cutoff: the item at index ($targetItems - 1)
        # in scroll order, clamped to whatever we actually saw.
        $cutoffIdx = [Math]::Min($targetItems - 1, $orderedNames.Count - 1)
        if ($cutoffIdx -lt 0) { $cutoffIdx = 0 }
        $cutoffName = $orderedNames[$cutoffIdx]
        if ($cutoffName -and $cbByName.ContainsKey($cutoffName)) {
            $lastCb = $cbByName[$cutoffName]
        }

        # If the cutoff item isn't currently onscreen, scroll it back into view
        # before the shift+click. Otherwise the click coordinates point at a
        # stale rect and we'd select the wrong range.
        $needsRescroll = $true
        try {
            $curOnscreen = Get-OnscreenFileCheckboxes
            foreach ($v in $curOnscreen) {
                try {
                    if ($v.Element.Current.Name -eq $cutoffName) {
                        $lastCb = $v
                        $needsRescroll = $false
                        break
                    }
                } catch { }
            }
        } catch { }

        if ($needsRescroll -and $scrollPattern -and $scrollPattern.Current.VerticallyScrollable) {
            # Cutoff scrolled past viewport. Walk backward in small increments
            # until we find it on screen again.
            for ($s = 0; $s -lt 50; $s++) {
                try { $scrollPattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::SmallDecrement) } catch { break }
                Start-Sleep -Milliseconds 40
                $curOnscreen = Get-OnscreenFileCheckboxes
                $found = $false
                foreach ($v in $curOnscreen) {
                    try {
                        if ($v.Element.Current.Name -eq $cutoffName) {
                            $lastCb = $v
                            $found = $true
                            break
                        }
                    } catch { }
                }
                if ($found) { break }
            }
        }

        $effectiveCount = $cutoffIdx + 1
        Write-Host "  Cutoff item index=$cutoffIdx (selecting $effectiveCount file(s))"

        # Step C: Shift+click the last visible checkbox to select the entire range.
        $lx = [int]($lastCb.Rect.X + $lastCb.Rect.Width / 2)
        $ly = [int]($lastCb.CY)
        Write-Host "  Shift+clicking LAST checkbox at ($lx, $ly) to range-select..."
        ShiftLeftClick-At $lx $ly
        Start-Sleep -Milliseconds 600

        $selectedCount = $effectiveCount  # best-effort estimate; real count comes from Pre-right-click diagnostics
    }
} else {
    Write-Host "  WARNING: Could not find results list."
}

Write-Host "  Selected approximately $selectedCount file(s) via range selection"

# Headed corpus scenarios know their exact file cardinality. Route selection through the app's
# ResultsList Ctrl+A handler so every model group is selected even when UI virtualization exposes
# only a subset of file checkboxes to UI Automation.
if ($ExpectedFiles -gt 0 -and $resultsList) {
    Write-Host "  Selecting all $ExpectedFiles model file group(s) through ResultsList Ctrl+A..."
    Activate-YaguWindow
    $focusCheckboxes = @(Get-OnscreenFileCheckboxes)
    if ($focusCheckboxes.Count -eq 0) {
        throw "Could not find a focusable file checkbox before model-wide Ctrl+A selection."
    }
    try { $focusCheckboxes[0].Element.SetFocus() } catch {
        throw "Could not focus a ResultsList checkbox before model-wide Ctrl+A selection: $($_.Exception.Message)"
    }
    Start-Sleep -Milliseconds 200
    [YaguInput]::ControlA()
    [YaguInput]::Progress()
    Start-Sleep -Milliseconds 600
}

# Helper: count how many file-level checkboxes report ToggleState=On right now (UIA tree).
function Count-CheckedFileCheckboxes {
    param($listElement)
    if (-not $listElement) { return @{ Total = 0; Checked = 0; Onscreen = 0; CheckedOnscreen = 0 } }
    $cond = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "FileGroupCheckBox")
    $all = $listElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $total = 0; $checked = 0; $onscreen = 0; $checkedOnscreen = 0
    foreach ($el in $all) {
        $total++
        $isOff = $false
        try { $isOff = $el.Current.IsOffscreen } catch {}
        if (-not $isOff) { $onscreen++ }
        try {
            $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
                $checked++
                if (-not $isOff) { $checkedOnscreen++ }
            }
        } catch {}
    }
    return @{ Total = $total; Checked = $checked; Onscreen = $onscreen; CheckedOnscreen = $checkedOnscreen }
}

$preStats = Count-CheckedFileCheckboxes -listElement $resultsList
Write-Host ("  Pre-right-click checkbox state: total={0}, checked={1}, onscreen={2}, checkedOnscreen={3}" -f `
    $preStats.Total, $preStats.Checked, $preStats.Onscreen, $preStats.CheckedOnscreen)

# "Preview all selected (N)" stays Collapsed unless MORE THAN ONE file is checked, so a selection that
# nets 0 or 1 leaves the menu item out of the UIA tree entirely and step 6 cannot open the preview.
# The coordinate range-select above does exactly that on a small corpus: when the first and last visible
# checkbox are the same element, the plain click checks it and the Shift+click unchecks it again.
# Fall back to toggling each checkbox directly, which is deterministic and needs no pointer input.
if ($preStats.Checked -le 1 -and $resultsList) {
    Write-Host "  Selection netted $($preStats.Checked) file(s); toggling checkboxes via UI Automation instead..."
    $cond = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "FileGroupCheckBox")
    $all = $resultsList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $toggled = 0
    foreach ($el in $all) {
        if ($toggled -ge $MaxFiles) { break }
        try {
            $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
                $tp.Toggle()
                Start-Sleep -Milliseconds 60
            }
            $toggled++
        } catch { }
    }
    [YaguInput]::Progress()
    $preStats = Count-CheckedFileCheckboxes -listElement $resultsList
    Write-Host ("  After UIA toggle: total={0}, checked={1}" -f $preStats.Total, $preStats.Checked)
}

Start-Sleep -Seconds 1

# 6. Right-click on a file group header to open the "Preview selected" context menu.
# The "Preview selected" MenuFlyout is attached to the StackPanel inside the Expander header,
# NOT to individual match-line ListItems. We scroll to top first, then right-click the first
# Expander header text area.
Write-Host "[6] Right-clicking file group header for 'Preview selected'..."

# Scroll list back to top so we have a visible header to right-click
if ($resultsList) {
    try {
        $sp2 = $resultsList.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($sp2) { $sp2.SetScrollPercent(-1, 0) }
    } catch { }
    Start-Sleep -Milliseconds 500
}

# Find an Expander element (group header) with a valid bounding rect inside ResultsList
$clickTarget = $null
$searchParent = if ($resultsList) { $resultsList } else { $yaguWindow }
$expanders = Find-AllElements -Parent $searchParent -ControlType ([System.Windows.Automation.ControlType]::Group)
if (-not $expanders -or $expanders.Count -eq 0) {
    # WinUI Expander might expose as different control types; try TreeItem or custom
    $expanders = Find-AllElements -Parent $searchParent -ControlType ([System.Windows.Automation.ControlType]::TreeItem)
}
# Fallback: look for ListItem elements (some WinUI versions expose group rows as ListItems)
if (-not $expanders -or $expanders.Count -eq 0) {
    $expanders = Find-AllElements -Parent $searchParent -ControlType ([System.Windows.Automation.ControlType]::ListItem)
}
foreach ($exp in $expanders) {
    $r = $exp.Current.BoundingRectangle
    if (-not [double]::IsNaN($r.X) -and $r.Width -gt 0 -and $r.Height -gt 0) {
        $clickTarget = $exp
        break
    }
}

if ($clickTarget) {
    $rect = $clickTarget.Current.BoundingRectangle
    # Right-click slightly right of center to land on the file name text, inside the StackPanel
    # that owns the context flyout
    $cx = [int]($rect.X + $rect.Width * 0.4)
    $cy = [int]($rect.Y + $rect.Height / 2)
    $rcStats = Count-CheckedFileCheckboxes -listElement $resultsList
    Write-Host ("  About to right-click at ({0},{1}). Checkbox state: total={2}, checked={3}, onscreen={4}, checkedOnscreen={5}" -f `
        $cx, $cy, $rcStats.Total, $rcStats.Checked, $rcStats.Onscreen, $rcStats.CheckedOnscreen)
    # The header MenuFlyout only opens on a real right-click over the header. The VK_APPS
    # context-menu key targets the focused checkbox instead, so the flyout never opened and
    # "Preview all selected (N)" was absent from the UIA tree.
    RightClick-At $cx $cy
    Start-Sleep -Seconds 1
    $postRcStats = Count-CheckedFileCheckboxes -listElement $resultsList
    Write-Host ("  After right-click (menu open): total={0}, checked={1}, onscreen={2}, checkedOnscreen={3}" -f `
        $postRcStats.Total, $postRcStats.Checked, $postRcStats.Onscreen, $postRcStats.CheckedOnscreen)
    
    # Find "Preview selected" menu item (context menu attaches to root in WinUI)
    $previewMenuItem = $null
    $deadline2 = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline2) {
        $allMenuItems = Find-AllElements -Parent $root -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
        foreach ($mi in $allMenuItems) {
            # The item renders as "Preview all selected (N)", so an exact "Preview selected" match never hit.
            if ($mi.Current.Name -match 'Preview all selected') {
                $previewMenuItem = $mi
                break
            }
        }
        if ($previewMenuItem) { break }
        Start-Sleep -Milliseconds 300
    }
    
    if ($previewMenuItem) {
        $previewMenuLabel = $previewMenuItem.Current.Name
        if ($ExpectedFiles -gt 0) {
            $expectedPreviewMenuLabel = "Preview all selected ($ExpectedFiles)"
            if ($previewMenuLabel -ne $expectedPreviewMenuLabel) {
                throw "Model-wide selection mismatch before preview: expected '$expectedPreviewMenuLabel', got '$previewMenuLabel'."
            }
            Write-Host "  Verified model selection before preview: $previewMenuLabel"
        }
        Click-Element $previewMenuItem
        Write-Host "  Clicked 'Preview selected'"
    } else {
        Write-Host "  WARNING: Could not find 'Preview selected' menu item. Trying AutomationId..."
        # Dismiss any wrong menu first
        try { [System.Windows.Forms.SendKeys]::SendWait("{ESC}") } catch {
            Write-Host "  Warning: could not dismiss the context menu through SendKeys: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 300
        # Fallback: use the AutomationId "CtxPreviewSelected" if the app exposes it
        $ctxBtn = Find-Element -Parent $yaguWindow -AutomationId "CtxPreviewSelected" -TimeoutSeconds 3
        if ($ctxBtn) {
            Click-Element $ctxBtn
            Write-Host "  Clicked via AutomationId fallback"
        } else {
            Write-Host "  WARNING: Could not invoke Preview selected at all"
        }
    }
} else {
    Write-Host "  WARNING: No group header elements found with valid bounding rectangle"
}

# 7. Wait for preview to finish loading. Strategy:
#    - Poll for any UI element whose Name contains "Adding" (e.g. status text like
#      "Adding 12 of 100..." that the app shows while populating the preview).
#    - If we never see it within a short grace window, assume the preview is
#      already populated and proceed immediately (no point waiting).
#    - If we DO see it, wait until it disappears, then a small settle delay.
Write-Host "[7] Waiting for preview to render (max ${PreviewLoadSeconds}s)..."

function Find-AddingElement {
    param([System.Windows.Automation.AutomationElement]$Window)
    try {
        $all = $Window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        [YaguInput]::Progress()
        foreach ($el in $all) {
            try {
                $name = $el.Current.Name
                if ($name -and $name -match 'Adding' -and -not $el.Current.IsOffscreen) {
                    return $el
                }
            } catch { }
        }
    } catch { }
    return $null
}

# Short grace window to detect "Adding"; if it never appears, just proceed.
$detectDeadline = (Get-Date).AddSeconds(2)
$adding = $null
while ((Get-Date) -lt $detectDeadline) {
    $adding = Find-AddingElement -Window $yaguWindow
    if ($adding) { break }
    Start-Sleep -Milliseconds 200
}

if (-not $adding) {
    Write-Host "  No 'Adding' indicator detected within grace window; proceeding."
} else {
    Write-Host "  Detected 'Adding...' indicator: '$($adding.Current.Name)'. Waiting for it to disappear..."
    $loadDeadline = (Get-Date).AddSeconds($PreviewLoadSeconds)
    while ((Get-Date) -lt $loadDeadline) {
        $stillAdding = Find-AddingElement -Window $yaguWindow
        if (-not $stillAdding) {
            Write-Host "  'Adding' indicator gone — preview ready."
            Start-Sleep -Milliseconds 500
            break
        }
        Start-Sleep -Milliseconds 300
    }
    if ((Get-Date) -ge $loadDeadline) {
        Write-Host "  WARNING: timed out waiting for 'Adding' indicator to disappear."
    }
}

Take-Screenshot "02-preview-loaded"

# 8. Click "Next match" button repeatedly and take screenshots
Write-Host "[8] Navigating matches (up to $MatchIterations iterations)..."

# Resolve the Next-match button and match-label once. The UI elements are
# stable for the lifetime of the preview, so re-finding them every iteration
# was burning ~hundreds of ms per click for no benefit.
#
# The MatchNavPanel is Visibility=Collapsed until the preview has populated and
# a match is active, so on big corpora the button can take a while to appear.
# Use a generous timeout here.
$nextBtn = Find-Element -Parent $yaguWindow -AutomationId "NextMatchButton" -TimeoutSeconds 120
if (-not $nextBtn) {
    $nextBtn = Find-Element -Parent $yaguWindow -Name "Next match (↓)" -TimeoutSeconds 10
}
if (-not $nextBtn) {
    Write-Error "Next match button not found before navigation loop."
    exit 1
}
$matchLabel = Find-Element -Parent $yaguWindow -AutomationId "MatchNavLabel" -TimeoutSeconds 5
if (-not $matchLabel) {
    Write-Error "Match navigation label not found before navigation loop."
    exit 1
}
$previewScroller = Find-Element -Parent $yaguWindow -AutomationId "PreviewScrollViewer" -TimeoutSeconds 5
if (-not $previewScroller) {
    Write-Error "PreviewScrollViewer not found before navigation loop."
    exit 1
}

function Get-PreviewViewportCaptureBounds {
    $h = Get-YaguWindowHandle
    if ($h -eq [IntPtr]::Zero) { return $null }
    $windowRect = New-Object YaguInput+RECT
    if (-not [YaguInput]::GetWindowRect($h, [ref]$windowRect)) { return $null }
    try { $previewRect = $previewScroller.Current.BoundingRectangle } catch { return $null }
    if ($previewRect.Width -le 0 -or $previewRect.Height -le 0) { return $null }
    return [pscustomobject]@{
        X      = [int][Math]::Floor($previewRect.X - $windowRect.Left)
        Y      = [int][Math]::Floor($previewRect.Y - $windowRect.Top)
        Width  = [int][Math]::Ceiling($previewRect.Width)
        Height = [int][Math]::Ceiling($previewRect.Height)
    }
}

function Get-MatchNavState {
    param([System.Windows.Automation.AutomationElement]$Element)
    try { $labelText = $Element.Current.Name } catch { return $null }
    if ($labelText -notmatch '^Occurrence\s+(\d+)\s*/\s*(\d+)\s+\((\d+)\s+files?\)$') {
        return $null
    }
    return [pscustomobject]@{
        Current = [int]$Matches[1]
        Total   = [int]$Matches[2]
        Files   = [int]$Matches[3]
        Label   = $labelText
    }
}

function ConvertFrom-ContextField([string]$Value) {
    try {
        return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
    } catch {
        throw "Invalid Base64 field in match-context probe: $($_.Exception.Message)"
    }
}

function Get-MatchContextComparison {
    param([System.Windows.Automation.AutomationElement]$Element)

    try { $payload = $Element.Current.HelpText } catch {
        throw "Could not read match-context probe: $($_.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($payload)) {
        throw "Match-context probe is empty for the active occurrence."
    }

    $fields = @($payload -split '\|')
    if ($fields.Count -ge 3 -and $fields[0] -eq 'v1' -and $fields[1] -eq 'error') {
        throw "Match-context probe reported an error: $($fields[2])"
    }
    if ($fields.Count -ne 11 -or $fields[0] -ne 'v1' -or $fields[1] -ne 'ok') {
        throw "Malformed match-context probe payload."
    }

    $leftBefore = ConvertFrom-ContextField $fields[5]
    $leftMatch = ConvertFrom-ContextField $fields[6]
    $leftAfter = ConvertFrom-ContextField $fields[7]
    $rightBefore = ConvertFrom-ContextField $fields[8]
    $rightMatch = ConvertFrom-ContextField $fields[9]
    $rightAfter = ConvertFrom-ContextField $fields[10]
    if ($leftBefore -cne $rightBefore -or $leftMatch -cne $rightMatch -or $leftAfter -cne $rightAfter) {
        $file = ConvertFrom-ContextField $fields[2]
        throw "Left/right match context differs for '$file' line $($fields[3]), column $($fields[4]): " +
            "left=[$leftBefore][$leftMatch][$leftAfter], right=[$rightBefore][$rightMatch][$rightAfter]."
    }

    return [pscustomobject]@{
        Status       = 'PASS'
        File64       = $fields[2]
        Line         = [int]$fields[3]
        Column       = [int]$fields[4]
        Before64     = $fields[5]
        Match64      = $fields[6]
        After64      = $fields[7]
        MatchText    = $leftMatch
    }
}

$initialNavState = Get-MatchNavState -Element $matchLabel
if (-not $initialNavState) {
    Write-Error "Unexpected match navigation label: '$($matchLabel.Current.Name)'"
    exit 1
}

$navigationManifest = Join-Path $ScreenshotDir "navigation.tsv"
Set-Content -LiteralPath $navigationManifest `
    -Value "Screenshot`tOccurrence`tTotal`tFiles`tViewportX`tViewportY`tViewportWidth`tViewportHeight`tLabel`tContextStatus`tContextFile64`tContextLine`tContextColumn`tContextBefore64`tContextMatch64`tContextAfter64" -Encoding utf8

for ($i = 1; $i -le $MatchIterations; $i++) {
    $before = Get-MatchNavState -Element $matchLabel
    if (-not $before) {
        Write-Error "Match navigation label became unreadable before iteration $i."
        exit 1
    }
    if ($before.Current -ge $before.Total) {
        Write-Host "  Reached last occurrence ($($before.Current) of $($before.Total)); stopping."
        break
    }

    Click-Element $nextBtn
    $advanceDeadline = (Get-Date).AddSeconds(8)
    $after = $null
    while ((Get-Date) -lt $advanceDeadline) {
        Start-Sleep -Milliseconds 100
        $after = Get-MatchNavState -Element $matchLabel
        if ($after -and $after.Current -ne $before.Current) { break }
        [YaguInput]::Progress()
    }
    if (-not $after -or $after.Current -ne ($before.Current + 1)) {
        $afterText = if ($after) { $after.Label } else { "<unreadable>" }
        Write-Error "Next occurrence did not advance exactly once: before='$($before.Label)', after='$afterText'."
        exit 1
    }

    Start-Sleep -Milliseconds 400
    $context = Get-MatchContextComparison -Element $matchLabel
    $screenshotName = "03-match-{0:D4}" -f $after.Current
    $viewport = Get-PreviewViewportCaptureBounds
    if (-not $viewport) {
        Write-Error "Could not resolve preview viewport bounds for occurrence $($after.Current)."
        exit 1
    }
    Take-Screenshot $screenshotName -Fast
    $safeLabel = $after.Label -replace "[`t`r`n]", " "
    Add-Content -LiteralPath $navigationManifest `
        -Value "$screenshotName.png`t$($after.Current)`t$($after.Total)`t$($after.Files)`t$($viewport.X)`t$($viewport.Y)`t$($viewport.Width)`t$($viewport.Height)`t$safeLabel`t$($context.Status)`t$($context.File64)`t$($context.Line)`t$($context.Column)`t$($context.Before64)`t$($context.Match64)`t$($context.After64)" `
        -Encoding utf8
    Write-Host "  NAV screenshot=$screenshotName.png occurrence=$($after.Current) total=$($after.Total) files=$($after.Files) context=PASS"
}

Write-Host ""
Write-Host "=== Test complete. Screenshots saved to: $ScreenshotDir ==="

# Close only the launcher and GUI child created by this run. The GUI self-relaunches with the original
# process as its parent, so parent-ID scoping avoids touching any unrelated Yagu process.
$launchedIds = [System.Collections.Generic.HashSet[int]]::new()
$null = $launchedIds.Add($proc.Id)
do {
    $added = $false
    $children = @(Get-CimInstance Win32_Process -Filter "Name = 'Yagu.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $launchedIds.Contains([int]$_.ParentProcessId) -and $_.ExecutablePath -eq $yaguExe })
    foreach ($child in $children) {
        if ($launchedIds.Add([int]$child.ProcessId)) { $added = $true }
    }
} while ($added)
foreach ($launchedId in $launchedIds) {
    Stop-Process -Id $launchedId -Force -ErrorAction SilentlyContinue
}
Write-Host "Review the screenshots to verify match is always centered in the viewport."

# Don't kill the app - leave it open for manual inspection
