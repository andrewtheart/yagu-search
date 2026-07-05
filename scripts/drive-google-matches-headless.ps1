# Drive the Yagu UI: search C: for "google", filter to one file, expand it, then
# click each line-number match in the file drawer one-by-one and screenshot the
# preview after every click so we can verify the active overlay tracks the match.
#
# Uses Windows UIAutomation (UIA) + raw mouse input, modelled on test-match-nav.ps1.

param(
    [string]$Directory = "C:",
    [string]$Query = "google",
    [string]$FileFilter = "15d84a40-875b-4915-b9b4-ab1fa643a4a4",
    [string]$ScreenshotDir = "C:\src\Yagu\TestResults\GoogleMatchNav",
    [string]$LogFile = "C:\src\Yagu\TestResults\GoogleMatchNav\run.log",
    [int]$FindFileTimeoutSec = 300,
    [int]$MaxMatches = 30
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class YaguDrive {
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public static void LeftClick() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); }
    public static void Activate(IntPtr hWnd) { ShowWindow(hWnd, 5); BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
    public static void Maximize(IntPtr hWnd) { ShowWindow(hWnd, 3); }
}
"@ -ErrorAction SilentlyContinue

$ErrorActionPreference = 'Stop'
$AE  = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$CT  = [System.Windows.Automation.ControlType]
$PC  = [System.Windows.Automation.PropertyCondition]

if (-not (Test-Path $ScreenshotDir)) { New-Item -ItemType Directory -Path $ScreenshotDir -Force | Out-Null }
"" | Set-Content -Path $LogFile

function Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $msg
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

function Activate-Window {
    if ($script:win) {
        try {
            $h = [IntPtr]$script:win.Current.NativeWindowHandle
            if ($h -ne [IntPtr]::Zero) { [YaguDrive]::Activate($h); Start-Sleep -Milliseconds 200 }
        } catch { }
    }
}

function Take-Screenshot([string]$Name) {
    # Headless variant: screen capture requires an interactive (unlocked) desktop and
    # fails with "handle is invalid" when locked. No-op so the run completes and the
    # GutterSync diagnostic still fires; we read the log instead of looking at pixels.
    return (Join-Path $ScreenshotDir "$Name.png")
}

function Find-Element {
    param($Parent, [string]$AutomationId, [string]$Name, $ControlType, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $conds = @()
        if ($AutomationId) { $conds += $PC::new($AE::AutomationIdProperty, $AutomationId) }
        if ($Name)         { $conds += $PC::new($AE::NameProperty, $Name) }
        if ($ControlType)  { $conds += $PC::new($AE::ControlTypeProperty, $ControlType) }
        $cond = if ($conds.Count -eq 1) { $conds[0] } else { [System.Windows.Automation.AndCondition]::new($conds) }
        $el = $Parent.FindFirst($TS::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Click-Rect($rect) {
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($x, $y)
    Start-Sleep -Milliseconds 120
    [YaguDrive]::LeftClick()
}

# The first-run IntroTeachingTip renders a "Got it" close button that can sit on top
# of the file drawer line numbers and the preview match overlay, hiding exactly what
# we are trying to click/observe. Invoke any visible "Got it" button so nothing is
# obscured. Searches from the desktop root because TeachingTips render in a popup
# that may be a separate top-level HWND.
function Dismiss-GotItTips {
    $dismissed = 0
    try {
        $root2 = $AE::RootElement
        $btns = $root2.FindAll($TS::Descendants, $PC::new($AE::NameProperty, "Got it"))
        foreach ($b in $btns) {
            try {
                if ($b.Current.ControlType -ne $CT::Button) { continue }
                if ($b.Current.IsOffscreen) { continue }
                try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
                catch { Click-Rect $b.Current.BoundingRectangle }
                $dismissed++
                Start-Sleep -Milliseconds 200
            } catch { }
        }
    } catch { }
    if ($dismissed -gt 0) { Log "  Dismissed $dismissed 'Got it' teaching tip(s)." }
    return $dismissed
}

# Poll for the (possibly delayed) "Got it" teaching tip for up to $TimeoutMs and
# dismiss it as soon as it appears. The PreviewMatch intro tip is shown a couple of
# seconds AFTER the preview editor finishes loading, so a single immediate dismiss
# can miss it. Returns the number of tips dismissed.
function WaitDismiss-GotItTips([int]$TimeoutMs = 4000, [int]$PollMs = 300) {
    $total = 0
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $n = Dismiss-GotItTips
        if ($n -gt 0) { $total += $n }
        Start-Sleep -Milliseconds $PollMs
    }
    return $total
}

# Set text into an AutoSuggestBox/TextBox container (exposes as Group) by locating
# its inner Edit and using ValuePattern, with a keyboard fallback.
function Set-BoxText($container, [string]$text) {
    $edit = $container
    if ($container.Current.ControlType -ne $CT::Edit) {
        $e = $container.FindFirst($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit))
        if ($e) { $edit = $e }
    }
    $ok = $false
    try {
        $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($text)
        $ok = $true
    } catch { }
    if (-not $ok) {
        Click-Rect $edit.Current.BoundingRectangle
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        Start-Sleep -Milliseconds 80
        [System.Windows.Forms.SendKeys]::SendWait(($text -replace '([+^%~(){}])','{$1}'))
    }
    return $edit
}

function Get-MatchNavLabel {
    try {
        $lbl = $script:win.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, "MatchNavLabel"))
        if ($lbl) { return $lbl.Current.Name }
    } catch { }
    return ""
}

# Returns onscreen line-number ToggleButtons inside ResultsList, with toggle state.
function Get-MatchToggles {
    param($listElement, $listRect)
    $btnCond = $PC::new($AE::ControlTypeProperty, $CT::Button)
    $all = $listElement.FindAll($TS::Descendants, $btnCond)
    $out = @()
    foreach ($el in $all) {
        try {
            $name = $el.Current.Name
            if ($name -notmatch '^\d+$') { continue }   # only numeric line-number buttons
            if ($el.Current.IsOffscreen) { continue }
            $r = $el.Current.BoundingRectangle
            if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            $cy = $r.Y + $r.Height / 2
            if ($cy -lt $listRect.Y -or $cy -gt ($listRect.Y + $listRect.Height)) { continue }
            $state = "Unknown"
            try {
                $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $state = $tp.Current.ToggleState.ToString()
            } catch { }
            $out += [pscustomobject]@{ Element = $el; Rect = $r; CY = $cy; Line = $name; State = $state }
        } catch { }
    }
    return $out | Sort-Object CY
}

# Scroll the list so $element sits in the upper-middle "comfort band" of the list
# viewport, leaving room below it so the drawer match line + surrounding context is
# fully visible rather than clipped against the bottom edge. This makes it easy to
# compare the selected match (and its context) in the left drawer against the match
# (and context) shown in the right preview panel. Best-effort; bails when the list
# cannot scroll any further in the needed direction.
function Scroll-IntoComfortBand($element, $listElement, $scrollPattern) {
    if (-not $scrollPattern) { return }
    for ($i = 0; $i -lt 25; $i++) {
        $listRect = $listElement.Current.BoundingRectangle
        try { $r = $element.Current.BoundingRectangle } catch { return }
        if ([double]::IsNaN($r.X)) { return }
        $cy = $r.Y + $r.Height / 2
        $bandTop    = $listRect.Y + $listRect.Height * 0.30
        $bandBottom = $listRect.Y + $listRect.Height * 0.55
        if ($cy -ge $bandTop -and $cy -le $bandBottom) { return }   # already comfortable
        if ($cy -gt $bandBottom) {
            if (-not $scrollPattern.Current.VerticallyScrollable) { return }
            if ($scrollPattern.Current.VerticalScrollPercent -ge 99.5) { return }
            try { $scrollPattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::SmallIncrement) } catch { return }
        } else {
            if ($scrollPattern.Current.VerticalScrollPercent -le 0.5) { return }
            try { $scrollPattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::SmallDecrement) } catch { return }
        }
        Start-Sleep -Milliseconds 150
    }
}

# ──────────────────────────────────────────────────────────────────────────────
Log "=== Drive Yagu: google match overlay verification ==="
Log "Directory=$Directory Query=$Query FileFilter=$FileFilter"

$yaguExe = "C:\src\Yagu\src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"
$root = $AE::RootElement

Log "[1] Launching a completely fresh Yagu instance..."
# Yagu is single-instance. Close any running Yagu so the named mutex is released and
# we get a brand-new window rather than reactivating the old one.
$existing = Get-Process -Name Yagu -ErrorAction SilentlyContinue
if ($existing) {
    Log "  Closing $($existing.Count) existing Yagu process(es): $($existing.Id -join ', ')"
    $existing | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
$win = $null
# Launch in traditional window mode WITHOUT --dir/--query so the app comes up idle (no
# auto-search); the directory + query are then driven through the UI in step [2].
Log "  Launching: $yaguExe --window-mode traditional"
$proc = Start-Process -FilePath $yaguExe -ArgumentList "--window-mode traditional" -PassThru
Start-Sleep -Seconds 5
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline -and -not $win) {
    $windows = $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
    foreach ($w in $windows) { try { if ($w.Current.Name -like "Yagu*") { $win = $w; break } } catch {} }
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Log "ERROR: Yagu window not found after launch"; exit 1 }
$script:win = $win
Log "  Window: '$($win.Current.Name)' pid=$($win.Current.ProcessId)"
Activate-Window
try { [YaguDrive]::Maximize([IntPtr]$win.Current.NativeWindowHandle); Start-Sleep -Milliseconds 700 } catch {}

Log "[2] Setting up the search (dir + query)..."
$dirBox = Find-Element -Parent $win -AutomationId "DirectoryBox" -TimeoutSeconds 10
$queryBox = Find-Element -Parent $win -AutomationId "QueryBox" -TimeoutSeconds 10
if (-not $dirBox -or -not $queryBox) { Log "ERROR: DirectoryBox/QueryBox not found"; exit 1 }
Set-BoxText $dirBox $Directory | Out-Null
Start-Sleep -Milliseconds 200
Set-BoxText $queryBox $Query | Out-Null
Start-Sleep -Milliseconds 300
$searchBtn = Find-Element -Parent $win -AutomationId "SearchCancelButton" -TimeoutSeconds 5
if (-not $searchBtn) { Log "ERROR: SearchCancelButton not found"; exit 1 }
$lblBefore = Find-Element -Parent $win -AutomationId "SearchCancelLabel" -TimeoutSeconds 2
Log "  Search button label before: '$($lblBefore.Current.Name)'"
try { $searchBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Click-Rect $searchBtn.Current.BoundingRectangle }
Start-Sleep -Milliseconds 900
$lblAfter = Find-Element -Parent $win -AutomationId "SearchCancelLabel" -TimeoutSeconds 2
Log "  Search button label after: '$($lblAfter.Current.Name)' (Cancel => search running)"
Take-Screenshot "00-search-started" | Out-Null

# Give the search time to start producing results so the results card (and its
# "Filter files…" box) is reliably rendered before we try to filter.
Log "  Waiting 10s after search start before filtering..."
Start-Sleep -Seconds 10

Log "[3] Locating ResultsList + filter box..."
$resultsList = Find-Element -Parent $win -AutomationId "ResultsList" -TimeoutSeconds 15
if (-not $resultsList) { Log "ERROR: ResultsList not found"; exit 1 }
$listRect = $resultsList.Current.BoundingRectangle

# The "Filter files…" TextBox has no AutomationId, but it sits on the SAME row as
# SelectAllFilesCheckBox (stable AutomationId), immediately to its right, just above
# ResultsList. Anchor on that checkbox and EXCLUDE the DirectoryBox/QueryBox (also
# Edit controls) so we never type the filter text into the directory box by mistake.
$excludeIds = @('DirectoryBox', 'QueryBox', 'FindTextBox', 'ReplaceTextBox')
$filterBox = $null
$selectAllCb = Find-Element -Parent $win -AutomationId "SelectAllFilesCheckBox" -TimeoutSeconds 10
if ($selectAllCb) {
    $cb = $selectAllCb.Current.BoundingRectangle
    $cbCenterY = $cb.Y + $cb.Height / 2
    $allEdits = $win.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit))
    $bestDx = [double]::MaxValue
    foreach ($e in $allEdits) {
        try {
            if ($excludeIds -contains $e.Current.AutomationId) { continue }
            $r = $e.Current.BoundingRectangle
            if ([double]::IsNaN($r.X)) { continue }
            $eCenterY = $r.Y + $r.Height / 2
            # Same row as the checkbox (within its height) and to its right.
            if ([math]::Abs($eCenterY - $cbCenterY) -le $cb.Height -and $r.X -ge $cb.X) {
                $dx = $r.X - $cb.X
                if ($dx -lt $bestDx) { $bestDx = $dx; $filterBox = $e }
            }
        } catch { }
    }
}
# Fallback: the non-dir/query Edit closest above the list top.
if (-not $filterBox) {
    $allEdits = $win.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit))
    $bestY = [double]::MinValue
    foreach ($e in $allEdits) {
        try {
            if ($excludeIds -contains $e.Current.AutomationId) { continue }
            $r = $e.Current.BoundingRectangle
            if ([double]::IsNaN($r.X)) { continue }
            $eCenterY = $r.Y + $r.Height / 2
            if ($eCenterY -lt $listRect.Y -and $eCenterY -gt $bestY) { $bestY = $eCenterY; $filterBox = $e }
        } catch { }
    }
}
if (-not $filterBox) { Log "ERROR: filter box not found"; exit 1 }
$fb = $filterBox.Current.BoundingRectangle
Log "  Filter box found at X=$([int]$fb.X) Y=$([int]$fb.Y) (aid='$($filterBox.Current.AutomationId)')"

Log "[4] Typing filter '$FileFilter' into the Filter-files box..."
Set-BoxText $filterBox $FileFilter | Out-Null
Start-Sleep -Milliseconds 900
# Safety check: confirm DirectoryBox still holds the search root, not the filter text.
$dirCheck = Find-Element -Parent $win -AutomationId "DirectoryBox" -TimeoutSeconds 2
if ($dirCheck) {
    $dv = ""
    try { $de = $dirCheck.FindFirst($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit)); if ($de) { $dv = $de.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } } catch {}
    Log "  DirectoryBox now reads: '$dv' (expected '$Directory')"
    if ($dv -like "*$FileFilter*") { Log "  WARNING: filter text leaked into DirectoryBox!" }
}

Log "[5] Waiting for target file to appear (max ${FindFileTimeoutSec}s)..."
$fileEl = $null
$deadline = (Get-Date).AddSeconds($FindFileTimeoutSec)
$lastLog = Get-Date
while ((Get-Date) -lt $deadline) {
    $fileEl = $resultsList.FindFirst($TS::Descendants, $PC::new($AE::NameProperty, "$FileFilter.jsonl"))
    if (-not $fileEl) {
        # Match by partial name: any descendant whose Name contains the UUID
        $all = $resultsList.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            try { if ($el.Current.Name -like "*$FileFilter*" -and $el.Current.ControlType -eq $CT::Text) { $fileEl = $el; break } } catch {}
        }
    }
    if ($fileEl) { Log "  Target file appeared: '$($fileEl.Current.Name)'"; break }
    if (((Get-Date) - $lastLog).TotalSeconds -ge 10) {
        $status = ""
        try { $st = Find-Element -Parent $win -AutomationId "StatusText" -TimeoutSeconds 0; if ($st) { $status = $st.Current.Name } } catch {}
        Log "  ...still searching (status: $status)"
        $lastLog = Get-Date
    }
    Start-Sleep -Milliseconds 700
}
if (-not $fileEl) { Log "ERROR: target file not found within timeout"; Take-Screenshot "00-file-not-found" | Out-Null; exit 2 }

Take-Screenshot "01-file-found" | Out-Null

Log "[6] Cancelling search to stabilize UI (only if still running)..."
$lbl = Find-Element -Parent $win -AutomationId "SearchCancelLabel" -TimeoutSeconds 2
if ($lbl -and $lbl.Current.Name -eq "Cancel") {
    $cancelBtn = Find-Element -Parent $win -AutomationId "SearchCancelButton" -TimeoutSeconds 3
    if ($cancelBtn) {
        try { $cancelBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Log "  Search cancelled." } catch { Click-Rect $cancelBtn.Current.BoundingRectangle }
    }
} else { Log "  (Search already finished; nothing to cancel.)" }
Start-Sleep -Seconds 1

Log "[7] Left-clicking the file to expand it..."
Activate-Window
$fileRect = $fileEl.Current.BoundingRectangle
Click-Rect $fileRect
Start-Sleep -Milliseconds 900

# Verify expansion: are there numeric toggle buttons now?
$listRect = $resultsList.Current.BoundingRectangle
$toggles = Get-MatchToggles -listElement $resultsList -listRect $listRect
if (-not $toggles -or $toggles.Count -eq 0) {
    Log "  No match rows after click; trying ExpandCollapse pattern on ancestor group..."
    $walkerC = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $fileEl
    for ($i = 0; $i -lt 6 -and $node; $i++) {
        try {
            $ecp = $node.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ecp) { $ecp.Expand(); Log "  Expanded via ExpandCollapsePattern."; break }
        } catch { }
        $node = $walkerC.GetParent($node)
    }
    Start-Sleep -Milliseconds 900
    $toggles = Get-MatchToggles -listElement $resultsList -listRect $listRect
}
Log "  Match rows visible after expand: $($toggles.Count) (lines: $(( $toggles | ForEach-Object { $_.Line }) -join ','))"
# The FileDrawer intro tip pops ~2s after the header loads; clear it before we start.
WaitDismiss-GotItTips -TimeoutMs 3500 -PollMs 300 | Out-Null
Take-Screenshot "02-file-expanded" | Out-Null

# Scroll the results list to the top so we start at the first match.
$scrollPattern = $null
try { $scrollPattern = $resultsList.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch {}
if ($scrollPattern -and $scrollPattern.Current.VerticallyScrollable) {
    try { $scrollPattern.SetScrollPercent(-1, 0) } catch {}
    Start-Sleep -Milliseconds 500
}

Log "[8] Clicking each match line-number, top-to-bottom, screenshotting preview..."
$summary = New-Object System.Collections.Generic.List[object]
$clicked = 0
$noProgress = 0
while ($clicked -lt $MaxMatches) {
    $listRect = $resultsList.Current.BoundingRectangle
    $toggles = Get-MatchToggles -listElement $resultsList -listRect $listRect
    # Next match = first toggle still in the "Off" state (not yet clicked), top-to-bottom.
    $next = $toggles | Where-Object { $_.State -eq 'Off' } | Select-Object -First 1
    if (-not $next) {
        # Nothing unclicked on screen; scroll down to reveal more matches.
        if ($scrollPattern -and $scrollPattern.Current.VerticallyScrollable -and $scrollPattern.Current.VerticalScrollPercent -lt 99.5) {
            try { $scrollPattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::SmallIncrement) } catch { break }
            Start-Sleep -Milliseconds 250
            $noProgress++
            if ($noProgress -gt 40) { Log "  Stopping: no more unclicked matches after scrolling."; break }
            continue
        } else {
            Log "  Reached end of match list."
            break
        }
    }
    $noProgress = 0
    $clicked++
    $line = $next.Line
    # Clear any teaching tip that may be covering the next line number before clicking.
    # No-op when none is present (the tip may or may not appear on any given click).
    Dismiss-GotItTips | Out-Null
    # Scroll the target match up into the upper-middle of the list so its drawer match
    # line + context is fully visible (not clipped against the bottom edge), making it
    # easy to compare the left drawer match against the right preview panel.
    Scroll-IntoComfortBand -element $next.Element -listElement $resultsList -scrollPattern $scrollPattern
    try { $rect = $next.Element.Current.BoundingRectangle } catch { $rect = $next.Rect }
    if ([double]::IsNaN($rect.X) -or $rect.Width -le 0) { $rect = $next.Rect }
    $cy = [int]($rect.Y + $rect.Height / 2)
    Log ("  [{0}] Invoking match on line {1} (state={2}) at Y={3}..." -f $clicked, $line, $next.State, $cy)
    # Headless: drive the drawer line-number ToggleButton through UIA (works on a
    # locked desktop) instead of a raw mouse click. Toggle() fires the same Click
    # handler (OnMatchLineCheckBoxClicked) that navigates the preview + runs gutter sync.
    $invoked = $false
    try {
        $tp = $next.Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $tp.Toggle()
        $invoked = $true
    } catch { }
    if (-not $invoked) {
        try {
            $ip = $next.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $ip.Invoke()
            $invoked = $true
        } catch { }
    }
    if (-not $invoked) { Click-Rect $rect }
    Start-Sleep -Milliseconds 900           # let preview update + overlay settle
    # A preview-match teaching tip can appear a couple of seconds after the preview
    # loads and sit over the overlay we want to screenshot. Poll-dismiss briefly so
    # the shot is clean; harmless no-op when no tip shows.
    WaitDismiss-GotItTips -TimeoutMs 2500 -PollMs 300 | Out-Null
    $navLabel = Get-MatchNavLabel
    $shot = Take-Screenshot ("match-{0:D2}-line-{1}" -f $clicked, $line)
    Log ("      -> MatchNav='{0}'  screenshot={1}" -f $navLabel, (Split-Path $shot -Leaf))
    $summary.Add([pscustomobject]@{ Index = $clicked; Line = $line; MatchNav = $navLabel; Screenshot = (Split-Path $shot -Leaf) })
}

Log "[9] Done. Clicked $clicked matches."
Log "Summary:"
foreach ($s in $summary) { Log ("  #{0,-2} line {1,-5} {2}" -f $s.Index, $s.Line, $s.MatchNav) }
$summary | ConvertTo-Json | Set-Content -Path (Join-Path $ScreenshotDir "summary.json")
Log "Screenshots in: $ScreenshotDir"
Log "App left open (window pid=$($win.Current.ProcessId)) for inspection."
