# Drive the Yagu UI against a long npm cache file, then screenshot the preview in
# one selected wrap mode after each selected match line.
#
# Uses Windows UIAutomation (UIA) + raw mouse input, modelled on test-match-nav.ps1.

param(
    [string]$Directory = "$env:USERPROFILE\AppData\Local\npm-cache\_cacache\content-v2\sha512\bf\20",
    [string]$Query = "test",
    [string]$FileFilter = "$env:USERPROFILE\AppData\Local\npm-cache\_cacache\content-v2\sha512\bf\20\036d75a0cc71b9c888a5bb3b68d681e0498e1f40657a77de6668619dd395fd99bde768ec1c2df9de5c93148e1fe5d0848635f4b628685d05df21b39b244d",
    [string]$ScreenshotDir = "C:\src\Yagu\TestResults\CacheWrapModes",
    [string]$LogFile = "C:\src\Yagu\TestResults\CacheWrapModes\run.log",
    [int]$FindFileTimeoutSec = 300,
    [Alias("MaxMatchesToClick")]
    [int]$MaxMatches = 30,
    [ValidateSet("Word wrap", "No wrap")]
    [string]$WrapMode = "Word wrap"
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
$TargetFileName = Split-Path -Leaf $FileFilter
$WrapModeSlug = if ($WrapMode -eq "Word wrap") { "word-wrap" } else { "no-wrap" }
if ($ScreenshotDir -eq "C:\src\Yagu\TestResults\CacheWrapModes") {
    $ScreenshotDir = Join-Path $ScreenshotDir $WrapModeSlug
    $LogFile = Join-Path $ScreenshotDir "run.log"
}

if (-not (Test-Path $ScreenshotDir)) { New-Item -ItemType Directory -Path $ScreenshotDir -Force | Out-Null }
"" | Set-Content -Path $LogFile

function Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $msg
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

function Set-PersistedPreviewWrapMode([ValidateSet("Word wrap", "No wrap")][string]$Mode) {
    $settingsPath = Join-Path $env:APPDATA "Yagu\settings.json"
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        Log "ERROR: settings.json not found at $settingsPath"
        exit 1
    }

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $enableWrap = $Mode -eq "Word wrap"
    if (-not ($settings.PSObject.Properties.Name -contains "PreviewWordWrap")) {
        $settings | Add-Member -NotePropertyName PreviewWordWrap -NotePropertyValue $enableWrap
    } else {
        $settings.PreviewWordWrap = $enableWrap
    }
    if (-not ($settings.PSObject.Properties.Name -contains "PreviewWrapModeIndex")) {
        $settings | Add-Member -NotePropertyName PreviewWrapModeIndex -NotePropertyValue $(if ($enableWrap) { 0 } else { 2 })
    } else {
        $settings.PreviewWrapModeIndex = if ($enableWrap) { 0 } else { 2 }
    }

    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Log "  Persisted preview wrap mode before launch: $Mode"
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
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $g.Dispose()
    $path = Join-Path $ScreenshotDir "$Name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $path
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

function Invoke-OrClick($element) {
    try {
        $ip = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($ip) { $ip.Invoke(); return }
    } catch { }
    Click-Rect $element.Current.BoundingRectangle
}

function Get-ToggleState($element) {
    try {
        $tp = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        return $tp.Current.ToggleState.ToString()
    } catch { }
    return "Unknown"
}

function Set-ToggleState($element, [bool]$On) {
    $desired = if ($On) { "On" } else { "Off" }
    $state = Get-ToggleState $element
    if ($state -eq $desired) { return }

    try {
        $tp = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $tp.Toggle()
    } catch {
        Invoke-OrClick $element
    }
    Start-Sleep -Milliseconds 500
}

function Find-VisibleByName {
    param($Parent, [string]$Name, $ControlType, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $all = $Parent.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            try {
                if ($ControlType -and $el.Current.ControlType -ne $ControlType) { continue }
                if ($el.Current.Name -ne $Name) { continue }
                if ($el.Current.IsOffscreen) { continue }
                $r = $el.Current.BoundingRectangle
                if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
                return $el
            } catch { }
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Clear-CurrentPreview {
    Activate-Window
    $clear = Find-Element -Parent $script:win -AutomationId "ClearPreviewButton" -TimeoutSeconds 3
    if (-not $clear) { Log "      ClearPreviewButton not found; preview is probably already empty."; return }
    try {
        if ($clear.Current.IsOffscreen) { Log "      ClearPreviewButton is offscreen; preview is probably already empty."; return }
    } catch { }

    Log "      Clearing current preview before changing wrap mode..."
    Invoke-OrClick $clear
    Start-Sleep -Milliseconds 900
}

function Open-SettingsWindow {
    Activate-Window
    $settingsButton = Find-Element -Parent $script:win -Name "Settings" -ControlType $CT::Button -TimeoutSeconds 3
    if (-not $settingsButton) {
        $winRect = $script:win.Current.BoundingRectangle
        $buttons = $script:win.FindAll($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Button))
        $topButtons = @()
        foreach ($button in $buttons) {
            try {
                if ($button.Current.IsOffscreen) { continue }
                $r = $button.Current.BoundingRectangle
                if ([double]::IsNaN($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
                if ($r.Y -le ($winRect.Y + 60) -and $r.X -ge ($winRect.X + $winRect.Width - 320)) {
                    $topButtons += [pscustomobject]@{ Element = $button; X = $r.X }
                }
            } catch { }
        }
        $ordered = $topButtons | Sort-Object X
        if ($ordered.Count -ge 3) { $settingsButton = $ordered[2].Element }
    }
    if (-not $settingsButton) { Log "ERROR: Settings button not found"; exit 1 }

    Invoke-OrClick $settingsButton
    Start-Sleep -Milliseconds 800

    $settingsWindow = $null
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline -and -not $settingsWindow) {
        $windows = $AE::RootElement.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
        foreach ($window in $windows) {
            try {
                if ($window.Current.Name -like "*Settings*") { $settingsWindow = $window; break }
            } catch { }
        }
        if (-not $settingsWindow) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $settingsWindow) { Log "ERROR: Settings window not found"; exit 1 }
    return $settingsWindow
}

function Close-SettingsWindow($settingsWindow) {
    $close = Find-VisibleByName -Parent $settingsWindow -Name "Close" -ControlType $CT::Button -TimeoutSeconds 8
    if ($close) {
        Invoke-OrClick $close
        Start-Sleep -Milliseconds 800
        return
    }

    try {
        $h = [IntPtr]$settingsWindow.Current.NativeWindowHandle
        if ($h -ne [IntPtr]::Zero) { [YaguDrive]::Activate($h) }
    } catch { }
    [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
    Start-Sleep -Milliseconds 800
}

function Select-PreviewWrapMode([ValidateSet("Word wrap", "No wrap")][string]$Mode) {
    Clear-CurrentPreview

    $settingsWindow = Open-SettingsWindow
    Log "      Selecting preview mode in Settings: $Mode"

    $displayTab = Find-VisibleByName -Parent $settingsWindow -Name "Display" -TimeoutSeconds 8
    if ($displayTab) {
        Invoke-OrClick $displayTab
        Start-Sleep -Milliseconds 700
    }

    $wordWrap = Find-VisibleByName -Parent $settingsWindow -Name "Word wrap in preview panel" -ControlType $CT::CheckBox -TimeoutSeconds 10
    if (-not $wordWrap) { Log "ERROR: Settings word-wrap checkbox not found"; Close-SettingsWindow $settingsWindow; exit 1 }

    Set-ToggleState $wordWrap ($Mode -eq "Word wrap")

    $save = Find-VisibleByName -Parent $settingsWindow -Name "Save" -ControlType $CT::Button -TimeoutSeconds 8
    if ($save) {
        try {
            if ($save.Current.IsEnabled) {
                Invoke-OrClick $save
                Start-Sleep -Milliseconds 900
            }
        } catch { }
    }

    Close-SettingsWindow $settingsWindow
    Activate-Window
}

function Rebuild-PreviewFromMatch($matchElement) {
    Activate-Window
    $state = Get-ToggleState $matchElement
    Log "      Rebuilding preview from match toggle (state=$state)..."
    if ($state -eq "On") {
        Invoke-OrClick $matchElement
        Start-Sleep -Milliseconds 700
    }
    Invoke-OrClick $matchElement
    Start-Sleep -Milliseconds 1200
    WaitDismiss-GotItTips -TimeoutMs 2000 -PollMs 300 | Out-Null
}

function Select-PreviewWrapModeAndRebuild([ValidateSet("Word wrap", "No wrap")][string]$Mode, $matchElement) {
    Select-PreviewWrapMode $Mode
    Rebuild-PreviewFromMatch $matchElement
}

function Capture-WrapModeScreenshots([string]$Prefix, $matchElement) {
    $nav = Get-MatchNavLabel
    $shot = Take-Screenshot "$Prefix-$WrapModeSlug"
    Log ("      -> {0} MatchNav='{1}' screenshot={2}" -f $WrapMode, $nav, (Split-Path $shot -Leaf))

    return [pscustomobject]@{
        Mode = $WrapMode
        MatchNav = $nav
        Screenshot = Split-Path $shot -Leaf
    }
}

# Returns onscreen line-number ToggleButtons inside ResultsList, with toggle state.
function Get-MatchToggles {
    param($listElement, $listRect)
    $btnCond = $PC::new($AE::ControlTypeProperty, $CT::Button)
    try {
        $all = $listElement.FindAll($TS::Descendants, $btnCond)
    } catch {
        Log "  WARNING: could not enumerate match toggles: $($_.Exception.Message)"
        return @()
    }
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
Log "=== Drive Yagu: cache preview wrap-mode verification ($WrapMode) ==="
Log "Directory=$Directory Query=$Query FileFilter=$FileFilter TargetFileName=$TargetFileName WrapMode=$WrapMode"

$yaguExe = "C:\src\Yagu\src\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"
$root = $AE::RootElement

Log "[1] Launching a completely fresh Yagu instance..."
# Yagu is single-instance. Close an existing Yagu main window by its UIA-owned PID
# so the named mutex is released and we get a brand-new window rather than
# reactivating the old one.
$existingYaguWindows = @()
try {
    $windows = $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
    foreach ($w in $windows) {
        try {
            if ($w.Current.Name -like "Yagu - Yet Another Grep Utility*") { $existingYaguWindows += $w }
        } catch { }
    }
} catch { }
if ($existingYaguWindows.Count -gt 0) {
    $script:win = $existingYaguWindows[0]
    Clear-CurrentPreview
    Set-PersistedPreviewWrapMode $WrapMode
    $existingPids = @($existingYaguWindows | ForEach-Object { try { $_.Current.ProcessId } catch { $null } } | Where-Object { $_ } | Sort-Object -Unique)
    Log "  Closing existing Yagu window process(es): $($existingPids -join ', ')"
    foreach ($existingPid in $existingPids) { Stop-Process -Id $existingPid -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
} else {
    Set-PersistedPreviewWrapMode $WrapMode
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
    $fileEl = $resultsList.FindFirst($TS::Descendants, $PC::new($AE::NameProperty, $TargetFileName))
    if (-not $fileEl) {
        # Match by partial name: any descendant whose Name contains either the full
        # filter path or the leaf cache file name.
        $all = $resultsList.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            try {
                $name = $el.Current.Name
                if (($name -like "*$FileFilter*" -or $name -like "*$TargetFileName*") -and $el.Current.ControlType -eq $CT::Text) { $fileEl = $el; break }
            } catch {}
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
    Log ("  [{0}] Clicking match on line {1} (state={2}) at Y={3}..." -f $clicked, $line, $next.State, $cy)
    Click-Rect $rect
    Start-Sleep -Milliseconds 700           # let preview update + overlay settle
    # A preview-match teaching tip can appear a couple of seconds after the preview
    # loads and sit over the overlay we want to screenshot. Poll-dismiss briefly so
    # the shot is clean; harmless no-op when no tip shows.
    WaitDismiss-GotItTips -TimeoutMs 2500 -PollMs 300 | Out-Null
    $capture = Capture-WrapModeScreenshots ("match-{0:D2}-line-{1}" -f $clicked, $line) $next.Element
    $summary.Add([pscustomobject]@{
        Index = $clicked
        Line = $line
        Mode = $capture.Mode
        MatchNav = $capture.MatchNav
        Screenshot = $capture.Screenshot
    })
}

Log "[9] Done. Clicked $clicked matches."
Log "Summary:"
foreach ($s in $summary) { Log ("  #{0,-2} line {1,-5} mode={2} {3}" -f $s.Index, $s.Line, $s.Mode, $s.MatchNav) }
$summary | ConvertTo-Json | Set-Content -Path (Join-Path $ScreenshotDir "summary.json")
Log "Screenshots in: $ScreenshotDir"
Log "App left open (window pid=$($win.Current.ProcessId)) for inspection."
