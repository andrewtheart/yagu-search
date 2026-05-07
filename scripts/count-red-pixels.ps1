<#
.SYNOPSIS
    Counts OrangeRed-ish pixels (the active match highlight color, ~#FF4500)
    in every PNG in a directory and reports counts per screenshot.

.DESCRIPTION
    Used to verify whether the red active-match highlight is actually visible
    in the test-match-nav.ps1 screenshot output. A screenshot with only a
    handful of stray red pixels (< ~30) almost certainly has no visible
    highlight; a screenshot with hundreds+ has a real highlight.

.PARAMETER Directory
    The directory containing PNG screenshots to analyse.

.PARAMETER Pattern
    Optional file name pattern. Defaults to '03-match-*.png' to match the
    test script's naming, but can be overridden (e.g. '*.png').

.PARAMETER Threshold
    Optional. If set, only outputs screenshots whose red pixel count is
    AT MOST this value (i.e. likely-failing screenshots).

.EXAMPLE
    .\scripts\count-red-pixels.ps1 -Directory D:\yagu\TestResults\MatchNavScreenshots\MatchCase3

.EXAMPLE
    .\scripts\count-red-pixels.ps1 -Directory D:\yagu\TestResults\MatchNavScreenshots\MatchCase3 -Threshold 30
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Directory,

    [string]$Pattern = '03-match-*.png',

    [int]$Threshold = -1
)

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $Directory)) {
    Write-Error "Directory not found: $Directory"
    exit 1
}

$files = Get-ChildItem -LiteralPath $Directory -Filter $Pattern -File | Sort-Object Name
if ($files.Count -eq 0) {
    Write-Warning "No files matching '$Pattern' in $Directory"
    exit 0
}

foreach ($file in $files) {
    try {
        $img = [System.Drawing.Image]::FromFile($file.FullName)
        $bmp = [System.Drawing.Bitmap]$img
        $count = 0
        # Sample every 2nd pixel in both dimensions for ~4x speed-up; the
        # red highlight is many pixels wide so this still detects it reliably.
        for ($y = 0; $y -lt $img.Height; $y += 2) {
            for ($x = 0; $x -lt $img.Width; $x += 2) {
                $p = $bmp.GetPixel($x, $y)
                if ($p.R -gt 200 -and $p.G -lt 100 -and $p.B -lt 50 -and ($p.R - $p.G) -gt 100) {
                    $count++
                }
            }
        }
        $img.Dispose()

        if ($Threshold -lt 0 -or $count -le $Threshold) {
            [pscustomobject]@{
                RedPixels = $count
                Path      = $file.FullName
            }
        }
    } catch {
        Write-Warning "Failed to process $($file.FullName): $_"
    }
}
