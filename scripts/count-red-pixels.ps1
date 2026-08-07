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

.PARAMETER MaximumHighlightComponents
    Maximum number of active-highlight components accepted by strict geometry
    validation. Defaults to one; multiline matches may opt into two.

.EXAMPLE
    .\scripts\count-red-pixels.ps1 -Directory C:\src\Yagu\TestResults\MatchNavScreenshots\MatchCase3

.EXAMPLE
    .\scripts\count-red-pixels.ps1 -Directory C:\src\Yagu\TestResults\MatchNavScreenshots\MatchCase3 -Threshold 30
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Directory,

    [string]$Pattern = '03-match-*.png',

    [int]$Threshold = -1,

    [string]$Manifest = '',

    [int]$ExpectedTermLength = 0,

    [ValidateRange(1, 2)]
    [int]$MaximumHighlightComponents = 1,

    [switch]$StrictGeometry
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
        $n -like 'System.Drawing*' -or $n -like 'System.Private.Windows*' -or
        $n -eq 'System.Collections' -or $n -eq 'System.Runtime'
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
    public sealed class GeometryResult
    {
        public int RedPixels { get; set; }
        public int ComponentCount { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int LeftClearance { get; set; }
        public int TopClearance { get; set; }
        public int RightClearance { get; set; }
        public int BottomClearance { get; set; }
        public string ComponentBounds { get; set; } = "";
        public bool Pass { get; set; }
        public string Reason { get; set; } = "";
    }

    private sealed class Component
    {
        public int Pixels;
        public int MinX = int.MaxValue;
        public int MinY = int.MaxValue;
        public int MaxX = int.MinValue;
        public int MaxY = int.MinValue;
        public Component Next;
    }

    private static bool IsHighlight(byte b, byte g, byte r)
        => r > 120 && (g * 2) < r && (b * 3) < r;

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
                // silently dropped the darker one. Match the OrangeRed hue ratio instead of one exact
                // shade; this deliberately excludes the gold text used for non-active match terms.
                for (int y = 0; y < h; y++)
                {
                    int rowStart = y * stride;
                    int rowEnd = rowStart + (w * 4);
                    for (int i = rowStart; i < rowEnd; i += 4)
                    {
                        byte b = buf[i];
                        byte g2 = buf[i + 1];
                        byte r = buf[i + 2];
                        if (IsHighlight(b, g2, r))
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

    public static GeometryResult Analyze(
        string path, int viewportX, int viewportY, int viewportWidth, int viewportHeight,
        int expectedTermLength, int maximumHighlightComponents)
    {
        using (var loaded = Image.FromFile(path))
        using (var bmp = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bmp))
                graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);

            int left = Math.Max(0, viewportX);
            int top = Math.Max(0, viewportY);
            int right = Math.Min(bmp.Width, viewportX + viewportWidth);
            int bottom = Math.Min(bmp.Height, viewportY + viewportHeight);
            int width = Math.Max(0, right - left);
            int height = Math.Max(0, bottom - top);
            var result = new GeometryResult();
            if (width <= 0 || height <= 0)
            {
                result.Reason = "preview viewport is outside the captured image";
                return result;
            }

            var mask = new bool[width * height];
            var rect = new Rectangle(left, top, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int byteCount = data.Stride * height;
                var buffer = new byte[byteCount];
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);
                for (int y = 0; y < height; y++)
                {
                    int row = y * data.Stride;
                    for (int x = 0; x < width; x++)
                    {
                        int i = row + x * 4;
                        if (IsHighlight(buffer[i], buffer[i + 1], buffer[i + 2]))
                        {
                            mask[y * width + x] = true;
                            result.RedPixels++;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            var visited = new bool[mask.Length];
            var queue = new int[mask.Length];
            int meaningfulCount = 0;
            Component first = null;
            Component last = null;
            Component largest = null;
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start])
                    continue;
                var component = new Component();
                visited[start] = true;
                int queueHead = 0;
                int queueTail = 0;
                queue[queueTail++] = start;
                while (queueHead < queueTail)
                {
                    int current = queue[queueHead++];
                    int x = current % width;
                    int y = current / width;
                    component.Pixels++;
                    component.MinX = Math.Min(component.MinX, x);
                    component.MinY = Math.Min(component.MinY, y);
                    component.MaxX = Math.Max(component.MaxX, x);
                    component.MaxY = Math.Max(component.MaxY, y);
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int next = ny * width + nx;
                        if (mask[next] && !visited[next])
                        {
                            visited[next] = true;
                            queue[queueTail++] = next;
                        }
                    }
                }

                // Ignore isolated anti-aliased/red content pixels; the active marker's connected border
                // is comfortably above this floor even for a short literal.
                if (component.Pixels >= 20)
                {
                    meaningfulCount++;
                    if (first == null)
                        first = component;
                    else
                        last.Next = component;
                    last = component;
                    if (largest == null || component.Pixels > largest.Pixels)
                        largest = component;
                }
            }

            result.ComponentCount = meaningfulCount;
            if (meaningfulCount == 0)
            {
                result.Reason = "no connected active-highlight component in preview";
                return result;
            }

            Component main = largest;
            result.Left = main.MinX;
            result.Top = main.MinY;
            result.Width = main.MaxX - main.MinX + 1;
            result.Height = main.MaxY - main.MinY + 1;
            result.LeftClearance = main.MinX;
            result.TopClearance = main.MinY;
            result.RightClearance = width - 1 - main.MaxX;
            result.BottomClearance = height - 1 - main.MaxY;

            int minimumWidth = Math.Max(8, expectedTermLength * 4);
            int maximumWidth = Math.Max(24, expectedTermLength * 12 + 8);
            if (meaningfulCount > maximumHighlightComponents)
            {
                result.Reason = maximumHighlightComponents == 1
                    ? $"expected one highlighted term, found {meaningfulCount} connected components"
                    : $"expected at most {maximumHighlightComponents} highlighted components, found {meaningfulCount}";
                return result;
            }

            int componentIndex = 0;
            for (Component component = first; component != null; component = component.Next)
            {
                componentIndex++;
                int componentWidth = component.MaxX - component.MinX + 1;
                int componentHeight = component.MaxY - component.MinY + 1;
                if (result.ComponentBounds.Length != 0)
                    result.ComponentBounds += ";";
                result.ComponentBounds += $"{component.MinX},{component.MinY},{componentWidth},{componentHeight}";
                int rightClearance = width - 1 - component.MaxX;
                int bottomClearance = height - 1 - component.MaxY;
                string prefix = meaningfulCount == 1 ? "highlight" : $"highlight component {componentIndex}";
                bool isPrimary = component == main;

                if (component.Pixels < Math.Max(30, expectedTermLength * 4))
                    result.Reason = $"{prefix} border has only {component.Pixels} connected pixels";
                else if (componentWidth < minimumWidth)
                    result.Reason = $"{prefix} width {componentWidth}px is too narrow for {expectedTermLength} characters";
                else if (componentWidth > maximumWidth)
                    result.Reason = $"{prefix} width {componentWidth}px exceeds term-only maximum {maximumWidth}px";
                else if (isPrimary && (componentHeight < 8 || componentHeight > 32))
                    result.Reason = $"{prefix} height {componentHeight}px is not a single text-row marker";
                else if (!isPrimary && componentHeight > 4 && (componentHeight < 8 || componentHeight > 32))
                    result.Reason = $"{prefix} height {componentHeight}px is neither a row marker nor a multiline continuation";
                else if (component.MinX < 48)
                    result.Reason = $"{prefix} enters/touches the left preview gutter ({component.MinX}px clearance)";
                else if (component.MinY < 4 || rightClearance < 4 || bottomClearance < 4)
                    result.Reason = $"{prefix} touches a preview edge (clearance L/T/R/B={component.MinX}/{component.MinY}/{rightClearance}/{bottomClearance})";
                else if (!isPrimary)
                {
                    int horizontalDelta = Math.Abs(component.MinX - main.MinX);
                    int widthDelta = Math.Abs(componentWidth - result.Width);
                    int verticalGap = component.MaxY < main.MinY
                        ? main.MinY - component.MaxY - 1
                        : component.MinY > main.MaxY
                            ? component.MinY - main.MaxY - 1
                            : 0;
                    if (horizontalDelta > 8 || widthDelta > 8)
                        result.Reason = $"{prefix} is not aligned with the primary multiline marker (x delta={horizontalDelta}px, width delta={widthDelta}px)";
                    else if (verticalGap <= 0 || verticalGap > 32)
                        result.Reason = $"{prefix} is not on an adjacent multiline row ({verticalGap}px gap)";
                }

                if (result.Reason.Length != 0)
                    return result;
            }

            result.Pass = true;
            return result;
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

$manifestRows = @{}
if ($StrictGeometry) {
    if ($ExpectedTermLength -le 0) {
        Write-Error "-ExpectedTermLength must be positive with -StrictGeometry"
        exit 1
    }
    if (-not (Test-Path -LiteralPath $Manifest)) {
        Write-Error "Navigation manifest not found: $Manifest"
        exit 1
    }
    foreach ($row in Import-Csv -LiteralPath $Manifest -Delimiter "`t") {
        $manifestRows[$row.Screenshot] = $row
    }
}

$strictFailures = 0
foreach ($file in $files) {
    try {
        if ($StrictGeometry) {
            $row = $manifestRows[$file.Name]
            if (-not $row) {
                Write-Error "No navigation manifest row for $($file.Name)"
                exit 1
            }
            $geometry = [RedPixelScanner]::Analyze(
                $file.FullName,
                [int]$row.ViewportX,
                [int]$row.ViewportY,
                [int]$row.ViewportWidth,
                [int]$row.ViewportHeight,
                $ExpectedTermLength,
                $MaximumHighlightComponents)
            $passText = if ($geometry.Pass) { 'PASS' } else { 'FAIL' }
            $reason = $geometry.Reason -replace "[`t`r`n]", ' '
            Write-Output ("GEOMETRY`t{0}`toccurrence={1}`tred={2}`tcomponents={3}`tbox={4},{5},{6},{7}`tcomponentBoxes={8}`tclearance={9},{10},{11},{12}`treason={13}`tpath={14}" -f `
                $passText, [int]$row.Occurrence, $geometry.RedPixels, $geometry.ComponentCount,
                $geometry.Left, $geometry.Top, $geometry.Width, $geometry.Height,
                $geometry.ComponentBounds,
                $geometry.LeftClearance, $geometry.TopClearance,
                $geometry.RightClearance, $geometry.BottomClearance,
                $reason, $file.FullName)
            if (-not $geometry.Pass) { $strictFailures++ }
        } else {
            $count = [RedPixelScanner]::Count($file.FullName)
            if ($Threshold -lt 0 -or $count -le $Threshold) {
                [pscustomobject]@{
                    RedPixels = $count
                    Path      = $file.FullName
                }
            }
        }
    } catch {
        Write-Warning "Failed to process $($file.FullName): $_"
        if ($StrictGeometry) { $strictFailures++ }
    }
}

if ($StrictGeometry -and $strictFailures -gt 0) {
    Write-Error "$strictFailures screenshot(s) failed strict active-match geometry validation."
    exit 2
}
