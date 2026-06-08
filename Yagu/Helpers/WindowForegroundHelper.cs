using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

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

    private static bool IsTopMost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        long extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        return (extendedStyle & WsExTopmost) != 0;
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
}