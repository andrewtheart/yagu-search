# voidtools Everything setup prerequisite for the Yagu OFFLINE installer edition.
#
# Yagu offers to install Everything (for very fast file discovery) on first launch, ALWAYS behind an
# explicit consent prompt. The lite editions download the voidtools setup on demand; the offline
# edition instead bundles that setup beside the app (<app>\everything-setup\) so it can be run with
# no internet connection. The Yagu app resolves the bundled path via
# Yagu.Services.EverythingAssetPaths and runs it (after consent + Authenticode verification) instead
# of downloading.
#
# The Everything license (https://www.voidtools.com/License.txt) is an MIT-style license that
# permits redistribution provided the copyright + permission notice is included; that notice is
# staged alongside the setup as Everything-License.txt.
#
# This is REQUIRED for the offline edition: a failure here throws so a broken build is caught rather
# than silently shipping an "offline" installer that still needs a download.

# Keep this version in sync with Yagu.Services.EverythingAssetPaths.Version (source-pinned by
# EverythingBundlingRegressionTests).
$script:EverythingVersion = '1.4.1.1032'
$script:EverythingSetupName = "Everything-$($script:EverythingVersion).x64-Setup.exe"
$script:EverythingSetupUrl = "https://www.voidtools.com/$($script:EverythingSetupName)"

function Copy-YaguEverythingPrerequisite {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $cacheDir = Join-Path $RepoRoot 'installer\prerequisites'
    $cachedSetup = Join-Path $cacheDir $script:EverythingSetupName

    if (-not (Test-Path -LiteralPath $cachedSetup)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
        Write-Host "  Downloading voidtools Everything setup ($($script:EverythingSetupName))..."
        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $script:EverythingSetupUrl -OutFile $cachedSetup -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previousProgress
        }
    }

    if (-not (Test-Path -LiteralPath $cachedSetup) -or (Get-Item -LiteralPath $cachedSetup).Length -lt 500000) {
        throw "Everything setup unavailable or too small at $cachedSetup. The offline edition requires the bundled voidtools installer."
    }

    $destDir = Join-Path $DestinationRoot 'everything-setup'
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item -LiteralPath $cachedSetup -Destination (Join-Path $destDir $script:EverythingSetupName) -Force

    # Redistribution notice required by the Everything MIT-style license.
    $licenseSource = Join-Path $RepoRoot 'installer\Everything-License.txt'
    if (Test-Path -LiteralPath $licenseSource) {
        Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $destDir 'Everything-License.txt') -Force
    } else {
        throw "Everything redistribution notice not found: $licenseSource"
    }

    Write-Host "  Bundled voidtools Everything setup for the offline edition (everything-setup\$($script:EverythingSetupName))"
}
