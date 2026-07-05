# UI Automation script to test match navigation centering in Yagu.
# Uses Windows UIAutomation via .NET to interact with the WinUI 3 app.

param(
    [string]$Directory = "C:",
    [string]$Query = "a",
    [string]$ScreenshotDir = "C:\src\Yagu\TestResults\MatchNavScreenshots",
    [int]$MatchIterations = 200,
    [int]$SearchWaitSeconds = 15,
    [int]$PreviewLoadSeconds = 120,
    [int]$MaxFiles = 500
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
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const byte VK_SHIFT = 0x10;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public static void LeftClick() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); }
    public static void RightClick() { mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero); }
    public static void ShiftLeftClick() {
        keybd_event(VK_SHIFT, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }
    public static void Activate(IntPtr hWnd) { ShowWindow(hWnd, 5); BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
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
"@ -ErrorAction SilentlyContinue

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
    if ($script:yaguWindow) {
        try {
            $h = [IntPtr]$script:yaguWindow.Current.NativeWindowHandle
            if ($h -ne [IntPtr]::Zero) {
                [YaguInput]::Activate($h)
                Start-Sleep -Milliseconds 300
            }
        } catch { }
    }
}

function Take-Screenshot([string]$Name, [switch]$Fast) {
    if (-not $Fast) {
        Activate-YaguWindow
        Start-Sleep -Milliseconds 200
    }
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $graphics.Dispose()
    $path = Join-Path $ScreenshotDir "$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [YaguInput]::Progress()
    if (-not $Fast) { Write-Host "  Screenshot saved: $path" }
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
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($x, $y)
    Start-Sleep -Milliseconds 100
    [YaguInput]::LeftClick()
}

function RightClick-At([int]$X, [int]$Y) {
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($X, $Y)
    Start-Sleep -Milliseconds 200
    [YaguInput]::RightClick()
}

function LeftClick-At([int]$X, [int]$Y) {
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($X, $Y)
    Start-Sleep -Milliseconds 100
    [YaguInput]::LeftClick()
}

function ShiftLeftClick-At([int]$X, [int]$Y) {
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($X, $Y)
    Start-Sleep -Milliseconds 100
    [YaguInput]::ShiftLeftClick()
}

function Toggle-Checkbox([System.Windows.Automation.AutomationElement]$Element) {
    $togglePattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($togglePattern) {
        $togglePattern.Toggle()
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# MAIN
# ──────────────────────────────────────────────────────────────────────────────

Write-Host "=== Yagu Match Navigation UI Test ==="
Write-Host "Directory: $Directory"
Write-Host "Query: $Query"
Write-Host ""

# 1. Launch Yagu with directory and query
Write-Host "[1] Launching Yagu..."
# Point the app at an inert editor so any accidental double-tap on a result during
# UI automation does NOT launch the user's real editor (e.g. `code`). Launching `code`
# under an elevated VS Code pops a modal "Another instance of Code is already running as
# administrator" dialog that steals focus and hangs this script until it times out.
# The exe name does not exist, so EditorLauncher.Open fails silently (no window, no dialog).
$env:YAGU_EDITOR_COMMAND = 'yagu-ui-test-noop-editor --goto "{file}:{line}"'
$yaguExe = "C:\src\Yagu\src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"
$proc = Start-Process -FilePath $yaguExe `
    -ArgumentList "--dir `"$Directory`" --query `"$Query`" --window-mode traditional" `
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

# 3. The app auto-searches when launched with --dir and --query. Wait for search.
Write-Host "[3] Waiting ${SearchWaitSeconds}s for search to gather results..."
Start-Sleep -Seconds $SearchWaitSeconds

# 4. Click Cancel button to stop the search
Write-Host "[4] Clicking Cancel to stop search..."
$cancelBtn = Find-Element -Parent $yaguWindow -AutomationId "SearchCancelButton" -TimeoutSeconds 5
if (-not $cancelBtn) {
    # Try by name - it might show "Cancel" text
    $cancelBtn = Find-Element -Parent $yaguWindow -Name "Cancel" -ControlType ([System.Windows.Automation.ControlType]::Button) -TimeoutSeconds 5
}
if ($cancelBtn) {
    Click-Element $cancelBtn
    Write-Host "  Cancelled search."
} else {
    Write-Host "  Cancel button not found (search may have finished already)."
}
Start-Sleep -Seconds 2

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
        try {
            if ([void]$seenNames.Add($first.Element.Current.Name)) {
                $orderedNames.Add($first.Element.Current.Name)
                $cbByName[$first.Element.Current.Name] = $first
            }
        } catch { }

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
        if ($cbByName.ContainsKey($cutoffName)) {
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
            if ($mi.Current.Name -match "Preview selected") {
                $previewMenuItem = $mi
                break
            }
        }
        if ($previewMenuItem) { break }
        Start-Sleep -Milliseconds 300
    }
    
    if ($previewMenuItem) {
        Click-Element $previewMenuItem
        Write-Host "  Clicked 'Preview selected'"
    } else {
        Write-Host "  WARNING: Could not find 'Preview selected' menu item. Trying AutomationId..."
        # Dismiss any wrong menu first
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
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

for ($i = 1; $i -le $MatchIterations; $i++) {
    # Check the match label (e.g. "Match 5 of 500") to see if we've reached the last match.
    if ($matchLabel) {
        $labelText = $null
        try { $labelText = $matchLabel.Current.Name } catch { }
        if ($labelText -and $labelText -match 'Match\s+(\d+)\s+of\s+(\d+)') {
            $cur = [int]$Matches[1]
            $total = [int]$Matches[2]
            if ($cur -ge $total) {
                Write-Host "  Reached last match ($cur of $total) — stopping."
                break
            }
        }
    }

    Click-Element $nextBtn
    Start-Sleep -Milliseconds 500
    # After clicking Next on iteration $i (starting at match 1), we are now on
    # match ($i + 1). Name the screenshot to reflect the actual match number
    # so 03-match-NN.png shows match NN. Match 1 is captured in 02-preview-loaded.
    Take-Screenshot ("03-match-{0:D2}" -f ($i + 1)) -Fast
}

Write-Host ""
Write-Host "=== Test complete. Screenshots saved to: $ScreenshotDir ==="
Write-Host "Review the screenshots to verify match is always centered in the viewport."

# Don't kill the app - leave it open for manual inspection
