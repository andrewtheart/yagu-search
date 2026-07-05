# Validate that the preview "Show more"/"Show all" action panel DISMISSES after the
# triggering ellipsis is expanded away.
#
# Scenario:
#   1. Search "test", filter to the giant npm cache file, expand it.
#   2. Select N same-line matches so an appended match-window with prefix+suffix
#      ellipses is rendered in the preview.
#   3. HOVER the prefix ellipsis to pop the action panel (Show more / Show all).
#   4. Verify the panel is visible (a "Show all" button is present).
#   5. Click "Show all". The whole line is revealed, so every ellipsis disappears.
#   6. Verify the panel is GONE (no visible "Show all"/"Show more" button remains).
#
# Validation is UIA-based (panel button presence) + log cross-check, so it works on an
# unlocked interactive desktop. Modelled on scripts/drive-cache-showmore-overlay.ps1.

param(
    [string]$Directory = "$env:USERPROFILE\AppData\Local\npm-cache\_cacache\content-v2\sha512\bf\20",
    [string]$Query = "test",
    [string]$FileFilter = "$env:USERPROFILE\AppData\Local\npm-cache\_cacache\content-v2\sha512\bf\20\036d75a0cc71b9c888a5bb3b68d681e0498e1f40657a77de6668619dd395fd99bde768ec1c2df9de5c93148e1fe5d0848635f4b628685d05df21b39b244d",
    [string]$OutDir = "C:\src\Yagu\TestResults\ShowMoreDismiss",
    [string]$YaguLog = "$env:APPDATA\Yagu\yagu.log",
    [int]$FindFileTimeoutSec = 300,
    [int]$MatchesToSelect = 8
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class YaguDismiss {
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public static void LeftClick() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); }
    // Relative move (mickeys) — DPI-independent and always emits a genuine WM_MOUSEMOVE
    // at the current cursor location, unlike absolute normalization which can mis-map
    // across multi-monitor / mixed-DPI setups.
    public static void MoveRel(int dx, int dy) { mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, IntPtr.Zero); }
    // Real hardware-level absolute move across the virtual desktop so WinUI 3 sees a
    // genuine pointer move (PointerEntered/PointerMoved), which SetCursorPos alone may not.
    public static void MoveAbs(int x, int y) {
        int vx = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int vy = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        int vw = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        int vh = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (vw <= 1) vw = 2; if (vh <= 1) vh = 2;
        int ax = (int)(((x - vx) * 65535.0) / (vw - 1));
        int ay = (int)(((y - vy) * 65535.0) / (vh - 1));
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay, 0, IntPtr.Zero);
    }
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
$Ellipsis = [char]0x2026

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
            if ($h -ne [IntPtr]::Zero) { [YaguDismiss]::Activate($h); Start-Sleep -Milliseconds 200 }
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

function Move-Cursor([int]$x, [int]$y) {
    # Physical-pixel positioning (no WinForms DPI virtualization) + a real hardware move.
    [YaguDismiss]::SetCursorPos($x, $y)
    [YaguDismiss]::MoveAbs($x, $y)
    Start-Sleep -Milliseconds 90
}

function Get-CursorPoint {
    $p = New-Object YaguDismiss+POINT
    [YaguDismiss]::GetCursorPos([ref]$p) | Out-Null
    return $p
}

function Probe-UnderCursor {
    $p = Get-CursorPoint
    $desc = "?"
    try {
        $el = $AE::FromPoint([System.Windows.Point]::new([double]$p.X, [double]$p.Y))
        if ($el) {
            $procId = -1
            try { $procId = $el.Current.ProcessId } catch { }
            $desc = "$($el.Current.ControlType.ProgrammaticName) name='$($el.Current.Name)' pid=$procId"
        }
    } catch { $desc = "FromPoint failed: $($_.Exception.Message)" }
    Log ("  [probe] cursor=({0},{1}) under={2}" -f $p.X, $p.Y, $desc)
}

function Hover-Rect($rect) {
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    # Approach from the left with absolute positioning, then jitter in place using
    # DPI-independent RELATIVE moves so WinUI 3 reliably raises PointerEntered/Moved
    # at the marker's true pixel (absolute injection can mis-map under mixed DPI).
    for ($sx = $x - 40; $sx -le $x; $sx += 2) {
        [YaguDismiss]::SetCursorPos($sx, $y) | Out-Null
        [YaguDismiss]::MoveRel(1, 0)
        [YaguDismiss]::MoveRel(-1, 0)
        Start-Sleep -Milliseconds 20
    }
    for ($i = 0; $i -lt 12; $i++) {
        [YaguDismiss]::SetCursorPos($x, $y) | Out-Null
        [YaguDismiss]::MoveRel(2, 0)
        [YaguDismiss]::MoveRel(-2, 1)
        [YaguDismiss]::MoveRel(0, -1)
        Start-Sleep -Milliseconds 25
    }
    [YaguDismiss]::SetCursorPos($x, $y) | Out-Null
}

function Click-Rect($rect) {
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    Move-Cursor $x $y
    Start-Sleep -Milliseconds 80
    [YaguDismiss]::LeftClick()
}

function Dismiss-GotItTips {
    $dismissed = 0
    try {
        $btns = $AE::RootElement.FindAll($TS::Descendants, $PC::new($AE::NameProperty, "Got it"))
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

# All visible "…" ellipsis Text markers in the preview, left-to-right.
function Get-EllipsisMarkers($previewParent) {
    $cond = $PC::new($AE::NameProperty, [string]$Ellipsis)
    $out = @()
    try { $all = $previewParent.FindAll($TS::Descendants, $cond) } catch { return @() }
    foreach ($el in $all) {
        try {
            if ($el.Current.ControlType -ne $CT::Text) { continue }
            if ($el.Current.IsOffscreen) { continue }
            $r = $el.Current.BoundingRectangle
            if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            $out += [pscustomobject]@{ Element = $el; X = $r.X; Y = $r.Y; Rect = $r }
        } catch { }
    }
    return $out | Sort-Object X
}

# Visible action-panel buttons ("Show more"/"Show all") identified by an exact text
# child, so the section pager's "Show all files" button is never mistaken for one.
function Get-ShowPanelButtons {
    $out = @()
    try { $all = $script:win.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Button)) } catch { return @() }
    foreach ($el in $all) {
        try {
            if ($el.Current.IsOffscreen) { continue }
            $kind = $null
            if ($el.FindFirst($TS::Descendants, $PC::new($AE::NameProperty, "Show all")))  { $kind = "Show all" }
            elseif ($el.FindFirst($TS::Descendants, $PC::new($AE::NameProperty, "Show more"))) { $kind = "Show more" }
            elseif ($el.Current.Name -eq "Show all" -or $el.Current.Name -eq "Show more") { $kind = $el.Current.Name }
            if (-not $kind) { continue }
            $r = $el.Current.BoundingRectangle
            if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            $out += [pscustomobject]@{ Element = $el; Name = $kind; Rect = $r }
        } catch { }
    }
    return $out
}

# Diagnostic: list visible buttons whose name mentions "Show" (helps when hover fails).
function Dump-ShowButtons {
    try { $all = $script:win.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Button)) } catch { return }
    $names = @()
    foreach ($el in $all) {
        try {
            if ($el.Current.IsOffscreen) { continue }
            $n = $el.Current.Name
            if ($n -like "*Show*") { $names += "'$n'" }
        } catch { }
    }
    Log ("  [diag] visible Show* buttons: {0}" -f ($(if ($names.Count) { $names -join ', ' } else { '(none)' })))
    $overlay = $script:win.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, "PreviewShowMoreTooltipOverlay"))
    $bubble  = $script:win.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, "PreviewShowMoreTooltipBubble"))
    Log ("  [diag] overlay byAid={0} offscreen={1}; bubble byAid={2} offscreen={3}" -f `
        [bool]$overlay, $(if ($overlay) { $overlay.Current.IsOffscreen } else { 'n/a' }), `
        [bool]$bubble,  $(if ($bubble)  { $bubble.Current.IsOffscreen }  else { 'n/a' }))
}

# ──────────────────────────────────────────────────────────────────────────────
Log "=== Drive Yagu: Show-more panel dismissal validation ==="
$yaguExe = "C:\src\Yagu\src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"
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
try { [YaguDismiss]::Maximize([IntPtr]$win.Current.NativeWindowHandle); Start-Sleep -Milliseconds 700 } catch { }
$logStartLineCount = 0
if (Test-Path -LiteralPath $YaguLog) { $logStartLineCount = (Get-Content -LiteralPath $YaguLog).Count }

Log "[2] Setting up search..."
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
Set-BoxText $filterBox $FileFilter | Out-Null
Start-Sleep -Milliseconds 900

Log "[4] Waiting for target file..."
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
    if ($fileEl) { Log "  Target file appeared."; break }
    Start-Sleep -Milliseconds 700
}
if (-not $fileEl) { Log "ERROR: target file not found within timeout"; exit 2 }

Log "[5] Cancelling search; expanding the file..."
$lbl = Find-Element -Parent $win -AutomationId "SearchCancelLabel" -TimeoutSeconds 2
if ($lbl -and $lbl.Current.Name -eq "Cancel") {
    $cancelBtn = Find-Element -Parent $win -AutomationId "SearchCancelButton" -TimeoutSeconds 3
    if ($cancelBtn) { Invoke-OrClick $cancelBtn }
}
Start-Sleep -Seconds 1
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

Log "[6] Selecting $MatchesToSelect same-line match occurrences..."
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
            if ($noProgress -gt 40) { break }
            continue
        } else { break }
    }
    $noProgress = 0
    $clicked++
    Dismiss-GotItTips | Out-Null
    Scroll-IntoComfortBand -element $next.Element -listElement $resultsList -scrollPattern $scrollPattern
    try { $rect = $next.Element.Current.BoundingRectangle } catch { $rect = $next.Rect }
    if ([double]::IsNaN($rect.X) -or $rect.Width -le 0) { $rect = $next.Rect }
    Log ("  [{0}/{1}] Selecting match on line {2}..." -f $clicked, $MatchesToSelect, $next.Line)
    Click-Rect $rect
    Start-Sleep -Milliseconds 700
    WaitDismiss-GotItTips -TimeoutMs 1500 -PollMs 300 | Out-Null
}
Log "  Selected $clicked matches. MatchNav='$(Get-MatchNavLabel)'"
Start-Sleep -Milliseconds 800
WaitDismiss-GotItTips -TimeoutMs 1500 -PollMs 300 | Out-Null

Log "[7] Hovering an ellipsis to pop the Show-more/Show-all panel..."
Activate-Window
$markers = Get-EllipsisMarkers -previewParent $win
if (-not $markers -or $markers.Count -eq 0) { Log "ERROR: no ellipsis marker found in preview"; exit 3 }
Log ("  Found {0} ellipsis marker(s): {1}" -f $markers.Count, (($markers | ForEach-Object { "({0},{1})" -f [int]$_.X, [int]$_.Y }) -join " "))
$target = $markers | Select-Object -First 1   # leftmost = appended window prefix marker

$panelButtons = @()
$deadline = (Get-Date).AddSeconds(12)
$attempt = 0
while ((Get-Date) -lt $deadline) {
    $attempt++
    # Rotate through the available markers in case one is occluded.
    $m = $markers[($attempt - 1) % $markers.Count]
    Hover-Rect $m.Rect
    Start-Sleep -Milliseconds 450
    if ($attempt -le $markers.Count) {
        Log ("  attempt {0}: hovering marker at ({1},{2}) size {3}x{4}" -f $attempt, [int]$m.Rect.X, [int]$m.Rect.Y, [int]$m.Rect.Width, [int]$m.Rect.Height)
        Probe-UnderCursor
    }
    $panelButtons = Get-ShowPanelButtons
    if ($panelButtons.Count -gt 0) { $target = $m; break }
    if ($attempt % 3 -eq 0) { Dump-ShowButtons }
}
if ($panelButtons.Count -eq 0) {
    Log "ERROR: action panel did not appear on hover (no Show more/Show all buttons found)."
    Dump-ShowButtons
    exit 4
}
Log ("  Panel visible. Buttons: {0}" -f (($panelButtons | ForEach-Object { "'$($_.Name.Trim())'" }) -join ", "))

Log "[8] Clicking 'Show all'..."
$showAll = $panelButtons | Where-Object { $_.Name -like "*Show all*" } | Select-Object -First 1
if (-not $showAll) { Log "ERROR: 'Show all' button not found in panel."; exit 5 }
# Keep the cursor over the panel (CancelHide) then invoke the button.
Hover-Rect $showAll.Rect
Start-Sleep -Milliseconds 150
Invoke-OrClick $showAll.Element
Start-Sleep -Seconds 2   # let the line fully expand + panel dismiss

Log "[9] Verifying the panel DISAPPEARED after expansion..."
# Move the cursor well away so a lingering hover can't keep the panel alive; the
# fix must dismiss it regardless, but this rules out a false positive.
Move-Cursor ([int]$win.Current.BoundingRectangle.X + 40) ([int]$win.Current.BoundingRectangle.Y + 40)
Start-Sleep -Milliseconds 400

$pass = $true
$remaining = Get-ShowPanelButtons
if ($remaining.Count -gt 0) {
    Log ("  FAIL: action panel still visible after Show all: {0}" -f (($remaining | ForEach-Object { "'$($_.Name.Trim())'" }) -join ", "))
    $pass = $false
} else {
    Log "  OK: no Show more/Show all buttons remain (panel dismissed)."
}

# Cross-check from the log that a full-line expansion actually completed.
if (Test-Path -LiteralPath $YaguLog) {
    $allLines = Get-Content -LiteralPath $YaguLog
    $newLines = if ($allLines.Count -gt $logStartLineCount) { $allLines[$logStartLineCount..($allLines.Count - 1)] } else { $allLines }
    $allModeExpand = ($newLines | Where-Object { $_ -match '\[PreviewShowMore\] action mode=All' } | Select-Object -Last 1)
    $expandComplete = ($newLines | Where-Object { $_ -match '\[PreviewShowMore\] expand complete mode=All' } | Select-Object -Last 1)
    if ($allModeExpand) { Log "  Log: $($allModeExpand -replace '^\[[^]]+\]\s*','')" }
    if ($expandComplete) { Log "  Log: $($expandComplete -replace '^\[[^]]+\]\s*','')" }
    if (-not $expandComplete) {
        Log "  WARN: no 'expand complete mode=All' in log; the Show all click may not have triggered an expansion."
        $pass = $false
    }
}

Log ""
if ($pass) {
    Log "RESULT: PASS - the Show more/Show all panel disappeared after the line was fully expanded."
} else {
    Log "RESULT: FAIL - see above."
}
Log "App left open (pid=$($win.Current.ProcessId)). Log: $LogFile"
if (-not $pass) { exit 1 }
