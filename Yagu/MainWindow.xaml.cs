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
    private const int AutoLoadChunkSize = 5;
    private bool _querySuggestionsDetached;
    private long _hideSuggestionsTick;
    private long _suppressQuerySuggestionsUntilTick;
    private DispatcherTimer? _autoScrollTimer;
    private DispatcherTimer? _previewContextDebounceTimer;
    private readonly Services.DiskUtilizationService _diskUtilService = new();
    private readonly Services.ScreenshotCaptureService _screenshotService = new();
    private DispatcherTimer? _diskSparklineTimer;
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
    private bool _screenshotCaptureInFlight;
    private bool _forceClose;

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
            ViewModel.WindowFocusBehavior = startupWindowFocusBehavior.Value;
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
        AttachPreviewBlockContextFlyout(PreviewBlock);
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
        };

        ((FrameworkElement)Content).Loaded += OnContentLoaded;

        // Start in compact "launcher" mode unless we have a query to auto-run.
        // A CLI window-mode override is explicit, so apply it even for auto-search launches.
        if (!_autoSearchOnLoad || startupWindowFocusBehavior.HasValue)
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
            _trayIcon?.Dispose();
            _trayIcon = null;
            _hotkeyService.Dispose();
            _diskSparklineTimer?.Stop();
            _diskUtilService.Dispose();
            RemoveGlobalHotkeyHook();
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

    private void UpdateTitleBarInsets()
    {
        try
        {
            double rightDip = 0;
            if (_nativeCaptionButtonsVisible)
            {
                var titleBar = AppWindow?.TitleBar;
                if (titleBar is not null)
                {
                    var scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
                    rightDip = titleBar.RightInset / scale;
                }

                if (rightDip < 48) rightDip = 148;
            }
            else
            {
                rightDip = 8;
            }

            TitleBarActions.Margin = new Thickness(0, 0, rightDip, 0);

            double actionWidth = TitleBarActions.ActualWidth;
            if (actionWidth <= 0)
                actionWidth = _nativeCaptionButtonsVisible ? 80 : 120;
            AppTitleBar.Padding = new Thickness(16, 0, rightDip + actionWidth + 16, 0);
        }
        catch { /* AppWindow not always available; ignore */ }
    }

    private void SetNativeCaptionButtonsVisible(bool visible)
    {
        _nativeCaptionButtonsVisible = visible;
        CloseWindowButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateTitleBarInsets();
    }

    private static Style? TryGetApplicationStyle(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var value) && value is Style style
                ? style
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyTitleBarButtonTheme()
    {
        try
        {
            var titleBar = AppWindow?.TitleBar;
            if (titleBar is null) return;

            var darkTitleBar = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20);
            titleBar.BackgroundColor = darkTitleBar;
            titleBar.InactiveBackgroundColor = darkTitleBar;
            titleBar.ForegroundColor = Microsoft.UI.Colors.White;
            titleBar.InactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        }
        catch { }
    }

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_autoScrollEnabled || ViewModel.ResultGroups.Count == 0) return;
        ResultsList.ScrollIntoView(ViewModel.ResultGroups[^1]);
    }

    private void UpdateSparkline()
    {
        var samples = _diskUtilService.GetSamples();

        // Update gauge bar and label even with few samples
        if (samples.Count > 0)
        {
            var latest = samples[^1];
            double gaugeContainerWidth = DiskGaugeBar.Parent is FrameworkElement parent ? parent.ActualWidth : 0;
            if (gaugeContainerWidth > 0)
                DiskGaugeBar.Width = latest.UtilizationPct / 100.0 * gaugeContainerWidth;

            DiskGaugeLabel.Text = $"{latest.MBPerSec:N0} MB/s \u00b7 {latest.UtilizationPct:N0}%";
        }
        else
        {
            DiskGaugeBar.Width = 0;
            DiskGaugeLabel.Text = string.Empty;
        }

        // Sparkline needs at least 2 points
        if (samples.Count < 2)
        {
            ThroughputSparkline.Points.Clear();
            return;
        }

        double width = ThroughputSparkline.ActualWidth;
        double height = ThroughputSparkline.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Plot disk MB/s
        double max = 1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].MBPerSec > max) max = samples[i].MBPerSec;
        }

        var pts = ThroughputSparkline.Points;
        pts.Clear();
        double xStep = width / (samples.Count - 1);
        for (int i = 0; i < samples.Count; i++)
        {
            double x = i * xStep;
            double y = height - (samples[i].MBPerSec / max * (height - 2)) - 1;
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

    private void OnFilterBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (IsFilterExampleText(tb))
            tb.Text = string.Empty;

        tb.PlaceholderText = string.Empty;
    }

    private void OnFilterBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (string.IsNullOrEmpty(tb.Text))
        {
            if (ReferenceEquals(tb, IncludeFilterBox))
                tb.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
            else
                tb.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
        }
    }

    private bool IsFilterExampleText(TextBox textBox)
    {
        string text = textBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return false;

        if (ReferenceEquals(textBox, IncludeFilterBox))
            return string.Equals(text, ViewModel.IncludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase);

        return string.Equals(text, ViewModel.ExcludeFilterPlaceholder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, AppSettings.DefaultExcludeGlobs, StringComparison.OrdinalIgnoreCase);
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
            if (!await CheckHddAndWarnAsync()) return;
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
        if (!await CheckHddAndWarnAsync()) return;
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
                 && !AreQuerySuggestionsSuppressed()
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

    private async Task CopyWindowScreenshotToClipboardAsync()
    {
        if (_screenshotCaptureInFlight)
            return;

        _screenshotCaptureInFlight = true;

        try
        {
            RootGrid.UpdateLayout();
            await YieldLowAsync();

            var capture = _screenshotService.CaptureWindow(_hwnd);

            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                stream);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                (uint)capture.Width,
                (uint)capture.Height,
                capture.Dpi,
                capture.Dpi,
                capture.Pixels);
            await encoder.FlushAsync();
            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();

            ViewModel.StatusText = $"Screenshot copied to clipboard ({capture.Width:N0}x{capture.Height:N0}).";
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Screenshot", "Could not copy window screenshot to clipboard", ex);
            ViewModel.StatusText = "Could not copy screenshot to clipboard.";
        }
        finally
        {
            _screenshotCaptureInFlight = false;
        }
    }

    private void RestoreQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        if (AreQuerySuggestionsSuppressed())
        {
            target.IsSuggestionListOpen = false;
            return;
        }

        if (!_querySuggestionsDetached) return;
        // After a deliberate hide (Enter to search), suppress re-attach briefly
        // so the AutoSuggestBox's spurious TextChanged events don't reopen the popup.
        if (Environment.TickCount64 - _hideSuggestionsTick < 400) return;
        _querySuggestionsDetached = false;
        target.ItemsSource = ViewModel.SearchHistory;
        target.IsSuggestionListOpen = false;
    }

    private bool AreQuerySuggestionsSuppressed()
        => Environment.TickCount64 < _suppressQuerySuggestionsUntilTick;

    private void SuppressQuerySuggestionsFor(int milliseconds, AutoSuggestBox? box = null)
    {
        long until = Environment.TickCount64 + milliseconds;
        if (until > _suppressQuerySuggestionsUntilTick)
            _suppressQuerySuggestionsUntilTick = until;

        HideQuerySuggestions(box);
    }

    private void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && !AreQuerySuggestionsSuppressed())
            RestoreQuerySuggestions(sender);
    }

    private void OnQueryLostFocus(object sender, RoutedEventArgs e)
    {
        if (!AreQuerySuggestionsSuppressed())
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

    private async void OnObeyGitignoreToggled(object sender, RoutedEventArgs e)
    {
        // Only show the dialog when the toggle is being turned on (not during initial load).
        if (!_isLoaded) return;
        if (sender is ToggleSwitch ts && !ts.IsOn) return;

        var dialog = new ContentDialog
        {
            Title = ".gitignore precedence",
            Content = "Should .gitignore exclusions take precedence over your Include filter?\n\n" +
                      "Yes — files excluded by .gitignore will be skipped even if they match your Include filter.\n\n" +
                      "No — your Include filter takes priority; matching files will be searched even if .gitignore would exclude them.",
            PrimaryButtonText = "Yes, .gitignore wins",
            SecondaryButtonText = "No, Include filter wins",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        ViewModel.GitignoreTakesPrecedence = result != ContentDialogResult.Secondary;
    }

    private async Task PickFolderAsync(Windows.Storage.Pickers.FolderPicker picker)
    {
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.Directory = folder.Path;
    }

    private SettingsWindow? _settingsWindow;

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        // If a settings window is already open, activate it instead of creating a new one.
        if (_settingsWindow is not null)
        {
            try { _settingsWindow.Activate(); return; }
            catch { _settingsWindow = null; }
        }

        _settingsWindow = new SettingsWindow(ViewModel, _hotkeyService, _hwnd, ApplyWordWrap);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Checks if the search directory is on a rotational HDD and, if LimitParallelismOnHdd is enabled,
    /// forces parallelism to 1 and warns the user. Returns false if the user cancels.
    /// </summary>
    private async Task<bool> CheckHddAndWarnAsync()
    {
        if (!ViewModel.LimitParallelismOnHdd) return true;
        if (string.IsNullOrWhiteSpace(ViewModel.Directory)) return true;
        if (!Helpers.DiskTypeDetector.IsHardDisk(ViewModel.Directory)) return true;

        // Force parallelism to 1 (sequential)
        ViewModel.ParallelismIndex = 1;

        var contentPanel = new StackPanel { Spacing = 8, MinWidth = 360 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = "The selected search directory is on a rotational hard disk (HDD). " +
                   "Parallelism has been set to 1 thread to avoid excessive disk thrashing.",
            TextWrapping = TextWrapping.Wrap,
        });

        var secondBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };
        secondBlock.Inlines.Add(new Run { Text = "You can increase parallelism or disable this warning in " });
        var settingsLink = new Hyperlink();
        settingsLink.Inlines.Add(new Run { Text = "Settings \u2192 Performance" });
        settingsLink.Click += (_, _) =>
        {
            if (_settingsWindow is not null)
            {
                try { _settingsWindow.Activate(); return; }
                catch { _settingsWindow = null; }
            }
            _settingsWindow = new SettingsWindow(ViewModel, _hotkeyService, _hwnd, ApplyWordWrap);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
            _settingsWindow.SelectTab(2);
        };
        secondBlock.Inlines.Add(settingsLink);
        secondBlock.Inlines.Add(new Run { Text = "." });
        contentPanel.Children.Add(secondBlock);

        var dialog = new ContentDialog
        {
            Title = "HDD detected — parallelism limited",
            Content = contentPanel,
            PrimaryButtonText = "Continue search",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
            Resources =
            {
                ["ContentDialogMaxHeight"] = double.PositiveInfinity,
            },
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        if (!_forceClose && ViewModel.CloseToTray)
        {
            HideToTray(isCloseToTray: true);
            return;
        }
        Close();
    }

    private HelpWindow? _helpWindow;

    private void OnOpenCredits(object sender, RoutedEventArgs e)
        => OpenHelpWindow();

    private void OpenHelpWindow()
    {
        if (_helpWindow is not null)
        {
            try { _helpWindow.Activate(); return; }
            catch { _helpWindow = null; }
        }

        var helpPath = Path.Combine(AppContext.BaseDirectory, "HELP.html");
        _helpWindow = new HelpWindow(_hwnd, helpPath);
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Activate();
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
    // Bulk match navigation: step size for Ctrl+Click on Next/Prev.
    // 0 = not configured yet (show flyout on Ctrl+Click).
    private int _bulkMatchStep;
    // When true the flyout won't be shown again this session; Ctrl+Click
    // jumps _bulkMatchStep immediately.
    private bool _bulkMatchStepLocked;
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
    // Section content-block → gutter-block for sticky line-number display.
    private readonly Dictionary<RichTextBlock, RichTextBlock> _sectionGutterBlocks = new();
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
    private readonly List<Border> _activeMatchExtraWordMarkers = new();
    private int _matchScrollRequestId;
    private bool _activeMatchOverlayRefreshPending;
    private int _activeMatchOverlayUpdateRequestId;
    private int _previewManualScrollVersion;
    private bool _suppressInitialMatchAutoScroll;
    private RichTextBlock? _activeOverlayStabilityBlock;
    private double _activeOverlayLastBlockTop = double.NaN;
    private long _activeOverlayLastMoveTick;
    private int _activeOverlayStablePasses;

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

        UpdateBottomStatusBarVisibility();
    }

    private void UpdateBottomStatusBarVisibility()
    {
        bool showStatusBar = !_resultsPaneCollapsed
            && SplitPaneGrid.Visibility == Visibility.Visible
            && (ResultsPanelBorder.Visibility == Visibility.Visible
                || PreviewPanelBorder.Visibility == Visibility.Visible);

        StatusBarRow.Height = showStatusBar ? GridLength.Auto : new GridLength(0);
        if (!showStatusBar)
            SkipBreakdownOverlay.Visibility = Visibility.Collapsed;
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

    // ── Responsive results list: compact (stacked) vs wide (side-by-side) ───
    private bool _resultsCompactMode;
    private const double ResultsCompactThreshold = 550;

    private void OnResultsListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < ResultsCompactThreshold;
        if (compact == _resultsCompactMode) return;
        _resultsCompactMode = compact;
        // Update all currently materialized containers
        for (int i = 0; i < ResultsList.Items.Count; i++)
        {
            if (ResultsList.ContainerFromIndex(i) is FrameworkElement container)
                ApplyResultsCompactState(container, compact);
        }
    }

    private void OnResultsListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue)
            ApplyResultsCompactState(args.ItemContainer, _resultsCompactMode);
    }

    private static void ApplyResultsCompactState(FrameworkElement container, bool compact)
    {
        // Find TextBlocks by Tag within the container's visual tree
        ApplyCompactStateRecursive(container, compact);
    }

    private static void ApplyCompactStateRecursive(Microsoft.UI.Xaml.DependencyObject parent, bool compact)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb)
            {
                if (tb.Tag is string tag)
                {
                    if (tag == "CompactDir")
                        tb.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                    else if (tag == "WideDir")
                        tb.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else if (child is Microsoft.UI.Xaml.DependencyObject dep)
            {
                ApplyCompactStateRecursive(dep, compact);
            }
        }
    }

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileGroup g && g.Count > 0)
        {
            LogService.Instance.Info("Preview",
                $"OnResultItemClick: no preview change file='{g.FilePath}', matchCount={g.Count}");
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
            if (_expanderFilePaths.TryGetValue(child, out var path)
                && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
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
                    PreviewScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
                }
                catch { /* Layout not ready — ignore */ }
                return true;
            }
        }
        return false;
    }

    private bool PreviewSectionExists(string filePath)
    {
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path)
                && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFindPreviewSection(string filePath, out Expander expander, out RichTextBlock section)
    {
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path)
                && string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase)
                && child.Tag is RichTextBlock block)
            {
                expander = child;
                section = block;
                return true;
            }
        }

        expander = null!;
        section = null!;
        return false;
    }

    private HashSet<string> GetExistingPreviewFilePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (_expanderFilePaths.TryGetValue(child, out var path))
                paths.Add(path);
        }
        return paths;
    }

    private void ReorderMatchParagraphsToPreviewSectionOrder()
    {
        if (_matchParagraphs.Count <= 1 || PreviewSectionsPanel.Visibility != Visibility.Visible)
            return;

        var orderedBlocks = PreviewSectionsPanel.Children
            .OfType<Expander>()
            .Select(expander => expander.Tag as RichTextBlock)
            .Where(block => block is not null)
            .Cast<RichTextBlock>()
            .ToList();
        if (orderedBlocks.Count <= 1)
            return;

        var buckets = new Dictionary<RichTextBlock, List<(RichTextBlock block, Paragraph para, int matchInPara)>>();
        foreach (var block in orderedBlocks)
            buckets[block] = new List<(RichTextBlock block, Paragraph para, int matchInPara)>();

        List<(RichTextBlock block, Paragraph para, int matchInPara)>? unmatched = null;
        foreach (var entry in _matchParagraphs)
        {
            if (buckets.TryGetValue(entry.block, out var bucket))
                bucket.Add(entry);
            else
                (unmatched ??= new List<(RichTextBlock block, Paragraph para, int matchInPara)>()).Add(entry);
        }

        var reordered = new List<(RichTextBlock block, Paragraph para, int matchInPara)>(_matchParagraphs.Count);
        foreach (var block in orderedBlocks)
            reordered.AddRange(buckets[block]);
        if (unmatched is not null)
            reordered.AddRange(unmatched);

        bool changed = false;
        for (int i = 0; i < reordered.Count; i++)
        {
            if (!ReferenceEquals(reordered[i].block, _matchParagraphs[i].block)
                || !ReferenceEquals(reordered[i].para, _matchParagraphs[i].para)
                || reordered[i].matchInPara != _matchParagraphs[i].matchInPara)
            {
                changed = true;
                break;
            }
        }

        if (!changed)
            return;

        _matchParagraphs.Clear();
        _matchParagraphs.AddRange(reordered);
        InvalidateParagraphIndexCache();
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
        _sectionGutterBlocks.Clear();
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
        PreviewScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, _) => NotePreviewManualScrollInput("wheel")),
            handledEventsToo: true);
        PreviewScrollViewer.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => NotePreviewManualScrollInput("pointer")),
            handledEventsToo: true);
        PreviewScrollViewer.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (IsPreviewScrollKey(e.Key))
                    NotePreviewManualScrollInput($"key:{e.Key}");
            }),
            handledEventsToo: true);
    }

    private void NotePreviewManualScrollInput(string source)
    {
        _previewManualScrollVersion++;
        InvalidatePendingMatchScrolls();
        _activeMatchOverlayUpdateRequestId++;
        HideActiveMatchOverlay();
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"Preview manual scroll input: source={source}, version={_previewManualScrollVersion}");
    }

    private static bool IsPreviewScrollKey(Windows.System.VirtualKey key)
        => key is Windows.System.VirtualKey.Up
            or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.Left
            or Windows.System.VirtualKey.Right
            or Windows.System.VirtualKey.PageUp
            or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home
            or Windows.System.VirtualKey.End
            or Windows.System.VirtualKey.Space;

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
        QueueActiveMatchOverlayRefresh();
        UpdateStickyFileHeader();
        if (e.IsIntermediate) return;
        TryAutoLoadMoreOnScroll();
        TryAutoLoadOverflowOnScroll();
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
                HideStickyFileHeader();
                return;
            }

            var sv = PreviewScrollViewer;
            double vpTop = 0; // viewport-relative top (we transform expanders into ScrollViewer space)
            double vpBottom = sv.ViewportHeight;
            if (vpBottom <= 0)
            {
                HideStickyFileHeader();
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
                HideStickyFileHeader();
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

    private void HideStickyFileHeader()
    {
        _stickyHeaderExpander = null;
        StickyFileHeader.Child = null;
        StickyFileHeader.Visibility = Visibility.Collapsed;
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

    /// <summary>
    /// When the user scrolls near the bottom of a section that has overflow
    /// (truncated matches), automatically expand the next chunk so reading
    /// is seamless. The chunk size is controlled by the
    /// <see cref="AppSettings.PreviewAutoLoadMatches"/> setting.
    /// </summary>
    private void TryAutoLoadOverflowOnScroll()
    {
        if (_autoLoadOverflowInFlight) return;
        int autoLoadCount = ViewModel.PreviewAutoLoadMatches;
        if (autoLoadCount <= 0) return;
        if (_sectionOverflow.Count == 0) return;

        var sv = PreviewScrollViewer;
        double vpH = sv.ViewportHeight;
        if (vpH <= 0) return;
        double vpBottom = sv.VerticalOffset + vpH;

        // Find any section whose truncation notice is within one viewport of
        // the current scroll position (i.e. about to become visible).
        foreach (var (section, ov) in _sectionOverflow)
        {
            if (ov.NoticePara is null) continue;
            try
            {
                // Get the position of the truncation notice relative to the scroll viewer content.
                var transform = section.TransformToVisual(sv);
                var sectionTop = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y + sv.VerticalOffset;
                double sectionBottom = sectionTop + section.ActualHeight;

                // Trigger when the bottom of the section (where the notice lives)
                // is within one viewport height below the current view.
                if (sectionBottom <= vpBottom + vpH && sectionBottom >= sv.VerticalOffset - vpH)
                {
                    _autoLoadOverflowInFlight = true;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        try
                        {
                            ExpandOverflowChunk(section, autoLoadCount);
                        }
                        finally
                        {
                            _autoLoadOverflowInFlight = false;
                        }
                    });
                    return; // Only expand one section per scroll event.
                }
            }
            catch { /* TransformToVisual can throw if not in tree */ }
        }
    }

    /// <summary>
    /// Expands up to <paramref name="matchCount"/> matches from a section's
    /// overflow, similar to <see cref="ExpandSectionNextChunk"/> but with a
    /// configurable chunk size.
    /// </summary>
    private void ExpandOverflowChunk(RichTextBlock section, int matchCount)
    {
        if (!_sectionOverflow.TryGetValue(section, out var ov)) return;
        int chunkSize = Math.Min(matchCount, ov.RemainingResults.Count);
        if (chunkSize <= 0)
        {
            _sectionOverflow.Remove(section);
            return;
        }

        // Remove the existing truncation notice.
        if (ov.NoticePara != null)
        {
            section.Blocks.Remove(ov.NoticePara);
            // Also remove the corresponding gutter spacer (always the last block).
            if (_sectionGutterBlocks.TryGetValue(section, out var gb) && gb.Blocks.Count > 0)
                gb.Blocks.RemoveAt(gb.Blocks.Count - 1);
        }

        // Compute insertion point in _matchParagraphs.
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

        int consumed = 0;
        if (ov.IsHighlightMode && ov.AllLines != null)
        {
            AppendHighlightMatchWindows(
                section,
                ov.RemainingResults,
                ov.AllLines,
                ov.Rx,
                sn,
                ov.PreviewLines,
                MaxMatchEntriesPerExpandChunk,
                chunkSize,
                MaxPreviewBlocksPerExpandChunk,
                out consumed,
                out _,
                out int lastRenderedLine,
                ov.LastRenderedLine);
            ov.LastRenderedLine = Math.Max(ov.LastRenderedLine, lastRenderedLine);
        }
        else
        {
            var matchLineNums = new HashSet<int>(ov.RemainingResults.Take(chunkSize).Select(r => r.LineNumber));
            int blocksAdded = 0;
            for (int ri = 0; ri < chunkSize; ri++)
            {
                if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                    break;

                var r = ov.RemainingResults[ri];
                var sep = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var sepRun = new Run { Text = $"{new string('\u2500', 6)}{label}{new string('\u2500', 6)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                section.Blocks.Add(sep);
                SyncGutterSpacer(section, sep.Margin);
                blocksAdded++;

                var lines = GetPreviewLines(r, ov.AllLines, ov.PreviewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                        break;

                    bool isMatchLine = matchLineNums.Contains(lineNum);
                    AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, ov.Rx, truncate: true,
                        lineNum == r.LineNumber ? _matchParagraphs : null, sn, out int addedParagraphs);
                    blocksAdded += addedParagraphs;
                }
                consumed++;
                if (_matchParagraphs.Count - beforeCount >= MaxMatchEntriesPerExpandChunk)
                    break;
            }
        }

        ov.RemainingResults.RemoveRange(0, consumed);

        int addedCount = _matchParagraphs.Count - beforeCount;
        if (addedCount > 0 && insertAt < beforeCount)
        {
            var newEntries = _matchParagraphs.GetRange(beforeCount, addedCount);
            _matchParagraphs.RemoveRange(beforeCount, addedCount);
            _matchParagraphs.InsertRange(insertAt, newEntries);
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

        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();

        LogService.Instance.Info("Preview", $"ExpandOverflowChunk(scroll): consumed={consumed}, addedEntries={addedCount}, renderedSoFar={ov.RenderedSoFar}, remaining={ov.RemainingResults.Count}");
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

        var filesToPrepend = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, results) in newFiles)
        {
            if (_pendingPreviewFilePaths.Contains(filePath))
            {
                LogService.Instance.Info("Preview", $"PrependPreviewSectionsForFilesAsync: skipping pending file '{filePath}'");
                continue;
            }

            if (PreviewSectionExists(filePath))
            {
                LogService.Instance.Info("Preview", $"PrependPreviewSectionsForFilesAsync: skipping existing file '{filePath}'");
                continue;
            }

            _pendingPreviewFilePaths.Add(filePath);
            filesToPrepend[filePath] = results;
        }

        if (filesToPrepend.Count == 0)
        {
            if (scrollToFile is not null && PreviewSectionExists(scrollToFile))
                TryScrollToPreviewSection(scrollToFile);
            return;
        }

        try
        {

        EnsurePreviewPanelVisible();
        EnsureSectionsSurface();
        PreviewToolbarContent.Visibility = Visibility.Visible;

        // Cap the initial render at PreviewSectionPageSize. Adding 10k+
        // Expanders to a flat StackPanel can crash the WinUI layout engine
        // (native fail-fast in CoreMessagingXP.dll). The remainder is paged
        // in via "Show more" using the same machinery as LoadMoreSectionsAsync.
        var orderedFiles = filesToPrepend.ToList();
        int totalRequested = orderedFiles.Count;
        int pageEnd = Math.Min(totalRequested, PreviewSectionPageSize);
        bool deferRemainder = pageEnd < totalRequested;
        if (deferRemainder)
            LogService.Instance.Info("Preview",
                $"PrependPreviewSectionsForFilesAsync: capping initial render at {pageEnd:N0}/{totalRequested:N0}; remainder deferred to 'Show more'.");

        bool showSpinner = pageEnd > PreviewSectionPageSize / 2 || deferRemainder;
        if (showSpinner)
            ShowProgressOverlay($"Adding {pageEnd:N0} of {totalRequested:N0} files\u2026", 0);

        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
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
                    await BuildHighlightSectionAsync(section, results, allLines, previewLines, rx);
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

        ReorderMatchParagraphsToPreviewSectionOrder();
        UnboxCurrentMatch();
        HideActiveMatchOverlay();
        InvalidatePendingMatchScrolls();
        _currentMatchIndex = -1;
        _initialMatchScrolled = false;

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
        _previewResult = filesToPrepend.Values.First().First();
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
        finally
        {
            foreach (var filePath in filesToPrepend.Keys)
                _pendingPreviewFilePaths.Remove(filePath);
        }
    }

    private void OnFileGroupExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is FileGroup g)
        {
            try
            {
                LogService.Instance.Info("Preview", $"OnFileGroupExpanding: expand only file='{g.FilePath}', matchCount={g.Count}");
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"OnFileGroupExpanding threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnFileGroupHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsControlKeyDown()
            || sender is not FrameworkElement header
            || header.DataContext is not FileGroup group
            || IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            return;
        }

        var point = e.GetCurrentPoint(header);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _ctrlFileHeaderGestureGroup = group;
        _ctrlFileHeaderGestureWasExpanded = group.IsExpanded;
        _ctrlFileHeaderGesturePointerId = e.Pointer.PointerId;
        e.Handled = true;
    }

    private async void OnFileGroupHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement header
            || header.DataContext is not FileGroup group
            || IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            return;
        }

        bool isTrackedCtrlHeaderClick = ReferenceEquals(group, _ctrlFileHeaderGestureGroup)
            && e.Pointer.PointerId == _ctrlFileHeaderGesturePointerId;
        if (!isTrackedCtrlHeaderClick)
            return;

        e.Handled = true;
        bool wasExpanded = _ctrlFileHeaderGestureWasExpanded;
        ClearCtrlFileHeaderGesture();
        await SelectFileGroupMatchesAndPreviewAsync(group, "ctrl click", preserveExpansionState: wasExpanded);
    }

    private async void OnFileGroupHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement header
            || header.DataContext is not FileGroup g
            || g.Count == 0)
        {
            return;
        }

        if (IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            LogService.Instance.Info("Preview", $"OnFileGroupHeaderTapped: command click ignored file='{g.FilePath}', isExpanded={g.IsExpanded}");
            return;
        }

        if (IsControlKeyDown())
        {
            e.Handled = true;
            if (WasCtrlFileHeaderPreviewJustHandled(g))
                return;

            bool wasExpanded = ReferenceEquals(g, _ctrlFileHeaderGestureGroup)
                ? _ctrlFileHeaderGestureWasExpanded
                : g.IsExpanded;
            ClearCtrlFileHeaderGesture();
            await SelectFileGroupMatchesAndPreviewAsync(g, "ctrl click", preserveExpansionState: wasExpanded);
            return;
        }

        if (g.IsExpanded)
        {
            LogService.Instance.Info("Preview", $"OnFileGroupHeaderTapped: collapse only file='{g.FilePath}', matchCount={g.Count}");
            return;
        }

        LogService.Instance.Info("Preview", $"OnFileGroupHeaderTapped: expand only file='{g.FilePath}', matchCount={g.Count}");
    }

    private async void OnFileGroupHeaderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement header
            || header.DataContext is not FileGroup g
            || g.Count == 0)
        {
            return;
        }

        if (IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))
        {
            LogService.Instance.Info("Preview", $"OnFileGroupHeaderDoubleTapped: command click ignored file='{g.FilePath}', isExpanded={g.IsExpanded}");
            return;
        }

        e.Handled = true;
        await SelectFileGroupMatchesAndPreviewAsync(g, "double click");
    }

    private async Task SelectFileGroupMatchesAndPreviewAsync(FileGroup group, string reason, bool? preserveExpansionState = null)
    {
        LogService.Instance.Info("Preview", $"SelectFileGroupMatchesAndPreviewAsync: reason='{reason}', file='{group.FilePath}', matchCount={group.Count}");

        if (preserveExpansionState.HasValue)
            group.IsExpanded = preserveExpansionState.Value;

        if (reason == "ctrl click")
            RecordCtrlFileHeaderPreview(group.FilePath);

        try
        {
            SelectFileGroupMatches(group);
            _initialMatchScrolled = false;

            var results = group.Where(r => r.IsSelected).ToList();
            if (results.Count > 0)
            {
                if (TryScrollToPreviewSection(group.FilePath))
                    return;
                var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase)
                {
                    [group.FilePath] = results
                };
                await PrependPreviewSectionsForFilesAsync(newFiles, group.FilePath);
            }
        }
        finally
        {
            if (preserveExpansionState.HasValue)
            {
                bool targetState = preserveExpansionState.Value;
                group.IsExpanded = targetState;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    group.IsExpanded = targetState;
                });
            }
        }
    }

    private void ClearCtrlFileHeaderGesture()
    {
        _ctrlFileHeaderGestureGroup = null;
        _ctrlFileHeaderGestureWasExpanded = false;
        _ctrlFileHeaderGesturePointerId = 0;
    }

    private void RecordCtrlFileHeaderPreview(string filePath)
    {
        _lastCtrlFileHeaderPreviewPath = filePath;
        _lastCtrlFileHeaderPreviewTick = Environment.TickCount64;
    }

    private bool WasCtrlFileHeaderPreviewJustHandled(FileGroup group)
    {
        if (_lastCtrlFileHeaderPreviewPath is null)
            return false;

        long elapsed = Environment.TickCount64 - _lastCtrlFileHeaderPreviewTick;
        return elapsed >= 0
            && elapsed < 750
            && string.Equals(_lastCtrlFileHeaderPreviewPath, group.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControlKeyDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsInsideHeaderCommand(DependencyObject? source, DependencyObject headerRoot)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, headerRoot); current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
                return true;
        }

        return false;
    }

    private void SelectFileGroupMatches(FileGroup group)
    {
        if (group.AllSelected && group.SelectedCount == group.Count)
            return;

        LogService.Instance.Info("Preview", $"SelectFileGroupMatches: file='{group.FilePath}', matchCount={group.Count}");
        _suppressPreviewUpdate = true;
        try
        {
            group.SelectAll();
        }
        finally
        {
            _suppressPreviewUpdate = false;
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

    private void OnMatchLineTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't trigger preview when user clicks the checkbox itself
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;

        if (sender is FrameworkElement { DataContext: SearchResult result })
        {
            LogService.Instance.Info("Preview",
                $"OnMatchLineTapped: no preview change file='{result.FilePath}', line={result.LineNumber}");
        }
    }

    private async void OnMatchLineCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: SearchResult result } checkBox)
        {
            if (checkBox.IsChecked is bool isChecked && result.IsSelected != isChecked)
                result.IsSelected = isChecked;

            UpdateSelectionForMatchLine(result, nameof(OnMatchLineCheckBoxClicked));

            if (result.IsSelected)
            {
                try
                {
                    await EnsureCheckedMatchInPreviewAsync(result);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Warning("Preview",
                        $"OnMatchLineCheckBoxClicked: failed to add checked line to preview for '{result.FilePath}' line {result.LineNumber}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                var selected = ViewModel.GetAllSelectedResults();
                if (selected.Count >= 2)
                    await UpdateMultiSelectPreviewAsync();
                else if (selected.Count == 1)
                    await ShowSingleFilePreviewAsync(selected[0], fullFile: false);
            }
        }
    }

    private void UpdateSelectionForMatchLine(SearchResult result, string caller)
    {
        FindParentGroup(result)?.NotifySelectionChanged();

        var selected = ViewModel.GetAllSelectedResults();
        LogService.Instance.Info("Preview", $"{caller}: selection only file='{result.FilePath}', line={result.LineNumber}, isSelected={result.IsSelected}, totalSelected={selected.Count}");
    }

    private async Task EnsureCheckedMatchInPreviewAsync(SearchResult result)
    {
        if (!result.IsSelected)
            return;

        ViewModel.HydrateResult(result);

        LogService.Instance.Info("Preview",
            $"EnsureCheckedMatchInPreviewAsync: file='{result.FilePath}', line={result.LineNumber}");

        if (!TryFindPreviewSection(result.FilePath, out var expander, out var section))
        {
            var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase)
            {
                [result.FilePath] = [result]
            };
            await PrependPreviewSectionsForFilesAsync(newFiles, result.FilePath);
            return;
        }

        EnsurePreviewPanelVisible();
        EnsureSectionsSurface();
        PreviewToolbarContent.Visibility = Visibility.Visible;
        expander.IsExpanded = true;
        await MaterializeLazySectionAsync(section);

        if (!TryFindPreviewMatchParagraph(section, result, out var paragraph, out var matchInPara))
        {
            await AppendCheckedMatchContextAsync(section, result);
            ReorderMatchParagraphsToPreviewSectionOrder();
            if (!TryFindPreviewMatchParagraph(section, result, out paragraph, out matchInPara))
            {
                LogService.Instance.Warning("Preview",
                    $"EnsureCheckedMatchInPreviewAsync: appended line but could not locate match paragraph for '{result.FilePath}' line {result.LineNumber}");
                TryScrollToPreviewSection(result.FilePath);
                return;
            }
        }

        SetCurrentMatchToMatch(section, paragraph, matchInPara);
        ScrollPreviewToLine(section, paragraph);
    }

    private async Task AppendCheckedMatchContextAsync(RichTextBlock section, SearchResult result)
    {
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);
        int previewLines = ViewModel.PreviewContextLines;
        string[]? allLines = null;

        try
        {
            allLines = await Task.Run(() => ReadAllLinesWithEncodingSync(result.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogService.Instance.Verbose("Preview",
                $"AppendCheckedMatchContextAsync: using stored context for '{result.FilePath}' line {result.LineNumber}: {ex.GetType().Name}: {ex.Message}");
        }

        bool isHighlight = ViewModel.PreviewModeIndex == 1;
        if (section.Blocks.Count > 0)
        {
            if (isHighlight)
            {
                AddGapIndicator(section);
            }
            else
            {
                var separator = new Paragraph();
                var separatorRun = new Run { Text = $"{new string('\u2500', 6)}\u00A0Line\u00A0{result.LineNumber}\u00A0{new string('\u2500', 6)}" };
                separatorRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                separator.Inlines.Add(separatorRun);
                separator.Margin = new Thickness(0, 8, 0, 4);
                section.Blocks.Add(separator);
                SyncGutterSpacer(section, separator.Margin);
            }
        }

        var lines = GetPreviewLines(result, allLines, previewLines, fullFile: false);
        _sectionMatchNavs.TryGetValue(section, out var sectionNav);
        foreach (var (line, lineNum) in lines)
        {
            bool isMatchLine = lineNum == result.LineNumber;
            AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, result, rx, truncate: true, _matchParagraphs, sectionNav, out _);
        }

        InvalidateParagraphIndexCache(section);
        if (sectionNav is not null)
            sectionNav.IndexByMatch = null;

        var totalFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        var (deferredFileCount, deferredMatchCount) = GetDeferredCounts();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount + deferredMatchCount;
        int grandFileCount = totalFiles + deferredFileCount;
        SetPreviewFileLabel(
            $"{totalMatches:N0} selected matches across {grandFileCount:N0} file(s)",
            string.Join(Environment.NewLine, GetExistingPreviewFilePaths()));
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
        UpdateExpandAllButtonVisibility();
    }

    private bool TryFindPreviewMatchParagraph(
        RichTextBlock section,
        SearchResult result,
        out Paragraph paragraph,
        out int matchInPara)
    {
        if (_sectionMatchNavs.TryGetValue(section, out var sectionNav))
        {
            foreach (var match in sectionNav.Matches)
            {
                if (TryGetPreviewParagraphLineNumber(match.para, out int lineNumber)
                    && lineNumber == result.LineNumber)
                {
                    paragraph = match.para;
                    matchInPara = match.matchInPara;
                    return true;
                }
            }
        }

        foreach (var match in _matchParagraphs)
        {
            if (ReferenceEquals(match.block, section)
                && TryGetPreviewParagraphLineNumber(match.para, out int lineNumber)
                && lineNumber == result.LineNumber)
            {
                paragraph = match.para;
                matchInPara = match.matchInPara;
                return true;
            }
        }

        paragraph = null!;
        matchInPara = 0;
        return false;
    }

    private static bool TryGetPreviewParagraphLineNumber(Paragraph paragraph, out int lineNumber)
    {
        lineNumber = 0;
        var gutter = paragraph.Inlines.OfType<Run>().Skip(1).FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(gutter))
            return false;

        var text = gutter.AsSpan().Trim();
        int spaceIndex = text.IndexOf(' ');
        if (spaceIndex >= 0)
            text = text[..spaceIndex];

        return int.TryParse(text, out lineNumber);
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

    private async void OnLayoutOptionClicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, LayoutConcatenated))
            ViewModel.PreviewModeIndex = 0;
        else if (ReferenceEquals(sender, LayoutMultiHighlight))
            ViewModel.PreviewModeIndex = 1;

        SyncLayoutToggles(ViewModel.PreviewModeIndex);
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
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
        ApplyPreviewEditorWordWrap(_previewEditorForcedWrap || wrap);
        foreach (var block in EnumeratePreviewSectionBlocks())
        {
            if (block.TextWrapping != wrapping)
                block.TextWrapping = wrapping;
        }
        if (PreviewSectionsPanel.Visibility != Visibility.Visible)
            PreviewScrollViewer.HorizontalScrollBarVisibility = hbar;
        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (expander.Content is Grid g && g.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv)
                sv.HorizontalScrollBarVisibility = hbar;
            else if (expander.Content is ScrollViewer sv2)
                sv2.HorizontalScrollBarVisibility = hbar;
        }
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
            ApplyPreviewEditorWordWrap(_previewEditorForcedWrap || wrap);
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
                if (expander.Content is Grid g && g.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv)
                    sv.HorizontalScrollBarVisibility = hbar;
                else if (expander.Content is ScrollViewer sv2)
                    sv2.HorizontalScrollBarVisibility = hbar;

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

    private Task<object?> DispatchIdleAsync()
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult(null)))
            tcs.TrySetResult(null);
        return tcs.Task;
    }

    private async void RefreshCurrentPreview(bool preserveScroll = false)
    {
        LogService.Instance.Verbose("Preview", $"RefreshCurrentPreview called preserveScroll={preserveScroll}");
        if (PreviewEditor.Visibility == Visibility.Visible) return;

        double restoreHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
        double restoreVerticalOffset = PreviewScrollViewer.VerticalOffset;
        int restoreMatchIndex = preserveScroll ? _currentMatchIndex : -1;
        bool previousSuppressInitialMatchAutoScroll = _suppressInitialMatchAutoScroll;
        if (preserveScroll)
            _suppressInitialMatchAutoScroll = true;

        try
        {
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
        finally
        {
            if (preserveScroll)
            {
                _suppressInitialMatchAutoScroll = previousSuppressInitialMatchAutoScroll;
                RestorePreviewScrollOffset(restoreHorizontalOffset, restoreVerticalOffset);
                RestoreActiveMatchAfterPreviewRefresh(restoreMatchIndex);
            }
        }
    }

    private void RestorePreviewScrollOffset(double horizontalOffset, double verticalOffset)
    {
        if (double.IsNaN(horizontalOffset) || double.IsInfinity(horizontalOffset)
            || double.IsNaN(verticalOffset) || double.IsInfinity(verticalOffset))
        {
            return;
        }

        void ApplyRestore()
        {
            if (PreviewScrollViewer.Visibility != Visibility.Visible)
                return;

            double targetX = Math.Clamp(horizontalOffset, 0, Math.Max(0, PreviewScrollViewer.ScrollableWidth));
            double targetY = Math.Clamp(verticalOffset, 0, Math.Max(0, PreviewScrollViewer.ScrollableHeight));
            PreviewScrollViewer.ChangeView(targetX, targetY, null, disableAnimation: true);
            UpdateStickyFileHeader();
            QueueActiveMatchOverlayRefresh();
        }

        ApplyRestore();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyRestore);
    }

    private void RestoreActiveMatchAfterPreviewRefresh(int matchIndex)
    {
        if (matchIndex < 0 || _matchParagraphs.Count == 0)
            return;

        _currentMatchIndex = Math.Clamp(matchIndex, 0, _matchParagraphs.Count - 1);
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        BoxMatchRun(para, matchInPara);
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        QueueActiveMatchOverlayRefresh();
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

        // Reset select-all checkbox for the new search.
        SelectAllFilesCheckBox.IsChecked = false;

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

    private async void OnClearResults(object sender, RoutedEventArgs e)
    {
        // Clear preview pane
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

        // Clear results, dispose temp store, and GC
        await ViewModel.ClearResultsAsync();
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

    private void OnGroupModeNone(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.None; }
    private void OnGroupFolderAZ(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Folder; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupFolderZA(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Folder; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupDateModifiedRecent(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModified; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupDateModifiedOlder(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModified; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupDateCreatedRecent(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeCreated; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupDateCreatedOlder(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeCreated; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupDateModCreatedRecent(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModifiedCreated; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupDateModCreatedOlder(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.DateRangeModifiedCreated; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupExtensionAZ(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Extension; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupExtensionZA(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.Extension; ViewModel.GroupSortDirectionIndex = 1; }
    private void OnGroupFileSizeSmallLarge(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.FileSize; ViewModel.GroupSortDirectionIndex = 0; }
    private void OnGroupFileSizeLargeSmall(object sender, RoutedEventArgs e) { ViewModel.GroupModeIndex = (int)GroupMode.FileSize; ViewModel.GroupSortDirectionIndex = 1; }

    // ── Sort menu handlers ────────────────────────────────────────

    private void OnSortNone(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 0; }
    private void OnSortMatchesDesc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 1; ViewModel.SortDirectionIndex = 0; }
    private void OnSortMatchesAsc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 1; ViewModel.SortDirectionIndex = 1; }
    private void OnSortDateModifiedDesc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 2; ViewModel.SortDirectionIndex = 0; }
    private void OnSortDateModifiedAsc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 2; ViewModel.SortDirectionIndex = 1; }
    private void OnSortFileSizeDesc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 3; ViewModel.SortDirectionIndex = 0; }
    private void OnSortFileSizeAsc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 3; ViewModel.SortDirectionIndex = 1; }
    private void OnSortFileNameDesc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 4; ViewModel.SortDirectionIndex = 0; }
    private void OnSortFileNameAsc(object sender, RoutedEventArgs e) { ViewModel.SortModeIndex = 4; ViewModel.SortDirectionIndex = 1; }

    // ── Date filter menu handlers ─────────────────────────────────

    private void OnDateFilterNone(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.None;
    private void OnDateFilterPastDay(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastDay;
    private void OnDateFilterPastWeek(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastWeek;
    private void OnDateFilterPastTwoWeeks(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastTwoWeeks;
    private void OnDateFilterPastMonth(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastMonth;
    private void OnDateFilterPastThreeMonths(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastThreeMonths;
    private void OnDateFilterPastSixMonths(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastSixMonths;
    private void OnDateFilterPastNineMonths(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastNineMonths;
    private void OnDateFilterPastYear(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastYear;
    private void OnDateFilterPastTwoYears(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastTwoYears;
    private void OnDateFilterPastThreeYears(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastThreeYears;
    private void OnDateFilterPastFiveYears(object sender, RoutedEventArgs e) => ViewModel.DateRangeFilterIndex = (int)DateRangeFilter.PastFiveYears;

    private void OnMatchLineLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBlock rtb) return;
        var dc = rtb.DataContext;
        if (dc is not SearchResult r) return;

        rtb.Blocks.Clear();
        var para = new Paragraph();
        int matchStart = r.IsEvicted ? r.ShortPreviewMatchStart : r.MatchStartColumn;
        HighlightInline(para, r.MatchLine, matchStart, r.MatchLength);
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

    private async void OnFileGroupCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not FileGroup group)
        {
            // DataContext not set (e.g. during container recycling) — revert checkbox to unchecked.
            if (sender is CheckBox orphan)
                orphan.IsChecked = false;
            return;
        }

        bool shouldSelect = checkBox.IsChecked == true;
        int currentIndex = ViewModel.ResultGroups.IndexOf(group);
        var groupsToPreview = new List<FileGroup>();
        bool isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                       .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        LogService.Instance.Info("Preview",
            $"OnFileGroupCheckBoxClicked: file='{group.FilePath}', shouldSelect={shouldSelect}, matchCount={group.Count}, index={currentIndex}");

        _suppressPreviewUpdate = true;
        try
        {
            if (isShift && currentIndex >= 0)
            {
                for (int i = 0; i <= currentIndex; i++)
                {
                    if (shouldSelect)
                    {
                        ViewModel.ResultGroups[i].SelectAll();
                        groupsToPreview.Add(ViewModel.ResultGroups[i]);
                    }
                    else
                    {
                        ViewModel.ResultGroups[i].DeselectAll();
                    }
                }
            }
            else if (shouldSelect)
            {
                group.SelectAll();
                groupsToPreview.Add(group);
            }
            else
            {
                group.DeselectAll();
            }
        }
        finally
        {
            _suppressPreviewUpdate = false;
        }

        // Force-sync checkbox to model to prevent divergence from OneWay binding.
        checkBox.IsChecked = group.AllSelected;

        _lastCheckedGroupIndex = currentIndex;

        if (groupsToPreview.Count > 0)
        {
            try
            {
                await EnsureFileGroupsInPreviewAsync(groupsToPreview, group.FilePath);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("Preview",
                    $"OnFileGroupCheckBoxClicked: failed to add checked file(s) to preview: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task EnsureFileGroupsInPreviewAsync(IReadOnlyList<FileGroup> groups, string scrollToFile)
    {
        var newFiles = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileGroup in groups)
        {
            if (PreviewSectionExists(fileGroup.FilePath))
                continue;

            var selectedResults = fileGroup.Where(result => result.IsSelected).ToList();
            if (selectedResults.Count > 0)
                newFiles[fileGroup.FilePath] = selectedResults;
        }

        if (newFiles.Count > 0)
        {
            await PrependPreviewSectionsForFilesAsync(newFiles, scrollToFile);
        }
        else if (PreviewSectionExists(scrollToFile))
        {
            TryScrollToPreviewSection(scrollToFile);
        }
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
            var contextGroup = GetFileHeaderContextGroup(flyout)
                ?? flyout.Items.OfType<MenuFlyoutItem>()
                    .Select(GetFileHeaderContextGroup)
                    .FirstOrDefault(g => g is not null);

            // First item: "Preview <filename>"
            if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem singleItem)
            {
                string fileName = contextGroup is not null ? System.IO.Path.GetFileName(contextGroup.FilePath) : "";
                singleItem.Text = $"Preview {fileName}";
                singleItem.Tag = contextGroup;
            }

            // Second item: "Preview all selected (x)" — hidden when ≤1 checked
            int checkedCount = GetCheckedFileGroups().Count;
            if (flyout.Items.Count > 1 && flyout.Items[1] is MenuFlyoutItem previewAllItem)
            {
                previewAllItem.Text = $"Preview all selected ({checkedCount})";
                previewAllItem.Tag = contextGroup;
                previewAllItem.Visibility = checkedCount > 1
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
            }

            // Update copy/save items: plural only when >1 file checked
            int count = checkedCount > 0 ? checkedCount : 1; // at least the right-clicked file
            bool plural = count > 1;
            // Items layout: [0]=Preview, [1]=PreviewAll, [2]=Sep, [3]=CopyPath, [4]=Sep, [5]=CopyPaths, [6]=CopyWithContent, [7]=Sep, [8]=SavePaths, [9]=SaveWithContent
            foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
            {
                if (item.Text.StartsWith("Copy Selected File Path", StringComparison.Ordinal))
                    item.Text = plural ? "Copy Selected File Paths" : "Copy Selected File Path";
                else if (item.Text.StartsWith("Copy Selected File", StringComparison.Ordinal))
                    item.Text = plural ? "Copy Selected Files With Content" : "Copy Selected File With Content";
                else if (item.Text.StartsWith("Save Selected File Path", StringComparison.Ordinal))
                    item.Text = plural ? "Save Selected File Paths\u2026" : "Save Selected File Path\u2026";
                else if (item.Text.StartsWith("Save Selected File", StringComparison.Ordinal))
                    item.Text = plural ? "Save Selected Files With Content\u2026" : "Save Selected File With Content\u2026";
            }
        }
    }

    private void OnResultsContextMenuOpening(object sender, object e)
    {
        var checkedGroups = GetCheckedFileGroups();
        var contextGroup = checkedGroups.Count == 0 ? GetRecentResultsContextMenuGroup() : null;
        int checkedCount = checkedGroups.Count;

        // "Preview <filename>" — always visible, shows right-clicked file name
        string fileName = contextGroup is not null
            ? System.IO.Path.GetFileName(contextGroup.FilePath)
            : checkedCount == 1
                ? System.IO.Path.GetFileName(checkedGroups[0].FilePath)
                : "";
        CtxPreviewSingle.Text = $"Preview {fileName}";
        CtxPreviewSingle.Tag = contextGroup ?? (checkedCount == 1 ? checkedGroups[0] : null);
        CtxPreviewSingle.Visibility = !string.IsNullOrEmpty(fileName)
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        // "Preview all selected (x)" — only when >1 checked
        CtxPreviewSelected.Text = $"Preview all selected ({checkedCount})";
        CtxPreviewSelected.Tag = contextGroup;
        CtxPreviewSelected.Visibility = checkedCount > 1
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        int count = checkedCount > 0 ? checkedCount : contextGroup is null ? 0 : 1;
        bool plural = count > 1;
        CtxCopyPaths.Text = plural ? "Copy File Paths" : "Copy File Path";
        CtxCopyWithContent.Text = plural ? "Copy Files With Content" : "Copy File With Content";
        CtxSavePaths.Text = plural ? "Save File Paths\u2026" : "Save File Path\u2026";
        CtxSaveWithContent.Text = plural ? "Save Files With Content\u2026" : "Save File With Content\u2026";
    }

    private void OnResultsListPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ResultsList);
        if (!point.Properties.IsRightButtonPressed)
            return;

        CaptureResultsContextMenuGroup(e.OriginalSource as DependencyObject);
    }

    private void OnResultsListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CaptureResultsContextMenuGroup(e.OriginalSource as DependencyObject);
    }

    private void CaptureResultsContextMenuGroup(DependencyObject? source)
    {
        _lastResultsContextMenuGroup = FindContextFileGroup(source);
        _lastResultsContextMenuTick = Environment.TickCount64;
    }

    private FileGroup? GetRecentResultsContextMenuGroup()
    {
        long elapsed = Environment.TickCount64 - _lastResultsContextMenuTick;
        return elapsed is >= 0 and < 2000 ? _lastResultsContextMenuGroup : null;
    }

    private async void OnPreviewSingleFile(object sender, RoutedEventArgs e)
    {
        FileGroup? group = null;
        if (sender is FrameworkElement fe && fe.Tag is FileGroup tagGroup)
            group = tagGroup;
        else if (sender is FrameworkElement fe2 && fe2.Tag is string filePath)
            group = FindFileGroup(filePath);

        if (group is null)
        {
            group = GetRecentResultsContextMenuGroup();
        }

        if (group is null) return;

        LogService.Instance.Info("Preview", $"OnPreviewSingleFile: {System.IO.Path.GetFileName(group.FilePath)}");
        _suppressPreviewUpdate = true;
        try
        {
            group.SelectAll();
            var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in group)
            {
                if (!r.IsSelected) continue;
                if (!byFile.TryGetValue(r.FilePath, out var list))
                {
                    list = new List<SearchResult>();
                    byFile[r.FilePath] = list;
                }
                list.Add(r);
            }
            if (byFile.Count == 0) return;

            var existing = GetExistingPreviewFilePaths();
            if (existing.Contains(group.FilePath))
            {
                TryScrollToPreviewSection(group.FilePath);
            }
            else
            {
                await PrependPreviewSectionsForFilesAsync(byFile, byFile.Keys.First());
            }
        }
        finally { _suppressPreviewUpdate = false; }
    }

    private async void OnPreviewSelectedFiles(object sender, RoutedEventArgs e)
    {
        var selectedGroups = GetPreviewFileGroups(sender);
        var groupNames = selectedGroups.Select(g => g.FilePath).ToList();
        LogService.Instance.Info("Preview", $"OnPreviewSelectedFiles: {groupNames.Count} groups selected: [{string.Join(", ", groupNames.Select(System.IO.Path.GetFileName))}]");
        _suppressPreviewUpdate = true;
        try
        {
            // Select all match results within each checked FileGroup.
            foreach (var g in selectedGroups)
                g.SelectAll();

            // Gather results only from the checked groups.
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

    private List<FileGroup> GetPreviewFileGroups(object sender)
    {
        var checkedGroups = GetCheckedFileGroups();
        if (checkedGroups.Count > 0)
            return checkedGroups;

        var contextGroup = GetFileHeaderContextGroup(sender);
        return contextGroup is null ? checkedGroups : [contextGroup];
    }

    private FileGroup? GetFileHeaderContextGroup(object? sender)
    {
        if (sender is MenuFlyout { Target: FrameworkElement target })
        {
            if (target.DataContext is FileGroup targetGroup)
                return targetGroup;

            var taggedTargetGroup = GetFileHeaderContextGroup(target);
            if (taggedTargetGroup is not null)
                return taggedTargetGroup;
        }

        if (sender is FrameworkElement element)
        {
            if (element.Tag is FileGroup taggedGroup)
                return taggedGroup;

            if (element.Tag is string filePath)
                return FindFileGroup(filePath);

            if (element.DataContext is FileGroup dataContextGroup)
                return dataContextGroup;
        }

        return null;
    }

    private FileGroup? FindContextFileGroup(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement element)
                continue;

            if (element.Tag is FileGroup taggedGroup)
                return taggedGroup;

            if (element.Tag is string filePath)
            {
                var taggedPathGroup = FindFileGroup(filePath);
                if (taggedPathGroup is not null)
                    return taggedPathGroup;
            }

            if (element.DataContext is FileGroup dataContextGroup)
                return dataContextGroup;

            if (element.DataContext is SearchResult result)
            {
                var parentGroup = FindParentGroup(result);
                if (parentGroup is not null)
                    return parentGroup;
            }
        }

        return null;
    }

    private FileGroup? FindFileGroup(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        return ViewModel.ResultGroups.FirstOrDefault(g =>
            string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
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

    private async void OnExportHtmlReport(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0) return;
        var groups = selected
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HtmlReportExportService.FileMatchGroup(g.Key, Path.GetFileName(g.Key), g.ToList()))
            .ToList();
        if (groups.Count == 0) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("HTML File", new List<string> { ".html" });
        picker.SuggestedFileName = "Yagu_Report";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // Hydrate all results before writing
        foreach (var group in groups)
            foreach (var r in group.Results)
                ViewModel.HydrateResult(r);

        int totalMatches = groups.Sum(g => g.Results.Count);

        await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        using var w = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: false);

        var stats = new HtmlReportExportService.SearchStats(
            ViewModel.SearchStartedUtc,
            ViewModel.LastSearchElapsed,
            ViewModel.FilesScanned,
            ViewModel.BytesScanned);

        await HtmlReportExportService.WriteMultiFileReportAsync(w, ViewModel.Query, groups, stats).ConfigureAwait(false);

        DispatcherQueue.TryEnqueue(() =>
            ViewModel.StatusText = $"Exported HTML report ({groups.Count:N0} files, {totalMatches:N0} matches) to {file.Path}");
    }

    private static string BuildHighlightedMatchHtml(string line, int matchStart, int matchLength)
        => HtmlReportExportService.BuildHighlightedMatchHtml(line, matchStart, matchLength);

    private void AttachPreviewBlockContextFlyout(RichTextBlock block)
    {
        var flyout = new MenuFlyout();

        var copyWithLines = new MenuFlyoutItem { Text = "Copy (with line numbers)", Icon = new SymbolIcon(Symbol.Copy) };
        copyWithLines.Click += (_, _) => CopyPreviewSelection(block, withLineNumbers: true);
        flyout.Items.Add(copyWithLines);

        var copyWithout = new MenuFlyoutItem { Text = "Copy (without line numbers)", Icon = new SymbolIcon(Symbol.Copy), KeyboardAcceleratorTextOverride = "Ctrl+C" };
        copyWithout.Click += (_, _) => CopyPreviewSelection(block, withLineNumbers: false);
        flyout.Items.Add(copyWithout);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var wrapItem = new ToggleMenuFlyoutItem { Text = "Wrap", Icon = new FontIcon { Glyph = "\uE8B3" } };
        wrapItem.IsChecked = ViewModel.PreviewWordWrap;
        wrapItem.Click += (_, _) =>
        {
            ViewModel.PreviewWordWrap = !ViewModel.PreviewWordWrap;
            WordWrapToggle.IsChecked = ViewModel.PreviewWordWrap;
            OnWordWrapToggled(WordWrapToggle, new RoutedEventArgs());
        };
        flyout.Items.Add(wrapItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var exportFileItem = new MenuFlyoutItem { Text = "Export file as HTML report", Icon = new FontIcon { Glyph = "\uE9F9" } };
        exportFileItem.Click += async (_, _) =>
        {
            var filePath = block.Tag as string;
            if (string.IsNullOrEmpty(filePath)) return;
            await ExportSingleFileHtmlReportAsync(filePath);
        };
        flyout.Items.Add(exportFileItem);

        flyout.Opening += (_, _) =>
        {
            wrapItem.IsChecked = ViewModel.PreviewWordWrap;
            bool hasSelection = !string.IsNullOrEmpty(block.SelectedText);
            copyWithLines.IsEnabled = hasSelection;
            copyWithout.IsEnabled = hasSelection;
        };
        block.ContextFlyout = flyout;
    }

    private static void CopyPreviewSelection(RichTextBlock block, bool withLineNumbers)
    {
        string selectedText = block.SelectedText;
        if (string.IsNullOrEmpty(selectedText)) return;

        string textToCopy;
        if (withLineNumbers)
        {
            // The selected text already contains gutter line numbers from the paragraph Runs.
            // Just copy it as-is.
            textToCopy = selectedText;
        }
        else
        {
            // Strip the gutter prefix: pattern is  "│NN │ " or " NN (cont) │ " at start of each line.
            // The gutter format is: indicator char + padded number + optional " (cont)" + " │ "
            var lines = selectedText.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                // Find the gutter separator "│ " and strip everything up to and including it
                int sepIdx = line.IndexOf("│ ", StringComparison.Ordinal);
                if (sepIdx >= 0)
                {
                    // There may be two separators (indicator│ and gutter│), take the last one
                    int secondSep = line.IndexOf("│ ", sepIdx + 2, StringComparison.Ordinal);
                    if (secondSep >= 0)
                        line = line[(secondSep + 2)..];
                    else
                        line = line[(sepIdx + 2)..];
                }
                if (i < lines.Length - 1)
                    sb.AppendLine(line);
                else
                    sb.Append(line);
            }
            textToCopy = sb.ToString();
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(textToCopy);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    private static int GetParagraphLineNumber(RichTextBlock block, Microsoft.UI.Xaml.Documents.TextPointer? pointer)
    {
        if (pointer is null) return 1;
        // Walk paragraphs to find which one the selection starts in,
        // using ConditionalWeakTable set during preview line construction.
        var paragraphs = block.Blocks.OfType<Paragraph>().ToList();
        int lastKnownLine = 1;
        foreach (var para in paragraphs)
        {
            if (s_paragraphLineNumbers.TryGetValue(para, out var tag) && tag is int lineNum)
                lastKnownLine = lineNum;
            var paraStart = para.ContentStart;
            var paraEnd = para.ContentEnd;
            if (paraStart is not null && paraEnd is not null
                && pointer.Offset >= paraStart.Offset && pointer.Offset <= paraEnd.Offset)
                return lastKnownLine;
        }
        return lastKnownLine;
    }

    private async Task ExportSingleFileHtmlReportAsync(string filePath)
    {
        var group = ViewModel.ResultGroups.FirstOrDefault(g =>
            string.Equals(g.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (group is null || group.Count == 0) return;

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("HTML File", new List<string> { ".html" });
        picker.SuggestedFileName = $"Yagu_Report_{Path.GetFileNameWithoutExtension(filePath)}";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // Hydrate all results before writing
        foreach (var result in group)
            ViewModel.HydrateResult(result);

        int totalMatches = group.Count;

        await using var stream = await file.OpenStreamForWriteAsync().ConfigureAwait(true);
        stream.SetLength(0);
        using var w = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: false);

        var fileGroup = new HtmlReportExportService.FileMatchGroup(group.FilePath, group.FileName, group.ToList());
        await HtmlReportExportService.WriteSingleFileReportAsync(w, ViewModel.Query, fileGroup).ConfigureAwait(false);

        ViewModel.StatusText = $"Exported HTML report ({totalMatches:N0} matches) for {Path.GetFileName(filePath)} to {file.Path}";
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
        LogService.Instance.Info("Preview", $"UpdateMultiSelectPreviewAsync: scrollTarget='{scrollTarget?.FilePath}', scrollToTop={scrollToTop}");
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
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);

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

            int renderedResults = 0;
            int startingBlocks = section.Blocks.Count;
            int cap = Math.Min(results.Count, MaxMatchesPerSection);
            var matchLineNums = new HashSet<int>(results.Select(r => r.LineNumber));
            foreach (var r in results)
            {
                if (renderedResults >= cap || section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                    break;

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
                SyncGutterSpacer(section, sep.Margin);

                var lines = GetPreviewLines(r, allLines, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                        break;

                    bool isMatchLine = matchLineNums.Contains(lineNum);
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    var firstPara = AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: true,
                        lineNum == r.LineNumber ? _matchParagraphs : null, sn, out int addedParagraphs);
                    parasInFile += addedParagraphs;

                    if (scrollTarget is not null && lineNum == r.LineNumber
                        && r.LineNumber == scrollTarget.LineNumber
                        && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        scrollBlock = section;
                        scrollPara = firstPara;
                    }
                }

                renderedResults++;
            }

            if (results.Count > renderedResults)
            {
                var notice = AppendTruncationNotice(section, results.Count, renderedResults);
                RegisterSectionOverflow(section,
                    filePath: filePath,
                    remainingResults: results.GetRange(renderedResults, results.Count - renderedResults),
                    allLines: allLines,
                    previewLines: previewLines,
                    rx: rx,
                    originalTotal: results.Count,
                    renderedSoFar: renderedResults,
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
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex, ViewModel.ExactMatch);

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
            int startingBlocks = section.Blocks.Count;

            fileContents.TryGetValue(filePath, out string[]? allLines);

            bool initiallyCapped = results.Count > MaxMatchesPerSection;
            var cappedResults = initiallyCapped ? results.GetRange(0, MaxMatchesPerSection) : results;
            int previewLines = ViewModel.PreviewContextLines;
            int lastRenderedLine1 = 0;

            if (allLines != null)
            {
                // Compute line ranges to display (union of all match windows)
                var ranges = new List<(int start, int end)>();
                foreach (var lineNum in cappedResults.Select(r => r.LineNumber).Distinct().OrderBy(n => n))
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
                var matchByLine = BuildMatchByLineForRanges(results, merged);

                bool firstRange = true;
                foreach (var (start, end) in merged)
                {
                    if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                        break;

                    if (!firstRange)
                    {
                        AddGapIndicator(section);
                    }
                    firstRange = false;

                    for (int i = start; i <= end; i++)
                    {
                        if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                            break;

                        int lineNum = i + 1;
                        bool isMatchLine = matchByLine.TryGetValue(lineNum, out var matchResult);
                        matchResult ??= cappedResults[0];
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        var firstPara = AddPreviewLineParagraphs(section, allLines[i], lineNum, isMatchLine, matchResult, rx, truncate: true, _matchParagraphs, sn, out _);
                        lastRenderedLine1 = lineNum;

                        if (scrollTarget is not null && isMatchLine && lineNum == scrollTarget.LineNumber
                            && string.Equals(filePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = firstPara;
                        }
                    }
                }
            }
            else
            {
                // Fallback: concatenated style
                var fallbackMatchLines = new HashSet<int>(cappedResults.Select(r => r.LineNumber));
                int fallbackIndex = 0;
                foreach (var r in cappedResults)
                {
                    if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                        break;

                    var lines = GetPreviewLines(r, null, ViewModel.PreviewContextLines, fullFile: false);
                    foreach (var (line, lineNum) in lines)
                    {
                        if (section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection)
                            break;

                        bool isMatchLine = fallbackMatchLines.Contains(lineNum);
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        var firstPara = AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, rx, truncate: true,
                            lineNum == r.LineNumber ? _matchParagraphs : null, sn, out _);

                        if (scrollTarget is not null && lineNum == r.LineNumber
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

            int renderedCount = allLines != null
                ? CountPrefixResultsThroughLine(results, lastRenderedLine1, allLines.Length)
                : Math.Min(results.Count, _matchParagraphs.Count - sectionMatchStart);
            int remainingBlockBudget = Math.Max(0, MaxPreviewBlocksPerSection - (section.Blocks.Count - startingBlocks));
            if (allLines != null && remainingBlockBudget > 0 && renderedCount < Math.Min(MaxMatchesPerSection, results.Count))
            {
                _sectionMatchNavs.TryGetValue(section, out var sn);
                var pending = results.Skip(renderedCount).ToList();
                AppendHighlightMatchWindows(
                    section,
                    pending,
                    allLines,
                    rx,
                    sn,
                    ViewModel.PreviewContextLines,
                    MaxMatchesPerSection - renderedCount,
                    MaxMatchesPerSection - renderedCount,
                    MaxPreviewBlocksPerSection,
                    out int consumed,
                    out _,
                    out int appendLastRenderedLine,
                    lastRenderedLine1);
                lastRenderedLine1 = Math.Max(lastRenderedLine1, appendLastRenderedLine);
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


    private bool _splitterDragging;
    private double _splitterStartX;
    private double _col0StartWidth;
    private double _col2StartWidth;

    private void OnAdvancedOptionsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        ChevronRotate.Angle = 0;
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    private void OnAdvancedOptionsCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        ChevronRotate.Angle = -90;
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    /// <summary>
    /// Forces the root grid to re-measure on every frame during the Expander
    /// expand/collapse animation so the split pane resizes in perfect sync.
    /// </summary>
    private void ListenForExpanderLayoutSync()
    {
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(400);
        debounce.IsRepeating = false;

        void handler(object? s, object? e)
        {
            AdvancedOptionsExpander.InvalidateMeasure();
            RootGrid.UpdateLayout();
        }

        debounce.Tick += (t, a) =>
        {
            debounce.Stop();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
        };

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        debounce.Start();
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
            if (await CheckHddAndWarnAsync())
                await ViewModel.StartSearchAsync();
        }
        else
        {
            FocusSearchBox();
        }
    }

    private void FocusSearchBox(bool suppressSuggestions = false)
    {
        if (suppressSuggestions)
            SuppressQuerySuggestionsFor(1000);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (suppressSuggestions)
                SuppressQuerySuggestionsFor(1000);

            QueryBox.Focus(FocusState.Programmatic);

            if (suppressSuggestions)
            {
                QueryBox.IsSuggestionListOpen = false;
                DispatcherQueue.TryEnqueue(() => QueryBox.IsSuggestionListOpen = false);
            }
        });
    }

    private async Task CheckEverythingAsync()
    {
        var esPath = FileLister.FindEsExe();
        bool everythingRunning = Process.GetProcessesByName("Everything").Length > 0;
        LogService.Instance.Info("MainWindow", $"CheckEverythingAsync: esPath={esPath ?? "(null)"}, everythingRunning={everythingRunning}");

        // Everything is running — SDK will work regardless of es.exe presence
        if (everythingRunning)
        {
            LogService.Instance.Info("MainWindow", "CheckEverythingAsync: Everything process is running — SDK will work, no action needed");
            return;
        }

        // es.exe found but Everything service not running — offer to start it
        if (esPath != null)
        {
            var everythingExe = FindEverythingExe(esPath);
            LogService.Instance.Info("MainWindow", $"CheckEverythingAsync: es.exe found at '{esPath}', Everything.exe resolve={everythingExe ?? "(null)"}");
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

        // Check if Everything.exe exists in standard locations even without es.exe
        var everythingExeStandalone = FindEverythingExeStandalone();
        if (everythingExeStandalone != null)
        {
            LogService.Instance.Info("MainWindow", $"CheckEverythingAsync: Everything.exe found at '{everythingExeStandalone}' (no es.exe), offering to start");
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
                        FileName = everythingExeStandalone,
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

        // Nothing found — offer to download and install
        LogService.Instance.Warning("MainWindow", "CheckEverythingAsync: Everything not found anywhere — showing install dialog");
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
            if (File.Exists(candidate))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExe: found at {candidate}");
                return candidate;
            }
        }
        // Check standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExe: found at {path}");
                return path;
            }
        }
        LogService.Instance.Warning("MainWindow", $"FindEverythingExe: NOT FOUND (esPath was '{esPath}', dir was '{dir}')");
        return null;
    }

    private static string? FindEverythingExeStandalone()
    {
        // Check registry install dirs for Everything.exe even when es.exe wasn't found
        foreach (var installDir in FileLister.GetEverythingInstallDirsFromRegistry())
        {
            var candidate = Path.Combine(installDir, "Everything.exe");
            if (File.Exists(candidate))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExeStandalone: found via registry at {candidate}");
                return candidate;
            }
        }
        // Standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path))
            {
                LogService.Instance.Info("MainWindow", $"FindEverythingExeStandalone: found at {path}");
                return path;
            }
        }
        LogService.Instance.Info("MainWindow", "FindEverythingExeStandalone: Everything.exe not found in any standard location");
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
    private bool _resultsPaneCollapsed;

    private void OnToggleResultsPane(object sender, RoutedEventArgs e)
    {
        _resultsPaneCollapsed = !_resultsPaneCollapsed;

        if (_resultsPaneCollapsed)
        {
            SplitPaneRow.Height = new GridLength(0);
            ProgressRow.Height = new GridLength(0);
            SplitPaneGrid.Visibility = Visibility.Collapsed;
            CollapseChevronIcon.Glyph = "\uE70E"; // ChevronUp
        }
        else
        {
            SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
            ProgressRow.Height = GridLength.Auto;
            SplitPaneGrid.Visibility = Visibility.Visible;
            CollapseChevronIcon.Glyph = "\uE70D"; // ChevronDown
        }

        UpdateBottomStatusBarVisibility();
    }

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

    private int _findIndex = -1; // last match start index in the editor text

    private void OnRootGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.F1)
        {
            e.Handled = true;
            OpenHelpWindow();
        }
        else if (e.Key == Windows.System.VirtualKey.S && ctrl && shift)
        {
            e.Handled = true;
            _ = CopyWindowScreenshotToClipboardAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.F && ctrl)
        {
            e.Handled = true;
            OpenFindBar(showReplace: false);
        }
        else if (e.Key == Windows.System.VirtualKey.H && ctrl)
        {
            e.Handled = true;
            OpenFindBar(showReplace: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter
            && !ctrl
            && TryHandlePreviewMatchEnter(e.OriginalSource as DependencyObject, shift))
        {
            e.Handled = true;
        }
    }

    private bool TryHandlePreviewMatchEnter(DependencyObject? source, bool shift)
    {
        if (source is null)
            return false;
        if (!HasNavigablePreviewMatchSurface())
            return false;
        if (!IsElementWithin(source, PreviewScrollViewer))
            return false;
        if (IsPreviewEnterReservedByFocusedControl(source))
            return false;

        if (shift)
            OnPrevMatch(PrevMatchButton, new RoutedEventArgs());
        else
            OnNextMatch(NextMatchButton, new RoutedEventArgs());
        return true;
    }

    private bool HasNavigablePreviewMatchSurface()
    {
        if (PreviewPanelBorder.Visibility != Visibility.Visible)
            return false;
        if (PreviewScrollViewer.Visibility != Visibility.Visible)
            return false;
        if (MatchNavPanel.Visibility != Visibility.Visible)
            return false;
        if (_matchParagraphs.Count + _lazyMatchCount <= 0)
            return false;

        if (PreviewSectionsPanel.Visibility == Visibility.Visible)
            return PreviewSectionsPanel.Children.OfType<Expander>().Any(expander => expander.IsExpanded);

        return PreviewBlock.Visibility == Visibility.Visible && PreviewBlock.Blocks.Count > 0;
    }

    private static bool IsElementWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static bool IsPreviewEnterReservedByFocusedControl(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox
                or PasswordBox
                or RichEditBox
                or AutoSuggestBox
                or NumberBox
                or ComboBox
                or CalendarDatePicker
                or DatePicker
                or TimePicker
                or ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
