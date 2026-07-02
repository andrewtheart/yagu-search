# Drive the Yagu UI to validate the OCR image preview: search the YaguOcrTest folder
# for a word that only exists inside an image (via OCR), click the match, and screenshot
# the preview. The preview should show the picture above its recognized OCR text rendered
# with a line-number gutter + highlighted match (NOT the raw binary bytes).
#
# Modelled on drive-google-matches.ps1 (UIAutomation + raw mouse input).

param(
    [string]$Directory = "$env:USERPROFILE\YaguOcrTest",
    [string]$Query = "BANANA",
    [string]$ImageFile = "banana-smoothie.png",
    [string]$ScreenshotDir = "C:\src\Yagu\TestResults\ImagePreview",
    [string]$LogFile = "C:\src\Yagu\TestResults\ImagePreview\run.log",
    [int]$FindFileTimeoutSec = 120
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class YaguDriveImg {
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
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$PC = [System.Windows.Automation.PropertyCondition]

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
            if ($h -ne [IntPtr]::Zero) { [YaguDriveImg]::Activate($h); Start-Sleep -Milliseconds 200 }
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
    [YaguDriveImg]::LeftClick()
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
        [System.Windows.Forms.SendKeys]::SendWait(($text -replace '([+^%~(){}])', '{$1}'))
    }
    return $edit
}

function Dismiss-GotItTips {
    try {
        $root2 = $AE::RootElement
        $btns = $root2.FindAll($TS::Descendants, $PC::new($AE::NameProperty, "Got it"))
        foreach ($b in $btns) {
            try {
                if ($b.Current.ControlType -ne $CT::Button) { continue }
                if ($b.Current.IsOffscreen) { continue }
                try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
                catch { Click-Rect $b.Current.BoundingRectangle }
                Start-Sleep -Milliseconds 200
            } catch { }
        }
    } catch { }
}

# ──────────────────────────────────────────────────────────────────────────────
Log "=== Drive Yagu: OCR image preview verification ==="
Log "Directory=$Directory Query=$Query ImageFile=$ImageFile"

$yaguExe = "C:\src\Yagu\Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe"
$root = $AE::RootElement

$existing = Get-Process -Name Yagu -ErrorAction SilentlyContinue
if ($existing) {
    Log "  Closing $($existing.Count) existing Yagu process(es): $($existing.Id -join ', ')"
    $existing | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Log "[1] Launching fresh Yagu with auto-search args..."
$argList = "--window-mode traditional --dir `"$Directory`" --query `"$Query`""
Log "  Args: $argList"
Start-Process -FilePath $yaguExe -ArgumentList $argList | Out-Null
Start-Sleep -Seconds 5
$win = $null
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
try { [YaguDriveImg]::Maximize([IntPtr]$win.Current.NativeWindowHandle); Start-Sleep -Milliseconds 700 } catch {}
Dismiss-GotItTips
Take-Screenshot "00-search-started" | Out-Null

Log "[3] Locating ResultsList..."
$resultsList = Find-Element -Parent $win -AutomationId "ResultsList" -TimeoutSeconds 15
if (-not $resultsList) { Log "ERROR: ResultsList not found"; exit 1 }

Log "[4] Waiting for target image '$ImageFile' to appear (max ${FindFileTimeoutSec}s)..."
$fileEl = $null
$deadline = (Get-Date).AddSeconds($FindFileTimeoutSec)
while ((Get-Date) -lt $deadline) {
    $all = $resultsList.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
        try { if ($el.Current.Name -like "*$ImageFile*" -and $el.Current.ControlType -eq $CT::Text) { $fileEl = $el; break } } catch {}
    }
    if ($fileEl) { Log "  Target image appeared: '$($fileEl.Current.Name)'"; break }
    Start-Sleep -Milliseconds 600
}
if (-not $fileEl) { Log "ERROR: target image not found within timeout"; Take-Screenshot "00-file-not-found" | Out-Null; exit 2 }
Take-Screenshot "01-file-found" | Out-Null

Log "[5] Opening the OCR match preview..."
Activate-Window
Dismiss-GotItTips
Start-Sleep -Milliseconds 400

# Ensure the file group is expanded so its match line renders. Use ExpandCollapsePattern
# (idempotent) on the FileGroup ListItem / expander rather than a toggle-click.
foreach ($cand in @(
        $resultsList.FindFirst($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::ListItem)),
        $resultsList.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, "FileGroupResultExpander")))) {
    if ($cand) {
        try { $cand.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600 } catch {}
    }
}
# Fallback: click the file row if still not expanded.
if (-not $resultsList.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, "MatchLineLocationButton"))) {
    Click-Rect $fileEl.Current.BoundingRectangle
    Start-Sleep -Milliseconds 900
}

# The match line is a MatchLineLocationButton (Name like "Line 1, column 1"). Clicking it
# opens the sections preview with the active-match box on the recognized OCR text. Retry
# while the result drawer renders.
$match = $null
for ($attempt = 0; $attempt -lt 15 -and -not $match; $attempt++) {
    foreach ($el in $resultsList.FindAll($TS::Descendants, $PC::new($AE::AutomationIdProperty, "MatchLineLocationButton"))) {
        try {
            if ($el.Current.IsOffscreen) { continue }
            $r = $el.Current.BoundingRectangle
            if ([double]::IsNaN($r.X) -or $r.Width -le 0) { continue }
            $match = $el; break
        } catch { }
    }
    if (-not $match) { Start-Sleep -Milliseconds 400 }
}
if ($match) {
    Log "  Clicking match line '$($match.Current.Name)'..."
    Click-Rect $match.Current.BoundingRectangle
    Start-Sleep -Milliseconds 1500
    Dismiss-GotItTips
    Start-Sleep -Milliseconds 800
} else {
    Log "  WARNING: No MatchLineLocationButton found; preview may be empty."
}

Log "[6] Screenshotting the preview..."
$shot = Take-Screenshot "02-image-preview"
Log "  Saved: $shot"
Log "Done. Screenshots in: $ScreenshotDir"
Log "App left open (pid=$($win.Current.ProcessId)) for inspection."
