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
    private bool _autoScrollEnabled = false;
    private bool _querySuggestionsDetached;
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

    private static readonly UIntPtr HotkeySubclassId = new(0x5147484Bu);
    private const int SW_RESTORE = 9;

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

    private bool _autoSearchOnLoad;

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
        Title = AppInfo.WindowTitle;

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
                SearchCancelIcon.Glyph = "\uE711";   // Cancel X
                SearchCancelLabel.Text = "Cancel";
                SearchCancelButton.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
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
                SearchCancelButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
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

        // Flush logs when the window closes so no diagnostic entries are lost.
        this.Closed += (_, _) =>
        {
            _hotkeyService.Dispose();
            RemoveGlobalHotkeyHook();
            LogService.Instance.Info("MainWindow", "Window closing — flushing logs");
            LogService.Instance.Flush();
        };

        // Show admin banner when not elevated
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator) && !ViewModel.SuppressAdminWarning)
            AdminBanner.IsOpen = true;
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
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                ShowWindow(_hwnd, SW_RESTORE);
                SetForegroundWindow(_hwnd);
            }

            Activate();
            FocusSearchBox();
        });
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

    private void OnRestartAsAdmin(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
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
            // User cancelled the UAC prompt — do nothing
        }
    }

    private async void OnDontShowAdminWarningAgain(object sender, RoutedEventArgs e)
    {
        ViewModel.SuppressAdminWarning = true;
        await ViewModel.PersistSettingsAsync();
        AdminBanner.IsOpen = false;
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

    /// <summary>Creates a settings group box: a bordered panel with a header and a content StackPanel.</summary>
    private static StackPanel MakeSettingsGroup(string header)
    {
        var content = new StackPanel { Spacing = 6, Padding = new Thickness(12, 8, 12, 12) };
        var border = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                        CornerRadius = new CornerRadius(6, 6, 0, 0),
                        Padding = new Thickness(12, 8, 12, 8),
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = new TextBlock
                        {
                            Text = header,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            FontSize = 14,
                        },
                    },
                    content,
                },
            },
        };
        // Stash the Border as the Tag so callers can retrieve it via .Parent.
        // We return the content panel for easy Children.Add() calls; the caller
        // adds (StackPanel).Parent (the outer StackPanel inside the Border) — but
        // that's the inner panel, not the Border.  Use a simple helper property instead.
        content.Tag = border;
        return content;
    }

    private FrameworkElement BuildSettingsPanel()
    {
        var sp = new StackPanel { Spacing = 12, Width = 480 };

        // Legend for the next-search icon
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 0) };
        legend.Children.Add(new FontIcon { Glyph = "\uE72C", FontSize = 11 });
        legend.Children.Add(new TextBlock { Text = "= takes effect on the next search", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
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

            g.Children.Add(new TextBlock { Text = "Multi-select preview mode:" });
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
            g.Children.Add(new TextBlock { Text = "When enabled, the original file is copied to <filename>.bak before saving changes from the built-in editor. If a .bak already exists, uses .bak-2, .bak-3, etc.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

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
            MaxHeight = 500,
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
    private readonly List<(RichTextBlock block, Paragraph para)> _matchParagraphs = new();
    private int _currentMatchIndex = -1;

    // Per-section match navigation state
    private sealed class SectionMatchNav
    {
        public List<Paragraph> Matches { get; } = new();
        public int CurrentIndex { get; set; } = -1;
        public ScrollViewer Scroller { get; set; } = null!;
        public RichTextBlock Block { get; set; } = null!;
    }
    private readonly Dictionary<RichTextBlock, SectionMatchNav> _sectionMatchNavs = new();
    private SectionMatchNav? _activeSectionNav;
    private static readonly SolidColorBrush s_activeExpanderBrush = new(Windows.UI.Color.FromArgb(25, 80, 180, 255));

    private void EnsurePreviewPanelVisible()
    {
        if (_previewPanelRevealed) return;
        _previewPanelRevealed = true;
        ResultsColumn.Width = new GridLength(2, GridUnitType.Star);
        SplitterColumn.Width = GridLength.Auto;
        PreviewColumn.Width = new GridLength(3, GridUnitType.Star);
        PreviewColumn.MinWidth = 200;
        SplitterBorder.Visibility = Visibility.Visible;
        PreviewPanelBorder.Visibility = Visibility.Visible;
        ExpandResultsIcon.Glyph = "\uE740"; // FullScreen glyph
        ToolTipService.SetToolTip(ExpandResultsButton, "Expand file list / collapse preview");
    }

    private void CollapsePreviewPanel()
    {
        if (!_previewPanelRevealed) return;
        _previewPanelRevealed = false;
        ResultsColumn.Width = new GridLength(1, GridUnitType.Star);
        SplitterColumn.Width = new GridLength(0);
        PreviewColumn.Width = new GridLength(0);
        PreviewColumn.MinWidth = 0;
        SplitterBorder.Visibility = Visibility.Collapsed;
        PreviewPanelBorder.Visibility = Visibility.Collapsed;
        ExpandResultsIcon.Glyph = "\uE73F"; // BackToWindow glyph
        ToolTipService.SetToolTip(ExpandResultsButton, "Restore preview panel");
    }

    private void OnExpandResultsPanel(object sender, RoutedEventArgs e)
    {
        if (_previewPanelRevealed)
            CollapsePreviewPanel();
        else
            EnsurePreviewPanelVisible();
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
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (ToolTipService.GetToolTip(child) is string tip
                && string.Equals(tip, filePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    child.IsExpanded = true;
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

    private async void OnFileGroupExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.SelectAll();
            var scrollTarget = g.Count > 0 ? g[0] : null;
            try { await UpdateMultiSelectPreviewAsync(scrollTarget, scrollToTop: true); }
            finally { _suppressPreviewUpdate = false; }
        }
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is FileGroup g && g.Count > 0)
            ViewModel.OpenInEditor(g[0]);
    }

    private async void OnMatchLineTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't trigger preview when user clicks the checkbox itself
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or CheckBox)
            return;

        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            var selected = ViewModel.GetAllSelectedResults();
            if (selected.Count >= 2)
                await UpdateMultiSelectPreviewAsync(scrollTarget: r);
            else
                await UpdatePreviewAsync(r);
        }
    }

    private async void OnShowFullFile(object sender, RoutedEventArgs e)
    {
        var targets = GetFullFilePreviewTargets();
        if (targets.Count == 0)
        {
            ShowPreviewMessage("Select a file or match in the results list first.");
            ViewModel.StatusText = "Select a file or match before showing the full file.";
            return;
        }

        await ShowFullFilePreviewAsync(targets);
    }

    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        ApplyWordWrap(ViewModel.PreviewWordWrap);
    }

    private async void OnPreviewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
    }

    private void ApplyWordWrap(bool wrap)
    {
        PreviewBlock.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PreviewEditor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        foreach (var block in EnumeratePreviewSectionBlocks())
            block.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        // For single-file block view, toggle outer scroll; for sections view it stays disabled.
        if (PreviewSectionsPanel.Visibility != Visibility.Visible)
        {
            PreviewScrollViewer.HorizontalScrollBarVisibility =
                wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }
        // Toggle each per-section horizontal scroller.
        foreach (var expander in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            if (expander.Content is ScrollViewer sv)
                sv.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }
        ScrollViewer.SetHorizontalScrollBarVisibility(
            PreviewEditor,
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }

    private async void OnSavePreviewEdit(object sender, RoutedEventArgs e)
    {
        await SavePreviewEditAsync();
    }

    private async void OnClosePreviewEdit(object sender, RoutedEventArgs e)
    {
        if (HasRealEditorChanges() && !await ConfirmDiscardPreviewEditAsync()) return;
        ClosePreviewEditor();

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
        else if (_previewResult is not null)
            await ShowSingleFilePreviewAsync(_previewResult, fullFile: false);
        else
            ShowPreviewMessage("No matches remain for this file.");
    }

    private void OnPreviewEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPreviewEditorTextChanged) return;
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        _previewEditorDirty = true;
        UpdatePreviewEditorButtons();
    }

    private async void RefreshCurrentPreview()
    {
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
        _currentMatchIndex = -1;
        HideMatchNavPanel();
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

    private async void OnMatchCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPreviewUpdate) return;
        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            var group = FindParentGroup(r);
            group?.NotifySelectionChanged();

            var selected = ViewModel.GetAllSelectedResults();
            if (selected.Count >= 2)
                await UpdateMultiSelectPreviewAsync(scrollTarget: r, scrollToTop: true);
        }
    }

    private async void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.SelectAll();
            var scrollTarget = g.Count > 0 ? g[0] : null;
            try { await UpdateMultiSelectPreviewAsync(scrollTarget, scrollToTop: true); }
            finally { _suppressPreviewUpdate = false; }
        }
    }

    private async void OnSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.DeselectAll();
            try { await UpdateMultiSelectPreviewAsync(); }
            finally { _suppressPreviewUpdate = false; }
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
            int count = ResultsList.SelectedItems.Count;
            if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem previewItem)
                previewItem.Text = $"Preview selected ({count})";
        }
    }

    private void OnResultsContextMenuOpening(object sender, object e)
    {
        int count = ResultsList.SelectedItems.Count;
        bool plural = count > 1;
        CtxPreviewSelected.Text = $"Preview selected ({count})";
        CtxCopyPaths.Text = plural ? "Copy File Paths" : "Copy File Path";
        CtxCopyWithContent.Text = plural ? "Copy Files With Content" : "Copy File With Content";
        CtxSavePaths.Text = plural ? "Save File Paths\u2026" : "Save File Path\u2026";
        CtxSaveWithContent.Text = plural ? "Save Files With Content\u2026" : "Save File With Content\u2026";
    }

    private async void OnPreviewSelectedFiles(object sender, RoutedEventArgs e)
    {
        // Select all match results within each selected FileGroup
        _suppressPreviewUpdate = true;
        try
        {
            foreach (var item in ResultsList.SelectedItems)
            {
                if (item is FileGroup g)
                    g.SelectAll();
            }
        }
        finally { _suppressPreviewUpdate = false; }

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0) return;

        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync(scrollTarget: selected[0], scrollToTop: true);
        else
            await UpdatePreviewAsync(selected[0]);
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
        var paths = new List<string>();
        foreach (var item in ResultsList.SelectedItems)
        {
            if (item is FileGroup g && !string.IsNullOrWhiteSpace(g.FilePath))
                paths.Add(g.FilePath);
        }

        return paths;
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
            return BuildFullFilePreviewTargets(selectedMatches);

        var selectedGroups = ResultsList.SelectedItems.OfType<FileGroup>()
            .Where(g => g.Count > 0)
            .ToList();
        if (selectedGroups.Count > 0)
        {
            var targets = new List<FullFilePreviewTarget>(selectedGroups.Count);
            foreach (var group in selectedGroups)
                targets.Add(new FullFilePreviewTarget(group.FilePath, group.ToList()));
            return targets;
        }

        if (_previewResult is null)
            return [];

        var parent = FindParentGroup(_previewResult);
        var matches = parent is null ? new List<SearchResult> { _previewResult } : parent.ToList();
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
        ShowPreviewSectionsSurface();
        _matchParagraphs.Clear();
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
            var (section, _) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results);

            fileContents.TryGetValue(filePath, out string[]? allLines);

            foreach (var r in results)
            {
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
                    var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx);
                    section.Blocks.Add(para);

                    if (isMatchLine)
                    {
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        AddMatchEntries(_matchParagraphs, sn, section, para, line, rx);
                    }

                    if (scrollTarget is not null && isMatchLine
                        && r.LineNumber == scrollTarget.LineNumber
                        && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        scrollBlock = section;
                        scrollPara = para;
                    }
                }
            }

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
        ShowPreviewSectionsSurface();
        _matchParagraphs.Clear();
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
            var (section, _) = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)", results);

            // Collect all match line numbers in this file
            var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));

            fileContents.TryGetValue(filePath, out string[]? allLines);

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
                        var gapRun = new Run { Text = "  ⋮" };
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
                        var matchResult = results.FirstOrDefault(r => r.LineNumber == lineNum) ?? results[0];
                        var para = MakePreviewParagraph(allLines[i], lineNum, isMatchLine, matchResult, rx);
                        section.Blocks.Add(para);
                        if (isMatchLine)
                        {
                            _sectionMatchNavs.TryGetValue(section, out var sn);
                            AddMatchEntries(_matchParagraphs, sn, section, para, allLines[i], rx);
                        }

                        if (scrollTarget is not null && isMatchLine && lineNum == scrollTarget.LineNumber
                            && string.Equals(filePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = para;
                        }
                    }
                }
            }
            else
            {
                // Fallback: concatenated style
                foreach (var r in results)
                {
                    var lines = GetPreviewLines(r, null, ViewModel.PreviewContextLines, fullFile: false);
                    foreach (var (line, lineNum) in lines)
                    {
                        bool isMatchLine = lineNum == r.LineNumber;
                        var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx);
                        section.Blocks.Add(para);
                        if (isMatchLine)
                        {
                            _sectionMatchNavs.TryGetValue(section, out var sn);
                            AddMatchEntries(_matchParagraphs, sn, section, para, line, rx);
                        }

                        if (scrollTarget is not null && isMatchLine
                            && r.LineNumber == scrollTarget.LineNumber
                            && string.Equals(r.FilePath, scrollTarget.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            scrollBlock = section;
                            scrollPara = para;
                        }
                    }
                }
            }

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

        // Activate the section for the clicked file
        if (scrollBlock is not null)
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
    private const int PreviewYieldBatchSize = 5;

    /// <summary>Max file sections to render in one page. Remaining are loaded on demand via "Show more".</summary>
    private const int PreviewSectionPageSize = 50;

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
        // Create a placeholder panel that LoadMoreSectionsAsync will remove.
        var placeholder = new StackPanel();
        PreviewSectionsPanel.Children.Add(placeholder);
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

    /// <summary>Max sections kept expanded during bulk "Show all" loads to reduce layout cost.</summary>
    private const int BulkExpandLimit = 10;

    private async Task LoadMoreSectionsAsync(
        StackPanel buttonPanel,
        List<KeyValuePair<string, List<SearchResult>>> orderedFiles,
        int pageStart, int requestedEnd,
        List<SearchResult> allSelected,
        int gen)
    {
        if (_previewUpdateGen != gen) return;

        // Remove the button panel.
        PreviewSectionsPanel.Children.Remove(buttonPanel);

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

            var fileContents = await ReadAllFileContentsAsync(pageFiles);
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
                fileContents.TryGetValue(filePath, out string[]? allLines);

                if (isHighlight)
                    BuildHighlightSection(section, results, allLines, previewLines, rx);
                else
                    BuildConcatenatedSection(section, results, allLines, previewLines, rx);

                pendingExpanders.Add(expander);
                totalSectionsAdded++;
            }

            // Add built sections to the visual tree in small batches with yields.
            for (int i = 0; i < pendingExpanders.Count; i++)
            {
                PreviewSectionsPanel.Children.Add(pendingExpanders[i]);

                if ((i + 1) % PreviewYieldBatchSize == 0)
                {
                    int filesLoaded = (cursor - pageStart) + i + 1;
                    if (loadingAll)
                        UpdateProgressOverlay((int)((double)filesLoaded / totalToLoad * 100));

                    await Task.Delay(1).ConfigureAwait(true);
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

        // Update match count and file count to reflect all loaded files.
        int loadedFiles = PreviewSectionsPanel.Children.OfType<Expander>().Count();
        SetPreviewFileLabel(
            $"{_matchParagraphs.Count:N0} selected matches across {loadedFiles:N0} file(s)",
            string.Join(Environment.NewLine, orderedFiles.Take(finalEnd).Select(kv => kv.Key)));
        UpdateMatchNavPanel();
        UpdateSectionMatchNavPanels();
    }

    private void BuildConcatenatedSection(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        foreach (var r in results)
        {
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
                var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx);
                section.Blocks.Add(para);
                if (isMatchLine)
                {
                    _sectionMatchNavs.TryGetValue(section, out var sn);
                    AddMatchEntries(_matchParagraphs, sn, section, para, line, rx);
                }
            }
        }
    }

    private void BuildHighlightSection(
        RichTextBlock section, List<SearchResult> results,
        string[]? allLines, int previewLines, Regex? rx)
    {
        var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));

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
                    var matchResult = results.FirstOrDefault(r => r.LineNumber == lineNum) ?? results[0];
                    var para = MakePreviewParagraph(allLines[i], lineNum, isMatchLine, matchResult, rx);
                    section.Blocks.Add(para);
                    if (isMatchLine)
                    {
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        AddMatchEntries(_matchParagraphs, sn, section, para, allLines[i], rx);
                    }
                }
            }
        }
        else
        {
            foreach (var r in results)
            {
                var lines = GetPreviewLines(r, null, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    bool isMatchLine = lineNum == r.LineNumber;
                    var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx);
                    section.Blocks.Add(para);
                    if (isMatchLine)
                    {
                        _sectionMatchNavs.TryGetValue(section, out var sn);
                        AddMatchEntries(_matchParagraphs, sn, section, para, line, rx);
                    }
                }
            }
        }
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
        string[]? allLines = null;
        try { allLines = await ReadAllLinesWithEncodingAsync(filePath); }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Preview", $"Cannot read file for full-file section preview: {filePath}", ex);
            return;
        }

        var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        // Remove old paragraph references for this section
        _matchParagraphs.RemoveAll(m => m.block == section);
        if (_sectionMatchNavs.TryGetValue(section, out var sn))
            sn.Matches.Clear();

        section.Blocks.Clear();
        for (int i = 0; i < allLines.Length; i++)
        {
            int lineNum = i + 1;
            bool isMatch = matchLines.Contains(lineNum);
            var matchResult = isMatch
                ? results.FirstOrDefault(r => r.LineNumber == lineNum) ?? results[0]
                : results[0];
            var para = MakePreviewParagraph(allLines[i], lineNum, isMatch, matchResult, rx, truncate: false);
            section.Blocks.Add(para);
            if (isMatch)
            {
                AddMatchEntries(_matchParagraphs, sn, section, para, allLines[i], rx);
            }
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
        if (_activeSectionNav == sn)
            UpdateSectionNavOverlay();
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
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        string[]? allLines = null;
        if (fullFile)
        {
            try { allLines = await ReadAllLinesWithEncodingAsync(r.FilePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for single-file preview: {r.FilePath}", ex); }
        }

        var lines = GetPreviewLines(r, allLines, ViewModel.PreviewContextLines, fullFile);
        foreach (var (line, lineNum) in lines)
        {
            bool isMatchLine = lineNum == r.LineNumber;
            var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx, truncate: !fullFile);
            PreviewBlock.Blocks.Add(para);
        }
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
            if (s is Expander exp && exp.Tag is RichTextBlock b)
                ActivateSectionForBlock(b);
        };
        ToolTipService.SetToolTip(expander, filePath);
        if (addToPanel)
            PreviewSectionsPanel.Children.Add(expander);
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

                // Remove per-section match nav data
                _sectionMatchNavs.Remove(block);

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
            foreach (var target in targets)
            {
                cts.Token.ThrowIfCancellationRequested();

                foreach (var result in target.Matches)
                    ViewModel.HydrateResult(result);

                try
                {
                    var document = await LoadPreviewDocumentAsync(target.FilePath, cts.Token).ConfigureAwait(true);
                    var section = AddFullFileSection(target, document.ByteLength);
                    RenderFullFileDocument(section, target, document.Text, rx);
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

    private static void RenderFullFileDocument(RichTextBlock section, FullFilePreviewTarget target, string text, Regex? rx)
    {
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
        while ((line = reader.ReadLine()) is not null)
        {
            var isMatchLine = matchByLine.TryGetValue(lineNumber, out var matchResult);
            var para = MakePreviewParagraph(line, lineNumber, isMatchLine, matchResult ?? target.Matches[0], rx, truncate: false);
            section.Blocks.Add(para);
            wroteLine = true;
            lineNumber++;
        }

        if (!wroteLine)
        {
            var para = new Paragraph();
            var run = new Run { Text = "(empty file)" };
            run.Foreground = s_contextTextBrush;
            para.Inlines.Add(run);
            section.Blocks.Add(para);
        }
    }

    private async Task ShowFullFileEditorAsync(SearchResult result)
    {
        if (PreviewEditor.Visibility == Visibility.Visible && HasRealEditorChanges())
        {
            ViewModel.StatusText = "Save or close the current editor before loading another full file.";
            return;
        }

        _previewLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;

        ClosePreviewEditor(clearText: true, cancelLoad: false);
        SetPreviewFileLabel(result.FilePath);
        ShowPreviewMessage($"Loading full file for editing (limit {FormatBytes(FullFilePreviewLimitBytes)})...");
        FullFileButton.IsEnabled = false;

        try
        {
            var document = await LoadPreviewDocumentAsync(result.FilePath, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            _previewEditorPath = result.FilePath;
            _previewEditorEncoding = document.Encoding;

            // Archive entries are read-only (cannot save back into zip)
            bool isArchive = ZipArchiveSearcher.IsArchivePath(result.FilePath);
            PreviewEditor.IsReadOnly = isArchive;

            // Progressively load text in chunks to avoid freezing the UI on large files.
            _suppressPreviewEditorTextChanged = true;
            SetPreviewEditorVisible(true);

            const int chunkSize = 256 * 1024; // 256 KB per chunk
            var text = document.Text;
            if (text.Length <= chunkSize)
            {
                PreviewEditor.Text = text;
            }
            else
            {
                // Load the first chunk immediately so the user sees content right away.
                PreviewEditor.Text = text[..chunkSize];
                int loaded = chunkSize;
                while (loaded < text.Length)
                {
                    if (cts.IsCancellationRequested) break;
                    // Yield to let the UI render the previous chunk.
                    await Task.Delay(1, cts.Token).ConfigureAwait(true);
                    int end = Math.Min(loaded + chunkSize, text.Length);
                    // Append next chunk by selecting the end and inserting.
                    PreviewEditor.Select(PreviewEditor.Text.Length, 0);
                    PreviewEditor.SelectedText = text[loaded..end];
                    loaded = end;
                }
            }

            _previewEditorOriginalText = PreviewEditor.Text;
            _suppressPreviewEditorTextChanged = false;
            _previewEditorDirty = false;
            UpdateEditorDirtyIndicator();

            PreviewEditor.Focus(FocusState.Programmatic);

            // Scroll to the match line and highlight the matched text.
            ScrollEditorToMatch(text, result);

            // WinUI 3 TextBox may fire deferred TextChanged after a bulk text
            // load (line-ending normalisation, layout pass, etc.).  If that
            // sets _previewEditorDirty even though the text is unchanged,
            // clear it again so the indicator doesn't show a false positive.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_previewEditorOriginalText is not null
                    && PreviewEditor.Visibility == Visibility.Visible
                    && _previewEditorDirty
                    && PreviewEditor.Text == _previewEditorOriginalText)
                {
                    _previewEditorDirty = false;
                    UpdatePreviewEditorButtons();
                }
            });

            string label = isArchive ? "viewing (read-only)" : "editing";
            ViewModel.StatusText = $"Loaded {Path.GetFileName(result.FilePath)} ({FormatBytes(document.ByteLength)}, {GetEncodingDisplayName(document.Encoding)}) for {label}.";
        }
        catch (OperationCanceledException)
        {
            ShowPreviewMessage("Full-file load cancelled.", showBackButton: _previewResult is not null);
        }
        catch (PreviewLoadException ex)
        {
            ShowPreviewMessage(ex.Message, showBackButton: _previewResult is not null);
            ViewModel.StatusText = ex.Message;
        }
        catch (OutOfMemoryException ex)
        {
            const string message = "Not enough memory to load this full file into the right-panel editor.";
            LogService.Instance.Warning("Preview", message, ex);
            ShowPreviewMessage(message, showBackButton: _previewResult is not null);
            ViewModel.StatusText = message;
        }
        catch (Exception ex)
        {
            var message = $"Could not load full file: {ex.Message}";
            LogService.Instance.Warning("Preview", $"Could not load full file: {result.FilePath}", ex);
            ShowPreviewMessage(message, showBackButton: _previewResult is not null);
            ViewModel.StatusText = message;
        }
        finally
        {
            if (ReferenceEquals(_previewLoadCts, cts))
                _previewLoadCts = null;
            cts.Dispose();
            FullFileButton.IsEnabled = _previewResult is not null;
            UpdatePreviewEditorButtons();
        }
    }

    private void ScrollEditorToMatch(string text, SearchResult result)
    {
        // Find the character offset of the target line (1-based LineNumber).
        int charOffset = 0;
        int currentLine = 1;
        for (int i = 0; i < text.Length && currentLine < result.LineNumber; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                currentLine++;
                charOffset = i + 2;
                i++; // skip \n
            }
            else if (text[i] == '\n')
            {
                currentLine++;
                charOffset = i + 1;
            }
        }

        // TextBox uses \r\n → each line adds 2 chars. But TextBox.Text already has \r\n,
        // so charOffset calculated above is correct. Select the matched portion.
        int selectStart = charOffset + result.MatchStartColumn;
        int selectLength = result.MatchLength;

        // Clamp to valid range.
        if (selectStart > text.Length) selectStart = charOffset;
        if (selectStart + selectLength > text.Length) selectLength = 0;

        // Select the match — this auto-scrolls the TextBox to the selection.
        PreviewEditor.Select(selectStart, Math.Max(selectLength, 0));
    }

    private static async Task<PreviewTextDocument> LoadPreviewDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        // Handle archive entry paths: extract the entry to a memory stream
        if (ZipArchiveSearcher.IsArchivePath(filePath))
        {
            return await LoadArchiveEntryPreviewAsync(filePath, cancellationToken).ConfigureAwait(false);
        }

        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch (Exception ex) { throw new PreviewLoadException($"Could not inspect full file: {ex.Message}"); }

        if (!info.Exists)
            throw new PreviewLoadException("Could not load full file: it no longer exists.");
        if (info.Length > FullFilePreviewLimitBytes)
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
            return new PreviewTextDocument(text, reader.CurrentEncoding, info.Length);
        }
        catch (PreviewLoadException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new PreviewLoadException($"Could not load full file: access denied. {ex.Message}"); }
        catch (DecoderFallbackException ex) { throw new PreviewLoadException($"Could not load full file: unsupported text encoding. {ex.Message}"); }
        catch (IOException ex) { throw new PreviewLoadException($"Could not load full file: {ex.Message}"); }
    }

    private static async Task<PreviewTextDocument> LoadArchiveEntryPreviewAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            using var ms = await ZipArchiveSearcher.ExtractToMemoryAsync(archivePath, cancellationToken).ConfigureAwait(false);
            long byteLength = ms.Length;

            if (byteLength > FullFilePreviewLimitBytes)
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

    private async Task<bool> SavePreviewEditAsync()
    {
        if (_previewEditorPath is null || _previewEditorEncoding is null) return false;

        SavePreviewEditButton.IsEnabled = false;
        try
        {
            if (ViewModel.BackupBeforeSave && File.Exists(_previewEditorPath))
            {
                var bakPath = _previewEditorPath + ".bak";
                if (!File.Exists(bakPath))
                {
                    File.Copy(_previewEditorPath, bakPath, overwrite: false);
                }
                else
                {
                    int suffix = 2;
                    while (File.Exists($"{_previewEditorPath}.bak-{suffix}"))
                        suffix++;
                    File.Copy(_previewEditorPath, $"{_previewEditorPath}.bak-{suffix}", overwrite: false);
                }
            }

            var textToSave = PreviewEditor.Text;
            if (TextHasUnencodableCharacters(textToSave, _previewEditorEncoding))
            {
                var encDialog = new ContentDialog
                {
                    XamlRoot = ((FrameworkElement)Content).XamlRoot,
                    Title = "Encoding Warning",
                    Content = $"This file contains characters that cannot be represented in {GetEncodingDisplayName(_previewEditorEncoding)}. Save as UTF-8 instead?",
                    PrimaryButtonText = "Save as UTF-8",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                var encChoice = await encDialog.ShowAsync();
                if (encChoice != ContentDialogResult.Primary) return false;
                _previewEditorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            await File.WriteAllTextAsync(_previewEditorPath, textToSave, _previewEditorEncoding).ConfigureAwait(true);
            _previewEditorOriginalText = textToSave;
            _previewEditorDirty = false;
            UpdatePreviewEditorButtons();

            // Re-validate search results for this file against the saved content.
            bool fileStillHasMatches = ViewModel.RevalidateFileResults(_previewEditorPath, PreviewEditor.Text);
            if (!fileStillHasMatches && _previewResult?.FilePath is not null &&
                string.Equals(_previewResult.FilePath, _previewEditorPath, StringComparison.OrdinalIgnoreCase))
            {
                _previewResult = null;
            }

            ViewModel.StatusText = $"Saved {_previewEditorPath}.";
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Could not save file: {ex.Message}";
            LogService.Instance.Warning("Preview", $"Could not save editor contents: {_previewEditorPath}", ex);
            ViewModel.StatusText = message;
            return false;
        }
        finally
        {
            UpdatePreviewEditorButtons();
        }
    }

    /// <summary>Returns true if the caller should proceed (edits were saved or discarded), false to cancel.</summary>
    private async Task<bool> ConfirmDiscardPreviewEditAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Unsaved changes",
            Content = "The right-panel editor has unsaved changes.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary)
        {
            // Save first, then allow the close/navigation to proceed.
            return await SavePreviewEditAsync();
        }
        return choice == ContentDialogResult.Secondary; // Discard → true, Cancel → false
    }

    private bool TryLeavePreviewEditorForPreviewChange()
    {
        if (_previewLoadCts is not null)
        {
            _previewLoadCts.Cancel();
            _previewLoadCts = null;
            FullFileButton.IsEnabled = true;
        }

        if (PreviewEditor.Visibility != Visibility.Visible) return true;
        if (HasRealEditorChanges())
        {
            ViewModel.StatusText = "Save or close the right-panel editor before changing the preview.";
            return false;
        }

        ClosePreviewEditor();
        return true;
    }

    private void ClosePreviewEditor(bool clearText = true, bool cancelLoad = true)
    {
        if (cancelLoad)
        {
            _previewLoadCts?.Cancel();
            _previewLoadCts = null;
        }

        SetPreviewEditorVisible(false);
        _previewEditorPath = null;
        _previewEditorEncoding = null;
        _previewEditorDirty = false;
        _previewEditorOriginalText = null;

        if (clearText)
        {
            _suppressPreviewEditorTextChanged = true;
            PreviewEditor.Text = string.Empty;
            _suppressPreviewEditorTextChanged = false;
        }

        UpdatePreviewEditorButtons();
    }

    private void SetPreviewEditorVisible(bool visible)
    {
        PreviewEditor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewScrollViewer.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        // Editor-mode group
        EditorSeparator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SavePreviewEditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ClosePreviewEditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // Hide view/action buttons while editing
        FullFileButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        OpenInDefaultAppButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        OpenInEditorButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        ShowInExplorerButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

        if (visible)
            PreviewToolbarContent.Visibility = Visibility.Visible;
        ApplyWordWrap(ViewModel.PreviewWordWrap);
    }

    /// <summary>
    /// Returns true only if the editor text actually differs from the
    /// originally-loaded content.  This avoids false positives caused by
    /// WinUI 3 TextBox raising deferred TextChanged events after a bulk load.
    /// </summary>
    private bool HasRealEditorChanges() =>
        _previewEditorDirty
        && _previewEditorOriginalText is not null
        && PreviewEditor.Text != _previewEditorOriginalText;

    private void UpdatePreviewEditorButtons()
    {
        SavePreviewEditButton.IsEnabled = PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && _previewEditorPath is not null;
        UpdateEditorDirtyIndicator();
    }

    private void UpdateEditorDirtyIndicator()
    {
        if (_previewEditorPath is null) return;
        string baseName = _previewEditorPath;
        // Strip any existing dirty indicator prefix.
        if (PreviewFileLabel.Text.StartsWith("● ", StringComparison.Ordinal))
        {
            var existingTooltip = ToolTipService.GetToolTip(PreviewFileLabel) as string;
            baseName = existingTooltip ?? PreviewFileLabel.Text[2..];
        }
        else
        {
            baseName = PreviewFileLabel.Text;
        }
        SetPreviewFileLabel(
            _previewEditorDirty ? $"● {baseName}" : baseName,
            baseName);
    }

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
            LogService.Instance.Verbose("Preview", "Invalid highlight regex", ex);
            return null;
        }
    }

    private static readonly SolidColorBrush s_matchGutterBrush = new(Microsoft.UI.Colors.LimeGreen);

    /// <summary>
    /// Returns the number of individual regex match occurrences on a line (minimum 1 for a match line).
    /// </summary>
    private static int CountRegexMatches(string? line, Regex? rx)
    {
        if (rx is null || string.IsNullOrEmpty(line)) return 1;
        int count = rx.Matches(line).Count;
        return count > 0 ? count : 1;
    }

    private static void AddMatchEntries(
        List<(RichTextBlock block, Paragraph para)> matchParagraphs,
        SectionMatchNav? sn,
        RichTextBlock section, Paragraph para,
        string? line, Regex? rx)
    {
        int count = CountRegexMatches(line, rx);
        for (int i = 0; i < count; i++)
        {
            matchParagraphs.Add((section, para));
            sn?.Matches.Add(para);
        }
    }

    private static readonly SolidColorBrush s_contextGutterBrush = new(Windows.UI.Color.FromArgb(255, 80, 80, 80));
    private static readonly SolidColorBrush s_gutterSepBrush = new(Windows.UI.Color.FromArgb(255, 60, 60, 60));
    private static readonly SolidColorBrush s_contextTextBrush = new(Windows.UI.Color.FromArgb(255, 110, 110, 110));
    private static readonly SolidColorBrush s_matchAccentBrush = new(Windows.UI.Color.FromArgb(255, 70, 140, 70));

    private static Paragraph MakePreviewParagraph(string line, int lineNum, bool isMatchLine, SearchResult r, Regex? rx, bool truncate = true)
    {
        line ??= string.Empty;
        if (truncate)
            line = TruncatePreviewLine(line, rx);
        var para = new Paragraph();

        // Match indicator + line number gutter
        var indicator = new Run { Text = isMatchLine ? "▎" : " " };
        indicator.Foreground = isMatchLine ? s_matchAccentBrush : s_contextTextBrush;
        para.Inlines.Add(indicator);

        var gutterRun = new Run { Text = $"{lineNum,5} " };
        gutterRun.Foreground = isMatchLine ? s_matchGutterBrush : s_contextGutterBrush;
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

    /// <summary>
    /// Scrolls the outer preview ScrollViewer so that <paramref name="targetPara"/>
    /// inside <paramref name="block"/> is vertically centred in the viewport.
    /// Must be called after the content has been added to the visual tree;
    /// actual scrolling is deferred to a low-priority dispatcher tick so layout
    /// has time to complete.
    /// </summary>
    private void ScrollPreviewToLine(RichTextBlock block, Paragraph targetPara)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                // Find the highlighted (Gold/Bold) Run within the paragraph for precise positioning.
                TextPointer? pointer = null;
                foreach (var inline in targetPara.Inlines)
                {
                    if (inline is Run run && run.FontWeight.Weight == Microsoft.UI.Text.FontWeights.Bold.Weight
                        && run.Foreground is SolidColorBrush brush
                        && brush.Color == Microsoft.UI.Colors.Gold)
                    {
                        pointer = run.ContentStart;
                        break;
                    }
                }
                pointer ??= targetPara.ContentStart;
                if (pointer is null) return;

                var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                var transform = block.TransformToVisual(PreviewScrollViewer);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, rect.Y));

                double viewportHeight = PreviewScrollViewer.ViewportHeight;
                double targetOffset = PreviewScrollViewer.VerticalOffset + point.Y - viewportHeight / 2;
                targetOffset = Math.Max(0, targetOffset);

                PreviewScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
            }
            catch
            {
                // Layout may not be ready — silently ignore.
            }
        });
    }

    private int MatchNavFileCount => _matchParagraphs.Select(m => m.block).Distinct().Count();

    private string FormatMatchNavLabel(int index)
    {
        int fileCount = MatchNavFileCount;
        return $"Match {index + 1} of {_matchParagraphs.Count} (across {fileCount} file{(fileCount != 1 ? "s" : "")})";
    }

    private void UpdateMatchNavPanel()
    {
        if (_matchParagraphs.Count > 0)
        {
            MatchNavPanel.Visibility = Visibility.Visible;
            _currentMatchIndex = 0;
            MatchNavLabel.Text = FormatMatchNavLabel(0);
        }
        else
        {
            HideMatchNavPanel();
        }
    }

    private void HideMatchNavPanel()
    {
        MatchNavPanel.Visibility = Visibility.Collapsed;
        SectionNavOverlay.Visibility = Visibility.Collapsed;
        _matchParagraphs.Clear();
        _currentMatchIndex = -1;
        _sectionMatchNavs.Clear();
        _activeSectionNav = null;
    }

    private void UpdateSectionMatchNavPanels()
    {
        // Find the first section with multiple matches and make it active
        _activeSectionNav = null;
        foreach (var sn in _sectionMatchNavs.Values)
        {
            if (sn.Matches.Count > 1)
            {
                sn.CurrentIndex = 0;
                _activeSectionNav ??= sn;
            }
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

    private void HighlightActiveExpander()
    {
        var activeBlock = _activeSectionNav?.Block;
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
        SectionNavLabel.Text = $"Match {_activeSectionNav.CurrentIndex + 1} of {_activeSectionNav.Matches.Count}";
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
        sn.CurrentIndex = (sn.CurrentIndex + 1) % sn.Matches.Count;
        _activeSectionNav = sn;
        HighlightActiveExpander();
        UpdateSectionNavOverlay();
        ScrollPreviewToLine(sn.Block, sn.Matches[sn.CurrentIndex]);
    }

    private void OnSectionPrevMatch(SectionMatchNav sn)
    {
        if (sn.Matches.Count == 0) return;
        sn.CurrentIndex = (sn.CurrentIndex - 1 + sn.Matches.Count) % sn.Matches.Count;
        _activeSectionNav = sn;
        HighlightActiveExpander();
        UpdateSectionNavOverlay();
        ScrollPreviewToLine(sn.Block, sn.Matches[sn.CurrentIndex]);
    }

    private void OnNextMatch(object sender, RoutedEventArgs e)
    {
        if (_matchParagraphs.Count == 0) return;
        _currentMatchIndex = (_currentMatchIndex + 1) % _matchParagraphs.Count;
        var (block, para) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        ActivateSectionForBlock(block);
        ScrollPreviewToLine(block, para);
    }

    private void OnPrevMatch(object sender, RoutedEventArgs e)
    {
        if (_matchParagraphs.Count == 0) return;
        _currentMatchIndex = (_currentMatchIndex - 1 + _matchParagraphs.Count) % _matchParagraphs.Count;
        var (block, para) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        ActivateSectionForBlock(block);
        ScrollPreviewToLine(block, para);
    }

    private bool _splitterDragging;
    private double _splitterStartX;
    private double _col0StartWidth;
    private double _col2StartWidth;

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
                        var bakPath = path + ".bak";
                        if (!File.Exists(bakPath))
                            File.Copy(path, bakPath, overwrite: false);
                        else
                        {
                            int suffix = 2;
                            while (File.Exists($"{path}.bak-{suffix}")) suffix++;
                            File.Copy(path, $"{path}.bak-{suffix}", overwrite: false);
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
