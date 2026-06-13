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

    /// <summary>Window pin state inside the compact launcher: how the window reacts to losing focus.
    /// Default is <see cref="StayOpen"/>. <see cref="FullWindow"/> is the in-session escape hatch
    /// from launcher mode and is also reached via the <c>Start in compact launcher mode</c> setting.</summary>
    private enum PinState { MinimizeToTray, StayOpen, AlwaysOnTop, FullWindow }

    private PinState _pinState = PinState.StayOpen;

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

        // Apply saved window-focus behaviour as the initial pin state. WindowFocusBehavior only
        // governs the launcher's response to focus loss; launcher-vs-traditional startup is
        // controlled by the separate StartInLauncherMode setting.
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

            // Force a layout pass so DesiredSize reflects only the currently
            // visible rows (admin banner + search card), then measure.
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
            int x = wa.X + Math.Max(0, (wa.Width - width) / 2);
            int y = wa.Y + (int)(4 * scale);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

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
                    RootGrid.Measure(new Windows.Foundation.Size(1400, double.PositiveInfinity));
                    double h = RootGrid.DesiredSize.Height;
                    if (h < MinimumLauncherHeightDip) h = MinimumLauncherHeightDip;
                    int newHeight = (int)((h + 2) * deferredScale) + chrome;
                    if (Math.Abs(newHeight - AppWindow.Size.Height) < 4) return;
                    int newY = wa.Y + (int)(4 * deferredScale);
                    int newWidth = (int)(1400 * deferredScale);
                    int newX = wa.X + Math.Max(0, (wa.Width - newWidth) / 2);
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(newX, newY, newWidth, newHeight));
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
            int desiredHeight = (int)(800 * scale);
            int maxHeight = Math.Max(0, wa.Y + wa.Height - curY);
            int newHeight = Math.Min(desiredHeight, maxHeight);
            if (newHeight < AppWindow.Size.Height) newHeight = AppWindow.Size.Height;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(curX, curY, curWidth, newHeight));
        }
        catch { }
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
                PinIcon.Glyph = "\uE77A";
                ToolTipService.SetToolTip(PinButton, "Minimize to system tray when window loses focus");
                SetAlwaysOnTop(false);
                RestoreToLauncherChrome();
                break;
            case PinState.StayOpen:
                PinIcon.Glyph = "\uE840";
                ToolTipService.SetToolTip(PinButton, "Window stays open (won't minimize to tray)");
                SetAlwaysOnTop(false);
                RestoreToLauncherChrome();
                break;
            case PinState.AlwaysOnTop:
                PinIcon.Glyph = "\uE72E";
                ToolTipService.SetToolTip(PinButton, "Window stays on top of all other windows");
                SetAlwaysOnTop(true);
                RestoreToLauncherChrome();
                break;
            case PinState.FullWindow:
                PinIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(PinButton, "Traditional window with title bar (click to return to launcher)");
                SetAlwaysOnTop(false);
                SwitchToFullWindow();
                break;
        }
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
        if (args.WindowActivationState == WindowActivationState.Deactivated && _pinState == PinState.MinimizeToTray)
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
        HideToTray(isCloseToTray: true);
    }

    private void HideToTray(bool isCloseToTray = false)
    {
        if (_hwnd == IntPtr.Zero) return;

        bool firstDock = false;
        bool firstCloseToTray = false;

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

        if (isCloseToTray)
        {
            var settings = new SettingsService().Load();
            firstCloseToTray = !settings.HasShownCloseToTrayNotification;
        }

        ShowWindow(_hwnd, SW_HIDE);

        if (firstCloseToTray)
        {
            var msg = "Yagu docked to the system tray instead of closing. You can change this in Settings \u2192 Window.";
            if (ViewModel.GlobalHotkeyEnabled && _hotkeyService.IsRegistered)
                msg += $" Press {HotkeyService.FormatCtrlShift(ViewModel.GlobalHotkeyKey[0])} to restore.";
            _trayIcon!.ShowBalloon("Yagu is still running", msg);

            var svc = new SettingsService();
            var s = svc.Load();
            s.HasShownCloseToTrayNotification = true;
            _ = svc.SaveAsync(s);
        }
        else if (firstDock)
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
