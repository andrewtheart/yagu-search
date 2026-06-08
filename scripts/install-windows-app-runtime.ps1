[CmdletBinding()]
param(
    [string]$RuntimeDir = (Join-Path $PSScriptRoot 'win10-x64')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-MsixPackageIdentity {
    param([string]$MsixPath)

    $zip = [System.IO.Compression.ZipFile]::OpenRead($MsixPath)
    $reader = $null
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) {
            throw "AppxManifest.xml was not found in $MsixPath"
        }

        $reader = [System.IO.StreamReader]::new($entry.Open())
        [xml]$manifest = $reader.ReadToEnd()
        [pscustomobject]@{
            Name = [string]$manifest.Package.Identity.Name
            Version = [version]$manifest.Package.Identity.Version
        }
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $zip.Dispose()
    }
}

function Test-AppxPackageInstalled {
    param(
        [string]$Name,
        [version]$MinimumVersion
    )

    $installed = Get-AppxPackage -Name $Name -PackageTypeFilter Main,Framework -ErrorAction SilentlyContinue |
        Where-Object { [version]$_.Version -ge $MinimumVersion } |
        Select-Object -First 1

    return $null -ne $installed
}

if (-not (Test-Path -LiteralPath $RuntimeDir)) {
    throw "Windows App Runtime payload directory not found: $RuntimeDir"
}

$installOrder = @(
    'Microsoft.WindowsAppRuntime.1.8.msix',
    'Microsoft.WindowsAppRuntime.Main.1.8.msix',
    'Microsoft.WindowsAppRuntime.Singleton.1.8.msix',
    'Microsoft.WindowsAppRuntime.DDLM.1.8.msix'
)

foreach ($fileName in $installOrder) {
    $msixPath = Join-Path $RuntimeDir $fileName
    if (-not (Test-Path -LiteralPath $msixPath)) {
        throw "Windows App Runtime package missing from installer payload: $msixPath"
    }

    $identity = Get-MsixPackageIdentity $msixPath
    if (Test-AppxPackageInstalled -Name $identity.Name -MinimumVersion $identity.Version) {
        Write-Host "Windows App Runtime package already installed: $($identity.Name) $($identity.Version)"
        continue
    }

    Write-Host "Installing Windows App Runtime package: $fileName"
    Add-AppxPackage -Path $msixPath -ErrorAction Stop
}

Write-Host 'Windows App Runtime 1.8 prerequisite is installed.'