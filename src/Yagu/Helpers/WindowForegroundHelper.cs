using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Yagu.Helpers;

internal static class WindowForegroundHelper
{
    private const int GwlpHwndParent = -8;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public static IntPtr GetWindowHandle(Window window)
    {
        try { return WinRT.Interop.WindowNative.GetWindowHandle(window); }
        catch { return IntPtr.Zero; }
    }

    public static IntPtr ConfigureOwnedWindow(Window window, IntPtr ownerHwnd)
    {
        IntPtr childHwnd = GetWindowHandle(window);
        ConfigureOwnedWindow(childHwnd, ownerHwnd);
        return childHwnd;
    }

    public static void ConfigureOwnedWindow(IntPtr childHwnd, IntPtr ownerHwnd)
    {
        if (childHwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero || childHwnd == ownerHwnd)
            return;

        _ = SetWindowLongPtr(childHwnd, GwlpHwndParent, ownerHwnd);
    }

    public static void BringOwnedWindowToFront(Window window, IntPtr ownerHwnd)
        => BringOwnedWindowToFront(GetWindowHandle(window), ownerHwnd);

    public static void BringOwnedWindowToFront(IntPtr childHwnd, IntPtr ownerHwnd)
    {
        if (childHwnd == IntPtr.Zero)
            return;

        ConfigureOwnedWindow(childHwnd, ownerHwnd);
        _ = ShowWindow(childHwnd, SwRestore);

        uint flags = SwpNoMove | SwpNoSize | SwpShowWindow;
        if (ownerHwnd != IntPtr.Zero && IsTopMost(ownerHwnd))
        {
            _ = SetWindowPos(childHwnd, HwndTopmost, 0, 0, 0, 0, flags);
        }
        else
        {
            _ = SetWindowPos(childHwnd, HwndNotTopmost, 0, 0, 0, 0, flags);
            _ = SetWindowPos(childHwnd, HwndTop, 0, 0, 0, 0, flags);
        }

        _ = SetForegroundWindow(childHwnd);
    }

    public static void CenterWindowOverOwner(
        AppWindow appWindow,
        IntPtr ownerHwnd,
        int width,
        int height,
        int minWidth = 420,
        int minHeight = 260)
    {
        appWindow.MoveAndResize(CalculateCenteredBounds(ownerHwnd, width, height, minWidth, minHeight));
    }

    private static RectInt32 CalculateCenteredBounds(IntPtr ownerHwnd, int width, int height, int minWidth, int minHeight)
    {
        int x = 100;
        int y = 100;

        const uint monitorDefaultToNearest = 2;
        IntPtr monitor = ownerHwnd == IntPtr.Zero
            ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, monitorDefaultToNearest)
            : MonitorFromWindow(ownerHwnd, monitorDefaultToNearest);

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref monitorInfo))
        {
            var workArea = monitorInfo.rcWork;
            width = Math.Min(width, Math.Max(minWidth, workArea.Right - workArea.Left));
            height = Math.Min(height, Math.Max(minHeight, workArea.Bottom - workArea.Top));

            if (ownerHwnd != IntPtr.Zero && GetWindowRect(ownerHwnd, out var ownerRect))
            {
                int ownerCenterX = (ownerRect.Left + ownerRect.Right) / 2;
                int ownerCenterY = (ownerRect.Top + ownerRect.Bottom) / 2;
                x = ownerCenterX - width / 2;
                y = ownerCenterY - height / 2;
            }
            else
            {
                x = workArea.Left + ((workArea.Right - workArea.Left) - width) / 2;
                y = workArea.Top + ((workArea.Bottom - workArea.Top) - height) / 2;
            }

            if (x < workArea.Left) x = workArea.Left;
            if (y < workArea.Top) y = workArea.Top;
            if (x + width > workArea.Right) x = workArea.Right - width;
            if (y + height > workArea.Bottom) y = workArea.Bottom - height;
        }

        return new RectInt32(x, y, width, height);
    }

    /// <summary>
    /// Positions <paramref name="appWindow"/> anchored to the TOP-RIGHT of its owner (just below the
    /// owner's title bar/toolbar) instead of centered — used by dialogs that should sit out of the way
    /// near the top-right controls (e.g. the editor "unsaved changes" prompt next to Save/Back) rather
    /// than covering the center of the window. Margins are DPI-scaled logical pixels; the result is
    /// clamped to the monitor work area.
    /// </summary>
    public static void PositionTopRightOverOwner(
        AppWindow appWindow,
        IntPtr ownerHwnd,
        int width,
        int height,
        int marginTopDip = 56,
        int marginRightDip = 14,
        int minWidth = 420,
        int minHeight = 260)
    {
        appWindow.MoveAndResize(CalculateTopRightBounds(ownerHwnd, width, height, marginTopDip, marginRightDip, minWidth, minHeight));
    }

    private static RectInt32 CalculateTopRightBounds(IntPtr ownerHwnd, int width, int height, int marginTopDip, int marginRightDip, int minWidth, int minHeight)
    {
        int x = 100;
        int y = 100;

        const uint monitorDefaultToNearest = 2;
        IntPtr monitor = ownerHwnd == IntPtr.Zero
            ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, monitorDefaultToNearest)
            : MonitorFromWindow(ownerHwnd, monitorDefaultToNearest);

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref monitorInfo))
        {
            var workArea = monitorInfo.rcWork;
            width = Math.Min(width, Math.Max(minWidth, workArea.Right - workArea.Left));
            height = Math.Min(height, Math.Max(minHeight, workArea.Bottom - workArea.Top));

            double dpiScale = ownerHwnd != IntPtr.Zero ? GetDpiForWindow(ownerHwnd) / 96.0 : 1.0;
            if (dpiScale <= 0) dpiScale = 1.0;
            int marginTop = (int)Math.Round(marginTopDip * dpiScale);
            int marginRight = (int)Math.Round(marginRightDip * dpiScale);

            if (ownerHwnd != IntPtr.Zero && GetWindowRect(ownerHwnd, out var ownerRect))
            {
                x = ownerRect.Right - width - marginRight;
                y = ownerRect.Top + marginTop;
            }
            else
            {
                x = workArea.Right - width - marginRight;
                y = workArea.Top + marginTop;
            }

            if (x < workArea.Left) x = workArea.Left;
            if (y < workArea.Top) y = workArea.Top;
            if (x + width > workArea.Right) x = workArea.Right - width;
            if (y + height > workArea.Bottom) y = workArea.Bottom - height;
        }

        return new RectInt32(x, y, width, height);
    }

    private static bool IsTopMost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        long extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        return (extendedStyle & WsExTopmost) != 0;
    }

    /// <summary>
    /// Returns the work area (excludes the taskbar) of the monitor nearest <paramref name="hwnd"/>,
    /// in physical pixels. Falls back to the primary monitor when <paramref name="hwnd"/> is 0.
    /// </summary>
    public static bool TryGetWorkArea(IntPtr hwnd, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        const uint monitorDefaultToNearest = 2;
        IntPtr monitor = hwnd == IntPtr.Zero
            ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, monitorDefaultToNearest)
            : MonitorFromWindow(hwnd, monitorDefaultToNearest);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var wa = info.rcWork;
        x = wa.Left;
        y = wa.Top;
        width = wa.Right - wa.Left;
        height = wa.Bottom - wa.Top;
        return width > 0 && height > 0;
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, index, value)
            : new IntPtr(SetWindowLong32(hWnd, index, value.ToInt32()));

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, index)
            : new IntPtr(GetWindowLong32(hWnd, index));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}