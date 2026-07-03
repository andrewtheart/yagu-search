<#
.SYNOPSIS
  Builds one or more Yagu installer variants by orchestrating build-installer.ps1.

.DESCRIPTION
  Yagu ships four installer variants:

    x64       64-bit (win-x64).   OCR works; models download on first use.
    x86       32-bit (win-x86).   OCR works; models download on first use.
    arm64     ARM64  (win-arm64). OCR works; models download on first use.
    x64-offline  64-bit OFFLINE edition: the OCR runtime + models AND the Tesseract
                 English data are bundled (no first-use download), and Tesseract is
                 the default OCR engine.

  There is intentionally no x86-offline / arm64-offline variant: the bundled OCR
  runtime (native PaddleOCR + OpenCv) is win-x64 only, so OCR can only be bundled for
  x64. On x86/arm64 OCR still works at runtime by downloading its assets.

  Each variant is a full self-contained Native AOT Release build plus its Inno
  Setup installer. Builds run to installer\ (and installer\output\), and the
  per-architecture/edition "keep newest" rule in build-installer.ps1 applies.

.PARAMETER Variant
  Which variant(s) to build. One or more of: x64, x86, arm64, x64-offline, or all
  (the default). Accepts a comma-separated list. Order/duplicates are normalized.

.PARAMETER InnoSetupPath
  Optional path to ISCC.exe. Passed through to build-installer.ps1 (which
  otherwise auto-detects the standard Inno Setup 6 location).

.PARAMETER OcrPayloadCacheDir
  Optional local OCR cache used to source the bundled payload for the x64-offline
  variant. Passed through to build-installer.ps1.

.PARAMETER SkipReadmeUpdate
  Skip rewriting the README "Download Installer" table. By default, after a
  successful build the four table rows are updated so their filename, GitHub raw
  URL, and (~N MB) size match the newest installer of each suffix on disk.

.EXAMPLE
  .\build-all-installers.ps1
  Builds all four variants (x64, x86, arm64, x64-offline).

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64,arm64
  Builds only the x64 and arm64 (no-bundled-OCR) installers.

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64-offline
  Builds only the OFFLINE x64 installer (OCR bundled, Tesseract default).

.EXAMPLE
  .\build-all-installers.ps1 -WhatIf
  Prints the build plan (resolved variants + the build-installer.ps1 commands)
  without building anything.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [ValidateSet('x64', 'x86', 'arm64', 'x64-offline', 'all')]
  [string[]]$Variant = @('all'),
  [string]$InnoSetupPath,
  [string]$OcrPayloadCacheDir,
  [switch]$SkipReadmeUpdate
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$buildInstaller = Join-Path $repoRoot 'build-installer.ps1'
if (-not (Test-Path -LiteralPath $buildInstaller)) {
  throw "build-installer.ps1 not found next to this script at: $buildInstaller"
}
$installerDir = Join-Path $repoRoot 'installer'

# Rewrites the four rows of the README "Download Installer" table so each row's
# filename, GitHub raw URL, and (~N MB) size match the newest installer of that
# suffix on disk. Only the link + size token is replaced; the bold label and the
# rest of every row (including its em-dash / middle-dot glyphs) are preserved via
# a capture group, so this script stays ASCII-only. Rows whose suffix has no
# installer on disk are left untouched.
function Update-ReadmeDownloadTable {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$ReadmePath,
    [Parameter(Mandatory)][string]$InstallerDir
  )

  if (-not (Test-Path -LiteralPath $ReadmePath)) {
    Write-Warning "README not found at '$ReadmePath' - skipping download-table update."
    return
  }

  $rawBase = 'https://github.com/andrewtheart/yagu-search/raw/main/installer'
  # End-anchored suffixes; 'x64-offline' is checked before 'x64' so the two never
  # collide. Each pattern ends with the exact '-<suffix>.exe', so the 'x64' row
  # can never match the 'x64-offline' installer.
  $suffixes = @('x64-offline', 'x64', 'arm64', 'x86')

  # Read as UTF-8 explicitly. Get-Content -Raw under Windows PowerShell 5.1 decodes
  # a BOM-less UTF-8 file as ANSI, which would corrupt the table's em-dash / middle-dot
  # glyphs on write. File.ReadAllText defaults to UTF-8 (BOM-aware) under 5.1 and pwsh 7.
  $content = [System.IO.File]::ReadAllText($ReadmePath)
  $original = $content
  $updated = New-Object System.Collections.Generic.List[string]

  foreach ($suffix in $suffixes) {
    $suffixEsc = [regex]::Escape($suffix)
    $exe = Get-ChildItem -LiteralPath $InstallerDir -Filter "YaguSetup-*-$suffix.exe" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match "-$suffixEsc\.exe$" } |
      Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $exe) {
      Write-Warning "No installer for suffix '$suffix' in '$InstallerDir' - leaving its README row unchanged."
      continue
    }

    $fileName = $exe.Name
    $sizeMb = [math]::Round($exe.Length / 1MB)

    # Group 1 captures the '[**Label** - ' display prefix generically (any chars
    # up to the filename), so the non-ASCII glyphs never appear in this file.
    $pattern = "(\[[^\]]*?)YaguSetup-[0-9.]+-$suffixEsc\.exe\]\(" +
               [regex]::Escape($rawBase) + "/YaguSetup-[0-9.]+-$suffixEsc\.exe\)\s*\(~[\d.]+\s*MB\)"
    $replacement = "`${1}$fileName]($rawBase/$fileName) (~$sizeMb MB)"

    $rx = [regex]$pattern
    if (-not $rx.IsMatch($content)) {
      Write-Warning "Could not find the '$suffix' row in the README download table - it was left unchanged."
      continue
    }

    $new = $rx.Replace($content, $replacement)
    if ($new -ne $content) {
      $content = $new
      $updated.Add("$suffix -> $fileName (~$sizeMb MB)")
    }
  }

  if ($content -ne $original) {
    # Preserve UTF-8 (no BOM) and the existing line endings; works under both
    # Windows PowerShell 5.1 and pwsh 7 (Set-Content -Encoding utf8 differs).
    [System.IO.File]::WriteAllText($ReadmePath, $content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "README download table updated:" -ForegroundColor Green
    foreach ($u in $updated) { Write-Host "  $u" -ForegroundColor Green }
  }
  else {
    Write-Host "README download table already up to date." -ForegroundColor DarkGray
  }
}

# Canonical variant -> (architecture, bundle-OCR) and the installer filename suffix
# that build-installer.ps1 produces (YaguSetup-<version>-<suffix>.exe).
$variantSpecs = [ordered]@{
  'x64'         = @{ Architecture = 'x64';   IncludeOcr = $false; Suffix = 'x64' }
  'x86'         = @{ Architecture = 'x86';   IncludeOcr = $false; Suffix = 'x86' }
  'arm64'       = @{ Architecture = 'arm64'; IncludeOcr = $false; Suffix = 'arm64' }
  'x64-offline' = @{ Architecture = 'x64';   IncludeOcr = $true;  Suffix = 'x64-offline' }
}

# Resolve requested variants: expand 'all', de-duplicate, keep canonical order.
$requested =
  if ($Variant -contains 'all') { @($variantSpecs.Keys) }
  else { @($variantSpecs.Keys | Where-Object { $Variant -contains $_ }) }

if ($requested.Count -eq 0) {
  throw "No valid variants selected. Choose from: $(@($variantSpecs.Keys) -join ', '), or 'all'."
}

Write-Host "Yagu installer build - variants: $($requested -join ', ')" -ForegroundColor Cyan

# Dry run (-WhatIf): print the plan and exit without building.
if ($WhatIfPreference) {
  Write-Host "WhatIf: the following installer builds would run:" -ForegroundColor Yellow
  foreach ($name in $requested) {
    $spec = $variantSpecs[$name]
    $cmd = "build-installer.ps1 -Architecture $($spec.Architecture)"
    if ($spec.IncludeOcr) { $cmd += ' -IncludeOcr' }
    Write-Host ("  {0,-8} -> {1}" -f $name, $cmd)
  }
  return
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($name in $requested) {
  $spec = $variantSpecs[$name]

  Write-Host ""
  Write-Host "############################################################" -ForegroundColor Cyan
  Write-Host "# Building variant: $name (Architecture=$($spec.Architecture), IncludeOcr=$($spec.IncludeOcr))" -ForegroundColor Cyan
  Write-Host "############################################################" -ForegroundColor Cyan

  $params = @{ Architecture = $spec.Architecture }
  if ($spec.IncludeOcr) { $params['IncludeOcr'] = $true }
  if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) { $params['InnoSetupPath'] = $InnoSetupPath }
  if (-not [string]::IsNullOrWhiteSpace($OcrPayloadCacheDir)) { $params['OcrPayloadCacheDir'] = $OcrPayloadCacheDir }

  $success = $true
  $errorMessage = $null
  try {
    # build-installer.ps1 has $ErrorActionPreference='Stop' and throws on any
    # failure, so a non-throwing return means the variant built successfully.
    & $buildInstaller @params
  }
  catch {
    $success = $false
    $errorMessage = $_.Exception.Message
    Write-Warning "Variant '$name' FAILED: $errorMessage"
  }

  $results.Add([pscustomobject]@{ Variant = $name; Suffix = $spec.Suffix; Success = $success; Error = $errorMessage })
}

# Summary: match each built variant to its installer in the repo installer\ folder.
Write-Host ""
Write-Host "==================== Build summary ====================" -ForegroundColor Cyan
foreach ($r in $results) {
  if ($r.Success) {
    $exe = Get-ChildItem -LiteralPath $installerDir -Filter "YaguSetup-*-$($r.Suffix).exe" -File -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match "-$([regex]::Escape($r.Suffix))\.exe$" } |
      Sort-Object LastWriteTime | Select-Object -Last 1
    if ($exe) {
      Write-Host ("  [OK]   {0,-8} -> {1} ({2} MB)" -f $r.Variant, $exe.Name, [math]::Round($exe.Length / 1MB, 1)) -ForegroundColor Green
    } else {
      Write-Host ("  [OK?]  {0,-8} -> built, but no matching installer found in installer\" -f $r.Variant) -ForegroundColor Yellow
    }
  } else {
    Write-Host ("  [FAIL] {0,-8} -> {1}" -f $r.Variant, $r.Error) -ForegroundColor Red
  }
}
Write-Host "=======================================================" -ForegroundColor Cyan

# Point the README download table at the newest installers on disk (unless opted out).
if ($SkipReadmeUpdate) {
  Write-Host "Skipping README download-table update (-SkipReadmeUpdate)." -ForegroundColor DarkGray
}
else {
  try {
    Update-ReadmeDownloadTable -ReadmePath (Join-Path $repoRoot 'README.md') -InstallerDir $installerDir
  }
  catch {
    Write-Warning "README download-table update failed: $($_.Exception.Message)"
  }
}

$failed = @($results | Where-Object { -not $_.Success })
if ($failed.Count -gt 0) {
  throw "$($failed.Count) of $($results.Count) variant(s) failed: $(@($failed.Variant) -join ', ')."
}

Write-Host "All $($results.Count) variant(s) built successfully." -ForegroundColor Green
