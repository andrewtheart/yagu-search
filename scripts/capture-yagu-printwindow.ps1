param([string]$OutPath = "C:\src\Yagu\TestResults\GoogleMatchNav\printwindow-current.png")

Add-Type -AssemblyName System.Drawing

$src = @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Pw {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Capture(IntPtr hwnd, string outPath, uint flags) {
        RECT r; if (!GetWindowRect(hwnd, out r)) return "NO_RECT";
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return "BAD_SIZE";
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(hwnd, hdc, flags);
            g.ReleaseHdc(hdc);
            if (!ok) return "PRINTWINDOW_FAILED";
            bmp.Save(outPath, ImageFormat.Png);
            // crude non-black check
            long sum = 0; int step = Math.Max(1, w / 40);
            for (int x = 0; x < w; x += step) for (int y = 0; y < h; y += Math.Max(1, h / 40)) { var c = bmp.GetPixel(x, y); sum += c.R + c.G + c.B; }
            return "OK sizes=" + w + "x" + h + " brightnessSum=" + sum;
        }
    }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing 2>$null
if (-not ("Pw" -as [type])) { Add-Type -TypeDefinition $src -ReferencedAssemblies @("System.Drawing") }

$proc = Get-Process -Name Yagu -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Host "NO_YAGU_WINDOW"; exit 1 }
$hwnd = $proc.MainWindowHandle
Write-Host "HWND=$hwnd pid=$($proc.Id)"

# Try PW_RENDERFULLCONTENT (0x2) first, then 0x0
$r2 = [Pw]::Capture($hwnd, $OutPath, 2)
Write-Host "flags=2 -> $r2"
if ($r2 -notlike "OK*") {
    $r0 = [Pw]::Capture($hwnd, $OutPath, 0)
    Write-Host "flags=0 -> $r0"
}
Write-Host "Saved: $OutPath"
