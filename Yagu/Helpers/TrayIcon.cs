using System.Runtime.InteropServices;

namespace Yagu.Helpers;

/// <summary>
/// Minimal Win32 system-tray icon wrapper for unpackaged WinUI 3 apps.
/// Creates a hidden message-only window to receive tray callbacks.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public event Action? OpenResetRequested;
    public event Action? OpenExistingRequested;
    public event Action? CloseRequested;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int NIIF_INFO = 0x00000001;

    private const int CMD_OPEN_RESET = 1;
    private const int CMD_OPEN_EXISTING = 2;
    private const int CMD_CLOSE = 3;

    private IntPtr _hwnd;
    private bool _added;
    private bool _disposed;
    private readonly WndProcDelegate _wndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIcon(string tooltip, string icoPath)
    {
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
        if (_hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadIcon(icoPath),
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        };

        _added = Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    /// <summary>
    /// Shows a balloon notification on the tray icon.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = text.Length > 255 ? text[..255] : text,
            dwInfoFlags = NIIF_INFO,
        };

        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Updates the tooltip text shown when hovering over the tray icon.
    /// </summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        };

        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _added = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int lp = (int)lParam;
            if (lp == WM_LBUTTONDBLCLK)
            {
                OpenExistingRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (lp == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        if (msg == WM_COMMAND)
        {
            int cmd = (int)wParam & 0xFFFF;
            switch (cmd)
            {
                case CMD_OPEN_RESET: OpenResetRequested?.Invoke(); break;
                case CMD_OPEN_EXISTING: OpenExistingRequested?.Invoke(); break;
                case CMD_CLOSE: CloseRequested?.Invoke(); break;
            }
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        const int MF_STRING = 0x0000;
        const int MF_SEPARATOR = 0x0800;
        const int TPM_RIGHTBUTTON = 0x0002;
        const int TPM_BOTTOMALIGN = 0x0020;

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(hMenu, MF_STRING, CMD_OPEN_RESET, "Open (reset search)");
            AppendMenuW(hMenu, MF_STRING, CMD_OPEN_EXISTING, "Open (existing search)");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, CMD_CLOSE, "Close");

            // Required so the menu dismisses when clicking outside it.
            SetForegroundWindow(_hwnd);
            GetCursorPos(out POINT pt);
            TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.x, pt.y, _hwnd, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private IntPtr CreateMessageWindow()
    {
        const string className = "YaguTrayMsgWnd";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className,
            hInstance = GetModuleHandleW(null),
        };
        RegisterClassExW(ref wc);

        const int HWND_MESSAGE = -3;
        return CreateWindowExW(0, className, "YaguTray", 0,
            0, 0, 0, 0, (IntPtr)HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static IntPtr LoadIcon(string icoPath)
    {
        if (!System.IO.File.Exists(icoPath)) return IntPtr.Zero;
        const int IMAGE_ICON = 1;
        const int LR_LOADFROMFILE = 0x0010;
        const int LR_DEFAULTSIZE = 0x0040;
        return LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
    }

    #region P/Invoke

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type,
        int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, int uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    #endregion
}
