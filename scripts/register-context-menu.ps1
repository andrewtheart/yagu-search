<#
.SYNOPSIS
  Registers (or unregisters) the "Search with Yagu" Explorer context menu entry.

.PARAMETER ExePath
  Full path to Yagu.exe.

.PARAMETER InstallDir
  Directory containing Yagu.exe. Used when ExePath is not supplied.

.PARAMETER MenuText
  Text displayed in Explorer. Defaults to "Search with Yagu".

.PARAMETER RegistryKeyName
  Registry key name under Directory\shell and Directory\Background\shell.

.PARAMETER Uninstall
  Removes the registry entries instead of installing them.

.EXAMPLE
  .\register-context-menu.ps1 -ExePath 'C:\Tools\Yagu\Yagu.exe'

.EXAMPLE
  .\register-context-menu.ps1 -InstallDir 'C:\Tools\Yagu'

.EXAMPLE
  .\register-context-menu.ps1 -Uninstall
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$ExePath,
  [Parameter(Mandatory = $false)]
  [string]$InstallDir,
  [Parameter(Mandatory = $false)]
  [string]$MenuText = 'Search with Yagu',
  [Parameter(Mandatory = $false)]
  [string]$RegistryKeyName = 'Yagu',
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RegistryKeyName)) {
  $RegistryKeyName = 'Yagu'
}

$regPaths = @(
  "HKCU:\Software\Classes\Directory\shell\$RegistryKeyName",
  "HKCU:\Software\Classes\Directory\Background\shell\$RegistryKeyName"
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

if (-not $ExePath -and $InstallDir) {
  $ExePath = Join-Path $InstallDir 'Yagu.exe'
}

if (-not $ExePath) {
  throw "Provide -ExePath '<path to Yagu.exe>', provide -InstallDir '<directory containing Yagu.exe>', or use -Uninstall."
}
if (-not (Test-Path $ExePath)) {
  throw "ExePath does not exist: $ExePath"
}

foreach ($regPath in $regPaths) {
  New-Item -Path $regPath -Force | Out-Null
  New-Item -Path "$regPath\command" -Force | Out-Null
  Set-ItemProperty -Path $regPath -Name '(Default)' -Value $MenuText
  Set-ItemProperty -Path $regPath -Name 'Icon' -Value $ExePath
  Set-ItemProperty -Path "$regPath\command" -Name '(Default)' -Value ('"{0}" --dir "%V"' -f $ExePath)
  Write-Host "Registered $regPath"
}

Write-Host "Done. Right-click any folder to see '$MenuText'."
