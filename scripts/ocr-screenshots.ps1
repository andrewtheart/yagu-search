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

function Wait-WinRTResult([object]$WinRtTask, [Type]$ResultType) {
    $asTask = $script:asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

# Also need the action version (no return value) for streams
$asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                   $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]

function Wait-WinRTAction([object]$WinRtTask) {
    $netTask = $script:asTaskAction.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
}

# Create OCR engine (uses system language)
$ocrEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if (-not $ocrEngine) {
    Write-Error "Failed to create Windows OCR engine. Ensure an OCR language pack is installed."
    exit 1
}
Write-Host "Windows OCR engine ready (language: $($ocrEngine.RecognizerLanguage.DisplayName))"

# Get screenshots from the latest capture batch sorted by time index.
$allScreenshots = Get-ChildItem $ScreenshotDir -Filter "taskmgr-*.png" -ErrorAction Stop

if ($allScreenshots.Count -gt 0) {
    $groups = $allScreenshots | Group-Object {
        if ($_.BaseName -match '^taskmgr-(\d{8}-\d{6})-t\d+s$') { $Matches[1] } else { 'ungrouped' }
    }
    $latestGroup = $groups | Where-Object { $_.Name -ne 'ungrouped' } | Sort-Object Name | Select-Object -Last 1
    if ($latestGroup) {
        if ($groups.Count -gt 1) {
            Write-Host "Multiple screenshot batches found; processing latest batch: $($latestGroup.Name) ($($latestGroup.Count) screenshots)"
        }
        $allScreenshots = $latestGroup.Group
    }
}

$screenshots = $allScreenshots | Sort-Object {
    if ($_.BaseName -match 't(\d+)s$') { [int]$Matches[1] } else { 0 }
}

if ($screenshots.Count -eq 0) {
    Write-Error "No screenshots found in $ScreenshotDir"
    exit 1
}

Write-Host "Processing $($screenshots.Count) screenshots with Windows OCR..."

$results = @()
$results += "Time_s`tDisk_MBps`tMemory_MB`tCPU_Pct`tRaw_Line"

function Convert-OcrNumber {
    param(
        [string]$Text,
        [switch]$TaskManagerDiskValue
    )
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $value = $Text -replace '[^0-9\.,]', ''

    # Task Manager's Disk column displays one decimal place for MB/s values.
    # Windows OCR often drops the decimal point in that narrow highlighted cell:
    #   222.2 -> 2222, 403.0 -> 4030, 1,551.1 -> 15511.
    if ($TaskManagerDiskValue -and $value -match '^\d{2,}$' -and $value -notmatch '[\.,]') {
        $value = $value.Insert($value.Length - 1, '.')
    }

    if ($value -match ',\d{3}\.') {
        $value = $value -replace ',', ''
    } elseif ($value -match ',' -and $value -notmatch '\.') {
        $value = $value -replace ',', '.'
    }
    return $value
}

function Get-NearestNumberLeft {
    param(
        [object[]]$Words,
        [double]$X,
        [double]$MinX = 0
    )
    $Words |
        Where-Object { $_.X -lt $X -and $_.X -ge $MinX -and $_.Text -match '^\d[\d\.,]*$' } |
        Sort-Object @{ Expression = { [math]::Abs($_.X - $X) } } |
        Select-Object -First 1
}

function Extract-TaskManagerRowSpatial {
    param([object]$OcrResult)

    $words = @()
    foreach ($ocrLine in $OcrResult.Lines) {
        foreach ($word in $ocrLine.Words) {
            $rect = $word.BoundingRect
            $words += [pscustomobject]@{
                Text = [string]$word.Text
                X = [double]$rect.X
                Y = [double]$rect.Y
                W = [double]$rect.Width
                H = [double]$rect.Height
                Cx = [double]($rect.X + ($rect.Width / 2.0))
                Cy = [double]($rect.Y + ($rect.Height / 2.0))
            }
        }
    }
    if ($words.Count -eq 0) { return $null }

    $diskHeader = $words | Where-Object { $_.Text -match '^(?i)Disk$' } | Sort-Object Y,X | Select-Object -First 1
    $memoryHeader = $words | Where-Object { $_.Text -match '^(?i)Memory$' } | Sort-Object Y,X | Select-Object -First 1
    $cpuHeader = $words | Where-Object { $_.Text -match '^(?i)CPU$' } | Sort-Object Y,X | Select-Object -First 1
    $commandHeader = $words | Where-Object { $_.Text -match '^(?i)Command$' } | Sort-Object Y,X | Select-Object -First 1

    if (-not $diskHeader -or -not $memoryHeader) { return $null }

    $headerY = [math]::Max($diskHeader.Y, $memoryHeader.Y)
    $cpuX = if ($cpuHeader) { $cpuHeader.Cx } else { $memoryHeader.Cx - 80 }
    $memoryX = $memoryHeader.Cx
    $diskX = $diskHeader.Cx
    $commandX = if ($commandHeader) { $commandHeader.X } else { $diskX + 110 }

    # OCR commonly reads the process name as "Vagu" and MB/s as "M8/s" in dark-mode screenshots.
    $yaguCandidate = $words |
        Where-Object {
            $_.Text -match '^(?i)[vy]agu$' -and
            $_.Y -gt $headerY -and
            $_.X -lt ($cpuX - 60)
        } |
        Sort-Object Y,X |
        Select-Object -First 1

    if (-not $yaguCandidate) { return $null }

    $rowY = $yaguCandidate.Cy
    $rowWords = $words |
        Where-Object { [math]::Abs($_.Cy - $rowY) -le 18 } |
        Sort-Object X

    $diskUnit = $rowWords |
        Where-Object {
            $_.Text -match '^(?i)M[B8]/s$|^(?i)MB/s$' -and
            $_.X -gt ($memoryX + 20) -and
            $_.X -lt $commandX
        } |
        Sort-Object X |
        Select-Object -First 1

    $diskWord = $null
    if ($diskUnit) {
        $diskWord = Get-NearestNumberLeft -Words $rowWords -X $diskUnit.X -MinX ($memoryX + 20)
    } else {
        $diskWord = $rowWords |
            Where-Object { $_.Text -match '^\d[\d\.,]*$' -and $_.X -gt ($memoryX + 20) -and $_.X -lt $commandX } |
            Sort-Object @{ Expression = { [math]::Abs($_.Cx - $diskX) } } |
            Select-Object -First 1
    }

    $memoryWord = $rowWords |
        Where-Object { $_.Text -match '^\d[\d\.,]*$' -and $_.X -gt ($cpuX + 20) -and $_.X -lt ($diskX - 20) } |
        Sort-Object @{ Expression = { [math]::Abs($_.Cx - $memoryX) } } |
        Select-Object -First 1

    $cpuWord = $rowWords |
        Where-Object { $_.Text -match '^\d[\d\.,]*%$' -and $_.X -gt ($cpuX - 60) -and $_.X -lt ($memoryX - 20) } |
        Sort-Object @{ Expression = { [math]::Abs($_.Cx - $cpuX) } } |
        Select-Object -First 1

    [pscustomobject]@{
        DiskMBps = Convert-OcrNumber $diskWord.Text -TaskManagerDiskValue
        MemoryMB = Convert-OcrNumber $memoryWord.Text
        CpuPct = Convert-OcrNumber $cpuWord.Text
        RawLine = ($rowWords | ForEach-Object { $_.Text }) -join ' '
    }
}

foreach ($img in $screenshots) {
    # Extract time index from filename
    $timeSec = 0
    if ($img.BaseName -match 't(\d+)s$') {
        $timeSec = [int]$Matches[1]
    }

    try {
        # Open the image via WinRT StorageFile → BitmapDecoder → SoftwareBitmap
        $file = Wait-WinRTResult ([Windows.Storage.StorageFile]::GetFileFromPathAsync($img.FullName)) ([Windows.Storage.StorageFile])
        $stream = Wait-WinRTResult ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
        $decoder = Wait-WinRTResult ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Wait-WinRTResult ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

        # Run OCR on full image
        $ocrResult = Wait-WinRTResult ($ocrEngine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])

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

    # Parse: find the Task Manager row spatially first. Windows OCR often splits
    # dark-mode table rows by column and may read "Yagu" as "Vagu" and "MB/s" as "M8/s".
    # Windows OCR returns lines separated by newlines. The Task Manager row for Yagu
    # will contain something like: "Yagu 20.6% 645.3 MB 757.6 MB/s ..."
    $diskMBps = ""
    $memMB = ""
    $cpuPct = ""
    $rawLine = ""

    $spatial = Extract-TaskManagerRowSpatial -OcrResult $ocrResult
    if ($spatial -and $spatial.DiskMBps) {
        $diskMBps = $spatial.DiskMBps
        $memMB = $spatial.MemoryMB
        $cpuPct = $spatial.CpuPct
        $rawLine = $spatial.RawLine -replace '\s+', ' '
    }

    $lines = $fullText -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }

    if (-not $diskMBps) {
        # Strategy 1: Find line(s) with "Yagu" text
        $yaguLines = $lines | Where-Object { $_ -match '\b[Vy]agu\b' }

        if ($yaguLines) {
            # Combine Yagu lines (OCR might split across lines)
            $dataLine = ($yaguLines -join ' ')
        } else {
            # Strategy 2: Windows OCR may split by spatial regions. Look for the line
            # with MB/s that also has a percentage (the process data row)
            $dataLine = $lines | Where-Object { $_ -match 'M[B8]/s|MB/s' -and $_ -match '\d+[\.,]?\d*\s*%' } | Select-Object -First 1
            if (-not $dataLine) {
                # Strategy 3: Just find any line with MB/s
                $dataLine = $lines | Where-Object { $_ -match 'M[B8]/s|MB/s' } | Select-Object -First 1
            }
        }

        if ($dataLine) {
            # Extract Disk MB/s value
            if ($dataLine -match '(\d+[\.,]\d+)\s*M[B8]/s') {
                $diskMBps = $Matches[1] -replace ',', '.'
            } elseif ($dataLine -match '(\d+)\s*M[B8]/s') {
                $diskMBps = Convert-OcrNumber $Matches[1] -TaskManagerDiskValue
            }

            # Extract Memory (number followed by MB but not MB/s)
            if ($dataLine -match '(\d{2,}[\.,]\d+)\s*MB(?!\s*/s)') {
                $memMB = $Matches[1] -replace ',', '.'
            }

            # Extract CPU % (first percentage value, typically like "20.6%")
            if ($dataLine -match '(\d+[\.,]\d+)\s*%') {
                $cpuPct = $Matches[1] -replace ',', '.'
            } elseif ($dataLine -match '(\d+)\s*%') {
                $cpuPct = $Matches[1]
            }

            $rawLine = $dataLine -replace '\s+', ' '
        } else {
            $rawLine = "[Yagu row not found in OCR text]"
        }
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
        $above1500 = ($diskValues | Where-Object { $_ -ge 1500 }).Count
        $pct1000 = [math]::Round($above1000/$diskValues.Count*100)
        $pct1500 = [math]::Round($above1500/$diskValues.Count*100)
        Write-Host "`n=== Disk Throughput Summary ==="
        Write-Host "  Samples with data: $($diskValues.Count) / $($screenshots.Count)"
        Write-Host "  Average: $([math]::Round($avg, 1)) MB/s"
        Write-Host "  Max:     $([math]::Round($max, 1)) MB/s"
        Write-Host "  Min:     $([math]::Round($min, 1)) MB/s"
        Write-Host "  >= 600 MB/s:  $above600 / $($diskValues.Count) ($([math]::Round($above600/$diskValues.Count*100))%)"
        Write-Host "  >= 1000 MB/s: $above1000 / $($diskValues.Count) ($pct1000%)"
        Write-Host "  >= 1500 MB/s: $above1500 / $($diskValues.Count) ($pct1500%)  [TARGET: >= 70%]"
        if ($pct1500 -ge 70) {
            Write-Host "  PASS: >= 1500 MB/s threshold met ($pct1500% >= 70%)" -ForegroundColor Green
        } else {
            Write-Host "  FAIL: >= 1500 MB/s threshold NOT met ($pct1500% < 70%)" -ForegroundColor Yellow
        }
    }
} else {
    Write-Warning "No valid data rows extracted. Check screenshots or OCR output."
}
