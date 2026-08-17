#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerates the dark-mode wizard artwork used by the Yagu Inno Setup installer.

.DESCRIPTION
    Produces installer\assets\wizard-dark.png (the tall welcome/finished panel) and
    installer\assets\wizard-small-dark.png (the header logo on the inner pages) from the
    brand logo at docs\images\yagu-logo.png. Both are 3x assets so the wizard stays sharp
    at 200-300% display scaling.

    Run this only when the branding changes; the generated PNGs are committed.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$LogoPath,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $LogoPath) { $LogoPath = Join-Path $RepoRoot 'docs\images\yagu-logo.png' }
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'installer\assets' }
if (-not (Test-Path $LogoPath)) { throw "Logo not found: $LogoPath" }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

# Brand palette. Accent is sampled from the logo's magnifier ring.
$Accent = [System.Drawing.Color]::FromArgb(0x44, 0x9B, 0xE4)
$BackTop = [System.Drawing.Color]::FromArgb(0x0D, 0x14, 0x1F)
$BackBottom = [System.Drawing.Color]::FromArgb(0x13, 0x1C, 0x29)
$Grid = [System.Drawing.Color]::FromArgb(0x1A, 0x24, 0x33)
$Card = [System.Drawing.Color]::FromArgb(0x16, 0x20, 0x2E)
$Tile = [System.Drawing.Color]::FromArgb(0x11, 0x19, 0x26)
$Muted = [System.Drawing.Color]::FromArgb(0x8D, 0x9A, 0xAB)
$Bright = [System.Drawing.Color]::FromArgb(0xE8, 0xEF, 0xF7)

function New-RoundedPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$Radius)
    $d = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Set-Quality {
    param($G)
    $G.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $G.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $G.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
}

function Write-TrackedText {
    param($G, [string]$Text, $Font, $Brush, [float]$X, [float]$Y, [float]$Tracking)
    $typographic = [System.Drawing.StringFormat]::GenericTypographic
    $spaceAdvance = $Font.Size * 0.32
    $cursor = $X
    foreach ($ch in $Text.ToCharArray()) {
        if ($ch -eq ' ') { $cursor += $spaceAdvance + $Tracking; continue }
        $s = [string]$ch
        $G.DrawString($s, $Font, $Brush, $cursor, $Y)
        $cursor += $G.MeasureString($s, $Font, [System.Drawing.PointF]::new(0, 0), $typographic).Width + $Tracking
    }
}

<# Crops the logo's transparent margin so it fills the artwork rather than floating in padding. #>
function Get-TrimmedLogo {
    param($Source)
    $minX = $Source.Width; $minY = $Source.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $Source.Height; $y++) {
        for ($x = 0; $x -lt $Source.Width; $x++) {
            if ($Source.GetPixel($x, $y).A -lt 16) { continue }
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($maxX -lt 0) { return $Source.Clone() }
    $crop = New-Object System.Drawing.Rectangle $minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1)
    return $Source.Clone($crop, $Source.PixelFormat)
}

$sourceLogo = [System.Drawing.Bitmap]::FromFile($LogoPath)
$logo = Get-TrimmedLogo $sourceLogo
$sourceLogo.Dispose()
try {
    # ---------------- Large welcome panel: 492x942 (3x of Inno's 164x314) ----------------
    $large = New-Object System.Drawing.Bitmap 492, 942, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($large)
    try {
        Set-Quality $g

        $rect = New-Object System.Drawing.Rectangle 0, 0, 492, 942
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $BackTop, $BackBottom, 90.0
        $g.FillRectangle($bg, $rect)
        $bg.Dispose()

        $gridPen = New-Object System.Drawing.Pen $Grid, 2
        for ($x = 63; $x -lt 492; $x += 63) { $g.DrawLine($gridPen, $x, 0, $x, 942) }
        for ($y = 63; $y -lt 942; $y += 63) { $g.DrawLine($gridPen, 0, $y, 492, $y) }
        $gridPen.Dispose()

        $accentBrush = New-Object System.Drawing.SolidBrush $Accent
        $g.FillRectangle($accentBrush, 0, 0, 9, 942)

        $titleFont = New-Object System.Drawing.Font 'Segoe UI', 46, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $subFont = New-Object System.Drawing.Font 'Segoe UI', 15, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $bodyFont = New-Object System.Drawing.Font 'Segoe UI', 19, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $bodyBold = New-Object System.Drawing.Font 'Segoe UI', 19, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $brightBrush = New-Object System.Drawing.SolidBrush $Bright
        $mutedBrush = New-Object System.Drawing.SolidBrush $Muted

        $g.DrawString('YAGU', $titleFont, $brightBrush, 57, 84)
        Write-TrackedText $g 'YET ANOTHER GREP UTILITY' $subFont $accentBrush 62 144 2.0

        $g.FillRectangle($accentBrush, 60, 214, 372, 9)
        $cardPath = New-RoundedPath 60 231 372 372 16
        $cardBrush = New-Object System.Drawing.SolidBrush $Card
        $g.FillPath($cardBrush, $cardPath)
        $cardBrush.Dispose()
        $cardPath.Dispose()

        $g.DrawImage($logo, 96, 267, 300, 300)

        $g.DrawString('Search every drive. Instantly.', $bodyFont, $mutedBrush, 58, 706)
        $g.DrawString('Text, PDFs, images, archives', $bodyBold, $brightBrush, 58, 766)

        foreach ($d in $titleFont, $subFont, $bodyFont, $bodyBold, $brightBrush, $mutedBrush, $accentBrush) { $d.Dispose() }
    }
    finally { $g.Dispose() }

    $largePath = Join-Path $OutputDir 'wizard-dark.png'
    $large.Save($largePath, [System.Drawing.Imaging.ImageFormat]::Png)
    $large.Dispose()
    Write-Host "Wrote $largePath (492x942)"

    # ---------------- Small header tile: 256x256 ----------------
    $small = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($small)
    try {
        Set-Quality $g
        $g.Clear([System.Drawing.Color]::Transparent)

        $tilePath = New-RoundedPath 0 0 256 256 52
        $tileBrush = New-Object System.Drawing.SolidBrush $Tile
        $g.FillPath($tileBrush, $tilePath)
        $tileBrush.Dispose()
        $tilePath.Dispose()

        $g.DrawImage($logo, 30, 30, 196, 196)
    }
    finally { $g.Dispose() }

    $smallPath = Join-Path $OutputDir 'wizard-small-dark.png'
    $small.Save($smallPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $small.Dispose()
    Write-Host "Wrote $smallPath (256x256)"
}
finally { $logo.Dispose() }
