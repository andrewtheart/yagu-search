# WebView2 Evergreen Runtime bootstrapper prerequisite for the Yagu installer.
#
# The embedded terminal (and only the terminal) renders with WebView2 + xterm.js. WebView2 needs
# the Microsoft Edge WebView2 Runtime, a system component that Windows 11 normally ships but a clean
# image (e.g. Windows Sandbox) does not. We stage the tiny (~2 MB) Evergreen *bootstrapper*
# (MicrosoftEdgeWebView2Setup.exe); the Inno [Code] step runs it silently at install (skipping when
# the runtime is already present). This is BEST-EFFORT: WebView2 is optional, so a failed download
# here must NOT fail the build (the app shows an in-terminal "install WebView2" message as a fallback).

$script:WebView2BootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
$script:WebView2BootstrapperName = 'MicrosoftEdgeWebView2Setup.exe'

# The x64 Evergreen *Standalone* Installer (~150 MB) installs the WebView2 Runtime with NO internet
# (unlike the bootstrapper above, which only downloads it online). The OFFLINE edition bundles this so
# the embedded terminal works on air-gapped machines. URL is Microsoft's stable "Accept and Download"
# fwlink for the x64 standalone from https://developer.microsoft.com/microsoft-edge/webview2/.
$script:WebView2StandaloneUrl = 'https://go.microsoft.com/fwlink/?linkid=2124701'
$script:WebView2StandaloneName = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'

function Copy-YaguWebView2Prerequisite {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    try {
        $cacheDir = Join-Path $RepoRoot 'installer\prerequisites'
        $cachedBootstrapper = Join-Path $cacheDir $script:WebView2BootstrapperName

        if (-not (Test-Path -LiteralPath $cachedBootstrapper)) {
            New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
            Write-Host "  Downloading WebView2 Evergreen bootstrapper..."
            $previousProgress = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'
            try {
                Invoke-WebRequest -Uri $script:WebView2BootstrapperUrl -OutFile $cachedBootstrapper -UseBasicParsing
            }
            finally {
                $ProgressPreference = $previousProgress
            }
        }

        if (-not (Test-Path -LiteralPath $cachedBootstrapper) -or (Get-Item -LiteralPath $cachedBootstrapper).Length -lt 100000) {
            Write-Warning "WebView2 bootstrapper unavailable; skipping (the app will prompt to install WebView2 if the runtime is missing)."
            return
        }

        $destDir = Join-Path $DestinationRoot 'Prerequisites\WebView2'
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        Copy-Item -LiteralPath $cachedBootstrapper -Destination (Join-Path $destDir $script:WebView2BootstrapperName) -Force

        Write-Host "  Included Microsoft Edge WebView2 Runtime bootstrapper prerequisite"
    }
    catch {
        Write-Warning "Failed to stage WebView2 bootstrapper prerequisite: $($_.Exception.Message). Continuing without it."
    }
}

function Copy-YaguWebView2StandalonePrerequisite {
    # OFFLINE edition only: stage the FULL x64 Evergreen Standalone Installer (~150 MB), which installs
    # the WebView2 Runtime with NO internet. REQUIRED for this edition -- throws on failure so a broken
    # offline build is caught rather than silently shipping an installer whose terminal can't work
    # offline. The Inno [Code] prefers this standalone over the bootstrapper when present.
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $cacheDir = Join-Path $RepoRoot 'installer\prerequisites'
    $cachedStandalone = Join-Path $cacheDir $script:WebView2StandaloneName

    if (-not (Test-Path -LiteralPath $cachedStandalone)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
        Write-Host "  Downloading WebView2 Evergreen Standalone Installer (x64, ~150 MB)..."
        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $script:WebView2StandaloneUrl -OutFile $cachedStandalone -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previousProgress
        }
    }

    if (-not (Test-Path -LiteralPath $cachedStandalone) -or (Get-Item -LiteralPath $cachedStandalone).Length -lt 50000000) {
        throw "WebView2 standalone installer unavailable or too small at $cachedStandalone. The offline edition requires the full offline WebView2 runtime installer."
    }

    $destDir = Join-Path $DestinationRoot 'Prerequisites\WebView2'
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item -LiteralPath $cachedStandalone -Destination (Join-Path $destDir $script:WebView2StandaloneName) -Force

    Write-Host "  Bundled WebView2 Evergreen Standalone Installer for the offline edition (installs with no internet)"
}
