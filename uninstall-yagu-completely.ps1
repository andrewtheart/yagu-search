#Requires -Version 5.1
<#
.SYNOPSIS
  Completely uninstalls Yagu, removes its saved data, and launches the latest x64 installer.

.DESCRIPTION
  Run this script from an elevated PowerShell. It:
  1. Discovers per-machine and per-user Yagu installs in both registry views.
  2. Stops only Yagu processes whose executable is under a detected Yagu install directory.
  3. Runs every registered Yagu uninstaller silently.
  4. Removes Yagu-owned settings, logs, caches, default indexes, and residual install folders.
  5. Removes only recognized Yagu index scopes from custom index roots, preserving unrelated files.
  6. Removes residual Yagu registry/context-menu/PATH entries.
  7. Verifies cleanup and launches the newest online x64 installer in this repository.

.EXAMPLE
  .\uninstall-yagu-completely.ps1

.EXAMPLE
  .\uninstall-yagu-completely.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$installer = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -File -Filter 'YaguSetup-*-x64.exe' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $installer) {
    throw "No online x64 Yagu installer was found under: $(Join-Path $repoRoot 'installer')"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
$isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isElevated -and -not $WhatIfPreference) {
    throw 'This script must be run from an Administrator PowerShell.'
}

$appId = '{8F4E2B5A-3C7D-4A1E-B9F6-2D8E5A7C3F1B}_is1'
$uninstallSubkey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$registryLocations = @(
    [pscustomobject]@{ Hive = [Microsoft.Win32.RegistryHive]::LocalMachine; View = [Microsoft.Win32.RegistryView]::Registry64 },
    [pscustomobject]@{ Hive = [Microsoft.Win32.RegistryHive]::LocalMachine; View = [Microsoft.Win32.RegistryView]::Registry32 },
    [pscustomobject]@{ Hive = [Microsoft.Win32.RegistryHive]::CurrentUser; View = [Microsoft.Win32.RegistryView]::Registry64 },
    [pscustomobject]@{ Hive = [Microsoft.Win32.RegistryHive]::CurrentUser; View = [Microsoft.Win32.RegistryView]::Registry32 }
)

function Get-YaguRegistrations {
    $registrations = foreach ($location in $registryLocations) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($location.Hive, $location.View)
        try {
            $key = $baseKey.OpenSubKey($uninstallSubkey)
            if ($key) {
                try {
                    [pscustomobject]@{
                        Hive = $location.Hive
                        View = $location.View
                        DisplayName = [string]$key.GetValue('DisplayName')
                        InstallLocation = [string]$key.GetValue('InstallLocation')
                        UninstallString = [string]$key.GetValue('UninstallString')
                    }
                }
                finally {
                    $key.Dispose()
                }
            }
        }
        finally {
            $baseKey.Dispose()
        }
    }

    $registrations | Where-Object {
        $_.DisplayName -like 'Yagu version*' -or $_.InstallLocation -match '(?i)\\Yagu\\?$'
    }
}

function Get-ExecutableFromCommand([string]$command) {
    if ([string]::IsNullOrWhiteSpace($command)) { return $null }
    if ($command -match '^\s*"([^"]+)"') { return $matches[1] }
    if ($command -match '^\s*([^\s]+)') { return $matches[1] }
    return $null
}

function Test-RecognizedIndexScope([System.IO.DirectoryInfo]$directory) {
    return $directory.Name -match '^[0-9a-fA-F]{32}$' -and
        ((Test-Path -LiteralPath (Join-Path $directory.FullName 'current.a')) -or
         (Test-Path -LiteralPath (Join-Path $directory.FullName 'current.b')))
}

function Remove-RecognizedCustomIndexes([string]$indexRoot) {
    if ([string]::IsNullOrWhiteSpace($indexRoot) -or -not (Test-Path -LiteralPath $indexRoot -PathType Container)) {
        return
    }

    $defaultRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Yagu\content-index')).TrimEnd('\')
    $fullRoot = [System.IO.Path]::GetFullPath($indexRoot).TrimEnd('\')
    if ([string]::Equals($fullRoot, $defaultRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    Get-ChildItem -LiteralPath $fullRoot -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object {
            (Test-RecognizedIndexScope $_) -or
            $_.Name -like '.build-*' -or
            $_.Name -like '.pdf-backup-*'
        } |
        ForEach-Object {
            if ($PSCmdlet.ShouldProcess($_.FullName, 'Remove recognized Yagu index data')) {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
        }

    $writerLock = Join-Path $fullRoot '.writer.lock'
    if ((Test-Path -LiteralPath $writerLock) -and
        $PSCmdlet.ShouldProcess($writerLock, 'Remove Yagu index writer lock')) {
        Remove-Item -LiteralPath $writerLock -Force
    }
}

function Remove-RegistrySubkey([Microsoft.Win32.RegistryHive]$hive, [Microsoft.Win32.RegistryView]$view, [string]$subkey) {
    $description = "$hive/$view/$subkey"
    if (-not $PSCmdlet.ShouldProcess($description, 'Remove registry key')) { return }

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
    try {
        $baseKey.DeleteSubKeyTree($subkey, $false)
    }
    finally {
        $baseKey.Dispose()
    }
}

$registrations = @(Get-YaguRegistrations)
$installDirectories = @(
    $registrations.InstallLocation
    (Join-Path $env:ProgramFiles 'Yagu')
    if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Yagu' }
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
    [System.IO.Path]::GetFullPath($_).TrimEnd('\')
} | Sort-Object -Unique

$customIndexRoots = @()
$roamingRoot = Join-Path $env:APPDATA 'Yagu'
if (Test-Path -LiteralPath $roamingRoot) {
    Get-ChildItem -LiteralPath $roamingRoot -File -Filter 'settings*' -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $settings = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace([string]$settings.IndexStorageDirectory)) {
                    $customIndexRoots += [string]$settings.IndexStorageDirectory
                }
            }
            catch {
                Write-Warning "Could not inspect custom index settings in $($_.FullName): $($_.Exception.Message)"
            }
        }
}

$preservedIndexRoot = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Yagu' -Name 'PreservedIndexStorageDirectory' -ErrorAction SilentlyContinue).PreservedIndexStorageDirectory
if (-not [string]::IsNullOrWhiteSpace([string]$preservedIndexRoot)) {
    $customIndexRoots += [string]$preservedIndexRoot
}
$customIndexRoots = @($customIndexRoots | Sort-Object -Unique)

Write-Host 'Stopping installed Yagu processes...' -ForegroundColor Cyan
Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
    $process = $_
    $processPath = $null
    try { $processPath = $process.Path } catch { }
    if (-not [string]::IsNullOrWhiteSpace($processPath)) {
        $ownedProcess = $installDirectories | Where-Object {
            $processPath.StartsWith($_ + '\', [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($ownedProcess -and $PSCmdlet.ShouldProcess("$($process.ProcessName) (PID $($process.Id))", 'Stop installed Yagu process')) {
            Stop-Process -Id $process.Id -Force
        }
    }
}

foreach ($registration in $registrations) {
    $uninstaller = Get-ExecutableFromCommand $registration.UninstallString
    if ($uninstaller -and (Test-Path -LiteralPath $uninstaller)) {
        Write-Host "Uninstalling $($registration.DisplayName)..." -ForegroundColor Cyan
        if ($PSCmdlet.ShouldProcess($uninstaller, 'Run silent Yagu uninstaller')) {
            $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -Wait -PassThru
            if ($process.ExitCode -ne 0) {
                throw "Yagu uninstaller failed with exit code $($process.ExitCode): $uninstaller"
            }
        }
    }
}

foreach ($customIndexRoot in $customIndexRoots) {
    Remove-RecognizedCustomIndexes $customIndexRoot
}

$ownedDirectories = @(
    $roamingRoot
    (Join-Path $env:LOCALAPPDATA 'Yagu')
    (Join-Path $env:ProgramData 'Yagu')
    $installDirectories
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique

foreach ($directory in $ownedDirectories) {
    if ((Test-Path -LiteralPath $directory) -and
        $PSCmdlet.ShouldProcess($directory, 'Remove Yagu-owned directory')) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

foreach ($location in $registryLocations) {
    Remove-RegistrySubkey $location.Hive $location.View $uninstallSubkey
}
Remove-RegistrySubkey ([Microsoft.Win32.RegistryHive]::CurrentUser) ([Microsoft.Win32.RegistryView]::Default) 'Software\Yagu'
Remove-RegistrySubkey ([Microsoft.Win32.RegistryHive]::CurrentUser) ([Microsoft.Win32.RegistryView]::Default) 'Software\Classes\Directory\shell\Yagu'
Remove-RegistrySubkey ([Microsoft.Win32.RegistryHive]::CurrentUser) ([Microsoft.Win32.RegistryView]::Default) 'Software\Classes\Directory\Background\shell\Yagu'

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not [string]::IsNullOrWhiteSpace($machinePath)) {
    $filteredPathEntries = @($machinePath -split ';' | Where-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return $false }
        $entry = $_.Trim().Trim('"').TrimEnd('\')
        -not ($installDirectories | Where-Object {
            [string]::Equals($entry, $_, [System.StringComparison]::OrdinalIgnoreCase)
        })
    })
    $updatedMachinePath = $filteredPathEntries -join ';'
    if ($updatedMachinePath -ne $machinePath -and
        $PSCmdlet.ShouldProcess('Machine PATH', 'Remove Yagu install directories')) {
        [Environment]::SetEnvironmentVariable('Path', $updatedMachinePath, 'Machine')
    }
}

if ($WhatIfPreference) {
    Write-Host 'WhatIf preview complete; no changes were made and the installer was not launched.' -ForegroundColor Yellow
    return
}

$remainingRegistrations = @(Get-YaguRegistrations)
$remainingOwnedDirectories = @($ownedDirectories | Where-Object { Test-Path -LiteralPath $_ })
if ($remainingRegistrations.Count -gt 0 -or $remainingOwnedDirectories.Count -gt 0) {
    throw "Cleanup verification failed. Remaining registrations: $($remainingRegistrations.Count); remaining owned directories: $($remainingOwnedDirectories -join ', ')"
}

Write-Host 'Yagu uninstall and data cleanup completed.' -ForegroundColor Green
Write-Host "Launching $($installer.FullName)" -ForegroundColor Green
Start-Process -FilePath $installer.FullName