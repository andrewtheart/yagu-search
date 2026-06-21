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
#>
[CmdletBinding()]
param(
  [ValidateSet('x64', 'x86', 'arm64', 'all')]
  [string]$Architecture = 'all',
  [string]$InnoSetupPath,
  [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'Yagu\Yagu.csproj'
$projectDir = Join-Path $repoRoot 'Yagu'
$installerDir = Join-Path $repoRoot 'installer'
$stagingDir = Join-Path $installerDir 'staging'
$outputDir = Join-Path $installerDir 'output'
$issFile = Join-Path $installerDir 'yagu-installer.iss'
$prereqHelper = Join-Path $repoRoot 'scripts\windows-app-runtime-prereq.ps1'
if (-not (Test-Path -LiteralPath $prereqHelper)) {
  throw "Windows App Runtime prerequisite helper not found: $prereqHelper"
}
. $prereqHelper

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
Write-Host "Architectures: $($architectures -join ', ')"

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

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
    & dotnet publish $projectPath -c Release -r $rid -p:Platform=$arch --self-contained -p:BuildInstallerOnPublish=false --nologo
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

  $version = Get-YaguBuildVersion
  Write-Host "Installer app version: $version"
  Write-Host "Staged $(( Get-ChildItem -LiteralPath $stagingDir -File -Recurse ).Count) files."

  # Step 3: Compile installer for this architecture
  Write-Host "Compiling installer ($arch)..."
  & $InnoSetupPath /DMyAppVersion=$version /DYaguArch=$arch "/DStagingDir=$stagingDir" $issFile
  if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation ($arch) failed with exit code $LASTEXITCODE."
  }

  $installerExe = Join-Path $outputDir "YaguSetup-$version-$arch.exe"
  if (Test-Path -LiteralPath $installerExe) {
    $rootInstallerExe = Join-Path $installerDir (Split-Path -Leaf $installerExe)
    # Keep only the newest installer for THIS architecture in the repo installer\ folder.
    Get-ChildItem -LiteralPath $installerDir -Filter "YaguSetup-*-$arch.exe" -File |
      Where-Object { $_.FullName -ne $rootInstallerExe } |
      Remove-Item -Force

    Copy-Item -LiteralPath $installerExe -Destination $rootInstallerExe -Force

    Write-Host ""
    Write-Host "Installer created: $installerExe"
    Write-Host "Latest $arch installer copied to: $rootInstallerExe"
    Write-Host "File size: $([math]::Round((Get-Item $installerExe).Length / 1MB, 2)) MB"
  } else {
    Write-Warning "Expected installer not found at $installerExe - check Inno Setup output above."
  }
}
