#requires -Version 5.1
<#
.SYNOPSIS
  Drops the Windows standby/system file cache to force a cold-cache measurement,
  then reports standby + available memory before and after.
.NOTES
  Requires elevation. Uses RAMMap64.exe (-Et empty standby, -E0 empty priority-0 standby,
  -Es empty system working set, -Em empty modified list).
#>
param(
    [string]$RamMap = "RAMMap64.exe"
)

function Get-StandbyMB {
    $s0 = (Get-Counter '\Memory\Standby Cache Core Bytes' -EA SilentlyContinue).CounterSamples[0].CookedValue
    $s1 = (Get-Counter '\Memory\Standby Cache Normal Priority Bytes' -EA SilentlyContinue).CounterSamples[0].CookedValue
    $s2 = (Get-Counter '\Memory\Standby Cache Reserve Bytes' -EA SilentlyContinue).CounterSamples[0].CookedValue
    [int](($s0 + $s1 + $s2) / 1MB)
}
function Get-AvailMB { [int](Get-Counter '\Memory\Available MBytes' -EA SilentlyContinue).CounterSamples[0].CookedValue }

$beforeStandby = Get-StandbyMB
$beforeAvail = Get-AvailMB
Write-Host ("Before: Standby={0} MB  Available={1} MB" -f $beforeStandby, $beforeAvail)

& $RamMap -Es | Out-Null
& $RamMap -Em | Out-Null
& $RamMap -Et | Out-Null
& $RamMap -E0 | Out-Null
Start-Sleep -Seconds 4

$afterStandby = Get-StandbyMB
$afterAvail = Get-AvailMB
Write-Host ("After:  Standby={0} MB  Available={1} MB" -f $afterStandby, $afterAvail)
Write-Host ("Dropped standby: {0} MB | Available gained: {1} MB" -f ($beforeStandby - $afterStandby), ($afterAvail - $beforeAvail))
