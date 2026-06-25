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
