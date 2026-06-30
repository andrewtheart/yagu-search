<#
.SYNOPSIS
  Builds one or more Yagu installer variants by orchestrating build-installer.ps1.

.DESCRIPTION
  Yagu ships four installer variants:

    x64       64-bit (win-x64).   OCR works; models download on first use.
    x86       32-bit (win-x86).   OCR works; models download on first use.
    arm64     ARM64  (win-arm64). OCR works; models download on first use.
    x64-ocr   64-bit with the OCR models bundled OFFLINE (no first-use download).

  There is intentionally no x86-ocr / arm64-ocr variant: the bundled OCR engine
  (the native PaddleOCR runtime) is win-x64 only, so OCR can only be bundled for
  x64. On x86/arm64 OCR still works at runtime by downloading the models.

  Each variant is a full self-contained Native AOT Release build plus its Inno
  Setup installer. Builds run to installer\ (and installer\output\), and the
  per-architecture/edition "keep newest" rule in build-installer.ps1 applies.

.PARAMETER Variant
  Which variant(s) to build. One or more of: x64, x86, arm64, x64-ocr, or all
  (the default). Accepts a comma-separated list. Order/duplicates are normalized.

.PARAMETER InnoSetupPath
  Optional path to ISCC.exe. Passed through to build-installer.ps1 (which
  otherwise auto-detects the standard Inno Setup 6 location).

.PARAMETER OcrPayloadCacheDir
  Optional local OCR cache used to source the bundled payload for the x64-ocr
  variant. Passed through to build-installer.ps1.

.EXAMPLE
  .\build-all-installers.ps1
  Builds all four variants (x64, x86, arm64, x64-ocr).

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64,arm64
  Builds only the x64 and arm64 (no-bundled-OCR) installers.

.EXAMPLE
  .\build-all-installers.ps1 -Variant x64-ocr
  Builds only the OCR-bundled x64 installer.

.EXAMPLE
  .\build-all-installers.ps1 -WhatIf
  Prints the build plan (resolved variants + the build-installer.ps1 commands)
  without building anything.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [ValidateSet('x64', 'x86', 'arm64', 'x64-ocr', 'all')]
  [string[]]$Variant = @('all'),
  [string]$InnoSetupPath,
  [string]$OcrPayloadCacheDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$buildInstaller = Join-Path $repoRoot 'build-installer.ps1'
if (-not (Test-Path -LiteralPath $buildInstaller)) {
  throw "build-installer.ps1 not found next to this script at: $buildInstaller"
}
$installerDir = Join-Path $repoRoot 'installer'

# Canonical variant -> (architecture, bundle-OCR) and the installer filename suffix
# that build-installer.ps1 produces (YaguSetup-<version>-<suffix>.exe).
$variantSpecs = [ordered]@{
  'x64'     = @{ Architecture = 'x64';   IncludeOcr = $false; Suffix = 'x64' }
  'x86'     = @{ Architecture = 'x86';   IncludeOcr = $false; Suffix = 'x86' }
  'arm64'   = @{ Architecture = 'arm64'; IncludeOcr = $false; Suffix = 'arm64' }
  'x64-ocr' = @{ Architecture = 'x64';   IncludeOcr = $true;  Suffix = 'x64-ocr' }
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

$failed = @($results | Where-Object { -not $_.Success })
if ($failed.Count -gt 0) {
  throw "$($failed.Count) of $($results.Count) variant(s) failed: $(@($failed.Variant) -join ', ')."
}

Write-Host "All $($results.Count) variant(s) built successfully." -ForegroundColor Green
