using System.Runtime.InteropServices;

namespace Yagu.Services;

/// <summary>
/// Registers a global hotkey via Win32 RegisterHotKey. Receives WM_HOTKEY in the
/// host window and raises <see cref="Pressed"/>.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint CtrlShiftModifiers = ModControl | ModShift;
    public const int WmHotkey = 0x0312;
    public const char DefaultStartKey = 'A';
    private const int HotkeyId = 0x1001;
    private const int ProbeHotkeyId = 0x1002;
    private const uint FirstLetterVirtualKey = 0x41;
    private const uint LastLetterVirtualKey = 0x5A;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private bool _registered;
    public event Action? Pressed;

    public bool IsRegistered => _registered;
    public char? RegisteredKey { get; private set; }

    public bool Register(IntPtr hwnd, char key) => Register(hwnd, CtrlShiftModifiers, VirtualKeyFromLetter(key));

    public bool Register(IntPtr hwnd, uint modifiers = CtrlShiftModifiers, uint vk = FirstLetterVirtualKey)
    {
        Unregister();
        _hwnd = hwnd;
        _registered = RegisterHotKey(hwnd, HotkeyId, modifiers, vk);
        RegisteredKey = _registered ? LetterFromVirtualKey(modifiers, vk) : null;
        if (!_registered)
            _hwnd = IntPtr.Zero;
        return _registered;
    }

    public bool TryRegisterFirstAvailableCtrlShift(IntPtr hwnd, string? preferredKey, out char selectedKey)
    {
        foreach (var key in BuildRegistrationOrder(preferredKey))
        {
            if (Register(hwnd, key))
            {
                selectedKey = key;
                return true;
            }
        }

        selectedKey = '\0';
        return false;
    }

    public IReadOnlyList<char> GetAvailableCtrlShiftLetterKeys(IntPtr hwnd)
    {
        var keys = new List<char>();
        foreach (var key in EnumerateLetterKeys())
        {
            if (_registered && _hwnd == hwnd && RegisteredKey == key)
            {
                keys.Add(key);
                continue;
            }

            if (IsCtrlShiftLetterAvailable(hwnd, key))
                keys.Add(key);
        }

        return keys;
    }

    public static char? ChooseAvailableKey(IReadOnlyList<char> availableKeys, string? preferredKey)
    {
        if (availableKeys.Count == 0)
            return null;

        if (TryNormalizeLetter(preferredKey, out var normalizedPreferred) && availableKeys.Contains(normalizedPreferred))
            return normalizedPreferred;

        return availableKeys[0];
    }

    public static IEnumerable<char> BuildRegistrationOrder(string? preferredKey)
    {
        bool hasPreferred = TryNormalizeLetter(preferredKey, out var normalizedPreferred);
        if (hasPreferred)
            yield return normalizedPreferred;

        foreach (var key in EnumerateLetterKeys())
        {
            if (!hasPreferred || key != normalizedPreferred)
                yield return key;
        }
    }

    public static bool TryNormalizeLetter(string? value, out char key)
    {
        key = '\0';
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        const string prefix = "Ctrl+Shift+";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[prefix.Length..];

        if (trimmed.Length != 1)
            return false;

        var normalized = char.ToUpperInvariant(trimmed[0]);
        if (normalized is < 'A' or > 'Z')
            return false;

        key = normalized;
        return true;
    }

    public static string FormatCtrlShift(char key) => $"Ctrl+Shift+{NormalizeLetterOrThrow(key)}";

    public static uint VirtualKeyFromLetter(char key) => NormalizeLetterOrThrow(key);

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
            RegisteredKey = null;
            _hwnd = IntPtr.Zero;
        }
    }

    public void Dispose() => Unregister();

    private static bool IsCtrlShiftLetterAvailable(IntPtr hwnd, char key)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        if (!RegisterHotKey(hwnd, ProbeHotkeyId, CtrlShiftModifiers, VirtualKeyFromLetter(key)))
            return false;

        UnregisterHotKey(hwnd, ProbeHotkeyId);
        return true;
    }

    private static IEnumerable<char> EnumerateLetterKeys()
    {
        for (var key = DefaultStartKey; key <= 'Z'; key++)
            yield return key;
    }

    private static char NormalizeLetterOrThrow(char key)
    {
        var normalized = char.ToUpperInvariant(key);
        if (normalized is < 'A' or > 'Z')
            throw new ArgumentOutOfRangeException(nameof(key), "Global hotkeys support Ctrl+Shift+A through Ctrl+Shift+Z.");

        return normalized;
    }

    private static char? LetterFromVirtualKey(uint modifiers, uint vk)
    {
        if (modifiers != CtrlShiftModifiers || vk is < FirstLetterVirtualKey or > LastLetterVirtualKey)
            return null;

        return (char)vk;
    }
}
