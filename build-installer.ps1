<#
.SYNOPSIS
  Builds Yagu and compiles the Inno Setup installer EXE.

.DESCRIPTION
  1. Builds the Yagu project in Release mode.
  2. Copies the build output and WinUI resources into a staging directory.
  3. Invokes the Inno Setup compiler (ISCC.exe) to produce the installer EXE.

.PARAMETER InnoSetupPath
  Path to ISCC.exe. Defaults to the standard Inno Setup 6 install location.

.PARAMETER SkipBuild
  Skip the dotnet build step (use existing build output).
#>
[CmdletBinding()]
param(
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
$assemblyName = @($projectXml.Project.PropertyGroup.AssemblyName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($assemblyName)) {
  $assemblyName = 'Yagu'
}

$buildOutputDir = Join-Path $projectDir "bin\Release\$targetFramework"

# Step 1: Build
if (-not $SkipBuild) {
  Write-Host "Building Yagu (Release)..."
  & dotnet build $projectPath -c Release --nologo
  if ($LASTEXITCODE -ne 0) { throw "dotnet build (Release) failed." }
} else {
  Write-Host "Skipping build (using existing output)."
}

$version = Get-YaguBuildVersion
Write-Host "Installer app version: $version"

if (-not (Test-Path -LiteralPath $buildOutputDir)) {
  throw "Build output not found at: $buildOutputDir"
}

# Step 2: Stage files
Write-Host "Staging files to $stagingDir..."
if (Test-Path -LiteralPath $stagingDir) {
  Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# Copy all build output
Copy-Item -Path "$buildOutputDir\*" -Destination $stagingDir -Recurse -Force

Copy-YaguWindowsAppRuntimePrerequisite -ProjectXml $projectXml -RepoRoot $repoRoot -DestinationRoot $stagingDir

Write-Host "Staged $(( Get-ChildItem -LiteralPath $stagingDir -File -Recurse ).Count) files."

# Step 3: Compile installer
Write-Host "Compiling installer..."
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $InnoSetupPath /DMyAppVersion=$version "/DStagingDir=$stagingDir" $issFile
if ($LASTEXITCODE -ne 0) {
  throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installerExe = Join-Path $outputDir "YaguSetup-$version.exe"
if (Test-Path -LiteralPath $installerExe) {
  $rootInstallerExe = Join-Path $installerDir (Split-Path -Leaf $installerExe)
  Get-ChildItem -LiteralPath $installerDir -Filter 'YaguSetup-*.exe' -File |
    Where-Object { $_.FullName -ne $rootInstallerExe } |
    Remove-Item -Force

  Copy-Item -LiteralPath $installerExe -Destination $rootInstallerExe -Force

  Write-Host ""
  Write-Host "Installer created: $installerExe"
  Write-Host "Latest installer copied to: $rootInstallerExe"
  Write-Host "File size: $([math]::Round((Get-Item $installerExe).Length / 1MB, 2)) MB"
} else {
  Write-Warning "Expected installer not found at $installerExe - check Inno Setup output above."
}
