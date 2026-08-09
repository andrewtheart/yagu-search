<#
.SYNOPSIS
  Builds Yagu (self-contained Native AOT) and compiles the per-architecture
  Inno Setup installer EXEs.

.DESCRIPTION
  For each requested architecture (x64, x86, arm64):
    1. Publishes the Yagu project self-contained for win-<arch> (unless -SkipBuild).
    2. Copies the publish output and the Windows App Runtime prerequisite into a
       staging directory.
    3. Invokes ISCC.exe with /DYaguArch=<arch> to produce YaguSetup-<version>-<arch>.exe.
    4. Copies the newest installer for that architecture into the repo installer\ folder.

.PARAMETER Architecture
  Which architecture(s) to build: x64, x86, arm64, or all (default).

.PARAMETER InnoSetupPath
  Path to ISCC.exe. Defaults to the standard Inno Setup 6 install location.

.PARAMETER SkipBuild
  Skip the dotnet publish step (package existing publish output). Used by the
  csproj AfterPublish hook, which already published a single architecture.

.PARAMETER IncludeOcr
  Build the OCR-bundled ("offline") edition: stages the native PaddleOCR runtime +
  PP-OCR models AND the Tesseract English data into <app>\ocr-payload so image-text
  search needs no download on first use. This edition defaults to the Tesseract OCR
  engine (which runs entirely from the bundled payload). The native runtime is win-x64
  only, so this edition is forced to x64 and the installer is named
  YaguSetup-<version>-x64-offline.exe.

.PARAMETER OcrPayloadCacheDir
  Local OCR cache to source the bundled payload from. Defaults to
  %LOCALAPPDATA%\Yagu\ocr-runtime. Missing assets are downloaded by running the
  staged worker.

.PARAMETER Push
  Delegate publishing to build-all-installers.ps1, the canonical release workflow.
  The requested architecture/edition is preserved while commit review, version pinning,
  release notes, upload protection, and live verification stay identical for every release.

.PARAMETER ReleaseMode
  GitHub release publication mode used with Push: Prompt (the default), Draft, or
  Published. Prompt asks interactively after a successful push. Draft and Published
  support unattended runs.

.PARAMETER SkipRelease
  With Push, commit and push but do not create or refresh a GitHub release.

.PARAMETER CopilotPath
  Optional explicit path to the Copilot CLI used by the canonical workflow for
  comprehensive release-note generation.

.PARAMETER SignCertThumbprint
  SHA-1 thumbprint of an Authenticode code-signing certificate (typically on a
  hardware token). When supplied, every Yagu-authored binary in the staging tree AND
  the produced setup EXE are signed and verified; without it the build is unsigned
  exactly as before. Signing is all-or-nothing on purpose: Yagu refuses to launch a
  worker or install an update whose publisher does not match the running build.

.PARAMETER SignTimestampUrl
  RFC 3161 timestamp server used when signing. Defaults to DigiCert's.

.PARAMETER SignToolPath
  Optional explicit path to signtool.exe. Defaults to PATH, then the newest Windows
  SDK signing tools.
#>
[CmdletBinding()]
param(
  [ValidateSet('x64', 'x86', 'arm64', 'all')]
  [string]$Architecture = 'all',
  [string]$InnoSetupPath,
  [switch]$SkipBuild,
  [switch]$IncludeOcr,
  [switch]$SkipVersionIncrement,
  [string]$OcrPayloadCacheDir = (Join-Path $env:LOCALAPPDATA 'Yagu\ocr-runtime'),
  [switch]$Push,
  [switch]$SkipRelease,
  [string]$CopilotPath,
  [ValidateSet('Prompt', 'Draft', 'Published')]
  [string]$ReleaseMode = 'Prompt',
  [string]$SignCertThumbprint,
  [string]$SignTimestampUrl = 'http://timestamp.digicert.com',
  [string]$SignToolPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
if ($Push) {
  $canonicalReleaseScript = Join-Path $repoRoot 'build-all-installers.ps1'
  if (-not (Test-Path -LiteralPath $canonicalReleaseScript)) {
    throw "Canonical release script not found: $canonicalReleaseScript"
  }

  [string[]]$variants = if ($IncludeOcr) {
    @('x64-offline')
  }
  elseif ($Architecture -eq 'all') {
    @('x64', 'x86', 'arm64')
  }
  else {
    @($Architecture)
  }

  $releaseParams = @{
    Variant = $variants
    Push = $true
    ReleaseMode = $ReleaseMode
  }
  if ($SkipBuild) { $releaseParams['SkipBuild'] = $true }
  if ($SkipVersionIncrement) { $releaseParams['KeepVersion'] = $true }
  if ($SkipRelease) { $releaseParams['SkipRelease'] = $true }
  if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) { $releaseParams['InnoSetupPath'] = $InnoSetupPath }
  if (-not [string]::IsNullOrWhiteSpace($OcrPayloadCacheDir)) { $releaseParams['OcrPayloadCacheDir'] = $OcrPayloadCacheDir }
  if (-not [string]::IsNullOrWhiteSpace($CopilotPath)) { $releaseParams['CopilotPath'] = $CopilotPath }
  if (-not [string]::IsNullOrWhiteSpace($SignCertThumbprint)) {
    $releaseParams['SignCertThumbprint'] = $SignCertThumbprint
    $releaseParams['SignTimestampUrl'] = $SignTimestampUrl
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) { $releaseParams['SignToolPath'] = $SignToolPath }
  }

  & $canonicalReleaseScript @releaseParams
  return
}
$projectPath = Join-Path $repoRoot 'src\Yagu\Yagu.csproj'
$projectDir = Join-Path $repoRoot 'src\Yagu'
$installerDir = Join-Path $repoRoot 'installer'
$stagingDir = Join-Path $installerDir 'staging'
$outputDir = Join-Path $installerDir 'output'
$issFile = Join-Path $installerDir 'yagu-installer.iss'
$prereqHelper = Join-Path $repoRoot 'scripts\windows-app-runtime-prereq.ps1'
if (-not (Test-Path -LiteralPath $prereqHelper)) {
  throw "Windows App Runtime prerequisite helper not found: $prereqHelper"
}
. $prereqHelper

$webView2PrereqHelper = Join-Path $repoRoot 'scripts\webview2-prereq.ps1'
if (Test-Path -LiteralPath $webView2PrereqHelper) {
  . $webView2PrereqHelper
}
$everythingPrereqHelper = Join-Path $repoRoot 'scripts\everything-prereq.ps1'
if (Test-Path -LiteralPath $everythingPrereqHelper) {
  . $everythingPrereqHelper
}

# Opt-in Authenticode signing. Resolved BEFORE any build work so a missing token,
# expired certificate, or missing signtool fails in seconds instead of after a
# multi-minute Native AOT publish.
$signBuild = -not [string]::IsNullOrWhiteSpace($SignCertThumbprint)
$signCert = $null
if ($signBuild) {
  $codeSigningHelper = Join-Path $repoRoot 'scripts\code-signing.ps1'
  if (-not (Test-Path -LiteralPath $codeSigningHelper)) {
    throw "Code-signing helper not found: $codeSigningHelper"
  }
  . $codeSigningHelper
  $signCert = Resolve-YaguSigningCertificate -Thumbprint $SignCertThumbprint
  $SignCertThumbprint = $signCert.Thumbprint
  $SignToolPath = Resolve-YaguSignTool -RequestedPath $SignToolPath
  Write-Host "Code signing ENABLED"
  Write-Host "  Certificate: $($signCert.Subject) (expires $($signCert.NotAfter.ToString('yyyy-MM-dd')))"
  Write-Host "  signtool:    $SignToolPath"
  Write-Host "  Timestamp:   $SignTimestampUrl"
}
# Read version from build-version.txt
$versionFile = Join-Path $projectDir 'Properties\build-version.txt'
function Get-YaguBuildVersion {
  if (Test-Path -LiteralPath $versionFile) {
    return (Get-Content -LiteralPath $versionFile -Raw).Trim()
  }

  return '1.0.0'
}
$version = Get-YaguBuildVersion

# Locate ISCC.exe
if ([string]::IsNullOrWhiteSpace($InnoSetupPath)) {
  $candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
  )
  foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) {
      $InnoSetupPath = $c
      break
    }
  }
}
if ([string]::IsNullOrWhiteSpace($InnoSetupPath) -or -not (Test-Path -LiteralPath $InnoSetupPath)) {
  throw "Could not find ISCC.exe. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or pass -InnoSetupPath."
}

Write-Host "Using Inno Setup: $InnoSetupPath"
Write-Host "Starting app version: $version"

# Parse target framework from csproj
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)
$targetFramework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)[0]

# Resolve the list of architectures to build.
if ($Architecture -eq 'all') {
  $architectures = @('x64', 'x86', 'arm64')
} else {
  $architectures = @($Architecture)
}

# The OCR-bundled edition ships the win-x64-only native runtime, so it is x64-only.
if ($IncludeOcr) {
  $nonX64 = @($architectures | Where-Object { $_ -ne 'x64' })
  if ($nonX64.Count -gt 0) {
    Write-Warning "-IncludeOcr bundles the win-x64-only OCR runtime; ignoring requested architecture(s): $($nonX64 -join ', '). Building x64 only."
  }
  $architectures = @('x64')
}

Write-Host "Architectures: $($architectures -join ', ')"
if ($IncludeOcr) {
  Write-Host "Edition: offline (OCR runtime + models bundled; Tesseract is the default engine)"
  $stageOcrHelper = Join-Path $repoRoot 'scripts\stage-ocr-payload.ps1'
  if (-not (Test-Path -LiteralPath $stageOcrHelper)) {
    throw "OCR payload staging helper not found: $stageOcrHelper"
  }
  # The offline edition also bundles the voidtools Everything setup (run after in-app consent).
  if (-not (Get-Command -Name Copy-YaguEverythingPrerequisite -ErrorAction SilentlyContinue)) {
    throw "Everything prerequisite helper not found or not loaded: $everythingPrereqHelper"
  }
  # ...and the FULL WebView2 standalone installer so the terminal's runtime installs with no internet.
  if (-not (Get-Command -Name Copy-YaguWebView2StandalonePrerequisite -ErrorAction SilentlyContinue)) {
    throw "WebView2 standalone prerequisite helper not found or not loaded: $webView2PrereqHelper"
  }
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$builtInstallers = New-Object System.Collections.Generic.List[string]

foreach ($arch in $architectures) {
  $rid = "win-$arch"
  Write-Host ""
  Write-Host "=== Building installer for $arch ($rid) ==="

  # Step 1: Publish (self-contained Native AOT) for this architecture.
  # Passing -p:Platform=$arch makes MSBuild emit output under a platform-specific
  # folder (bin\<arch>\Release\...) rather than the default bin\Release\... path.
  $publishDir = Join-Path $projectDir "bin\$arch\Release\$targetFramework\$rid\publish"
  if (-not $SkipBuild) {
    Write-Host "Publishing Yagu (Release, $rid, self-contained Native AOT)..."
    # -p:SkipYaguVersionIncrement=true (when requested) keeps build-version.txt fixed so a
    # multi-variant release can share ONE version instead of the publish bumping it per arch.
    $publishArgs = @($projectPath, '-c', 'Release', '-r', $rid, "-p:Platform=$arch", '--self-contained', '-p:BuildInstallerOnPublish=false', '--nologo')
    if ($SkipVersionIncrement) { $publishArgs += '-p:SkipYaguVersionIncrement=true' }
    & dotnet publish @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($rid) failed." }
  } else {
    Write-Host "Skipping build (using existing publish output for $rid)."
  }

  if (-not (Test-Path -LiteralPath $publishDir)) {
    throw "Publish output not found at: $publishDir"
  }

  # Step 2: Stage files
  Write-Host "Staging files to $stagingDir..."
  if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
  }
  New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

  # Copy all publish output (self-contained app + native deps)
  Copy-Item -Path "$publishDir\*" -Destination $stagingDir -Recurse -Force

  Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $repoRoot -DestinationRoot $stagingDir

  # Best-effort: stage the WebView2 Evergreen bootstrapper for the embedded terminal. Never throws.
  if (Get-Command -Name Copy-YaguWebView2Prerequisite -ErrorAction SilentlyContinue) {
    Copy-YaguWebView2Prerequisite -RepoRoot $repoRoot -DestinationRoot $stagingDir
  }

  # Step 2b: For the OCR-bundled edition, assemble the offline OCR payload into
  # <staging>\ocr-payload (the existing [Files] recursesubdirs entry ships it as <app>\ocr-payload).
  if ($IncludeOcr) {
    $ocrPayloadDir = Join-Path $stagingDir 'ocr-payload'
    $stagedWorker = Join-Path $stagingDir 'ocr-worker\Yagu.OcrWorker.exe'
    Write-Host "Staging OCR payload..."
    & $stageOcrHelper -OutputDir $ocrPayloadDir -WorkerExe $stagedWorker -CacheDir $OcrPayloadCacheDir -RequireTesseract
    if ($LASTEXITCODE -ne 0) { throw "OCR payload staging failed." }

    # Bundle the voidtools Everything setup so the offline edition can install Everything (after the
    # in-app consent prompt) with no download. Required for this edition — throws on failure. The
    # <staging>\* [Files] entry in the ISS ships <staging>\everything-setup as <app>\everything-setup.
    Write-Host "Staging bundled Everything setup..."
    Copy-YaguEverythingPrerequisite -RepoRoot $repoRoot -DestinationRoot $stagingDir

    # Bundle the FULL WebView2 Evergreen Standalone Installer (~194 MB) so the embedded terminal's
    # runtime installs with NO internet. The lite bootstrapper staged above only downloads it online;
    # the Inno [Code] prefers this standalone when present. Required for this edition — throws on failure.
    Write-Host "Staging WebView2 offline standalone installer..."
    Copy-YaguWebView2StandalonePrerequisite -RepoRoot $repoRoot -DestinationRoot $stagingDir
  }

  $version = Get-YaguBuildVersion
  Write-Host "Installer app version: $version"
  Write-Host "Staged $(( Get-ChildItem -LiteralPath $stagingDir -File -Recurse ).Count) files."

  # Step 2c: Sign every Yagu-authored staged binary (app, Rust core, all three workers)
  # BEFORE Inno compresses them into the setup EXE. Third-party payloads keep their
  # vendor signatures.
  if ($signBuild) {
    $signable = Get-YaguSignableStagedFile -StagingDir $stagingDir
    if ($signable.Count -eq 0) {
      throw "Code signing was requested but no Yagu binaries were found under $stagingDir."
    }
    Invoke-YaguAuthenticodeSign -Path $signable -CertThumbprint $SignCertThumbprint `
      -TimestampUrl $SignTimestampUrl -SignToolPath $SignToolPath -Description "Yagu $version"
  }

  # Step 3: Compile installer for this architecture
  Write-Host "Compiling installer ($arch)..."
  $isccArgs = @("/DMyAppVersion=$version", "/DYaguArch=$arch", "/DStagingDir=$stagingDir")
  if ($IncludeOcr) { $isccArgs += '/DIncludeOcr=1' }
  # Tells the [Code] section this build is signed, so it does not abort under Smart App Control.
  if ($signBuild) { $isccArgs += '/DYaguSigned=1' }
  & $InnoSetupPath @isccArgs $issFile
  if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation ($arch) failed with exit code $LASTEXITCODE."
  }

  $ocrSuffix = if ($IncludeOcr) { '-offline' } else { '' }
  $installerExe = Join-Path $outputDir "YaguSetup-$version-$arch$ocrSuffix.exe"
  if (Test-Path -LiteralPath $installerExe) {
    # Sign the setup EXE itself: Yagu's in-app updater rejects a downloaded installer
    # whose publisher does not match the running build.
    if ($signBuild) {
      Invoke-YaguAuthenticodeSign -Path @($installerExe) -CertThumbprint $SignCertThumbprint `
        -TimestampUrl $SignTimestampUrl -SignToolPath $SignToolPath -Description "Yagu $version Setup"
    }

    $rootInstallerExe = Join-Path $installerDir (Split-Path -Leaf $installerExe)
    # Keep only the newest installer for THIS architecture + edition in the repo installer\ folder.
    Get-ChildItem -LiteralPath $installerDir -Filter "YaguSetup-*-$arch$ocrSuffix.exe" -File |
      Where-Object { $_.FullName -ne $rootInstallerExe } |
      Remove-Item -Force

    Copy-Item -LiteralPath $installerExe -Destination $rootInstallerExe -Force
    $builtInstallers.Add($rootInstallerExe)

    Write-Host ""
    Write-Host "Installer created: $installerExe"
    Write-Host "Latest $arch installer copied to: $rootInstallerExe"
    Write-Host "File size: $([math]::Round((Get-Item $installerExe).Length / 1MB, 2)) MB"
  } else {
    Write-Warning "Expected installer not found at $installerExe - check Inno Setup output above."
  }
}
