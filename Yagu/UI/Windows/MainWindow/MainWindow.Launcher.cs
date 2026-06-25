using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Launcher mode, pin-state cycling, global hotkey, window chrome,
/// and system-tray lifecycle.
/// </summary>
public sealed partial class MainWindow
{
    private const double MinimumLauncherHeightDip = 190;
    private const double DefaultSearchResultsWindowHeightDip = 900;

    /// <summary>Small bottom margin (DIPs) kept below the fully expanded Advanced Options drawer
    /// when the traditional window is grown to fit it, so the drawer isn't clipped but the window
    /// doesn't overshoot and reveal the top of the results/preview pane. The startup-collapsed
    /// case is held up by <see cref="DefaultSearchResultsWindowHeightDip"/> instead.</summary>
    private const double TraditionalDrawerBottomMarginDip = 16;

    /// <summary>Window pin state inside the compact launcher: how the window reacts to losing focus.
    /// Default is <see cref="StayOpen"/>. <see cref="FullWindow"/> is the in-session escape hatch
    /// from launcher mode and is also reached via the <c>Start in compact launcher mode</c> setting.</summary>
    private enum PinState { MinimizeToTray, StayOpen, AlwaysOnTop, FullWindow }

    private PinState _pinState = PinState.StayOpen;

    /// <summary>Active focus-loss behavior (0 = minimize to tray, 1 = stay open, 2 = always on top),
    /// applied to the window in EVERY mode (compact launcher AND traditional window) — not just the
    /// launcher. Seeded from the saved <c>WindowFocusBehavior</c> setting at startup, updated live when
    /// the setting changes, and overridable per session via the launcher pin button. Kept separate from
    /// <see cref="_pinState"/> (whose <see cref="PinState.FullWindow"/> value is a window-MODE switch,
    /// not a focus behavior) so switching to the full window doesn't discard the configured behavior.</summary>
    private int _focusLossBehavior = 1;

    /// <summary>
    /// Compact "launcher" mode: hides the results panel and status bar,
    /// switches to a borderless small window centered at the top of the screen.
    /// </summary>
    private void EnterLauncherMode()
    {
        if (_launcherMode) return;
        _launcherMode = true;

        TitleBarRow.Height = GridLength.Auto;
        SplitPaneRow.Height = new GridLength(0);
        ProgressRow.Height = new GridLength(0);
        AppTitleBar.Visibility = Visibility.Visible;
        SplitPaneGrid.Visibility = Visibility.Collapsed;
        UpdateBottomStatusBarVisibility();

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch { }

        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, false);
                op.IsResizable = true;
                op.IsMaximizable = false;
                op.IsMinimizable = false;
                ApplyTitleBarButtonTheme();
            }
        }
        catch { }
        SetNativeCaptionButtonsVisible(false);

        // Apply saved window-focus behaviour as the initial pin state. WindowFocusBehavior governs the
        // window's response to focus loss in EVERY mode (tracked by _focusLossBehavior, applied via
        // ApplyPinState below); launcher-vs-traditional startup is controlled by the separate
        // StartInLauncherMode setting.
        _pinState = ViewModel.WindowFocusBehavior switch
        {
            0 => PinState.MinimizeToTray,
            2 => PinState.AlwaysOnTop,
            _ => PinState.StayOpen,
        };
        ApplyPinState();
    }

    private void PositionLauncherWindow()
    {
        try
        {
            if (AppWindow is null) return;
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;
            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;

            // Measure the natural content height with the Advanced Options drawer
            // unbounded so the window grows to fully cover the expanded panel when
            // the work area has room. The scroll bound is (re)applied afterward via
            // UpdateAdvancedOptionsDrawerMaxHeight so a drawer taller than the work
            // area still scrolls internally instead of being clipped.
            if (AdvancedOptionsScrollViewer is not null)
                AdvancedOptionsScrollViewer.MaxHeight = double.PositiveInfinity;
            RootGrid.UpdateLayout();
            RootGrid.Measure(new Windows.Foundation.Size(1400, double.PositiveInfinity));
            double desiredHeightDip = RootGrid.DesiredSize.Height;
            if (desiredHeightDip < MinimumLauncherHeightDip) desiredHeightDip = MinimumLauncherHeightDip;

            // Non-client chrome (border/resize grip) eats into the outer rect.
            // AppWindow.Size vs ClientSize may not be updated synchronously after
            // SetBorderAndTitleBar, so query the Win32 frame metrics directly.
            int chromeHeight = 0;
            try
            {
                int dpi = GetDpiForWindow(_hwnd);
                int frameY = GetSystemMetricsForDpi(33 /* SM_CYFRAME */, (uint)dpi);
                int padded = GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, (uint)dpi);
                chromeHeight = (frameY + padded) * 2; // top + bottom border
            }
            catch { }

            LogService.Instance.Info("Launcher", $"PositionLauncherWindow: desiredH={desiredHeightDip:F1} dip, scale={scale:F2}, chromeH={chromeHeight}px, outer={AppWindow.Size.Height}, client={AppWindow.ClientSize.Height}");

            int width = (int)(1400 * scale);
            int height = (int)((desiredHeightDip + 2) * scale) + chromeHeight;
            if (height > wa.Height) height = wa.Height; // never extend past the work area
            (int x, int y) = ComputeLauncherPosition(wa, width, height, scale);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

            // Apply the scroll bound now that the window has its final height so a
            // drawer taller than the work area scrolls instead of clipping.
            RootGrid.UpdateLayout();
            UpdateAdvancedOptionsDrawerMaxHeight();

            // Re-fit once after layout has fully settled (admin banner can wrap
            // and the actual height becomes accurate only on a later tick).
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_launcherMode) return;
                try
                {
                    double deferredScale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
                    int chrome = 0;
                    try
                    {
                        int dpi2 = GetDpiForWindow(_hwnd);
                        int fy2 = GetSystemMetricsForDpi(33, (uint)dpi2);
                        int pd2 = GetSystemMetricsForDpi(92, (uint)dpi2);
                        chrome = (fy2 + pd2) * 2;
                    }
                    catch { }

                    RootGrid.UpdateLayout();
                    if (AdvancedOptionsScrollViewer is not null)
                        AdvancedOptionsScrollViewer.MaxHeight = double.PositiveInfinity;
                    RootGrid.UpdateLayout();
                    RootGrid.Measure(new Windows.Foundation.Size(1400, double.PositiveInfinity));
                    double h = RootGrid.DesiredSize.Height;
                    if (h < MinimumLauncherHeightDip) h = MinimumLauncherHeightDip;
                    int newHeight = (int)((h + 2) * deferredScale) + chrome;
                    if (newHeight > wa.Height) newHeight = wa.Height;
                    if (Math.Abs(newHeight - AppWindow.Size.Height) < 4)
                    {
                        UpdateAdvancedOptionsDrawerMaxHeight();
                        return;
                    }
                    int newWidth = (int)(1400 * deferredScale);
                    (int newX, int newY) = ComputeLauncherPosition(wa, newWidth, newHeight, deferredScale);
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(newX, newY, newWidth, newHeight));
                    RootGrid.UpdateLayout();
                    UpdateAdvancedOptionsDrawerMaxHeight();
                    LogService.Instance.Info("Launcher", $"PositionLauncherWindow (deferred): h={h:F1} dip, scale={deferredScale:F2}, chrome={chrome}px, newHeight={newHeight}px");
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>
    /// Restores the full layout while keeping the bottom status bar governed
    /// by split-panel visibility. The window grows downward in place.
    /// </summary>
    private void ExitLauncherMode()
    {
        if (!_launcherMode) return;
        _launcherMode = false;

        SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
        ProgressRow.Height = GridLength.Auto;
        SplitPaneGrid.Visibility = Visibility.Visible;
        _resultsPaneCollapsed = false;
        UpdateBottomStatusBarVisibility();

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch { }
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, true);
                op.IsResizable = true;
                op.IsMaximizable = true;
                op.IsMinimizable = true;
                ApplyTitleBarButtonTheme();
            }
        }
        catch { }
        SetNativeCaptionButtonsVisible(true);

        try
        {
            if (AppWindow is null) return;
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;
            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;

            int curX = AppWindow.Position.X;
            int curY = AppWindow.Position.Y;
            int curWidth = AppWindow.Size.Width;
            int desiredHeight = (int)(DefaultSearchResultsWindowHeightDip * scale);
            int maxHeight = Math.Max(0, wa.Y + wa.Height - curY);
            int newHeight = Math.Min(desiredHeight, maxHeight);
            if (newHeight < AppWindow.Size.Height) newHeight = AppWindow.Size.Height;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(curX, curY, curWidth, newHeight));
        }
        catch { }

        // Now that the results pane is restored, host the Advanced Options drawer in the
        // floating overlay so expanding it overlays the results instead of reflowing.
        MoveAdvancedOptionsDrawerToOverlay();
    }

    /// <summary>
    /// Grows the traditional (non-launcher) window so the entire Advanced Options drawer is
    /// visible instead of being clipped to a scrolling region. Used both at startup and whenever
    /// the drawer is expanded. Measures the natural content height with the drawer unbounded,
    /// reserves room for the results/preview split pane, and clamps to the monitor work area.
    /// Only ever grows the window (never shrinks).
    /// </summary>
    private void FitTraditionalWindowHeightToContent()
    {
        try
        {
            if (AppWindow is null || _launcherMode) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized })
                return;

            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            if (scale <= 0) scale = 1.0;

            // Measure with the Advanced Options drawer unbounded so the fully expanded panel
            // contributes its natural height. The scroll bound is reapplied afterward via
            // UpdateAdvancedOptionsDrawerMaxHeight so a drawer taller than the work area still
            // scrolls internally instead of being clipped.
            bool restoreDrawerBound = false;
            if (AdvancedOptionsScrollViewer is not null && AdvancedOptionsExpander.IsExpanded)
            {
                AdvancedOptionsScrollViewer.MaxHeight = double.PositiveInfinity;
                restoreDrawerBound = true;
            }

            double measureWidthDip = AppWindow.ClientSize.Width > 0
                ? AppWindow.ClientSize.Width / scale
                : Math.Max(1, RootGrid.ActualWidth);

            RootGrid.UpdateLayout();
            RootGrid.Measure(new Windows.Foundation.Size(measureWidthDip, double.PositiveInfinity));

            // The split pane is a star row that measures to ~0 with no results yet, so the
            // measured content is essentially the title bar + search card + expanded drawer.
            // Add a small margin so the drawer's last row isn't flush against the window edge.
            double desiredDip = RootGrid.DesiredSize.Height + TraditionalDrawerBottomMarginDip;

            int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            int desiredHeight = (int)Math.Ceiling((desiredDip + 2) * scale) + chromeHeight;

            // Floor at the standard results-window height so short content still opens roomy.
            desiredHeight = Math.Max(desiredHeight, (int)(DefaultSearchResultsWindowHeightDip * scale));

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var wa = displayArea?.WorkArea ?? default;
            if (wa.Height > 0)
            {
                int maxHeight = Math.Max(0, wa.Y + wa.Height - AppWindow.Position.Y);
                if (maxHeight > 0) desiredHeight = Math.Min(desiredHeight, maxHeight);
            }

            if (desiredHeight > AppWindow.Size.Height)
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, desiredHeight));
            }

            if (restoreDrawerBound)
            {
                RootGrid.UpdateLayout();
                UpdateAdvancedOptionsDrawerMaxHeight();
            }
        }
        catch { }
    }

    /// <summary>
    /// Places the traditional (non-launcher) window on screen at launch according to the user's
    /// <c>LaunchWindowPosition</c> setting (0 = Centered default, 1..8 = the eight edge/corner
    /// anchors). Keeps the window's current size and clamps it fully within the monitor work area.
    /// No-op while maximized or in compact launcher mode (the launcher docks top-center via
    /// <see cref="PositionLauncherWindow"/>).
    /// </summary>
    private void PositionWindowOnLaunch()
    {
        try
        {
            if (AppWindow is null || _launcherMode) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized })
                return;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;
            if (wa.Width <= 0 || wa.Height <= 0) return;

            int w = AppWindow.Size.Width;
            int h = AppWindow.Size.Height;

            // col: 0 = left, 1 = horizontal center, 2 = right.
            // row: 0 = top,  1 = vertical center,   2 = bottom.
            (int col, int row) = LaunchPositionToAnchors(ViewModel.LaunchWindowPosition);

            int x = col switch
            {
                0 => wa.X,
                2 => wa.X + wa.Width - w,
                _ => wa.X + (wa.Width - w) / 2,
            };
            int y = row switch
            {
                0 => wa.Y,
                2 => wa.Y + wa.Height - h,
                _ => wa.Y + (wa.Height - h) / 2,
            };

            // Clamp fully on-screen; a window larger than the work area pins to the top/left edge.
            x = Math.Max(wa.X, Math.Min(x, wa.X + Math.Max(0, wa.Width - w)));
            y = Math.Max(wa.Y, Math.Min(y, wa.Y + Math.Max(0, wa.Height - h)));

            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { }
    }

    /// <summary>Maps a <c>LaunchWindowPosition</c> index to (column, row) anchors where 0/1/2 mean
    /// left/center/right and top/center/bottom respectively. Out-of-range falls back to Centered.</summary>
    private static (int col, int row) LaunchPositionToAnchors(int position) => position switch
    {
        1 => (0, 0), // Top Left
        2 => (1, 0), // Top Middle
        3 => (2, 0), // Top Right
        4 => (0, 1), // Middle Left
        5 => (2, 1), // Middle Right
        6 => (0, 2), // Bottom Left
        7 => (1, 2), // Bottom Middle
        8 => (2, 2), // Bottom Right
        _ => (1, 1), // Centered (default)
    };

    /// <summary>Computes the compact launcher's top-left placement within the work area for its
    /// configured <c>LauncherWindowPosition</c> anchor, keeping a small edge margin and clamping the
    /// window fully on-screen. Top Middle (the default) reproduces the classic top-center dock.</summary>
    private (int x, int y) ComputeLauncherPosition(Windows.Graphics.RectInt32 wa, int width, int height, double scale)
    {
        (int col, int row) = LaunchPositionToAnchors(ViewModel.LauncherWindowPosition);
        int margin = (int)(4 * scale);

        int x = col switch
        {
            0 => wa.X + margin,
            2 => wa.X + wa.Width - width - margin,
            _ => wa.X + Math.Max(0, (wa.Width - width) / 2),
        };
        int y = row switch
        {
            0 => wa.Y + margin,
            2 => wa.Y + wa.Height - height - margin,
            _ => wa.Y + Math.Max(0, (wa.Height - height) / 2),
        };

        // Clamp fully on-screen; a window larger than the work area pins to the top/left edge.
        x = Math.Max(wa.X, Math.Min(x, wa.X + Math.Max(0, wa.Width - width)));
        y = Math.Max(wa.Y, Math.Min(y, wa.Y + Math.Max(0, wa.Height - height)));
        return (x, y);
    }

    private void InitializeGlobalHotkey()
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_hwnd == IntPtr.Zero)
        {
            LogService.Instance.Warning("Hotkey", "Could not initialize global hotkey: window handle was not available");
            return;
        }

        _hotkeySubclassProc = HotkeyWindowProc;
        _hotkeyHookInstalled = SetWindowSubclass(_hwnd, _hotkeySubclassProc, HotkeySubclassId, UIntPtr.Zero);
        if (!_hotkeyHookInstalled)
        {
            LogService.Instance.Warning("Hotkey", "Could not install global hotkey window hook");
            return;
        }

        _hotkeyService.Pressed += OnGlobalHotkeyPressed;
        ApplyGlobalHotkeyRegistration();
    }

    private void ApplyGlobalHotkeyRegistration()
    {
        if (!_hotkeyHookInstalled || _hwnd == IntPtr.Zero)
            return;

        if (!ViewModel.GlobalHotkeyEnabled)
        {
            _hotkeyService.Unregister();
            return;
        }

        if (_hotkeyService.TryRegisterFirstAvailableCtrlShift(_hwnd, ViewModel.GlobalHotkeyKey, out var selectedKey))
        {
            var selectedKeyText = selectedKey.ToString();
            if (!string.Equals(ViewModel.GlobalHotkeyKey, selectedKeyText, StringComparison.OrdinalIgnoreCase))
            {
                _suppressHotkeySettingChange = true;
                try { ViewModel.GlobalHotkeyKey = selectedKeyText; }
                finally { _suppressHotkeySettingChange = false; }
            }

            LogService.Instance.Info("Hotkey", $"Registered global hotkey {HotkeyService.FormatCtrlShift(selectedKey)}");
            return;
        }

        _hotkeyService.Unregister();
        ViewModel.StatusText = "No available Ctrl+Shift+letter global hotkeys were found.";
        LogService.Instance.Warning("Hotkey", "Could not register any Ctrl+Shift+letter global hotkey");
    }

    private IntPtr HotkeyWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo && TryApplyMaximizedWorkArea(hWnd, lParam))
            return IntPtr.Zero;

        if (IsHelpShortcutMessage(message, wParam))
        {
            OpenHelpWindow();
            return IntPtr.Zero;
        }

        if (message == HotkeyService.WmHotkey)
        {
            _hotkeyService.OnWmHotkey((int)wParam);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static bool IsHelpShortcutMessage(uint message, UIntPtr wParam)
        => message is WmKeyDown or WmSysKeyDown && wParam.ToUInt32() == VkF1;

    private static bool TryApplyMaximizedWorkArea(IntPtr hWnd, IntPtr lParam)
    {
        if (hWnd == IntPtr.Zero || lParam == IntPtr.Zero)
            return false;

        var monitorHandle = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
            return false;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
            return false;

        var monitorRect = monitorInfo.rcMonitor;
        var workRect = monitorInfo.rcWork;
        var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMaxInfo.ptMaxPosition.X = workRect.Left - monitorRect.Left;
        minMaxInfo.ptMaxPosition.Y = workRect.Top - monitorRect.Top;
        minMaxInfo.ptMaxSize.X = workRect.Right - workRect.Left;
        minMaxInfo.ptMaxSize.Y = workRect.Bottom - workRect.Top;
        minMaxInfo.ptMaxTrackSize.X = minMaxInfo.ptMaxSize.X;
        minMaxInfo.ptMaxTrackSize.Y = minMaxInfo.ptMaxSize.Y;
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
        return true;
    }

    private void OnGlobalHotkeyPressed()
    {
        DispatcherQueue.TryEnqueue(async () => await ResetToLauncherModeAsync());
    }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        _pinState = _pinState switch
        {
            PinState.MinimizeToTray => PinState.StayOpen,
            PinState.StayOpen => PinState.AlwaysOnTop,
            PinState.AlwaysOnTop => PinState.FullWindow,
            _ => PinState.MinimizeToTray,
        };
        ApplyPinState();
    }

    private void SetAlwaysOnTop(bool onTop)
    {
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                op.IsAlwaysOnTop = onTop;
        }
        catch { }
    }

    private void ApplyPinState()
    {
        switch (_pinState)
        {
            case PinState.MinimizeToTray:
                _focusLossBehavior = 0;
                PinIcon.Glyph = "\uE77A";
                ToolTipService.SetToolTip(PinButton, "Minimize to system tray when window loses focus");
                ApplyWindowFocusBehavior();
                RestoreToLauncherChrome();
                break;
            case PinState.StayOpen:
                _focusLossBehavior = 1;
                PinIcon.Glyph = "\uE840";
                ToolTipService.SetToolTip(PinButton, "Window stays open (won't minimize to tray)");
                ApplyWindowFocusBehavior();
                RestoreToLauncherChrome();
                break;
            case PinState.AlwaysOnTop:
                _focusLossBehavior = 2;
                PinIcon.Glyph = "\uE72E";
                ToolTipService.SetToolTip(PinButton, "Window stays on top of all other windows");
                ApplyWindowFocusBehavior();
                RestoreToLauncherChrome();
                break;
            case PinState.FullWindow:
                // Switching to the full traditional window reverts the focus-loss behavior to the saved
                // setting (the three launcher pin states are per-session overrides; the full window uses
                // the configured default). The behavior still applies — it is not tied to the mode.
                _focusLossBehavior = ViewModel.WindowFocusBehavior;
                PinIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(PinButton, "Traditional window with title bar (click to return to launcher)");
                ApplyWindowFocusBehavior();
                SwitchToFullWindow();
                break;
        }
    }

    /// <summary>Applies <see cref="_focusLossBehavior"/> to the window regardless of launcher vs
    /// traditional mode: "always on top" (2) pins the window above all others; the other behaviors clear
    /// it. Minimize-to-tray (0) is handled on deactivation in <see cref="OnWindowActivated"/>. Called at
    /// startup, when the WindowFocusBehavior setting changes, and on pin/mode transitions.</summary>
    private void ApplyWindowFocusBehavior()
    {
        SetAlwaysOnTop(_focusLossBehavior == 2);
    }

    /// <summary>Switch from compact launcher to a traditional window with title bar and all chrome.</summary>
    private void SwitchToFullWindow()
    {
        _launcherMode = false;

        TitleBarRow.Height = GridLength.Auto;
        AppTitleBar.Visibility = Visibility.Visible;

        SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
        ProgressRow.Height = GridLength.Auto;
        SplitPaneGrid.Visibility = Visibility.Visible;
        _resultsPaneCollapsed = false;
        UpdateBottomStatusBarVisibility();

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch { }
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, true);
                op.IsResizable = true;
                op.IsMaximizable = true;
                op.IsMinimizable = true;
                ApplyTitleBarButtonTheme();
            }
        }
        catch { }
        SetNativeCaptionButtonsVisible(true);

        try
        {
            if (AppWindow is null) return;
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;

            int width = Math.Max((int)(wa.Width * 0.85), 1400);
            if (width > wa.Width) width = wa.Width;
            int height = Math.Max((int)(wa.Height * 0.85), 900);
            if (height > wa.Height) height = wa.Height;
            int x = wa.X + Math.Max(0, (wa.Width - width) / 2);
            int y = wa.Y + Math.Max(0, (wa.Height - height) / 2);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        catch { }
    }

    /// <summary>Restore compact launcher chrome when leaving FullWindow state.</summary>
    private void RestoreToLauncherChrome()
    {
        _launcherMode = true;

        TitleBarRow.Height = GridLength.Auto;
        AppTitleBar.Visibility = Visibility.Visible;

        if (_resultsPaneCollapsed)
        {
            SplitPaneRow.Height = new GridLength(0);
            SplitPaneGrid.Visibility = Visibility.Collapsed;
        }
        ProgressRow.Height = _resultsPaneCollapsed ? new GridLength(0) : GridLength.Auto;
        UpdateBottomStatusBarVisibility();

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch { }
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, false);
                op.IsResizable = true;
                op.IsMaximizable = false;
                op.IsMinimizable = false;
                ApplyTitleBarButtonTheme();
            }
        }
        catch { }
        SetNativeCaptionButtonsVisible(false);

        PositionLauncherWindow();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // Focus-loss behavior is governed by the configured WindowFocusBehavior at ALL times (compact
        // launcher AND traditional window), tracked by _focusLossBehavior — not tied to launcher mode.
        if (args.WindowActivationState == WindowActivationState.Deactivated && _focusLossBehavior == 0)
        {
            if (HasOpenAppOwnedWindowOrModal()) return;
            HideToTray();
        }
    }

    private bool HasOpenAppOwnedWindowOrModal()
        => IsLauncherHideTemporarilySuppressed() || _settingsWindow is not null || _helpWindow is not null || _ownedModalWindowDepth > 0 || YaguDialog.HasOpenOwnedWindow(_hwnd);

    private void SuppressLauncherHideToTrayForOwnedWindowClose()
        => _suppressLauncherHideUntilUtc = DateTimeOffset.UtcNow.AddSeconds(10);

    private bool IsLauncherHideTemporarilySuppressed()
        => _suppressLauncherHideUntilUtc > DateTimeOffset.UtcNow;

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceClose || !ViewModel.CloseToTray) return;

        args.Cancel = true;
        RequestCloseToTray();
    }

    /// <summary>
    /// Entry point for the window/title-bar close action when "dock to tray" is enabled. The very
    /// first time, shows an explanatory dialog (with a "Don't remind me again" option and a way to
    /// switch to fully exiting) before docking; afterwards it docks silently.
    /// </summary>
    private void RequestCloseToTray()
    {
        if (!ViewModel.HasShownCloseToTrayNotification)
        {
            // Defer so the originating Closing event can return (with Cancel = true) before the
            // modal dialog opens over the still-visible window.
            DispatcherQueue.TryEnqueue(async () => await ShowFirstCloseToTrayDialogAsync());
            return;
        }

        HideToTray();
    }

    private async Task ShowFirstCloseToTrayDialogAsync()
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "Closing the window doesn't quit Yagu \u2014 it keeps running in the system tray so you can reopen it instantly.",
            TextWrapping = TextWrapping.Wrap,
        });

        var hint = "Right-click the tray icon to bring Yagu back or exit it completely.";
        if (ViewModel.GlobalHotkeyEnabled && _hotkeyService.IsRegistered)
            hint += $" You can also press {HotkeyService.FormatCtrlShift(ViewModel.GlobalHotkeyKey[0])} to restore it.";
        hint += " You can change this anytime in Settings \u2192 Window.";
        panel.Children.Add(new TextBlock
        {
            Text = hint,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        });

        var dontRemind = new CheckBox { Content = "Don't remind me again", IsChecked = true };
        panel.Children.Add(dontRemind);

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Yagu keeps running in the tray",
                Content = panel,
                PrimaryButtonText = "Got it, keep in tray",
                SecondaryButtonText = "Exit fully from now on",
                CloseButtonText = null,
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 600,
                Height = 320,
                MaxContentHeight = 220,
            });

        if (dontRemind.IsChecked == true)
        {
            ViewModel.HasShownCloseToTrayNotification = true;
            await ViewModel.PersistSettingsAsync();
        }

        if (result == YaguDialogResult.Secondary)
        {
            // The user chose to change the close behavior to fully exit; honor it now and onward.
            ViewModel.CloseToTray = false;
            await ViewModel.PersistSettingsAsync();
            _forceClose = true;
            Close();
            return;
        }

        // Primary or dismissed: dock to tray. The dialog already explained the tray, so suppress
        // the redundant first-dock balloon.
        MarkTrayNotificationShown();
        HideToTray();
    }

    private void HideToTray()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool firstDock = false;

        if (_trayIcon is null)
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
            _trayIcon = new Helpers.TrayIcon("Yagu", icoPath);
            _trayIcon.OpenResetRequested += () =>
            {
                DispatcherQueue.TryEnqueue(async () => await ResetToLauncherModeAsync());
            };
            _trayIcon.OpenExistingRequested += () =>
            {
                DispatcherQueue.TryEnqueue(() => RestoreWindowFromTray());
            };
            _trayIcon.CloseRequested += () =>
            {
                _forceClose = true;
                DispatcherQueue.TryEnqueue(() => Close());
            };
            firstDock = !HasShownTrayNotification();
        }

        ShowWindow(_hwnd, SW_HIDE);

        if (firstDock)
        {
            var msg = "Yagu is still running in the system tray. Right-click the tray icon to reopen or close it.";
            if (ViewModel.GlobalHotkeyEnabled && _hotkeyService.IsRegistered)
                msg += $" You can also press {HotkeyService.FormatCtrlShift(ViewModel.GlobalHotkeyKey[0])} to restore it.";
            _trayIcon!.ShowBalloon("Yagu minimized to tray", msg);
            MarkTrayNotificationShown();
        }
    }

    private void RestoreWindowFromTray()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
        }
        Activate();
        FocusSearchBox(suppressSuggestions: true);
    }

    public void FocusSearchOnLaunch()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
        }

        Activate();
        FocusSearchBox();
    }

    private static string TrayNotificationFlagPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yagu", ".tray-notified");

    private static bool HasShownTrayNotification() => File.Exists(TrayNotificationFlagPath);

    private static void MarkTrayNotificationShown()
    {
        try
        {
            var dir = Path.GetDirectoryName(TrayNotificationFlagPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(TrayNotificationFlagPath, "1");
        }
        catch { }
    }

    private async Task ResetToLauncherModeAsync()
    {
        if (ViewModel.IsSearching)
            await ViewModel.CancelAsync();

        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        PreviewSectionsPanel.Children.Clear();
        FullFileButton.IsEnabled = true;
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        CompletePreviewContentUpdate();
        _currentMatchIndex = -1;
        HideMatchNavPanel();
        ViewModel.ResultGroups.Clear();
        ViewModel.StatusText = string.Empty;
        ViewModel.Query = string.Empty;

        EnterLauncherMode();

        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
        }

        Activate();
        PositionLauncherWindow();
        FocusSearchBox();
    }

    private void RemoveGlobalHotkeyHook()
    {
        if (_hotkeyHookInstalled && _hwnd != IntPtr.Zero && _hotkeySubclassProc is not null)
            RemoveWindowSubclass(_hwnd, _hotkeySubclassProc, HotkeySubclassId);

        _hotkeyHookInstalled = false;
        _hotkeySubclassProc = null;
        _hwnd = IntPtr.Zero;
    }
}
