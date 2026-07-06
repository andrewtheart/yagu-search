# Autonomous GUI regression test for the multiline (cross-line) search toggles.
# Drives the running WinUI 3 app via UIAutomation and verifies:
#   TC-G01  the inline "\n" MultilineToggle exists, has the \n glyph, sits between Regex and Exact,
#           and starts Off by default.
#   TC-G02  the Advanced Options "Match across lines" checkbox + its ". matches newlines" sub-toggle
#           exist, and the sub-toggle is disabled while multiline is off and enabled once it is on.
#   TC-G06  the inline toggle and the Advanced Options checkbox stay in sync (two-way binding).
#
# Exit codes: 0 = all checks pass, 1 = one or more checks failed, 2 = skipped (environment not clean,
# e.g. another Yagu instance is running so single-instance would hijack the launch).
#
# The user's settings.json is snapshotted and restored so toggling never persists a changed default.
param([Parameter(Mandatory = $true)][string]$Exe)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$auto  = [System.Windows.Automation.AutomationElement]
$Desc  = [System.Windows.Automation.TreeScope]::Descendants
$Child = [System.Windows.Automation.TreeScope]::Children
$Tog   = [System.Windows.Automation.TogglePattern]::Pattern
$Inv   = [System.Windows.Automation.InvokePattern]::Pattern

$settings = Join-Path $env:APPDATA 'Yagu\settings.json'
$backup   = $null
$launched = $null
$failures = @()

function Check($name, $cond, $detail) {
    if ($cond) { Write-Host "PASS $name  $detail" }
    else { Write-Host "FAIL $name  $detail"; $script:failures += $name }
}

try {
    # Snapshot settings and force MultilineSearchDefault=false so the "off by default" check is
    # deterministic regardless of the machine's current saved default.
    if (Test-Path $settings) {
        $backup = [IO.File]::ReadAllText($settings)
        try {
            $j = $backup | ConvertFrom-Json
            $j.MultilineSearchDefault = $false
            [IO.File]::WriteAllText($settings, ($j | ConvertTo-Json -Depth 30))
        } catch { }
    }

    # Ensure a clean single-instance environment: kill dev-build Yagu; bail if any other Yagu runs.
    Get-Process Yagu -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\Yagu\bin\*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $other = Get-Process Yagu -ErrorAction SilentlyContinue
    if ($other) { Write-Host "SKIP: another Yagu instance is running ($($other[0].Path)); close it to run the GUI test."; exit 2 }

    Start-Process -FilePath $Exe | Out-Null
    Start-Sleep -Seconds 6
    # The launcher relaunches detached; find the live dev-build GUI process.
    $proc = Get-Process Yagu -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\Yagu\bin\*' -and -not $_.HasExited } | Select-Object -First 1
    if (-not $proc) { Write-Host "SKIP: launched Yagu exited (single-instance handoff?); no window to test."; exit 2 }
    $launched = $proc

    $root = $auto::RootElement
    $win = $null
    for ($i = 0; $i -lt 15 -and -not $win; $i++) {
        $win = $root.FindFirst($Child, (New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$proc.Id)))
        if (-not $win) { Start-Sleep -Milliseconds 700 }
    }
    if (-not $win) { Write-Host 'FAIL: Yagu window not found via UIAutomation.'; exit 1 }

    function WinId($id) { $win.FindFirst($Desc, (New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id))) }
    function State($e) { ($e.GetCurrentPattern($Tog)).Current.ToggleState }
    function Flip($e) { ($e.GetCurrentPattern($Tog)).Toggle(); Start-Sleep -Milliseconds 400 }
    function Click($e) { ($e.GetCurrentPattern($Inv)).Invoke(); Start-Sleep -Milliseconds 600 }
    function Left($e) { $e.Current.BoundingRectangle.Left }

    # --- TC-G01: inline toggle exists, glyph, order, off by default ---
    $mt = WinId 'MultilineToggle'; $rt = WinId 'RegexToggle'; $et = WinId 'ExactMatchToggle'
    Check 'TC-G01-exists' ($null -ne $mt) 'MultilineToggle present'
    if ($mt -and $rt -and $et) {
        Check 'TC-G01-glyph' ($mt.Current.Name -eq '\n') "glyph='$($mt.Current.Name)'"
        Check 'TC-G01-order' (((Left $rt) -lt (Left $mt)) -and ((Left $mt) -lt (Left $et))) 'Regex < Multiline < Exact'
        Check 'TC-G01-default-off' ((State $mt) -eq 'Off') "state=$(State $mt)"
        if ((State $mt) -ne 'Off') { Flip $mt }
    }

    # --- Open Advanced Options ---
    $adv = WinId 'AdvancedOptionsToggle'
    if ($adv) { Click $adv }
    $mlAdv = WinId 'MultilineAdvancedToggle'; $mlDot = WinId 'MultilineDotAllToggle'
    Check 'TC-G02-advanced-present' ($mlAdv -and $mlDot) 'Advanced "Match across lines" + ". matches newlines" present'
    if ($mlAdv -and $mlDot) {
        # TC-G06a: advanced mirrors the (Off) inline state
        Check 'TC-G06-mirror-off' ((State $mlAdv) -eq 'Off') "advanced=$(State $mlAdv) mirrors inline Off"
        # TC-G02a: dot-all disabled while multiline off
        Check 'TC-G02-dotall-disabled' (-not $mlDot.Current.IsEnabled) "dot-all IsEnabled=$($mlDot.Current.IsEnabled) while multiline Off"
        # Toggle multiline ON via the advanced checkbox
        Flip $mlAdv
        # TC-G02b: dot-all enabled after multiline on
        Check 'TC-G02-dotall-enabled' ($mlDot.Current.IsEnabled) "dot-all IsEnabled=$($mlDot.Current.IsEnabled) after multiline On"
        # TC-G06b: inline toggle now reflects On (two-way binding round-trip)
        if ($mt) { Check 'TC-G06-roundtrip' ((State $mt) -eq 'On') "inline=$(State $mt) after Advanced On" }
    }

    if ($failures.Count -eq 0) { Write-Host 'ALL PASS'; exit 0 }
    else { Write-Host "FAILURES: $($failures -join ', ')"; exit 1 }
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($launched) { try { Stop-Process -Id $launched.Id -Force -ErrorAction SilentlyContinue } catch { } }
    if ($null -ne $backup) { try { [IO.File]::WriteAllText($settings, $backup) } catch { } }
}
