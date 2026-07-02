# Capture curated Yagu screenshots for the README via UI Automation + PrintWindow.
#
# Drives the built Debug Yagu.exe into specific states (traditional search + match navigation,
# built-in C# editor, multi-file preview with collapsed drawers, semantic search, AI/OCR
# settings) and captures each window with PrintWindow (works even if the window is occluded).
#
# Usage:
#   pwsh -File scripts/capture-readme-screenshots.ps1 -Scenario all
#   pwsh -File scripts/capture-readme-screenshots.ps1 -Scenario semantic
#
# Scenarios: all, match-nav, editor, multi-preview, semantic, traditional, settings-ai, settings-ocr

param(
    [ValidateSet('all','match-nav','editor','multi-preview','semantic','traditional','settings-ai','settings-ocr')]
    [string]$Scenario = 'all',
    [string]$Directory = 'C:\src\Yagu\Yagu',
    [string]$OutDir = 'C:\src\Yagu\docs\images',
    [string]$YaguExe = 'C:\src\Yagu\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Native {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP   = 0x0010;

    public static void Activate(IntPtr h) { ShowWindow(h, 3); BringWindowToTop(h); SetForegroundWindow(h); }
    // Force the window above others in Z-order without needing foreground rights, so physical
    // clicks land on it even when another app (e.g. the editor) is in front. Does NOT change the
    // maximized/normal state (no ShowWindow), so the layout stays put.
    public static void Raise(IntPtr h) {
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // TOPMOST | NOSIZE|NOMOVE|SHOWWINDOW
        SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // NOTOPMOST
        BringWindowToTop(h);
    }
    private static void Spin(int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) {} }
    public static void LeftClickAt(int x, int y) {
        SetCursorPos(x, y); Spin(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
    public static void RightClickAt(int x, int y) {
        SetCursorPos(x, y); Spin(60);
        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, IntPtr.Zero);
    }
    public static void DoubleClickAt(int x, int y) {
        SetCursorPos(x, y); Spin(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Spin(80);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    public static string Capture(IntPtr hwnd, string outPath) {
        RECT r; if (!GetWindowRect(hwnd, out r)) return "NO_RECT";
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return "BAD_SIZE";
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
            if (!ok) return "PRINTWINDOW_FAILED";
            bmp.Save(outPath, ImageFormat.Png);
            return "OK " + w + "x" + h;
        }
    }
}
"@ -ReferencedAssemblies System.Drawing

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$PC = [System.Windows.Automation.PropertyCondition]

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
if (-not (Test-Path $YaguExe)) { throw "Yagu.exe not found at $YaguExe. Build Debug first." }

function Write-Step($msg) { Write-Host "  $msg" }

function Stop-AllYagu {
    Get-Process Yagu -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like '*\Yagu\bin\*' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

$script:LaunchedPid = 0

function Start-Yagu {
    param([string]$LaunchArgs)
    $p = Start-Process -FilePath $YaguExe -ArgumentList $LaunchArgs -PassThru
    $script:LaunchedPid = $p.Id
    return $p
}

function Test-IsYaguAppWindow($w) {
    try {
        $n = $w.Current.Name
        if ($n -eq 'Yagu - Yet Another Grep Utility') { return $true }
        # Exclude lookalikes: voidtools "YaguSetup - Everything", installer, Settings.
        if ($n -like '*Everything*' -or $n -like 'YaguSetup*' -or $n -like '*Settings*') { return $false }
        return ($n -like 'Yagu - *')
    } catch { return $false }
}

function Get-YaguWindow {
    param([int]$TimeoutSeconds = 20)
    $root = $AE::RootElement
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # Prefer the exact process we launched.
        if ($script:LaunchedPid -gt 0) {
            try {
                $byPid = $root.FindFirst($TS::Children, $PC::new($AE::ProcessIdProperty, [int]$script:LaunchedPid))
                if ($byPid -and (Test-IsYaguAppWindow $byPid)) { return $byPid }
            } catch {}
        }
        $windows = $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
        foreach ($w in $windows) {
            if (Test-IsYaguAppWindow $w) { return $w }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Get-WindowByNameLike {
    param([string]$Like, [int]$TimeoutSeconds = 15)
    $root = $AE::RootElement
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $windows = $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
        foreach ($w in $windows) {
            try { if ($w.Current.Name -like $Like) { return $w } } catch {}
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Find-ById {
    param($Parent, [string]$Id, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $el = $Parent.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, $Id))
            if ($el) { return $el }
        } catch {}
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Find-ByName {
    param($Parent, [string]$Name, $ControlType = $null, [int]$TimeoutSeconds = 8)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $cond = $PC::new($AE::NameProperty, $Name)
            if ($ControlType) {
                $cond = [System.Windows.Automation.AndCondition]::new(
                    $cond, $PC::new($AE::ControlTypeProperty, $ControlType))
            }
            $el = $Parent.FindFirst($TS::Descendants, $cond)
            if ($el) { return $el }
        } catch {}
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-El($el) {
    try { $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $p.Invoke(); return $true } catch {}
    try {
        $r = $el.Current.BoundingRectangle
        [Native]::LeftClickAt([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
        return $true
    } catch {}
    return $false
}

function Set-EditText {
    param($Container, [string]$Text)
    # Container may itself be an Edit, or contain one (AutoSuggestBox/TextBox).
    $edit = $null
    try {
        if ($Container.Current.ControlType -eq $CT::Edit) { $edit = $Container }
        else { $edit = $Container.FindFirst($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit)) }
    } catch {}
    if (-not $edit) { $edit = $Container }
    try {
        $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($Text)
        return $true
    } catch { return $false }
}

function Capture-Window {
    param($WindowEl, [string]$FileName, [switch]$Activate)
    $hwnd = [IntPtr]$WindowEl.Current.NativeWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { Write-Step "No HWND for capture $FileName"; return }
    if ($Activate) { [Native]::Activate($hwnd); Start-Sleep -Milliseconds 400 }
    $path = Join-Path $OutDir $FileName
    $res = [Native]::Capture($hwnd, $path)
    Write-Step "Captured $FileName -> $res"
}

function Wait-Settle([int]$Seconds) { Start-Sleep -Seconds $Seconds }

# --- Search + preview helpers ------------------------------------------------

function Cancel-Search($win) {
    $cancel = Find-ById $win 'SearchCancelButton' 4
    if (-not $cancel) { $cancel = Find-ByName $win 'Cancel' $CT::Button 4 }
    if ($cancel) { [void](Invoke-El $cancel); Write-Step 'Search cancelled.' }
}

function Get-FileCheckboxes($win) {
    $resultsList = Find-ById $win 'ResultsList' 6
    if (-not $resultsList) { return @() }
    $cond = $PC::new($AE::AutomationIdProperty, 'FileGroupCheckBox')
    try { $all = $resultsList.FindAll($TS::Descendants, $cond) } catch { return @() }
    $list = @()
    foreach ($el in $all) {
        try { if (-not $el.Current.IsOffscreen) { $list += $el } } catch {}
    }
    return ,($list | Sort-Object { $_.Current.BoundingRectangle.Y })
}

# Double-click a result FILE ROW (not its checkbox) to open the single-file preview panel.
function Open-FilePreview($win, [int]$RowIndex = 0) {
    $resultsList = Find-ById $win 'ResultsList' 6
    if (-not $resultsList) { Write-Step 'ResultsList not found'; return $false }
    $checkboxes = Get-FileCheckboxes $win
    if ($checkboxes.Count -le $RowIndex) { Write-Step "Only $($checkboxes.Count) file rows"; return $false }
    $cbRect = $checkboxes[$RowIndex].Current.BoundingRectangle
    # Double-click ~160px right of the checkbox (on the file name) to open the preview.
    $x = [int]($cbRect.X + 160)
    $y = [int]($cbRect.Y + $cbRect.Height/2)
    [Native]::DoubleClickAt($x, $y)
    Start-Sleep -Milliseconds 1200
    return $true
}

# --- Scenarios ---------------------------------------------------------------

function Scenario-MatchNav {
    Write-Host "[match-nav] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --query `"async`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 12
    Cancel-Search $win
    Wait-Settle 2
    if (Open-FilePreview $win 0) {
        Wait-Settle 3
        # Advance a few matches to center a highlight and show "Match N of M".
        $next = Find-ById $win 'NextMatchButton' 15
        if (-not $next) { $next = Find-ByName $win 'Next match ($([char]0x2193))' $null 5 }
        if ($next) {
            for ($k = 0; $k -lt 4; $k++) { [void](Invoke-El $next); Start-Sleep -Milliseconds 700 }
        } else { Write-Step 'NextMatchButton not found' }
    }
    Wait-Settle 1
    Capture-Window $win 'match-navigation.png' -Activate
}

function Scenario-Traditional {
    Write-Host "[traditional] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --query `"async Task`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 12
    Cancel-Search $win
    Wait-Settle 2
    Capture-Window $win 'traditional-search.png' -Activate
}

function Scenario-Editor {
    Write-Host "[editor] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --query `"public sealed class`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 12
    Cancel-Search $win
    Wait-Settle 2
    if (-not (Open-FilePreview $win 0)) { Write-Host 'FAILED: could not open preview'; return }
    Wait-Settle 3
    # The built-in editor opens by double-clicking INSIDE the preview text area
    # (EnterPreviewEditorFromPointerDoubleClick). Double-click on a code line in
    # the right-hand preview pane to switch the preview into the editable editor.
    $r = $win.Current.BoundingRectangle
    $ex = [int]($r.X + $r.Width * 0.68)
    $ey = [int]($r.Y + $r.Height * 0.50)
    [Native]::DoubleClickAt($ex, $ey)
    Wait-Settle 3
    # Confirm we're in editor mode (Save/Back buttons visible) before capturing.
    $backBtn = Find-ById $win 'ClosePreviewEditButton' 6
    if (-not $backBtn) { Write-Step 'Editor did not open (no Back button) — retrying double-click' ; [Native]::DoubleClickAt($ex, [int]($r.Y + $r.Height * 0.42)); Wait-Settle 3 }
    Capture-Window $win 'builtin-editor.png' -Activate
}

function Scenario-MultiPreview {
    Write-Host "[multi-preview] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --query `"async`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 12
    Cancel-Search $win
    Wait-Settle 2
    # Check the first 4 file checkboxes (auto-adds each to the preview panel).
    $checkboxes = Get-FileCheckboxes $win
    $n = [Math]::Min(4, $checkboxes.Count)
    for ($i = 0; $i -lt $n; $i++) {
        try {
            $tp = $checkboxes[$i].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $tp.Toggle()
        } catch {
            $r = $checkboxes[$i].Current.BoundingRectangle
            [Native]::LeftClickAt([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
        }
        Start-Sleep -Milliseconds 400
    }
    Write-Step "Checked $n files"
    Wait-Settle 3
    # Switch to the Concatenated layout so each file is a separate collapsible
    # section (drawer) stacked in the preview, instead of the unified occurrence view.
    $layoutBtn = Find-ById $win 'LayoutDropDown' 6
    if ($layoutBtn) {
        [void](Invoke-El $layoutBtn)
        Start-Sleep -Milliseconds 800
        $root = $AE::RootElement
        $concat = Find-ByName $root 'Concatenated' $null 4
        if ($concat) { [void](Invoke-El $concat); Write-Step 'Selected Concatenated layout' }
        else { Write-Step 'Concatenated item not found' }
    } else { Write-Step 'LayoutDropDown not found' }
    Wait-Settle 4
    # Collapse the first two stacked file drawers by clicking their Expander header
    # bars (WinUI Expander headers toggle on click). This leaves later files
    # expanded, so the preview shows several file drawers with some collapsed.
    $rect = $win.Current.BoundingRectangle
    $hx = [int]($rect.X + $rect.Width * 0.46)   # on the file-name text (left of toolbar icons)
    $y1 = [int]($rect.Y + $rect.Height * 0.121) # 1st section header
    $y2 = [int]($rect.Y + $rect.Height * 0.149) # 2nd section header (after 1st collapses)
    [Native]::LeftClickAt($hx, $y1); Start-Sleep -Milliseconds 900
    [Native]::LeftClickAt($hx, $y2); Start-Sleep -Milliseconds 900
    Write-Step 'Collapsed first two preview drawers'
    Wait-Settle 1
    # Scroll the preview to the top so the stacked file headers are visible.
    $mid = [int]($rect.X + $rect.Width * 0.5)
    try {
        $scond = $PC::new($AE::IsScrollPatternAvailableProperty, $true)
        $scrolls = $win.FindAll($TS::Descendants, $scond)
        foreach ($sc in $scrolls) {
            if ($sc.Current.BoundingRectangle.X -ge $mid) {
                $sp = $sc.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0); break }
            }
        }
    } catch { Write-Step "Scroll-to-top failed: $_" }
    Wait-Settle 1
    Capture-Window $win 'multi-file-preview.png' -Activate
}

function Scenario-Semantic {
    Write-Host "[semantic] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 3
    # Switch mode to Semantic via the SplitButton chevron.
    $split = Find-ById $win 'SearchSplitButton' 5
    if ($split) {
        $r = $split.Current.BoundingRectangle
        # Chevron is at the far right ~18px of the SplitButton.
        [Native]::LeftClickAt([int]($r.X + $r.Width - 12), [int]($r.Y + $r.Height/2))
        Start-Sleep -Milliseconds 900
        $root = $AE::RootElement
        $sem = Find-ByName $root 'Semantic' $null 4
        if ($sem) { [void](Invoke-El $sem); Write-Step 'Switched to Semantic mode' }
        else { Write-Step 'Semantic menu item not found' }
    }
    Wait-Settle 1
    # Type a natural-language query to showcase Semantic mode (do NOT run it — the
    # readable NL query and the "Semantic" mode indicator are what we want to show).
    $query = Find-ById $win 'QueryBox' 5
    if ($query) { [void](Set-EditText $query 'C# files with async methods modified this year') }
    Wait-Settle 3
    Capture-Window $win 'semantic-search.png' -Activate
}

function Open-Settings($win, [int]$TabIndex, [string]$TabName) {
    # Locate the gear by AutomationId (x:Name="SettingsButton") and physically click its center.
    # Raise the window first so the click isn't occluded by another app (e.g. VS Code).
    [Native]::Raise([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 1
    $gear = Find-ById $win 'SettingsButton' 5
    if ($gear) {
        $r = $gear.Current.BoundingRectangle
        Write-Step ("SettingsButton rect X={0} Y={1} W={2} H={3}" -f $r.X, $r.Y, $r.Width, $r.Height)
        try { $p = $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $p.Invoke() } catch { Write-Step "Invoke threw: $_" }
        Wait-Settle 1
        [Native]::LeftClickAt([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
    } else {
        Write-Step 'SettingsButton not found'
    }
    Wait-Settle 2
    $settingsWin = Get-WindowByNameLike '*Settings*' 8
    if (-not $settingsWin) {
        # The settings window may appear as an owned window (child of main in the UIA tree).
        try {
            $cond = [System.Windows.Automation.OrCondition]::new(
                $PC::new($AE::ControlTypeProperty, $CT::Window),
                $PC::new($AE::ControlTypeProperty, $CT::Pane))
            $deadline = (Get-Date).AddSeconds(6)
            while ((Get-Date) -lt $deadline -and -not $settingsWin) {
                $cands = $AE::RootElement.FindAll($TS::Descendants, $cond)
                foreach ($c in $cands) { try { if ($c.Current.Name -like '*Settings*' -and $c.Current.Name -notlike 'YaguSetup*') { $settingsWin = $c; break } } catch {} }
                if (-not $settingsWin) { Start-Sleep -Milliseconds 400 }
            }
        } catch {}
    }
    if (-not $settingsWin) {
        Write-Step 'Settings window not found — listing top-level windows:'
        try {
            $wins = $AE::RootElement.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
            foreach ($w in $wins) { try { Write-Host ("    win: '" + $w.Current.Name + "'") } catch {} }
        } catch {}
        return $null
    }
    # Select the desired tab by its header text in the TabList.
    $tab = Find-ByName $settingsWin $TabName $null 5
    if ($tab) {
        try { $sip = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern); $sip.Select() }
        catch { [void](Invoke-El $tab) }
        Write-Step "Selected tab $TabName"
    } else { Write-Step "Tab $TabName not found" }
    Wait-Settle 2
    return $settingsWin
}

function Scenario-SettingsAi {
    Write-Host "[settings-ai] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 3
    $sw = Open-Settings $win 11 'AI'
    if ($sw) { Capture-Window $sw 'settings-ai.png' -Activate }
}

function Scenario-SettingsOcr {
    Write-Host "[settings-ocr] launching..."
    Stop-AllYagu
    Start-Yagu "--dir `"$Directory`" --window-mode traditional" | Out-Null
    $win = Get-YaguWindow 25
    if (-not $win) { Write-Host 'FAILED: no Yagu window'; return }
    [Native]::Activate([IntPtr]$win.Current.NativeWindowHandle)
    Wait-Settle 3
    $sw = Open-Settings $win 2 'OCR'
    if ($sw) { Capture-Window $sw 'settings-ocr.png' -Activate }
}

# --- Dispatch ----------------------------------------------------------------

switch ($Scenario) {
    'match-nav'     { Scenario-MatchNav }
    'traditional'   { Scenario-Traditional }
    'editor'        { Scenario-Editor }
    'multi-preview' { Scenario-MultiPreview }
    'semantic'      { Scenario-Semantic }
    'settings-ai'   { Scenario-SettingsAi }
    'settings-ocr'  { Scenario-SettingsOcr }
    'all' {
        Scenario-Traditional
        Scenario-MatchNav
        Scenario-Editor
        Scenario-MultiPreview
        Scenario-Semantic
        Scenario-SettingsAi
        Scenario-SettingsOcr
    }
}

Stop-AllYagu
Write-Host "Done. Screenshots in $OutDir"
