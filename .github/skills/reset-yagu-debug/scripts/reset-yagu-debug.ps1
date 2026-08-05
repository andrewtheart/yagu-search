[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CommandContext = $PSCmdlet
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:ProcessInspectionFailed = $false
$script:YaguProcessNames = @(
    'Yagu.exe',
    'Yagu.IndexWorker.exe',
    'Yagu.OcrWorker.exe',
    'Yagu.SemanticWorker.exe'
)

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[reset-yagu-debug] $Message"
}

function Add-Failure {
    param([Parameter(Mandatory)][string]$Message)
    $script:Failures.Add($Message)
    Write-Error $Message -ErrorAction Continue
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

function Test-PathBelow {
    param(
        [AllowNull()][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $normalizedPath = Get-NormalizedPath $Path
    $normalizedRoot = Get-NormalizedPath $Root
    if ($null -eq $normalizedPath -or $null -eq $normalizedRoot) {
        return $false
    }
    return $normalizedPath.StartsWith(
        $normalizedRoot.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)
}

function Test-SafeYaguDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
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
        [switch]$UpdateDirectory
    )

    $normalized = Get-NormalizedPath $Path
    if ($null -eq $normalized -or -not (Test-Path -LiteralPath $normalized -PathType Container)) {
        return
    }
    if (-not (Test-SafeYaguDirectory -Path $normalized -UpdateDirectory:$UpdateDirectory)) {
        Add-Failure "Refused to recursively remove unsafe directory '$normalized'."
        return
    }

    try {
        Invoke-ResetAction -Target $normalized -Action 'Remove Yagu-owned runtime directory' -Operation {
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
        Invoke-ResetAction -Target $normalized -Action 'Remove Yagu-owned runtime file' -Operation {
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

function Get-DebugProcesses {
    param([Parameter(Mandatory)][string[]]$DebugRoots)

    $processes = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($process in @(Get-CimInstance Win32_Process | Where-Object {
            $_.Name -in $script:YaguProcessNames
        })) {
            $isDebug = $false
            foreach ($debugRoot in $DebugRoots) {
                if (Test-PathBelow -Path $process.ExecutablePath -Root $debugRoot) {
                    $isDebug = $true
                    break
                }
            }
            if ($isDebug) {
                $processes.Add($process)
            }
        }
    }
    catch {
        $script:ProcessInspectionFailed = $true
        Add-Failure "Could not inspect Yagu executable paths: $($_.Exception.Message)"
    }
    return @($processes)
}

function Get-NonDebugYaguProcesses {
    param([Parameter(Mandatory)][string[]]$DebugRoots)

    $processes = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($process in @(Get-CimInstance Win32_Process | Where-Object {
            $_.Name -in $script:YaguProcessNames
        })) {
            $isDebug = $false
            foreach ($debugRoot in $DebugRoots) {
                if (Test-PathBelow -Path $process.ExecutablePath -Root $debugRoot) {
                    $isDebug = $true
                    break
                }
            }
            if (-not $isDebug) {
                $processes.Add($process)
            }
        }
    }
    catch {
        $script:ProcessInspectionFailed = $true
        Add-Failure "Could not inspect non-Debug Yagu processes: $($_.Exception.Message)"
    }
    return @($processes)
}

function Stop-DebugYaguProcesses {
    param([Parameter(Mandatory)][string[]]$DebugRoots)

    for ($pass = 0; $pass -lt 3; $pass++) {
        $processes = @(Get-DebugProcesses -DebugRoots $DebugRoots)
        if ($processes.Count -eq 0) {
            return
        }

        foreach ($process in $processes) {
            try {
                $target = "$($process.Name) (PID $($process.ProcessId)) at '$($process.ExecutablePath)'"
                Invoke-ResetAction -Target $target -Action 'Stop repository Debug Yagu process by PID' -Operation {
                    Stop-Process -Id $process.ProcessId -Force
                }
            }
            catch {
                if (Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) {
                    Add-Failure "Failed to stop $target`: $($_.Exception.Message)"
                }
            }
        }
        if ($WhatIfPreference) {
            return
        }
        Start-Sleep -Milliseconds 500
    }

    foreach ($process in @(Get-DebugProcesses -DebugRoots $DebugRoots)) {
        Add-Failure "Debug process '$($process.Name)' (PID $($process.ProcessId)) is still running."
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

function Remove-PreservedIndexLocator {
    try {
        $key = Get-Item -LiteralPath 'HKCU:\Software\Yagu' -ErrorAction SilentlyContinue
        if ($null -ne $key -and $null -ne $key.GetValue('PreservedIndexStorageDirectory', $null)) {
            Invoke-ResetAction -Target 'HKCU:\Software\Yagu\PreservedIndexStorageDirectory' -Action 'Remove stale Yagu index locator' -Operation {
                Remove-ItemProperty -LiteralPath 'HKCU:\Software\Yagu' -Name 'PreservedIndexStorageDirectory'
            }
        }
    }
    catch {
        Add-Failure "Failed to remove the preserved index locator: $($_.Exception.Message)"
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

function Verify-DebugReset {
    param(
        [Parameter(Mandatory)][string[]]$DebugRoots,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$CustomIndexRoots,
        [AllowNull()][string]$CustomTempDirectory
    )

    foreach ($process in @(Get-DebugProcesses -DebugRoots $DebugRoots)) {
        Add-Failure "Verification found Debug process '$($process.Name)' (PID $($process.ProcessId))."
    }

    foreach ($path in @(
        (Join-Path $env:APPDATA 'Yagu'),
        (Join-Path $env:LOCALAPPDATA 'Yagu'),
        (Join-Path $env:USERPROFILE '.Yagu')
    )) {
        if (Test-Path -LiteralPath $path) {
            Add-Failure "Verification found Yagu runtime data '$path'."
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
}

$skillDirectory = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$repoRoot = Get-NormalizedPath (Join-Path $skillDirectory '..\..\..')
if ($null -eq $repoRoot -or -not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
    Write-Error 'Could not resolve the Yagu repository root from the skill path.'
    exit 1
}

$debugRoots = @(
    (Join-Path $repoRoot 'src\Yagu\bin\Debug'),
    (Join-Path $repoRoot 'src\Yagu.IndexWorker\bin\Debug'),
    (Join-Path $repoRoot 'src\Yagu.OcrWorker\bin\Debug'),
    (Join-Path $repoRoot 'src\Yagu.SemanticWorker\bin\Debug')
)

Write-Step 'Discovering Debug processes and shared runtime storage.'
$stateLocations = Read-YaguStateLocations

$blockingProcesses = @(Get-NonDebugYaguProcesses -DebugRoots $debugRoots)
if ($script:ProcessInspectionFailed -or $blockingProcesses.Count -gt 0) {
    foreach ($process in $blockingProcesses) {
        $path = if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            '<path unavailable>'
        } else {
            $process.ExecutablePath
        }
        Add-Failure "A non-Debug Yagu process is using shared state: '$($process.Name)' (PID $($process.ProcessId), $path). Stop or reset the installed copy first."
    }
    Write-Host ''
    Write-Host "Debug reset aborted: $($script:Failures.Count) failure(s)." -ForegroundColor Red
    foreach ($failure in $script:Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Step 'Stopping only Yagu processes below repository Debug output directories.'
Stop-DebugYaguProcesses -DebugRoots $debugRoots
if ($script:ProcessInspectionFailed) {
    Write-Host ''
    Write-Host "Debug reset aborted: $($script:Failures.Count) failure(s)." -ForegroundColor Red
    foreach ($failure in $script:Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Step 'Removing shared settings, indexes, caches, and temporary data.'
foreach ($customIndexRoot in $stateLocations.CustomIndexRoots) {
    Remove-CustomIndexData $customIndexRoot
}
Remove-YaguTempData $stateLocations.CustomTempDirectory
Remove-PreservedIndexLocator
foreach ($dataDirectory in @(
    (Join-Path $env:APPDATA 'Yagu'),
    (Join-Path $env:LOCALAPPDATA 'Yagu'),
    (Join-Path $env:USERPROFILE '.Yagu')
)) {
    Remove-YaguDirectory $dataDirectory
}

if (-not $WhatIfPreference) {
    Write-Step 'Verifying Debug runtime state without modifying installations or build outputs.'
    Verify-DebugReset -DebugRoots $debugRoots `
        -CustomIndexRoots $stateLocations.CustomIndexRoots `
        -CustomTempDirectory $stateLocations.CustomTempDirectory
}

if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Debug reset incomplete: $($script:Failures.Count) failure(s)." -ForegroundColor Red
    foreach ($failure in $script:Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

if ($WhatIfPreference) {
    Write-Step 'Preview complete; no changes were made.'
} else {
    Write-Step 'Debug reset complete. Installations and build outputs were preserved.'
}
exit 0
