# Drives the running Yagu window: sets directory=C:, query=println, clicks Search,
# then captures a PrintWindow screenshot every ~0.3s (annotating each frame with the
# elapsed time since click, the Search/Cancel button label, and whether the progress
# overlay is visible) until results start coming in. Read-only aside from typing + one click.
param(
    [string]$OutDir = "C:\src\Yagu\TestResults\SearchStartCapture",
    [string]$Directory = "C:",
    [string]$Query = "println",
    [double]$IntervalSec = 0.3,
    [int]$MaxFrames = 60,
    [int]$ExtraFramesAfterResults = 6
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Pw {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static bool Capture(IntPtr hwnd, string outPath, uint flags) {
        RECT r; if (!GetWindowRect(hwnd, out r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, flags);
            g.ReleaseHdc(hdc);
            if (!ok) return false;
            bmp.Save(outPath, ImageFormat.Png);
            return true;
        }
    }
}
"@
if (-not ("Pw" -as [type])) { Add-Type -TypeDefinition $src -ReferencedAssemblies @("System.Drawing") }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$PC = [System.Windows.Automation.PropertyCondition]
$ValuePattern = [System.Windows.Automation.ValuePattern]
$InvokePattern = [System.Windows.Automation.InvokePattern]

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Filter 'frame_*.png' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# Locate the Yagu window.
$proc = Get-Process -Name Yagu -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Host "NO_YAGU_WINDOW"; exit 1 }
$hwnd = $proc.MainWindowHandle
$root = $AE::RootElement
$win = $null
foreach ($w in $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))) {
    try { if ($w.Current.Name -like "Yagu*" -and $w.Current.ProcessId -eq $proc.Id) { $win = $w; break } } catch {}
}
if (-not $win) { Write-Host "NO_YAGU_UIA_WINDOW"; exit 1 }
Write-Host "Window '$($win.Current.Name)' pid=$($proc.Id) hwnd=$hwnd"

function Find([string]$id) { return $win.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, $id)) }
# AutoSuggestBox does not expose ValuePattern itself; its inner Edit child does.
function SetBoxValue([string]$id, [string]$v) {
    $box = Find $id
    if (-not $box) { Write-Host "SetBoxValue: '$id' not found"; return $false }
    $edit = $box.FindFirst($TS::Descendants, $PC::new($AE::ControlTypeProperty, $CT::Edit))
    $target = if ($edit) { $edit } else { $box }
    try { $vp = $target.GetCurrentPattern($ValuePattern::Pattern); $vp.SetValue($v); return $true }
    catch { Write-Host "SetBoxValue '$id' failed: $_"; return $false }
}

$results = Find 'ResultsList'
Write-Host "Populating fields..."
[void](SetBoxValue 'DirectoryBox' $Directory)
Start-Sleep -Milliseconds 200
[void](SetBoxValue 'QueryBox' $Query)
Start-Sleep -Milliseconds 500   # let bindings settle & any suggestion list dismiss

# Re-find each frame: WinUI removes Collapsed elements from the UIA tree, so presence == visible.
function ButtonState {
    $cancel = Find 'SearchCancelButton'
    if ($cancel) {
        $lbl = Find 'SearchCancelLabel'
        $t = '?'; try { $t = $lbl.Current.Name } catch {}
        return "cancelBtn:'$t'"
    }
    $split = Find 'SearchSplitButton'
    if ($split) { return "splitBtn:Search" }
    return "none"
}
function ProgressVisible { if (Find 'SearchProgressOverlay') { return $true } else { return $false } }
function ResultCount {
    try {
        if (-not $results) { $results = Find 'ResultsList' }
        $items = $results.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::DataItem))
        if ($items -and $items.Count -gt 0) { return $items.Count }
        $items = $results.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::ListItem))
        if ($items) { return $items.Count } else { return 0 }
    } catch { return -1 }
}

# Ensure the app is idle before we start: cancel any in-flight search and wait for the idle SplitButton.
$cancelBtn = Find 'SearchCancelButton'
if ($cancelBtn) {
    Write-Host "A search is in progress - cancelling first..."
    try { $cancelBtn.GetCurrentPattern($InvokePattern::Pattern).Invoke() } catch {}
    for ($j = 0; $j -lt 120; $j++) {
        Start-Sleep -Milliseconds 500
        if (Find 'SearchSplitButton') { break }
        $c = Find 'SearchCancelButton'
        if ($c) { try { $c.GetCurrentPattern($InvokePattern::Pattern).Invoke() } catch {} }
    }
    Write-Host "Idle now: splitBtn=$([bool](Find 'SearchSplitButton'))"
}

Write-Host "Pre-click: button=$(ButtonState) progressVisible=$(ProgressVisible) results=$(ResultCount)"
$baselineResults = ResultCount

# Locate the idle Search button (SplitButton when semantic is available, else the plain button).
$clickTarget = Find 'SearchSplitButton'
if (-not $clickTarget) { $clickTarget = Find 'SearchCancelButton' }
if (-not $clickTarget) { Write-Host "NO_SEARCH_BUTTON"; exit 1 }
Write-Host "Click target automationId=$($clickTarget.Current.AutomationId) name='$($clickTarget.Current.Name)'"

# Click Search and start the clock at the same instant.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$t0 = Get-Date
try { $clickTarget.GetCurrentPattern($InvokePattern::Pattern).Invoke() } catch { Write-Host "Invoke failed: $_"; exit 1 }
Write-Host "Clicked Search at $($t0.ToString('HH:mm:ss.fff')) (baseline results=$baselineResults)"

$rows = @()
$framesAfterMorph = 0
$progressFirstSeenMs = $null
$buttonMorphFirstMs = $null
$resultsFirstSeenMs = $null

for ($i = 0; $i -lt $MaxFrames; $i++) {
    $elapsedMs = [int]$sw.Elapsed.TotalMilliseconds
    $lbl = ButtonState
    $prog = ProgressVisible
    $rc = ResultCount
    $name = "frame_{0:D3}_{1:D5}ms.png" -f $i, $elapsedMs
    $path = Join-Path $OutDir $name
    $ok = [Pw]::Capture($hwnd, $path, 2)
    if ($prog -and $null -eq $progressFirstSeenMs) { $progressFirstSeenMs = $elapsedMs }
    # The idle button is 'splitBtn:Search'; anything else means the search UI has taken over (morphed).
    if ($null -eq $buttonMorphFirstMs -and $lbl -ne 'splitBtn:Search') { $buttonMorphFirstMs = $elapsedMs }
    if ($null -eq $resultsFirstSeenMs -and $null -ne $buttonMorphFirstMs -and $rc -gt 0 -and $rc -ne $baselineResults) { $resultsFirstSeenMs = $elapsedMs }
    $rows += [pscustomobject]@{ Frame = $i; ElapsedMs = $elapsedMs; Button = $lbl; ProgressVisible = $prog; Results = $rc; Saved = $ok }
    Write-Host ("  [{0,2}] t={1,6}ms  {2,-18}  progress={3,-5}  results={4}" -f $i, $elapsedMs, $lbl, $prog, $rc)

    # Stop once the button has morphed (search UI is live) and we've captured a few streaming frames.
    if ($null -ne $buttonMorphFirstMs) {
        $framesAfterMorph++
        if ($framesAfterMorph -ge $ExtraFramesAfterResults) { break }
    }

    $target = ($i + 1) * $IntervalSec * 1000.0
    $remain = $target - $sw.Elapsed.TotalMilliseconds
    if ($remain -gt 0) { Start-Sleep -Milliseconds ([int]$remain) }
}

Write-Host ""
Write-Host "=== Summary ==="
Write-Host ("Button morphed to Cancel   : {0}" -f ($(if ($buttonMorphFirstMs -ne $null) { "$buttonMorphFirstMs ms after click" } else { "not seen" })))
Write-Host ("Progress bar first visible : {0}" -f ($(if ($progressFirstSeenMs -ne $null) { "$progressFirstSeenMs ms after click" } else { "not detected via UIA (Grid not in control tree; see screenshots)" })))
Write-Host ("New results first seen     : {0}" -f ($(if ($resultsFirstSeenMs -ne $null) { "$resultsFirstSeenMs ms after click" } else { "not seen" })))
Write-Host ("Frames captured            : {0}  ->  {1}" -f $rows.Count, $OutDir)
$rows | Format-Table -AutoSize
