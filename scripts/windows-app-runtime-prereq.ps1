function Get-YaguWindowsAppSdkVersion {
    param([xml]$ProjectXml)

    $packageRef = @($ProjectXml.Project.ItemGroup.PackageReference |
        Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' } |
        Select-Object -First 1)[0]

    if ($null -eq $packageRef -or [string]::IsNullOrWhiteSpace($packageRef.Version)) {
        throw 'Could not find Microsoft.WindowsAppSDK PackageReference version in Yagu.csproj.'
    }

    return [string]$packageRef.Version
}

function Get-YaguWindowsAppRuntimePackageRoot {
    param([xml]$ProjectXml)

    $runtimeVersion = Get-YaguWindowsAppSdkVersion -ProjectXml $ProjectXml
    $packageRootCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        $packageRootCandidates += $env:NUGET_PACKAGES
    }
    $packageRootCandidates += Join-Path $env:USERPROFILE '.nuget\packages'

    foreach ($packageRoot in $packageRootCandidates | Select-Object -Unique) {
        $candidate = Join-Path $packageRoot "microsoft.windowsappsdk.runtime\$runtimeVersion"
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "Microsoft.WindowsAppSDK.Runtime $runtimeVersion was not found in the NuGet package cache. Run dotnet restore and try again."
}

function Copy-YaguWindowsAppRuntimePrerequisite {
    param(
        [xml]$ProjectXml,
        [string]$RepoRoot,
        [string]$DestinationRoot
    )

    $runtimePackageRoot = Get-YaguWindowsAppRuntimePackageRoot -ProjectXml $ProjectXml
    $runtimeVersion = Get-YaguWindowsAppSdkVersion -ProjectXml $ProjectXml
    $majorMinor = ($runtimeVersion -split '\.' | Select-Object -First 2) -join '.'
    $sourceDir = Join-Path $runtimePackageRoot 'tools\MSIX\win10-x64'
    if (-not (Test-Path -LiteralPath $sourceDir)) {
        throw "Windows App Runtime x64 MSIX payload not found: $sourceDir"
    }

    $prereqRoot = Join-Path $DestinationRoot 'Prerequisites\WindowsAppRuntime'
    $destDir = Join-Path $prereqRoot 'win10-x64'
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null

    $files = @(
        'MSIX.inventory',
        "Microsoft.WindowsAppRuntime.$majorMinor.msix",
        "Microsoft.WindowsAppRuntime.Main.$majorMinor.msix",
        "Microsoft.WindowsAppRuntime.Singleton.$majorMinor.msix",
        "Microsoft.WindowsAppRuntime.DDLM.$majorMinor.msix"
    )

    foreach ($file in $files) {
        $source = Join-Path $sourceDir $file
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Required Windows App Runtime prerequisite file is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $destDir $file) -Force
    }

    $installScript = Join-Path $RepoRoot 'scripts\install-windows-app-runtime.ps1'
    if (-not (Test-Path -LiteralPath $installScript)) {
        throw "Windows App Runtime install script not found: $installScript"
    }
    Copy-Item -LiteralPath $installScript -Destination (Join-Path $prereqRoot 'Install-WindowsAppRuntime.ps1') -Force

    Write-Host "  Included Windows App Runtime $majorMinor x64 prerequisite"
}