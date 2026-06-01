using System.Runtime.InteropServices;
using Yagu.Services;

namespace Yagu.Tests;

public class HotkeyServiceTests : IDisposable
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly IntPtr _testHwnd;
    private readonly HotkeyService _sut = new();

    public HotkeyServiceTests()
    {
        _testHwnd = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (_testHwnd != IntPtr.Zero)
            DestroyWindow(_testHwnd);
    }

    private bool HasTestWindow => _testHwnd != IntPtr.Zero;

    // ── TryNormalizeLetter ──────────────────────────────────────────

    [Theory]
    [InlineData("A", 'A')]
    [InlineData("a", 'A')]
    [InlineData("z", 'Z')]
    [InlineData("Ctrl+Shift+M", 'M')]
    [InlineData("ctrl+shift+z", 'Z')]
    public void TryNormalizeLetter_AcceptsLettersAndDisplayText(string value, char expected)
    {
        Assert.True(HotkeyService.TryNormalizeLetter(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AA")]
    [InlineData("Ctrl+Alt+A")]
    [InlineData("1")]
    [InlineData("Ctrl+Shift+")]
    [InlineData("Ctrl+Shift+1")]
    public void TryNormalizeLetter_RejectsUnsupportedKeys(string? value)
    {
        Assert.False(HotkeyService.TryNormalizeLetter(value, out var key));
        Assert.Equal('\0', key);
    }

    // ── BuildRegistrationOrder ──────────────────────────────────────

    [Fact]
    public void BuildRegistrationOrder_StartsWithPreferredThenFallsBackAlphabetically()
    {
        var order = HotkeyService.BuildRegistrationOrder("M").Take(4).ToArray();
        Assert.Equal(['M', 'A', 'B', 'C'], order);
    }

    [Fact]
    public void BuildRegistrationOrder_UsesAlphabeticalDefaultWhenPreferredInvalid()
    {
        var order = HotkeyService.BuildRegistrationOrder("Ctrl+Alt+M").Take(3).ToArray();
        Assert.Equal(['A', 'B', 'C'], order);
    }

    [Fact]
    public void BuildRegistrationOrder_NullPreferred_StartsAlphabetically()
    {
        var order = HotkeyService.BuildRegistrationOrder(null).Take(3).ToArray();
        Assert.Equal(['A', 'B', 'C'], order);
    }

    [Fact]
    public void BuildRegistrationOrder_CoversFullAlphabet()
    {
        var order = HotkeyService.BuildRegistrationOrder(null).ToArray();
        Assert.Equal(26, order.Length);
        Assert.Equal('A', order[0]);
        Assert.Equal('Z', order[25]);
    }

    [Fact]
    public void BuildRegistrationOrder_PreferredNotDuplicated()
    {
        var order = HotkeyService.BuildRegistrationOrder("A").ToArray();
        Assert.Equal(26, order.Length);
        Assert.Equal('A', order[0]);
        Assert.DoesNotContain('A', order.Skip(1));
    }

    // ── ChooseAvailableKey ──────────────────────────────────────────

    [Fact]
    public void ChooseAvailableKey_PrefersSavedKeyWhenAvailable()
    {
        var selected = HotkeyService.ChooseAvailableKey(['A', 'M', 'Z'], "M");
        Assert.Equal('M', selected);
    }

    [Fact]
    public void ChooseAvailableKey_FallsBackToFirstAvailableKey()
    {
        var selected = HotkeyService.ChooseAvailableKey(['B', 'C'], "M");
        Assert.Equal('B', selected);
    }

    [Fact]
    public void ChooseAvailableKey_EmptyList_ReturnsNull()
    {
        Assert.Null(HotkeyService.ChooseAvailableKey(Array.Empty<char>(), "A"));
    }

    [Fact]
    public void ChooseAvailableKey_NullPreferred_ReturnsFirst()
    {
        Assert.Equal('X', HotkeyService.ChooseAvailableKey(['X', 'Y'], null));
    }

    // ── FormatCtrlShift ─────────────────────────────────────────────

    [Fact]
    public void FormatCtrlShift_UsesNormalizedDisplayText()
    {
        Assert.Equal("Ctrl+Shift+A", HotkeyService.FormatCtrlShift('a'));
    }

    [Fact]
    public void FormatCtrlShift_ThrowsForNonLetter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyService.FormatCtrlShift('1'));
    }

    // ── VirtualKeyFromLetter ────────────────────────────────────────

    [Fact]
    public void VirtualKeyFromLetter_ReturnsCorrectCode()
    {
        Assert.Equal(0x41u, HotkeyService.VirtualKeyFromLetter('A'));
        Assert.Equal(0x5Au, HotkeyService.VirtualKeyFromLetter('z'));
    }

    [Fact]
    public void VirtualKeyFromLetter_ThrowsForNonLetter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyService.VirtualKeyFromLetter('0'));
    }

    // ── OnWmHotkey ──────────────────────────────────────────────────

    [Fact]
    public void OnWmHotkey_MatchingId_RaisesPressed()
    {
        bool fired = false;
        _sut.Pressed += () => fired = true;

        _sut.OnWmHotkey(0x1001); // HotkeyId

        Assert.True(fired);
    }

    [Fact]
    public void OnWmHotkey_NonMatchingId_DoesNotRaise()
    {
        bool fired = false;
        _sut.Pressed += () => fired = true;

        _sut.OnWmHotkey(0x9999);

        Assert.False(fired);
    }

    [Fact]
    public void OnWmHotkey_NoSubscribers_DoesNotThrow()
    {
        _sut.OnWmHotkey(0x1001);
    }

    // ── Initial State / Unregister / Dispose (no P/Invoke needed) ───

    [Fact]
    public void InitialState_NotRegistered()
    {
        Assert.False(_sut.IsRegistered);
        Assert.Null(_sut.RegisteredKey);
    }

    [Fact]
    public void Unregister_WhenNotRegistered_IsNoOp()
    {
        _sut.Unregister();
        Assert.False(_sut.IsRegistered);
    }

    [Fact]
    public void Dispose_WhenNotRegistered_IsNoOp()
    {
        _sut.Dispose();
        Assert.False(_sut.IsRegistered);
    }

    // ── Register (P/Invoke — message-only window) ───────────────────

    [Fact]
    public void Register_WithValidHwndAndLetter_Succeeds()
    {
        if (!HasTestWindow) return;

        Assert.True(_sut.Register(_testHwnd, 'Q'));
        Assert.True(_sut.IsRegistered);
        Assert.Equal('Q', _sut.RegisteredKey);
    }

    [Fact]
    public void Register_WithModifiersAndVk_SetsRegisteredKey()
    {
        if (!HasTestWindow) return;

        Assert.True(_sut.Register(_testHwnd, HotkeyService.CtrlShiftModifiers, 0x42));
        Assert.True(_sut.IsRegistered);
        Assert.Equal('B', _sut.RegisteredKey);
    }

    [Fact]
    public void Register_InvalidHwnd_FailsAndClearsState()
    {
        var invalidHwnd = new IntPtr(1);

        Assert.False(_sut.Register(invalidHwnd, 'A'));
        Assert.False(_sut.IsRegistered);
        Assert.Null(_sut.RegisteredKey);
    }

    [Fact]
    public void Register_NonCtrlShiftModifiers_RegisteredKeyIsNull()
    {
        if (!HasTestWindow) return;

        // MOD_ALT + letter — OS may accept it, but LetterFromVirtualKey returns null
        var result = _sut.Register(_testHwnd, HotkeyService.ModAlt, 0x41);
        if (result)
            Assert.Null(_sut.RegisteredKey);
    }

    [Fact]
    public void Register_VkOutOfLetterRange_RegisteredKeyIsNull()
    {
        if (!HasTestWindow) return;

        // CtrlShift + numeric key 0x30 — LetterFromVirtualKey returns null for vk outside A-Z
        var result = _sut.Register(_testHwnd, HotkeyService.CtrlShiftModifiers, 0x30);
        if (result)
            Assert.Null(_sut.RegisteredKey);
    }

    [Fact]
    public void Register_CalledTwice_UnregistersFirst()
    {
        if (!HasTestWindow) return;

        Assert.True(_sut.Register(_testHwnd, 'Q'));
        Assert.True(_sut.Register(_testHwnd, 'R'));
        Assert.True(_sut.IsRegistered);
        Assert.Equal('R', _sut.RegisteredKey);
    }

    [Fact]
    public void Unregister_AfterRegister_ClearsState()
    {
        if (!HasTestWindow) return;

        _sut.Register(_testHwnd, 'Q');
        _sut.Unregister();

        Assert.False(_sut.IsRegistered);
        Assert.Null(_sut.RegisteredKey);
    }

    [Fact]
    public void Dispose_AfterRegister_Unregisters()
    {
        if (!HasTestWindow) return;

        _sut.Register(_testHwnd, 'Q');
        _sut.Dispose();

        Assert.False(_sut.IsRegistered);
        Assert.Null(_sut.RegisteredKey);
    }

    // ── TryRegisterFirstAvailableCtrlShift ──────────────────────────

    [Fact]
    public void TryRegisterFirstAvailable_FindsKey()
    {
        if (!HasTestWindow) return;

        Assert.True(_sut.TryRegisterFirstAvailableCtrlShift(_testHwnd, "Q", out var selected));
        Assert.True(_sut.IsRegistered);
        Assert.True(selected is >= 'A' and <= 'Z');
    }

    [Fact]
    public void TryRegisterFirstAvailable_NullPreferred_StillFindsKey()
    {
        if (!HasTestWindow) return;

        Assert.True(_sut.TryRegisterFirstAvailableCtrlShift(_testHwnd, null, out var selected));
        Assert.True(selected is >= 'A' and <= 'Z');
    }

    [Fact]
    public void TryRegisterFirstAvailable_AllFail_ReturnsFalseAndNullChar()
    {
        var invalidHwnd = new IntPtr(1);

        Assert.False(_sut.TryRegisterFirstAvailableCtrlShift(invalidHwnd, "A", out var selected));
        Assert.Equal('\0', selected);
        Assert.False(_sut.IsRegistered);
    }

    // ── GetAvailableCtrlShiftLetterKeys ─────────────────────────────

    [Fact]
    public void GetAvailableKeys_ReturnsNonEmptyList()
    {
        if (!HasTestWindow) return;

        var keys = _sut.GetAvailableCtrlShiftLetterKeys(_testHwnd);
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(k is >= 'A' and <= 'Z'));
    }

    [Fact]
    public void GetAvailableKeys_IncludesAlreadyRegisteredKey()
    {
        if (!HasTestWindow) return;

        _sut.Register(_testHwnd, 'Q');
        var keys = _sut.GetAvailableCtrlShiftLetterKeys(_testHwnd);

        Assert.Contains('Q', keys);
    }

    [Fact]
    public void GetAvailableKeys_ZeroHwnd_ReturnsEmpty()
    {
        var keys = _sut.GetAvailableCtrlShiftLetterKeys(IntPtr.Zero);
        Assert.Empty(keys);
    }

    [Fact]
    public void GetAvailableKeys_DifferentHwnd_ProbesInsteadOfShortCircuiting()
    {
        if (!HasTestWindow) return;

        // Register on _testHwnd, then query with a second window
        _sut.Register(_testHwnd, 'Q');
        var hwnd2 = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (hwnd2 == IntPtr.Zero) return;
            // _registered is true, but _hwnd != hwnd2 → falls through to probe branch
            var keys = _sut.GetAvailableCtrlShiftLetterKeys(hwnd2);
            Assert.All(keys, k => Assert.True(k is >= 'A' and <= 'Z'));
        }
        finally
        {
            if (hwnd2 != IntPtr.Zero)
                DestroyWindow(hwnd2);
        }
    }

    [Fact]
    public void GetAvailableKeys_RegisteredKeyNull_FallsThroughToProbe()
    {
        if (!HasTestWindow) return;

        // Register with CtrlShift + VK outside letter range → RegisteredKey is null
        // but _registered is true and _hwnd matches
        var result = _sut.Register(_testHwnd, HotkeyService.CtrlShiftModifiers, 0x30);
        if (!result) return;

        Assert.Null(_sut.RegisteredKey);
        // Now query same hwnd: _registered && _hwnd == hwnd is true,
        // but RegisteredKey (null) == key is false for all keys → probes each one
        var keys = _sut.GetAvailableCtrlShiftLetterKeys(_testHwnd);
        Assert.All(keys, k => Assert.True(k is >= 'A' and <= 'Z'));
    }
}
