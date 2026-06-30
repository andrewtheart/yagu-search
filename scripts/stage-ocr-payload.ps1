<#
.SYNOPSIS
  Assembles the bundled OCR payload (native PaddleOCR runtime + PP-OCR models, and optionally the
  Tesseract English language data) into a folder laid out exactly how OcrAssetPaths.BundledRoot
  expects it: <OutputDir>\paddle\native, <OutputDir>\paddle\models, <OutputDir>\tesseract\tessdata.

.DESCRIPTION
  build-installer.ps1 calls this with -IncludeOcr so the OCR-bundled edition ships truly download-free.
  Assets are sourced in this order:
    1. Copied from the local download cache (%LOCALAPPDATA%\Yagu\ocr-runtime) when already present
       (fast, offline).
    2. Otherwise downloaded by running the staged Yagu.OcrWorker.exe once per engine with
       YAGU_OCR_ALLOW_DOWNLOAD=1, pointing its runtime/model/tessdata dirs straight at the payload.

  The function returns once the payload is verified complete (Paddle native probe DLLs + det/rec/cls
  models present); Tesseract is best-effort unless -RequireTesseract is set.

.PARAMETER OutputDir
  Destination payload root (becomes <app>\ocr-payload in the installer).

.PARAMETER WorkerExe
  Path to a built Yagu.OcrWorker.exe used to download any assets missing from the cache.

.PARAMETER CacheDir
  Local OCR cache to copy from. Defaults to %LOCALAPPDATA%\Yagu\ocr-runtime.

.PARAMETER SkipTesseract
  Do not include the Tesseract eng.traineddata (Paddle is the default engine).

.PARAMETER RequireTesseract
  Fail if the Tesseract language data cannot be obtained.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$OutputDir,
  [string]$WorkerExe,
  [string]$CacheDir = (Join-Path $env:LOCALAPPDATA 'Yagu\ocr-runtime'),
  [switch]$SkipTesseract,
  [switch]$RequireTesseract
)

$ErrorActionPreference = 'Stop'

$paddleNativeOut = Join-Path $OutputDir 'paddle\native'
$paddleModelsOut = Join-Path $OutputDir 'paddle\models'
$tessOut         = Join-Path $OutputDir 'tesseract\tessdata'

$cachePaddleNative = Join-Path $CacheDir 'paddle\native'
$cachePaddleModels = Join-Path $CacheDir 'paddle\models'
$cacheTessdata     = Join-Path $CacheDir 'tesseract\tessdata'

function Test-PaddleNativePresent([string]$dir) {
  return (Test-Path (Join-Path $dir 'paddle_inference_c.dll')) -and
         (Test-Path (Join-Path $dir 'OpenCvSharpExtern.dll'))
}

function Test-PaddleModelsPresent([string]$dir) {
  if (-not (Test-Path $dir)) { return $false }
  $haveDet = $false; $haveRec = $false; $haveCls = $false
  foreach ($sub in (Get-ChildItem -Directory -Path $dir -ErrorAction SilentlyContinue)) {
    if (-not (Test-Path (Join-Path $sub.FullName 'inference.pdiparams'))) { continue }
    if ($sub.Name -like '*_det') { $haveDet = $true }
    elseif ($sub.Name -like '*_rec') { $haveRec = $true }
    elseif ($sub.Name -like '*_cls') { $haveCls = $true }
  }
  return $haveDet -and $haveRec -and $haveCls
}

function Copy-Tree([string]$from, [string]$to) {
  New-Item -ItemType Directory -Path $to -Force | Out-Null
  Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
}

# Runs the worker once for a single engine and blocks until it emits the protocol "ready" envelope
# on stdout (diagnostics go to stderr, so stdout carries only protocol JSON). Then closes stdin so
# the worker exits cleanly.
function Invoke-OcrWorkerStage([string]$workerExe, [hashtable]$envVars, [string]$label) {
  if ([string]::IsNullOrWhiteSpace($workerExe) -or -not (Test-Path -LiteralPath $workerExe)) {
    throw "OCR worker not found ($label). Provide -WorkerExe pointing at a built Yagu.OcrWorker.exe, or pre-populate the cache."
  }

  Write-Host "  Downloading $label via worker (this can take several minutes)..."
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $workerExe
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  foreach ($k in $envVars.Keys) { $psi.Environment[$k] = [string]$envVars[$k] }
  $psi.Environment['YAGU_OCR_ALLOW_DOWNLOAD'] = '1'

  $proc = [System.Diagnostics.Process]::Start($psi)
  try {
    while (-not $proc.StandardOutput.EndOfStream) {
      $line = $proc.StandardOutput.ReadLine()
      if ($null -eq $line) { break }
      if ($line -match '"type"\s*:\s*"ready"') { break }
      if ($line -match '"type"\s*:\s*"error"') { throw "OCR worker init failed ($label): $line" }
    }
  }
  finally {
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(5000)) { try { $proc.Kill() } catch { } }
  }
}

Write-Host "Staging OCR payload to $OutputDir"
if (Test-Path -LiteralPath $OutputDir) {
  Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# --- Paddle native runtime ---
if (Test-PaddleNativePresent $cachePaddleNative) {
  Write-Host "  Copying Paddle native runtime from cache."
  Copy-Tree $cachePaddleNative $paddleNativeOut
} else {
  Invoke-OcrWorkerStage $WorkerExe @{
    'YAGU_OCR_ENGINE'       = 'paddle'
    'YAGU_OCR_RUNTIME_DIR'  = $paddleNativeOut
    'YAGU_OCR_MODEL_DIR'    = $paddleModelsOut
  } 'Paddle native runtime + models'
}

# --- Paddle models (the worker run above already fetched them when it ran) ---
if (-not (Test-PaddleModelsPresent $paddleModelsOut)) {
  if (Test-PaddleModelsPresent $cachePaddleModels) {
    Write-Host "  Copying Paddle models from cache."
    Copy-Tree $cachePaddleModels $paddleModelsOut
  }
}

if (-not (Test-PaddleNativePresent $paddleNativeOut)) {
  throw "Paddle native runtime payload is incomplete at $paddleNativeOut."
}
if (-not (Test-PaddleModelsPresent $paddleModelsOut)) {
  throw "Paddle model payload is incomplete at $paddleModelsOut."
}

# --- Tesseract language data (optional) ---
if (-not $SkipTesseract) {
  $tessTarget = Join-Path $tessOut 'eng.traineddata'
  if (Test-Path (Join-Path $cacheTessdata 'eng.traineddata')) {
    Write-Host "  Copying Tesseract eng.traineddata from cache."
    Copy-Tree $cacheTessdata $tessOut
  } else {
    try {
      Invoke-OcrWorkerStage $WorkerExe @{
        'YAGU_OCR_ENGINE'        = 'tesseract'
        'YAGU_OCR_TESSDATA_DIR'  = $tessOut
      } 'Tesseract eng.traineddata'
    } catch {
      if ($RequireTesseract) { throw }
      Write-Warning "Could not stage Tesseract data ($($_.Exception.Message)). The OCR-bundled installer will still ship Paddle (the default engine); Tesseract would download eng.traineddata on first use."
    }
  }

  if (-not (Test-Path $tessTarget)) {
    if ($RequireTesseract) { throw "Tesseract payload is missing at $tessTarget." }
    Write-Warning "Tesseract eng.traineddata not bundled."
  }
}

$mb = [math]::Round((Get-ChildItem -Recurse -File $OutputDir | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "OCR payload staged: $mb MB at $OutputDir"
