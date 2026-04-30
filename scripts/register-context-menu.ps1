<#
.SYNOPSIS
  Registers (or unregisters) the "Search with Yagu" Explorer context menu entry.

.PARAMETER ExePath
  Full path to Yagu.exe.

.PARAMETER Uninstall
  Removes the registry entries instead of installing them.

.EXAMPLE
  .\register-context-menu.ps1 -ExePath 'C:\Tools\Yagu\Yagu.exe'
  .\register-context-menu.ps1 -Uninstall
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$ExePath,
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$regPaths = @(
  'HKCU:\Software\Classes\Directory\shell\Yagu',
  'HKCU:\Software\Classes\Directory\Background\shell\Yagu'
)

if ($Uninstall) {
  foreach ($p in $regPaths) {
    if (Test-Path $p) {
      Remove-Item -Path $p -Recurse -Force
      Write-Host "Removed $p"
    }
  }
  return
}

if (-not $ExePath) {
  throw "Provide -ExePath '<path to Yagu.exe>' or use -Uninstall."
}
if (-not (Test-Path $ExePath)) {
  throw "ExePath does not exist: $ExePath"
}

foreach ($regPath in $regPaths) {
  New-Item -Path $regPath -Force | Out-Null
  New-Item -Path "$regPath\command" -Force | Out-Null
  Set-ItemProperty -Path $regPath -Name '(Default)' -Value 'Search with Yagu'
  Set-ItemProperty -Path $regPath -Name 'Icon' -Value $ExePath
  Set-ItemProperty -Path "$regPath\command" -Name '(Default)' -Value ('"{0}" --dir "%V"' -f $ExePath)
  Write-Host "Registered $regPath"
}

Write-Host "Done. Right-click any folder to see 'Search with Yagu'."
