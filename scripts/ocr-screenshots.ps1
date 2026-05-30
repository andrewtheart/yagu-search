<#
.SYNOPSIS
    OCR Task Manager screenshots using Windows.Media.Ocr and extract Yagu's Disk MB/s.
.DESCRIPTION
    Uses the built-in Windows OCR engine (WinRT) to recognize text in Task Manager
    screenshots captured during profiling. Searches for the "Yagu" row and extracts
    CPU %, Memory MB, and Disk MB/s values. Outputs a TSV for analysis.

    Windows.Media.Ocr handles WinUI dark-theme rendering far better than Tesseract.
.PARAMETER ScreenshotDir
    Directory containing the screenshots.
.PARAMETER OutputFile
    Output TSV file path.
#>
param(
    [string]$ScreenshotDir,
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'

# Default paths relative to script location (workspace: scripts/)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if (-not $ScreenshotDir) {
    $ScreenshotDir = Join-Path $repoRoot "TestResults\TaskManagerScreenshots"
}
if (-not $OutputFile) {
    $OutputFile = Join-Path $repoRoot "TestResults\taskmgr-disk-throughput.tsv"
}

# --- Windows.Media.Ocr requires Windows PowerShell 5.1 (WinRT projection) ---
# If running under PS7+, re-invoke with powershell.exe (5.1)
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $args5 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
    if ($ScreenshotDir) { $args5 += '-ScreenshotDir'; $args5 += $ScreenshotDir }
    if ($OutputFile) { $args5 += '-OutputFile'; $args5 += $OutputFile }
    Write-Host "Re-invoking under Windows PowerShell 5.1 for WinRT OCR support..."
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @args5
    exit $LASTEXITCODE
}

# --- Load Windows.Media.Ocr (WinRT) ---
Add-Type -AssemblyName System.Runtime.WindowsRuntime

# Load WinRT types
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation.UniversalApiContract, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation.UniversalApiContract, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation.UniversalApiContract, ContentType=WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.RandomAccessStream, Windows.Storage, ContentType=WindowsRuntime]

# Helper to await WinRT async operations
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                   $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function Await-WinRT([object]$WinRtTask, [Type]$ResultType) {
    $asTask = $script:asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

# Also need the action version (no return value) for streams
$asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                   $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]

function Await-Action([object]$WinRtTask) {
    $netTask = $script:asTaskAction.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
}

function Convert-OcrNumberText([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    $text = $Value.Trim()

    # Task Manager uses comma thousands for values >= 1000. OCR sometimes drops
    # the decimal point after that comma, e.g. "1,1221" for 1,122.1.
    if ($text -match '^(\d{1,3}),(\d{4})$') {
        return "$($Matches[1])$($Matches[2].Substring(0, 3)).$($Matches[2].Substring(3, 1))"
    }

    if ($text -match '^\d{1,3}(?:,\d{3})+(?:\.\d+)?$') {
        return $text -replace ',', ''
    }

    if ($text -match '^\d+,\d+$') {
        return $text -replace ',', '.'
    }

    return $text -replace ',', ''
}

# Create OCR engine (uses system language)
$ocrEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if (-not $ocrEngine) {
    Write-Error "Failed to create Windows OCR engine. Ensure an OCR language pack is installed."
    exit 1
}
Write-Host "Windows OCR engine ready (language: $($ocrEngine.RecognizerLanguage.DisplayName))"

# Get all screenshots sorted by time index
$screenshots = Get-ChildItem $ScreenshotDir -Filter "taskmgr-*.png" -ErrorAction Stop | Sort-Object {
    if ($_.BaseName -match 't(\d+)s$') { [int]$Matches[1] } else { 0 }
}

if ($screenshots.Count -eq 0) {
    Write-Error "No screenshots found in $ScreenshotDir"
    exit 1
}

Write-Host "Processing $($screenshots.Count) screenshots with Windows OCR..."

$results = @()
$results += "Time_s`tDisk_MBps`tMemory_MB`tCPU_Pct`tRaw_Line"

foreach ($img in $screenshots) {
    # Extract time index from filename
    $timeSec = 0
    if ($img.BaseName -match 't(\d+)s$') {
        $timeSec = [int]$Matches[1]
    }

    try {
        # Open the image via WinRT StorageFile → BitmapDecoder → SoftwareBitmap
        $file = Await-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync($img.FullName)) ([Windows.Storage.StorageFile])
        $stream = Await-WinRT ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
        $decoder = Await-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await-WinRT ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

        # Run OCR on full image
        $ocrResult = Await-WinRT ($ocrEngine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])

        $bitmap.Dispose()
        $stream.Dispose()
    } catch {
        $results += "$timeSec`t`t`t`t[OCR failed: $($_.Exception.Message)]"
        continue
    }

    $fullText = $ocrResult.Text
    if (-not $fullText) {
        $results += "$timeSec`t`t`t`t[No text recognized]"
        continue
    }

    # Parse: find lines containing "Yagu" and extract numeric values
    # Windows OCR returns lines separated by newlines. The Task Manager row for Yagu
    # will contain something like: "Yagu 20.6% 645.3 MB 757.6 MB/s ..."
    $diskMBps = ""
    $memMB = ""
    $cpuPct = ""
    $rawLine = ""

    $lines = $fullText -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }

    # Strategy 1: Find line(s) with "Yagu" text
    $yaguLines = $lines | Where-Object { $_ -match '\bYagu\b' }

    if ($yaguLines) {
        # Combine Yagu lines (OCR might split across lines)
        $dataLine = ($yaguLines -join ' ')
    } else {
        # Strategy 2: Windows OCR may split by spatial regions. Look for the line
        # with MB/s that also has a percentage (the process data row)
        $dataLine = $lines | Where-Object { $_ -match 'MB/s' -and $_ -match '\d+[\.,]?\d*\s*%' } | Select-Object -First 1
        if (-not $dataLine) {
            # Strategy 3: Just find any line with MB/s
            $dataLine = $lines | Where-Object { $_ -match 'MB/s' } | Select-Object -First 1
        }
    }

    if ($dataLine) {
        # Windows OCR variants for "MB/s": observed renderings include "MB/s",
        # "MB's" (apostrophe), "M B/s" (split), "MBs", "M B's". Be permissive.
        # Pattern explanation:
        #   M\s?B   = "MB" with optional space
        #   \s?[/']?s = optional "/" or "'" before final 's'
        $mbpsUnit = '\s*M\s?B\s?[/'']?s\b'

        # ── Yagu's CPU/Memory/Disk are always the FIRST values after each
        # keyword, because Task Manager sorts by Disk-desc and Yagu is the only
        # heavy I/O process. Anchor on keywords for precision.
        $numberPattern = '(\d{1,3}(?:,\d{3})*(?:\.\d+)?|\d{1,3},\d{4}|\d+(?:[\.,]\d+)?)'

        # Disk: "Disk 731.7 MB's" / "Disk 731.7 MB/s" / "Disk 731 M B/s"
        if ($dataLine -match "Disk\s+$numberPattern$mbpsUnit") {
            $diskMBps = Convert-OcrNumberText $Matches[1]
        }
        # Some OCR captures drop the unit on the first value; fall back to first
        # number after "Disk" if it's plausible (>= 0.1, not "0.1 MB's" alone).
        if (-not $diskMBps -and $dataLine -match "Disk\s+$numberPattern\s+\d") {
            $diskMBps = Convert-OcrNumberText $Matches[1]
        }
        if (-not $diskMBps -and $dataLine -match "Disk\s+$numberPattern\s+Command\s+line") {
            $diskMBps = Convert-OcrNumberText $Matches[1]
        }

        # Memory: "Memory 681.1 MB" — first number after "Memory" keyword
        if ($dataLine -match 'Memory\s+([\d,]+(?:\.\d+)?)\s*MB(?!/)') {
            $memMB = $Matches[1] -replace ',', ''
        }

        # CPU: "CPU 27.1%" — first percentage after "CPU" keyword
        if ($dataLine -match 'CPU\s+(\d+(?:[.,]\d+)?)\s*%') {
            $cpuPct = $Matches[1] -replace ',', '.'
        }

        $rawLine = $dataLine -replace '\s+', ' '
    } else {
        $rawLine = "[Yagu row not found in OCR text]"
    }

    $results += "$timeSec`t$diskMBps`t$memMB`t$cpuPct`t$rawLine"

    if ($timeSec % 20 -eq 0 -or $timeSec -le 10) {
        Write-Host "  t=${timeSec}s: Disk=$diskMBps MB/s, Mem=$memMB MB, CPU=$cpuPct%"
    }
}

# Write results
$results | Out-File $OutputFile -Encoding UTF8
Write-Host "`nResults written to: $OutputFile"
Write-Host "Total screenshots processed: $($screenshots.Count)"

# Quick summary
$dataLines = $results | Select-Object -Skip 1 | Where-Object { $_ -match '^\d+\t[\d\.]+' }
if ($dataLines.Count -gt 0) {
    $diskValues = $dataLines | ForEach-Object { ($_ -split '\t')[1] } | Where-Object { $_ -ne '' } | ForEach-Object { [double]$_ }
    if ($diskValues.Count -gt 0) {
        $avg = ($diskValues | Measure-Object -Average).Average
        $max = ($diskValues | Measure-Object -Maximum).Maximum
        $min = ($diskValues | Measure-Object -Minimum).Minimum
        $above600 = ($diskValues | Where-Object { $_ -ge 600 }).Count
        $above1000 = ($diskValues | Where-Object { $_ -ge 1000 }).Count
        $pct1000 = [math]::Round($above1000/$diskValues.Count*100)
        Write-Host "`n=== Disk Throughput Summary ==="
        Write-Host "  Samples with data: $($diskValues.Count) / $($screenshots.Count)"
        Write-Host "  Average: $([math]::Round($avg, 1)) MB/s"
        Write-Host "  Max:     $([math]::Round($max, 1)) MB/s"
        Write-Host "  Min:     $([math]::Round($min, 1)) MB/s"
        Write-Host "  >= 600 MB/s:  $above600 / $($diskValues.Count) ($([math]::Round($above600/$diskValues.Count*100))%)"
        Write-Host "  >= 1000 MB/s: $above1000 / $($diskValues.Count) ($pct1000%)  [TARGET: >= 70%]"
        if ($pct1000 -ge 70) {
            Write-Host "  PASS: >= 1000 MB/s threshold met ($pct1000% >= 70%)" -ForegroundColor Green
        } else {
            Write-Host "  FAIL: >= 1000 MB/s threshold NOT met ($pct1000% < 70%)" -ForegroundColor Yellow
        }
    }
} else {
    Write-Warning "No valid data rows extracted. Check screenshots or OCR output."
}
