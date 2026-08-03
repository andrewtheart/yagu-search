using System.Runtime.InteropServices;

namespace Yagu.Helpers;

/// <summary>
/// Receives <see cref="SearchRequest"/>s from a second Yagu launch via WM_COPYDATA, so the single
/// running instance can honor an Explorer "Search with Yagu" context-menu invocation (or any other
/// forwarded search) instead of the second process silently dropping the folder. A dedicated
/// message-only window with a well-known class name is used so the sender can find it with
/// <c>FindWindow</c> regardless of whether the main window is visible or docked in the tray.
/// </summary>
internal sealed class SearchRequestListener : IDisposable
{
    /// <summary>Well-known window class the sender locates with <c>FindWindow</c>. Global (not
    /// per-user) is fine because the single-instance mutex already scopes to one running instance.</summary>
    public const string WindowClassName = "YaguSearchRequestListenerWnd";

    /// <summary>Identifies Yagu's own WM_COPYDATA payloads so stray senders are ignored.</summary>
    public const int CopyDataId = 0x59475551; // 'YGUQ'

    /// <summary>Raised (on the thread that owns this window — the UI thread) when a request arrives.</summary>
    public event Action<SearchRequest>? RequestReceived;

    private const int WM_COPYDATA = 0x004A;
    private const int WM_DESTROY = 0x0002;

    private IntPtr _hwnd;
    private bool _disposed;
    private readonly WndProcDelegate _wndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public SearchRequestListener()
    {
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COPYDATA)
        {
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (cds.dwData == (IntPtr)CopyDataId && cds.lpData != IntPtr.Zero && cds.cbData > 0)
                {
                    // cbData is a byte count of a UTF-16 string.
                    var payload = Marshal.PtrToStringUni(cds.lpData, (int)(cds.cbData / 2));
                    if (SearchRequestCodec.TryDecode(payload, out var request))
                        RequestReceived?.Invoke(request);
                }
            }
            catch
            {
                // A malformed payload must never destabilize the message loop.
            }
            return (IntPtr)1;
        }

        if (msg == WM_DESTROY)
            return IntPtr.Zero;

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private IntPtr CreateMessageWindow()
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = WindowClassName,
            hInstance = GetModuleHandleW(null),
        };
        RegisterClassExW(ref wc);

        const int HWND_MESSAGE = -3;
        return CreateWindowExW(0, WindowClassName, "YaguSearchRequestListener", 0,
            0, 0, 0, 0, (IntPtr)HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    #region P/Invoke

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

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    #endregion
}

/// <summary>
/// Sends a <see cref="SearchRequest"/> to the running Yagu instance's <see cref="SearchRequestListener"/>
/// via WM_COPYDATA. Used by <c>Program.Main</c> when a second launch finds the single-instance mutex
/// already owned, so the folder/query is handed to the live window instead of being discarded.
/// </summary>
internal static class SearchRequestSender
{
    private const int WM_COPYDATA = 0x004A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Returns true when a listening instance was found and the message was delivered.</summary>
    public static bool TrySend(SearchRequest request)
    {
        IntPtr target = FindWindowW(SearchRequestListener.WindowClassName, null);
        if (target == IntPtr.Zero) return false;

        string payload = SearchRequestCodec.Encode(request);
        IntPtr buffer = Marshal.StringToHGlobalUni(payload);
        try
        {
            var cds = new COPYDATASTRUCT
            {
                dwData = (IntPtr)SearchRequestListener.CopyDataId,
                cbData = (payload.Length + 1) * 2, // include the terminating null, in bytes
                lpData = buffer,
            };

            IntPtr result = SendMessageTimeoutW(
                target, WM_COPYDATA, IntPtr.Zero, ref cds,
                SMTO_ABORTIFHUNG, 3000, out _);
            return result != IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam,
        ref COPYDATASTRUCT lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
}
