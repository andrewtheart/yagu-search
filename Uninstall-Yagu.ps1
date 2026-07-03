<#
.SYNOPSIS
  Removes files installed by Install-Wagu.ps1 and unregisters the Explorer context menu.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$InstallDir,

  # Skip all interactive prompts, accepting the default answer for each.
  [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$contextMenuScript = Join-Path $repoRoot 'scripts\register-context-menu.ps1'
$manifestName = '.wagu-install-manifest.txt'
$exeName = 'Yagu.exe'
$installRegistryPath = 'HKCU:\Software\Yagu'
$installRegistryValueName = 'InstallDir'

function Get-RegisteredInstallDirectory {
  try {
    if (-not (Test-Path -LiteralPath $installRegistryPath)) {
      return $null
    }

    $value = (Get-ItemProperty -LiteralPath $installRegistryPath -Name $installRegistryValueName -ErrorAction Stop).$installRegistryValueName
    if ([string]::IsNullOrWhiteSpace($value)) {
      return $null
    }

    return [System.IO.Path]::GetFullPath($value)
  }
  catch {
    return $null
  }
}

function Resolve-InstallDirectory {
  param([string]$Value)

  $registeredDir = Get-RegisteredInstallDirectory
  if (-not [string]::IsNullOrWhiteSpace($registeredDir) -and -not (Test-Path -LiteralPath $registeredDir)) {
    Write-Warning "The registered install directory no longer exists on disk: $registeredDir"
    Write-Warning "The registry entry will still be cleaned up."
  }
  $defaultDir = if ([string]::IsNullOrWhiteSpace($registeredDir)) { (Get-Location).Path } else { $registeredDir }
  if ([string]::IsNullOrWhiteSpace($Value)) {
    $answer = Read-Host "Install directory to remove [$defaultDir]"
    if ([string]::IsNullOrWhiteSpace($answer)) {
      $Value = $defaultDir
    } else {
      $Value = $answer
    }
  }

  return [System.IO.Path]::GetFullPath($Value)
}

function Remove-InstallRegistryEntry {
  param([string]$Path)

  if (Test-Path -LiteralPath $installRegistryPath) {
    Remove-Item -LiteralPath $installRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed install location registry entry."
  }
}

function Unregister-ContextMenu {
  $registryKeyNames = @('Yagu', 'Wagu')
  if (Test-Path -LiteralPath $contextMenuScript) {
    foreach ($registryKeyName in $registryKeyNames) {
      try {
        & $contextMenuScript -RegistryKeyName $registryKeyName -Uninstall
      } catch {
        Write-Warning "Context menu unregistration for '$registryKeyName' failed: $_ — continuing."
      }
    }
  } else {
    Write-Warning "Could not find context menu script: $contextMenuScript"
  }

  foreach ($registryKeyName in $registryKeyNames) {
    $regPaths = @(
      "HKCU:\Software\Classes\Directory\shell\$registryKeyName",
      "HKCU:\Software\Classes\Directory\Background\shell\$registryKeyName"
    )

    foreach ($regPath in $regPaths) {
      if (Test-Path -LiteralPath $regPath) {
        Remove-Item -LiteralPath $regPath -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed $regPath"
      }
    }
  }
}

function Read-YesNo {
  param(
    [string]$Prompt,
    [bool]$DefaultYes = $false
  )

  if ($Force) { return $DefaultYes }

  $suffix = if ($DefaultYes) { '[Y/n]' } else { '[y/N]' }
  $answer = Read-Host "$Prompt $suffix"
  if ([string]::IsNullOrWhiteSpace($answer)) {
    return $DefaultYes
  }

  return $answer -match '^(y|yes)$'
}

function Stop-RunningYagu {
  param([string]$InstallPath)

  $exeFullPath = Join-Path $InstallPath $exeName
  $procs = @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue |
    Where-Object {
      try { [System.IO.Path]::GetFullPath($_.MainModule.FileName) -eq [System.IO.Path]::GetFullPath($exeFullPath) }
      catch { $false }
    })

  if ($procs.Count -eq 0) { return }

  Write-Warning "$exeName is currently running from the install directory."
  $stop = if ($Force) { $true } else { Read-YesNo -Prompt "Stop it now to continue uninstall?" -DefaultYes $true }
  if (-not $stop) {
    throw "Uninstall cancelled: $exeName must not be running when uninstalling."
  }

  foreach ($p in $procs) {
    try {
      $p.CloseMainWindow() | Out-Null
      if (-not $p.WaitForExit(3000)) { $p.Kill() }
      Write-Host "Stopped $exeName (PID $($p.Id))."
    } catch {
      Write-Warning "Could not stop $exeName (PID $($p.Id)): $_"
    }
  }
}

function Test-IsPathUnderDirectory {
  param(
    [string]$Parent,
    [string]$Child
  )

  $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
  $childFull = [System.IO.Path]::GetFullPath($Child)
  return $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)
}

function Remove-InstalledFiles {
  param([string]$Path)

  $manifestPath = Join-Path $Path $manifestName
  if (Test-Path -LiteralPath $manifestPath) {
    $entries = @(Get-Content -LiteralPath $manifestPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $directories = New-Object System.Collections.Generic.HashSet[string]

    foreach ($entry in $entries) {
      $target = Join-Path $Path $entry
      if (-not (Test-IsPathUnderDirectory -Parent $Path -Child $target)) {
        Write-Warning "Skipping suspicious manifest entry outside install directory: $entry"
        continue
      }

      if (Test-Path -LiteralPath $target -PathType Leaf) {
        try {
          Remove-Item -LiteralPath $target -Force -ErrorAction Stop
          Write-Host "Removed $target"
        } catch {
          Write-Warning "Could not remove $target : $_"
        }
      }

      $dir = Split-Path -Parent $target
      while ($dir -and (Test-IsPathUnderDirectory -Parent $Path -Child $dir)) {
        $directories.Add($dir) | Out-Null
        $next = Split-Path -Parent $dir
        if ($next -eq $dir) { break }
        $dir = $next
      }
    }

    Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue

    $directories |
      Sort-Object Length -Descending |
      ForEach-Object {
        if ((Test-Path -LiteralPath $_ -PathType Container) -and -not (Get-ChildItem -LiteralPath $_ -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
          Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue
        }
      }

    return
  }

  $exePath = Join-Path $Path $exeName
  if (Test-Path -LiteralPath $exePath -PathType Leaf) {
    Remove-Item -LiteralPath $exePath -Force
    Write-Host "Removed $exePath"
  } else {
    Write-Host "No installer manifest or $exeName found in $Path"
  }
}

function Get-EverythingInstallation {
  $registryPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
  )

  foreach ($registryPath in $registryPaths) {
    $registryMatches = @(Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue |
      Where-Object {
        ($_.DisplayName -match '^Everything(\s|$)') -or
        ($_.DisplayName -like '*Everything*' -and $_.Publisher -like '*voidtools*')
      })

    if ($registryMatches.Count -gt 0) {
      $registryMatch = $registryMatches[0]
      return [pscustomobject]@{
        DisplayName = $registryMatch.DisplayName
        UninstallString = $registryMatch.UninstallString
        InstallLocation = $registryMatch.InstallLocation
      }
    }
  }

  $commonPaths = @(
    'C:\Program Files\Everything\Everything.exe',
    'C:\Program Files (x86)\Everything\Everything.exe',
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Everything\Everything.exe')
  )

  foreach ($candidate in $commonPaths) {
    if (Test-Path -LiteralPath $candidate) {
      return [pscustomobject]@{
        DisplayName = 'Everything Search'
        UninstallString = $null
        InstallLocation = Split-Path -Parent $candidate
      }
    }
  }

  return $null
}

function Invoke-EverythingUninstall {
  param($Installation)

  if (-not $Installation.UninstallString) {
    Write-Warning "Everything appears to be installed, but no uninstall command was found."
    if ($Installation.InstallLocation) {
      Write-Host "Install location: $($Installation.InstallLocation)"
    }
    return
  }

  Write-Host "Starting Everything uninstaller..."
  Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $Installation.UninstallString) -Wait
}

$installPath = Resolve-InstallDirectory -Value $InstallDir

if (Test-Path -LiteralPath $installPath) {
  Stop-RunningYagu -InstallPath $installPath
}

Unregister-ContextMenu

if (Test-Path -LiteralPath $installPath) {
  Remove-InstalledFiles -Path $installPath
  Remove-InstallRegistryEntry -Path $installPath
} else {
  Write-Host "Install directory does not exist: $installPath"
  Remove-InstallRegistryEntry -Path $installPath
}

$everything = Get-EverythingInstallation
if ($everything) {
  Write-Host "Detected $($everything.DisplayName). Everything is optional; Yagu can run without it."
  if (Read-YesNo -Prompt "Uninstall Everything Search too?" -DefaultYes $false) {
    Invoke-EverythingUninstall -Installation $everything
  } else {
    Write-Host "Leaving Everything Search installed."
  }
}

Write-Host "Uninstall complete."
