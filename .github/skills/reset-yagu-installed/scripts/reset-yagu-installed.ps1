[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(DontShow)]
    [switch]$Elevated,

    [Parameter(DontShow)]
    [string]$ExpectedUserSid,

    [Parameter(DontShow)]
    [string]$ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CommandContext = $PSCmdlet
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:FailedUninstallRecords = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:FailedInstallLocations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:YaguProcessNames = @(
    'Yagu',
    'Yagu.IndexWorker',
    'Yagu.OcrWorker',
    'Yagu.SemanticWorker'
)
$script:YaguUninstallKeyName = '{8F4E2B5A-3C7D-4A1E-B9F6-2D8E5A7C3F1B}_is1'

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[reset-yagu-installed] $Message"
}

function Add-Failure {
    param([Parameter(Mandatory)][string]$Message)
    $script:Failures.Add($Message)
    Write-Error $Message -ErrorAction Continue
}

function Get-UninstallRecordId {
    param([Parameter(Mandatory)][object]$Record)
    return "$($Record.Hive)|$($Record.View)|$($Record.SubkeyName)"
}

function Add-UninstallFailure {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$Message
    )

    [void]$script:FailedUninstallRecords.Add((Get-UninstallRecordId $Record))
    $registeredLocation = Get-NormalizedPath $Record.InstallLocation
    if ($null -ne $registeredLocation) {
        [void]$script:FailedInstallLocations.Add($registeredLocation)
    }
    $registeredCommand = Split-UninstallCommand $(if ([string]::IsNullOrWhiteSpace($Record.QuietUninstallString)) {
        $Record.UninstallString
    } else {
        $Record.QuietUninstallString
    })
    if ($null -ne $registeredCommand) {
        $registeredExecutable = Get-NormalizedPath $registeredCommand.Executable
        if ($null -ne $registeredExecutable) {
            [void]$script:FailedInstallLocations.Add((Split-Path -Parent $registeredExecutable))
        }
    }
    Add-Failure $Message
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-NormalizedPath {
    param([AllowNull()][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    try {
        $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
        $full = [IO.Path]::GetFullPath($expanded)
        $root = [IO.Path]::GetPathRoot($full)
        if ([string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase)) {
            return $root
        }
        return $full.TrimEnd('\')
    }
    catch {
        return $null
    }
}

$skillDirectory = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$script:RepositoryRoot = Get-NormalizedPath (Join-Path $skillDirectory '..\..\..')
if ($null -eq $script:RepositoryRoot -or
    -not (Test-Path -LiteralPath (Join-Path $script:RepositoryRoot '.git'))) {
    throw 'Could not resolve the Yagu repository root from the installed-reset skill path.'
}

function Test-SamePath {
    param(
        [AllowNull()][string]$Left,
        [AllowNull()][string]$Right
    )

    $normalizedLeft = Get-NormalizedPath $Left
    $normalizedRight = Get-NormalizedPath $Right
    return $null -ne $normalizedLeft -and
        $null -ne $normalizedRight -and
        [string]::Equals($normalizedLeft, $normalizedRight, [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathAtOrBelow {
    param(
        [AllowNull()][string]$Path,
        [AllowNull()][string]$Root
    )

    $normalizedPath = Get-NormalizedPath $Path
    $normalizedRoot = Get-NormalizedPath $Root
    if ($null -eq $normalizedPath -or $null -eq $normalizedRoot) {
        return $false
    }
    return (Test-SamePath $normalizedPath $normalizedRoot) -or
        $normalizedPath.StartsWith(
            $normalizedRoot.TrimEnd('\') + '\',
            [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathHasReparsePoint {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$StopAt
    )

    $current = Get-NormalizedPath $Path
    $stop = Get-NormalizedPath $StopAt
    while ($null -ne $current -and (Test-PathAtOrBelow $current $stop)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            return $true
        }
        if (Test-SamePath $current $stop) {
            break
        }
        $current = Split-Path -Parent $current
    }
    return $false
}

function Test-SafeYaguDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$InstallDirectory,
        [switch]$UpdateDirectory
    )

    $normalized = Get-NormalizedPath $Path
    if ($null -eq $normalized) {
        return $false
    }

    $root = [IO.Path]::GetPathRoot($normalized)
    if ([string]::Equals($normalized, $root, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $leaf = [IO.Path]::GetFileName($normalized)
    if ($UpdateDirectory) {
        return $leaf -like 'yagu-update-*'
    }
    if ($InstallDirectory) {
        if ((Test-PathAtOrBelow $normalized $script:RepositoryRoot) -or
            (Test-PathAtOrBelow $script:RepositoryRoot $normalized)) {
            return $false
        }
        return $leaf -match '^(?i:Yagu)(?:[-_.].*)?$'
    }
    return $leaf -in @('Yagu', '.Yagu')
}

function Invoke-ResetAction {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Action,
        [Parameter(Mandatory)][scriptblock]$Operation
    )

    if ($script:CommandContext.ShouldProcess($Target, $Action)) {
        & $Operation
    }
}

function Remove-TreeWithoutTraversingReparsePoints {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Remove-Item -LiteralPath $item.FullName -Force
        return
    }

    foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force)) {
        if ($child.PSIsContainer -or
            (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            Remove-TreeWithoutTraversingReparsePoints $child.FullName
        } else {
            Remove-Item -LiteralPath $child.FullName -Force
        }
    }
    Remove-Item -LiteralPath $item.FullName -Force
}

function Remove-YaguDirectory {
    param(
        [AllowNull()][string]$Path,
        [switch]$InstallDirectory,
        [switch]$UpdateDirectory
    )

    $normalized = Get-NormalizedPath $Path
    if ($null -eq $normalized -or -not (Test-Path -LiteralPath $normalized -PathType Container)) {
        return
    }
    if (-not (Test-SafeYaguDirectory -Path $normalized -InstallDirectory:$InstallDirectory -UpdateDirectory:$UpdateDirectory)) {
        Add-Failure "Refused to recursively remove unsafe directory '$normalized'."
        return
    }

    try {
        Invoke-ResetAction -Target $normalized -Action 'Remove Yagu-owned directory' -Operation {
            Remove-TreeWithoutTraversingReparsePoints $normalized
        }
    }
    catch {
        Add-Failure "Failed to remove directory '$normalized': $($_.Exception.Message)"
    }
}

function Remove-YaguFile {
    param([AllowNull()][string]$Path)

    $normalized = Get-NormalizedPath $Path
    if ($null -eq $normalized -or -not (Test-Path -LiteralPath $normalized -PathType Leaf)) {
        return
    }

    try {
        Invoke-ResetAction -Target $normalized -Action 'Remove Yagu-owned file' -Operation {
            Remove-Item -LiteralPath $normalized -Force
        }
    }
    catch {
        Add-Failure "Failed to remove file '$normalized': $($_.Exception.Message)"
    }
}

function Read-YaguStateLocations {
    $settingsPath = Join-Path $env:APPDATA 'Yagu\settings.json'
    $customIndexRoots = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $customTempDirectory = $null

    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $settingsIndexRoot = Get-NormalizedPath ([string]$settings.IndexStorageDirectory)
            if ($null -ne $settingsIndexRoot) {
                [void]$customIndexRoots.Add($settingsIndexRoot)
            }
            $customTempDirectory = Get-NormalizedPath ([string]$settings.SearchResultTempDirectory)
        }
        catch {
            Add-Failure "Could not read Yagu settings before cleanup: $($_.Exception.Message)"
        }
    }

    try {
        $registryState = Get-ItemProperty -LiteralPath 'HKCU:\Software\Yagu' -ErrorAction SilentlyContinue
        $preservedProperty = if ($null -eq $registryState) {
            $null
        } else {
            $registryState.PSObject.Properties['PreservedIndexStorageDirectory']
        }
        $preserved = if ($null -eq $preservedProperty) { $null } else { $preservedProperty.Value }
        $preservedIndexRoot = Get-NormalizedPath ([string]$preserved)
        if ($null -ne $preservedIndexRoot) {
            [void]$customIndexRoots.Add($preservedIndexRoot)
        }
    }
    catch {
        Add-Failure "Could not read the preserved index locator: $($_.Exception.Message)"
    }

    return [pscustomobject]@{
        CustomIndexRoots = @($customIndexRoots)
        CustomTempDirectory = $customTempDirectory
    }
}

function Get-YaguUninstallRecords {
    $records = [System.Collections.Generic.List[object]]::new()
    $hives = @(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryHive]::CurrentUser
    )
    $views = @(
        [Microsoft.Win32.RegistryView]::Registry64,
        [Microsoft.Win32.RegistryView]::Registry32
    )

    foreach ($hive in $hives) {
        foreach ($view in $views) {
            $baseKey = $null
            $uninstallKey = $null
            try {
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                $uninstallKey = $baseKey.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
                if ($null -eq $uninstallKey) {
                    continue
                }

                foreach ($subkeyName in $uninstallKey.GetSubKeyNames()) {
                    $appKey = $null
                    try {
                        $appKey = $uninstallKey.OpenSubKey($subkeyName)
                        $displayName = [string]$appKey.GetValue('DisplayName', '')
                        $isYagu = $subkeyName -eq $script:YaguUninstallKeyName
                        if (-not $isYagu) {
                            continue
                        }

                        $records.Add([pscustomobject]@{
                            Hive = $hive
                            View = $view
                            SubkeyName = $subkeyName
                            DisplayName = $displayName
                            InstallLocation = [string]$appKey.GetValue('InstallLocation', '')
                            UninstallString = [string]$appKey.GetValue('UninstallString', '')
                            QuietUninstallString = [string]$appKey.GetValue('QuietUninstallString', '')
                        })
                    }
                    finally {
                        if ($null -ne $appKey) {
                            $appKey.Dispose()
                        }
                    }
                }
            }
            catch {
                Add-Failure "Could not inspect $hive $view uninstall registrations: $($_.Exception.Message)"
            }
            finally {
                if ($null -ne $uninstallKey) {
                    $uninstallKey.Dispose()
                }
                if ($null -ne $baseKey) {
                    $baseKey.Dispose()
                }
            }
        }
    }

    return @($records)
}

function Split-UninstallCommand {
    param([AllowNull()][string]$Command)

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $null
    }

    $trimmed = $Command.Trim()
    if ($trimmed -match '^"([^"]+)"\s*(.*)$') {
        return [pscustomobject]@{ Executable = $matches[1]; Arguments = $matches[2] }
    }
    if ($trimmed -match '^(\S+)\s*(.*)$') {
        return [pscustomobject]@{ Executable = $matches[1]; Arguments = $matches[2] }
    }
    return $null
}

function Test-YaguInstallMarker {
    param([AllowNull()][string]$Path)

    $normalized = Get-NormalizedPath $Path
    if ($null -eq $normalized -or
        (Test-PathAtOrBelow $normalized $script:RepositoryRoot) -or
        (Test-PathAtOrBelow $script:RepositoryRoot $normalized) -or
        -not (Test-Path -LiteralPath $normalized -PathType Container)) {
        return $false
    }
    if (Test-Path -LiteralPath (Join-Path $normalized 'Yagu.exe') -PathType Leaf) {
        return $true
    }
    return @(Get-ChildItem -LiteralPath $normalized -File -Filter 'unins*.exe' -ErrorAction SilentlyContinue).Count -gt 0
}

function Get-YaguInstallLocations {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Records
    )

    $locations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $Records) {
        $installLocation = Get-NormalizedPath $record.InstallLocation
        if ($null -ne $installLocation -and (Test-YaguInstallMarker $installLocation)) {
            [void]$locations.Add($installLocation)
        }

        $command = Split-UninstallCommand $(if ([string]::IsNullOrWhiteSpace($record.QuietUninstallString)) {
            $record.UninstallString
        } else {
            $record.QuietUninstallString
        })
        if ($null -ne $command) {
            $uninstaller = Get-NormalizedPath $command.Executable
            if ($null -ne $uninstaller) {
                $uninstallerRoot = Split-Path -Parent $uninstaller
                if (Test-YaguInstallMarker $uninstallerRoot) {
                    [void]$locations.Add($uninstallerRoot)
                }
            }
        }
    }

    try {
        $registryState = Get-ItemProperty -LiteralPath 'HKCU:\Software\Yagu' -ErrorAction SilentlyContinue
        $installProperty = if ($null -eq $registryState) {
            $null
        } else {
            $registryState.PSObject.Properties['InstallDir']
        }
        $registeredInstall = if ($null -eq $installProperty) { $null } else { $installProperty.Value }
        $normalizedRegisteredInstall = Get-NormalizedPath ([string]$registeredInstall)
        if ($null -ne $normalizedRegisteredInstall -and
            (Test-YaguInstallMarker $normalizedRegisteredInstall)) {
            [void]$locations.Add($normalizedRegisteredInstall)
        }
    }
    catch {
        Add-Failure "Could not read Yagu's registered install directory: $($_.Exception.Message)"
    }

    $knownLocations = @(
        (Join-Path $env:ProgramFiles 'Yagu'),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Yagu' }),
        (Join-Path $env:LOCALAPPDATA 'Programs\Yagu')
    )
    foreach ($knownLocation in $knownLocations) {
        if (-not [string]::IsNullOrWhiteSpace($knownLocation)) {
            [void]$locations.Add((Get-NormalizedPath $knownLocation))
        }
    }

    foreach ($target in @('Machine', 'User')) {
        $pathValue = [Environment]::GetEnvironmentVariable('Path', $target)
        foreach ($segment in @($pathValue -split ';')) {
            $normalizedSegment = Get-NormalizedPath $segment
            if ($null -ne $normalizedSegment -and
                (Test-Path -LiteralPath (Join-Path $normalizedSegment 'Yagu.exe') -PathType Leaf)) {
                [void]$locations.Add($normalizedSegment)
            }
        }
    }

    return @($locations)
}

function Stop-YaguProcesses {
    for ($pass = 0; $pass -lt 3; $pass++) {
        $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessName -in $script:YaguProcessNames
        })
        if ($processes.Count -eq 0) {
            return
        }

        foreach ($process in $processes) {
            try {
                $target = "$($process.ProcessName) (PID $($process.Id))"
                Invoke-ResetAction -Target $target -Action 'Stop Yagu process by PID' -Operation {
                    Stop-Process -Id $process.Id -Force
                }
            }
            catch {
                if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
                    Add-Failure "Failed to stop $target`: $($_.Exception.Message)"
                }
            }
        }
        if ($WhatIfPreference) {
            return
        }
        Start-Sleep -Milliseconds 500
    }

    $remaining = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in $script:YaguProcessNames
    })
    foreach ($process in $remaining) {
        Add-Failure "Yagu process '$($process.ProcessName)' (PID $($process.Id)) is still running."
    }
}

function Invoke-YaguUninstallers {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Records
    )

    $started = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $Records) {
        $rawCommand = if ([string]::IsNullOrWhiteSpace($record.QuietUninstallString)) {
            $record.UninstallString
        } else {
            $record.QuietUninstallString
        }
        $command = Split-UninstallCommand $rawCommand
        if ($null -eq $command) {
            Add-UninstallFailure $record "No valid uninstaller command was registered for '$($record.DisplayName)'."
            continue
        }

        $executable = Get-NormalizedPath $command.Executable
        if ($null -eq $executable -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            Add-UninstallFailure $record "Registered Yagu uninstaller was not found: '$($command.Executable)'."
            continue
        }
        if ([IO.Path]::GetFileName($executable) -notmatch '^(?i:unins\d+\.exe)$') {
            Add-UninstallFailure $record "Refused to run unexpected Yagu uninstall command '$executable'."
            continue
        }

        $approvedInstallAncestors = if ($record.Hive -eq [Microsoft.Win32.RegistryHive]::LocalMachine) {
            @(
                $env:ProgramFiles,
                ${env:ProgramFiles(x86)}
            )
        } else {
            @((Join-Path $env:LOCALAPPDATA 'Programs'))
        }
        $uninstallerRoot = Split-Path -Parent $executable
        $approvedAncestor = @($approvedInstallAncestors | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            (Test-PathAtOrBelow $uninstallerRoot $_) -and
            -not (Test-SamePath $uninstallerRoot $_)
        } | Select-Object -First 1)
        if ($approvedAncestor.Count -eq 0) {
            Add-UninstallFailure $record "Refused to run Yagu uninstaller outside an approved install root: '$executable'."
            continue
        }
        if (-not [string]::IsNullOrWhiteSpace($record.InstallLocation) -and
            -not (Test-SamePath $record.InstallLocation $uninstallerRoot)) {
            Add-UninstallFailure $record "Yagu uninstaller '$executable' does not match its registered install location."
            continue
        }
        if (-not (Test-Path -LiteralPath (Join-Path $uninstallerRoot 'Yagu.exe') -PathType Leaf)) {
            Add-UninstallFailure $record "Yagu executable marker was not found beside '$executable'."
            continue
        }
        if (Test-PathHasReparsePoint -Path $uninstallerRoot -StopAt $approvedAncestor[0]) {
            Add-UninstallFailure $record "Refused to run Yagu uninstaller through a reparse point: '$executable'."
            continue
        }
        if ($record.Hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser -and
            (Test-IsAdministrator)) {
            Add-UninstallFailure $record "Refused to elevate the per-user Yagu uninstaller '$executable'."
            continue
        }
        if (-not $started.Add($executable)) {
            continue
        }

        $arguments = @()
        if (-not [string]::IsNullOrWhiteSpace($command.Arguments)) {
            $arguments += $command.Arguments
        }
        $arguments += @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

        try {
            Invoke-ResetAction -Target $executable -Action 'Run registered Yagu uninstaller silently' -Operation {
                $process = Start-Process -FilePath $executable -ArgumentList $arguments -Wait -PassThru
                if ($process.ExitCode -ne 0) {
                    throw "Uninstaller exited with code $($process.ExitCode)."
                }
            }
        }
        catch {
            Add-UninstallFailure $record "Yagu uninstaller '$executable' failed: $($_.Exception.Message)"
        }
    }
}

function Remove-StaleUninstallRegistrations {
    $remaining = @(Get-YaguUninstallRecords)
    foreach ($record in $remaining) {
        if ($script:FailedUninstallRecords.Contains((Get-UninstallRecordId $record))) {
            continue
        }
        $baseKey = $null
        $uninstallKey = $null
        try {
            $target = "$($record.Hive) $($record.View)\$($record.SubkeyName)"
            Invoke-ResetAction -Target $target -Action 'Remove stale Yagu uninstall registration' -Operation {
                $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($record.Hive, $record.View)
                $uninstallKey = $baseKey.OpenSubKey(
                    'Software\Microsoft\Windows\CurrentVersion\Uninstall',
                    $true)
                $uninstallKey.DeleteSubKeyTree($record.SubkeyName, $false)
            }
        }
        catch {
            Add-Failure "Failed to remove stale uninstall registration '$target': $($_.Exception.Message)"
        }
        finally {
            if ($null -ne $uninstallKey) {
                $uninstallKey.Dispose()
            }
            if ($null -ne $baseKey) {
                $baseKey.Dispose()
            }
        }
    }
}

function Remove-YaguRegistryIntegrations {
    if ($script:FailedUninstallRecords.Count -gt 0) {
        Write-Step 'Preserving integrations because at least one registered installation could not be removed.'
        return
    }

    $registryPaths = @(
        'HKCU:\Software\Classes\Directory\shell\Yagu',
        'HKCU:\Software\Classes\Directory\Background\shell\Yagu',
        'HKLM:\Software\Classes\Directory\shell\Yagu',
        'HKLM:\Software\Classes\Directory\Background\shell\Yagu',
        'HKCU:\Software\Yagu'
    )

    foreach ($registryPath in $registryPaths) {
        if (-not (Test-Path -LiteralPath $registryPath)) {
            continue
        }
        try {
            Invoke-ResetAction -Target $registryPath -Action 'Remove Yagu registry key' -Operation {
                Remove-Item -LiteralPath $registryPath -Recurse -Force
            }
        }
        catch {
            Add-Failure "Failed to remove registry key '$registryPath': $($_.Exception.Message)"
        }
    }
}

function Remove-YaguPathEntries {
    param([Parameter(Mandatory)][string[]]$InstallLocations)

    foreach ($target in @('Machine', 'User')) {
        try {
            $original = [Environment]::GetEnvironmentVariable('Path', $target)
            if ([string]::IsNullOrWhiteSpace($original)) {
                continue
            }

            $kept = [System.Collections.Generic.List[string]]::new()
            $changed = $false
            foreach ($segment in @($original -split ';')) {
                if ([string]::IsNullOrWhiteSpace($segment)) {
                    continue
                }

                $remove = $false
                foreach ($installLocation in $InstallLocations) {
                    if (Test-SamePath $segment $installLocation) {
                        $remove = $true
                        break
                    }
                }
                if ($remove) {
                    $changed = $true
                } else {
                    $kept.Add($segment)
                }
            }

            if ($changed) {
                $newValue = $kept -join ';'
                Invoke-ResetAction -Target "$target PATH" -Action 'Remove Yagu install directory entries' -Operation {
                    [Environment]::SetEnvironmentVariable('Path', $newValue, $target)
                }
            }
        }
        catch {
            Add-Failure "Failed to clean the $target PATH: $($_.Exception.Message)"
        }
    }
}

function Remove-CustomIndexData {
    param([AllowNull()][string]$IndexRoot)

    $normalizedRoot = Get-NormalizedPath $IndexRoot
    $defaultRoot = Get-NormalizedPath (Join-Path $env:LOCALAPPDATA 'Yagu\content-index')
    if ($null -eq $normalizedRoot -or
        (Test-SamePath $normalizedRoot $defaultRoot) -or
        -not (Test-Path -LiteralPath $normalizedRoot -PathType Container)) {
        return
    }

    try {
        foreach ($directory in @(Get-ChildItem -LiteralPath $normalizedRoot -Directory -Force)) {
            $recognizedScope = $directory.Name -match '^[0-9a-fA-F]{32}$' -and
                ((Test-Path -LiteralPath (Join-Path $directory.FullName 'current.a') -PathType Leaf) -or
                 (Test-Path -LiteralPath (Join-Path $directory.FullName 'current.b') -PathType Leaf))
            if ($recognizedScope -or
                $directory.Name -match '^(?i:\.build-)[0-9a-f]{32}$' -or
                $directory.Name -match '^(?i:\.pdf-backup-)[0-9a-f]{32}$') {
                try {
                    Invoke-ResetAction -Target $directory.FullName -Action 'Remove recognized Yagu index artifact' -Operation {
                        Remove-TreeWithoutTraversingReparsePoints $directory.FullName
                    }
                }
                catch {
                    Add-Failure "Failed to remove custom index artifact '$($directory.FullName)': $($_.Exception.Message)"
                }
            }
        }
        Remove-YaguFile (Join-Path $normalizedRoot '.writer.lock')
    }
    catch {
        Add-Failure "Failed to inspect custom index root '$normalizedRoot': $($_.Exception.Message)"
    }
}

function Remove-YaguTempData {
    param([AllowNull()][string]$ConfiguredTempDirectory)

    $dedicatedTempDirectories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$dedicatedTempDirectories.Add((Join-Path ([IO.Path]::GetTempPath()) 'Yagu'))
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if ($drive.IsReady -and $drive.DriveType -in @(
            [IO.DriveType]::Fixed,
            [IO.DriveType]::Removable)) {
            [void]$dedicatedTempDirectories.Add((Join-Path $drive.RootDirectory.FullName 'Temp\Yagu'))
        }
    }

    $normalizedConfigured = Get-NormalizedPath $ConfiguredTempDirectory
    if ($null -ne $normalizedConfigured) {
        $parentLeaf = [IO.Path]::GetFileName((Split-Path -Parent $normalizedConfigured))
        $leaf = [IO.Path]::GetFileName($normalizedConfigured)
        if ($leaf -eq 'Yagu' -and $parentLeaf -eq 'Temp') {
            [void]$dedicatedTempDirectories.Add($normalizedConfigured)
        } elseif (Test-Path -LiteralPath $normalizedConfigured -PathType Container) {
            foreach ($pattern in @('yagu-results-*.tmp', '.yagu-write-test-*.tmp')) {
                foreach ($file in @(Get-ChildItem -LiteralPath $normalizedConfigured -File -Force -Filter $pattern -ErrorAction SilentlyContinue)) {
                    Remove-YaguFile $file.FullName
                }
            }
        }
    }

    foreach ($directory in $dedicatedTempDirectories) {
        Remove-YaguDirectory $directory
    }

    $systemTemp = Get-NormalizedPath ([IO.Path]::GetTempPath())
    if ($null -ne $systemTemp -and (Test-Path -LiteralPath $systemTemp -PathType Container)) {
        foreach ($pattern in @('yagu-results-*.tmp', '.yagu-write-test-*.tmp')) {
            foreach ($file in @(Get-ChildItem -LiteralPath $systemTemp -File -Force -Filter $pattern -ErrorAction SilentlyContinue)) {
                Remove-YaguFile $file.FullName
            }
        }
        foreach ($directory in @(Get-ChildItem -LiteralPath $systemTemp -Directory -Force -Filter 'yagu-update-*' -ErrorAction SilentlyContinue)) {
            Remove-YaguDirectory -Path $directory.FullName -UpdateDirectory
        }
    }
}

function Remove-YaguShortcuts {
    if ($script:FailedUninstallRecords.Count -gt 0) {
        return
    }

    $shortcutPaths = @(
        (Join-Path $env:PUBLIC 'Desktop\Yagu.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Yagu.lnk'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Yagu'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Yagu')
    )
    foreach ($shortcutPath in $shortcutPaths) {
        if (Test-Path -LiteralPath $shortcutPath -PathType Container) {
            Remove-YaguDirectory $shortcutPath
        } else {
            Remove-YaguFile $shortcutPath
        }
    }
}

function Test-CustomIndexDataAbsent {
    param([AllowNull()][string]$IndexRoot)

    $normalizedRoot = Get-NormalizedPath $IndexRoot
    $defaultRoot = Get-NormalizedPath (Join-Path $env:LOCALAPPDATA 'Yagu\content-index')
    if ($null -eq $normalizedRoot -or
        (Test-SamePath $normalizedRoot $defaultRoot) -or
        -not (Test-Path -LiteralPath $normalizedRoot -PathType Container)) {
        return $true
    }

    if (Test-Path -LiteralPath (Join-Path $normalizedRoot '.writer.lock')) {
        return $false
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $normalizedRoot -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($directory.Name -match '^(?i:\.build-)[0-9a-f]{32}$' -or
            $directory.Name -match '^(?i:\.pdf-backup-)[0-9a-f]{32}$') {
            return $false
        }
        if ($directory.Name -match '^[0-9a-fA-F]{32}$' -and
            ((Test-Path -LiteralPath (Join-Path $directory.FullName 'current.a')) -or
             (Test-Path -LiteralPath (Join-Path $directory.FullName 'current.b')))) {
            return $false
        }
    }
    return $true
}

function Verify-Reset {
    param(
        [Parameter(Mandatory)][string[]]$InstallLocations,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$CustomIndexRoots,
        [AllowNull()][string]$CustomTempDirectory
    )

    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in $script:YaguProcessNames
    })) {
        Add-Failure "Verification found running process '$($process.ProcessName)' (PID $($process.Id))."
    }

    foreach ($record in @(Get-YaguUninstallRecords)) {
        Add-Failure "Verification found uninstall registration '$($record.DisplayName)' in $($record.Hive) $($record.View)."
    }

    foreach ($installLocation in $InstallLocations) {
        if (Test-Path -LiteralPath $installLocation) {
            Add-Failure "Verification found install directory '$installLocation'."
        }
    }

    foreach ($path in @(
        (Join-Path $env:APPDATA 'Yagu'),
        (Join-Path $env:LOCALAPPDATA 'Yagu'),
        (Join-Path $env:USERPROFILE '.Yagu'),
        (Join-Path $env:LOCALAPPDATA 'VirtualStore\Program Files\Yagu')
    )) {
        if (Test-Path -LiteralPath $path) {
            Add-Failure "Verification found Yagu data '$path'."
        }
    }

    foreach ($customIndexRoot in $CustomIndexRoots) {
        if (-not (Test-CustomIndexDataAbsent $customIndexRoot)) {
            Add-Failure "Verification found recognized Yagu data under custom index root '$customIndexRoot'."
        }
    }

    $normalizedTemp = Get-NormalizedPath $CustomTempDirectory
    if ($null -ne $normalizedTemp -and (Test-Path -LiteralPath $normalizedTemp -PathType Container)) {
        $leftovers = @(Get-ChildItem -LiteralPath $normalizedTemp -File -Force -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -like 'yagu-results-*.tmp' -or $_.Name -like '.yagu-write-test-*.tmp'
        })
        if ($leftovers.Count -gt 0) {
            Add-Failure "Verification found Yagu result temp files under '$normalizedTemp'."
        }
    }

    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if ($drive.IsReady -and $drive.DriveType -in @(
            [IO.DriveType]::Fixed,
            [IO.DriveType]::Removable)) {
            $dedicatedTemp = Join-Path $drive.RootDirectory.FullName 'Temp\Yagu'
            if (Test-Path -LiteralPath $dedicatedTemp) {
                Add-Failure "Verification found dedicated Yagu temp data '$dedicatedTemp'."
            }
        }
    }

    $systemTemp = Get-NormalizedPath ([IO.Path]::GetTempPath())
    $systemYaguTemp = Join-Path $systemTemp 'Yagu'
    if (Test-Path -LiteralPath $systemYaguTemp) {
        Add-Failure "Verification found Yagu preview temp data '$systemYaguTemp'."
    }
    if ($null -ne $systemTemp -and (Test-Path -LiteralPath $systemTemp -PathType Container)) {
        $systemTempResidue = @(Get-ChildItem -LiteralPath $systemTemp -Force -ErrorAction SilentlyContinue | Where-Object {
            ($_.PSIsContainer -and $_.Name -like 'yagu-update-*') -or
            (-not $_.PSIsContainer -and
                ($_.Name -like 'yagu-results-*.tmp' -or $_.Name -like '.yagu-write-test-*.tmp'))
        })
        if ($systemTempResidue.Count -gt 0) {
            Add-Failure "Verification found Yagu residue in system temp '$systemTemp'."
        }
    }

    foreach ($shortcutPath in @(
        (Join-Path $env:PUBLIC 'Desktop\Yagu.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Yagu.lnk'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Yagu'),
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Yagu')
    )) {
        if (Test-Path -LiteralPath $shortcutPath) {
            Add-Failure "Verification found Yagu shortcut residue '$shortcutPath'."
        }
    }

    foreach ($registryPath in @(
        'HKCU:\Software\Yagu',
        'HKCU:\Software\Classes\Directory\shell\Yagu',
        'HKCU:\Software\Classes\Directory\Background\shell\Yagu',
        'HKLM:\Software\Classes\Directory\shell\Yagu',
        'HKLM:\Software\Classes\Directory\Background\shell\Yagu'
    )) {
        if (Test-Path -LiteralPath $registryPath) {
            Add-Failure "Verification found registry integration '$registryPath'."
        }
    }

    foreach ($target in @('Machine', 'User')) {
        $pathValue = [Environment]::GetEnvironmentVariable('Path', $target)
        foreach ($segment in @($pathValue -split ';')) {
            foreach ($installLocation in $InstallLocations) {
                if (Test-SamePath $segment $installLocation) {
                    Add-Failure "Verification found '$segment' in the $target PATH."
                }
            }
        }
    }
}

if ($Elevated) {
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ([string]::IsNullOrWhiteSpace($ExpectedUserSid) -or $currentSid -ne $ExpectedUserSid) {
        Write-Error 'The elevated reset identity does not match the invoking user. Sign in with an administrator account and rerun the skill from that account.'
        exit 1
    }
}

if (-not $WhatIfPreference -and -not $Elevated -and -not (Test-IsAdministrator)) {
    $perUserRecords = @(Get-YaguUninstallRecords | Where-Object {
        $_.Hive -eq [Microsoft.Win32.RegistryHive]::CurrentUser
    })
    if ($perUserRecords.Count -gt 0) {
        Write-Step 'Running registered per-user uninstallers without elevation.'
        Invoke-YaguUninstallers -Records $perUserRecords
    }

    Write-Step 'Requesting elevation for the complete machine-wide reset.'
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $invokingSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $resultFile = Join-Path ([IO.Path]::GetTempPath()) "reset-yagu-installed-$([Guid]::NewGuid().ToString('N')).result"
    $argumentList = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated -ExpectedUserSid `"$invokingSid`" -ResultPath `"$resultFile`""
    try {
        $elevatedProcess = Start-Process -FilePath $windowsPowerShell -Verb RunAs -ArgumentList $argumentList -WindowStyle Hidden -Wait -PassThru
        if (Test-Path -LiteralPath $resultFile -PathType Leaf) {
            Get-Content -LiteralPath $resultFile | ForEach-Object { Write-Host $_ }
            Remove-Item -LiteralPath $resultFile -Force
        }
        if ($script:Failures.Count -gt 0) {
            exit 1
        }
        exit $elevatedProcess.ExitCode
    }
    catch {
        Write-Error "Could not start the elevated reset: $($_.Exception.Message)"
        exit 1
    }
}

Write-Step 'Discovering settings, custom storage, installations, and uninstallers.'
$stateLocations = Read-YaguStateLocations
$uninstallRecords = @(Get-YaguUninstallRecords)
$installLocations = @(Get-YaguInstallLocations -Records $uninstallRecords)

Write-Step 'Stopping Yagu and its worker processes.'
Stop-YaguProcesses

Write-Step "Running $($uninstallRecords.Count) unique registered uninstall source(s)."
Invoke-YaguUninstallers -Records $uninstallRecords
Stop-YaguProcesses

Write-Step 'Removing install directories and integration residue.'
$cleanupInstallLocations = @($installLocations | Where-Object {
    -not $script:FailedInstallLocations.Contains($_)
})
foreach ($installLocation in $cleanupInstallLocations) {
    Remove-YaguDirectory -Path $installLocation -InstallDirectory
}
Remove-StaleUninstallRegistrations
Remove-YaguPathEntries -InstallLocations $cleanupInstallLocations
Remove-YaguRegistryIntegrations
Remove-YaguShortcuts

Write-Step 'Removing settings, indexes, caches, and temporary data.'
foreach ($customIndexRoot in $stateLocations.CustomIndexRoots) {
    Remove-CustomIndexData $customIndexRoot
}
Remove-YaguTempData $stateLocations.CustomTempDirectory
foreach ($dataDirectory in @(
    (Join-Path $env:APPDATA 'Yagu'),
    (Join-Path $env:LOCALAPPDATA 'Yagu'),
    (Join-Path $env:USERPROFILE '.Yagu'),
    (Join-Path $env:LOCALAPPDATA 'VirtualStore\Program Files\Yagu')
)) {
    Remove-YaguDirectory $dataDirectory
}

if (-not $WhatIfPreference) {
    Write-Step 'Verifying the reset.'
    Verify-Reset -InstallLocations $installLocations `
        -CustomIndexRoots $stateLocations.CustomIndexRoots `
        -CustomTempDirectory $stateLocations.CustomTempDirectory
}

if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Reset incomplete: $($script:Failures.Count) failure(s)." -ForegroundColor Red
    foreach ($failure in $script:Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    if ($Elevated -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        @(
            "Reset incomplete: $($script:Failures.Count) failure(s)."
            $script:Failures | ForEach-Object { " - $_" }
        ) | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    }
    exit 1
}

if ($WhatIfPreference) {
    Write-Step 'Preview complete; no changes were made.'
} else {
    Write-Step 'Reset complete. Yagu was not relaunched.'
}
if ($Elevated -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
    'Reset complete. Yagu was not relaunched.' | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}
exit 0
