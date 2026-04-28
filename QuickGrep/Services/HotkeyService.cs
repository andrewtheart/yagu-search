using System.Runtime.InteropServices;

namespace QuickGrep.Services;

/// <summary>
/// Registers a global hotkey via Win32 RegisterHotKey. Receives WM_HOTKEY in the
/// host window and raises <see cref="Pressed"/>.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x1001;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private bool _registered;
    public event Action? Pressed;

    public bool Register(IntPtr hwnd, uint modifiers = MOD_WIN | MOD_SHIFT, uint vk = 0x47 /*G*/)
    {
        Unregister();
        _hwnd = hwnd;
        _registered = RegisterHotKey(hwnd, HotkeyId, modifiers, vk);
        return _registered;
    }

    /// <summary>Forward WM_HOTKEY messages here from the window subclass.</summary>
    public void OnWmHotkey(int wParam)
    {
        if (wParam == HotkeyId) Pressed?.Invoke();
    }

    public void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose() => Unregister();
}
