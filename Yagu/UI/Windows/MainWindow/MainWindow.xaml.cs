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

public sealed partial class MainWindow : Window, IDisposable
{
    private const int VisibleResultShowMoreBatchSize = 24;

    public MainViewModel ViewModel { get; }
    private bool _suppressPreviewUpdate;
    private int _previewUpdateGen;
    private readonly HashSet<string> _pendingPreviewFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private int _lastCheckedGroupIndex = -1;
    private FileGroup? _ctrlFileHeaderGestureGroup;
    private bool _ctrlFileHeaderGestureWasExpanded;
    private uint _ctrlFileHeaderGesturePointerId;
    private FileGroup? _lastResultsContextMenuGroup;
    private long _lastResultsContextMenuTick;
    private string? _lastCtrlFileHeaderPreviewPath;
    private long _lastCtrlFileHeaderPreviewTick;
    private bool _autoScrollEnabled;
    // Deferred-files state — for tail of newFiles past PreviewSectionPageSize.
    // The match-nav label includes deferred matches even though their sections
    // are not yet inserted in the visual tree.
    private List<KeyValuePair<string, List<SearchResult>>>? _deferredOrderedFiles;
    private int _deferredCursor;
    private List<SearchResult>? _deferredAllSelected;
    private int _deferredGen;
    private StackPanel? _deferredButtonPanel;
    private bool _autoLoadMoreInFlight;
    private bool _autoLoadOverflowInFlight;
    private long _suppressOverflowAutoLoadUntilTick;
    private long _lastPreviewManualScrollTick;
    private const int AutoLoadChunkSize = 5;
    private const double OverflowPrefetchBufferViewports = 4.0;
    private const int AutoLoadOverflowMaxMatchesPerFrame = 20;
    private const int MatchNavigationOverflowAutoLoadSuppressMs = 2_000;
    private const int SingleStepOverflowExpandMatches = 1;
    private bool _querySuggestionsDetached;
    private bool _querySuggestionsUserOpened;
    private long _hideSuggestionsTick;
    private long _suppressQuerySuggestionsUntilTick;
    private DispatcherTimer? _autoScrollTimer;
    private DispatcherTimer? _previewContextDebounceTimer;
    private readonly Services.DiskUtilizationService _diskUtilService = new();
    private DispatcherTimer? _diskSparklineTimer;
    private const long DefaultFullFilePreviewLimitBytes = 1L * 1024 * 1024 * 1024;
    private long EffectiveFullFilePreviewLimitBytes => ViewModel.FullFilePreviewLimitMB > 0 ? (long)ViewModel.FullFilePreviewLimitMB * 1024 * 1024 : DefaultFullFilePreviewLimitBytes;
    private CancellationTokenSource? _previewLoadCts;
    private string? _previewEditorPath;
    private Encoding? _previewEditorEncoding;
    private bool _previewEditorDirty;
    private string? _previewEditorOriginalText;
    private bool _suppressPreviewEditorTextChanged;
    private volatile bool _previewMutating;
    private readonly HotkeyService _hotkeyService = new();
    private SubclassProc? _hotkeySubclassProc;
    private IntPtr _hwnd;
    private bool _hotkeyHookInstalled;
    private bool _suppressHotkeySettingChange;
    private Helpers.TrayIcon? _trayIcon;
    private bool _screenshotCaptureInFlight;
    private bool _forceClose;
    private bool _disposed;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    private bool _autoSearchOnLoad;
    private bool _launcherMode;
    private bool _nativeCaptionButtonsVisible = true;
    private bool _isLoaded;

    public MainWindow(string? startupDirectory, string? startupQuery = null, int? startupWindowFocusBehavior = null)
    {
        ViewModel = new MainViewModel();
        if (startupWindowFocusBehavior is >= 0 and <= 3)
        {
            // Legacy CLI: value 3 meant "Traditional window" \u2014 honour it by skipping launcher mode.
            // Other values map directly onto the deactivation-only WindowFocusBehavior setting.
            if (startupWindowFocusBehavior.Value == 3)
            {
                ViewModel.StartInLauncherMode = false;
                ViewModel.WindowFocusBehavior = 1;
            }
            else
            {
                ViewModel.WindowFocusBehavior = startupWindowFocusBehavior.Value;
            }
        }
        ViewModel.SetDirectoryFromArgs(startupDirectory);
        if (!string.IsNullOrWhiteSpace(startupQuery))
        {
            ViewModel.Query = startupQuery;
            _autoSearchOnLoad = !string.IsNullOrWhiteSpace(startupDirectory);
        }
        InitializeComponent();
        TextControlBoxNS.TextControlBoxDiagnostics.VerboseLogger = (source, message) => LogService.Instance.Verbose(source, message);
        TextControlBoxNS.TextControlBoxDiagnostics.IsVerboseEnabledProvider = () => LogService.Instance.IsVerboseEnabled;
        QueryBox.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnQueryBoxPointerPressed),
            handledEventsToo: true);
        SearchCancelButton.SizeChanged += (_, _) => AlignBrowseButtonToSearchButton();
        SyncLayoutToggles(ViewModel.PreviewModeIndex);
        Title = AppInfo.WindowTitle;

        // PreviewBlock's built-in text-selection swallows DoubleTapped by
        // marking it handled. AddHandler with handledEventsToo: true is the
        // only way to still receive it.
        PreviewBlock.AddHandler(UIElement.DoubleTappedEvent,
            new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(OnPreviewBlockDoubleTapped),
            handledEventsToo: true);
        AttachPreviewSelectionAutoScroll(PreviewBlock);
        ConfigurePreviewSelectionMode(PreviewBlock);
        AttachPreviewBlockContextFlyout(PreviewBlock);
        InitializePreviewEditorZoom();
        InitializeResultsListSmartScroll();
        PreviewScrollViewer.SizeChanged += OnPreviewViewportSizeChanged;

        // Extend content into the title bar for a modern Windows 11 look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTitleText.Text = AppInfo.WindowTitle;

        ApplyTitleBarButtonTheme();

        // Reserve room on the right for native caption buttons when they are visible;
        // launcher chrome hides them, so it uses the custom close button instead.
        SetNativeCaptionButtonsVisible(true);
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
                if (_previewContextDebounceTimer is null)
                {
                    _previewContextDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    _previewContextDebounceTimer.Tick += (_, _) =>
                    {
                        _previewContextDebounceTimer.Stop();
                        RefreshCurrentPreview(preserveScroll: true);
                    };
                }
                _previewContextDebounceTimer.Stop();
                _previewContextDebounceTimer.Start();
            }

            if (!_suppressHotkeySettingChange &&
                (e.PropertyName == nameof(ViewModel.GlobalHotkeyEnabled) || e.PropertyName == nameof(ViewModel.GlobalHotkeyKey)))
            {
                ApplyGlobalHotkeyRegistration();
            }

            if (e.PropertyName == nameof(ViewModel.SelectedPreviewContentBackgroundColor) ||
                e.PropertyName == nameof(ViewModel.UnselectedPreviewContentBackgroundColor))
            {
                ApplyPreviewSectionBackgrounds();
            }

            if (e.PropertyName == nameof(ViewModel.PreviewGutterContextColor) ||
                e.PropertyName == nameof(ViewModel.PreviewGutterMatchColor) ||
                e.PropertyName == nameof(ViewModel.PreviewEditorGutterColor) ||
                e.PropertyName == nameof(ViewModel.PreviewMatchTextColor) ||
                e.PropertyName == nameof(ViewModel.PreviewOverlayColor) ||
                e.PropertyName == nameof(ViewModel.PreviewMatchLineColor))
            {
                ApplyPreviewColors();
            }
        };

        ((FrameworkElement)Content).Loaded += OnContentLoaded;

        // Start in compact "launcher" mode unless we have a query to auto-run or the user has
        // opted out via Settings \u2192 Window \u2192 "Start in compact launcher mode".
        // A CLI window-mode override is explicit, so apply it even for auto-search launches.
        if ((!_autoSearchOnLoad || startupWindowFocusBehavior.HasValue) && ViewModel.StartInLauncherMode)
        {
            EnterLauncherMode();
        }
        UpdateBottomStatusBarVisibility();

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
                if (TryGetApplicationStyle("DefaultButtonStyle") is { } defaultButtonStyle)
                    SearchCancelButton.Style = defaultButtonStyle;
                SearchCancelButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 220, 220));
                SearchCancelButton.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 80, 80));
                SearchCancelButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 24, 24));
                _autoScrollEnabled = AutoScrollResultsCheckBox.IsChecked == true;
                _autoScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _autoScrollTimer.Tick += OnAutoScrollTick;
                _autoScrollTimer.Start();
                ThroughputSparkline.Opacity = 0.8;
                DiskGaugeBar.Opacity = 0.5;
            }
            else
            {
                SearchCancelIcon.Glyph = "\uE721";   // Search magnifier
                SearchCancelLabel.Text = "Search";
                ToolTipService.SetToolTip(SearchCancelButton, "Search (F5)");
                if (TryGetApplicationStyle("AccentButtonStyle") is { } accentButtonStyle)
                    SearchCancelButton.Style = accentButtonStyle;
                SearchCancelButton.ClearValue(Control.BackgroundProperty);
                SearchCancelButton.ClearValue(Control.BorderBrushProperty);
                SearchCancelButton.ClearValue(Control.ForegroundProperty);
                _autoScrollTimer?.Stop();
                ThroughputSparkline.Opacity = 0.45;
                DiskGaugeBar.Opacity = 0.25;
            }
        };

        // Update tray tooltip and taskbar progress during search.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.FilesScanned) || e.PropertyName == nameof(ViewModel.TotalFiles))
            {
                if (ViewModel.IsSearching && ViewModel.TotalFiles > 0)
                {
                    _trayIcon?.SetTooltip(ViewModel.ProgressTooltip);
                    Helpers.TaskbarProgress.SetProgress(_hwnd,
                        (ulong)ViewModel.FilesScanned, (ulong)ViewModel.TotalFiles);
                }
            }
            else if (e.PropertyName == nameof(ViewModel.IsSearching))
            {
                if (!ViewModel.IsSearching)
                {
                    _trayIcon?.SetTooltip("Yagu");
                    Helpers.TaskbarProgress.ClearProgress(_hwnd);
                }
            }
        };

        // Hide to system tray when the window loses focus.
        this.Activated += OnWindowActivated;

        // Intercept window close: dock to tray instead of exiting (when enabled).
        AppWindow.Closing += OnAppWindowClosing;

        // Flush logs when the window actually closes.
        this.Closed += (_, _) =>
        {
            Dispose();
            LogService.Instance.Info("MainWindow", "Window closing — flushing logs");
            LogService.Instance.Flush();
        };

        // Show admin banner when not elevated
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator) && !ViewModel.SuppressAdminWarning)
            AdminBanner.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        // Defer _isLoaded until the visual tree is materialized so that
        // Checked events queued by x:Bind during InitializeComponent
        // (which fire asynchronously on the dispatcher AFTER the
        // constructor returns) are still treated as "initial load".
        ((FrameworkElement)this.Content).Loaded += (_, _) => _isLoaded = true;

        // Start disk utilization polling (background thread) and a UI timer to redraw the sparkline
        _diskUtilService.Start();
        _diskSparklineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diskSparklineTimer.Tick += (_, _) => UpdateSparkline();
        _diskSparklineTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoScrollTimer?.Stop();
        _previewContextDebounceTimer?.Stop();
        _diskSparklineTimer?.Stop();
        DisposePreviewSelectionAutoScroll();
        DisposeResultsListSmartScroll();
        DisposeTerminal();
        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        _previewLoadCts = null;
        RemoveGlobalHotkeyHook();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _hotkeyService.Dispose();
        _diskUtilService.Dispose();
        ViewModel.Dispose();
        GC.SuppressFinalize(this);
    }


}
