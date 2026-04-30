<#
.SYNOPSIS
  Builds and installs the Yagu executable and companion files.

.DESCRIPTION
  The script name follows the requested Wagu installer name. The current project
  binary is Yagu.exe, so this installer publishes Yagu and copies the published
  output plus required WinUI XAML resources to the selected install directory.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $false)]
  [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'Yagu\Yagu.csproj'
$projectDir = Split-Path -Parent $projectPath
$contextMenuScript = Join-Path $repoRoot 'scripts\register-context-menu.ps1'
$manifestName = '.wagu-install-manifest.txt'
$exeName = 'Yagu.exe'
$installRegistryPath = 'HKCU:\Software\Yagu'
$installRegistryValueName = 'InstallDir'

$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)
$targetFramework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)[0]
$assemblyName = @($projectXml.Project.PropertyGroup.AssemblyName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($assemblyName)) {
  $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
}

function Resolve-InstallDirectory {
  param([string]$Value)

  $defaultDir = (Get-Location).Path
  if ([string]::IsNullOrWhiteSpace($Value)) {
    $answer = Read-Host "Install directory [$defaultDir]"
    if ([string]::IsNullOrWhiteSpace($answer)) {
      $Value = $defaultDir
    } else {
      $Value = $answer
    }
  }

  return [System.IO.Path]::GetFullPath($Value)
}

function Read-YesNo {
  param(
    [string]$Prompt,
    [bool]$DefaultYes = $false
  )

  $suffix = if ($DefaultYes) { '[Y/n]' } else { '[y/N]' }
  $answer = Read-Host "$Prompt $suffix"
  if ([string]::IsNullOrWhiteSpace($answer)) {
    return $DefaultYes
  }

  return $answer -match '^(y|yes)$'
}

function Get-RelativePathFromBase {
  param(
    [string]$BasePath,
    [string]$FullPath
  )

  $base = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
  $full = [System.IO.Path]::GetFullPath($FullPath)
  if ($full.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)) {
    return $full.Substring($base.Length)
  }

  return [System.IO.Path]::GetFileName($full)
}

function Copy-InstallFile {
  param(
    [string]$SourcePath,
    [string]$BasePath,
    [string]$InstallPath,
    [System.Collections.Generic.List[string]]$ManifestEntries,
    [System.Collections.Generic.HashSet[string]]$ManifestEntrySet
  )

  $relativePath = Get-RelativePathFromBase -BasePath $BasePath -FullPath $SourcePath
  $destination = Join-Path $InstallPath $relativePath
  $destinationDir = Split-Path -Parent $destination
  if (-not [string]::IsNullOrWhiteSpace($destinationDir)) {
    New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
  }

  Copy-Item -LiteralPath $SourcePath -Destination $destination -Force
  if ($ManifestEntrySet.Add($relativePath)) {
    $ManifestEntries.Add($relativePath) | Out-Null
  }
}

function Set-InstallRegistryEntry {
  param([string]$Path)

  New-Item -Path $installRegistryPath -Force | Out-Null
  Set-ItemProperty -Path $installRegistryPath -Name $installRegistryValueName -Value $Path
  Set-ItemProperty -Path $installRegistryPath -Name 'DisplayName' -Value 'Yagu'
  Set-ItemProperty -Path $installRegistryPath -Name 'ExecutablePath' -Value (Join-Path $Path $exeName)
  Set-ItemProperty -Path $installRegistryPath -Name 'InstalledAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
}

if (-not (Test-Path -LiteralPath $projectPath)) {
  throw "Could not find project file: $projectPath"
}

$installPath = Resolve-InstallDirectory -Value $InstallDir
$tempPublishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("wagu-publish-" + [System.Guid]::NewGuid().ToString('N'))

try {
  New-Item -ItemType Directory -Path $tempPublishDir -Force | Out-Null
  New-Item -ItemType Directory -Path $installPath -Force | Out-Null

  Write-Host "Building Yagu in Release mode..."
  & dotnet publish $projectPath --configuration Release --output $tempPublishDir --nologo
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
  }

  $publishedFiles = @(Get-ChildItem -LiteralPath $tempPublishDir -File -Recurse)
  if ($publishedFiles.Count -eq 0) {
    throw "Publish produced no files in $tempPublishDir"
  }

  $manifestEntries = New-Object System.Collections.Generic.List[string]
  $manifestEntrySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
  foreach ($file in $publishedFiles) {
    Copy-InstallFile -SourcePath $file.FullName -BasePath $tempPublishDir -InstallPath $installPath -ManifestEntries $manifestEntries -ManifestEntrySet $manifestEntrySet
  }

  $buildOutputDir = Join-Path $projectDir "bin\Release\$targetFramework"
  if (-not (Test-Path -LiteralPath $buildOutputDir)) {
    throw "Could not find Release build output directory: $buildOutputDir"
  }

  $winuiResourceFiles = @(
    Get-ChildItem -LiteralPath $buildOutputDir -Filter '*.xbf' -File -Recurse
    Get-ChildItem -LiteralPath $buildOutputDir -Filter "$assemblyName.pri" -File -Recurse
  )

  foreach ($file in $winuiResourceFiles) {
    Copy-InstallFile -SourcePath $file.FullName -BasePath $buildOutputDir -InstallPath $installPath -ManifestEntries $manifestEntries -ManifestEntrySet $manifestEntrySet
  }

  $manifestPath = Join-Path $installPath $manifestName
  Set-Content -LiteralPath $manifestPath -Value $manifestEntries -Encoding UTF8

  $exePath = Join-Path $installPath $exeName
  if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Install completed, but $exeName was not found at $exePath"
  }

  Set-InstallRegistryEntry -Path $installPath

  Write-Host "Installed $exeName to $installPath"
  Write-Host "Saved install location to $installRegistryPath\$installRegistryValueName"

  if (Read-YesNo -Prompt "Add a 'Search with Yagu' Explorer context menu?" -DefaultYes $true) {
    if (-not (Test-Path -LiteralPath $contextMenuScript)) {
      throw "Could not find context menu script: $contextMenuScript"
    }

    & $contextMenuScript -InstallDir $installPath
    if ($LASTEXITCODE -ne 0) {
      throw "Context menu registration failed with exit code $LASTEXITCODE"
    }
  }

  Write-Host ""
  Write-Host "Everything Search is not required for Yagu to work, but it is highly recommended to improve performance."
  Write-Host "If Everything is not installed, Yagu will offer to download and install it when the app loads."
  Write-Host ""
  Write-Host "Installation complete. Run: $exePath"
}
finally {
  if (Test-Path -LiteralPath $tempPublishDir) {
    Remove-Item -LiteralPath $tempPublishDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}