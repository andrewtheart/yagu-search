using System;
using System.Runtime.InteropServices;

namespace Yagu.Services;

/// <summary>
/// Captures window pixels via Win32 GDI. Decoupled from UI framework —
/// only requires the target window HWND.
/// </summary>
public sealed class ScreenshotCaptureService
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    public sealed record WindowPixelCapture(int Width, int Height, uint Dpi, byte[] Pixels);

    public static WindowPixelCapture CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Window handle was not initialized.");

        if (!TryGetVisibleWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("Window bounds were not available.");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Window bounds were empty.");

        uint dpi = (uint)Math.Max(96, GetDpiForWindow(hwnd));
        int stride = checked(width * 4);
        int byteCount = checked(stride * height);

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException("Screen device context was not available.");

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
                throw new InvalidOperationException("Compatible device context could not be created.");

            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                    biSizeImage = (uint)byteCount,
                },
            };

            bitmap = CreateDIBSection(screenDc, ref bitmapInfo, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("Capture bitmap could not be created.");

            oldObject = SelectObject(memoryDc, bitmap);
            if (oldObject == IntPtr.Zero)
                throw new InvalidOperationException("Capture bitmap could not be selected.");

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("Window pixels could not be copied.");

            var pixels = new byte[byteCount];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            return new WindowPixelCapture(width, height, dpi, pixels);
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, oldObject);
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero)
                _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static bool TryGetVisibleWindowRect(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0)
            return true;

        return GetWindowRect(hwnd, out rect);
    }
}
