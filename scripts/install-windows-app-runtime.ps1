[CmdletBinding()]
param(
    [string]$RuntimeDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrWhiteSpace($RuntimeDir)) {
    $RuntimeDir = Join-Path $PSScriptRoot 'win10-x64'
}

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

# The MSIX filename version token differs across Windows App Runtime majors (WAR 1.x uses a
# major.minor token such as "1.8"; WAR 2.x uses the major only, e.g. "2"), so discover it from the
# base runtime MSIX in the payload rather than hardcoding it.
$baseMsix = @(Get-ChildItem -LiteralPath $RuntimeDir -Filter 'Microsoft.WindowsAppRuntime.*.msix' |
    Where-Object { $_.Name -notmatch '\.(Main|Singleton|DDLM)\.' })[0]
if ($null -eq $baseMsix -or $baseMsix.Name -notmatch '^Microsoft\.WindowsAppRuntime\.(.+)\.msix$') {
    throw "No Microsoft.WindowsAppRuntime.<version>.msix payload found in $RuntimeDir"
}
$runtimeToken = $Matches[1]

$installOrder = @(
    "Microsoft.WindowsAppRuntime.$runtimeToken.msix",
    "Microsoft.WindowsAppRuntime.Main.$runtimeToken.msix",
    "Microsoft.WindowsAppRuntime.Singleton.$runtimeToken.msix",
    "Microsoft.WindowsAppRuntime.DDLM.$runtimeToken.msix"
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

Write-Host "Windows App Runtime $runtimeToken prerequisite is installed."