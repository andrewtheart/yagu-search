#Requires -Version 5.1
<#
.SYNOPSIS
  Repeatedly drives the Settings search -> "Open section" jump and reports whether the aurora
  highlight became ready each time.

.DESCRIPTION
  Verification driver for the settings-search highlight. For each query it types the query into the
  Settings search box, invokes the first "Open section" result, and reads back from yagu.log how many
  layout passes the highlight needed (or whether it gave up).

  OPEN THE YAGU SETTINGS WINDOW FIRST. A background process cannot bring Yagu to the foreground
  (Windows foreground lock), so this script does not try to click the gear itself.

  Requires file logging at Verbose (Settings -> Logging -> file log level = Verbose).

.EXAMPLE
  .\scripts\drive-settings-highlight.ps1 -Rounds 3
#>
[CmdletBinding()]
param(
    [string[]]$Queries = @('theme', 'font', 'index', 'terminal', 'preview', 'hotkey', 'wrap', 'color'),
    [int]$Rounds = 2
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$logPath = Join-Path $env:APPDATA 'Yagu\yagu.log'
if (-not (Test-Path -LiteralPath $logPath)) { throw "No yagu.log at $logPath" }

function Get-YaguSettingsWindow {
    $procs = @(Get-Process -Name Yagu -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { throw 'Yagu is not running.' }
    $ids = @($procs.Id)
    $mainHandles = @($procs | ForEach-Object { $_.MainWindowHandle.ToInt64() })

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $all) {
        try {
            if ($ids -notcontains $w.Current.ProcessId) { continue }
            if ($mainHandles -contains [int64]$w.Current.NativeWindowHandle) { continue }
            return $w
        } catch { }
    }
    return $null
}

function Find-Descendant {
    param($Root, [string]$AutomationId, [string]$Name, [string]$ControlType)
    $conditions = New-Object System.Collections.Generic.List[System.Windows.Automation.Condition]
    if ($AutomationId) {
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)))
    }
    if ($Name) {
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    }
    if ($ControlType) {
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::$ControlType)))
    }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] }
                 else { New-Object System.Windows.Automation.AndCondition($conditions.ToArray()) }
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Read-LogSince {
    param([long]$Offset)
    $stream = [System.IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($Offset, 'Begin')
        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    } finally { $stream.Dispose() }
}

$settings = Get-YaguSettingsWindow
if (-not $settings) {
    throw 'Settings window not found. Open Yagu Settings (gear icon) first, then re-run this script.'
}
Write-Host ("Driving Settings window (pid {0})." -f $settings.Current.ProcessId) -ForegroundColor Green

$results = @()
for ($round = 1; $round -le $Rounds; $round++) {
    foreach ($query in $Queries) {
        $searchBox = Find-Descendant -Root $settings -AutomationId 'SearchBox'
        if (-not $searchBox) { throw 'Could not find the Settings SearchBox.' }
        $value = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)

        $value.SetValue('')
        Start-Sleep -Milliseconds 250
        $value.SetValue($query)
        Start-Sleep -Milliseconds 700

        $jump = Find-Descendant -Root $settings -Name 'Open section' -ControlType 'Button'
        if (-not $jump) {
            $results += [pscustomobject]@{ Round = $round; Query = $query; Status = 'no-result'; Passes = $null }
            continue
        }

        $beforeLength = (Get-Item -LiteralPath $logPath).Length
        $jump.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 2600   # the flash runs ~1.9s once the scroll settles

        $delta = Read-LogSince -Offset $beforeLength
        $ready = [regex]::Match($delta, 'Setting highlight ready after (\d+) layout pass')
        $results += [pscustomobject]@{
            Round  = $round
            Query  = $query
            Status = if ($ready.Success) { 'flashed' }
                     elseif ($delta -match 'Setting highlight gave up') { 'GAVE-UP' }
                     else { 'NO-LOG' }
            Passes = if ($ready.Success) { [int]$ready.Groups[1].Value } else { $null }
        }
    }
}

$results | Format-Table -AutoSize
$attempted = @($results | Where-Object { $_.Status -ne 'no-result' })
$flashed = @($attempted | Where-Object { $_.Status -eq 'flashed' })
Write-Host ''
$allGood = $attempted.Count -gt 0 -and $flashed.Count -eq $attempted.Count
Write-Host ("Flashed {0} / {1} jumps." -f $flashed.Count, $attempted.Count) `
    -ForegroundColor $(if ($allGood) { 'Green' } else { 'Red' })
if ($flashed.Count -gt 0) {
    $passes = @($flashed.Passes)
    Write-Host ("Layout passes needed: min={0} max={1}" -f `
        ($passes | Measure-Object -Minimum).Minimum, ($passes | Measure-Object -Maximum).Maximum)
}
if (-not $allGood) { exit 1 }
