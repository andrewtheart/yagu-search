using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Net.Http;
using System.Security.Principal;
using Microsoft.Win32;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
using Yagu.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Yagu;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private bool _suppressPreviewUpdate;
    private int _previewUpdateGen;
    private int _lastCheckedGroupIndex = -1;
    private bool _autoScrollEnabled = false;
    // Deferred-files state — for tail of newFiles past PreviewSectionPageSize.
    // The match-nav label includes deferred matches even though their sections
    // are not yet inserted in the visual tree.
    private List<KeyValuePair<string, List<SearchResult>>>? _deferredOrderedFiles;
    private int _deferredCursor;
    private List<SearchResult>? _deferredAllSelected;
    private int _deferredGen;
    private StackPanel? _deferredButtonPanel;
    private bool _autoLoadMoreInFlight;
    private const int AutoLoadChunkSize = 5;
    private bool _querySuggestionsDetached;
    private long _hideSuggestionsTick;
    private DispatcherTimer? _autoScrollTimer;
    private const long FullFilePreviewLimitBytes = 1L * 1024 * 1024 * 1024;
    private CancellationTokenSource? _previewLoadCts;
    private string? _previewEditorPath;
    private Encoding? _previewEditorEncoding;
    private bool _previewEditorDirty;
    private string? _previewEditorOriginalText;
    private bool _suppressPreviewEditorTextChanged;
    private readonly HotkeyService _hotkeyService = new();
    private SubclassProc? _hotkeySubclassProc;
    private IntPtr _hwnd;
    private bool _hotkeyHookInstalled;
    private bool _suppressHotkeySettingChange;
    private Helpers.TrayIcon? _trayIcon;

    private static readonly UIntPtr HotkeySubclassId = new(0x5147484Bu);
    private const int SW_RESTORE = 9;
    private const int SW_HIDE = 0;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc subclassProc, UIntPtr subclassId, UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc subclassProc, UIntPtr subclassId);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    private bool _autoSearchOnLoad;
    private bool _launcherMode;

    public MainWindow(string? startupDirectory, string? startupQuery = null)
    {
        ViewModel = new MainViewModel();
        ViewModel.SetDirectoryFromArgs(startupDirectory);
        if (!string.IsNullOrWhiteSpace(startupQuery))
        {
            ViewModel.Query = startupQuery;
            _autoSearchOnLoad = !string.IsNullOrWhiteSpace(startupDirectory);
        }
        InitializeComponent();
        SyncLayoutToggles(ViewModel.PreviewModeIndex);
        Title = AppInfo.WindowTitle;

        // PreviewBlock's built-in text-selection swallows DoubleTapped by
        // marking it handled. AddHandler with handledEventsToo: true is the
        // only way to still receive it.
        PreviewBlock.AddHandler(UIElement.DoubleTappedEvent,
            new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(OnPreviewBlockDoubleTapped),
            handledEventsToo: true);

        // Extend content into the title bar for a modern Windows 11 look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTitleText.Text = AppInfo.WindowTitle;

        // Reserve room on the right for system caption buttons (min/max/close)
        // so the gear icon doesn't overlap with them.
        UpdateTitleBarInsets();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange) UpdateTitleBarInsets();
        };

        // Set the window icon (unpackaged WinUI 3 doesn't pick up ApplicationIcon automatically)
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
        if (File.Exists(icoPath))
            AppWindow.SetIcon(icoPath);

        InitializeGlobalHotkey();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.PreviewContextLines))
            {
                RefreshCurrentPreview();
            }

            if (!_suppressHotkeySettingChange &&
                (e.PropertyName == nameof(ViewModel.GlobalHotkeyEnabled) || e.PropertyName == nameof(ViewModel.GlobalHotkeyKey)))
            {
                ApplyGlobalHotkeyRegistration();
            }
        };

        ((FrameworkElement)Content).Loaded += OnContentLoaded;

        // Start in compact "launcher" mode unless we have a query to auto-run.
        if (!_autoSearchOnLoad)
        {
            EnterLauncherMode();
        }

        // Auto-scroll the file list as new results arrive (timer-based to avoid
        // per-Add overhead on the hot CollectionChanged path).
        ResultsList.PointerWheelChanged += (_, e) =>
        {
            if (e.GetCurrentPoint(ResultsList).Properties.MouseWheelDelta > 0)
                SetAutoScrollEnabled(false); // user scrolled up — stop auto-scroll
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModel.IsSearching)) return;
            if (ViewModel.IsSearching)
            {
                if (_launcherMode) ExitLauncherMode();
                SearchCancelIcon.Glyph = "\uE711";   // Cancel X
                SearchCancelLabel.Text = "Cancel";
                ToolTipService.SetToolTip(SearchCancelButton, "Cancel search (F5)");
                SearchCancelButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
                SearchCancelButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 220, 220));
                SearchCancelButton.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 80, 80));
                SearchCancelButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 24, 24));
                _autoScrollEnabled = AutoScrollResultsCheckBox.IsChecked == true;
                _autoScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _autoScrollTimer.Tick += OnAutoScrollTick;
                _autoScrollTimer.Start();
                ThroughputSparkline.Points.Clear();
                ThroughputSparkline.Opacity = 0.7;
            }
            else
            {
                SearchCancelIcon.Glyph = "\uE721";   // Search magnifier
                SearchCancelLabel.Text = "Search";
                ToolTipService.SetToolTip(SearchCancelButton, "Search (F5)");
                SearchCancelButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                SearchCancelButton.ClearValue(Control.BackgroundProperty);
                SearchCancelButton.ClearValue(Control.BorderBrushProperty);
                SearchCancelButton.ClearValue(Control.ForegroundProperty);
                _autoScrollTimer?.Stop();
                UpdateSparkline(); // final update
                ThroughputSparkline.Opacity = 0.35;
            }
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModel.FilesPerSecondText) or nameof(ViewModel.StatusText))
                UpdateSparkline();
        };

        // Hide to system tray when the window loses focus.
        this.Activated += OnWindowActivated;

        // Flush logs when the window closes so no diagnostic entries are lost.
        this.Closed += (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _hotkeyService.Dispose();
            RemoveGlobalHotkeyHook();
            LogService.Instance.Info("MainWindow", "Window closing — flushing logs");
            LogService.Instance.Flush();
        };

        // Show admin banner when not elevated
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator) && !ViewModel.SuppressAdminWarning)
            AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void UpdateTitleBarInsets()
    {
        try
        {
            var tb = AppWindow?.TitleBar;
            if (tb is null) return;
            // RightInset is in physical pixels; convert to DIPs.
            var scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            var rightDip = tb.RightInset / scale;
            // Ensure a minimum so the ? and gear icons are never clipped
            // when RightInset returns 0 before the window is fully rendered.
            if (rightDip < 48) rightDip = 148;
            AppTitleBar.Padding = new Thickness(16, 0, rightDip, 0);
        }
        catch { /* AppWindow not always available; ignore */ }
    }

    /// <summary>
    /// Compact "launcher" mode: hides the title bar, results panel, and status bar,
    /// switches to a borderless small window centered at the top of the screen.
    /// </summary>
    private void EnterLauncherMode()
    {
        if (_launcherMode) return;
        _launcherMode = true;

        TitleBarRow.Height = new GridLength(0);
        SplitPaneRow.Height = new GridLength(0);
        StatusBarRow.Height = new GridLength(0);
        AppTitleBar.Visibility = Visibility.Collapsed;
        SplitPaneGrid.Visibility = Visibility.Collapsed;

        try { ExtendsContentIntoTitleBar = false; } catch { }

        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, false);
                op.IsResizable = true;
                op.IsMaximizable = false;
                op.IsMinimizable = false;
            }
        }
        catch { }

        // Apply saved window-focus behaviour as the initial pin state.
        _pinState = ViewModel.WindowFocusBehavior switch
        {
            1 => PinState.StayOpen,
            2 => PinState.AlwaysOnTop,
            3 => PinState.FullWindow,
            _ => PinState.MinimizeToTray,
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
            if (desiredHeightDip < 60) desiredHeightDip = 170;

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
            int height = (int)(desiredHeightDip * scale) + chromeHeight;
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
                    // Re-query scale — on the first call XamlRoot may not be ready
                    // and scale captures as 1.0, producing a wrong pixel height.
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
                    if (h < 60) return;
                    int newHeight = (int)(h * deferredScale) + chrome;
                    // Compare against current actual window height, not the
                    // captured value (which may have been set with wrong scale).
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
    /// Restores the full layout (results panel + status bar) but keeps the
    /// borderless launcher chrome and current window position. The window
    /// just grows downward in place to make room for the results.
    /// </summary>
    private void ExitLauncherMode()
    {
        if (!_launcherMode) return;
        _launcherMode = false;

        SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
        StatusBarRow.Height = GridLength.Auto;
        SplitPaneGrid.Visibility = Visibility.Visible;

        // Grow the borderless window downward in place so the results panel
        // has room. Keep the current X/Y/width — do NOT recenter.
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
        if (message == HotkeyService.WM_HOTKEY)
        {
            _hotkeyService.OnWmHotkey((int)wParam);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void OnGlobalHotkeyPressed()
    {
        DispatcherQueue.TryEnqueue(async () => await ResetToLauncherModeAsync());
    }

    /// <summary>Window pin state: MinimizeToTray (default), StayOpen, AlwaysOnTop, FullWindow.</summary>
    private enum PinState { MinimizeToTray, StayOpen, AlwaysOnTop, FullWindow }

    private PinState _pinState = PinState.MinimizeToTray;

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
                PinIcon.Glyph = "\uE77A"; // UnPin
                ToolTipService.SetToolTip(PinButton, "Minimize to system tray when window loses focus");
                SetAlwaysOnTop(false);
                RestoreToLauncherChrome();
                break;
            case PinState.StayOpen:
                PinIcon.Glyph = "\uE840"; // Pin
                ToolTipService.SetToolTip(PinButton, "Window stays open (won't minimize to tray)");
                SetAlwaysOnTop(false);
                RestoreToLauncherChrome();
                break;
            case PinState.AlwaysOnTop:
                PinIcon.Glyph = "\uE72E"; // Lock
                ToolTipService.SetToolTip(PinButton, "Window stays on top of all other windows");
                SetAlwaysOnTop(true);
                RestoreToLauncherChrome();
                break;
            case PinState.FullWindow:
                PinIcon.Glyph = "\uE740"; // FullScreen / expand — traditional window
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

        // Show title bar row and app title bar
        TitleBarRow.Height = GridLength.Auto;
        AppTitleBar.Visibility = Visibility.Visible;

        // Show results pane and status bar
        SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
        StatusBarRow.Height = GridLength.Auto;
        SplitPaneGrid.Visibility = Visibility.Visible;

        try { ExtendsContentIntoTitleBar = true; } catch { }
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, true);
                op.IsResizable = true;
                op.IsMaximizable = true;
                op.IsMinimizable = true;
            }
        }
        catch { }

        // Resize to a comfortable traditional window size, using work area
        try
        {
            if (AppWindow is null) return;
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;

            // Use most of the work area for a proper traditional window
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

    /// <summary>Restore compact launcher chrome (borderless, no title bar) when leaving FullWindow state.</summary>
    private void RestoreToLauncherChrome()
    {
        _launcherMode = true;

        // If coming back from full window, hide the title bar and extra rows again
        TitleBarRow.Height = new GridLength(0);
        AppTitleBar.Visibility = Visibility.Collapsed;
        SplitPaneRow.Height = new GridLength(0);
        StatusBarRow.Height = new GridLength(0);
        SplitPaneGrid.Visibility = Visibility.Collapsed;

        try { ExtendsContentIntoTitleBar = false; } catch { }
        try
        {
            if (AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.SetBorderAndTitleBar(true, false);
                op.IsResizable = true;
                op.IsMaximizable = false;
                op.IsMinimizable = false;
            }
        }
        catch { }

        PositionLauncherWindow();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _pinState == PinState.MinimizeToTray)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool firstDock = false;

        // Lazily create the tray icon on first hide.
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
        FocusSearchBox();
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
        // Cancel any running search
        if (ViewModel.IsSearching)
            await ViewModel.CancelAsync();

        // Clear results and preview
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
        _currentMatchIndex = -1;
        HideMatchNavPanel();
        ViewModel.ResultGroups.Clear();
        ViewModel.StatusText = string.Empty;
        ViewModel.Query = string.Empty;

        // Re-enter launcher mode
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

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_autoScrollEnabled || ViewModel.ResultGroups.Count == 0) return;
        ResultsList.ScrollIntoView(ViewModel.ResultGroups[^1]);
    }

    private void UpdateSparkline()
    {
        var samples = ViewModel.ThroughputSamples;
        if (samples.Count < 2)
        {
            ThroughputSparkline.Points.Clear();
            return;
        }

        double width = ThroughputSparkline.ActualWidth;
        double height = ThroughputSparkline.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Use MB/s for the sparkline (more visually interesting than files/s)
        double max = 1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].mbPerSec > max) max = samples[i].mbPerSec;
        }

        var pts = ThroughputSparkline.Points;
        pts.Clear();
        double xStep = width / (samples.Count - 1);
        for (int i = 0; i < samples.Count; i++)
        {
            double x = i * xStep;
            double y = height - (samples[i].mbPerSec / max * (height - 2)) - 1;
            pts.Add(new Windows.Foundation.Point(x, y));
        }
    }

    private void SetAutoScrollEnabled(bool enabled)
    {
        _autoScrollEnabled = enabled;
        if (AutoScrollResultsCheckBox.IsChecked != enabled)
            AutoScrollResultsCheckBox.IsChecked = enabled;
    }

    private void OnAutoScrollResultsChanged(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = AutoScrollResultsCheckBox.IsChecked == true;
        if (_autoScrollEnabled && ViewModel.ResultGroups.Count > 0)
            ResultsList.ScrollIntoView(ViewModel.ResultGroups[^1]);
    }

    private void OnFilterBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.FontStyle = string.IsNullOrEmpty(tb.Text)
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal;
    }

    private async void OnSearchCancelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSearching)
        {
            await ViewModel.CancelAsync();
        }
        else
        {
            HideQuerySuggestions();
            if (!await ClearPreviewPanelForNewSearchAsync()) return;
            await ViewModel.StartSearchAsync();
        }
    }

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var submittedQuery = args.ChosenSuggestion as string;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = args.QueryText;
        if (string.IsNullOrEmpty(submittedQuery))
            submittedQuery = sender.Text;

        if (!string.IsNullOrEmpty(submittedQuery))
            ViewModel.Query = submittedQuery;

        HideQuerySuggestions(sender);
        if (!await ClearPreviewPanelForNewSearchAsync()) return;
        await ViewModel.StartSearchAsync();
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter is handled by OnQuerySubmitted — only handle Escape here.
        if (e.Key == VirtualKey.Escape && ViewModel.IsSearching)
        {
            e.Handled = true;
            await ViewModel.CancelAsync();
        }
        // Down arrow opens the search history dropdown.
        else if (e.Key == VirtualKey.Down && !QueryBox.IsSuggestionListOpen
                 && ViewModel.SearchHistory.Count > 0)
        {
            RestoreQuerySuggestions();
            QueryBox.IsSuggestionListOpen = true;
        }
    }

    private void HideQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        _querySuggestionsDetached = true;
        _hideSuggestionsTick = Environment.TickCount64;
        target.IsSuggestionListOpen = false;
        target.ItemsSource = null;
        target.IsSuggestionListOpen = false;
        // The AutoSuggestBox sometimes re-opens its popup after QuerySubmitted.
        // Fight back with a deferred close.
        DispatcherQueue.TryEnqueue(() =>
        {
            target.IsSuggestionListOpen = false;
            DispatcherQueue.TryEnqueue(() => target.IsSuggestionListOpen = false);
        });
    }

    private void RestoreQuerySuggestions(AutoSuggestBox? box = null)
    {
        if (!_querySuggestionsDetached) return;
        // After a deliberate hide (Enter to search), suppress re-attach briefly
        // so the AutoSuggestBox's spurious TextChanged events don't reopen the popup.
        if (Environment.TickCount64 - _hideSuggestionsTick < 400) return;
        var target = box ?? QueryBox;
        _querySuggestionsDetached = false;
        target.ItemsSource = ViewModel.SearchHistory;
        target.IsSuggestionListOpen = false;
    }

    private void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            RestoreQuerySuggestions(sender);
    }

    private void OnQueryLostFocus(object sender, RoutedEventArgs e)
    {
        RestoreQuerySuggestions(sender as AutoSuggestBox);
    }

    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void OnBrowseDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        _ = PickFolderAsync(picker);
    }

    private void OnDirectoryQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // User pressed Enter in the directory box — just accept the text (already bound).
    }

    private void OnDirectoryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only respond to user typing, not programmatic changes.
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _ = ViewModel.UpdateDirectorySuggestionsAsync(sender.Text);
        }
    }

    private void OnDirectorySuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string chosen)
        {
            // Append trailing backslash so user can continue drilling down.
            sender.Text = chosen.EndsWith('\\') ? chosen : chosen + '\\';
        }
    }

    private void OnRestartAsAdmin(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            // Strip any pre-existing --wait-for-pid <n> tokens, then append our own
            // pointing at the current process so the elevated instance waits for us
            // to fully exit (and release the single-instance mutex) before starting.
            var existing = Environment.GetCommandLineArgs().Skip(1).ToList();
            for (int i = existing.Count - 2; i >= 0; i--)
            {
                if (string.Equals(existing[i], "--wait-for-pid", StringComparison.OrdinalIgnoreCase))
                {
                    existing.RemoveAt(i + 1);
                    existing.RemoveAt(i);
                }
            }
            existing.Add("--wait-for-pid");
            existing.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var args = string.Join(" ", existing.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            // Release the single-instance mutex BEFORE starting the elevated process,
            // so there's no race where the new instance sees the mutex still owned.
            try
            {
                App.InstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException) { /* not owned — ignore */ }
            App.InstanceMutex?.Dispose();
            App.InstanceMutex = null;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt — re-acquire the mutex so this instance
            // remains the single instance, then do nothing.
            try
            {
                App.InstanceMutex = new System.Threading.Mutex(true, @"Global\YaguSingleInstance", out _);
            }
            catch { /* best-effort */ }
        }
    }

    private async void OnDontShowAdminWarningAgain(object sender, RoutedEventArgs e)
    {
        ViewModel.SuppressAdminWarning = true;
        await ViewModel.PersistSettingsAsync();
        AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_launcherMode)
            PositionLauncherWindow();
    }

    private void OnAdminBannerCloseClick(object sender, RoutedEventArgs e)
    {
        AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_launcherMode)
            PositionLauncherWindow();
        FocusSearchBox();
    }

    private async void OnAdminLearnMore(object sender, RoutedEventArgs e)
    {
        var segments = Yagu.Services.FileLister.ParseAdminProtectedSegments(ViewModel.AdminProtectedPathSegments);
        if (segments.Count == 0) segments.AddRange(Yagu.Services.FileLister.DefaultAdminProtectedPathSegments);

        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock
        {
            Text = "Some paths are not accessible by non-administrative processes. Currently, Yagu is configured to skip the following administrator-only paths while running in non-administrator mode:",
            TextWrapping = TextWrapping.Wrap,
        });

        // Use a ScrollViewer + StackPanel of TextBlocks instead of a multiline TextBox.
        // WinUI TextBox programmatic Text with multiple newlines is fiddly; a list of
        // TextBlocks is simpler and renders reliably.
        var listPanel = new StackPanel { Spacing = 2 };
        foreach (var seg in segments)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = seg,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                IsTextSelectionEnabled = true,
            });
        }
        var scroller = new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 220,
            Padding = new Thickness(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlElevationBorderBrush"],
            CornerRadius = new CornerRadius(4),
        };
        sp.Children.Add(scroller);

        sp.Children.Add(new TextBlock
        {
            Text = "This list is not exhaustive, and some other protected paths may be inaccessible and fail during search.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "To modify this list, please go to the Settings page (click the gear on the top right of the app).",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });

        var dlg = new ContentDialog
        {
            Title = "Admin-protected paths",
            Content = sp,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private async Task PickFolderAsync(Windows.Storage.Pickers.FolderPicker picker)
    {
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.Directory = folder.Path;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Settings",
            CloseButtonText = "Close",
            PrimaryButtonText = "Save",
            Content = BuildSettingsPanel(),
            Resources =
            {
                ["ContentDialogMaxWidth"] = 620d,
            },
        };
        dialog.PrimaryButtonClick += async (_, _) => await ViewModel.PersistSettingsAsync();
        _ = dialog.ShowAsync();
    }

    private void OnOpenCredits(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = AppInfo.WindowTitle,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Work by Andrew Stein, 2026.",
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "About Yagu",
            CloseButtonText = "Close",
            Content = panel,
        };
        _ = dialog.ShowAsync();
    }

    /// <summary>Creates a label with a small refresh icon indicating the setting takes effect on the next search.</summary>
    private static StackPanel NextSearchLabel(string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = text });
        var icon = new FontIcon { Glyph = "\uE72C", FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(icon, "Takes effect on the next search");
        panel.Children.Add(icon);
        return panel;
    }

    /// <summary>Icon glyphs for each settings group header.</summary>
    private static string SettingsGroupIcon(string header) => header switch
    {
        "Search Defaults" => "\uE721",   // Search
        "Search Limits" => "\uE74C",     // Filter
        "Performance" => "\uE9F5",       // SpeedHigh
        "Display" => "\uE7B5",           // View
        "Editor" => "\uE70F",            // Edit
        "Window" => "\uE737",            // Window
        "General" => "\uE713",           // Settings
        _ => "\uE7FC",                   // Placeholder
    };

    /// <summary>Creates a settings group box: a bordered panel with an icon + header and a content StackPanel.</summary>
    private static StackPanel MakeSettingsGroup(string header)
    {
        var content = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 14) };
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        headerPanel.Children.Add(new FontIcon
        {
            Glyph = SettingsGroupIcon(header),
            FontSize = 16,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = header,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var border = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        CornerRadius = new CornerRadius(8, 8, 0, 0),
                        Padding = new Thickness(14, 10, 14, 10),
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = headerPanel,
                    },
                    content,
                },
            },
        };
        content.Tag = border;
        return content;
    }

    private FrameworkElement BuildSettingsPanel()
    {
        var sp = new StackPanel { Spacing = 14, Width = 540 };

        // Legend for the next-search icon
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Opacity = 0.6, Margin = new Thickness(2, 0, 0, 2) };
        legend.Children.Add(new FontIcon { Glyph = "\uE72C", FontSize = 12 });
        legend.Children.Add(new TextBlock { Text = "= takes effect on the next search", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(legend);

        // ── Search Defaults ──
        {
            var g = MakeSettingsGroup("Search Defaults");

            g.Children.Add(NextSearchLabel("Context lines (lines shown before & after each match in results):"));
            var ctx = new NumberBox { Value = ViewModel.ContextLines, Minimum = 0, Maximum = 50 };
            ctx.ValueChanged += (_, args) => ViewModel.ContextLines = (int)args.NewValue;
            g.Children.Add(ctx);

            g.Children.Add(new TextBlock { Text = "Preview context lines (lines shown around each match in the preview panel):" });
            var prevCtx = new NumberBox { Value = ViewModel.PreviewContextLines, Minimum = 0, Maximum = 200 };
            prevCtx.ValueChanged += (_, args) => ViewModel.PreviewContextLines = (int)args.NewValue;
            g.Children.Add(prevCtx);

            g.Children.Add(NextSearchLabel("Default include globs (comma/semicolon-separated):"));
            var incGlobs = new TextBox { Text = ViewModel.IncludeGlobs, PlaceholderText = "e.g. *.cs;*.ts" };
            incGlobs.TextChanged += (_, _) => ViewModel.IncludeGlobs = incGlobs.Text;
            g.Children.Add(incGlobs);

            g.Children.Add(NextSearchLabel("Default exclude globs (comma/semicolon-separated):"));
            var excGlobs = new TextBox { Text = ViewModel.ExcludeGlobs, PlaceholderText = "e.g. node_modules;bin;obj;.git" };
            excGlobs.TextChanged += (_, _) => ViewModel.ExcludeGlobs = excGlobs.Text;
            g.Children.Add(excGlobs);

            sp.Children.Add((Border)g.Tag!);
        }

        // ── Search Limits ──
        {
            var g = MakeSettingsGroup("Search Limits");

            g.Children.Add(NextSearchLabel("Max results (0 = unlimited, non-zero values capped at 50,000):"));
            var max = new NumberBox { Value = ViewModel.MaxResults, Minimum = 0 };
            max.ValueChanged += (_, args) => ViewModel.MaxResults = (int)args.NewValue;
            g.Children.Add(max);
            g.Children.Add(new TextBlock { Text = "Stops the search after this many matches. Set to 0 for no limit (memory pressure will still protect against runaway usage).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Max file size to search (MB, 0 = no limit):"));
            var size = new NumberBox { Value = ViewModel.MaxFileSizeBytes / (1024d * 1024d), Minimum = 0 };
            size.ValueChanged += (_, args) => ViewModel.MaxFileSizeBytes = (long)(args.NewValue * 1024 * 1024);
            g.Children.Add(size);
            g.Children.Add(new TextBlock { Text = "Files larger than this are skipped during search. Also used by the Everything SDK to pre-filter results.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipBinary = new CheckBox { Content = NextSearchLabel("Skip binary files"), IsChecked = ViewModel.SkipBinary };
            skipBinary.Checked += (_, _) => ViewModel.SkipBinary = true;
            skipBinary.Unchecked += (_, _) => ViewModel.SkipBinary = false;
            g.Children.Add(skipBinary);
            g.Children.Add(new TextBlock { Text = "When enabled, files detected as binary (null bytes, magic bytes) are skipped during content search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipAdmin = new CheckBox { Content = NextSearchLabel("Skip admin-protected paths when not elevated"), IsChecked = ViewModel.ExcludeAdminProtectedPaths };
            skipAdmin.Checked += (_, _) => ViewModel.ExcludeAdminProtectedPaths = true;
            skipAdmin.Unchecked += (_, _) => ViewModel.ExcludeAdminProtectedPaths = false;
            g.Children.Add(skipAdmin);
            g.Children.Add(new TextBlock { Text = "When the process is not elevated, exclude directories that always require admin (System Volume Information, $Recycle.Bin, Windows\\System32\\config, Windows\\Installer, etc.). Speeds up search by skipping guaranteed access-denied trees. No effect when running as administrator.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var adminSegLabel = NextSearchLabel("Admin-protected path segments (one per line or semicolon-separated):");
            adminSegLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(adminSegLabel);
            var adminSeg = new TextBox
            {
                Text = ViewModel.AdminProtectedPathSegments,
                PlaceholderText = @"\Windows\System32\config;\System Volume Information",
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Height = 100,
            };
            adminSeg.TextChanged += (_, _) => ViewModel.AdminProtectedPathSegments = adminSeg.Text;
            g.Children.Add(adminSeg);
            var resetAdminSeg = new Button { Content = "Reset to defaults", Margin = new Thickness(0, 4, 0, 0) };
            resetAdminSeg.Click += (_, _) =>
            {
                adminSeg.Text = AppSettings.DefaultAdminProtectedPathSegments;
                ViewModel.AdminProtectedPathSegments = adminSeg.Text;
            };
            g.Children.Add(resetAdminSeg);
            g.Children.Add(new TextBlock { Text = "Each entry is a path substring like \\Windows\\System32\\config. Anchored with backslashes so it matches the folder anywhere in the tree. Only applied when the process is not elevated.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var skipExtLabel = NextSearchLabel("Skip extensions (semicolon-separated, no dots):");
            skipExtLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(skipExtLabel);
            var skipExt = new TextBox { Text = ViewModel.SkipExtensions, PlaceholderText = "e.g. exe;dll;zip;png;pdf", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            skipExt.TextChanged += (_, _) => ViewModel.SkipExtensions = skipExt.Text;
            g.Children.Add(skipExt);
            g.Children.Add(new TextBlock { Text = "Files with these extensions are skipped entirely — no binary check, no content read.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            var archiveExtLabel = NextSearchLabel("Archive extensions (semicolon-separated, no dots):");
            archiveExtLabel.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(archiveExtLabel);
            var archiveExt = new TextBox { Text = ViewModel.ArchiveExtensions, PlaceholderText = "e.g. zip;jar;docx;xlsx;pptx;epub", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            archiveExt.TextChanged += (_, _) => ViewModel.ArchiveExtensions = archiveExt.Text;
            g.Children.Add(archiveExt);
            g.Children.Add(new TextBlock { Text = "Extensions that are ZIP-like containers. When 'Search archives' is on, these are removed from the skip list so they reach the content searcher. Detection still uses file-header magic bytes.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            sp.Children.Add((Border)g.Tag!);
        }

        // ── Performance ──
        {
            var g = MakeSettingsGroup("Performance");

            g.Children.Add(NextSearchLabel("Content-search parallelism (concurrent file scan threads):"));
            var parallelism = new ComboBox();
            parallelism.Items.Add($"Auto (safe cap · up to {Math.Min(16, Environment.ProcessorCount)})");
            parallelism.Items.Add("1 thread (sequential, HDD safe)");
            parallelism.Items.Add($"Half cores ({Math.Max(1, Environment.ProcessorCount / 2)})");
            parallelism.Items.Add($"2× cores ({Environment.ProcessorCount * 2}, I/O heavy)");
            parallelism.Items.Add($"All cores ({Math.Max(1, Environment.ProcessorCount)})");
            parallelism.SelectedIndex = ViewModel.ParallelismIndex;
            parallelism.SelectionChanged += (_, _) => ViewModel.ParallelismIndex = parallelism.SelectedIndex;
            g.Children.Add(parallelism);

            g.Children.Add(new TextBlock { Text = "File-listing backend (how files are discovered before searching):" });
            var backend = new ComboBox();
            backend.Items.Add("Auto (SDK → es.exe → .NET)");
            backend.Items.Add("Everything SDK only (in-process, fastest)");
            backend.Items.Add("es.exe only (process spawn)");
            backend.Items.Add(".NET enumeration only (no Everything dependency)");
            backend.SelectedIndex = ViewModel.FileListerBackendIndex;
            backend.SelectionChanged += (_, _) => ViewModel.FileListerBackendIndex = backend.SelectedIndex;
            g.Children.Add(backend);
            g.Children.Add(new TextBlock { Text = "Auto tries the Everything SDK first, then es.exe, then .NET recursive enumeration. Requires voidtools Everything to be running for SDK/es.exe.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // Memory saving mode sub-section
            g.Children.Add(new TextBlock { Text = "Memory saving mode", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });
            g.Children.Add(new TextBlock { Text = "These two settings control when Yagu enters memory-saving mode. Use one or the other — if the hard cap is set (> 0), it takes precedence over the pressure percentage. Changes apply to the next search.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

            g.Children.Add(NextSearchLabel("System memory pressure limit (%, 0 = disabled):"));
            var memPressure = new NumberBox { Value = ViewModel.MemoryPressurePercent, Minimum = 0, Maximum = 100 };
            memPressure.ValueChanged += (_, args) => ViewModel.MemoryPressurePercent = (int)args.NewValue;
            g.Children.Add(memPressure);
            g.Children.Add(new TextBlock { Text = "Yagu enters memory-saving mode when total machine RAM usage exceeds this %. Recommended for most users.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("Process memory hard cap (MB, 0 = use pressure % above):"));
            var memLimit = new NumberBox { Value = ViewModel.MemoryLimitMB, Minimum = 0, Maximum = 65536 };
            memLimit.ValueChanged += (_, args) => ViewModel.MemoryLimitMB = (int)args.NewValue;
            g.Children.Add(memLimit);
            g.Children.Add(new TextBlock { Text = "When set above 0, memory-saving mode activates when the Yagu process exceeds this working-set size regardless of system memory pressure. Leave at 0 to use the pressure % instead.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(NextSearchLabel("SDK channel buffer size:"));
            var sdkBuf = new NumberBox { Value = ViewModel.SdkChannelBufferSize, Minimum = 16, Maximum = 1000000 };
            sdkBuf.ValueChanged += (_, args) => ViewModel.SdkChannelBufferSize = (int)args.NewValue;
            g.Children.Add(sdkBuf);
            g.Children.Add(new TextBlock { Text = "Number of file paths buffered between the Everything SDK producer thread and the consumer. Higher values may improve throughput on large directories but use more memory. Only applies when using the Everything SDK backend.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            sp.Children.Add((Border)g.Tag!);
        }

        // ── Display ──
        {
            var g = MakeSettingsGroup("Display");

            g.Children.Add(new TextBlock { Text = "Line truncation length (characters):" });
            var trunc = new NumberBox { Value = ViewModel.LineTruncationLength, Minimum = 0, Maximum = 10000 };
            trunc.ValueChanged += (_, args) => ViewModel.LineTruncationLength = (int)args.NewValue;
            g.Children.Add(trunc);
            g.Children.Add(new TextBlock { Text = "Lines longer than this are truncated in the results list to prevent UI slowdowns from extremely long lines. Set to 0 to disable truncation.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            g.Children.Add(new TextBlock { Text = "Preview layout:" });
            var previewMode = new ComboBox();
            previewMode.Items.Add("Concatenated (separate match snippets)");
            previewMode.Items.Add("Multi-highlight (unified file view)");
            previewMode.SelectedIndex = ViewModel.PreviewModeIndex;
            previewMode.SelectionChanged += (_, _) => ViewModel.PreviewModeIndex = previewMode.SelectedIndex;
            g.Children.Add(previewMode);

            var wordWrap = new CheckBox { Content = "Word wrap in preview panel", IsChecked = ViewModel.PreviewWordWrap };
            wordWrap.Checked += (_, _) => { ViewModel.PreviewWordWrap = true; ApplyWordWrap(true); };
            wordWrap.Unchecked += (_, _) => { ViewModel.PreviewWordWrap = false; ApplyWordWrap(false); };
            g.Children.Add(wordWrap);

            sp.Children.Add((Border)g.Tag!);
        }

        // ── Editor ──
        {
            var g = MakeSettingsGroup("Editor");

            g.Children.Add(new TextBlock { Text = "Editor command ({file} = full file path, {line} = line number):" });
            var editor = new TextBox { Text = ViewModel.EditorCommand };
            editor.TextChanged += (_, _) => ViewModel.EditorCommand = editor.Text;
            g.Children.Add(editor);

            var backup = new CheckBox { Content = "Backup file before saving", IsChecked = ViewModel.BackupBeforeSave };
            backup.Checked += (_, _) => ViewModel.BackupBeforeSave = true;
            backup.Unchecked += (_, _) => ViewModel.BackupBeforeSave = false;
            g.Children.Add(backup);
            g.Children.Add(new TextBlock { Text = "When enabled, the original file is copied to <filename>.yagubak before saving changes from the built-in editor. If a .yagubak already exists, uses .yagubak-2, .yagubak-3, etc.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            sp.Children.Add((Border)g.Tag!);
        }

        // ── Window ──
        {
            var g = MakeSettingsGroup("Window");

            g.Children.Add(new TextBlock { Text = "Default window focus behavior (launcher mode):" });
            var focusBehavior = new ComboBox();
            focusBehavior.Items.Add("Minimize to system tray (default)");
            focusBehavior.Items.Add("Stay open (don't minimize on focus loss)");
            focusBehavior.Items.Add("Always on top (stay above all windows)");
            focusBehavior.Items.Add("Traditional window (full window with title bar)");
            focusBehavior.SelectedIndex = ViewModel.WindowFocusBehavior;
            focusBehavior.SelectionChanged += (_, _) => ViewModel.WindowFocusBehavior = focusBehavior.SelectedIndex;
            g.Children.Add(focusBehavior);
            g.Children.Add(new TextBlock { Text = "Controls what happens when the launcher window loses focus. This sets the default; you can override per-session using the pin button next to Browse.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            sp.Children.Add((Border)g.Tag!);
        }

        // ── General ──
        {
            var g = MakeSettingsGroup("General");

            var availableHotkeyKeys = _hotkeyService.GetAvailableCtrlShiftLetterKeys(_hwnd);
            var selectedHotkeyKey = HotkeyService.ChooseAvailableKey(availableHotkeyKeys, ViewModel.GlobalHotkeyKey);
            if (selectedHotkeyKey is char selectedKey && !string.Equals(ViewModel.GlobalHotkeyKey, selectedKey.ToString(), StringComparison.OrdinalIgnoreCase))
                ViewModel.GlobalHotkeyKey = selectedKey.ToString();

            var hotkey = new CheckBox { Content = "Enable global hotkey", IsChecked = ViewModel.GlobalHotkeyEnabled, IsEnabled = availableHotkeyKeys.Count > 0 };
            hotkey.Checked += (_, _) => ViewModel.GlobalHotkeyEnabled = true;
            hotkey.Unchecked += (_, _) => ViewModel.GlobalHotkeyEnabled = false;
            g.Children.Add(hotkey);

            g.Children.Add(new TextBlock { Text = "Global hotkey:" });
            var hotkeyCombo = new ComboBox { IsEnabled = availableHotkeyKeys.Count > 0 };
            foreach (var key in availableHotkeyKeys)
            {
                hotkeyCombo.Items.Add(new ComboBoxItem
                {
                    Content = HotkeyService.FormatCtrlShift(key),
                    Tag = key.ToString(),
                });
            }

            if (selectedHotkeyKey is char hotkeyKey)
            {
                for (int itemIndex = 0; itemIndex < hotkeyCombo.Items.Count; itemIndex++)
                {
                    if (hotkeyCombo.Items[itemIndex] is ComboBoxItem item &&
                        string.Equals(item.Tag as string, hotkeyKey.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        hotkeyCombo.SelectedIndex = itemIndex;
                        break;
                    }
                }
            }
            else
            {
                hotkeyCombo.Items.Add("No Ctrl+Shift+letter combinations available");
                hotkeyCombo.SelectedIndex = 0;
            }

            hotkeyCombo.SelectionChanged += (_, _) =>
            {
                if (hotkeyCombo.SelectedItem is ComboBoxItem item && item.Tag is string key)
                    ViewModel.GlobalHotkeyKey = key;
            };
            g.Children.Add(hotkeyCombo);

            g.Children.Add(new TextBlock { Text = "Max recent directories / queries to remember:" });
            var recent = new NumberBox { Value = ViewModel.MaxRecentItems, Minimum = 1, Maximum = 100 };
            recent.ValueChanged += (_, args) => ViewModel.MaxRecentItems = (int)args.NewValue;
            g.Children.Add(recent);

            g.Children.Add(new TextBlock { Text = "File log level:" });
            var fileLogLevel = new ComboBox();
            fileLogLevel.Items.Add("None (logging disabled)");
            fileLogLevel.Items.Add("Critical (errors only)");
            fileLogLevel.Items.Add("Warning (errors + warnings)");
            fileLogLevel.Items.Add("Info (general activity)");
            fileLogLevel.Items.Add("Verbose (all details, may slow performance)");
            fileLogLevel.SelectedIndex = ViewModel.FileLogLevelIndex + 1;
            fileLogLevel.SelectionChanged += (_, _) => ViewModel.FileLogLevelIndex = fileLogLevel.SelectedIndex - 1;
            g.Children.Add(fileLogLevel);

            g.Children.Add(new TextBlock { Text = "Console log level:" });
            var consoleLogLevel = new ComboBox();
            consoleLogLevel.Items.Add("None (logging disabled)");
            consoleLogLevel.Items.Add("Critical (errors only)");
            consoleLogLevel.Items.Add("Warning (errors + warnings)");
            consoleLogLevel.Items.Add("Info (general activity)");
            consoleLogLevel.Items.Add("Verbose (all details, may slow performance)");
            consoleLogLevel.SelectedIndex = ViewModel.ConsoleLogLevelIndex + 1;
            consoleLogLevel.SelectionChanged += (_, _) => ViewModel.ConsoleLogLevelIndex = consoleLogLevel.SelectedIndex - 1;
            g.Children.Add(consoleLogLevel);

            g.Children.Add(new TextBlock { Text = $"Log file: {LogService.DefaultLogPath()}", FontSize = 11, Opacity = 0.6 });

            // Reset admin warning
            if (ViewModel.SuppressAdminWarning)
            {
                var resetAdmin = new Button { Content = "Re-enable admin privilege warning", FontSize = 12, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0) };
                resetAdmin.Click += (_, _) =>
                {
                    ViewModel.SuppressAdminWarning = false;
                    resetAdmin.Content = "Admin warning re-enabled ✓";
                    resetAdmin.IsEnabled = false;
                };
                g.Children.Add(resetAdmin);
                g.Children.Add(new TextBlock { Text = "The non-administrator warning was previously dismissed. Click to show it again on next launch.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            }

            sp.Children.Add((Border)g.Tag!);
        }

        // Wrap in ScrollViewer so the dialog is scrollable when content overflows.
        return new ScrollViewer
        {
            Content = sp,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 560,
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFolder f)
            {
                ViewModel.Directory = f.Path;
                return;
            }
        }
    }

    private SearchResult? _previewResult;
    private bool _previewPanelRevealed;

    // Match navigation state for multi-highlight mode
    private readonly List<(RichTextBlock block, Paragraph para, int matchInPara)> _matchParagraphs = new();
    private int _currentMatchIndex = -1;
    // Set to true once we've auto-scrolled to the first match for the current
    // preview load; reset by HideMatchNavPanel / preview-clear paths so a fresh
    // preview re-triggers the auto-scroll-to-first-match.
    private bool _initialMatchScrolled;
    // Paragraph metrics cache for O(1) lookup and O(1) cumulative-height estimates
    // in large files. Invalidated whenever a block's Blocks collection changes
    // (full-file toggle, section materialize, clear).
    private sealed class ParagraphMetrics
    {
        public required Dictionary<Paragraph, int> IndexByParagraph { get; init; }
        public double[]? PrefixHeights;
        public int PrefixCharsPerLine;
        public double PrefixLineHeight;
    }
    private readonly Dictionary<RichTextBlock, ParagraphMetrics> _paragraphMetricsCache = new();
    private readonly Dictionary<Paragraph, List<(Run run, int column)>> _paragraphMatchRunCache = new();
    private readonly Dictionary<RichTextBlock, Expander> _blockExpanderCache = new();
    private readonly Dictionary<RichTextBlock, double> _blockAbsoluteTopCache = new();

    // Per-section match navigation state (class moved to Models/SectionMatchNav.cs)
    private readonly Dictionary<RichTextBlock, SectionMatchNav> _sectionMatchNavs = new();
    private SectionMatchNav? _activeSectionNav;
    // Expander → file path, used to render the sticky file-header overlay
    // (shown when the active expander's own header has scrolled out of view).
    private readonly Dictionary<Expander, string> _expanderFilePaths = new();
    // Cached metadata for rebuilding a section header inside the sticky overlay.
    private readonly Dictionary<Expander, (string FilePath, string? Detail, RichTextBlock Block, List<SearchResult>? Results)> _expanderHeaderArgs = new();
    // The expander whose header is currently mirrored in StickyFileHeader, so we
    // only rebuild + replace its child when the topmost-in-viewport changes.
    private Expander? _stickyHeaderExpander;

    private List<KeyValuePair<string, List<SearchResult>>>? _cachedDeferredCountsList;
    private int _cachedDeferredCountsCursor = -1;
    private (int Files, int Matches) _cachedDeferredCounts;

    // Lazy section rendering — deferred content building for collapsed sections
    private sealed class LazySection
    {
        public required string FilePath { get; init; }
        public required List<SearchResult> Results { get; init; }
        public string[]? AllLines { get; init; }
        public int PreviewLines { get; init; }
        public bool IsHighlight { get; init; }
        public int MatchCount { get; init; } // pre-computed match count for nav label
    }
    private readonly Dictionary<RichTextBlock, LazySection> _lazySections = new();
    private int _lazyMatchCount; // total matches in un-rendered sections
    private bool _previewViewChangedHooked;
    private bool _viewportMaterializePending;
    private static readonly SolidColorBrush s_activeExpanderBrush = new(Windows.UI.Color.FromArgb(25, 80, 180, 255));

    // Tracks remaining (un-rendered) matches for sections that were truncated to
    // avoid UI freezes on huge files (see MaxMatchesPerSection). Each click of
    // "Next match" past the last rendered match in such a section appends the
    // next chunk of results to the section.
    private sealed class SectionOverflow
    {
        public string? FilePath;
        public required List<SearchResult> RemainingResults;
        public required string[]? AllLines;
        public required int PreviewLines;
        public required Regex? Rx;
        public required int OriginalTotal;
        public required int RenderedSoFar;
        public Paragraph? NoticePara;
        /// <summary>True if the section was rendered in multi-highlight mode
        /// (contiguous line ranges with <c>⋮</c> gap markers) rather than
        /// concat mode (per-match "── Line N ──" separators).</summary>
        public bool IsHighlightMode;
        /// <summary>Highest line index (1-based) already rendered. Used in
        /// highlight mode to clip overlapping context windows so expansion
        /// continues directly from the last rendered line.</summary>
        public int LastRenderedLine;
    }
    private readonly Dictionary<RichTextBlock, SectionOverflow> _sectionOverflow = new();

    // Active match state. The active visual marker is rendered as an overlay;
    // do not mutate Run styling here because RichTextBlock re-renders expensively.
    private (Paragraph para, Run run, int column, int matchInPara)? _activeMatchHighlight;
    private int _matchScrollRequestId;

    private enum SplitLayoutMode { Split, ResultsMaximized, PreviewMaximized }
    private SplitLayoutMode _splitLayoutMode = SplitLayoutMode.ResultsMaximized;

    private void EnsurePreviewPanelVisible()
    {
        if (_previewPanelRevealed && _splitLayoutMode == SplitLayoutMode.Split) return;
        _previewPanelRevealed = true;
        ApplySplitLayout(SplitLayoutMode.Split);
    }

    private void CollapsePreviewPanel()
    {
        if (!_previewPanelRevealed && _splitLayoutMode == SplitLayoutMode.ResultsMaximized) return;
        _previewPanelRevealed = false;
        ApplySplitLayout(SplitLayoutMode.ResultsMaximized);
    }

    private void ApplySplitLayout(SplitLayoutMode mode)
    {
        _splitLayoutMode = mode;
        switch (mode)
        {
            case SplitLayoutMode.Split:
                ResultsPanelBorder.Visibility = Visibility.Visible;
                ResultsColumn.Width = new GridLength(2, GridUnitType.Star);
                ResultsColumn.MinWidth = 200;
                SplitterColumn.Width = GridLength.Auto;
                PreviewColumn.Width = new GridLength(3, GridUnitType.Star);
                PreviewColumn.MinWidth = 200;
                SplitterBorder.Visibility = Visibility.Visible;
                PreviewPanelBorder.Visibility = Visibility.Visible;
                _previewPanelRevealed = true;
                ExpandResultsIcon.Glyph = "\uE740"; // FullScreen
                ToolTipService.SetToolTip(ExpandResultsButton, "Maximize file list / hide preview");
                ExpandPreviewIcon.Glyph = "\uE740"; // FullScreen
                ToolTipService.SetToolTip(ExpandPreviewButton, "Maximize preview / hide file list");
                break;
            case SplitLayoutMode.ResultsMaximized:
                ResultsPanelBorder.Visibility = Visibility.Visible;
                ResultsColumn.Width = new GridLength(1, GridUnitType.Star);
                ResultsColumn.MinWidth = 200;
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(0);
                PreviewColumn.MinWidth = 0;
                SplitterBorder.Visibility = Visibility.Collapsed;
                PreviewPanelBorder.Visibility = Visibility.Collapsed;
                _previewPanelRevealed = false;
                ExpandResultsIcon.Glyph = "\uE73F"; // BackToWindow
                ToolTipService.SetToolTip(ExpandResultsButton, "Restore split view");
                ExpandPreviewIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(ExpandPreviewButton, "Maximize preview / hide file list");
                break;
            case SplitLayoutMode.PreviewMaximized:
                ResultsPanelBorder.Visibility = Visibility.Collapsed;
                ResultsColumn.Width = new GridLength(0);
                ResultsColumn.MinWidth = 0;
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
                PreviewColumn.MinWidth = 200;
                SplitterBorder.Visibility = Visibility.Collapsed;
                PreviewPanelBorder.Visibility = Visibility.Visible;
                _previewPanelRevealed = true;
                ExpandResultsIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(ExpandResultsButton, "Maximize file list / hide preview");
                ExpandPreviewIcon.Glyph = "\uE73F"; // BackToWindow
                ToolTipService.SetToolTip(ExpandPreviewButton, "Restore split view");
                break;
        }
    }

    private void OnExpandResultsPanel(object sender, RoutedEventArgs e)
    {
        // Toggle: maximize results <-> split view (always restore split when not currently maximized).
        if (_splitLayoutMode == SplitLayoutMode.ResultsMaximized)
            ApplySplitLayout(SplitLayoutMode.Split);
        else
            ApplySplitLayout(SplitLayoutMode.ResultsMaximized);
    }

    private void OnExpandPreviewPanel(object sender, RoutedEventArgs e)
    {
        // Toggle: maximize preview <-> split view.
        if (_splitLayoutMode == SplitLayoutMode.PreviewMaximized)
            ApplySplitLayout(SplitLayoutMode.Split);
        else
            ApplySplitLayout(SplitLayoutMode.PreviewMaximized);
    }

    private async void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileGroup g && g.Count > 0)
        {
            // If the file is already visible in the multi-select preview sections, scroll to it instead of reloading.
            if (PreviewSectionsPanel.Visibility == Visibility.Visible && TryScrollToPreviewSection(g[0].FilePath))
                return;

            await UpdatePreviewAsync(g[0]);
        }
    }

    /// <summary>
    /// Scrolls to an existing preview section for the given file path.
    /// Returns true if the section was found and scrolled to.
    /// </summary>
    private bool TryScrollToPreviewSection(string filePath)
    {
        LogService.Instance.Verbose("Preview", $"TryScrollToPreviewSection: looking for '{filePath}' among {PreviewSectionsPanel.Children.OfType<Expander>().Count()} sections");
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (ToolTipService.GetToolTip(child) is string tip
                && string.Equals(tip, filePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    child.IsExpanded = true;
                    if (child.Tag is RichTextBlock block)
                        ActivateSectionForBlock(block);
                    var transform = child.TransformToVisual(PreviewScrollViewer);
                    var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    double targetOffset = PreviewScrollViewer.VerticalOffset + point.Y;
                    targetOffset = Math.Max(0, targetOffset);
                    PreviewScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
                }
                catch { /* Layout not ready — ignore */ }
                return true;
            }
        }
        return false;
    }

    private HashSet<string> GetExistingPreviewFilePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (ToolTipService.GetToolTip(child) is string tip)
                paths.Add(tip);
        }
        return paths;
    }

    /// <summary>
    /// Ensures the preview panel is in sections mode. If it is already in sections mode,
    /// existing sections are preserved. If switching from PreviewBlock mode, clears
    /// PreviewBlock and match state but keeps any already-added section children.
    /// </summary>
    private void EnsureSectionsSurface()
    {
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            LogService.Instance.Verbose("Preview", "EnsureSectionsSurface: already visible, preserving existing sections");
            return;
        }
        LogService.Instance.Verbose("Preview", "EnsureSectionsSurface: switching to sections mode, clearing PreviewBlock");

        PreviewBlock.Blocks.Clear();
        PreviewBlock.Visibility = Visibility.Collapsed;
        PreviewSectionsPanel.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Collapsed);
        HideMatchNavPanel();
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        _sectionMatchNavs.Clear();
        PreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        EnsurePreviewViewChangedHooked();
    }

    /// <summary>
    /// Hooks PreviewScrollViewer.ViewChanged once to drive viewport-based
    /// lazy section virtualization. Sections outside the viewport remain
    /// collapsed (and un-materialized) until they scroll close enough to be
    /// visible, capping peak XAML memory in large multi-file previews.
    /// </summary>
    private void EnsurePreviewViewChangedHooked()
    {
        if (_previewViewChangedHooked) return;
        _previewViewChangedHooked = true;
        PreviewScrollViewer.ViewChanged += OnPreviewScrollViewChanged;
    }

    /// <summary>
    /// Yields to the dispatcher at Low priority. Unlike Task.Yield (which
    /// reposts at Normal priority and can starve Input/Render), this lets
    /// pointer/keyboard input and frame rendering run before we resume —
    /// keeping the window marked "responsive" by Windows during long bulk
    /// preview-build loops.
    /// </summary>
    private Task YieldLowAsync()
    {
        var tcs = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => tcs.SetResult()))
        {
            tcs.SetResult();
        }
        return tcs.Task;
    }

    private void OnPreviewScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        TryAutoLoadMoreOnScroll();
        UpdateStickyFileHeader();
        if (_lazySections.Count == 0) return;
        if (_viewportMaterializePending) return;
        _viewportMaterializePending = true;
        // Defer to the next dispatcher pass so multiple ViewChanged events from
        // a flick/drag coalesce into a single materialization sweep.
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewportMaterializePending = false;
            MaterializeVisibleLazySections();
        });
    }

    /// <summary>
    /// Shows a sticky file-header banner at the top of the preview viewport when
    /// the topmost visible Expander's own header has scrolled above the viewport,
    /// so the user always knows which file the visible content belongs to.
    /// </summary>
    private void UpdateStickyFileHeader()
    {        try
        {
            if (PreviewSectionsPanel.Visibility != Visibility.Visible
                || PreviewSectionsPanel.Children.Count == 0)
            {
                StickyFileHeader.Visibility = Visibility.Collapsed;
                return;
            }

            var sv = PreviewScrollViewer;
            double vpTop = 0; // viewport-relative top (we transform expanders into ScrollViewer space)
            double vpBottom = sv.ViewportHeight;
            if (vpBottom <= 0)
            {
                StickyFileHeader.Visibility = Visibility.Collapsed;
                return;
            }

            Expander? topMostInView = null;
            bool anyHeaderAboveViewport = false;
            foreach (var child in PreviewSectionsPanel.Children)
            {
                if (child is not Expander exp) continue;
                Windows.Foundation.Point topLeft;
                double height;
                try
                {
                    var t = exp.TransformToVisual(sv);
                    topLeft = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    height = exp.ActualHeight;
                }
                catch { continue; }
                double expTop = topLeft.Y;
                double expBottom = expTop + height;
                if (expBottom <= vpTop) continue;       // entirely above
                if (expTop >= vpBottom) break;          // entirely below — children are in order
                if (expTop < vpTop)
                {
                    anyHeaderAboveViewport = true;
                    topMostInView = exp;
                    break;                               // first visible-but-header-clipped expander wins
                }
                // Header is in view — no sticky needed for this section.
                break;
            }

            if (!anyHeaderAboveViewport || topMostInView is null
                || !_expanderFilePaths.TryGetValue(topMostInView, out var path))
            {
                StickyFileHeader.Visibility = Visibility.Collapsed;
                StickyFileHeader.Child = null;
                _stickyHeaderExpander = null;
                return;
            }

            // Only rebuild the header content when the active section changes —
            // otherwise we'd thrash event handlers on every ViewChanged tick.
            if (!ReferenceEquals(_stickyHeaderExpander, topMostInView))
            {
                FrameworkElement? headerContent = null;
                if (_expanderHeaderArgs.TryGetValue(topMostInView, out var args))
                {
                    headerContent = BuildPreviewSectionHeader(args.FilePath, args.Detail, args.Block, args.Results);
                }
                else
                {
                    headerContent = BuildPreviewSectionHeader(path, detail: null);
                }
                StickyFileHeader.Child = headerContent;
                _stickyHeaderExpander = topMostInView;
            }
            StickyFileHeader.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"UpdateStickyFileHeader threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tap on the sticky file-header banner (outside any of its action buttons)
    /// toggles expand/collapse on the section it represents. Button taps do not
    /// reach here because Button handles the Tapped routed event itself.
    /// </summary>
    private void StickyFileHeader_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.Handled) return;
        var expander = _stickyHeaderExpander;
        if (expander is null) return;

        // The buttons inside the sticky header (Show full file, Copy, Open,
        // Edit, Show in Explorer, Dismiss) raise Click via PointerPressed but
        // do NOT mark the bubbling Tapped event as handled. Without this guard,
        // a click on any of those buttons would also collapse the file
        // expander. Walk up from the original source and bail if a Button is
        // in the path.
        if (e.OriginalSource is DependencyObject src && IsInsideButton(src))
            return;

        try
        {
            expander.IsExpanded = !expander.IsExpanded;
            // After collapse, the topmost section will be a different one; refresh.
            UpdateStickyFileHeader();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"StickyFileHeader_Tapped threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsInsideButton(DependencyObject element)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return true;
        }
        return false;
    }

    /// <summary>
    /// When the scroll position is near the bottom and there are still files
    /// behind a "Show more" button, auto-load the next AutoLoadChunkSize
    /// files. Triggered both by manual scrolling and by match-nav pagination
    /// (Next-match scrolls the active section into view, which fires
    /// ViewChanged → this handler).
    /// </summary>
    private void TryAutoLoadMoreOnScroll()
    {
        if (_autoLoadMoreInFlight) return;
        var list = _deferredOrderedFiles;
        var panel = _deferredButtonPanel;
        var allSelected = _deferredAllSelected;
        if (list is null || panel is null || allSelected is null) return;
        if (_deferredCursor >= list.Count) return;

        var sv = PreviewScrollViewer;
        double vpH = sv.ViewportHeight;
        if (vpH <= 0) return;
        // Trigger when within one viewport of the bottom.
        double remaining = sv.ScrollableHeight - sv.VerticalOffset;
        if (remaining > vpH) return;

        int chunkEnd = Math.Min(list.Count, _deferredCursor + AutoLoadChunkSize);
        int chunkSize = chunkEnd - _deferredCursor;
        int gen = _deferredGen;
        int pageStart = _deferredCursor;
        _autoLoadMoreInFlight = true;

        // Replace the button panel contents with a "Loading N more..." indicator
        // so the user gets feedback during the auto-load. LoadMoreSectionsAsync
        // will remove this panel and (if more remain) re-add a fresh
        // "Show more" / "Show all" button pair.
        try
        {
            panel.Children.Clear();
            panel.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Loading {chunkSize:N0} more file(s)\u2026",
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        catch { /* non-fatal cosmetic update */ }

        _ = AutoLoadMoreChunkAsync(panel, list, pageStart, chunkEnd, allSelected, gen);
    }

    private async Task AutoLoadMoreChunkAsync(
        StackPanel panel,
        List<KeyValuePair<string, List<SearchResult>>> list,
        int pageStart, int chunkEnd,
        List<SearchResult> allSelected, int gen)
    {
        try
        {
            await LoadMoreSectionsAsync(panel, list, pageStart, chunkEnd, allSelected, gen);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"AutoLoadMoreChunkAsync threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _autoLoadMoreInFlight = false;
        }
    }

    private void MaterializeVisibleLazySections()
    {
        try
        {
            if (_lazySections.Count == 0) return;
            double vpH = PreviewScrollViewer.ViewportHeight;
            if (vpH <= 0) return;
            // Pre-materialize up to ~1.5 viewports above and below the visible area
            // so users see content immediately on small scrolls instead of a flash
            // of blank/collapsed sections.
            double bufferPx = vpH * 1.5;
            var children = PreviewSectionsPanel.Children;
            // Collect targets first so we don't mutate Expander state while
            // walking the panel (the Expanding handler can reentrantly scroll
            // and call back into us via ViewChanged).
            var toExpand = new List<Expander>();
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not Expander exp || exp.Tag is not RichTextBlock block) continue;
                if (exp.IsExpanded) continue;
                if (!_lazySections.ContainsKey(block)) continue;

                double itemY, itemH;
                try
                {
                    var t = exp.TransformToVisual(PreviewScrollViewer);
                    var p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    itemY = p.Y;
                    itemH = exp.ActualHeight;
                }
                catch
                {
                    continue;
                }

                // Above viewport (and out of buffer) — skip.
                if (itemY + itemH < -bufferPx) continue;
                // Below viewport (and out of buffer) — sections are in document order,
                // so once we're past the buffer we can stop.
                if (itemY > vpH + bufferPx) break;

                toExpand.Add(exp);
            }

            // Expand each target on its own dispatcher tick so any synchronous
            // work done by the Expanding handler (paragraph build, scroll into
            // view, match-nav update) does not run inside the ViewChanged
            // callback or recurse into MaterializeVisibleLazySections.
            foreach (var exp in toExpand)
            {
                var captured = exp;
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (!captured.IsExpanded) captured.IsExpanded = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning("Preview",
                            $"MaterializeVisibleLazySections: IsExpanded threw: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview",
                $"MaterializeVisibleLazySections: sweep threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepends new file sections to the top of the preview panel.
    /// Shows a loading spinner when adding more than 50 files.
    /// </summary>
    private async Task PrependPreviewSectionsForFilesAsync(
        Dictionary<string, List<SearchResult>> newFiles, string? scrollToFile)
    {
        LogService.Instance.Info("Preview", $"PrependPreviewSectionsForFilesAsync: newFiles={newFiles.Count}, scrollToFile='{scrollToFile}'");
        if (newFiles.Count == 0) return;

        EnsurePreviewPanelVisible();
        EnsureSectionsSurface();
        PreviewToolbarContent.Visibility = Visibility.Visible;

        // Cap the initial render at PreviewSectionPageSize. Adding 10k+
        // Expanders to a flat StackPanel can crash the WinUI layout engine
        // (native fail-fast in CoreMessagingXP.dll). The remainder is paged
        // in via "Show more" using the same machinery as LoadMoreSectionsAsync.
        var orderedFiles = newFiles.ToList();
        int totalRequested = orderedFiles.Count;
        int pageEnd = Math.Min(totalRequested, PreviewSectionPageSize);
        bool deferRemainder = pageEnd < totalRequested;
        if (deferRemainder)
            LogService.Instance.Info("Preview",
                $"PrependPreviewSectionsForFilesAsync: capping initial render at {pageEnd:N0}/{totalRequested:N0}; remainder deferred to 'Show more'.");

        bool showSpinner = pageEnd > PreviewSectionPageSize / 2 || deferRemainder;
        if (showSpinner)
            ShowProgressOverlay($"Adding {pageEnd:N0} of {totalRequested:N0} files\u2026", 0);

        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);
        int previewLines = ViewModel.PreviewContextLines;

        // Batch-read file contents off the UI thread — but only for the
        // sections we will eagerly expand. Lazy/collapsed sections read their
        // file on demand inside MaterializeLazySection.
        var fileList = orderedFiles.GetRange(0, pageEnd);
        bool isHighlight = ViewModel.PreviewModeIndex == 1;
        bool bulkInsert = fileList.Count > BulkExpandLimit;
        int eagerCount = bulkInsert ? Math.Min(BulkExpandLimit, fileList.Count) : fileList.Count;
        var fileContents = await ReadAllFileContentsAsync(
            eagerCount == fileList.Count ? fileList : fileList.GetRange(0, eagerCount));

        // Build all Expanders OFF-tree first. Adding to PreviewSectionsPanel.Children
        // while the panel is in the visual tree triggers a layout invalidation per
        // insert; with 10k+ files that produces a multi-second UI freeze. Building
        // off-tree is pure C# object construction (no layout cost).
        int filesToAdd = fileList.Count;
        var built = new List<Expander>(filesToAdd);
        int fileIndex = 0;
        foreach (var (filePath, results) in fileList)
        {
            bool expanded = !bulkInsert || fileIndex < BulkExpandLimit;
            var (section, expander) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results,
                isExpanded: expanded, addToPanel: false);

            string[]? allLines = null;
            if (expanded)
                fileContents.TryGetValue(filePath, out allLines);

            if (expanded)
            {
                if (isHighlight)
                    BuildHighlightSection(section, results, allLines, previewLines, rx);
                else
                    BuildConcatenatedSection(section, results, allLines, previewLines, rx);
            }
            else
            {
                // Approximate match count without reading the file. Exact count
                // is recomputed when the section materializes.
                int lazyCount = ComputeMatchCount(results, null, isHighlight, previewLines, rx);
                _lazySections[section] = new LazySection
                {
                    FilePath = filePath,
                    Results = results,
                    AllLines = null,
                    PreviewLines = previewLines,
                    IsHighlight = isHighlight,
                    MatchCount = lazyCount,
                };
                _lazyMatchCount += lazyCount;
            }

            built.Add(expander);

            // Yield to the dispatcher periodically. We use a low-priority repost
            // (YieldLowAsync) so Input and Render priority work runs before we
            // resume — without this, Windows flags the window "Not responding"
            // even though we're pumping the queue.
            if (++fileIndex % PreviewYieldBatchSize == 0)
            {
                if (showSpinner)
                    UpdateProgressOverlay(fileIndex * 15 / filesToAdd); // build phase: 0-15%
                await YieldLowAsync();
            }
        }

        // Phase 2: insert built expanders into the live panel in batches.
        // We insert at the head (insertIndex grows) so newer files appear first.
        int insertIndex = 0;
        for (int i = 0; i < built.Count; i++)
        {
            PreviewSectionsPanel.Children.Insert(insertIndex++, built[i]);
            InvalidateScrollPositionCache();
            if ((i + 1) % PreviewYieldBatchSize == 0)
            {
                if (showSpinner)
                    UpdateProgressOverlay(15 + (i + 1) * 85 / built.Count); // insert phase: 15-100%
                await YieldLowAsync();
            }
        }

        if (showSpinner)
            HideProgressOverlay();

        // If we capped the initial render, append a "Show more" button so the
        // user can page in the rest. Uses the same LoadMoreSectionsAsync path
        // as the file-group flow.
        if (deferRemainder)
        {
            var allSelectedRemainder = new List<SearchResult>();
            for (int i = pageEnd; i < orderedFiles.Count; i++)
                allSelectedRemainder.AddRange(orderedFiles[i].Value);
            int gen = ++_previewUpdateGen;
            // Stash deferred state so the match-nav label can include not-yet-
            // inserted matches and so scroll-to-bottom can auto-load the next chunk.
            _deferredOrderedFiles = orderedFiles;
            _deferredCursor = pageEnd;
            _deferredAllSelected = allSelectedRemainder;
            _deferredGen = gen;
            AddShowMoreSectionsButton(orderedFiles, pageEnd, allSelectedRemainder, gen);
        }

        // Update match nav and file label to include the new sections.
        var totalFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        var (deferredFileCount, deferredMatchCount) = GetDeferredCounts();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount + deferredMatchCount;
        int grandFileCount = totalFiles + deferredFileCount;
        SetPreviewFileLabel(
            $"{totalMatches:N0} selected matches across {grandFileCount:N0} file(s)",
            string.Join(Environment.NewLine, GetExistingPreviewFilePaths()));
        _previewResult = newFiles.Values.First().First();
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        UpdateExpandAllButtonVisibility();

        // Scroll to the target file.
        if (scrollToFile is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                TryScrollToPreviewSection(scrollToFile);
            });
        }

        // After layout settles, materialize any lazy sections that already fall
        // within the viewport (e.g. when the viewport is taller than the
        // BulkExpandLimit-many initially expanded sections).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MaterializeVisibleLazySections);

        // Flush log synchronously so a subsequent layout-engine fail-fast still
        // leaves the prior progress lines on disk for diagnostics.
        try { LogService.Instance.Flush(); } catch { }
    }

    private async void OnFileGroupExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is FileGroup g)
        {
            LogService.Instance.Info("Preview", $"OnFileGroupExpanding: file='{g.FilePath}', matchCount={g.Count}, setting _suppressPreviewUpdate=true");
            _suppressPreviewUpdate = true;
            g.SelectAll();

            try
            {
                // If this file is already on the right panel, scroll to it.
                if (TryScrollToPreviewSection(g.FilePath))
                {
                    LogService.Instance.Info("Preview", $"OnFileGroupExpanding: already on panel, scrolled to '{g.FilePath}'");
                    return;
                }

                // Not present — prepend it.
                var results = g.Where(r => r.IsSelected).ToList();
                LogService.Instance.Info("Preview", $"OnFileGroupExpanding: not on panel, prepending {results.Count} results for '{g.FilePath}'");
                if (results.Count > 0)
                {
                    var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [g.FilePath] = results
                    };
                    await PrependPreviewSectionsForFilesAsync(newFiles, g.FilePath);
                }
            }
            finally
            {
                // Defer turning off suppression to the next low-priority frame so
                // CheckBox.Checked events from the newly-realized expander content
                // (template loading) are still suppressed.
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        LogService.Instance.Info("Preview", "OnFileGroupExpanding: deferred _suppressPreviewUpdate=false");
                        _suppressPreviewUpdate = false;
                    });
            }
        }
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe)
        {
            var g = fe.DataContext as FileGroup
                ?? (fe.DataContext is SearchResult r ? FindParentGroup(r) : null);
            if (g is not null && g.Count > 0)
                ViewModel.OpenInEditor(g[0]);
        }
    }

    private async void OnMatchLineTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't trigger preview when user clicks the checkbox itself
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or CheckBox)
            return;

        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            var selected = ViewModel.GetAllSelectedResults();
            LogService.Instance.Info("Preview", $"OnMatchLineTapped: file='{r.FilePath}', line={r.LineNumber}, totalSelected={selected.Count}");
            if (selected.Count >= 2)
                await UpdateMultiSelectPreviewAsync(scrollTarget: r);
            else
                await UpdatePreviewAsync(r);
        }
    }

    private async void OnShowFullFile(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Info("Preview", "OnShowFullFile: button clicked");
        var targets = GetFullFilePreviewTargets();
        if (targets.Count == 0)
        {
            LogService.Instance.Info("Preview", "OnShowFullFile: no targets found");
            ShowPreviewMessage("Select a file or match in the results list first.");
            ViewModel.StatusText = "Select a file or match before showing the full file.";
            return;
        }

        LogService.Instance.Info("Preview", $"OnShowFullFile: {targets.Count} target(s), files=[{string.Join(", ", targets.Select(t => System.IO.Path.GetFileName(t.FilePath)))}]");
        await ShowFullFilePreviewAsync(targets);
        LogService.Instance.Info("Preview", "OnShowFullFile: completed");
    }

    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        // Apply asynchronously / incrementally so the UI doesn't freeze when many large
        // preview sections are loaded on the right panel.
        _ = ApplyWordWrapAsync(ViewModel.PreviewWordWrap);
    }

    private async void OnPreviewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
    }

    private void OnLayoutOptionClicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, LayoutConcatenated))
            ViewModel.PreviewModeIndex = 0;
        else if (ReferenceEquals(sender, LayoutMultiHighlight))
            ViewModel.PreviewModeIndex = 1;

        SyncLayoutToggles(ViewModel.PreviewModeIndex);
        OnPreviewModeChanged(sender, new SelectionChangedEventArgs([], []));
    }

    private void SyncLayoutToggles(int index)
    {
        LayoutConcatenated.IsChecked = index == 0;
        LayoutMultiHighlight.IsChecked = index == 1;
    }

    // Synchronous variant retained for callers that already run inside an async preview
    // refresh (e.g. ShowSingleFilePreviewAsync). Only safe when the number of preview
    // sections is small or known. Prefer ApplyWordWrapAsync for user-initiated toggles.
    private void ApplyWordWrap(bool wrap)
    {
        var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var hbar = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        InvalidatePendingMatchScrolls();
        UnboxCurrentMatch();

        if (PreviewBlock.TextWrapping != wrapping)
            PreviewBlock.TextWrapping = wrapping;
        if (PreviewEditor.TextWrapping != wrapping)
            PreviewEditor.TextWrapping = wrapping;
        foreach (var block in EnumeratePreviewSectionBlocks())
        {
            if (block.TextWrapping != wrapping)
                block.TextWrapping = wrapping;
        }
        if (PreviewSectionsPanel.Visibility != Visibility.Visible)
            PreviewScrollViewer.HorizontalScrollBarVisibility = hbar;
        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (expander.Content is ScrollViewer sv)
                sv.HorizontalScrollBarVisibility = hbar;
        }
        ScrollViewer.SetHorizontalScrollBarVisibility(PreviewEditor, hbar);
    }

    private bool _applyingWordWrap;

    private async Task ApplyWordWrapAsync(bool wrap)
    {
        if (_applyingWordWrap) return;
        _applyingWordWrap = true;
        try
        {
            WordWrapToggle.IsEnabled = false;
            var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            var hbar = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            LogService.Instance.Info("Preview", $"ApplyWordWrapAsync: start wrap={wrap}");

            InvalidatePendingMatchScrolls();
            UnboxCurrentMatch();

            // Cheap, always-visible controls first.
            if (PreviewBlock.TextWrapping != wrapping)
                PreviewBlock.TextWrapping = wrapping;
            if (PreviewEditor.TextWrapping != wrapping)
                PreviewEditor.TextWrapping = wrapping;
            ScrollViewer.SetHorizontalScrollBarVisibility(PreviewEditor, hbar);
            if (PreviewSectionsPanel.Visibility != Visibility.Visible)
                PreviewScrollViewer.HorizontalScrollBarVisibility = hbar;

            // Snapshot the expanders so we don't touch the panel children mid-iteration if
            // anything reflows during a yield.
            var expanders = PreviewSectionsPanel.Children.OfType<Expander>().ToList();
            var totalSections = expanders.Count;
            if (totalSections > 0)
                ViewModel.StatusText = $"Applying word wrap to {totalSections} section(s)...";

            int processed = 0;
            foreach (var expander in expanders)
            {
                // Always toggle the per-section scrollbar (cheap, doesn't relayout the text).
                if (expander.Content is ScrollViewer sv)
                    sv.HorizontalScrollBarVisibility = hbar;

                // Only re-measure expanded sections; collapsed ones are not visible and
                // will pick up the current wrap state when re-expanded (see Expanding
                // handler in AddPreviewSection).
                if (expander.IsExpanded && expander.Tag is RichTextBlock block)
                {
                    if (block.TextWrapping != wrapping)
                        block.TextWrapping = wrapping;

                    processed++;
                    // Yield to the UI thread between heavy sections so the toggle remains
                    // responsive and the window keeps painting.
                    if (processed % 2 == 0)
                    {
                        await Task.Yield();
                        await DispatchIdleAsync();
                    }
                }
            }

            ViewModel.StatusText = string.Empty;
            LogService.Instance.Info("Preview", $"ApplyWordWrapAsync: done wrap={wrap}, sections={totalSections}, processedExpanded={processed}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", $"ApplyWordWrapAsync failed: wrap={wrap}", ex);
            ViewModel.StatusText = "Could not apply word wrap to the current preview.";
        }
        finally
        {
            WordWrapToggle.IsEnabled = true;
            _applyingWordWrap = false;
        }
    }

    private Task DispatchIdleAsync()
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult(null)))
            tcs.TrySetResult(null);
        return tcs.Task;
    }

    private async void RefreshCurrentPreview()
    {
        LogService.Instance.Verbose("Preview", "RefreshCurrentPreview called");
        if (PreviewEditor.Visibility == Visibility.Visible) return;

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
        {
            await UpdateMultiSelectPreviewAsync();
            return;
        }

        if (_previewResult is null) return;
        ViewModel.HydrateResult(_previewResult);
        await ShowSingleFilePreviewAsync(_previewResult, fullFile: false);
    }

    private async Task<bool> ClearPreviewPanelForNewSearchAsync()
    {
        if (PreviewEditor.Visibility == Visibility.Visible && HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync())
            return false;

        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        FullFileButton.IsEnabled = true;
        PreviewToolbarContent.Visibility = Visibility.Collapsed;

        // Collapse the preview panel so only the file list shows during search
        CollapsePreviewPanel();

        return true;
    }

    private void OnOpenInDefaultApp(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_previewResult.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to open in default app: {_previewResult.FilePath}", ex); }
    }

    private async void OnOpenInEditor(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        await ShowFullFileEditorAsync(_previewResult);
    }

    private async void OnExpandAllSections(object sender, RoutedEventArgs e)
    {
        ExpandAllSectionsButton.IsEnabled = false;
        try
        {
            await MaterializeAllLazySectionsAsync();
        }
        finally
        {
            ExpandAllSectionsButton.IsEnabled = true;
            UpdateExpandAllButtonVisibility();
        }
    }

    private void UpdateExpandAllButtonVisibility()
    {
        ExpandAllSectionsButton.Visibility = _lazySections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnClearPreview(object sender, RoutedEventArgs e)
    {
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
        _currentMatchIndex = -1;
        HideMatchNavPanel();

        // Building a large preview leaves a lot of long-lived allocations on
        // Gen2 (Paragraph/Run/Inline trees) and the LOH (string[] file-content
        // buffers). Plain Clear() drops references but the GC won't release
        // segments back to the OS without a forced collection, so the user
        // sees process memory stay high. Run a compacting Gen2 GC + LOH
        // compaction here — this is rare and user-initiated, so the cost is
        // acceptable in exchange for visibly reclaiming memory.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_previewResult.FilePath}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to show in Explorer: {_previewResult.FilePath}", ex); }
    }

    private void OnShowFileGroupInExplorer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to show in Explorer: {path}", ex); }
    }

    // ── Group mode menu handlers ──────────────────────────────────

    private void OnGroupModeNone(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.None;
    private void OnGroupModeFolder(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.Folder;
    private void OnGroupModeDateToday(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DateToday;
    private void OnGroupModeDateYesterday(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DateYesterday;
    private void OnGroupModeDateThisWeek(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DateThisWeek;
    private void OnGroupModeDateThisMonth(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DateThisMonth;
    private void OnGroupModeDateThisYear(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DateThisYear;
    private void OnGroupModeDatePast2Years(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DatePast2Years;
    private void OnGroupModeDatePast5Years(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DatePast5Years;
    private void OnGroupModeDatePast10Years(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DatePast10Years;
    private void OnGroupModeDatePast20Years(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DatePast20Years;
    private void OnGroupModeDatePast30Years(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DatePast30Years;
    private void OnGroupModeDatePast50Years(object sender, RoutedEventArgs e) => ViewModel.GroupModeIndex = (int)GroupMode.DatePast50Years;

    private void OnMatchLineLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBlock rtb) return;
        var dc = rtb.DataContext;
        if (dc is not SearchResult r) return;

        rtb.Blocks.Clear();
        var para = new Paragraph();
        HighlightInline(para, r.MatchLine, r.MatchStartColumn, r.MatchLength);
        rtb.Blocks.Add(para);
    }

    private void OnSelectAllFilesChecked(object sender, RoutedEventArgs e)
    {
        _suppressPreviewUpdate = true;
        try
        {
            foreach (var g in ViewModel.ResultGroups)
                g.SelectAll();
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private void OnSelectAllFilesUnchecked(object sender, RoutedEventArgs e)
    {
        _suppressPreviewUpdate = true;
        try
        {
            foreach (var g in ViewModel.ResultGroups)
                g.DeselectAll();
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressPreviewUpdate)
        {
            LogService.Instance.Info("Preview", $"OnSelectAllChecked: SUPPRESSED (group={(sender is FrameworkElement f && f.DataContext is FileGroup fg ? fg.FilePath : "?")}");
            return;
        }
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            // Skip spurious events from ListView virtualization/recycling:
            // the Checked event fires when a recycled container is re-bound,
            // but the model already has AllSelected == true.
            if (g.AllSelected)
            {
                LogService.Instance.Info("Preview", $"OnSelectAllChecked: SKIP (model already AllSelected) file='{g.FilePath}'");
                return;
            }

            int currentIndex = ViewModel.ResultGroups.IndexOf(g);
            LogService.Instance.Info("Preview", $"OnSelectAllChecked: file='{g.FilePath}', matchCount={g.Count}, index={currentIndex}");

            // Shift+Click: check all from top of list to clicked item
            bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                           .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isShift && currentIndex >= 0)
            {
                _suppressPreviewUpdate = true;
                try
                {
                    for (int i = 0; i <= currentIndex; i++)
                        ViewModel.ResultGroups[i].SelectAll();
                }
                finally { _suppressPreviewUpdate = false; }
            }
            else
            {
                _suppressPreviewUpdate = true;
                try { g.SelectAll(); }
                finally { _suppressPreviewUpdate = false; }
            }

            _lastCheckedGroupIndex = currentIndex;
        }
    }

    private void OnSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressPreviewUpdate)
        {
            LogService.Instance.Info("Preview", $"OnSelectAllUnchecked: SUPPRESSED (group={(sender is FrameworkElement f && f.DataContext is FileGroup fg ? fg.FilePath : "?")}");
            return;
        }
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            // Skip spurious events from ListView virtualization/recycling:
            // when a selected item scrolls off-screen, the container is recycled
            // and CheckBox.Unchecked fires even though the model is still selected.
            if (!g.AllSelected)
            {
                LogService.Instance.Info("Preview", $"OnSelectAllUnchecked: SKIP (model already deselected) file='{g.FilePath}'");
                return;
            }

            int currentIndex = ViewModel.ResultGroups.IndexOf(g);
            LogService.Instance.Info("Preview", $"OnSelectAllUnchecked: file='{g.FilePath}', index={currentIndex}");

            // Shift+Click: uncheck all from top of list to clicked item
            bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                           .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isShift && currentIndex >= 0)
            {
                _suppressPreviewUpdate = true;
                try
                {
                    for (int i = 0; i <= currentIndex; i++)
                        ViewModel.ResultGroups[i].DeselectAll();
                }
                finally { _suppressPreviewUpdate = false; }
            }
            else
            {
                _suppressPreviewUpdate = true;
                try { g.DeselectAll(); }
                finally { _suppressPreviewUpdate = false; }
            }

            _lastCheckedGroupIndex = currentIndex;
        }
    }

    private void OnResultsListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.A &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _suppressPreviewUpdate = true;
            try
            {
                foreach (var g in ViewModel.ResultGroups)
                    g.SelectAll();
            }
            finally { _suppressPreviewUpdate = false; }
            e.Handled = true;
        }
    }

    private void OnShowMoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
            g.ShowMore();
    }

    private void OnCopyFileGroupPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && !string.IsNullOrWhiteSpace(path))
            SetClipboardText(path, "file group path");
    }

    private void OnFileHeaderContextMenuOpening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            int count = GetCheckedFileGroups().Count;
            if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem previewItem)
                previewItem.Text = $"Preview selected ({count})";
        }
    }

    private void OnResultsContextMenuOpening(object sender, object e)
    {
        int count = GetCheckedFileGroups().Count;
        bool plural = count > 1;
        CtxPreviewSelected.Text = $"Preview selected ({count})";
        CtxCopyPaths.Text = plural ? "Copy File Paths" : "Copy File Path";
        CtxCopyWithContent.Text = plural ? "Copy Files With Content" : "Copy File With Content";
        CtxSavePaths.Text = plural ? "Save File Paths\u2026" : "Save File Path\u2026";
        CtxSaveWithContent.Text = plural ? "Save Files With Content\u2026" : "Save File With Content\u2026";
    }

    private async void OnPreviewSelectedFiles(object sender, RoutedEventArgs e)
    {
        var checkedGroups = GetCheckedFileGroups();
        var groupNames = checkedGroups.Select(g => g.FilePath).ToList();
        LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: {groupNames.Count} groups selected: [{string.Join(", ", groupNames.Select(System.IO.Path.GetFileName))}]");
        // Select all match results within each checked FileGroup
        _suppressPreviewUpdate = true;
        foreach (var g in checkedGroups)
            g.SelectAll();

        // Gather results only from the checked groups
        var selectedGroups = checkedGroups;
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in selectedGroups)
        {
            foreach (var r in g)
            {
                if (!r.IsSelected) continue;
                if (!byFile.TryGetValue(r.FilePath, out var list))
                {
                    list = new List<SearchResult>();
                    byFile[r.FilePath] = list;
                }
                list.Add(r);
            }
        }
        if (byFile.Count == 0)
        {
            LogService.Instance.Info("Preview", "OnPreviewSelectedFiles: no selected results, returning");
            return;
        }

        // Determine which files are new vs already present on the right panel.
        var existing = GetExistingPreviewFilePaths();
        LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: byFile={byFile.Count}, existingOnPanel={existing.Count}");
        var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        string? firstExistingFile = null;
        foreach (var (filePath, results) in byFile)
        {
            if (existing.Contains(filePath))
                firstExistingFile ??= filePath;
            else
                newFiles[filePath] = results;
        }

        bool isSingleFile = byFile.Count == 1;

        LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: newFiles={newFiles.Count}, isSingleFile={isSingleFile}, firstExistingFile='{firstExistingFile}'");
        try
        {
            if (newFiles.Count > 0)
            {
                // For single-file: scroll to the new file after prepending.
                // For multi-file: no scroll.
                string? scrollTo = isSingleFile ? newFiles.Keys.First() : null;
                await PrependPreviewSectionsForFilesAsync(newFiles, scrollTo);
            }
            else if (isSingleFile && firstExistingFile is not null)
            {
                // Single file already present — just scroll to it.
                LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: single file already on panel, scrolling to '{firstExistingFile}'");
                TryScrollToPreviewSection(firstExistingFile);
            }
            // Multi-file with all already present — nothing to do.
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private void OnCopySelectedFilePaths(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;
        SetClipboardText(SelectedFileExportService.BuildPathListText(paths), "selected file paths");
    }

    private async void OnCopySelectedFilesWithContent(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;

        try
        {
            var text = await SelectedFileExportService.BuildFilesWithContentTextAsync(paths).ConfigureAwait(true);
            SetClipboardText(text, "selected files with content");
            ViewModel.StatusText = $"Copied {paths.Count:N0} selected file(s) with content.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Could not copy selected files with content", ex);
            ViewModel.StatusText = $"Could not copy selected files with content: {ex.Message}";
        }
    }

    private async void OnSaveSelectedFilePaths(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;

        try
        {
            var file = await PickTextExportFileAsync("Yagu_Selected_File_Paths").ConfigureAwait(true);
            if (file is null) return;

            await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 128 * 1024, leaveOpen: false);
            await SelectedFileExportService.WritePathListAsync(paths, writer).ConfigureAwait(true);
            ViewModel.StatusText = $"Saved {paths.Count:N0} selected file path(s) to {file.Path}.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Could not save selected file paths", ex);
            ViewModel.StatusText = $"Could not save selected file paths: {ex.Message}";
        }
    }

    private async void OnSaveSelectedFilesWithContent(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0) return;

        try
        {
            var file = await PickTextExportFileAsync("Yagu_Selected_Files_With_Content").ConfigureAwait(true);
            if (file is null) return;

            await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 128 * 1024, leaveOpen: false);
            await SelectedFileExportService.WriteFilesWithContentAsync(paths, writer).ConfigureAwait(true);
            ViewModel.StatusText = $"Saved {paths.Count:N0} selected file(s) with content to {file.Path}.";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MainWindow", "Could not save selected files with content", ex);
            ViewModel.StatusText = $"Could not save selected files with content: {ex.Message}";
        }
    }

    private List<string> GetSelectedFilePaths()
    {
        return GetCheckedFileGroups()
            .Where(g => !string.IsNullOrWhiteSpace(g.FilePath))
            .Select(g => g.FilePath)
            .ToList();
    }

    /// <summary>Returns all FileGroups whose header checkbox is checked (AllSelected).</summary>
    private List<FileGroup> GetCheckedFileGroups()
    {
        return ViewModel.ResultGroups.Where(g => g.AllSelected).ToList();
    }

    private async Task<Windows.Storage.StorageFile?> PickTextExportFileAsync(string suggestedFileName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text File", new List<string> { ".txt" });
        picker.SuggestedFileName = suggestedFileName;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        return await picker.PickSaveFileAsync();
    }

    private void OnCopyPreviewFilePath(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        SetClipboardText(_previewResult.FilePath, "preview file path");
    }

    private void SetPreviewFileLabel(string text, string? tooltip = null)
    {
        PreviewFileLabel.Text = text;
        ToolTipService.SetToolTip(PreviewFileLabel, string.IsNullOrWhiteSpace(tooltip) ? text : tooltip);
        if (!string.IsNullOrWhiteSpace(text)) EnsureWidthForPreview();
    }

    /// <summary>
    /// When a preview file is shown while the window is still at the narrow
    /// launcher width (~1400 dip), grow it horizontally to a normal width so
    /// the preview pane has room. Position stays anchored on the left.
    /// </summary>
    private void EnsureWidthForPreview()
    {
        try
        {
            if (AppWindow is null) return;
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null) return;
            var wa = displayArea.WorkArea;
            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            int desiredWidth = (int)(1200 * scale);
            int curWidth = AppWindow.Size.Width;
            if (curWidth >= desiredWidth) return;

            int curX = AppWindow.Position.X;
            int curY = AppWindow.Position.Y;
            int curHeight = AppWindow.Size.Height;
            int maxWidth = Math.Max(0, wa.X + wa.Width - curX);
            int newWidth = Math.Min(desiredWidth, maxWidth);
            if (newWidth <= curWidth) return;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(curX, curY, newWidth, curHeight));
        }
        catch { }
    }

    private FileGroup? FindParentGroup(SearchResult r)
    {
        foreach (var g in ViewModel.ResultGroups)
        {
            if (string.Equals(g.FilePath, r.FilePath, StringComparison.OrdinalIgnoreCase))
                return g;
        }
        return null;
    }

    private List<FullFilePreviewTarget> GetFullFilePreviewTargets()
    {
        var selectedMatches = ViewModel.GetAllSelectedResults();
        if (selectedMatches.Count > 0)
        {
            LogService.Instance.Info("Preview", $"GetFullFilePreviewTargets: using {selectedMatches.Count} selected matches");
            return BuildFullFilePreviewTargets(selectedMatches);
        }

        var selectedGroups = GetCheckedFileGroups()
            .Where(g => g.Count > 0)
            .ToList();
        if (selectedGroups.Count > 0)
        {
            LogService.Instance.Info("Preview", $"GetFullFilePreviewTargets: using {selectedGroups.Count} checked file groups");
            var targets = new List<FullFilePreviewTarget>(selectedGroups.Count);
            foreach (var group in selectedGroups)
                targets.Add(new FullFilePreviewTarget(group.FilePath, group.ToList()));
            return targets;
        }

        if (_previewResult is null)
        {
            LogService.Instance.Info("Preview", "GetFullFilePreviewTargets: no preview result, returning empty");
            return [];
        }

        var parent = FindParentGroup(_previewResult);
        var matches = parent is null ? new List<SearchResult> { _previewResult } : parent.ToList();
        LogService.Instance.Info("Preview", $"GetFullFilePreviewTargets: fallback to current preview file='{System.IO.Path.GetFileName(_previewResult.FilePath)}', matches={matches.Count}");
        return [new FullFilePreviewTarget(_previewResult.FilePath, matches)];
    }

    private static List<FullFilePreviewTarget> BuildFullFilePreviewTargets(IReadOnlyList<SearchResult> results)
    {
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            if (!byFile.TryGetValue(result.FilePath, out var matches))
            {
                matches = new List<SearchResult>();
                byFile[result.FilePath] = matches;
            }
            matches.Add(result);
        }

        var targets = new List<FullFilePreviewTarget>(byFile.Count);
        foreach (var (filePath, matches) in byFile)
            targets.Add(new FullFilePreviewTarget(filePath, matches));
        return targets;
    }

    private void OnCopySelectedLines(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0 && sender is FrameworkElement fe && fe.DataContext is SearchResult single)
            selected = new List<SearchResult> { single };
        if (selected.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        foreach (var r in selected)
            sb.AppendLine($"{r.FilePath}:{r.LineNumber}: {r.MatchLine}");

        try
        {
            var pkg = new DataPackage();
            pkg.SetText(sb.ToString());
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { LogService.Instance.Verbose("MainWindow", "Clipboard unavailable for copy selected", ex); }
    }

    private static void SetClipboardText(string text, string description)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { LogService.Instance.Verbose("MainWindow", $"Clipboard unavailable for copy {description}", ex); }
    }

    private void OnCopySingleLine(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText($"{r.FilePath}:{r.LineNumber}: {r.MatchLine}");
                Clipboard.SetContent(pkg);
            }
            catch (Exception ex) { LogService.Instance.Verbose("MainWindow", "Clipboard unavailable for copy single", ex); }
        }
    }

    private async void OnExportSelectedLines(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0 && sender is FrameworkElement fe && fe.DataContext is SearchResult single)
            selected = new List<SearchResult> { single };
        if (selected.Count == 0) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text File", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("CSV File", new List<string> { ".csv" });
        picker.SuggestedFileName = "Yagu_Export";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var sb = new System.Text.StringBuilder();
        string ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (ext == ".csv")
        {
            sb.AppendLine("\"File\",\"Line\",\"Match\"");
            foreach (var r in selected)
            {
                var escaped = r.MatchLine.Replace("\"", "\"\"");
                sb.AppendLine($"\"{r.FilePath}\",{r.LineNumber},\"{escaped}\"");
            }
        }
        else
        {
            foreach (var r in selected)
                sb.AppendLine($"{r.FilePath}:{r.LineNumber}: {r.MatchLine}");
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, sb.ToString());
    }

    private static void HighlightInline(Paragraph para, string line, int matchStart, int matchLength)
    {
        var displayLine = LineTruncator.TruncateAroundMatch(line, matchStart, matchLength);
        line = displayLine.Text;
        matchStart = displayLine.MatchStart;
        if (matchStart >= 0 && matchStart < line.Length && matchLength > 0)
        {
            int safeLen = Math.Min(matchLength, line.Length - matchStart);
            if (matchStart > 0) para.Inlines.Add(new Run { Text = line[..matchStart] });
            var hit = new Run { Text = line.Substring(matchStart, safeLen) };
            hit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            hit.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold);
            para.Inlines.Add(hit);
            if (matchStart + safeLen < line.Length)
                para.Inlines.Add(new Run { Text = line[(matchStart + safeLen)..] });
        }
        else
        {
            para.Inlines.Add(new Run { Text = line });
        }
    }

    private async Task UpdatePreviewAsync(SearchResult r)
    {
        LogService.Instance.Info("Preview", $"UpdatePreviewAsync: file='{r.FilePath}', line={r.LineNumber}");
        if (!TryLeavePreviewEditorForPreviewChange()) return;

        EnsurePreviewPanelVisible();

        // Hydrate from disk if this result was evicted during memory pressure.
        ViewModel.HydrateResult(r);
        _previewResult = r;
        SetPreviewFileLabel(r.FilePath);
        PreviewToolbarContent.Visibility = Visibility.Visible;
        await ShowSingleFilePreviewAsync(r, fullFile: false);
    }

    private async Task UpdateMultiSelectPreviewAsync(SearchResult? scrollTarget = null, bool scrollToTop = false)
    {
        LogService.Instance.Info("Preview", $"UpdateMultiSelectPreviewAsync: scrollTarget='{scrollTarget?.FilePath}', scrollToTop={scrollToTop}, caller={new System.Diagnostics.StackTrace().GetFrame(1)?.GetMethod()?.Name}");
        int gen = ++_previewUpdateGen;

        if (!TryLeavePreviewEditorForPreviewChange()) return;

        EnsurePreviewPanelVisible();

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0)
        {
            ShowPreviewBlockSurface();
            PreviewBlock.Blocks.Clear();
            SetPreviewFileLabel(string.Empty);
            PreviewToolbarContent.Visibility = Visibility.Collapsed;
            _previewResult = null;
            return;
        }

        // Hydrate any evicted results before rendering the preview.
        foreach (var r in selected)
            ViewModel.HydrateResult(r);

        // Group by file first so we know file count for the loading message.
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in selected)
        {
            if (!byFile.TryGetValue(r.FilePath, out var list))
            {
                list = new List<SearchResult>();
                byFile[r.FilePath] = list;
            }
            list.Add(r);
        }

        // Show loading indicator immediately for large previews.
        if (byFile.Count > PreviewSectionPageSize)
            ShowPreviewLoading($"Reading {byFile.Count:N0} files\u2026");

        PreviewToolbarContent.Visibility = Visibility.Visible;
        LogService.Instance.Info("Preview", $"UpdateMultiSelectPreviewAsync: byFile={byFile.Count} files, {selected.Count} results, mode={ViewModel.PreviewModeIndex}, gen={gen}");
        if (ViewModel.PreviewModeIndex == 1)
            await ShowMultiHighlightPreviewAsync(selected, byFile, scrollTarget, gen, scrollToTop);
        else
            await ShowConcatenatedPreviewAsync(selected, byFile, scrollTarget, gen, scrollToTop);
    }

    private async Task ShowConcatenatedPreviewAsync(
        List<SearchResult> selected,
        Dictionary<string, List<SearchResult>> byFile,
        SearchResult? scrollTarget, int gen, bool scrollToTop)
    {
        LogService.Instance.Info("Preview", $"ShowConcatenatedPreviewAsync: {byFile.Count} files, {selected.Count} results, gen={gen}");
        ShowPreviewSectionsSurface();
        _matchParagraphs.Clear();
        _sectionOverflow.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        int previewLines = ViewModel.PreviewContextLines;
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        RichTextBlock? scrollBlock = null;
        Paragraph? scrollPara = null;

        // Reorder so scrollTarget's file comes first (will appear at top)
        var orderedFiles = OrderByFileFirst(byFile, scrollTarget?.FilePath).ToList();

        // Determine which files to render in this page.
        int pageEnd = Math.Min(orderedFiles.Count, PreviewSectionPageSize);
        var pageFiles = orderedFiles.GetRange(0, pageEnd);

        // Batch-read page file contents off the UI thread.
        var fileContents = await ReadAllFileContentsAsync(pageFiles);
        if (_previewUpdateGen != gen) return;
        HidePreviewLoading();

        int fileIndex = 0;
        foreach (var (filePath, results) in pageFiles)
        {
            var fileSw = System.Diagnostics.Stopwatch.StartNew();
            var (section, _) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results);

            fileContents.TryGetValue(filePath, out string[]? allLines);
            int parasInFile = 0;

            int matchIndex = 0;
            int cap = Math.Min(results.Count, MaxMatchesPerSection);
            foreach (var r in results)
            {
                if (matchIndex >= cap) break;
                matchIndex++;

                // Separator between matches in same file
                var sep = new Paragraph();
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var lineChar = '\u2500'; // ─ box-drawing horizontal
                const int shortLeft = 6;
                const int shortRight = 6;
                var sepRun = new Run { Text = $"{new string(lineChar, shortLeft)}{label}{new string(lineChar, shortRight)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                sep.Margin = new Thickness(0, 8, 0, 4);
                section.Blocks.Add(sep);

                var lines = GetPreviewLines(r, allLines, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    bool isMatchLine = lineNum == r.LineNumber;
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    var firstPara = AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: true, _matchParagraphs, sn, out int addedParagraphs);
                    parasInFile += addedParagraphs;

                    if (scrollTarget is not null && isMatchLine
                        && r.LineNumber == scrollTarget.LineNumber
                        && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        scrollBlock = section;
                        scrollPara = firstPara;
                    }
                }
            }

            if (results.Count > cap)
            {
                var notice = AppendTruncationNotice(section, results.Count, cap);
                RegisterSectionOverflow(section,
                    filePath: filePath,
                    remainingResults: results.GetRange(cap, results.Count - cap),
                    allLines: allLines,
                    previewLines: previewLines,
                    rx: rx,
                    originalTotal: results.Count,
                    renderedSoFar: cap,
                    noticePara: notice);
            }

            fileSw.Stop();
            LogService.Instance.Verbose("Preview", $"ShowConcatenatedPreviewAsync: file='{System.IO.Path.GetFileName(filePath)}', results={results.Count}, paragraphs={parasInFile}, elapsed={fileSw.ElapsedMilliseconds}ms");

            // Yield to the UI thread periodically so the app stays responsive.
            if (++fileIndex % PreviewYieldBatchSize == 0)
            {
                await Task.Delay(1).ConfigureAwait(true);
                if (_previewUpdateGen != gen) return;
            }
        }

        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];

        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();

        if (scrollBlock is not null && scrollPara is not null)
            SetCurrentMatchToParagraph(scrollBlock, scrollPara);

        if (scrollToTop)
            PreviewScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        else if (scrollBlock is not null && scrollPara is not null)
            ScrollPreviewToLine(scrollBlock, scrollPara);

        // Auto-load remaining files using the efficient off-tree batched approach.
        if (pageEnd < orderedFiles.Count)
            await AutoLoadRemainingSectionsAsync(orderedFiles, pageEnd, selected, gen);
    }

    private async Task ShowMultiHighlightPreviewAsync(
        List<SearchResult> selected,
        Dictionary<string, List<SearchResult>> byFile,
        SearchResult? scrollTarget, int gen, bool scrollToTop)
    {
        LogService.Instance.Info("Preview", $"ShowMultiHighlightPreviewAsync: {byFile.Count} files, {selected.Count} results, gen={gen}");
        ShowPreviewSectionsSurface();
        _matchParagraphs.Clear();
        _sectionOverflow.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        RichTextBlock? scrollBlock = null;
        Paragraph? scrollPara = null;

        // Reorder so scrollTarget's file comes first (will appear at top)
        var orderedFiles = OrderByFileFirst(byFile, scrollTarget?.FilePath).ToList();

        // Determine which files to render in this page.
        int pageEnd = Math.Min(orderedFiles.Count, PreviewSectionPageSize);
        var pageFiles = orderedFiles.GetRange(0, pageEnd);

        // Batch-read page file contents off the UI thread.
        var fileContents = await ReadAllFileContentsAsync(pageFiles);
        if (_previewUpdateGen != gen) return;
        HidePreviewLoading();

        int fileIndex = 0;
        foreach (var (filePath, results) in pageFiles)
        {
            var fileSw = System.Diagnostics.Stopwatch.StartNew();
            var (section, _) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results);
            int sectionMatchStart = _matchParagraphs.Count;

            fileContents.TryGetValue(filePath, out string[]? allLines);

            bool initiallyCapped = results.Count > MaxMatchesPerSection;
            var cappedResults = initiallyCapped ? results.GetRange(0, MaxMatchesPerSection) : results;
            var matchLines = new HashSet<int>(cappedResults.Select(r => r.LineNumber));
            int lastRenderedLine1 = 0;

            if (allLines != null)
            {
                // Compute line ranges to display (union of all match windows)
                int previewLines = ViewModel.PreviewContextLines;
                var ranges = new List<(int start, int end)>();
                foreach (var lineNum in matchLines.OrderBy(n => n))
                {
                    int s = Math.Max(0, lineNum - 1 - previewLines);
                    int e = Math.Min(allLines.Length - 1, lineNum - 1 + previewLines);
                    ranges.Add((s, e));
                }

                // Merge overlapping ranges
                var merged = new List<(int start, int end)>();
                foreach (var range in ranges.OrderBy(r => r.start))
                {
                    if (merged.Count > 0 && range.start <= merged[^1].end + 1)
                        merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, range.end));
                    else
                        merged.Add(range);
                }

                bool firstRange = true;
                foreach (var (start, end) in merged)
                {
                    if (!firstRange)
                    {
                        var gap = new Paragraph();
                        var gapRun = new Run { Text = "  \u22EE" };
                        gapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
                        gap.Inlines.Add(gapRun);
                        section.Blocks.Add(gap);
                    }
                    firstRange = false;

                    for (int i = start; i <= end; i++)
                    {
                        int lineNum = i + 1;
                        bool isMatchLine = matchLines.Contains(lineNum);
                        // Use first matching result for this line (for column-based highlighting)
                        var matchResult = cappedResults.FirstOrDefault(r => r.LineNumber == lineNum) ?? cappedResults[0];
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        var firstPara = AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatchLine, matchResult, rx, truncate: true, _matchParagraphs, sn, out _);

                        if (scrollTarget is not null && isMatchLine && lineNum == scrollTarget.LineNumber
                            && string.Equals(filePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = firstPara;
                        }
                    }
                }
                if (merged.Count > 0)
                    lastRenderedLine1 = merged[^1].end + 1;
            }
            else
            {
                // Fallback: concatenated style
                int fallbackIndex = 0;
                foreach (var r in cappedResults)
                {
                    var lines = GetPreviewLines(r, null, ViewModel.PreviewContextLines, fullFile: false);
                    foreach (var (line, lineNum) in lines)
                    {
                        bool isMatchLine = lineNum == r.LineNumber;
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        var firstPara = AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: true, _matchParagraphs, sn, out _);

                        if (scrollTarget is not null && isMatchLine
                            && r.LineNumber == scrollTarget.LineNumber
                            && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = firstPara;
                        }
                    }
                    fallbackIndex++;
                }
            }

            int renderedCount = Math.Min(results.Count, _matchParagraphs.Count - sectionMatchStart);
            if (allLines != null && renderedCount < Math.Min(MaxMatchesPerSection, results.Count))
            {
                _sectionMatchNavs.TryGetValue(section, out var sn);
                var pending = results.Skip(renderedCount).ToList();
                AppendHighlightMatchWindows(
                    section,
                    pending,
                    allLines,
                    rx,
                    sn,
                    MaxMatchesPerSection - renderedCount,
                    MaxMatchesPerSection - renderedCount,
                    out int consumed,
                    out _,
                    lastRenderedLine1);
                renderedCount = Math.Min(results.Count, renderedCount + consumed);
            }
            else if (allLines == null)
            {
                renderedCount = Math.Min(MaxMatchesPerSection, results.Count);
            }
            var remaining = results.Skip(renderedCount).ToList();
            if (remaining.Count > 0)
            {
                var notice = AppendTruncationNotice(section, results.Count, renderedCount);
                RegisterSectionOverflow(section,
                    filePath: filePath,
                    remainingResults: remaining,
                    allLines: allLines,
                    previewLines: ViewModel.PreviewContextLines,
                    rx: rx,
                    originalTotal: results.Count,
                    renderedSoFar: renderedCount,
                    noticePara: notice,
                    isHighlightMode: allLines != null,
                    lastRenderedLine: lastRenderedLine1);
            }

            fileSw.Stop();
            LogService.Instance.Verbose("Preview", $"ShowMultiHighlightPreviewAsync: file='{System.IO.Path.GetFileName(filePath)}', results={results.Count}, blocks={section.Blocks.Count}, elapsed={fileSw.ElapsedMilliseconds}ms");

            // Yield to the UI thread periodically so the app stays responsive.
            if (++fileIndex % PreviewYieldBatchSize == 0)
            {
                await Task.Delay(1).ConfigureAwait(true);
                if (_previewUpdateGen != gen) return;
            }
        }

        // Add "Show more" button if there are remaining files.
        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];

        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();

        // Activate the section for the clicked file and align global nav with it.
        if (scrollBlock is not null && scrollPara is not null)
            SetCurrentMatchToParagraph(scrollBlock, scrollPara);
        else if (scrollBlock is not null)
            ActivateSectionForBlock(scrollBlock);

        if (scrollToTop)
            PreviewScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        else if (scrollBlock is not null && scrollPara is not null)
            ScrollPreviewToLine(scrollBlock, scrollPara);

        // Auto-load remaining files using the efficient off-tree batched approach.
        if (pageEnd < orderedFiles.Count)
            await AutoLoadRemainingSectionsAsync(orderedFiles, pageEnd, selected, gen);
    }

    private static IEnumerable<KeyValuePair<string, List<SearchResult>>> OrderByFileFirst(
        Dictionary<string, List<SearchResult>> byFile, string? firstFilePath)
    {
        if (firstFilePath is null || !byFile.ContainsKey(firstFilePath))
            return byFile;

        // Yield the target file first, then the rest in their original order.
        return Enumerate();
        IEnumerable<KeyValuePair<string, List<SearchResult>>> Enumerate()
        {
            yield return new KeyValuePair<string, List<SearchResult>>(firstFilePath, byFile[firstFilePath]);
            foreach (var kvp in byFile)
            {
                if (!string.Equals(kvp.Key, firstFilePath, StringComparison.OrdinalIgnoreCase))
                    yield return kvp;
            }
        }
    }

    /// <summary>
    /// Read all lines using the same encoding detection as the search engine
    /// so that line numbers in the preview match the search results.
    /// </summary>

    /// <summary>Number of file sections to build before yielding to the UI message pump.</summary>
    private const int PreviewYieldBatchSize = 32;

    /// <summary>Max file sections to render in one page. Remaining are loaded on demand via "Show more".</summary>
    private const int PreviewSectionPageSize = 50;

    /// <summary>XAML paragraph chunk size for very long physical lines; all text is still rendered.</summary>
    private const int PreviewLineLayoutSegmentChars = 4096;

    /// <summary>
    /// Maximum matches to render per file section before truncating.
    /// Prevents multi-second UI freezes when a single file has hundreds
    /// of thousands of matches (e.g. 600K).
    /// </summary>
    private const int MaxMatchesPerSection = 5_000;

    /// <summary>
    /// Number of additional results to materialize per "Next match" click
    /// once a section's overflow has been registered. Smaller than the
    /// initial cap because expanded chunks are appended on the UI thread,
    /// and each result can produce many match runs (long lines split into
    /// multiple paragraphs, multi-occurrence regex matches, etc.).
    /// </summary>
    private const int MaxMatchesPerExpandChunk = 500;

    /// <summary>
    /// Hard cap on match entries (paragraphs added to <c>_matchParagraphs</c>)
    /// produced by a single <see cref="ExpandSectionNextChunk"/> call. Dense
    /// lines (many regex matches per line) can multiply the result count by
    /// 20× or more, which would freeze the UI thread for seconds.
    /// </summary>
    private const int MaxMatchEntriesPerExpandChunk = 2_000;

    /// <summary>Appends a notice paragraph when a section's matches were truncated.</summary>
    private static Paragraph AppendTruncationNotice(RichTextBlock section, int totalMatches, int renderedMatches)
    {
        var notice = new Paragraph { Margin = new Thickness(0, 12, 0, 4) };
        var run = new Run
        {
            Text = $"\u26A0 Showing first {renderedMatches:N0} of {totalMatches:N0} matches. " +
                   "Click \u2193 (Next match) to load more, or open in editor to browse all.",
        };
        run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 160, 60));
        notice.Inlines.Add(run);
        section.Blocks.Add(notice);
        return notice;
    }

    /// <summary>
    /// Registers a section as having un-rendered overflow matches so that
    /// "Next match" navigation can progressively load more chunks.
    /// </summary>
    private void RegisterSectionOverflow(
        RichTextBlock section, string? filePath, List<SearchResult> remainingResults,
        string[]? allLines, int previewLines, Regex? rx, int originalTotal, int renderedSoFar,
        Paragraph noticePara, bool isHighlightMode = false, int lastRenderedLine = 0)
    {
        _sectionOverflow[section] = new SectionOverflow
        {
            FilePath = filePath,
            RemainingResults = remainingResults,
            AllLines = allLines,
            PreviewLines = previewLines,
            Rx = rx,
            OriginalTotal = originalTotal,
            RenderedSoFar = renderedSoFar,
            NoticePara = noticePara,
            IsHighlightMode = isHighlightMode,
            LastRenderedLine = lastRenderedLine,
        };
    }

    /// <summary>
    /// Batch-read all file contents off the UI thread so the per-file loop only does
    /// XAML element construction (which must be on the UI thread) without interleaving I/O waits.
    /// </summary>
    private static async Task<Dictionary<string, string[]?>> ReadAllFileContentsAsync(
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles)
    {
        return await Task.Run(() =>
        {
            var result = new Dictionary<string, string[]?>(orderedFiles.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (filePath, _) in orderedFiles)
            {
                if (result.ContainsKey(filePath)) continue;
                try
                {
                    result[filePath] = ReadAllLinesWithEncodingSync(filePath);
                }
                catch
                {
                    result[filePath] = null;
                }
            }
            return result;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Automatically loads all remaining file sections using the efficient off-tree batched approach
    /// (same code path as "Show all files"). Called after the first page is rendered inline.
    /// </summary>
    private async Task AutoLoadRemainingSectionsAsync(
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int pageStart,
        List<SearchResult> allSelected,
        int gen)
    {
        LogService.Instance.Info("Preview", $"AutoLoadRemainingSectionsAsync: pageStart={pageStart}, totalFiles={orderedFiles.Count}, remaining={orderedFiles.Count - pageStart}, gen={gen}");
        // Create a placeholder panel that LoadMoreSectionsAsync will remove.
        var placeholder = new StackPanel();
        PreviewSectionsPanel.Children.Add(placeholder);
        InvalidateScrollPositionCache();
        await LoadMoreSectionsAsync(placeholder, orderedFiles, pageStart, orderedFiles.Count, allSelected, gen);
    }

    private void AddShowMoreSectionsButton(
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int nextIndex,
        List<SearchResult> allSelected,
        int gen)
    {
        int remaining = orderedFiles.Count - nextIndex;

        var moreBtn = new Button { Content = $"Show {remaining:N0} more file(s)\u2026" };
        var allBtn = new Button { Content = "Show all files" };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 8),
        };
        panel.Children.Add(moreBtn);
        panel.Children.Add(allBtn);

        moreBtn.Click += async (_, _) => await LoadMoreSectionsAsync(panel, orderedFiles, nextIndex, nextIndex + PreviewSectionPageSize, allSelected, gen);
        allBtn.Click += async (_, _) => await LoadMoreSectionsAsync(panel, orderedFiles, nextIndex, orderedFiles.Count, allSelected, gen);

        PreviewSectionsPanel.Children.Add(panel);
        InvalidateScrollPositionCache();

        // Track for scroll-driven auto-load and for accurate match-nav totals.
        _deferredOrderedFiles = orderedFiles;
        _deferredCursor = nextIndex;
        _deferredAllSelected = allSelected;
        _deferredGen = gen;
        _deferredButtonPanel = panel;
    }

    /// <summary>Max sections kept expanded during bulk "Show all" loads to reduce layout cost.</summary>
    private const int BulkExpandLimit = 3;

    private async Task LoadMoreSectionsAsync(
        StackPanel buttonPanel,
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int pageStart, int requestedEnd,
        List<SearchResult> allSelected,
        int gen)
    {
        LogService.Instance.Info("Preview", $"LoadMoreSectionsAsync: pageStart={pageStart}, requestedEnd={requestedEnd}, totalFiles={orderedFiles.Count}, gen={gen}");
        if (_previewUpdateGen != gen) return;

        // Remove the button panel.
        PreviewSectionsPanel.Children.Remove(buttonPanel);
            InvalidateScrollPositionCache();
        if (ReferenceEquals(_deferredButtonPanel, buttonPanel))
            _deferredButtonPanel = null;

        // When loading all remaining files, process in page-sized chunks
        // with a longer yield between pages so the layout engine stays responsive.
        bool loadingAll = requestedEnd - pageStart > PreviewSectionPageSize;
        int cursor = pageStart;
        int finalEnd = Math.Min(orderedFiles.Count, requestedEnd);
        int totalToLoad = finalEnd - pageStart;
        int totalSectionsAdded = 0;

        // Show progress overlay for "Show all" operations.
        if (loadingAll)
            ShowProgressOverlay($"Loading {totalToLoad:N0} files\u2026", 0);

        while (cursor < finalEnd)
        {
            int chunkEnd = Math.Min(finalEnd, cursor + PreviewSectionPageSize);
            var pageFiles = orderedFiles.GetRange(cursor, chunkEnd - cursor);

            // Only pre-read files we will eagerly expand in this chunk.
            // For bulk "Show all" loads, that's at most BulkExpandLimit-many
            // sections across the entire load — the rest are lazy.
            int chunkEagerCount;
            if (!loadingAll) chunkEagerCount = pageFiles.Count;
            else chunkEagerCount = Math.Max(0, Math.Min(pageFiles.Count, BulkExpandLimit - totalSectionsAdded));
            var fileContents = chunkEagerCount > 0
                ? await ReadAllFileContentsAsync(
                    chunkEagerCount == pageFiles.Count ? pageFiles : pageFiles.GetRange(0, chunkEagerCount))
                : new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
            if (_previewUpdateGen != gen) { HideProgressOverlay(); return; }

            Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);
            bool isHighlight = ViewModel.PreviewModeIndex == 1;
            int previewLines = ViewModel.PreviewContextLines;

            // Build sections off-tree to avoid layout cycles, then add in small batches.
            var pendingExpanders = new List<Expander>();
            foreach (var (filePath, results) in pageFiles)
            {
                // Collapse sections beyond the first BulkExpandLimit during bulk loads.
                bool expanded = !loadingAll || totalSectionsAdded < BulkExpandLimit;
                var (section, expander) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results,
                    isExpanded: expanded, addToPanel: false);

                string[]? allLines = null;
                if (expanded)
                    fileContents.TryGetValue(filePath, out allLines);

                if (expanded)
                {
                    // Eagerly build content for expanded sections.
                    if (isHighlight)
                        BuildHighlightSection(section, results, allLines, previewLines, rx);
                    else
                        BuildConcatenatedSection(section, results, allLines, previewLines, rx);
                }
                else
                {
                    // Defer content building for collapsed sections (lazy rendering).
                    // Skip pre-reading the file; MaterializeLazySection reads it on demand.
                    int lazyCount = ComputeMatchCount(results, null, isHighlight, previewLines, rx);
                    _lazySections[section] = new LazySection
                    {
                        FilePath = filePath,
                        Results = results,
                        AllLines = null,
                        PreviewLines = previewLines,
                        IsHighlight = isHighlight,
                        MatchCount = lazyCount,
                    };
                    _lazyMatchCount += lazyCount;
                }

                pendingExpanders.Add(expander);
                totalSectionsAdded++;
            }

            // Add built sections to the visual tree in small batches with yields.
            if (pendingExpanders.Count > 0) _lastHighlightedActiveBlock = null;
            for (int i = 0; i < pendingExpanders.Count; i++)
            {
                PreviewSectionsPanel.Children.Add(pendingExpanders[i]);
                InvalidateScrollPositionCache();

                if ((i + 1) % PreviewYieldBatchSize == 0)
                {
                    int filesLoaded = (cursor - pageStart) + i + 1;
                    if (loadingAll)
                        UpdateProgressOverlay((int)((double)filesLoaded / totalToLoad * 100));

                    await YieldLowAsync();
                    if (_previewUpdateGen != gen) { HideProgressOverlay(); return; }
                }
            }

            cursor = chunkEnd;

            // Update progress after completing each chunk.
            if (loadingAll)
                UpdateProgressOverlay((int)((double)(cursor - pageStart) / totalToLoad * 100));

            // Longer yield between pages so layout can fully process the batch.
            if (loadingAll && cursor < finalEnd)
            {
                await Task.Delay(50).ConfigureAwait(true);
                if (_previewUpdateGen != gen) { HideProgressOverlay(); return; }
            }
        }

        HideProgressOverlay();

        // Add another "Show more" button if still more remain.
        if (finalEnd < orderedFiles.Count)
            AddShowMoreSectionsButton(orderedFiles, finalEnd, allSelected, gen);
        else
        {
            // Exhausted — clear deferred state.
            _deferredOrderedFiles = null;
            _deferredAllSelected = null;
            _deferredButtonPanel = null;
            _deferredCursor = 0;
        }

        // Update match count and file count to reflect all loaded files.
        int loadedFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        var (deferredFileCount, deferredMatchCount) = GetDeferredCounts();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount + deferredMatchCount;
        int grandFileCount = loadedFiles + deferredFileCount;
        SetPreviewFileLabel(
            $"{totalMatches:N0} selected matches across {grandFileCount:N0} file(s)",
            string.Join(Environment.NewLine, orderedFiles.Take(finalEnd).Select(kv => kv.Key)));
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        UpdateExpandAllButtonVisibility();

        // Materialize any lazy sections that already fall within the viewport.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MaterializeVisibleLazySections);
    }

    private void BuildConcatenatedSection(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        var buildSw = System.Diagnostics.Stopwatch.StartNew();
        int parasBuilt = 0;
        int matchIndex = 0;
        int cap = Math.Min(results.Count, MaxMatchesPerSection);
        foreach (var r in results)
        {
            if (matchIndex >= cap) break;
            matchIndex++;

            var sep = new Paragraph();
            var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
            var sepRun = new Run { Text = $"{new string('\u2500', 6)}{label}{new string('\u2500', 6)}" };
            sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
            sep.Inlines.Add(sepRun);
            sep.Margin = new Thickness(0, 8, 0, 4);
            section.Blocks.Add(sep);

            var lines = GetPreviewLines(r, allLines, previewLines, fullFile: false);
            foreach (var (line, lineNum) in lines)
            {
                bool isMatchLine = lineNum == r.LineNumber;
                _sectionMatchNavs.TryGetValue(section, out var sn);
                AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: true, _matchParagraphs, sn, out int addedParagraphs);
                parasBuilt += addedParagraphs;
            }
        }

        if (results.Count > cap)
        {
            var notice = AppendTruncationNotice(section, results.Count, cap);
            RegisterSectionOverflow(section,
                filePath: null,
                remainingResults: results.GetRange(cap, results.Count - cap),
                allLines: allLines,
                previewLines: previewLines,
                rx: rx,
                originalTotal: results.Count,
                renderedSoFar: cap,
                noticePara: notice);
        }

        buildSw.Stop();
        LogService.Instance.Info("Preview", $"BuildConcatenatedSection: results={results.Count}, rendered={cap}, paragraphs={parasBuilt}, blocks={section.Blocks.Count}, elapsed={buildSw.ElapsedMilliseconds}ms");
    }

    private void BuildHighlightSection(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        var buildSw = System.Diagnostics.Stopwatch.StartNew();
        int parasBuilt = 0;
        int sectionMatchStart = _matchParagraphs.Count;
        bool initiallyCapped = results.Count > MaxMatchesPerSection;
        var cappedResults = initiallyCapped ? results.GetRange(0, MaxMatchesPerSection) : results;
        var matchLines = new HashSet<int>(cappedResults.Select(r => r.LineNumber));
        int lastRenderedLine1 = 0;

        if (allLines != null)
        {
            var ranges = new List<(int start, int end)>();
            foreach (var lineNum in matchLines.OrderBy(n => n))
            {
                int s = Math.Max(0, lineNum - 1 - previewLines);
                int e = Math.Min(allLines.Length - 1, lineNum - 1 + previewLines);
                ranges.Add((s, e));
            }
            var merged = new List<(int start, int end)>();
            foreach (var range in ranges.OrderBy(r => r.start))
            {
                if (merged.Count > 0 && range.start <= merged[^1].end + 1)
                    merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, range.end));
                else
                    merged.Add(range);
            }
            bool firstRange = true;
            foreach (var (start, end) in merged)
            {
                if (!firstRange)
                {
                    var gap = new Paragraph();
                    var gapRun = new Run { Text = "  \u22EE" };
                    gapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
                    gap.Inlines.Add(gapRun);
                    section.Blocks.Add(gap);
                }
                firstRange = false;
                for (int i = start; i <= end; i++)
                {
                    int lineNum = i + 1;
                    bool isMatchLine = matchLines.Contains(lineNum);
                    var matchResult = cappedResults.FirstOrDefault(r => r.LineNumber == lineNum) ?? cappedResults[0];
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatchLine, matchResult, rx, truncate: true, _matchParagraphs, sn, out int addedParagraphs);
                    parasBuilt += addedParagraphs;
                }
            }
            if (merged.Count > 0)
                lastRenderedLine1 = merged[^1].end + 1;
        }
        else
        {
            foreach (var r in cappedResults)
            {
                var lines = GetPreviewLines(r, null, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    bool isMatchLine = lineNum == r.LineNumber;
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: true, _matchParagraphs, sn, out int addedParagraphs);
                    parasBuilt += addedParagraphs;
                }
            }
        }

        int renderedCount = Math.Min(results.Count, _matchParagraphs.Count - sectionMatchStart);
        if (allLines != null && renderedCount < Math.Min(MaxMatchesPerSection, results.Count))
        {
            _sectionMatchNavs.TryGetValue(section, out var sn);
            var pending = results.Skip(renderedCount).ToList();
            int addedEntries = AppendHighlightMatchWindows(
                section,
                pending,
                allLines,
                rx,
                sn,
                MaxMatchesPerSection - renderedCount,
                MaxMatchesPerSection - renderedCount,
                out int consumed,
                out int addedParagraphs,
                lastRenderedLine1);
            renderedCount = Math.Min(results.Count, renderedCount + consumed);
            parasBuilt += addedParagraphs;
            _ = addedEntries;
        }
        else if (allLines == null)
        {
            renderedCount = Math.Min(MaxMatchesPerSection, results.Count);
        }

        var remaining = results.Skip(renderedCount).ToList();
        if (remaining.Count > 0)
        {
            var notice = AppendTruncationNotice(section, results.Count, renderedCount);
            RegisterSectionOverflow(section,
                filePath: null,
                remainingResults: remaining,
                allLines: allLines,
                previewLines: previewLines,
                rx: rx,
                originalTotal: results.Count,
                renderedSoFar: renderedCount,
                noticePara: notice,
                isHighlightMode: allLines != null,
                lastRenderedLine: lastRenderedLine1);
        }

        buildSw.Stop();
        LogService.Instance.Info("Preview", $"BuildHighlightSection: results={results.Count}, rendered={cappedResults.Count}, paragraphs={parasBuilt}, blocks={section.Blocks.Count}, hasAllLines={allLines != null}, elapsed={buildSw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Pre-computes the total regex match count for a section without building any UI elements.
    /// Used for lazy sections so the global nav label can show the correct total.
    /// </summary>
    private static int ComputeMatchCount(
        List<SearchResult> results, string[]? allLines,
        bool isHighlight, int previewLines, Regex? rx)
    {
        int total = 0;
        if (isHighlight && allLines != null)
        {
            var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));
            foreach (int lineNum in matchLines)
            {
                int idx = lineNum - 1;
                if (idx >= 0 && idx < allLines.Length)
                    total += CountRegexMatches(allLines[idx], rx);
            }
        }
        else if (isHighlight)
        {
            // Approximation when we haven't read the file yet: count matches
            // across one MatchLine per unique line number. MatchLine may be
            // truncated (ShortPreview), so this can undercount on very long
            // lines, but it's recomputed exactly when the section materializes.
            var seen = new HashSet<int>();
            foreach (var r in results)
            {
                if (seen.Add(r.LineNumber))
                    total += CountRegexMatches(r.MatchLine, rx);
            }
        }
        else
        {
            foreach (var r in results)
                total += CountRegexMatches(r.MatchLine, rx);
        }
        return total;
    }

    /// <summary>
    /// Materializes a lazy section: builds paragraphs, adds match entries, and removes it from the lazy dictionary.
    /// Returns true if the section was lazy and has been materialized.
    /// </summary>
    private bool MaterializeLazySection(RichTextBlock section)
    {
        if (!_lazySections.Remove(section, out var lazy))
            return false;

        var matSw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Instance.Info("Preview", $"MaterializeLazySection: file='{System.IO.Path.GetFileName(lazy.FilePath)}', matches={lazy.MatchCount}");

        // Lazy file read: bulk inserts skip the upfront read for collapsed
        // sections. Read the single file now, on demand.
        string[]? allLines = lazy.AllLines;
        if (allLines is null)
        {
            try { allLines = ReadAllLinesWithEncodingSync(lazy.FilePath); }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"MaterializeLazySection: read failed for '{lazy.FilePath}': {ex.GetType().Name}: {ex.Message}");
                allLines = null;
            }
        }

        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);
        if (lazy.IsHighlight)
            BuildHighlightSection(section, lazy.Results, allLines, lazy.PreviewLines, rx);
        else
            BuildConcatenatedSection(section, lazy.Results, allLines, lazy.PreviewLines, rx);

        _lazyMatchCount -= lazy.MatchCount;
        matSw.Stop();
        LogService.Instance.Info("Preview", $"MaterializeLazySection complete: file='{System.IO.Path.GetFileName(lazy.FilePath)}', elapsed={matSw.ElapsedMilliseconds}ms, remainingLazy={_lazySections.Count}");
        return true;
    }

    /// <summary>
    /// Materializes all remaining lazy sections at once, with a progress overlay.
    /// </summary>
    private bool _suppressExpandingHandler;

    private async Task MaterializeAllLazySectionsAsync()
    {
        if (_lazySections.Count == 0) return;

        var lazyBlocks = _lazySections.Keys.ToList();
        int total = lazyBlocks.Count;
        LogService.Instance.Info("Preview", $"MaterializeAllLazySectionsAsync: starting, total={total}");
        ShowProgressOverlay($"Rendering {total:N0} sections\u2026", 0);

        try
        {
            int done = 0;
            foreach (var block in lazyBlocks)
            {
                try
                {
                    MaterializeLazySection(block);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview", "MaterializeLazySection failed for one section; skipping.", ex);
                }
                done++;
                if (done % PreviewYieldBatchSize == 0 || done == total)
                {
                    UpdateProgressOverlay(done * 100 / total);
                    // Use DispatchIdleAsync (DispatcherQueue.TryEnqueue) instead of
                    // Task.Delay(1).ConfigureAwait(true). On WinUI 3 the UI thread does
                    // not necessarily have a SynchronizationContext installed, so a
                    // Task.Delay continuation can resume on a threadpool thread; any
                    // subsequent XAML touch from there triggers a CoreMessagingXP
                    // fail-fast (exception 0xE0464645). DispatchIdleAsync guarantees
                    // we resume on the UI thread.
                    await DispatchIdleAsync();
                }
            }

            LogService.Instance.Info("Preview", $"MaterializeAllLazySectionsAsync: materialization complete, expanding sections");
            UpdateMatchNavPanel();
            UpdateSectionMatchNavPanels();
        }
        finally
        {
            // Hide the overlay before the expand phase. Expanding many Expanders triggers
            // WinUI layout passes that can take noticeable time, but the user's "spinner
            // forever at 100%" feedback was that the overlay made it look frozen.
            HideProgressOverlay();
        }

        // Expand all sections without firing the per-section Expanding side effects
        // (which would call MaterializeLazySection (no-op now) and ActivateSectionForBlock,
        // and the latter is O(N) per call so the bulk expand becomes O(N^2) and
        // appears to hang for hundreds of sections).
        //
        // Crash mitigation: with 2000+ Expanders, flipping IsExpanded in a tight loop
        // queues thousands of concurrent expand-state storyboards and content-reveal
        // theme transitions on CoreMessagingXP, which fail-fasts (0xE0464645) when its
        // dispatcher buffers fill up. Two mitigations applied here:
        //   1) Clear the per-Expander ContentTransitions so each expansion does not
        //      schedule the default reveal theme transition (the biggest offender).
        //   2) Yield to the UI dispatcher via DispatchIdleAsync between batches so the
        //      messaging queue can drain. Smaller batch (1) than the materialize loop
        //      because each Expander expansion still triggers a layout pass.
        _suppressExpandingHandler = true;
        try
        {
            int expanded = 0;
            const int expandYieldBatchSize = 1;
            foreach (var child in PreviewSectionsPanel.Children)
            {
                if (child is Expander exp && !exp.IsExpanded)
                {
                    try
                    {
                        // Suppress the content-reveal theme transition that fires
                        // when IsExpanded flips. The chevron-rotation storyboard in
                        // the Expander template still runs but is comparatively cheap.
                        exp.ContentTransitions = null;
                        exp.IsExpanded = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning("Preview", "Failed to expand section.", ex);
                    }
                }
                expanded++;
                if (expanded % expandYieldBatchSize == 0)
                    await DispatchIdleAsync();
            }
        }
        finally
        {
            _suppressExpandingHandler = false;
        }
        LogService.Instance.Info("Preview", "MaterializeAllLazySectionsAsync: done");
    }

    private static string[] ReadAllLinesWithEncodingSync(string filePath)
    {
        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var encoding = Helpers.EncodingDetector.DetectEncoding(fs);
        if (encoding is System.Text.UTF8Encoding)
            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        fs.Position = 0;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();

        if (content.Length == 0)
            return Array.Empty<string>();

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                int end = (i > start && content[i - 1] == '\r') ? i - 1 : i;
                lines.Add(content[start..end]);
                start = i + 1;
            }
        }
        if (start < content.Length)
        {
            int end = content[content.Length - 1] == '\r' ? content.Length - 1 : content.Length;
            lines.Add(content[start..end]);
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Reads all lines from a file, splitting only on <c>\n</c> (stripping optional
    /// trailing <c>\r</c>).  This matches the Rust <c>bstr::ByteSlice::lines()</c>
    /// behaviour used by the native search engine so that line numbers agree between
    /// the searcher and the preview panel.  C#'s <c>StreamReader.ReadLine</c> also
    /// splits on lone <c>\r</c>, which creates phantom extra lines in binary files
    /// and causes the highlighted line to drift from the actual match.
    /// </summary>
    private static async Task<string[]> ReadAllLinesWithEncodingAsync(string filePath)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var encoding = Helpers.EncodingDetector.DetectEncoding(fs);
        if (encoding is System.Text.UTF8Encoding)
            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        fs.Position = 0;
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync();

        // Split on \n only (matching bstr::lines), strip trailing \r from each line.
        if (content.Length == 0)
            return Array.Empty<string>();

        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                int end = (i > start && content[i - 1] == '\r') ? i - 1 : i;
                lines.Add(content[start..end]);
                start = i + 1;
            }
        }
        // Trailing content after last \n (bstr::lines includes it if non-empty).
        if (start < content.Length)
        {
            int end = content[content.Length - 1] == '\r' ? content.Length - 1 : content.Length;
            lines.Add(content[start..end]);
        }
        return lines.ToArray();
    }

    private async Task ExpandSectionToFullFileAsync(RichTextBlock section, string filePath, List<SearchResult> results)
    {
        // Remove lazy section data if it was never rendered
        if (_lazySections.Remove(section, out var lazySec))
        {
            _lazyMatchCount -= lazySec.MatchCount;
            UpdateExpandAllButtonVisibility();
        }

        string[]? allLines = null;
        try { allLines = await ReadAllLinesWithEncodingAsync(filePath); }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", $"Cannot read file for full-file section preview: {filePath}", ex);
            return;
        }

        var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        int insertionIndex = _matchParagraphs.FindIndex(m => ReferenceEquals(m.block, section));
        if (insertionIndex < 0)
            insertionIndex = _matchParagraphs.Count;

        int currentSectionOrdinal = -1;
        for (int i = 0, ordinal = 0; i < _matchParagraphs.Count; i++)
        {
            if (!ReferenceEquals(_matchParagraphs[i].block, section))
                continue;

            if (i == _currentMatchIndex)
            {
                currentSectionOrdinal = ordinal;
                break;
            }

            ordinal++;
        }

        UnboxCurrentMatch();

        _matchParagraphs.RemoveAll(m => m.block == section);
        _sectionMatchNavs.TryGetValue(section, out var sn);
        sn?.Matches.Clear();

        section.Blocks.Clear();
        InvalidateParagraphIndexCache(section);
        var sectionMatches = new List<(RichTextBlock block, Paragraph para, int matchInPara)>();
        for (int i = 0; i < allLines.Length; i++)
        {
            int lineNum = i + 1;
            bool isMatch = matchLines.Contains(lineNum);
            var matchResult = isMatch
                ? results.FirstOrDefault(r => r.LineNumber == lineNum) ?? results[0]
                : results[0];
            AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatch, matchResult, rx, truncate: false, sectionMatches, sn, out _);
        }

        (Paragraph para, int matchInPara)? matchToReveal = null;
        if (sectionMatches.Count > 0)
        {
            insertionIndex = Math.Clamp(insertionIndex, 0, _matchParagraphs.Count);
            _matchParagraphs.InsertRange(insertionIndex, sectionMatches);

            int revealOrdinal = currentSectionOrdinal >= 0
                ? Math.Min(currentSectionOrdinal, sectionMatches.Count - 1)
                : 0;
            var revealEntry = sectionMatches[revealOrdinal];
            matchToReveal = (revealEntry.para, revealEntry.matchInPara);
        }

        if (allLines.Length == 0)
        {
            var para = new Paragraph();
            var run = new Run { Text = "(empty file)" };
            run.Foreground = s_contextTextBrush;
            para.Inlines.Add(run);
            section.Blocks.Add(para);
        }

        // Update navigation state
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        if (matchToReveal is { } reveal)
        {
            SetCurrentMatchToMatch(section, reveal.para, reveal.matchInPara);
            ScrollPreviewToLine(section, reveal.para);
        }
    }

    private static List<(string line, int lineNum)> GetPreviewLines(SearchResult r, string[]? allLines, int previewLines, bool fullFile)
    {
        var lines = new List<(string, int)>();
        int matchLineNum = r.LineNumber;

        if (allLines != null)
        {
            int matchIdx = matchLineNum - 1;
            if (matchIdx < 0) matchIdx = 0;
            if (matchIdx >= allLines.Length) matchIdx = allLines.Length - 1;

            int startLine, endLine;
            if (fullFile)
            {
                startLine = 0;
                endLine = allLines.Length - 1;
            }
            else
            {
                startLine = Math.Max(0, matchIdx - previewLines);
                endLine = Math.Min(allLines.Length - 1, matchIdx + previewLines);
            }
            for (int i = startLine; i <= endLine; i++)
            {
                var line = !fullFile && i == matchIdx ? r.MatchLine : allLines[i];
                lines.Add((line, i + 1));
            }
        }
        else
        {
            int ln = matchLineNum - r.ContextBefore.Count;
            foreach (var line in r.ContextBefore) lines.Add((line, ln++));
            lines.Add((r.MatchLine, matchLineNum));
            ln = matchLineNum + 1;
            foreach (var line in r.ContextAfter) lines.Add((line, ln++));
        }
        return lines;
    }

    private async Task ShowSingleFilePreviewAsync(SearchResult r, bool fullFile)
    {
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync: file='{r.FilePath}', line={r.LineNumber}, fullFile={fullFile}");
        var singleSw = System.Diagnostics.Stopwatch.StartNew();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        string[]? allLines = null;
        if (fullFile)
        {
            try { allLines = await ReadAllLinesWithEncodingAsync(r.FilePath); } catch (Exception ex) { LogService.Instance.Warning("Preview", $"Cannot read file for single-file preview: {r.FilePath}", ex); }
        }

        int lineCount = 0;
        var lines = GetPreviewLines(r, allLines, ViewModel.PreviewContextLines, fullFile);
        foreach (var (line, lineNum) in lines)
        {
            bool isMatchLine = lineNum == r.LineNumber;
            AddPreviewLineParagraphs(PreviewBlock, line, lineNum, isMatchLine, r, rx, truncate: !fullFile, null, null, out int addedParagraphs);
            lineCount += addedParagraphs;
        }
        singleSw.Stop();
        LogService.Instance.Info("Preview", $"ShowSingleFilePreviewAsync complete: lines={lineCount}, blocks={PreviewBlock.Blocks.Count}, elapsed={singleSw.ElapsedMilliseconds}ms");
    }

    private void ShowPreviewBlockSurface()
    {
        PreviewSectionsPanel.Children.Clear();
        PreviewSectionsPanel.Visibility = Visibility.Collapsed;
        PreviewBlock.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Visible);
        HideMatchNavPanel();
        // Restore outer horizontal scroll for single-file block view.
        PreviewScrollViewer.HorizontalScrollBarVisibility =
            ViewModel.PreviewWordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void ShowPreviewSectionsSurface()
    {
        LogService.Instance.Info("Preview", $"ShowPreviewSectionsSurface: clearing {PreviewSectionsPanel.Children.Count} existing sections");
        PreviewBlock.Blocks.Clear();
        PreviewBlock.Visibility = Visibility.Collapsed;
        PreviewSectionsPanel.Children.Clear();
        PreviewSectionsPanel.Visibility = Visibility.Visible;
        HidePreviewLoading();
        SetPerFileToolbarVisibility(Visibility.Collapsed);
        HideMatchNavPanel();
        // Sections have their own per-section horizontal scroll; outer viewer stays vertical-only.
        PreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private void ShowPreviewLoading(string message = "Loading preview\u2026")
    {
        PreviewMessagePanel.Visibility = Visibility.Collapsed;
        PreviewSectionsPanel.Visibility = Visibility.Collapsed;
        PreviewLoadingText.Text = message;
        PreviewLoadingRing.IsActive = true;
        PreviewLoadingPanel.Visibility = Visibility.Visible;
    }

    private void HidePreviewLoading()
    {
        PreviewLoadingRing.IsActive = false;
        PreviewLoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowProgressOverlay(string message, int percent)
    {
        PreviewProgressText.Text = message;
        PreviewProgressPercent.Text = $"{percent}%";
        PreviewProgressRing.IsActive = true;
        PreviewProgressOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateProgressOverlay(int percent)
    {
        PreviewProgressPercent.Text = $"{percent}%";
    }

    private void HideProgressOverlay()
    {
        PreviewProgressRing.IsActive = false;
        PreviewProgressOverlay.Visibility = Visibility.Collapsed;
    }

    private void SetPerFileToolbarVisibility(Visibility visibility)
    {
        CopyPreviewFilePathButton.Visibility = visibility;
        PreviewToolbarSeparator.Visibility = visibility;
        FullFileButton.Visibility = visibility;
        OpenInDefaultAppButton.Visibility = visibility;
        OpenInEditorButton.Visibility = visibility;
    }

    private IEnumerable<RichTextBlock> EnumeratePreviewSectionBlocks()
    {
        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (expander.Content is ScrollViewer sv && sv.Content is Border { Child: RichTextBlock block })
                yield return block;
            else if (expander.Content is Border { Child: RichTextBlock legacyBlock })
                yield return legacyBlock;
        }
    }

    private (RichTextBlock block, Expander expander) AddPreviewSection(string filePath, string? detail = null, List<SearchResult>? results = null, bool isExpanded = true, bool addToPanel = true)
    {
        LogService.Instance.Verbose("Preview", $"AddPreviewSection: file='{System.IO.Path.GetFileName(filePath)}', detail='{detail}', expanded={isExpanded}, addToPanel={addToPanel}");
        var block = new RichTextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = ViewModel.PreviewWordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };

        var content = new Border
        {
            Padding = new Thickness(8, 4, 0, 8),
            Child = block,
        };
        block.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => ActivateSectionForBlock(block)),
            handledEventsToo: true);

        // Double-click anywhere in this section's preview text to open the
        // inline editor positioned at the clicked line. Use AddHandler with
        // handledEventsToo so RichTextBlock's built-in text-selection logic
        // (which marks DoubleTapped handled when it selects a word) doesn't
        // swallow the event before we see it.
        var capturedSectionPath = filePath;
        block.AddHandler(UIElement.DoubleTappedEvent,
            new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(async (s, e) =>
            {
                if (s is RichTextBlock rtb)
                    await EnterPreviewEditorAtPointAsync(rtb, e, capturedSectionPath);
            }),
            handledEventsToo: true);

        var sectionScroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ViewModel.PreviewWordWrap
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var sectionNav = new SectionMatchNav
        {
            Scroller = sectionScroller,
            Block = block,
        };
        _sectionMatchNavs[block] = sectionNav;

        var expander = new Expander
        {
            Header = BuildPreviewSectionHeader(filePath, detail, block, results),
            Content = sectionScroller,
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = block,
        };
        expander.Expanding += (s, _) =>
        {
            InvalidateScrollPositionCache();
            if (_suppressExpandingHandler) return;
            try
            {
                if (s is Expander exp && exp.Tag is RichTextBlock b)
                {
                    UpdateExpandAllButtonVisibility();
                    if (MaterializeLazySection(b) && MatchNavPanel.Visibility == Visibility.Visible)
                        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
                    // Re-apply the current wrap state in case the user toggled wrap while
                    // this section was collapsed (we skip collapsed sections in
                    // ApplyWordWrapAsync to keep the toggle responsive for huge previews).
                    var wrap = ViewModel.PreviewWordWrap;
                    b.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    if (exp.Content is ScrollViewer scroller)
                        scroller.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
                    ActivateSectionForBlock(b);
                }
            }
            catch (Exception ex)
            {
                // A managed exception that escapes a XAML callback fail-fasts the
                // process via CoreMessagingXP. Catch + log so we survive.
                LogService.Instance.Warning("Preview",
                    $"Expander.Expanding handler threw: {ex.GetType().Name}: {ex.Message}");
                try { LogService.Instance.Flush(); } catch { }
            }
        };
        // Tooltip is set on the header grid only (BuildPreviewSectionHeader);
        // setting it on the Expander itself would also show when hovering the
        // content body, which is noisy.
        _blockExpanderCache[block] = expander;
        _expanderFilePaths[expander] = filePath;
        _expanderHeaderArgs[expander] = (filePath, detail, block, results);
        if (addToPanel)
        {
            PreviewSectionsPanel.Children.Add(expander);
            InvalidateScrollPositionCache();
            _lastHighlightedActiveBlock = null;
        }
        return (block, expander);
    }

    private FrameworkElement BuildPreviewSectionHeader(string filePath, string? detail, RichTextBlock? sectionBlock = null, List<SearchResult>? sectionResults = null)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };

        var infoPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        infoPanel.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });

        infoPanel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(filePath),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 360,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            infoPanel.Children.Add(new TextBlock
            {
                Text = detail,
                Opacity = 0.65,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        grid.Children.Add(infoPanel);

        // Per-file action buttons — right-aligned
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(buttonPanel, 1);

        var path = filePath; // capture for lambdas

        if (sectionBlock is not null && sectionResults is not null)
        {
            var fullFileBtn = new Button
            {
                Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                Content = new FontIcon { Glyph = "\uE81E", FontSize = 12 },
            };
            ToolTipService.SetToolTip(fullFileBtn, "Show full file");
            var capturedBlock = sectionBlock;
            var capturedResults = sectionResults;
            fullFileBtn.Click += async (_, _) =>
            {
                fullFileBtn.IsEnabled = false;
                await ExpandSectionToFullFileAsync(capturedBlock, path, capturedResults);
            };
            buttonPanel.Children.Add(fullFileBtn);
        }

        var copyBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
        };
        ToolTipService.SetToolTip(copyBtn, "Copy full file path");
        copyBtn.Click += (_, _) => SetClipboardText(path, "section file path");
        buttonPanel.Children.Add(copyBtn);

        var openBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 12 },
        };
        ToolTipService.SetToolTip(openBtn, "Open with default application");
        openBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to open in default app: {path}", ex); }
        };
        buttonPanel.Children.Add(openBtn);

        var editorBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 12 },
        };
        ToolTipService.SetToolTip(editorBtn, "Edit file");
        editorBtn.Click += async (_, _) =>
        {
            var result = ViewModel.ResultGroups
                .FirstOrDefault(g => string.Equals(g.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?.FirstOrDefault();
            if (result is not null)
                await ShowFullFileEditorAsync(result);
        };
        buttonPanel.Children.Add(editorBtn);

        // Open containing folder in Explorer
        var explorerBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uED25", FontSize = 12 },
        };
        ToolTipService.SetToolTip(explorerBtn, "Open containing folder in Explorer");
        explorerBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
            catch (Exception ex) { LogService.Instance.Warning("MainWindow", $"Failed to show in Explorer: {path}", ex); }
        };
        buttonPanel.Children.Add(explorerBtn);

        // Dismiss button — remove this file section from the preview
        var dismissBtn = new Button
        {
            Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
        };
        ToolTipService.SetToolTip(dismissBtn, "Remove from preview");
        if (sectionBlock is not null)
        {
            var capturedBlock = sectionBlock;
            var capturedPath = filePath;
            dismissBtn.Click += (_, _) => RemovePreviewSection(capturedBlock, capturedPath);
        }
        buttonPanel.Children.Add(dismissBtn);

        grid.Children.Add(buttonPanel);

        ToolTipService.SetToolTip(grid, filePath);
        return grid;
    }

    private void RemovePreviewSection(RichTextBlock block, string filePath)
    {
        // Find and remove the Expander containing this block
        for (int i = PreviewSectionsPanel.Children.Count - 1; i >= 0; i--)
        {
            if (PreviewSectionsPanel.Children[i] is Expander expander
                && expander.Content is ScrollViewer sv
                && sv.Content is Border border
                && border.Child == block)
            {
                PreviewSectionsPanel.Children.RemoveAt(i);
                _blockExpanderCache.Remove(block);
                InvalidateParagraphIndexCache(block);
                _lastHighlightedActiveBlock = null;

                // Remove per-section match nav data
                _sectionMatchNavs.Remove(block);
                _expanderFilePaths.Remove(expander);
                _expanderHeaderArgs.Remove(expander);
                if (ReferenceEquals(_stickyHeaderExpander, expander))
                {
                    _stickyHeaderExpander = null;
                    StickyFileHeader.Child = null;
                    StickyFileHeader.Visibility = Visibility.Collapsed;
                }

                // Remove lazy section data if it was never rendered
                if (_lazySections.Remove(block, out var lazy))
                    _lazyMatchCount -= lazy.MatchCount;

                // Remove global matches for this block
                _matchParagraphs.RemoveAll(m => m.block == block);
                _currentMatchIndex = -1;
                UpdateMatchNavPanel();
                UpdateSectionMatchNavPanels();

                // Deselect and collapse the file group in the left panel
                var group = ViewModel.ResultGroups
                    .FirstOrDefault(g => string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (group is not null)
                {
                    group.DeselectAll();
                    group.IsExpanded = false;
                }
                return;
            }
        }
    }

    private async Task ShowFullFilePreviewAsync(IReadOnlyList<FullFilePreviewTarget> targets)
    {
        LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: {targets.Count} targets");
        if (!TryLeavePreviewEditorForPreviewChange()) return;

        _previewLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;

        var tooltip = string.Join(Environment.NewLine, targets.Select(t => t.FilePath));
        SetPreviewFileLabel(
            targets.Count == 1 ? targets[0].FilePath : $"{targets.Count:N0} selected files",
            tooltip);
        ShowPreviewMessage(targets.Count == 1
            ? $"Loading full file preview for {Path.GetFileName(targets[0].FilePath)}..."
            : $"Loading full file preview for {targets.Count:N0} files...");

        try
        {
            Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);
            ShowPreviewSectionsSurface();

            int filesLoaded = 0;
            (RichTextBlock block, Paragraph para, int matchInPara)? firstMatch = null;
            (RichTextBlock block, Paragraph para, int matchInPara)? preferredMatch = null;
            foreach (var target in targets)
            {
                cts.Token.ThrowIfCancellationRequested();

                foreach (var result in target.Matches)
                    ViewModel.HydrateResult(result);

                try
                {
                    LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: loading file '{System.IO.Path.GetFileName(target.FilePath)}', matches={target.Matches.Count}");
                    var document = await LoadPreviewDocumentAsync(target.FilePath, cts.Token).ConfigureAwait(true);
                    LogService.Instance.Info("Preview", $"ShowFullFilePreviewAsync: loaded '{System.IO.Path.GetFileName(target.FilePath)}', bytes={document.ByteLength:N0}, textLen={document.Text.Length:N0}");
                    var section = AddFullFileSection(target, document.ByteLength);
                    _sectionMatchNavs.TryGetValue(section, out var sectionNav);
                    var renderedMatch = RenderFullFileDocument(section, target, document.Text, rx, _matchParagraphs, sectionNav, _previewResult);
                    firstMatch ??= renderedMatch.firstMatch;
                    preferredMatch ??= renderedMatch.preferredMatch;
                    filesLoaded++;
                }
                catch (OperationCanceledException) { throw; }
                catch (PreviewLoadException ex)
                {
                    var section = AddFullFileSection(target, byteLength: null);
                    AddFullFileError(section, ex.Message);
                }
                catch (OutOfMemoryException ex)
                {
                    const string message = "Not enough memory to load this full file into the right-panel preview.";
                    LogService.Instance.Warning("Preview", message, ex);
                    var section = AddFullFileSection(target, byteLength: null);
                    AddFullFileError(section, message);
                }
                catch (Exception ex)
                {
                    var message = $"Could not load full file: {ex.Message}";
                    LogService.Instance.Warning("Preview", $"Could not load full file: {target.FilePath}", ex);
                    var section = AddFullFileSection(target, byteLength: null);
                    AddFullFileError(section, message);
                }
            }

            _previewResult = targets[0].Matches[0];
            UpdateMatchNavPanel();
            UpdateSectionMatchNavPanels();

            var matchToReveal = preferredMatch ?? firstMatch;
            if (matchToReveal is { } match)
            {
                SetCurrentMatchToMatch(match.block, match.para, match.matchInPara);
                ScrollPreviewToLine(match.block, match.para);
            }

            ViewModel.StatusText = targets.Count == 1
                ? $"Loaded full file preview for {Path.GetFileName(targets[0].FilePath)}."
                : $"Loaded full file preview for {filesLoaded:N0}/{targets.Count:N0} selected files.";
        }
        catch (OperationCanceledException)
        {
            ShowPreviewMessage("Full-file preview cancelled.");
        }
        finally
        {
            if (ReferenceEquals(_previewLoadCts, cts))
                _previewLoadCts = null;
            cts.Dispose();
            FullFileButton.IsEnabled = true;
            UpdatePreviewEditorButtons();
        }
    }

    private RichTextBlock AddFullFileSection(FullFilePreviewTarget target, long? byteLength)
    {
        var detail = byteLength.HasValue ? FormatBytes(byteLength.Value) : null;
        return AddPreviewSection(target.FilePath, detail).block;
    }

    private static void AddFullFileError(RichTextBlock section, string message)
    {
        var para = new Paragraph();
        var run = new Run { Text = message };
        run.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        para.Inlines.Add(run);
        section.Blocks.Add(para);
    }

    private static (
        (RichTextBlock block, Paragraph para, int matchInPara)? firstMatch,
        (RichTextBlock block, Paragraph para, int matchInPara)? preferredMatch)
        RenderFullFileDocument(
            RichTextBlock section,
            FullFilePreviewTarget target,
            string text,
            Regex? rx,
            List<(RichTextBlock block, Paragraph para, int matchInPara)> matchParagraphs,
            SectionMatchNav? sectionNav,
            SearchResult? preferredResult)
    {
        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        var matchByLine = new Dictionary<int, SearchResult>();
        foreach (var result in target.Matches.OrderBy(r => r.LineNumber))
        {
            if (!matchByLine.ContainsKey(result.LineNumber))
                matchByLine[result.LineNumber] = result;
        }

        using var reader = new StringReader(text);
        string? line;
        int lineNumber = 1;
        bool wroteLine = false;
        (RichTextBlock block, Paragraph para, int matchInPara)? firstMatch = null;
        (RichTextBlock block, Paragraph para, int matchInPara)? preferredMatch = null;
        while ((line = reader.ReadLine()) is not null)
        {
            var isMatchLine = matchByLine.TryGetValue(lineNumber, out var matchResult);
            int beforeCount = matchParagraphs.Count;
            AddPreviewLineParagraphs(section, line, lineNumber, isMatchLine, matchResult ?? target.Matches[0], rx, truncate: false, matchParagraphs, sectionNav, out _);
            if (isMatchLine && matchParagraphs.Count > beforeCount)
            {
                var entry = matchParagraphs[beforeCount];
                firstMatch ??= entry;
                if (preferredResult is not null
                    && lineNumber == preferredResult.LineNumber
                    && string.Equals(target.FilePath, preferredResult.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    preferredMatch ??= entry;
                }
            }
            wroteLine = true;
            lineNumber++;
        }

        renderSw.Stop();
        LogService.Instance.Info("Preview", $"RenderFullFileDocument: file='{System.IO.Path.GetFileName(target.FilePath)}', lines={lineNumber - 1}, matches={matchByLine.Count}, blocks={section.Blocks.Count}, elapsed={renderSw.ElapsedMilliseconds}ms");

        if (!wroteLine)
        {
            var para = new Paragraph();
            var run = new Run { Text = "(empty file)" };
            run.Foreground = s_contextTextBrush;
            para.Inlines.Add(run);
            section.Blocks.Add(para);
        }

        return (firstMatch, preferredMatch);
    }

    private async void OnPreviewBlockDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is RichTextBlock rtb)
            await EnterPreviewEditorAtPointAsync(rtb, e, _previewResult?.FilePath);
    }

    // EnterPreviewEditorAtPointAsync, ResolveLineNumberAtPointer, ShowFullFileEditorAsync,
    // ScrollEditorToMatch, CenterEditorOnSelectionAfterScroll moved to MainWindow.PreviewEditor.cs.

    private static async Task<PreviewTextDocument> LoadPreviewDocumentAsync(string filePath, CancellationToken cancellationToken, bool enforceLimit = true)
    {
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Instance.Verbose("Preview", $"LoadPreviewDocumentAsync: start file='{System.IO.Path.GetFileName(filePath)}'");

        // Handle archive entry paths: extract the entry to a memory stream
        if (ZipArchiveSearcher.IsArchivePath(filePath))
        {
            LogService.Instance.Verbose("Preview", $"LoadPreviewDocumentAsync: archive path, delegating to LoadArchiveEntryPreviewAsync");
            return await LoadArchiveEntryPreviewAsync(filePath, cancellationToken, enforceLimit).ConfigureAwait(false);
        }

        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch (Exception ex) { throw new PreviewLoadException($"Could not inspect full file: {ex.Message}"); }

        if (!info.Exists)
            throw new PreviewLoadException("Could not load full file: it no longer exists.");
        if (enforceLimit && info.Length > FullFilePreviewLimitBytes)
            throw new PreviewLoadException($"Full-file preview is limited to {FormatBytes(FullFilePreviewLimitBytes)}. This file is {FormatBytes(info.Length)}.");

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int probeSize = (int)Math.Min(BinaryDetector.SampleBytes, Math.Max(0, info.Length));
            if (probeSize > 0)
            {
                var probe = new byte[probeSize];
                int read = await stream.ReadAsync(probe.AsMemory(0, probe.Length), cancellationToken).ConfigureAwait(false);
                if (BinaryDetector.IsBinary(probe.AsSpan(0, read)))
                    throw new PreviewLoadException("Full-file editing is only available for non-binary text files.");
            }

            stream.Position = 0;
            var encoding = EncodingDetector.DetectEncoding(stream);
            // Use a replacement fallback so non-UTF-8 files render with '�' instead of throwing.
            if (encoding is UTF8Encoding)
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            loadSw.Stop();
            LogService.Instance.Info("Preview", $"LoadPreviewDocumentAsync: done file='{System.IO.Path.GetFileName(filePath)}', bytes={info.Length:N0}, textLen={text.Length:N0}, elapsed={loadSw.ElapsedMilliseconds}ms");
            return new PreviewTextDocument(text, reader.CurrentEncoding, info.Length);
        }
        catch (PreviewLoadException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new PreviewLoadException($"Could not load full file: access denied. {ex.Message}"); }
        catch (DecoderFallbackException ex) { throw new PreviewLoadException($"Could not load full file: unsupported text encoding. {ex.Message}"); }
        catch (IOException ex) { throw new PreviewLoadException($"Could not load full file: {ex.Message}"); }
    }

    private static async Task<PreviewTextDocument> LoadArchiveEntryPreviewAsync(string archivePath, CancellationToken cancellationToken, bool enforceLimit = true)
    {
        try
        {
            using var ms = await ZipArchiveSearcher.ExtractToMemoryAsync(archivePath, cancellationToken).ConfigureAwait(false);
            long byteLength = ms.Length;

            if (enforceLimit && byteLength > FullFilePreviewLimitBytes)
                throw new PreviewLoadException($"Full-file preview is limited to {FormatBytes(FullFilePreviewLimitBytes)}. This entry is {FormatBytes(byteLength)}.");

            int probeSize = (int)Math.Min(BinaryDetector.SampleBytes, Math.Max(0, byteLength));
            if (probeSize > 0)
            {
                var probe = new byte[probeSize];
                int read = await ms.ReadAsync(probe.AsMemory(0, probeSize), cancellationToken).ConfigureAwait(false);
                if (BinaryDetector.IsBinary(probe.AsSpan(0, read)))
                    throw new PreviewLoadException("Full-file editing is only available for non-binary text files.");
                ms.Position = 0;
            }

            var encoding = EncodingDetector.DetectEncoding(ms);
            if (encoding is UTF8Encoding)
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            ms.Position = 0;
            using var reader = new StreamReader(ms, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return new PreviewTextDocument(text, reader.CurrentEncoding, byteLength);
        }
        catch (PreviewLoadException) { throw; }
        catch (FileNotFoundException ex) { throw new PreviewLoadException($"Could not find archive entry: {ex.Message}"); }
        catch (InvalidDataException ex) { throw new PreviewLoadException($"Could not read archive: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { throw new PreviewLoadException($"Access denied to archive: {ex.Message}"); }
        catch (IOException ex) { throw new PreviewLoadException($"Could not load archive entry: {ex.Message}"); }
    }

    // SavePreviewEditAsync, ConfirmDiscardPreviewEditAsync, TryLeavePreviewEditorForPreviewChange,
    // ClosePreviewEditor, SetPreviewEditorVisible, HasRealEditorChanges,
    // UpdatePreviewEditorButtons, UpdateEditorDirtyIndicator moved to MainWindow.PreviewEditor.cs.

    private void ShowPreviewMessage(string message, bool showBackButton = false)
    {
        SetPreviewEditorVisible(false);
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        var para = new Paragraph();
        para.Inlines.Add(new Run { Text = message });
        PreviewBlock.Blocks.Add(para);
        PreviewBackButton.Visibility = showBackButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPreviewBackClick(object sender, RoutedEventArgs e)
    {
        PreviewBackButton.Visibility = Visibility.Collapsed;

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
        else if (_previewResult is { } result)
            await UpdatePreviewAsync(result);
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : bytes >= mb ? $"{bytes / mb:F1} MB" : bytes >= kb ? $"{bytes / kb:F1} KB" : $"{bytes} B";
    }

    private sealed record PreviewTextDocument(string Text, Encoding Encoding, long ByteLength);

    private sealed record FullFilePreviewTarget(string FilePath, List<SearchResult> Matches);

    private sealed class PreviewLoadException(string message) : Exception(message);

    private static string GetEncodingDisplayName(Encoding enc)
    {
        var name = enc.WebName.ToUpperInvariant();
        if (name == "UTF-8" && enc.GetPreamble().Length > 0) return "UTF-8 BOM";
        if (name == "UTF-16") return "UTF-16 LE";
        return name;
    }

    /// <summary>
    /// Returns true if <paramref name="text"/> contains characters that cannot
    /// be losslessly encoded with <paramref name="encoding"/> (e.g. lone surrogates).
    /// </summary>
    private static bool TextHasUnencodableCharacters(string text, Encoding encoding)
    {
        try
        {
            var strict = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            strict.GetByteCount(text);
            return false;
        }
        catch (EncoderFallbackException)
        {
            return true;
        }
    }

    private static Regex? BuildHighlightRegex(string query, bool caseSensitive, bool useRegex)
    {
        if (string.IsNullOrEmpty(query)) return null;
        try
        {
            var options = RegexOptions.Compiled;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            string pattern = useRegex ? query : Regex.Escape(query);
            return new Regex(pattern, options);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Preview", "Invalid highlight regex", ex);
            return null;
        }
    }

    private static readonly SolidColorBrush s_matchGutterBrush = new(Microsoft.UI.Colors.LimeGreen);

    /// <summary>
    /// Returns the number of individual regex match occurrences on a line (minimum 1 for a match line).
    /// </summary>
    private static int CountRegexMatches(string? line, Regex? rx)
    {
        return CountRegexMatches(line, rx, minimumOne: true);
    }

    private static int CountRegexMatches(string? line, Regex? rx, bool minimumOne)
    {
        if (rx is null || string.IsNullOrEmpty(line)) return minimumOne ? 1 : 0;
        int count = rx.Matches(line).Count;
        return count > 0 || !minimumOne ? count : 1;
    }

    private static void AddMatchEntries(
        List<(RichTextBlock block, Paragraph para, int matchInPara)> matchParagraphs,
        SectionMatchNav? sn,
        RichTextBlock section, Paragraph para,
        string? line, Regex? rx,
        bool minimumOne = true)
    {
        int count = CountRegexMatches(line, rx, minimumOne);
        for (int i = 0; i < count; i++)
        {
            matchParagraphs.Add((section, para, i));
            sn?.Matches.Add((para, i));
        }
    }

    private static readonly SolidColorBrush s_contextGutterBrush = new(Windows.UI.Color.FromArgb(255, 80, 80, 80));
    private static readonly SolidColorBrush s_gutterSepBrush = new(Windows.UI.Color.FromArgb(255, 60, 60, 60));
    private static readonly SolidColorBrush s_contextTextBrush = new(Windows.UI.Color.FromArgb(255, 110, 110, 110));
    private static readonly SolidColorBrush s_matchAccentBrush = new(Windows.UI.Color.FromArgb(255, 70, 140, 70));

    private static Paragraph AddPreviewLineParagraphs(
        RichTextBlock section,
        string? line,
        int lineNum,
        bool isMatchLine,
        SearchResult result,
        Regex? rx,
        bool truncate,
        List<(RichTextBlock block, Paragraph para, int matchInPara)>? matchParagraphs,
        SectionMatchNav? sectionNav,
        out int paragraphsAdded)
    {
        line ??= string.Empty;
        if (truncate)
            line = TruncatePreviewLine(line, rx);

        Paragraph? firstParagraph = null;
        bool addedMatchEntries = false;
        paragraphsAdded = 0;

        foreach (var segment in EnumeratePreviewLineLayoutSegments(line))
        {
            var para = MakePreviewParagraph(segment, lineNum, isMatchLine, result, rx, truncate: false);
            section.Blocks.Add(para);
            firstParagraph ??= para;
            paragraphsAdded++;

            if (!isMatchLine || matchParagraphs is null)
                continue;

            int beforeCount = matchParagraphs.Count;
            AddMatchEntries(
                matchParagraphs,
                sectionNav,
                section,
                para,
                segment,
                rx,
                minimumOne: rx is null && !addedMatchEntries);
            if (matchParagraphs.Count > beforeCount)
                addedMatchEntries = true;
        }

        if (isMatchLine && matchParagraphs is not null && !addedMatchEntries && firstParagraph is not null)
            AddMatchEntries(matchParagraphs, sectionNav, section, firstParagraph, string.Empty, rx: null);

        return firstParagraph ?? throw new InvalidOperationException("Preview line renderer did not create a paragraph.");
    }

    private readonly record struct PreviewLineWindow(string Text, int SourceStart, int SourceEnd);

    private static PreviewLineWindow TruncatePreviewLineAroundResult(string? line, SearchResult result, Regex? rx)
    {
        line ??= string.Empty;
        int matchStart = result.MatchStartColumn;
        int matchLength = result.MatchLength;
        if (matchStart < 0 || matchLength <= 0 || matchStart >= line.Length)
        {
            var match = rx?.Match(line);
            if (match is { Success: true, Length: > 0 })
            {
                matchStart = match.Index;
                matchLength = match.Length;
            }
        }

        if (LineTruncator.TruncatedLength == 0
            || line.Length <= LineTruncator.MaxDisplayLength
            || matchStart < 0
            || matchLength <= 0
            || matchStart >= line.Length)
        {
            return new PreviewLineWindow(LineTruncator.Truncate(line), 0, line.Length);
        }

        int start = Math.Max(0, matchStart);
        int end = Math.Min(line.Length, start + LineTruncator.TruncatedLength);

        string prefix = start > 0 ? LineTruncator.Ellipsis : string.Empty;
        string suffix = end < line.Length ? LineTruncator.Ellipsis : string.Empty;
        string text = string.Concat(prefix, line.AsSpan(start, end - start), suffix);
        return new PreviewLineWindow(text, start, end);
    }

    private static Paragraph AddPreviewLineParagraphsAroundResult(
        RichTextBlock section,
        string? line,
        int lineNum,
        SearchResult result,
        Regex? rx,
        List<(RichTextBlock block, Paragraph para, int matchInPara)>? matchParagraphs,
        SectionMatchNav? sectionNav,
        out int paragraphsAdded,
        out int matchEntriesAdded,
        bool continuationGutter = false)
    {
        var window = TruncatePreviewLineAroundResult(line, result, rx);
        Paragraph? firstParagraph = null;
        bool addedMatchEntries = false;
        paragraphsAdded = 0;
        matchEntriesAdded = 0;

        foreach (var segment in EnumeratePreviewLineLayoutSegments(window.Text))
        {
            var para = MakePreviewParagraph(segment, lineNum, isMatchLine: true, result, rx, truncate: false, continuationGutter: continuationGutter || firstParagraph is not null);
            section.Blocks.Add(para);
            firstParagraph ??= para;
            paragraphsAdded++;

            if (matchParagraphs is null)
                continue;

            int beforeCount = matchParagraphs.Count;
            AddMatchEntries(
                matchParagraphs,
                sectionNav,
                section,
                para,
                segment,
                rx,
                minimumOne: rx is null && !addedMatchEntries);
            int added = matchParagraphs.Count - beforeCount;
            if (added > 0)
            {
                addedMatchEntries = true;
                matchEntriesAdded += added;
            }
        }

        if (matchParagraphs is not null && !addedMatchEntries && firstParagraph is not null)
        {
            int beforeCount = matchParagraphs.Count;
            AddMatchEntries(matchParagraphs, sectionNav, section, firstParagraph, string.Empty, rx: null);
            matchEntriesAdded += matchParagraphs.Count - beforeCount;
        }

        return firstParagraph ?? throw new InvalidOperationException("Preview line renderer did not create a paragraph.");
    }

    private int AppendHighlightMatchWindows(
        RichTextBlock section,
        List<SearchResult> pendingResults,
        string[] allLines,
        Regex? rx,
        SectionMatchNav? sectionNav,
        int maxAdditionalMatchEntries,
        int maxResultsToConsume,
        out int consumedResults,
        out int paragraphsAdded,
        int previouslyRenderedLine = 0)
    {
        consumedResults = 0;
        paragraphsAdded = 0;
        int addedMatchEntries = 0;
        int previousLine = 0;

        while (consumedResults < pendingResults.Count
               && consumedResults < maxResultsToConsume
               && addedMatchEntries < maxAdditionalMatchEntries)
        {
            var result = pendingResults[consumedResults];
            int lineIndex = result.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= allLines.Length)
            {
                consumedResults++;
                continue;
            }

            if (previousLine > 0 && result.LineNumber > previousLine + 1)
            {
                var gap = new Paragraph();
                var gapRun = new Run { Text = "  \u22EE" };
                gapRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
                gap.Inlines.Add(gapRun);
                section.Blocks.Add(gap);
            }
            bool continuationGutter = result.LineNumber <= previouslyRenderedLine || result.LineNumber == previousLine;
            previousLine = result.LineNumber;
            AddPreviewLineParagraphsAroundResult(
                section,
                allLines[lineIndex],
                result.LineNumber,
                result,
                rx,
                _matchParagraphs,
                sectionNav,
                out int addedParagraphs,
                out int visibleMatches,
                continuationGutter);

            paragraphsAdded += addedParagraphs;
            int consume = Math.Max(1, visibleMatches);
            consume = Math.Min(consume, pendingResults.Count - consumedResults);
            consumedResults += consume;
            addedMatchEntries += visibleMatches;
        }

        return addedMatchEntries;
    }

    private static IEnumerable<string> EnumeratePreviewLineLayoutSegments(string line)
    {
        if (line.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        if (line.Length <= PreviewLineLayoutSegmentChars)
        {
            yield return line;
            yield break;
        }

        for (int start = 0; start < line.Length; start += PreviewLineLayoutSegmentChars)
        {
            int length = Math.Min(PreviewLineLayoutSegmentChars, line.Length - start);
            yield return line.Substring(start, length);
        }
    }

    private static Paragraph MakePreviewParagraph(string line, int lineNum, bool isMatchLine, SearchResult r, Regex? rx, bool truncate = true, bool continuationGutter = false)
    {
        line ??= string.Empty;
        if (truncate)
            line = TruncatePreviewLine(line, rx);
        var para = new Paragraph();

        // Match indicator + line number gutter.
        // Use a glyph that Consolas renders at full cell width so match lines
        // align horizontally with context lines (which use a plain space).
        var indicator = new Run { Text = isMatchLine ? "│" : " " };
        indicator.Foreground = isMatchLine ? s_matchAccentBrush : s_contextTextBrush;
        para.Inlines.Add(indicator);

        var gutterRun = new Run { Text = continuationGutter ? $"{lineNum,5} (cont) " : $"{lineNum,5} " };
        gutterRun.Foreground = continuationGutter ? s_matchAccentBrush : (isMatchLine ? s_matchGutterBrush : s_contextGutterBrush);
        para.Inlines.Add(gutterRun);
        var gutterSep = new Run { Text = "│ " };
        gutterSep.Foreground = s_gutterSepBrush;
        para.Inlines.Add(gutterSep);

        // Highlight matches only on the actual match line, not context lines.
        if (rx != null && isMatchLine)
        {
            int lastIdx = 0;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(line))
            {
                if (m.Index > lastIdx)
                {
                    var before = new Run { Text = line[lastIdx..m.Index] };
                    if (!isMatchLine) before.Foreground = s_contextTextBrush;
                    para.Inlines.Add(before);
                }
                var hit = new Run { Text = m.Value };
                hit.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                hit.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold);
                para.Inlines.Add(hit);
                lastIdx = m.Index + m.Length;
            }
            if (lastIdx < line.Length)
            {
                var tail = new Run { Text = line[lastIdx..] };
                if (!isMatchLine) tail.Foreground = s_contextTextBrush;
                para.Inlines.Add(tail);
            }
        }
        else
        {
            var plain = new Run { Text = line };
            if (!isMatchLine) plain.Foreground = s_contextTextBrush;
            para.Inlines.Add(plain);
        }

        return para;
    }

    private static string TruncatePreviewLine(string line, Regex? rx)
    {
        if (rx is not null)
        {
            var match = rx.Match(line);
            if (match.Success && match.Length > 0)
                return LineTruncator.TruncateAroundMatch(line, match.Index, match.Length).Text;
        }

        return LineTruncator.Truncate(line);
    }

    private void BoxMatchRun(Paragraph para, int matchInPara)
    {
        UnboxCurrentMatch();
        var matches = GetMatchRunsForParagraph(para);
        if ((uint)matchInPara >= (uint)matches.Count)
        {
            _paragraphMatchRunCache.Remove(para);
            matches = GetMatchRunsForParagraph(para);
            if ((uint)matchInPara >= (uint)matches.Count)
                return;
        }

        var (run, column) = matches[matchInPara];
        _activeMatchHighlight = (para, run, column, matchInPara);
        if (LogService.Instance.IsVerboseEnabled)
        {
            int paragraphIndex = _matchParagraphs.Count > 0
                ? GetParagraphIndex(_matchParagraphs[Math.Clamp(_currentMatchIndex, 0, _matchParagraphs.Count - 1)].block, para)
                : -1;
            LogService.Instance.Verbose("MatchNav", $"BoxMatchRun: idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}/{matches.Count}, col={column}, runText='{run.Text}'");
        }
    }

    private List<(Run run, int column)> GetMatchRunsForParagraph(Paragraph para)
    {
        if (_paragraphMatchRunCache.TryGetValue(para, out var matches))
            return matches;

        matches = new List<(Run run, int column)>();
        int column = 0;
        for (int i = 0; i < para.Inlines.Count; i++)
        {
            if (para.Inlines[i] is not Run run)
                continue;

            if (IsSearchMatchRun(run))
                matches.Add((run, column));

            column += run.Text?.Length ?? 0;
        }
        _paragraphMatchRunCache[para] = matches;
        return matches;
    }

    private static bool IsSearchMatchRun(Run run)
        => run.FontWeight.Weight == Microsoft.UI.Text.FontWeights.Bold.Weight
           && run.Foreground is SolidColorBrush brush
           && brush.Color == Microsoft.UI.Colors.Gold;

    private void UnboxCurrentMatch([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_activeMatchHighlight is null) return;
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"UnboxCurrentMatch: idx={_currentMatchIndex}, caller={caller}");
        _activeMatchHighlight = null;
    }

    /// <summary>
    /// Scrolls the outer preview ScrollViewer so that <paramref name="targetPara"/>
    /// inside <paramref name="block"/> is visible, optionally centered.
    /// Must be called after the content has been added to the visual tree;
    /// actual scrolling is deferred to a low-priority dispatcher tick so layout
    /// has time to complete.
    /// </summary>
    private void ScrollPreviewToLine(RichTextBlock block, Paragraph targetPara, bool forceCenter = true)
    {
        int requestId = ++_matchScrollRequestId;
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav",
                $"ScrollPreviewToLine: entry idx={_currentMatchIndex}, requestId={requestId}, mode=estimated, forceCenter={forceCenter}");

        if (TryScrollPreviewToLine(block, targetPara, verifyAfterScroll: false, forceCenter, out _))
            return;

        ScrollPreviewToLine(block, targetPara, attemptsRemaining: 3, requestId, forceCenter);
    }

    /// <summary>
    /// Variant used immediately after MaterializeNextLazySection: forces a synchronous
    /// layout pass on the freshly-expanded section so the run's character rect is
    /// available BEFORE the first ScrollPreviewToLine, then hooks LayoutUpdated to
    /// re-center if the section continues to settle on subsequent layout passes.
    /// Without this the corrective scroll loop in VerifyActiveMatchVisibleAfterScroll
    /// converges on a stale position because the Expander's content is still reflowing.
    /// </summary>
    private void ScrollAfterMaterialization(RichTextBlock block, Paragraph targetPara)
    {
        try
        {
            block.UpdateLayout();
            PreviewScrollViewer.UpdateLayout();
        }
        catch { }

        ScrollPreviewToLine(block, targetPara);

        // Re-center on subsequent layout passes too: when many lines/Runs in a freshly
        // expanded section are measured/arranged, the absolute Y of our paragraph
        // can shift by hundreds of pixels after our initial scroll. Hook
        // LayoutUpdated on the PreviewScrollViewer (NOT the block — the Expander
        // animation reflows ancestors/siblings without firing block.LayoutUpdated
        // on every pass) and re-issue the scroll if the active run drifts off
        // screen. We track two timers: an "idle" timer reset on every rescroll
        // (so we keep watching as long as layout is still moving) and an
        // absolute hard cap to guard against pathological infinite reflow.
        int requestId = _matchScrollRequestId;
        int idxAtAttach = _currentMatchIndex;
        EventHandler<object>? handler = null;
        var idleStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var hardCapStopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int kIdleWindowMs = 1500;        // detach if no rescroll needed for 1.5 s
        const int kHardCapMs = 5000;           // never watch longer than 5 s total
        const int kMaxRescrolls = 30;
        int rescrolls = 0;
        int consecutiveOnScreen = 0;
        int layoutPasses = 0;
        LogService.Instance.Info("MatchNav",
            $"ScrollAfterMaterialization: attach idx={idxAtAttach}, requestId={requestId}");
        handler = (_, __) =>
        {
            if (handler is null) return;
            layoutPasses++;
            if (requestId != _matchScrollRequestId
                || idleStopwatch.ElapsedMilliseconds > kIdleWindowMs
                || hardCapStopwatch.ElapsedMilliseconds > kHardCapMs
                || rescrolls >= kMaxRescrolls)
            {
                string reason = requestId != _matchScrollRequestId
                    ? $"new-request(curr={_matchScrollRequestId})"
                    : (hardCapStopwatch.ElapsedMilliseconds > kHardCapMs
                        ? "hard-cap"
                        : (idleStopwatch.ElapsedMilliseconds > kIdleWindowMs ? "idle" : "max-rescrolls"));
                // Take a final snapshot of the active highlight at detach time.
                // If the run has drifted off-screen since our last rescroll AND we
                // still have hardCap budget AND this is an idle (not new-request /
                // hard-cap / max-rescrolls) detach, issue one more corrective
                // scroll and STAY attached. Without this rescue, materialize-path
                // navs commonly look correct at first verify (~30ms in) but then
                // the section continues reflowing for another 100-1500ms, the run
                // drifts off-screen, no LayoutUpdated fires for the final settle,
                // and we detach silently \u2014 leaving the user looking at empty
                // space below the match.
                string snapshot = "(no-highlight)";
                bool snapshotOnScr = true; // assume on-screen if we can't measure
                double snapshotRunY = double.NaN;
                double snapshotRunH = double.NaN;
                try
                {
                    if (_activeMatchHighlight is { para: var ap, run: var ar } && ar is not null)
                    {
                        var fg = (ar.Foreground as SolidColorBrush)?.Color.ToString() ?? "(non-solid)";
                        var rect2 = ar.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                        var t2 = block.TransformToVisual(PreviewScrollViewer);
                        var p2 = t2.TransformPoint(new Windows.Foundation.Point(rect2.X, rect2.Y));
                        double vTop = PreviewScrollViewer.VerticalOffset;
                        double vH = PreviewScrollViewer.ViewportHeight;
                        double rY = vTop + p2.Y;
                        bool onScr = rect2.Height > 0 && rY >= vTop && rY + rect2.Height <= vTop + vH;
                        bool sameRun = ReferenceEquals(ap, targetPara);
                        snapshot = $"fg={fg}, sameRun={sameRun}, onScr={onScr}, runY={rY:N1}, runH={rect2.Height:N1}";
                        snapshotOnScr = onScr || rect2.Height <= 0;
                        snapshotRunY = rY;
                        snapshotRunH = rect2.Height;
                    }
                }
                catch { }

                bool isIdleDetach = reason == "idle";
                bool canRescue = isIdleDetach
                    && !snapshotOnScr
                    && !double.IsNaN(snapshotRunY)
                    && !double.IsNaN(snapshotRunH)
                    && snapshotRunH > 0
                    && hardCapStopwatch.ElapsedMilliseconds < kHardCapMs - 200
                    && rescrolls < kMaxRescrolls;
                if (canRescue)
                {
                    double vH2 = PreviewScrollViewer.ViewportHeight;
                    double target = snapshotRunY - vH2 / 2 + snapshotRunH / 2;
                    target = Math.Clamp(target, 0, PreviewScrollViewer.ScrollableHeight);
                    double vpTop2 = PreviewScrollViewer.VerticalOffset;
                    if (Math.Abs(target - vpTop2) > 1)
                    {
                        rescrolls++;
                        idleStopwatch.Restart();
                        consecutiveOnScreen = 0;
                        bool accepted2 = PreviewScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                        LogService.Instance.Info("MatchNav",
                            $"ScrollAfterMaterialization: detach-rescue idx={idxAtAttach}, snapshot={snapshot}, fromY={vpTop2:N1}, toY={target:N1}, accepted={accepted2}, rescrolls={rescrolls}, total={hardCapStopwatch.ElapsedMilliseconds}ms (staying attached)");
                        return; // do NOT detach \u2014 keep watching
                    }
                }

                LogService.Instance.Info("MatchNav",
                    $"ScrollAfterMaterialization: detach idx={idxAtAttach}, reason={reason}, layoutPasses={layoutPasses}, rescrolls={rescrolls}, idle={idleStopwatch.ElapsedMilliseconds}ms, total={hardCapStopwatch.ElapsedMilliseconds}ms, finalVpTop={PreviewScrollViewer.VerticalOffset:N1}, snapshot={snapshot}");
                PreviewScrollViewer.LayoutUpdated -= handler;
                handler = null;
                return;
            }
            try
            {
                if (_activeMatchHighlight is { para: var activePara, run: var activeRun }
                    && ReferenceEquals(activePara, targetPara) && activeRun is not null)
                {
                    var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    if (rect.Height > 0)
                    {
                        var t = block.TransformToVisual(PreviewScrollViewer);
                        var p = t.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                        double vpTop = PreviewScrollViewer.VerticalOffset;
                        double vpH = PreviewScrollViewer.ViewportHeight;
                        double runY = vpTop + p.Y;
                        bool onScreen = runY >= vpTop && runY + rect.Height <= vpTop + vpH;
                        if (!onScreen)
                        {
                            consecutiveOnScreen = 0;
                            double target = runY - vpH / 2 + rect.Height / 2;
                            target = Math.Clamp(target, 0, PreviewScrollViewer.ScrollableHeight);
                            if (Math.Abs(target - vpTop) > 1)
                            {
                                rescrolls++;
                                idleStopwatch.Restart();
                                bool accepted = PreviewScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                                LogService.Instance.Info("MatchNav",
                                    $"ScrollAfterMaterialization: post-layout re-center #{rescrolls} idx={_currentMatchIndex}, runY={runY:N1}, vpTop={vpTop:N1}, vpH={vpH:N1}, toY={target:N1}, accepted={accepted}, total={hardCapStopwatch.ElapsedMilliseconds}ms");
                            }
                        }
                        else
                        {
                            consecutiveOnScreen++;
                            // Two consecutive on-screen confirmations \u2192 layout has stabilised,
                            // BUT only allow early detach after the WinUI Expander animation
                            // has had time to play (~600 ms). Otherwise we routinely detach
                            // 1\u20132 ms in, before the animation re-shuffles layout and pushes
                            // the active run back off-screen with no one watching.
                            const int kMinElapsedForEarlyDetachMs = 600;
                            if (consecutiveOnScreen >= 2 && hardCapStopwatch.ElapsedMilliseconds >= kMinElapsedForEarlyDetachMs)
                            {
                                LogService.Instance.Info("MatchNav",
                                    $"ScrollAfterMaterialization: detach idx={idxAtAttach}, reason=stable-on-screen, layoutPasses={layoutPasses}, rescrolls={rescrolls}, idle={idleStopwatch.ElapsedMilliseconds}ms, total={hardCapStopwatch.ElapsedMilliseconds}ms, finalVpTop={PreviewScrollViewer.VerticalOffset:N1}, runY={runY:N1}");
                                PreviewScrollViewer.LayoutUpdated -= handler;
                                handler = null;
                            }
                        }
                    }
                }
            }
            catch
            {
                if (handler is not null)
                {
                    PreviewScrollViewer.LayoutUpdated -= handler;
                    handler = null;
                }
            }
        };
        PreviewScrollViewer.LayoutUpdated += handler;

        // Also poll on a 100ms DispatcherTimer. LayoutUpdated only fires when WinUI
        // actually invalidates layout, but the Expander animation can finish a
        // pass and leave the run drifted off-screen without firing another
        // LayoutUpdated. Polling guarantees drift is caught within ~100ms instead
        // of waiting for the 1.5s idle timeout's detach-rescue.
        var pollTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        EventHandler<object>? pollHandler = null;
        pollHandler = (_, __) =>
        {
            if (handler is null)
            {
                // Watcher already detached \u2014 stop polling.
                pollTimer.Stop();
                if (pollHandler is not null) pollTimer.Tick -= pollHandler;
                return;
            }
            try
            {
                if (_activeMatchHighlight is { para: var activePara, run: var activeRun }
                    && ReferenceEquals(activePara, targetPara) && activeRun is not null)
                {
                    var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    if (rect.Height > 0)
                    {
                        var t = block.TransformToVisual(PreviewScrollViewer);
                        var p = t.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                        double vpTop = PreviewScrollViewer.VerticalOffset;
                        double vpH = PreviewScrollViewer.ViewportHeight;
                        double runY = vpTop + p.Y;
                        bool onScreen = runY >= vpTop && runY + rect.Height <= vpTop + vpH;
                        if (!onScreen && rescrolls < kMaxRescrolls)
                        {
                            double target = runY - vpH / 2 + rect.Height / 2;
                            target = Math.Clamp(target, 0, PreviewScrollViewer.ScrollableHeight);
                            if (Math.Abs(target - vpTop) > 1)
                            {
                                rescrolls++;
                                idleStopwatch.Restart();
                                consecutiveOnScreen = 0;
                                bool accepted = PreviewScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                                LogService.Instance.Info("MatchNav",
                                    $"ScrollAfterMaterialization: poll re-center #{rescrolls} idx={_currentMatchIndex}, runY={runY:N1}, vpTop={vpTop:N1}, vpH={vpH:N1}, toY={target:N1}, accepted={accepted}, total={hardCapStopwatch.ElapsedMilliseconds}ms");
                            }
                        }
                    }
                }
            }
            catch { }
        };
        pollTimer.Tick += pollHandler;
        pollTimer.Start();
    }

    private void ScrollPreviewToLine(RichTextBlock block, Paragraph targetPara, int attemptsRemaining, int requestId, bool forceCenter)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (requestId != _matchScrollRequestId)
                return;

            if (TryScrollPreviewToLine(block, targetPara, verifyAfterScroll: false, forceCenter, out var reason))
                return;

            if (attemptsRemaining > 0)
            {
                ScrollPreviewToLine(block, targetPara, attemptsRemaining - 1, requestId, forceCenter);
                return;
            }

            LogService.Instance.Verbose("Preview", $"ScrollPreviewToLine: skipped after layout retries ({reason})");
        });
    }

    private void InvalidatePendingMatchScrolls()
    {
        _matchScrollRequestId++;
    }

    private bool TryScrollPreviewToLine(RichTextBlock block, Paragraph targetPara, bool verifyAfterScroll, bool forceCenter, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (ExpandPreviewSectionForBlock(block))
            {
                reason = "section expanded; waiting for layout";
                return false;
            }

            if (PreviewScrollViewer.ViewportHeight <= 0)
            {
                reason = "preview viewport not ready";
                return false;
            }

            if (forceCenter
                && _activeMatchHighlight is { para: var activePara }
                && ReferenceEquals(activePara, targetPara))
            {
                if (LogService.Instance.IsVerboseEnabled)
                    LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine: idx={_currentMatchIndex}, mode=actual-run, forceCenter=True, fromY={PreviewScrollViewer.VerticalOffset:N1}, viewportH={PreviewScrollViewer.ViewportHeight:N1}");

                ScrollMatchHorizontallyIntoView(block, targetPara);
                QueueActiveMatchOverlayUpdate(block, targetPara, PreviewScrollViewer.VerticalOffset);
                return true;
            }

            double lineHeight = EstimatePreviewLineHeight(block);

            int paragraphIndex = GetParagraphIndex(block, targetPara);
            if (paragraphIndex < 0)
            {
                reason = "paragraph not found";
                return false;
            }
            double blockTop = GetBlockAbsoluteTop(block);
            double wrappedLineOffset = EstimateWrappedLineOffset(block, targetPara);
            double cumulativeHeight = EstimateCumulativeHeightBefore(block, targetPara, lineHeight);
            double targetLineCenter = blockTop + cumulativeHeight + wrappedLineOffset * lineHeight + lineHeight / 2;
            double targetVerticalOffset = targetLineCenter - PreviewScrollViewer.ViewportHeight / 2;

            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine(estimated): idx={_currentMatchIndex}, para={paragraphIndex}/{block.Blocks.Count}, wrapOffset={wrappedLineOffset:N1}, blockTop={blockTop:N1}, lineH={lineHeight:N1}, cumulH={cumulativeHeight:N1}");

            targetVerticalOffset = Math.Clamp(targetVerticalOffset, 0, PreviewScrollViewer.ScrollableHeight);
            double beforeVerticalOffset = PreviewScrollViewer.VerticalOffset;
            bool verticalScrollNeeded = forceCenter;
            if (!verticalScrollNeeded)
            {
                double viewportHeight = PreviewScrollViewer.ViewportHeight;
                double guard = Math.Min(160, Math.Max(lineHeight * 4, viewportHeight * 0.22));
                verticalScrollNeeded = targetLineCenter < beforeVerticalOffset + guard
                    || targetLineCenter > beforeVerticalOffset + viewportHeight - guard;
            }

            bool verticalScrollRequested = verticalScrollNeeded && Math.Abs(targetVerticalOffset - beforeVerticalOffset) > 1;
            bool verticalScrollAccepted = false;
            double overlayVerticalOffset = beforeVerticalOffset;
            if (verticalScrollRequested)
            {
                verticalScrollAccepted = PreviewScrollViewer.ChangeView(null, targetVerticalOffset, null, disableAnimation: true);
                if (verticalScrollAccepted)
                    overlayVerticalOffset = targetVerticalOffset;
            }

            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine: idx={_currentMatchIndex}, mode=estimated, forceCenter={forceCenter}, verticalScroll={verticalScrollNeeded}, requested={verticalScrollRequested}, accepted={verticalScrollAccepted}, fromY={beforeVerticalOffset:N1}, targetY={targetVerticalOffset:N1}, overlayY={overlayVerticalOffset:N1}, lineCenter={targetLineCenter:N1}, viewportH={PreviewScrollViewer.ViewportHeight:N1}");

            ScrollMatchHorizontallyIntoView(block, targetPara);
            QueueActiveMatchOverlayUpdate(block, targetPara, overlayVerticalOffset);
            if (verifyAfterScroll)
            {
                int paraIdx = GetParagraphIndex(block, targetPara);
                VerifyActiveMatchVisibleAfterScroll(block, targetPara, paraIdx);
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            HideActiveMatchOverlay();
            return false;
        }
    }

    private void QueueActiveMatchOverlayUpdate(RichTextBlock block, Paragraph targetPara, double? expectedVerticalOffset = null)
    {
        if (_activeMatchHighlight is not { para: var activePara, run: var activeRun }
            || !ReferenceEquals(activePara, targetPara))
        {
            HideActiveMatchOverlay();
            return;
        }

        HideActiveMatchOverlay();
        int navIndex = _currentMatchIndex;
        Run targetRun = activeRun;

        void EnqueueUpdate(int retriesRemaining)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (_currentMatchIndex != navIndex
                    || _activeMatchHighlight is not { para: var currentPara, run: var currentRun }
                    || !ReferenceEquals(currentPara, targetPara)
                    || !ReferenceEquals(currentRun, targetRun))
                {
                    return;
                }

                if (!TryUpdateActiveMatchOverlayFromActualRun(block, targetPara, targetRun, expectedVerticalOffset, retryIfCenterRejected: retriesRemaining > 0) && retriesRemaining > 0)
                    EnqueueUpdate(retriesRemaining - 1);
            });
        }

        EnqueueUpdate(retriesRemaining: 2);
    }

    private bool TryUpdateActiveMatchOverlayFromActualRun(RichTextBlock block, Paragraph targetPara, Run targetRun, double? expectedVerticalOffset = null, bool retryIfCenterRejected = false)
    {
        try
        {
            if (_activeMatchHighlight is not { para: var activePara, run: var activeRun, matchInPara: var matchInPara }
                || !ReferenceEquals(activePara, targetPara)
                || !ReferenceEquals(activeRun, targetRun))
                return false;

            double viewportWidth = PreviewScrollViewer.ActualWidth;
            double viewportHeight = PreviewScrollViewer.ActualHeight;
            if (viewportWidth <= 0 || viewportHeight <= 0)
                return false;

            var rect = targetRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
            if (double.IsNaN(rect.X) || double.IsNaN(rect.Y) || rect.Height <= 0)
                return false;

            var transform = block.TransformToVisual(PreviewScrollViewer);
            var point = transform.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
            double markerHeight = Math.Max(12, rect.Height);
            double currentVerticalOffset = PreviewScrollViewer.VerticalOffset;
            double actualRunTop = currentVerticalOffset + point.Y;
            double overlayTop = point.Y;
            double effectiveVerticalOffset = currentVerticalOffset;
            bool centeredFromActualRun = false;
            bool actualCenterAccepted = false;
            if (expectedVerticalOffset.HasValue)
            {
                double actualTargetVerticalOffset = actualRunTop + markerHeight / 2 - viewportHeight / 2;
                actualTargetVerticalOffset = Math.Clamp(actualTargetVerticalOffset, 0, PreviewScrollViewer.ScrollableHeight);
                centeredFromActualRun = true;
                bool actualCenterNeeded = Math.Abs(actualTargetVerticalOffset - currentVerticalOffset) > 1;
                if (actualCenterNeeded)
                    actualCenterAccepted = PreviewScrollViewer.ChangeView(null, actualTargetVerticalOffset, null, disableAnimation: true);

                if (actualCenterAccepted || !actualCenterNeeded)
                    effectiveVerticalOffset = actualTargetVerticalOffset;
                else if (retryIfCenterRejected)
                {
                    if (LogService.Instance.IsVerboseEnabled)
                    {
                        int paragraphIndex = GetParagraphIndex(block, targetPara);
                        LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: retry centering idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, scrollY={currentVerticalOffset:N1}, expectedScrollY={expectedVerticalOffset.Value:N1}, actualTargetY={actualTargetVerticalOffset:N1}");
                    }
                    return false;
                }
                else
                {
                    return false;
                }

                overlayTop = actualRunTop - effectiveVerticalOffset;
            }

            if (overlayTop + markerHeight < 0 || overlayTop > viewportHeight)
                return false;

            double charWidth = Math.Max(EstimatePreviewCharWidth(block), rect.Width > 0 ? rect.Width : 0);
            double markerWidth = Math.Max(12, (targetRun.Text?.Length ?? 1) * charWidth);
            double markerLeft = point.X;

            ActiveMatchBand.Height = markerHeight;
            ActiveMatchBand.Width = viewportWidth;
            Canvas.SetTop(ActiveMatchBand, overlayTop);
            Canvas.SetLeft(ActiveMatchBand, 0);

            ActiveMatchWordMarker.Height = markerHeight;
            ActiveMatchWordMarker.Width = markerWidth;
            Canvas.SetTop(ActiveMatchWordMarker, overlayTop);
            Canvas.SetLeft(ActiveMatchWordMarker, markerLeft);

            ActiveMatchOverlay.Visibility = Visibility.Visible;
            if (LogService.Instance.IsVerboseEnabled)
            {
                int paragraphIndex = GetParagraphIndex(block, targetPara);
                string expectedScroll = expectedVerticalOffset.HasValue ? expectedVerticalOffset.Value.ToString("N1") : "actual";
                LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, rect=({rect.X:N1},{rect.Y:N1},{rect.Width:N1},{rect.Height:N1}), point=({point.X:N1},{point.Y:N1}), scrollY={currentVerticalOffset:N1}, expectedScrollY={expectedScroll}, effectiveScrollY={effectiveVerticalOffset:N1}, centeredActual={centeredFromActualRun}, centerAccepted={actualCenterAccepted}, marker=({markerLeft:N1},{overlayTop:N1},{markerWidth:N1},{markerHeight:N1}), text='{targetRun.Text}'");
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void HideActiveMatchOverlay()
    {
        ActiveMatchOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Post-scroll diagnostic: after layout settles, log where the boxed (active) match
    /// actually rendered relative to the preview viewport. Helps diagnose cases where the
    /// nav advances but the highlighted match is off-screen or hidden.
    /// </summary>
    private void VerifyActiveMatchVisibleAfterScroll(RichTextBlock block, Paragraph targetPara, int paragraphIndex, int correctionAttempt = 0, double previousParaAbsY = double.NaN)
    {
        int navIdx = _currentMatchIndex;
        // Use Normal priority so rapid Next/Prev clicks don't starve the
        // corrective scroll — Low priority let the verify queue grow without
        // running until the user stopped clicking, leaving the highlighted
        // match off-screen for hundreds of ms.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                if (_activeMatchHighlight is not { para: var activePara, run: var activeRun, column: var column })
                {
                    LogService.Instance.Info("MatchNav", $"VerifyActiveMatch: idx={navIdx} -- NO active highlight set");
                    return;
                }

                // Bail out if the user has already navigated to a different match
                // since this verification was enqueued — avoids expensive work on
                // stale requests (especially the UpdateLayout fallback).
                if (_currentMatchIndex != navIdx)
                {
                    LogService.Instance.Verbose("MatchNav", $"VerifyActiveMatch: stale (current={_currentMatchIndex}, enqueued={navIdx}), skipping");
                    return;
                }

                bool paraMatches = ReferenceEquals(activePara, targetPara);
                int activeIdx = navIdx;

                double vpH = PreviewScrollViewer.ViewportHeight;
                double vpTop = PreviewScrollViewer.VerticalOffset;
                double vpBottom = vpTop + vpH;

                double paraY = double.NaN;
                try
                {
                    var t = block.TransformToVisual(PreviewScrollViewer);
                    var p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    paraY = vpTop + p.Y;
                }
                catch { }

                double runY = double.NaN;
                double runH = double.NaN;
                try
                {
                    var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    var t = block.TransformToVisual(PreviewScrollViewer);
                    var p = t.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                    runY = vpTop + p.Y;
                    runH = rect.Height;
                }
                catch { }

                bool runOnScreen = !double.IsNaN(runY) && runY >= vpTop && (runY + (double.IsNaN(runH) ? 0 : runH)) <= vpBottom;
                string runText = activeRun.Text ?? "";
                if (runText.Length > 30) runText = runText.Substring(0, 30) + "…";

                LogService.Instance.Info("MatchNav",
                    $"VerifyActiveMatch: idx={navIdx}, activeIdx={activeIdx}, paraMatches={paraMatches}, paraIdx={paragraphIndex}, " +
                    $"col={column}, runText='{runText}', runFG={(activeRun.Foreground is SolidColorBrush sb ? sb.Color.ToString() : "?")}, " +
                    $"vpTop={vpTop:N1}, vpBottom={vpBottom:N1}, vpH={vpH:N1}, paraAbsY={paraY:N1}, runAbsY={runY:N1}, runH={runH:N1}, runOnScreen={runOnScreen}");

                // Self-correcting: if the run isn't centered/visible but we now have an
                // accurate rect, perform a corrective scroll. Allow extra attempts when
                // the layout is still shifting under us (paraAbsY changed since the
                // previous attempt) — those don't count against the cap because the
                // miss was caused by a moving target, not by a bad scroll computation.
                const int kHardCap = 6;
                bool layoutMoved = !double.IsNaN(previousParaAbsY) && Math.Abs(paraY - previousParaAbsY) > 2.0;
                int nextAttempt = layoutMoved ? correctionAttempt : correctionAttempt + 1;

                // If the run hasn't been measured yet (runH == 0 or NaN), the layout
                // pass hasn't reached this paragraph. Force an explicit UpdateLayout
                // and re-enqueue verification so we try again after layout has had
                // another tick. Without this we silently leave the user looking at
                // the wrong content — and the only way to recover used to be moving
                // the mouse over the preview panel (which forces realization).
                if ((double.IsNaN(runH) || runH <= 0) && nextAttempt <= kHardCap)
                {
                    bool layoutForced = false;
                    string layoutEx = "";
                    try
                    {
                        block.UpdateLayout();
                        PreviewScrollViewer.UpdateLayout();
                        layoutForced = true;
                    }
                    catch (Exception ex) { layoutEx = ex.GetType().Name; }
                    LogService.Instance.Info("MatchNav",
                        $"VerifyActiveMatch: run not yet measured (runH={runH:N1}), forcedLayout={layoutForced}{(layoutEx.Length > 0 ? $", layoutEx={layoutEx}" : "")}, re-enqueue verify idx={navIdx}, attempt={nextAttempt}/{kHardCap}");
                    VerifyActiveMatchVisibleAfterScroll(block, targetPara, paragraphIndex, nextAttempt, paraY);
                    return;
                }

                if (!runOnScreen && !double.IsNaN(runY) && !double.IsNaN(runH) && runH > 0 && nextAttempt <= kHardCap)
                {
                    double effectiveH = runH > 0 ? runH : EstimatePreviewLineHeight(block);
                    double correctedTarget = runY - vpH / 2 + effectiveH / 2;
                    correctedTarget = Math.Clamp(correctedTarget, 0, PreviewScrollViewer.ScrollableHeight);
                    if (Math.Abs(correctedTarget - vpTop) > 1)
                    {
                        // Wait for the ChangeView to actually settle before re-verifying.
                        EventHandler<ScrollViewerViewChangedEventArgs>? handler = null;
                        double capturedParaY = paraY;
                        handler = (_, ev) =>
                        {
                            if (ev.IsIntermediate) return;
                            PreviewScrollViewer.ViewChanged -= handler;
                            VerifyActiveMatchVisibleAfterScroll(block, targetPara, paragraphIndex, nextAttempt, capturedParaY);
                        };
                        PreviewScrollViewer.ViewChanged += handler;
                        bool accepted = PreviewScrollViewer.ChangeView(null, correctedTarget, null, disableAnimation: true);
                        LogService.Instance.Info("MatchNav",
                            $"VerifyActiveMatch: corrective scroll idx={navIdx}, attempt={nextAttempt}/{kHardCap}, layoutMoved={layoutMoved}, fromY={vpTop:N1}, toY={correctedTarget:N1}, accepted={accepted}");
                        if (!accepted)
                        {
                            // ChangeView rejected the request — no ViewChanged will fire,
                            // so detach the handler to avoid leaking.
                            PreviewScrollViewer.ViewChanged -= handler;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Info("MatchNav", $"VerifyActiveMatch: exception {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private static double EstimatePreviewLineHeight(RichTextBlock block) => Math.Max(16d, block.FontSize * 1.35d);

    private static double EstimatePreviewCharWidth(RichTextBlock block) => Math.Max(6d, block.FontSize * 0.58d);

    private double EstimateWrappedLineOffset(RichTextBlock block, Paragraph targetPara)
    {
        if (!ViewModel.PreviewWordWrap)
            return 0;

        if (_activeMatchHighlight is not { para: var activePara, column: var column }
            || !ReferenceEquals(activePara, targetPara))
            return 0;

        double charWidth = EstimatePreviewCharWidth(block);
        double availableWidth = GetPreviewTextViewportWidth(block) - 24;
        int charsPerWrappedLine = Math.Max(1, (int)Math.Floor(availableWidth / charWidth));
        double wrappedOffset = column / (double)charsPerWrappedLine;
        LogService.Instance.Verbose("Preview", $"EstimateWrappedLineOffset: idx={_currentMatchIndex}, column={column}, availableW={availableWidth:N1}, charW={charWidth:N1}, charsPerLine={charsPerWrappedLine}, wrappedOffset={wrappedOffset:N1}");
        return wrappedOffset;
    }

    private double EstimateCumulativeHeightBefore(RichTextBlock block, Paragraph targetPara, double lineHeight)
    {
        int paraIdx = GetParagraphIndex(block, targetPara);
        return paraIdx >= 0 ? GetCumulativeHeightBefore(block, paraIdx, lineHeight) : 0;
    }

    private int GetParagraphIndex(RichTextBlock block, Paragraph targetPara)
    {
        var metrics = GetParagraphMetrics(block);
        return metrics.IndexByParagraph.TryGetValue(targetPara, out int result) ? result : -1;
    }

    private ParagraphMetrics GetParagraphMetrics(RichTextBlock block)
    {
        if (_paragraphMetricsCache.TryGetValue(block, out var metrics))
            return metrics;

        var map = new Dictionary<Paragraph, int>(block.Blocks.Count);
        for (int idx = 0; idx < block.Blocks.Count; idx++)
        {
            if (block.Blocks[idx] is Paragraph p)
                map[p] = idx;
        }

        metrics = new ParagraphMetrics { IndexByParagraph = map };
        _paragraphMetricsCache[block] = metrics;
        return metrics;
    }

    private double GetCumulativeHeightBefore(RichTextBlock block, int blockIndex, double lineHeight)
    {
        if (blockIndex < 0) return 0;
        if (!ViewModel.PreviewWordWrap)
            return blockIndex * lineHeight;

        double availableWidth = GetPreviewTextViewportWidth(block) - 24;
        double charWidth = EstimatePreviewCharWidth(block);
        int charsPerLine = Math.Max(1, (int)Math.Floor(availableWidth / charWidth));
        var metrics = GetParagraphMetrics(block);
        if (metrics.PrefixHeights is not null
            && metrics.PrefixHeights.Length == block.Blocks.Count + 1
            && metrics.PrefixCharsPerLine == charsPerLine
            && Math.Abs(metrics.PrefixLineHeight - lineHeight) < 0.01)
        {
            return metrics.PrefixHeights[Math.Min(blockIndex, metrics.PrefixHeights.Length - 1)];
        }

        var prefix = new double[block.Blocks.Count + 1];
        for (int i = 0; i < block.Blocks.Count; i++)
        {
            double height = lineHeight;
            if (block.Blocks[i] is Paragraph p)
            {
                int textLen = 0;
                foreach (var inline in p.Inlines)
                {
                    if (inline is Run r)
                        textLen += r.Text?.Length ?? 0;
                }
                int wrappedLines = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, textLen) / charsPerLine));
                height = wrappedLines * lineHeight;
            }
            prefix[i + 1] = prefix[i] + height;
        }
        metrics.PrefixHeights = prefix;
        metrics.PrefixCharsPerLine = charsPerLine;
        metrics.PrefixLineHeight = lineHeight;
        return prefix[Math.Min(blockIndex, prefix.Length - 1)];
    }

    private void InvalidateParagraphIndexCache(RichTextBlock? block = null)
    {
        if (block is not null)
            _paragraphMetricsCache.Remove(block);
        else
            _paragraphMetricsCache.Clear();
        _paragraphMatchRunCache.Clear();
        InvalidateScrollPositionCache();
    }

    private double GetBlockAbsoluteTop(RichTextBlock block)
    {
        if (_blockAbsoluteTopCache.TryGetValue(block, out double top))
            return top;

        var verticalTransform = block.TransformToVisual(PreviewScrollViewer);
        var verticalPoint = verticalTransform.TransformPoint(new Windows.Foundation.Point(0, 0));
        top = PreviewScrollViewer.VerticalOffset + verticalPoint.Y;
        _blockAbsoluteTopCache[block] = top;
        return top;
    }

    private void InvalidateScrollPositionCache(RichTextBlock? block = null)
    {
        if (block is not null)
            _blockAbsoluteTopCache.Remove(block);
        else
            _blockAbsoluteTopCache.Clear();
    }

    private double GetPreviewTextViewportWidth(RichTextBlock block)
    {
        if (_sectionMatchNavs.TryGetValue(block, out var sectionNav) && sectionNav.Scroller.ViewportWidth > 0)
            return sectionNav.Scroller.ViewportWidth;

        return PreviewScrollViewer.ViewportWidth;
    }

    private bool ExpandPreviewSectionForBlock(RichTextBlock block)
    {
        if (_blockExpanderCache.TryGetValue(block, out var cachedExpander))
        {
            if (cachedExpander.IsExpanded)
                return false;

            cachedExpander.IsExpanded = true;
            return true;
        }

        return false;
    }

    private void ScrollMatchHorizontallyIntoView(RichTextBlock block, Paragraph targetPara)
    {
        if (ViewModel.PreviewWordWrap)
        {
            return;
        }

        if (_activeMatchHighlight is not { para: var activePara, run: var activeRun, column: var column }
            || !ReferenceEquals(activePara, targetPara))
        {
            LogService.Instance.Verbose("Preview", "ScrollMatchHorizontallyIntoView: skipped because active match does not match target paragraph");
            return;
        }

        var scroller = _sectionMatchNavs.TryGetValue(block, out var sectionNav)
            ? sectionNav.Scroller
            : PreviewScrollViewer;

        if (scroller.ViewportWidth <= 0 || scroller.ScrollableWidth <= 0)
        {
            LogService.Instance.Verbose("Preview", $"ScrollMatchHorizontallyIntoView: skipped, viewportW={scroller.ViewportWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}");
            return;
        }

        double charWidth = EstimatePreviewCharWidth(block);
        double matchStart = 8 + column * charWidth;
        double matchWidth = Math.Max(charWidth, (activeRun.Text?.Length ?? 0) * charWidth);
        double matchEnd = matchStart + matchWidth;
        double viewportLeft = scroller.HorizontalOffset;
        double viewportRight = viewportLeft + scroller.ViewportWidth;
        double guard = Math.Min(96, Math.Max(16, scroller.ViewportWidth * 0.15));
        if (matchStart >= viewportLeft + guard && matchEnd <= viewportRight - guard)
        {
            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("Preview", $"ScrollMatchHorizontallyIntoView: skipped visible idx={_currentMatchIndex}, column={column}, viewport=({viewportLeft:N1},{viewportRight:N1})");
            return;
        }

        double matchCenter = matchStart + matchWidth / 2;
        double targetHorizontalOffset = matchCenter - scroller.ViewportWidth / 2;
        targetHorizontalOffset = Math.Clamp(targetHorizontalOffset, 0, scroller.ScrollableWidth);
        double beforeHorizontalOffset = scroller.HorizontalOffset;
        if (Math.Abs(targetHorizontalOffset - beforeHorizontalOffset) <= 1)
            return;

        bool horizontalAccepted = scroller.ChangeView(targetHorizontalOffset, null, null, disableAnimation: true);
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("Preview", $"ScrollMatchHorizontallyIntoView: idx={_currentMatchIndex}, column={column}, runLen={activeRun.Text?.Length ?? 0}, charW={charWidth:N1}, beforeX={beforeHorizontalOffset:N1}, targetX={targetHorizontalOffset:N1}, viewportW={scroller.ViewportWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}, accepted={horizontalAccepted}, sectionScroller={_sectionMatchNavs.ContainsKey(block)}");
    }

    private int MatchNavFileCount => _sectionMatchNavs.Count > 0
        ? _sectionMatchNavs.Count
        : _matchParagraphs.Select(m => m.block).Distinct().Count() + _lazySections.Count;

    /// <summary>
    /// Files and matches not yet inserted into the visual tree (waiting behind
    /// a "Show more" button). Included in the match-nav grand totals so the
    /// user sees the true scope of their result set.
    /// </summary>
    private (int Files, int Matches) GetDeferredCounts()
    {
        var list = _deferredOrderedFiles;
        if (list is null || _deferredCursor >= list.Count) return (0, 0);

        if (ReferenceEquals(list, _cachedDeferredCountsList)
            && _cachedDeferredCountsCursor == _deferredCursor)
        {
            return _cachedDeferredCounts;
        }

        int files = list.Count - _deferredCursor;
        int matches = 0;
        for (int i = _deferredCursor; i < list.Count; i++)
            matches += list[i].Value.Count;
        _cachedDeferredCountsList = list;
        _cachedDeferredCountsCursor = _deferredCursor;
        _cachedDeferredCounts = (files, matches);
        return _cachedDeferredCounts;
    }

    private void InvalidateDeferredCountsCache()
    {
        _cachedDeferredCountsList = null;
        _cachedDeferredCountsCursor = -1;
        _cachedDeferredCounts = default;
    }

    private string FormatMatchNavLabel(int index)
    {
        var (deferredFiles, deferredMatches) = GetDeferredCounts();
        int overflowRemaining = 0;
        foreach (var ov in _sectionOverflow.Values)
            overflowRemaining += ov.RemainingResults.Count;
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount + deferredMatches + overflowRemaining;
        int fileCount = MatchNavFileCount + deferredFiles;
        return $"Match {index + 1} of {totalMatches} (across {fileCount} file{(fileCount != 1 ? "s" : "")})";
    }

    private void UpdateMatchNavPanel()
    {
        var (_, deferredMatches) = GetDeferredCounts();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount + deferredMatches;
        if (totalMatches > 0)
        {
            MatchNavPanel.Visibility = Visibility.Visible;
            bool hadActiveHighlight = _activeMatchHighlight is not null;
            var activeIndex = FindActiveMatchIndex();
            if (activeIndex >= 0)
                _currentMatchIndex = activeIndex;
            else if (_matchParagraphs.Count > 0)
            {
                if (_currentMatchIndex < 0)
                    _currentMatchIndex = 0;
                else if (_currentMatchIndex >= _matchParagraphs.Count)
                    _currentMatchIndex = _matchParagraphs.Count - 1;
            }
            else if (_currentMatchIndex < 0)
            {
                _currentMatchIndex = 0;
            }

            MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);

            // If the label says "Match 1 of N" but nothing is actually boxed/red yet,
            // box the current match and scroll to it.  Without this, the very first
            // match after a preview load shows a count but no visible highlight until
            // the user clicks Next.  Reuse the known-good OnNextMatch code path:
            // setting _currentMatchIndex = -1 makes the next click land on index 0
            // and run the same Box+Scroll flow that works for subsequent navigation.
            if (!_initialMatchScrolled
                && !hadActiveHighlight
                && _matchParagraphs.Count > 0)
            {
                _initialMatchScrolled = true;
                // Two chained Low-priority dispatches so this runs AFTER the
                // TryScrollToPreviewSection call that PrependPreviewSectionsForFilesAsync
                // also queues — otherwise that scroll-to-section overrides our
                // scroll-to-first-match and the user lands at the file header
                // instead of the first highlight.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (_matchParagraphs.Count == 0) return;
                        if (_activeMatchHighlight is not null
                            && _activeMatchHighlight.Value.para is var ap
                            && ReferenceEquals(ap, _matchParagraphs[0].para))
                        {
                            // Already on first match — just make sure it's visible.
                            ScrollPreviewToLine(_matchParagraphs[0].block, _matchParagraphs[0].para);
                            return;
                        }
                        _currentMatchIndex = -1;
                        OnNextMatch(this, new RoutedEventArgs());
                    });
                });
            }
        }
        else
        {
            HideMatchNavPanel();
        }
    }

    private int FindActiveMatchIndex()
    {
        if (_activeMatchHighlight is not { para: var activePara, matchInPara: var activeMatchInPara })
            return -1;

        for (int i = 0; i < _matchParagraphs.Count; i++)
        {
            if (ReferenceEquals(_matchParagraphs[i].para, activePara)
                && _matchParagraphs[i].matchInPara == activeMatchInPara)
                return i;
        }

        return -1;
    }

    private void SetCurrentMatchToParagraph(RichTextBlock block, Paragraph para)
        => SetCurrentMatchToMatch(block, para, matchInPara: null);

    private void SetCurrentMatchToMatch(RichTextBlock block, Paragraph para, int? matchInPara)
    {
        for (int i = 0; i < _matchParagraphs.Count; i++)
        {
            var match = _matchParagraphs[i];
            if (ReferenceEquals(match.block, block)
                && ReferenceEquals(match.para, para)
                && (matchInPara is null || match.matchInPara == matchInPara.Value))
            {
                _currentMatchIndex = i;
                MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
                if (_sectionMatchNavs.TryGetValue(block, out var sn))
                    SetSectionCurrentMatch(sn, para, match.matchInPara);
                ActivateSectionForBlock(block);
                BoxMatchRun(para, match.matchInPara);
                return;
            }
        }

        ActivateSectionForBlock(block);
    }

    private static void SetSectionCurrentMatch(SectionMatchNav sn, Paragraph para, int matchInPara)
    {
        // O(1) lookup once the cache is built. The cache is invalidated by
        // sites that mutate sn.Matches (see InsertSectionMatches / lazy materialization).
        var cache = sn.IndexByMatch;
        if (cache is null || cache.Count != sn.Matches.Count)
        {
            cache = new Dictionary<(Paragraph, int), int>(sn.Matches.Count);
            for (int i = 0; i < sn.Matches.Count; i++)
                cache[sn.Matches[i]] = i;
            sn.IndexByMatch = cache;
        }
        if (cache.TryGetValue((para, matchInPara), out int idx))
            sn.CurrentIndex = idx;
    }

    private void HideMatchNavPanel()
    {
        UnboxCurrentMatch();
        HideActiveMatchOverlay();
        MatchNavPanel.Visibility = Visibility.Collapsed;
        SectionNavOverlay.Visibility = Visibility.Collapsed;
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        _sectionMatchNavs.Clear();
        _activeSectionNav = null;
        _lazySections.Clear();
        _lazyMatchCount = 0;
        _sectionOverflow.Clear();
        _deferredOrderedFiles = null;
        _deferredAllSelected = null;
        _deferredButtonPanel = null;
        _deferredCursor = 0;
        InvalidateDeferredCountsCache();
        _autoLoadMoreInFlight = false;
        _initialMatchScrolled = false;
        _expanderFilePaths.Clear();
        _expanderHeaderArgs.Clear();
        _blockExpanderCache.Clear();
        InvalidateScrollPositionCache();
        _stickyHeaderExpander = null;
        StickyFileHeader.Child = null;
        StickyFileHeader.Visibility = Visibility.Collapsed;
    }

    private void UpdateSectionMatchNavPanels()
    {
        // Initialize or clamp match indices for all sections without resetting
        // the user's current position during background section loading.
        foreach (var sn in _sectionMatchNavs.Values)
        {
            if (sn.Matches.Count == 0)
                sn.CurrentIndex = -1;
            else if (sn.CurrentIndex < 0)
                sn.CurrentIndex = 0;
            else if (sn.CurrentIndex >= sn.Matches.Count)
                sn.CurrentIndex = sn.Matches.Count - 1;
        }

        // Only auto-activate per-file section nav when there's exactly one file section.
        // For multi-file views, the user must click/expand a section to activate its nav.
        if (_sectionMatchNavs.Count == 1)
        {
            var sn = _sectionMatchNavs.Values.First();
            if (sn.Matches.Count > 1)
                _activeSectionNav = sn;
            else
                _activeSectionNav = null;
        }
        else
        {
            _activeSectionNav = null;
        }

        HighlightActiveExpander();
        UpdateSectionNavOverlay();
    }

    private void ActivateSectionForBlock(RichTextBlock block)
    {
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
        {
            _activeSectionNav = sn;
            HighlightActiveExpander();
            UpdateSectionNavOverlay();
        }
    }

    private RichTextBlock? _lastHighlightedActiveBlock;

    private void HighlightActiveExpander()
    {
        var activeBlock = _activeSectionNav?.Block;
        // Avoid the per-click loop when the active section hasn't changed:
        // setting Background on every Expander invalidates each section panel
        // and is wasted work during rapid Next/Prev within a single file.
        if (ReferenceEquals(activeBlock, _lastHighlightedActiveBlock))
            return;
        _lastHighlightedActiveBlock = activeBlock;
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            bool isActive = child.Tag is RichTextBlock b && b == activeBlock;
            child.Background = isActive ? s_activeExpanderBrush : null;
        }
    }

    private void UpdateSectionNavOverlay()
    {
        if (_activeSectionNav is null || _activeSectionNav.Matches.Count <= 1)
        {
            SectionNavOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        SectionNavOverlay.Visibility = Visibility.Visible;
        int total = _activeSectionNav.Matches.Count;
        if (_sectionOverflow.TryGetValue(_activeSectionNav.Block, out var ov))
            total += ov.RemainingResults.Count;
        SectionNavLabel.Text = $"Match {_activeSectionNav.CurrentIndex + 1} of {total}";
    }

    private void OnSectionNavNext(object sender, RoutedEventArgs e)
    {
        if (_activeSectionNav is null || _activeSectionNav.Matches.Count == 0) return;
        OnSectionNextMatch(_activeSectionNav);
    }

    private void OnSectionNavPrev(object sender, RoutedEventArgs e)
    {
        if (_activeSectionNav is null || _activeSectionNav.Matches.Count == 0) return;
        OnSectionPrevMatch(_activeSectionNav);
    }

    private void OnSectionNavDismiss(object sender, RoutedEventArgs e)
    {
        SectionNavOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnSectionNavPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SectionNavOverlay.Opacity = 1.0;
    }

    private void OnSectionNavPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SectionNavOverlay.Opacity = 0.75;
    }

    private void OnSectionNextMatch(SectionMatchNav sn)
    {
        if (sn.Matches.Count == 0) return;
        Paragraph? previousPara = sn.CurrentIndex >= 0 && sn.CurrentIndex < sn.Matches.Count ? sn.Matches[sn.CurrentIndex].para : null;
        bool wrappedToStart = false;
        bool expandedOverflow = false;
        int nextIndex = sn.CurrentIndex + 1;
        if (nextIndex >= sn.Matches.Count)
        {
            if (_sectionOverflow.ContainsKey(sn.Block) && ExpandSectionNextChunk(sn.Block))
            {
                nextIndex = sn.CurrentIndex + 1;
                expandedOverflow = true;
            }
            else
            {
                nextIndex = 0;
                wrappedToStart = true;
            }
        }
        sn.CurrentIndex = Math.Clamp(nextIndex, 0, sn.Matches.Count - 1);
        _activeSectionNav = sn;
        HighlightActiveExpander();
        UpdateSectionNavOverlay();
        var (para, matchInPara) = sn.Matches[sn.CurrentIndex];
        BoxMatchRun(para, matchInPara);
        ScrollAfterMatchNavigation(sn.Block, para, justMaterialized: expandedOverflow, sameParagraph: !expandedOverflow && !wrappedToStart && ReferenceEquals(previousPara, para));
    }

    private void OnSectionPrevMatch(SectionMatchNav sn)
    {
        if (sn.Matches.Count == 0) return;
        Paragraph? previousPara = sn.CurrentIndex >= 0 && sn.CurrentIndex < sn.Matches.Count ? sn.Matches[sn.CurrentIndex].para : null;
        bool wrappedToEnd = sn.CurrentIndex <= 0;
        sn.CurrentIndex = (sn.CurrentIndex - 1 + sn.Matches.Count) % sn.Matches.Count;
        _activeSectionNav = sn;
        HighlightActiveExpander();
        UpdateSectionNavOverlay();
        var (para, matchInPara) = sn.Matches[sn.CurrentIndex];
        BoxMatchRun(para, matchInPara);
        ScrollAfterMatchNavigation(sn.Block, para, justMaterialized: false, sameParagraph: !wrappedToEnd && ReferenceEquals(previousPara, para));
    }

    private void ScrollAfterMatchNavigation(RichTextBlock block, Paragraph para, bool justMaterialized, bool sameParagraph)
    {
        if (justMaterialized)
        {
            ScrollAfterMaterialization(block, para);
            return;
        }

        if (sameParagraph)
        {
            if (ViewModel.PreviewWordWrap)
            {
                ScrollPreviewToLine(block, para, forceCenter: true);
            }
            else
            {
                ScrollMatchHorizontallyIntoView(block, para);
                QueueActiveMatchOverlayUpdate(block, para);
            }
            return;
        }

        ScrollPreviewToLine(block, para, forceCenter: true);
    }

    private void OnNextMatch(object sender, RoutedEventArgs e)
    {
        var navSw = System.Diagnostics.Stopwatch.StartNew();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount;
        if (totalMatches == 0) return;
        Paragraph? previousPara = _currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count ? _matchParagraphs[_currentMatchIndex].para : null;
        bool expandedOverflow = false;

        // If the current match is the last one in its section and that section
        // still has overflow (was truncated), expand the next chunk before
        // navigating so the user can progressively reach all matches.
        if (_sectionOverflow.Count > 0
            && _matchParagraphs.Count > 0
            && _currentMatchIndex >= 0
            && _currentMatchIndex < _matchParagraphs.Count)
        {
            var (curBlock, _, _) = _matchParagraphs[_currentMatchIndex];
            bool atSectionEnd = _currentMatchIndex == _matchParagraphs.Count - 1
                || !ReferenceEquals(_matchParagraphs[_currentMatchIndex + 1].block, curBlock);
            if (atSectionEnd && _sectionOverflow.ContainsKey(curBlock))
                expandedOverflow = ExpandSectionNextChunk(curBlock);
        }

        bool justMaterialized = false;
        bool wrappedToStart = false;
        if (_matchParagraphs.Count == 0 || _currentMatchIndex >= _matchParagraphs.Count - 1)
        {
            // Past the last rendered match — try to materialize the next lazy section.
            if (_lazyMatchCount > 0 && MaterializeNextLazySection(forward: true))
            {
                // Land on the first match of the newly materialized section.
                _currentMatchIndex = _matchParagraphs.Count - _lazySectionJustAdded;
                UpdateSectionMatchNavPanels();
                justMaterialized = true;
            }
            else
            {
                // Wrap to start.
                _currentMatchIndex = 0;
                wrappedToStart = true;
            }
        }
        else
        {
            _currentMatchIndex++;
        }

        if (_matchParagraphs.Count == 0 || _currentMatchIndex < 0 || _currentMatchIndex >= _matchParagraphs.Count) return;
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
            SetSectionCurrentMatch(sn, para, matchInPara);
        ActivateSectionForBlock(block);
        BoxMatchRun(para, matchInPara);
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"OnNextMatch: idx={_currentMatchIndex}, path={(justMaterialized ? "materialize" : "normal")}");
        ScrollAfterMatchNavigation(block, para, justMaterialized || expandedOverflow, sameParagraph: !expandedOverflow && !wrappedToStart && ReferenceEquals(previousPara, para));
        navSw.Stop();
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("Preview", $"OnNextMatch: index={_currentMatchIndex}, elapsed={navSw.ElapsedMilliseconds}ms");
    }

    private void OnPrevMatch(object sender, RoutedEventArgs e)
    {
        var navSw = System.Diagnostics.Stopwatch.StartNew();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount;
        if (totalMatches == 0) return;
        Paragraph? previousPara = _currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count ? _matchParagraphs[_currentMatchIndex].para : null;

        bool justMaterialized = false;
        bool wrappedToEnd = false;
        if (_matchParagraphs.Count == 0 || _currentMatchIndex <= 0)
        {
            // Before the first rendered match — try to materialize the last lazy section.
            if (_lazyMatchCount > 0 && MaterializeNextLazySection(forward: false))
            {
                // Land on the last match of the newly materialized section.
                _currentMatchIndex = _matchParagraphs.Count - 1;
                UpdateSectionMatchNavPanels();
                justMaterialized = true;
            }
            else
            {
                // Wrap to end.
                _currentMatchIndex = _matchParagraphs.Count - 1;
                wrappedToEnd = true;
            }
        }
        else
        {
            _currentMatchIndex--;
        }

        if (_matchParagraphs.Count == 0 || _currentMatchIndex < 0 || _currentMatchIndex >= _matchParagraphs.Count) return;
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
            SetSectionCurrentMatch(sn, para, matchInPara);
        ActivateSectionForBlock(block);
        BoxMatchRun(para, matchInPara);
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"OnPrevMatch: idx={_currentMatchIndex}, path={(justMaterialized ? "materialize" : "normal")}");
        ScrollAfterMatchNavigation(block, para, justMaterialized, sameParagraph: !wrappedToEnd && ReferenceEquals(previousPara, para));
        navSw.Stop();
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("Preview", $"OnPrevMatch: index={_currentMatchIndex}, elapsed={navSw.ElapsedMilliseconds}ms");
    }

    // Tracks how many match entries the last MaterializeNextLazySection call added.
    private int _lazySectionJustAdded;

    /// <summary>
    /// Finds the next (or previous) lazy section in visual order and materializes it.
    /// Skips sections that produce zero match entries. Also expands the Expander.
    /// Returns true if at least one materialized section produced match entries.
    /// </summary>
    private bool MaterializeNextLazySection(bool forward)
    {
        _lazySectionJustAdded = 0;

        // Walk expanders in visual order. Skip already-rendered sections and any
        // newly-materialized section that produces zero match entries.
        var children = PreviewSectionsPanel.Children;
        int start = forward ? 0 : children.Count - 1;
        int end = forward ? children.Count : -1;
        int step = forward ? 1 : -1;

        for (int i = start; i != end; i += step)
        {
            if (children[i] is Expander exp
                && exp.Tag is RichTextBlock b
                && _lazySections.ContainsKey(b))
            {
                int beforeCount = _matchParagraphs.Count;
                MaterializeLazySection(b);
                int added = _matchParagraphs.Count - beforeCount;
                exp.IsExpanded = true;
                if (added > 0)
                {
                    _lazySectionJustAdded = added;
                    LogService.Instance.Info("MatchNav", $"MaterializeNextLazySection: forward={forward}, added={added}, expanderIdx={i}, isExpanded={exp.IsExpanded}");
                    return true;
                }
                // Otherwise keep walking — try the next lazy section.
            }
        }
        return false;
    }

    /// <summary>
    /// Expands the next chunk of un-rendered matches for a section that was
    /// truncated at <see cref="MaxMatchesPerSection"/>. Called from match
    /// navigation when the user reaches the end of the rendered range and
    /// the section still has overflow. Returns true if any matches were added.
    /// </summary>
    private bool ExpandSectionNextChunk(RichTextBlock section)
    {
        if (!_sectionOverflow.TryGetValue(section, out var ov)) return false;
        int chunkSize = Math.Min(MaxMatchesPerExpandChunk, ov.RemainingResults.Count);
        if (chunkSize <= 0)
        {
            _sectionOverflow.Remove(section);
            return false;
        }

        // Remove the existing truncation notice (we'll re-append it if more remain).
        if (ov.NoticePara != null)
            section.Blocks.Remove(ov.NoticePara);

        // Compute the insertion point in _matchParagraphs (right after this
        // section's last existing match) before appending new entries.
        int insertAt = -1;
        for (int i = _matchParagraphs.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_matchParagraphs[i].block, section))
            {
                insertAt = i + 1;
                break;
            }
        }
        if (insertAt < 0) insertAt = _matchParagraphs.Count;

        _sectionMatchNavs.TryGetValue(section, out var sn);
        int beforeCount = _matchParagraphs.Count;

        // Stop early once we've added enough match entries to keep the UI
        // responsive — dense lines can produce 20×+ match entries per result.
        int consumed = 0;
        if (ov.IsHighlightMode && ov.AllLines != null)
        {
            AppendHighlightMatchWindows(
                section,
                ov.RemainingResults,
                ov.AllLines,
                ov.Rx,
                sn,
                MaxMatchEntriesPerExpandChunk,
                chunkSize,
                out consumed,
                out _,
                ov.LastRenderedLine);
            for (int i = 0; i < consumed; i++)
                ov.LastRenderedLine = Math.Max(ov.LastRenderedLine, ov.RemainingResults[i].LineNumber);
        }
        else
        {
            for (int ri = 0; ri < chunkSize; ri++)
            {
                var r = ov.RemainingResults[ri];
                var sep = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var sepRun = new Run { Text = $"{new string('\u2500', 6)}{label}{new string('\u2500', 6)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                section.Blocks.Add(sep);

                var lines = GetPreviewLines(r, ov.AllLines, ov.PreviewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    bool isMatchLine = lineNum == r.LineNumber;
                    AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, ov.Rx, truncate: true, _matchParagraphs, sn, out _);
                }
                consumed++;
                if (_matchParagraphs.Count - beforeCount >= MaxMatchEntriesPerExpandChunk)
                    break;
            }
        }

        ov.RemainingResults.RemoveRange(0, consumed);

        int addedCount = _matchParagraphs.Count - beforeCount;
        // AddPreviewLineParagraphs appends to _matchParagraphs at the end. If
        // there are later sections whose matches come after this section's,
        // move the newly added entries into the correct slot.
        if (addedCount > 0 && insertAt < beforeCount)
        {
            var newEntries = _matchParagraphs.GetRange(beforeCount, addedCount);
            _matchParagraphs.RemoveRange(beforeCount, addedCount);
            _matchParagraphs.InsertRange(insertAt, newEntries);
            // Shift the current cursor if it sat after the insertion point.
            if (_currentMatchIndex >= insertAt)
                _currentMatchIndex += addedCount;
        }

        InvalidateParagraphIndexCache(section);
        if (sn != null) sn.IndexByMatch = null;

        ov.RenderedSoFar += consumed;

        if (ov.RemainingResults.Count > 0)
        {
            ov.NoticePara = AppendTruncationNotice(section, ov.OriginalTotal, ov.RenderedSoFar);
        }
        else
        {
            ov.NoticePara = null;
            _sectionOverflow.Remove(section);
        }

        LogService.Instance.Info("MatchNav", $"ExpandSectionNextChunk: results={consumed}, addedEntries={addedCount}, renderedSoFar={ov.RenderedSoFar}, remaining={ov.RemainingResults.Count}");
        return addedCount > 0;
    }

    private bool _splitterDragging;
    private double _splitterStartX;
    private double _col0StartWidth;
    private double _col2StartWidth;

    private void OnAdvancedOptionsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        ChevronRotate.Angle = 0;
        if (_launcherMode)
            ListenForExpanderResize();
    }

    private void OnAdvancedOptionsCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        ChevronRotate.Angle = -90;
        if (_launcherMode)
            ListenForExpanderResize();
    }

    /// <summary>
    /// Tracks the expander animation by resizing the window on every
    /// SizeChanged event, keeping content and window perfectly in sync.
    /// A debounce timer detects when the animation has finished and
    /// unsubscribes the handler.
    /// </summary>
    private void ListenForExpanderResize()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;

        void handler(object s, SizeChangedEventArgs e)
        {
            if (_launcherMode) PositionLauncherWindow();
            timer.Stop();
            timer.Start();
        }

        timer.Tick += (t, a) =>
        {
            timer.Stop();
            RootGrid.SizeChanged -= handler;
            if (_launcherMode) PositionLauncherWindow();
        };

        RootGrid.SizeChanged += handler;
        timer.Start();
    }

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        var border = (Border)sender;
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(SplitPaneGrid).Position.X;
        _col0StartWidth = SplitPaneGrid.ColumnDefinitions[0].ActualWidth;
        _col2StartWidth = SplitPaneGrid.ColumnDefinitions[2].ActualWidth;
        border.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        double currentX = e.GetCurrentPoint(SplitPaneGrid).Position.X;
        double delta = currentX - _splitterStartX;
        double newCol0 = _col0StartWidth + delta;
        double newCol2 = _col2StartWidth - delta;
        double minWidth = 200;
        if (newCol0 < minWidth || newCol2 < minWidth) return;
        SplitPaneGrid.ColumnDefinitions[0].Width = new GridLength(newCol0, GridUnitType.Pixel);
        SplitPaneGrid.ColumnDefinitions[2].Width = new GridLength(newCol2, GridUnitType.Pixel);
        e.Handled = true;
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        ((Border)sender).ReleasePointerCapture(e.Pointer);
        // Convert back to star sizing so the layout adapts on window resize.
        double col0 = SplitPaneGrid.ColumnDefinitions[0].ActualWidth;
        double col2 = SplitPaneGrid.ColumnDefinitions[2].ActualWidth;
        double total = col0 + col2;
        if (total > 0)
        {
            SplitPaneGrid.ColumnDefinitions[0].Width = new GridLength(col0 / total, GridUnitType.Star);
            SplitPaneGrid.ColumnDefinitions[2].Width = new GridLength(col2 / total, GridUnitType.Star);
        }
        e.Handled = true;
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SplitterBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.Colors.Gray);
        SplitterBorder.Opacity = 0.5;
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            SplitterBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Transparent);
            SplitterBorder.Opacity = 1.0;
        }
    }

    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnContentLoaded;
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        if (_launcherMode) PositionLauncherWindow();
        FocusSearchOnLaunch();
        await CheckEverythingAsync();
        await CheckFirstRunContextMenuAsync();

        if (_autoSearchOnLoad)
        {
            _autoSearchOnLoad = false;
            await ViewModel.StartSearchAsync();
        }
        else
        {
            FocusSearchBox();
        }
    }

    private void FocusSearchBox()
    {
        DispatcherQueue.TryEnqueue(() => QueryBox.Focus(FocusState.Programmatic));
    }

    private async Task CheckEverythingAsync()
    {
        var esPath = FileLister.FindEsExe();
        bool everythingRunning = Process.GetProcessesByName("Everything").Length > 0;

        // Both installed and running — nothing to do
        if (esPath != null && everythingRunning) return;

        // es.exe found but Everything service not running — offer to start it
        if (esPath != null && !everythingRunning)
        {
            var everythingExe = FindEverythingExe(esPath);
            if (everythingExe != null)
            {
                var startDialog = new ContentDialog
                {
                    XamlRoot = ((FrameworkElement)Content).XamlRoot,
                    Title = "Everything Search Not Running",
                    Content = "Everything Search is installed but not currently running.\nIt must be running for fast file discovery.\n\nWould you like to start it now?",
                    PrimaryButtonText = "Start Everything",
                    CloseButtonText = "Skip",
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await startDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = everythingExe,
                            UseShellExecute = true,
                        });
                        await WaitForEverythingReadyAndNotifyAsync();
                    }
                    catch (Exception ex)
                    {
                        ViewModel.StatusText = $"Could not start Everything: {ex.Message}. Using built-in file enumeration.";
                        LogService.Instance.Warning("MainWindow", "Failed to start Everything", ex);
                    }
                }
                return;
            }
        }

        // es.exe not found — offer to download and install
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Everything Search Not Found",
            Content = "Everything Search by voidtools provides significantly faster file discovery.\n\nWould you like to download and install it?",
            PrimaryButtonText = "Install",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool is64Bit = Environment.Is64BitOperatingSystem;
        string url = is64Bit
            ? "https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe"
            : "https://www.voidtools.com/Everything-1.4.1.1032.x86-Setup.exe";
        string fileName = is64Bit ? "Everything-1.4.1.1032.x64-Setup.exe" : "Everything-1.4.1.1032.x86-Setup.exe";
        string tempPath = Path.Combine(Path.GetTempPath(), fileName);

        ViewModel.StatusText = "Downloading Everything Search installer\u2026";

        try
        {
            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(new Uri(url));
            await File.WriteAllBytesAsync(tempPath, data);

            ViewModel.StatusText = "Running Everything Search installer \u2014 please complete the setup wizard\u2026";

            var psi = new ProcessStartInfo
            {
                FileName = tempPath,
                Verb = "runas",
                UseShellExecute = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }

            var installedEsPath = FileLister.FindEsExe();
            if (installedEsPath is null)
            {
                ViewModel.StatusText = "Installer completed. Restart Yagu if Everything was installed to a custom location.";
                return;
            }

            if (Process.GetProcessesByName("Everything").Length == 0)
            {
                var everythingExe = FindEverythingExe(installedEsPath);
                if (everythingExe != null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = everythingExe,
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        LogService.Instance.Warning("MainWindow", "Failed to start Everything after install", ex);
                    }
                }
            }

            await WaitForEverythingReadyAndNotifyAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ViewModel.StatusText = "Everything Search installation was cancelled. Using built-in file enumeration.";
            LogService.Instance.Info("MainWindow", "Everything install UAC declined");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Failed to install Everything: {ex.Message}. Using built-in file enumeration.";
            LogService.Instance.Warning("MainWindow", "Everything install failed", ex);
        }
    }

    private async Task<bool> WaitForEverythingReadyAndNotifyAsync()
    {
        ViewModel.StatusText = "Waiting for Everything Search to return indexed files and folders...";
        var readiness = await FileLister.WaitForEverythingSdkReadyAsync(
            timeout: TimeSpan.FromSeconds(90),
            pollInterval: TimeSpan.FromSeconds(1),
            cancellationToken: CancellationToken.None);

        if (!readiness.IsReady)
        {
            ViewModel.StatusText = $"Everything Search is not ready yet: {readiness.Error}. Using built-in file enumeration.";
            return false;
        }

        uint indexedCount = readiness.TotalCount > 0 ? readiness.TotalCount : readiness.ReturnedCount;
        ViewModel.StatusText = $"Everything Search is ready - {indexedCount:N0} files and folders indexed.";

        var readyDialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Everything Search Ready",
            Content = $"Everything Search returned indexed files and folders through the SDK. Fast file discovery is ready to use.\n\nIndexed items reported: {indexedCount:N0}",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };

        await readyDialog.ShowAsync();
        return true;
    }

    private static string? FindEverythingExe(string esPath)
    {
        // Everything.exe is typically in the same directory as es.exe
        var dir = Path.GetDirectoryName(esPath);
        if (dir != null)
        {
            var candidate = Path.Combine(dir, "Everything.exe");
            if (File.Exists(candidate)) return candidate;
        }
        // Check standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ── First-run context menu prompt ──────────────────────────────────
    private const string ContextMenuRegKeyDir = @"Software\Classes\Directory\shell\Yagu";
    private const string ContextMenuRegKeyBg  = @"Software\Classes\Directory\Background\shell\Yagu";
    private const string ContextMenuText = "Search with Yagu";

    private async Task CheckFirstRunContextMenuAsync()
    {
        if (ViewModel.HasCompletedFirstRun)
            return;

        // Mark first run complete regardless of what the user chooses
        ViewModel.HasCompletedFirstRun = true;
        await ViewModel.PersistSettingsAsync();

        // If context menu is already registered, nothing to do
        if (IsContextMenuRegistered())
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Add Explorer Context Menu?",
            Content = "Would you like to add a \"Search with Yagu\" option to the Windows Explorer right-click menu?\n\nThis lets you quickly search any folder by right-clicking it.",
            PrimaryButtonText = "Yes, add it",
            CloseButtonText = "No thanks",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            RegisterContextMenu();

            var successDialog = new ContentDialog
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
                Title = "Context Menu Installed",
                Content = "The \"Search with Yagu\" context menu has been added.\n\nTo use it: right-click any folder in Windows Explorer and select \"Search with Yagu\". Yagu will open with that folder ready to search.",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await successDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("ContextMenu", "Failed to register context menu", ex);

            var errorDialog = new ContentDialog
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot,
                Title = "Context Menu Registration Failed",
                Content = $"Could not register the context menu entry:\n{ex.Message}",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await errorDialog.ShowAsync();
        }
    }

    private static bool IsContextMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ContextMenuRegKeyDir);
        return key != null;
    }

    private static void RegisterContextMenu()
    {
        var exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Yagu.exe");

        foreach (var regPath in new[] { ContextMenuRegKeyDir, ContextMenuRegKeyBg })
        {
            using var shellKey = Registry.CurrentUser.CreateSubKey(regPath);
            shellKey.SetValue(null, ContextMenuText);
            shellKey.SetValue("Icon", exePath);

            using var cmdKey = Registry.CurrentUser.CreateSubKey(regPath + @"\command");
            cmdKey.SetValue(null, $"\"{exePath}\" --dir \"%V\"");
        }
    }

    // ── Skip-extensions dropdown ──────────────────────────────────
    private void OnSkipExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnSkipExtensionToggled();

    private void OnSkipExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.SkipExtensionItems) item.IsEnabled = true;
        ViewModel.OnSkipExtensionToggled();
    }

    private void OnSkipExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.SkipExtensionItems) item.IsEnabled = false;
        ViewModel.OnSkipExtensionToggled();
    }

    // ── Archive-extensions dropdown ───────────────────────────────
    private void OnArchiveExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnArchiveExtensionToggled();

    private void OnArchiveExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.ArchiveExtensionItems) item.IsEnabled = true;
        ViewModel.OnArchiveExtensionToggled();
    }

    private void OnArchiveExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.ArchiveExtensionItems) item.IsEnabled = false;
        ViewModel.OnArchiveExtensionToggled();
    }

    // ── Skip-count breakdown overlay ─────────────────────────────
    private void OnSkipCountTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        SkipBreakdownOverlay.Visibility =
            SkipBreakdownOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void OnSkipCountPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            tb.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
    }

    private void OnSkipCountPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is TextBlock tb)
            tb.TextDecorations = Windows.UI.Text.TextDecorations.None;
    }

    // ── Find / Replace bar ─────────────────────────────────────────────

    private int _findIndex = -1; // last match start index in PreviewEditor.Text

    private void OnRootGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            OpenFindBar(showReplace: false);
        }
        else if (e.Key == Windows.System.VirtualKey.H && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            OpenFindBar(showReplace: true);
        }
    }

    private void OnOpenFindReplaceBar(object sender, RoutedEventArgs e)
    {
        OpenFindBar(showReplace: true);
    }

    private void OpenFindBar(bool showReplace)
    {
        FindBar.Visibility = Visibility.Visible;
        bool inEditor = PreviewEditor.Visibility == Visibility.Visible;
        if (showReplace)
        {
            ReplaceRow.Visibility = Visibility.Visible;
            FindReplaceToggle.IsChecked = true;
            ReplaceOneButton.IsEnabled = inEditor;
            ReplaceAllButton.IsEnabled = inEditor;
            ReplaceInFilesButton.IsEnabled = ViewModel.HasResults;
        }

        // Pre-fill with selected text from the editor
        if (PreviewEditor.Visibility == Visibility.Visible && PreviewEditor.SelectedText.Length > 0 && !PreviewEditor.SelectedText.Contains('\n'))
            FindTextBox.Text = PreviewEditor.SelectedText;

        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    private void OnCloseFindBar(object sender, RoutedEventArgs e)
    {
        CloseFindBar();
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ReplaceRow.Visibility = Visibility.Collapsed;
        FindReplaceToggle.IsChecked = false;
        _findIndex = -1;
        FindStatusText.Text = string.Empty;

        // Return focus to the editor or preview
        if (PreviewEditor.Visibility == Visibility.Visible)
            PreviewEditor.Focus(FocusState.Programmatic);
    }

    private void OnFindReplaceToggle(object sender, RoutedEventArgs e)
    {
        bool show = FindReplaceToggle.IsChecked == true;
        ReplaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Single-file replace buttons only make sense in the editor
        bool inEditor = PreviewEditor.Visibility == Visibility.Visible;
        ReplaceOneButton.IsEnabled = inEditor;
        ReplaceAllButton.IsEnabled = inEditor;
        ReplaceInFilesButton.IsEnabled = ViewModel.HasResults;
    }

    private void OnFindTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                             .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) FindPrevious(); else FindNext();
            e.Handled = true;
        }
    }

    private void OnReplaceTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter) { ReplaceOne(); e.Handled = true; }
    }

    private void OnFindTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _findIndex = -1; // reset so next find starts from current selection
        UpdateFindStatus();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _findIndex = -1;
        UpdateFindStatus();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindPrevious();
    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceOne();
    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    private StringComparison FindComparison =>
        FindMatchCaseCheckBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private string FindTarget => PreviewEditor.Visibility == Visibility.Visible ? PreviewEditor.Text : GetPreviewBlockText();

    private string GetPreviewBlockText()
    {
        var sb = new StringBuilder();
        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            foreach (var block in EnumeratePreviewSectionBlocks())
                AppendBlockText(block, sb);
        }
        else
        {
            AppendBlockText(PreviewBlock, sb);
        }
        return sb.ToString();
    }

    private static void AppendBlockText(RichTextBlock richBlock, StringBuilder sb)
    {
        foreach (var block in richBlock.Blocks)
        {
            if (block is Microsoft.UI.Xaml.Documents.Paragraph p)
            {
                foreach (var inline in p.Inlines)
                {
                    if (inline is Microsoft.UI.Xaml.Documents.Run run) sb.Append(run.Text);
                    else if (inline is Microsoft.UI.Xaml.Documents.Span span)
                    {
                        foreach (var inner in span.Inlines)
                        {
                            if (inner is Microsoft.UI.Xaml.Documents.Run innerRun) sb.Append(innerRun.Text);
                        }
                    }
                }
                sb.AppendLine();
            }
        }
    }

    private void FindNext()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;
        var haystack = FindTarget;
        if (haystack.Length == 0) { FindStatusText.Text = "No content"; return; }

        int startPos = _findIndex >= 0 ? _findIndex + needle.Length : 0;
        if (startPos >= haystack.Length) startPos = 0;

        int idx = haystack.IndexOf(needle, startPos, FindComparison);
        if (idx < 0 && startPos > 0)
            idx = haystack.IndexOf(needle, 0, FindComparison); // wrap around

        if (idx < 0) { FindStatusText.Text = "No matches"; _findIndex = -1; return; }

        _findIndex = idx;
        SelectFindMatch(idx, needle.Length);
        UpdateFindStatus();
    }

    private void FindPrevious()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;
        var haystack = FindTarget;
        if (haystack.Length == 0) { FindStatusText.Text = "No content"; return; }

        int startPos = _findIndex > 0 ? _findIndex - 1 : haystack.Length - 1;

        // Search backwards by scanning substring before startPos
        int idx = haystack.LastIndexOf(needle, startPos, FindComparison);
        if (idx < 0 && startPos < haystack.Length - 1)
            idx = haystack.LastIndexOf(needle, haystack.Length - 1, FindComparison); // wrap around

        if (idx < 0) { FindStatusText.Text = "No matches"; _findIndex = -1; return; }

        _findIndex = idx;
        SelectFindMatch(idx, needle.Length);
        UpdateFindStatus();
    }

    private void SelectFindMatch(int index, int length)
    {
        if (PreviewEditor.Visibility == Visibility.Visible)
        {
            PreviewEditor.Focus(FocusState.Programmatic);
            PreviewEditor.Select(index, length);
        }
    }

    private void UpdateFindStatus()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = string.Empty; return; }
        var haystack = FindTarget;
        int count = 0;
        int pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, FindComparison)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        FindStatusText.Text = count == 0 ? "No matches" : $"{count} match{(count == 1 ? "" : "es")}";
    }

    private void ReplaceOne()
    {
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        // If the current selection matches the needle, replace it; otherwise find next first
        if (PreviewEditor.SelectedText.Equals(needle, FindComparison))
        {
            int selStart = PreviewEditor.SelectionStart;
            PreviewEditor.SelectedText = ReplaceTextBox.Text;
            _findIndex = selStart;
        }
        FindNext();
    }

    private void ReplaceAll()
    {
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var replacement = ReplaceTextBox.Text;
        var text = PreviewEditor.Text;
        var sb = new StringBuilder(text.Length);
        int count = 0;
        int pos = 0;
        while (true)
        {
            int idx = text.IndexOf(needle, pos, FindComparison);
            if (idx < 0) { sb.Append(text, pos, text.Length - pos); break; }
            sb.Append(text, pos, idx - pos);
            sb.Append(replacement);
            count++;
            pos = idx + needle.Length;
        }

        if (count > 0)
        {
            _suppressPreviewEditorTextChanged = true;
            PreviewEditor.Text = sb.ToString();
            _suppressPreviewEditorTextChanged = false;
            _previewEditorDirty = true;
            UpdatePreviewEditorButtons();
        }

        _findIndex = -1;
        FindStatusText.Text = count > 0 ? $"Replaced {count}" : "No matches";
    }

    private async void OnReplaceInAllFiles(object sender, RoutedEventArgs e)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) { FindStatusText.Text = "Enter text to find"; return; }

        var replacement = ReplaceTextBox.Text;
        var comparison = FindComparison;
        var groups = ViewModel.ResultGroups.ToList();

        if (groups.Count == 0) { FindStatusText.Text = "No result files"; return; }

        ReplaceInFilesButton.IsEnabled = false;
        FindStatusText.Text = "Scanning files…";

        // Collect changes off the UI thread.
        var changes = await Task.Run(() =>
        {
            var list = new List<(string Path, string Original, string Replaced, Encoding Encoding, int Count)>();
            foreach (var group in groups)
            {
                if (group.IsArchiveEntry) continue;
                var path = group.FilePath;
                if (!File.Exists(path)) continue;
                try
                {
                    Encoding encoding;
                    string original;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                    {
                        encoding = Helpers.EncodingDetector.DetectEncoding(stream);
                        if (encoding is System.Text.UTF8Encoding)
                            encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                        original = reader.ReadToEnd();
                        encoding = reader.CurrentEncoding;
                    }

                    var sb = new System.Text.StringBuilder(original.Length);
                    int replaceCount = 0;
                    int pos = 0;
                    while (true)
                    {
                        int idx = original.IndexOf(needle, pos, comparison);
                        if (idx < 0) { sb.Append(original, pos, original.Length - pos); break; }
                        sb.Append(original, pos, idx - pos);
                        sb.Append(replacement);
                        replaceCount++;
                        pos = idx + needle.Length;
                    }
                    if (replaceCount > 0)
                    {
                        var replaced = sb.ToString();
                        if (!TextHasUnencodableCharacters(replaced, encoding))
                            list.Add((path, original, replaced, encoding, replaceCount));
                    }
                }
                catch { /* skip unreadable files */ }
            }
            return list;
        });

        if (changes.Count == 0)
        {
            FindStatusText.Text = "No matches in any file";
            ReplaceInFilesButton.IsEnabled = true;
            return;
        }

        int totalReplacements = changes.Sum(c => c.Count);

        // Confirm before writing.
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Replace in All Files",
            Content = $"Replace {totalReplacements:N0} occurrence{(totalReplacements == 1 ? "" : "s")} across {changes.Count:N0} file{(changes.Count == 1 ? "" : "s")}?",
            PrimaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            FindStatusText.Text = "Cancelled";
            ReplaceInFilesButton.IsEnabled = true;
            return;
        }

        // Write changes to disk.
        int written = 0;
        int errors = 0;
        bool backupEnabled = ViewModel.BackupBeforeSave;
        await Task.Run(() =>
        {
            foreach (var (path, _, replaced, encoding, _) in changes)
            {
                try
                {
                    if (backupEnabled)
                    {
                        var bakPath = path + ".yagubak";
                        if (!File.Exists(bakPath))
                            File.Copy(path, bakPath, overwrite: false);
                        else
                        {
                            int suffix = 2;
                            while (File.Exists($"{path}.yagubak-{suffix}")) suffix++;
                            File.Copy(path, $"{path}.yagubak-{suffix}", overwrite: false);
                        }
                    }
                    File.WriteAllText(path, replaced, encoding);
                    Interlocked.Increment(ref written);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

        // Re-validate results for each changed file.
        foreach (var (path, _, replaced, _, _) in changes)
        {
            ViewModel.RevalidateFileResults(path, replaced);
        }

        // Refresh preview if the currently shown file was affected.
        if (_previewResult is { } current && changes.Any(c =>
                string.Equals(c.Path, current.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowSingleFilePreviewAsync(current, fullFile: false);
        }

        var statusParts = new List<string> { $"Replaced in {written:N0} file{(written == 1 ? "" : "s")}" };
        if (errors > 0) statusParts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
        FindStatusText.Text = string.Join(", ", statusParts);
        ViewModel.StatusText = FindStatusText.Text;
        ReplaceInFilesButton.IsEnabled = true;
    }
}
