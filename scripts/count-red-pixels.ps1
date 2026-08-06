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
    .\scripts\count-red-pixels.ps1 -Directory C:\src\Yagu\TestResults\MatchNavScreenshots\MatchCase3

.EXAMPLE
    .\scripts\count-red-pixels.ps1 -Directory C:\src\Yagu\TestResults\MatchNavScreenshots\MatchCase3 -Threshold 30
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Directory,

    [string]$Pattern = '03-match-*.png',

    [int]$Threshold = -1
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Drawing.Common -ErrorAction SilentlyContinue

# Compile a tiny native helper to scan pixels — pure PowerShell loops over
# millions of bytes are pathologically slow (12+ minutes for 200 1920×1200
# screenshots), even when reading a pre-locked byte[]. C# does the same scan
# in well under a second per image.
#
# In PS7/.NET 10, System.Drawing's public surface depends on private
# assemblies (System.Private.Windows.GdiPlus, System.Private.Windows.Core).
# Touch a few types so the runtime force-loads them, then build the
# reference list from every loaded System.Drawing*/Private.Windows*
# assembly so Add-Type can resolve transitive types.
$null = [System.Drawing.Bitmap]
$null = [System.Drawing.Imaging.ImageLockMode]
$drawingRefs = [System.AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object {
        $n = $_.GetName().Name
        $n -like 'System.Drawing*' -or $n -like 'System.Private.Windows*'
    } |
    ForEach-Object { $_.Location } |
    Where-Object { $_ } |
    Sort-Object -Unique

Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class RedPixelScanner
{
    // Counts pixels where R>200, G<100, B<50, R-G>100 — the OrangeRed-ish
    // active-match highlight. Samples every 2nd pixel in both dims for ~4x
    // speed-up, matching the original heuristic.
    public static int Count(string path)
    {
        using (var loaded = Image.FromFile(path))
        using (var bmp = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);

            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int h = bmp.Height;
                int w = bmp.Width;
                int byteCount = stride * h;
                byte[] buf = new byte[byteCount];
                Marshal.Copy(data.Scan0, buf, 0, byteCount);
                int count = 0;
                // BGRA in memory: B=+0 G=+1 R=+2 A=+3.
                // Scan EVERY pixel: the highlight border is ~1px, so the old "every 2nd row and column"
                // sampling discarded three quarters of it and could report 0 for a clearly visible box.
                // The predicate also has to span the whole highlight palette -- the active match renders
                // both OrangeRed (255,69,0) and a darker (192,51,0) variant, and the old r>200 test
                // silently dropped the darker one. Match "strongly red/orange relative to G and B" instead
                // of one exact shade; a frame with no highlight still scores ~3.
                for (int y = 0; y < h; y++)
                {
                    int rowStart = y * stride;
                    int rowEnd = rowStart + (w * 4);
                    for (int i = rowStart; i < rowEnd; i += 4)
                    {
                        byte b = buf[i];
                        byte g2 = buf[i + 1];
                        byte r = buf[i + 2];
                        if (r > 150 && (r - g2) > 40 && (r - b) > 90)
                            count++;
                    }
                }
                return count;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
}
"@ -ReferencedAssemblies $drawingRefs

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
        $count = [RedPixelScanner]::Count($file.FullName)
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
