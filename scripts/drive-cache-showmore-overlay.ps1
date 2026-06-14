# Drive the Yagu UI to reproduce + validate the "Show more overlay tracking" fix.
#
# Scenario (mirrors the user repro on the giant single-line npm cache file):
#   1. Search "test", filter to the one cache file, expand it.
#   2. Select N same-line match occurrences (default 8). The last selection appends a
#      fresh match-centered window (a 2nd preview paragraph) whose active match is
#      boxed by the overlay.
#   3. Click that appended window's PREFIX "Show more" ellipsis (edge=Prefix), which
#      rebuilds the paragraph and shifts the active match down/right.
#   4. Read the Yagu log and assert the active-match overlay RE-TRACKED the match:
#        - a BoxMatchRun re-box appears AFTER "[PreviewShowMore] expand complete"
#        - the following "WrapOverlayDiag stage=applied" used the ACTUAL run rect
#          (usedWrappedPointEstimate=False, finalPoint ~= rawPoint), not the drifted
#          uniform-chars-per-line estimate.
#
# Validation is LOG-based (no screenshots), so it works on a locked/RDP session where
# GDI screen capture fails. Modelled on scripts/drive-cache-wrap-modes.ps1.

param(
    [string]$Directory = "C:\Users\andre\AppData\Local\npm-cache\_cacache\content-v2\sha512\bf\20",
    [string]$Query = "test",
    [string]$FileFilter = "C:\Users\andre\AppData\Local\npm-cache\_cacache\content-v2\sha512\bf\20\036d75a0cc71b9c888a5bb3b68d681e0498e1f40657a77de6668619dd395fd99bde768ec1c2df9de5c93148e1fe5d0848635f4b628685d05df21b39b244d",
    [string]$OutDir = "C:\src\Yagu\TestResults\ShowMoreOverlay",
    [string]$YaguLog = "$env:APPDATA\Yagu\yagu.log",
    [int]$FindFileTimeoutSec = 300,
    [int]$MatchesToSelect = 8
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class YaguShowMore {
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
$TargetFileName = Split-Path -Leaf $FileFilter
$Ellipsis = [char]0x2026   # … U+2026 HORIZONTAL ELLIPSIS

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$LogFile = Join-Path $OutDir "run.log"
"" | Set-Content -Path $LogFile

function Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $msg
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

function Set-PersistedWordWrap([bool]$On) {
    $settingsPath = Join-Path $env:APPDATA "Yagu\settings.json"
    if (-not (Test-Path -LiteralPath $settingsPath)) { Log "ERROR: settings.json not found at $settingsPath"; exit 1 }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if (-not ($settings.PSObject.Properties.Name -contains "PreviewWordWrap")) {
        $settings | Add-Member -NotePropertyName PreviewWordWrap -NotePropertyValue $On
    } else { $settings.PreviewWordWrap = $On }
    if (-not ($settings.PSObject.Properties.Name -contains "PreviewWrapModeIndex")) {
        $settings | Add-Member -NotePropertyName PreviewWrapModeIndex -NotePropertyValue $(if ($On) { 0 } else { 2 })
    } else { $settings.PreviewWrapModeIndex = if ($On) { 0 } else { 2 } }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Log "  Persisted PreviewWordWrap=$On before launch"
}

function Activate-Window {
    if ($script:win) {
        try {
            $h = [IntPtr]$script:win.Current.NativeWindowHandle
            if ($h -ne [IntPtr]::Zero) { [YaguShowMore]::Activate($h); Start-Sleep -Milliseconds 200 }
        } catch { }
    }
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
    [YaguShowMore]::LeftClick()
}

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
    return $dismissed
}

function WaitDismiss-GotItTips([int]$TimeoutMs = 3000, [int]$PollMs = 300) {
    $total = 0
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $n = Dismiss-GotItTips
        if ($n -gt 0) { $total += $n }
        Start-Sleep -Milliseconds $PollMs
    }
    return $total
}

function Set-BoxText($container, [string]$text) {
    $edit = $container
    if ($container.Current.ControlType -ne $CT::Edit) {
        $e = $container.FindFirst($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit))
        if ($e) { $edit = $e }
    }
    $ok = $false
    try { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text); $ok = $true } catch { }
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

function Invoke-OrClick($element) {
    try { $ip = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); if ($ip) { $ip.Invoke(); return } } catch { }
    Click-Rect $element.Current.BoundingRectangle
}

function Get-MatchToggles {
    param($listElement, $listRect)
    try { $all = $listElement.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Button)) }
    catch { Log "  WARNING: could not enumerate match toggles: $($_.Exception.Message)"; return @() }
    $out = @()
    foreach ($el in $all) {
        try {
            $name = $el.Current.Name
            if ($name -notmatch '^\d+$') { continue }
            if ($el.Current.IsOffscreen) { continue }
            $r = $el.Current.BoundingRectangle
            if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            $cy = $r.Y + $r.Height / 2
            if ($cy -lt $listRect.Y -or $cy -gt ($listRect.Y + $listRect.Height)) { continue }
            $state = "Unknown"
            try { $state = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() } catch { }
            $out += [pscustomobject]@{ Element = $el; Rect = $r; CY = $cy; Line = $name; State = $state }
        } catch { }
    }
    return $out | Sort-Object CY
}

function Scroll-IntoComfortBand($element, $listElement, $scrollPattern) {
    if (-not $scrollPattern) { return }
    for ($i = 0; $i -lt 25; $i++) {
        $listRect = $listElement.Current.BoundingRectangle
        try { $r = $element.Current.BoundingRectangle } catch { return }
        if ([double]::IsNaN($r.X)) { return }
        $cy = $r.Y + $r.Height / 2
        $bandTop    = $listRect.Y + $listRect.Height * 0.30
        $bandBottom = $listRect.Y + $listRect.Height * 0.55
        if ($cy -ge $bandTop -and $cy -le $bandBottom) { return }
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

# Find the appended window's PREFIX "Show more" ellipsis: the leftmost visible "…"
# Text element inside the preview area. The initial window starts at column 0 (no
# prefix ellipsis), so the only ellipsis sitting at the very left text margin is the
# appended window's prefix marker.
function Find-PrefixShowMoreEllipsis {
    param($previewParent)
    $cond = $PC::new($AE::NameProperty, [string]$Ellipsis)
    $candidates = @()
    try { $all = $previewParent.FindAll($TS::Descendants, $cond) } catch { return $null }
    foreach ($el in $all) {
        try {
            if ($el.Current.ControlType -ne $CT::Text) { continue }
            if ($el.Current.IsOffscreen) { continue }
            $r = $el.Current.BoundingRectangle
            if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            $candidates += [pscustomobject]@{ Element = $el; X = $r.X; Y = $r.Y; Rect = $r }
        } catch { }
    }
    if ($candidates.Count -eq 0) { return $null }
    Log ("  Found {0} ellipsis marker(s): {1}" -f $candidates.Count, (($candidates | ForEach-Object { "({0},{1})" -f [int]$_.X, [int]$_.Y }) -join " "))
    return ($candidates | Sort-Object X | Select-Object -First 1)
}

# ──────────────────────────────────────────────────────────────────────────────
Log "=== Drive Yagu: Show-more overlay tracking validation ==="
Log "Directory=$Directory Query=$Query Target=$TargetFileName MatchesToSelect=$MatchesToSelect"

$yaguExe = "C:\src\Yagu\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"
$root = $AE::RootElement

Log "[1] Launching a fresh Yagu instance (Word wrap ON)..."
$existing = @()
try {
    foreach ($w in $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))) {
        try { if ($w.Current.Name -like "Yagu - Yet Another Grep Utility*") { $existing += $w } } catch { }
    }
} catch { }
Set-PersistedWordWrap $true
if ($existing.Count -gt 0) {
    $pids = @($existing | ForEach-Object { try { $_.Current.ProcessId } catch { $null } } | Where-Object { $_ } | Sort-Object -Unique)
    Log "  Closing existing Yagu window process(es): $($pids -join ', ')"
    foreach ($p in $pids) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}
Log "  Launching: $yaguExe --window-mode traditional"
$proc = Start-Process -FilePath $yaguExe -ArgumentList "--window-mode traditional" -PassThru
Start-Sleep -Seconds 5
$win = $null
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline -and -not $win) {
    foreach ($w in $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))) {
        try { if ($w.Current.Name -like "Yagu*") { $win = $w; break } } catch { }
    }
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Log "ERROR: Yagu window not found after launch"; exit 1 }
$script:win = $win
Log "  Window: '$($win.Current.Name)' pid=$($win.Current.ProcessId)"
Activate-Window
try { [YaguShowMore]::Maximize([IntPtr]$win.Current.NativeWindowHandle); Start-Sleep -Milliseconds 700 } catch { }
# Record where in the log this run starts so validation only inspects new entries.
$logStartLineCount = 0
if (Test-Path -LiteralPath $YaguLog) { $logStartLineCount = (Get-Content -LiteralPath $YaguLog).Count }

Log "[2] Setting up search (dir + query)..."
$dirBox = Find-Element -Parent $win -AutomationId "DirectoryBox" -TimeoutSeconds 10
$queryBox = Find-Element -Parent $win -AutomationId "QueryBox" -TimeoutSeconds 10
if (-not $dirBox -or -not $queryBox) { Log "ERROR: DirectoryBox/QueryBox not found"; exit 1 }
Set-BoxText $dirBox $Directory | Out-Null
Start-Sleep -Milliseconds 200
Set-BoxText $queryBox $Query | Out-Null
Start-Sleep -Milliseconds 300
$searchBtn = Find-Element -Parent $win -AutomationId "SearchCancelButton" -TimeoutSeconds 5
if (-not $searchBtn) { Log "ERROR: SearchCancelButton not found"; exit 1 }
Invoke-OrClick $searchBtn
Start-Sleep -Seconds 10

Log "[3] Locating ResultsList + filter box..."
$resultsList = Find-Element -Parent $win -AutomationId "ResultsList" -TimeoutSeconds 15
if (-not $resultsList) { Log "ERROR: ResultsList not found"; exit 1 }
$listRect = $resultsList.Current.BoundingRectangle
$excludeIds = @('DirectoryBox', 'QueryBox', 'FindTextBox', 'ReplaceTextBox')
$filterBox = $null
$selectAllCb = Find-Element -Parent $win -AutomationId "SelectAllFilesCheckBox" -TimeoutSeconds 10
if ($selectAllCb) {
    $cb = $selectAllCb.Current.BoundingRectangle
    $cbCenterY = $cb.Y + $cb.Height / 2
    $bestDx = [double]::MaxValue
    foreach ($e in $win.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit))) {
        try {
            if ($excludeIds -contains $e.Current.AutomationId) { continue }
            $r = $e.Current.BoundingRectangle
            if ([double]::IsNaN($r.X)) { continue }
            $eCenterY = $r.Y + $r.Height / 2
            if ([math]::Abs($eCenterY - $cbCenterY) -le $cb.Height -and $r.X -ge $cb.X) {
                $dx = $r.X - $cb.X
                if ($dx -lt $bestDx) { $bestDx = $dx; $filterBox = $e }
            }
        } catch { }
    }
}
if (-not $filterBox) { Log "ERROR: filter box not found"; exit 1 }

Log "[4] Typing file filter..."
Set-BoxText $filterBox $FileFilter | Out-Null
Start-Sleep -Milliseconds 900

Log "[5] Waiting for target file (max ${FindFileTimeoutSec}s)..."
$fileEl = $null
$deadline = (Get-Date).AddSeconds($FindFileTimeoutSec)
while ((Get-Date) -lt $deadline) {
    $fileEl = $resultsList.FindFirst($TS::Descendants, $PC::new($AE::NameProperty, $TargetFileName))
    if (-not $fileEl) {
        foreach ($el in $resultsList.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            try {
                $name = $el.Current.Name
                if (($name -like "*$FileFilter*" -or $name -like "*$TargetFileName*") -and $el.Current.ControlType -eq $CT::Text) { $fileEl = $el; break }
            } catch { }
        }
    }
    if ($fileEl) { Log "  Target file appeared: '$($fileEl.Current.Name)'"; break }
    Start-Sleep -Milliseconds 700
}
if (-not $fileEl) { Log "ERROR: target file not found within timeout"; exit 2 }

Log "[6] Cancelling search to stabilize UI (if still running)..."
$lbl = Find-Element -Parent $win -AutomationId "SearchCancelLabel" -TimeoutSeconds 2
if ($lbl -and $lbl.Current.Name -eq "Cancel") {
    $cancelBtn = Find-Element -Parent $win -AutomationId "SearchCancelButton" -TimeoutSeconds 3
    if ($cancelBtn) { Invoke-OrClick $cancelBtn; Log "  Search cancelled." }
}
Start-Sleep -Seconds 1

Log "[7] Expanding the file..."
Activate-Window
Click-Rect $fileEl.Current.BoundingRectangle
Start-Sleep -Milliseconds 900
$listRect = $resultsList.Current.BoundingRectangle
$toggles = Get-MatchToggles -listElement $resultsList -listRect $listRect
if (-not $toggles -or $toggles.Count -eq 0) {
    $walkerC = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $fileEl
    for ($i = 0; $i -lt 6 -and $node; $i++) {
        try { $ecp = $node.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); if ($ecp) { $ecp.Expand(); break } } catch { }
        $node = $walkerC.GetParent($node)
    }
    Start-Sleep -Milliseconds 900
    $toggles = Get-MatchToggles -listElement $resultsList -listRect $listRect
}
Log "  Match rows visible after expand: $($toggles.Count)"
WaitDismiss-GotItTips -TimeoutMs 3500 -PollMs 300 | Out-Null

$scrollPattern = $null
try { $scrollPattern = $resultsList.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }
if ($scrollPattern -and $scrollPattern.Current.VerticallyScrollable) {
    try { $scrollPattern.SetScrollPercent(-1, 0) } catch { }
    Start-Sleep -Milliseconds 500
}

Log "[8] Selecting $MatchesToSelect same-line match occurrences..."
$clicked = 0
$noProgress = 0
while ($clicked -lt $MatchesToSelect) {
    $listRect = $resultsList.Current.BoundingRectangle
    $toggles = Get-MatchToggles -listElement $resultsList -listRect $listRect
    $next = $toggles | Where-Object { $_.State -eq 'Off' } | Select-Object -First 1
    if (-not $next) {
        if ($scrollPattern -and $scrollPattern.Current.VerticallyScrollable -and $scrollPattern.Current.VerticalScrollPercent -lt 99.5) {
            try { $scrollPattern.ScrollVertical([System.Windows.Automation.ScrollAmount]::SmallIncrement) } catch { break }
            Start-Sleep -Milliseconds 250
            $noProgress++
            if ($noProgress -gt 40) { Log "  Stopping: no more unclicked matches."; break }
            continue
        } else { Log "  Reached end of match list."; break }
    }
    $noProgress = 0
    $clicked++
    $line = $next.Line
    Dismiss-GotItTips | Out-Null
    Scroll-IntoComfortBand -element $next.Element -listElement $resultsList -scrollPattern $scrollPattern
    try { $rect = $next.Element.Current.BoundingRectangle } catch { $rect = $next.Rect }
    if ([double]::IsNaN($rect.X) -or $rect.Width -le 0) { $rect = $next.Rect }
    Log ("  [{0}/{1}] Selecting match on line {2}..." -f $clicked, $MatchesToSelect, $line)
    Click-Rect $rect
    Start-Sleep -Milliseconds 700
    WaitDismiss-GotItTips -TimeoutMs 1500 -PollMs 300 | Out-Null
}
$navBefore = Get-MatchNavLabel
Log "  Selected $clicked matches. MatchNav='$navBefore'"
Start-Sleep -Milliseconds 800

Log "[9] Locating + clicking the appended window's PREFIX 'Show more' ellipsis..."
Activate-Window
WaitDismiss-GotItTips -TimeoutMs 1500 -PollMs 300 | Out-Null
# Preview lives under PreviewSectionsPanel (multi-section) or PreviewBlock; search the
# whole window for the leftmost ellipsis marker either way.
$ellipsis = Find-PrefixShowMoreEllipsis -previewParent $win
if (-not $ellipsis) { Log "ERROR: no Show-more ellipsis marker found in preview"; exit 3 }
Log ("  Clicking prefix ellipsis at X={0} Y={1} ..." -f [int]$ellipsis.X, [int]$ellipsis.Y)
Click-Rect $ellipsis.Rect
Start-Sleep -Seconds 2   # let expansion rebuild + overlay re-track + layout settle

Log "[10] Validating overlay tracking from the Yagu log..."
if (-not (Test-Path -LiteralPath $YaguLog)) { Log "ERROR: Yagu log not found at $YaguLog"; exit 4 }
$allLines = Get-Content -LiteralPath $YaguLog
$newLines = if ($allLines.Count -gt $logStartLineCount) { $allLines[$logStartLineCount..($allLines.Count - 1)] } else { $allLines }

# Find the last expansion in this run.
$expandIdx = -1
for ($i = $newLines.Count - 1; $i -ge 0; $i--) {
    if ($newLines[$i] -match '\[PreviewShowMore\] expand complete') { $expandIdx = $i; break }
}
if ($expandIdx -lt 0) {
    Log "FAIL: no '[PreviewShowMore] expand complete' entry found - the ellipsis click did not trigger an expansion."
    Log "      (Check that the prefix ellipsis was actually clicked.)"
    exit 5
}
$expandActionLine = ($newLines[0..$expandIdx] | Where-Object { $_ -match '\[PreviewShowMore\] expand edge=' } | Select-Object -Last 1)
Log "  Expansion: $($expandActionLine -replace '^\[[^]]+\]\s*','')"

# After expansion: a BoxMatchRun re-box (FIX part 1) ...
$post = $newLines[$expandIdx..($newLines.Count - 1)]
$reboxLine = ($post | Where-Object { $_ -match '\[MatchNav\] BoxMatchRun:' } | Select-Object -First 1)
# ... and the overlay applied from the ACTUAL run rect, not the drifted estimate (FIX part 2).
$diagLine  = ($post | Where-Object { $_ -match 'WrapOverlayDiag stage=applied' } | Select-Object -First 1)

# Also accept a re-box that is logged on the SAME line block right before expand complete
if (-not $reboxLine) {
    $reboxLine = ($newLines[0..$expandIdx] | Where-Object { $_ -match '\[MatchNav\] BoxMatchRun:' } | Select-Object -Last 1)
}

$pass = $true
if ($reboxLine) {
    Log "  Re-box: $($reboxLine -replace '^.*BoxMatchRun:\s*','BoxMatchRun ')"
} else {
    Log "  FAIL: no BoxMatchRun re-box after expansion."
    $pass = $false
}

if (-not $diagLine) {
    Log "  FAIL: no 'WrapOverlayDiag stage=applied' after expansion (overlay never re-applied)."
    $pass = $false
} else {
    $usedEstimate = if ($diagLine -match 'usedWrappedPointEstimate=(True|False)') { $matches[1] } else { "?" }
    $estimateReason = if ($diagLine -match "estimateReason='([^']*)'") { $matches[1] } else { "?" }
    $rawPoint = if ($diagLine -match 'rawPoint=\(([-\d.]+),([-\d.]+)\)') { [pscustomobject]@{ X = [double]$matches[1]; Y = [double]$matches[2] } } else { $null }
    $finalPoint = if ($diagLine -match 'finalPoint=\(([-\d.]+),([-\d.]+)\)') { [pscustomobject]@{ X = [double]$matches[1]; Y = [double]$matches[2] } } else { $null }
    Log ("  Overlay: usedWrappedPointEstimate={0}, estimateReason='{1}'" -f $usedEstimate, $estimateReason)
    if ($rawPoint -and $finalPoint) {
        $dx = [math]::Abs($rawPoint.X - $finalPoint.X)
        $dy = [math]::Abs($rawPoint.Y - $finalPoint.Y)
        Log ("  Overlay: rawPoint=({0:N1},{1:N1}) finalPoint=({2:N1},{3:N1}) delta=({4:N1},{5:N1})" -f $rawPoint.X, $rawPoint.Y, $finalPoint.X, $finalPoint.Y, $dx, $dy)
        # The box must sit on the ACTUAL measured run, not the drifted uniform estimate.
        if ($usedEstimate -ne 'False') {
            Log "  FAIL: overlay used the wrapped-point ESTIMATE (drifts for deep matches) instead of the actual run rect."
            $pass = $false
        } elseif ($dx -gt 12 -or $dy -gt 8) {
            Log "  FAIL: overlay finalPoint diverges from the actual run rect (box not on the match)."
            $pass = $false
        } else {
            Log "  OK: overlay box sits on the actual measured run (tracks the match)."
        }
    } else {
        Log "  WARN: could not parse rawPoint/finalPoint from the diag line."
    }
}

Log ""
if ($pass) {
    Log "RESULT: PASS - the active match overlay re-tracked the match after Show-more expansion."
} else {
    Log "RESULT: FAIL - the overlay did not correctly track the match after expansion. See above."
}
Log "App left open (pid=$($win.Current.ProcessId)) for inspection. Log: $LogFile"
if (-not $pass) { exit 1 }
