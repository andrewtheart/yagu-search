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
    private bool _autoScrollEnabled = false;
    private bool _querySuggestionsDetached;
    private DispatcherTimer? _autoScrollTimer;
    private const long FullFilePreviewLimitBytes = 1L * 1024 * 1024 * 1024;
    private CancellationTokenSource? _previewLoadCts;
    private string? _previewEditorPath;
    private Encoding? _previewEditorEncoding;
    private bool _previewEditorDirty;
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
                _autoScrollEnabled = AutoScrollResultsCheckBox.IsChecked == true;
                _autoScrollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _autoScrollTimer.Tick += OnAutoScrollTick;
                _autoScrollTimer.Start();
            }
            else
            {
                _autoScrollTimer?.Stop();
            }
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

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        HideQuerySuggestions();
        if (!await ClearPreviewPanelForNewSearchAsync()) return;
        await ViewModel.StartSearchAsync();
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
        => await ViewModel.CancelAsync();

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
        await ViewModel.StartSearchAsync();
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !IsShiftDown())
        {
            e.Handled = true;
            HideQuerySuggestions(sender as AutoSuggestBox);
            await ViewModel.StartSearchAsync();
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.IsSearching)
        {
            e.Handled = true;
            await ViewModel.CancelAsync();
        }
    }

    private void HideQuerySuggestions(AutoSuggestBox? box = null)
    {
        var target = box ?? QueryBox;
        _querySuggestionsDetached = true;
        target.IsSuggestionListOpen = false;
        target.ItemsSource = null;
        target.IsSuggestionListOpen = false;
        DispatcherQueue.TryEnqueue(() => target.IsSuggestionListOpen = false);
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

    private FrameworkElement BuildSettingsPanel()
    {
        var sp = new StackPanel { Spacing = 8, Width = 480 };

        // ── Search Defaults ──
        sp.Children.Add(new TextBlock { Text = "Search Defaults", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 4, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Context lines (lines shown before & after each match in results):" });
        var ctx = new NumberBox { Value = ViewModel.ContextLines, Minimum = 0, Maximum = 50 };
        ctx.ValueChanged += (_, args) => ViewModel.ContextLines = (int)args.NewValue;
        sp.Children.Add(ctx);

        sp.Children.Add(new TextBlock { Text = "Preview context lines (lines shown around each match in the preview panel):" });
        var prevCtx = new NumberBox { Value = ViewModel.PreviewContextLines, Minimum = 0, Maximum = 200 };
        prevCtx.ValueChanged += (_, args) => ViewModel.PreviewContextLines = (int)args.NewValue;
        sp.Children.Add(prevCtx);

        sp.Children.Add(new TextBlock { Text = "Default include globs (comma/semicolon-separated):" });
        var incGlobs = new TextBox { Text = ViewModel.IncludeGlobs, PlaceholderText = "e.g. *.cs;*.ts" };
        incGlobs.TextChanged += (_, _) => ViewModel.IncludeGlobs = incGlobs.Text;
        sp.Children.Add(incGlobs);

        sp.Children.Add(new TextBlock { Text = "Default exclude globs (comma/semicolon-separated):" });
        var excGlobs = new TextBox { Text = ViewModel.ExcludeGlobs, PlaceholderText = "e.g. node_modules;bin;obj;.git" };
        excGlobs.TextChanged += (_, _) => ViewModel.ExcludeGlobs = excGlobs.Text;
        sp.Children.Add(excGlobs);

        // ── Search Limits ──
        sp.Children.Add(new TextBlock { Text = "Search Limits", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Max results (0 = unlimited, non-zero values capped at 50,000):" });
        var max = new NumberBox { Value = ViewModel.MaxResults, Minimum = 0 };
        max.ValueChanged += (_, args) => ViewModel.MaxResults = (int)args.NewValue;
        sp.Children.Add(max);
        sp.Children.Add(new TextBlock { Text = "Stops the search after this many matches. Set to 0 for no limit (memory pressure will still protect against runaway usage).", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "Max file size to search (MB, 0 = no limit):" });
        var size = new NumberBox { Value = ViewModel.MaxFileSizeBytes / (1024d * 1024d), Minimum = 0 };
        size.ValueChanged += (_, args) => ViewModel.MaxFileSizeBytes = (long)(args.NewValue * 1024 * 1024);
        sp.Children.Add(size);
        sp.Children.Add(new TextBlock { Text = "Files larger than this are skipped during search. Also used by the Everything SDK to pre-filter results.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        var skipBinary = new CheckBox { Content = "Skip binary files", IsChecked = ViewModel.SkipBinary };
        skipBinary.Checked += (_, _) => ViewModel.SkipBinary = true;
        skipBinary.Unchecked += (_, _) => ViewModel.SkipBinary = false;
        sp.Children.Add(skipBinary);
        sp.Children.Add(new TextBlock { Text = "When enabled, files detected as binary (null bytes, magic bytes) are skipped during content search.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "Skip extensions (semicolon-separated, no dots):", Margin = new Thickness(0, 4, 0, 0) });
        var skipExt = new TextBox { Text = ViewModel.SkipExtensions, PlaceholderText = "e.g. exe;dll;zip;png;pdf", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
        skipExt.TextChanged += (_, _) => ViewModel.SkipExtensions = skipExt.Text;
        sp.Children.Add(skipExt);
        sp.Children.Add(new TextBlock { Text = "Files with these extensions are skipped entirely — no binary check, no content read.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "Archive extensions (semicolon-separated, no dots):", Margin = new Thickness(0, 4, 0, 0) });
        var archiveExt = new TextBox { Text = ViewModel.ArchiveExtensions, PlaceholderText = "e.g. zip;jar;docx;xlsx;pptx;epub", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
        archiveExt.TextChanged += (_, _) => ViewModel.ArchiveExtensions = archiveExt.Text;
        sp.Children.Add(archiveExt);
        sp.Children.Add(new TextBlock { Text = "Extensions that are ZIP-like containers. When 'Search archives' is on, these are removed from the skip list so they reach the content searcher. Detection still uses file-header magic bytes.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        // ── Performance ──
        sp.Children.Add(new TextBlock { Text = "Performance", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Content-search parallelism (concurrent file scan threads):" });
        var parallelism = new ComboBox();
        parallelism.Items.Add($"Auto (safe cap · up to {Math.Min(16, Environment.ProcessorCount)})");
        parallelism.Items.Add("1 thread (sequential, HDD safe)");
        parallelism.Items.Add($"Half cores ({Math.Max(1, Environment.ProcessorCount / 2)})");
        parallelism.Items.Add($"2× cores ({Environment.ProcessorCount * 2}, I/O heavy)");
        parallelism.Items.Add($"All cores ({Math.Max(1, Environment.ProcessorCount)})");
        parallelism.SelectedIndex = ViewModel.ParallelismIndex;
        parallelism.SelectionChanged += (_, _) => ViewModel.ParallelismIndex = parallelism.SelectedIndex;
        sp.Children.Add(parallelism);

        sp.Children.Add(new TextBlock { Text = "File-listing backend (how files are discovered before searching):" });
        var backend = new ComboBox();
        backend.Items.Add("Auto (SDK → es.exe → .NET)");
        backend.Items.Add("Everything SDK only (in-process, fastest)");
        backend.Items.Add("es.exe only (process spawn)");
        backend.Items.Add(".NET enumeration only (no Everything dependency)");
        backend.SelectedIndex = ViewModel.FileListerBackendIndex;
        backend.SelectionChanged += (_, _) => ViewModel.FileListerBackendIndex = backend.SelectedIndex;
        sp.Children.Add(backend);
        sp.Children.Add(new TextBlock { Text = "Auto tries the Everything SDK first, then es.exe, then .NET recursive enumeration. Requires voidtools Everything to be running for SDK/es.exe.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "Process memory hard cap (MB):" });
        var memLimit = new NumberBox { Value = ViewModel.MemoryLimitMB, Minimum = 0, Maximum = 65536 };
        memLimit.ValueChanged += (_, args) => ViewModel.MemoryLimitMB = (int)args.NewValue;
        sp.Children.Add(memLimit);
        sp.Children.Add(new TextBlock { Text = "Maximum working set size for the Yagu process. Set to 0 for auto (25% of physical RAM, minimum 2 GB). Search enters memory-saving mode when this is exceeded.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "SDK channel buffer size:" });
        var sdkBuf = new NumberBox { Value = ViewModel.SdkChannelBufferSize, Minimum = 16, Maximum = 1000000 };
        sdkBuf.ValueChanged += (_, args) => ViewModel.SdkChannelBufferSize = (int)args.NewValue;
        sp.Children.Add(sdkBuf);
        sp.Children.Add(new TextBlock { Text = "Number of file paths buffered between the Everything SDK producer thread and the consumer. Higher values may improve throughput on large directories but use more memory. Only applies when using the Everything SDK backend.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "System memory pressure limit (%, 0 = disabled):", TextWrapping = TextWrapping.Wrap });
        var memPressure = new NumberBox { Value = ViewModel.MemoryPressurePercent, Minimum = 0, Maximum = 100 };
        memPressure.ValueChanged += (_, args) => ViewModel.MemoryPressurePercent = (int)args.NewValue;
        sp.Children.Add(memPressure);
        sp.Children.Add(new TextBlock { Text = "Yagu enters memory saving / eviction mode when total machine RAM usage exceeds this %.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        // ── Display ──
        sp.Children.Add(new TextBlock { Text = "Display", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Line truncation length (characters):" });
        var trunc = new NumberBox { Value = ViewModel.LineTruncationLength, Minimum = 50, Maximum = 10000 };
        trunc.ValueChanged += (_, args) => ViewModel.LineTruncationLength = (int)args.NewValue;
        sp.Children.Add(trunc);
        sp.Children.Add(new TextBlock { Text = "Lines longer than this are truncated in the results list to prevent UI slowdowns from extremely long lines.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(new TextBlock { Text = "Multi-select preview mode:" });
        var previewMode = new ComboBox();
        previewMode.Items.Add("Concatenated (separate match snippets)");
        previewMode.Items.Add("Multi-highlight (unified file view)");
        previewMode.SelectedIndex = ViewModel.PreviewModeIndex;
        previewMode.SelectionChanged += (_, _) => ViewModel.PreviewModeIndex = previewMode.SelectedIndex;
        sp.Children.Add(previewMode);

        var wordWrap = new CheckBox { Content = "Word wrap in preview panel", IsChecked = ViewModel.PreviewWordWrap };
        wordWrap.Checked += (_, _) => { ViewModel.PreviewWordWrap = true; ApplyWordWrap(true); };
        wordWrap.Unchecked += (_, _) => { ViewModel.PreviewWordWrap = false; ApplyWordWrap(false); };
        sp.Children.Add(wordWrap);

        // ── Editor ──
        sp.Children.Add(new TextBlock { Text = "Editor", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Editor command ({file} = full file path, {line} = line number):" });
        var editor = new TextBox { Text = ViewModel.EditorCommand };
        editor.TextChanged += (_, _) => ViewModel.EditorCommand = editor.Text;
        sp.Children.Add(editor);

        // ── General ──
        sp.Children.Add(new TextBlock { Text = "General", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        var availableHotkeyKeys = _hotkeyService.GetAvailableCtrlShiftLetterKeys(_hwnd);
        var selectedHotkeyKey = HotkeyService.ChooseAvailableKey(availableHotkeyKeys, ViewModel.GlobalHotkeyKey);
        if (selectedHotkeyKey is char selectedKey && !string.Equals(ViewModel.GlobalHotkeyKey, selectedKey.ToString(), StringComparison.OrdinalIgnoreCase))
            ViewModel.GlobalHotkeyKey = selectedKey.ToString();

        var hotkey = new CheckBox { Content = "Enable global hotkey", IsChecked = ViewModel.GlobalHotkeyEnabled, IsEnabled = availableHotkeyKeys.Count > 0 };
        hotkey.Checked += (_, _) => ViewModel.GlobalHotkeyEnabled = true;
        hotkey.Unchecked += (_, _) => ViewModel.GlobalHotkeyEnabled = false;
        sp.Children.Add(hotkey);

        sp.Children.Add(new TextBlock { Text = "Global hotkey:" });
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
        sp.Children.Add(hotkeyCombo);

        sp.Children.Add(new TextBlock { Text = "Max recent directories / queries to remember:" });
        var recent = new NumberBox { Value = ViewModel.MaxRecentItems, Minimum = 1, Maximum = 100 };
        recent.ValueChanged += (_, args) => ViewModel.MaxRecentItems = (int)args.NewValue;
        sp.Children.Add(recent);

        sp.Children.Add(new TextBlock { Text = "Log verbosity level:" });
        var logLevel = new ComboBox();
        logLevel.Items.Add("Critical (errors only)");
        logLevel.Items.Add("Warning (errors + warnings)");
        logLevel.Items.Add("Info (general activity)");
        logLevel.Items.Add("Verbose (all details, may slow performance)");
        logLevel.SelectedIndex = ViewModel.LogLevelIndex;
        logLevel.SelectionChanged += (_, _) => ViewModel.LogLevelIndex = logLevel.SelectedIndex;
        sp.Children.Add(logLevel);

        sp.Children.Add(new TextBlock { Text = $"Log file: {LogService.DefaultLogPath()}", FontSize = 11, Opacity = 0.6 });

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

    private async void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileGroup g && g.Count > 0)
            await UpdatePreviewAsync(g[0]);
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

    private async void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        await ViewModel.PersistSettingsAsync();
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
        if (_previewEditorDirty && !await ConfirmDiscardPreviewEditAsync()) return;
        ClosePreviewEditor();

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count >= 2)
            await UpdateMultiSelectPreviewAsync();
        else if (_previewResult is not null)
            await ShowSingleFilePreviewAsync(_previewResult, fullFile: false);
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
        if (PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && !await ConfirmDiscardPreviewEditAsync())
            return false;

        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        FullFileButton.IsEnabled = true;
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
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

    private void OnMatchCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPreviewUpdate) return;
        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            var group = FindParentGroup(r);
            group?.NotifySelectionChanged();
        }
    }

    private async void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.SelectAll();
            _suppressPreviewUpdate = false;
            await UpdateMultiSelectPreviewAsync();
        }
    }

    private async void OnSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.DeselectAll();
            _suppressPreviewUpdate = false;
            await UpdateMultiSelectPreviewAsync();
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

        // Hydrate from disk if this result was evicted during memory pressure.
        ViewModel.HydrateResult(r);
        _previewResult = r;
        SetPreviewFileLabel(r.FilePath);
        PreviewToolbarContent.Visibility = Visibility.Visible;
        await ShowSingleFilePreviewAsync(r, fullFile: false);
    }

    private async Task UpdateMultiSelectPreviewAsync(SearchResult? scrollTarget = null)
    {
        if (!TryLeavePreviewEditorForPreviewChange()) return;

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

        PreviewToolbarContent.Visibility = Visibility.Visible;
        if (ViewModel.PreviewModeIndex == 1)
            await ShowMultiHighlightPreviewAsync(selected, scrollTarget);
        else
            await ShowConcatenatedPreviewAsync(selected, scrollTarget);
    }

    private async Task ShowConcatenatedPreviewAsync(List<SearchResult> selected, SearchResult? scrollTarget)
    {
        ShowPreviewSectionsSurface();
        int previewLines = ViewModel.PreviewContextLines;
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        RichTextBlock? scrollBlock = null;
        Paragraph? scrollPara = null;

        // Group by file to show file headers
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in selected)
        {
            var key = r.FilePath;
            if (!byFile.TryGetValue(key, out var list))
            {
                list = new List<SearchResult>();
                byFile[key] = list;
            }
            list.Add(r);
        }

        foreach (var (filePath, results) in byFile)
        {
            var section = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)");

            string[]? allLines = null;
            try { allLines = await File.ReadAllLinesAsync(filePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for concatenated preview: {filePath}", ex); }

            foreach (var r in results)
            {
                // Separator between matches in same file
                var sep = new Paragraph();
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var lineChar = '\u2500'; // ─ box-drawing horizontal
                // Use short fixed dividers so the separator never wraps.
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

        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];

        if (scrollBlock is not null && scrollPara is not null)
            ScrollPreviewToLine(scrollBlock, scrollPara);
    }

    private async Task ShowMultiHighlightPreviewAsync(List<SearchResult> selected, SearchResult? scrollTarget)
    {
        ShowPreviewSectionsSurface();
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        RichTextBlock? scrollBlock = null;
        Paragraph? scrollPara = null;

        // Group by file
        var byFile = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in selected)
        {
            var key = r.FilePath;
            if (!byFile.TryGetValue(key, out var list))
            {
                list = new List<SearchResult>();
                byFile[key] = list;
            }
            list.Add(r);
        }

        foreach (var (filePath, results) in byFile)
        {
            var section = AddPreviewSection(filePath, $"{results.Count:N0} selected match(es)");

            // Collect all match line numbers in this file
            var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));

            string[]? allLines = null;
            try { allLines = await File.ReadAllLinesAsync(filePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for multi-highlight preview: {filePath}", ex); }

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
        }

        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];

        if (scrollBlock is not null && scrollPara is not null)
            ScrollPreviewToLine(scrollBlock, scrollPara);
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
            try { allLines = await File.ReadAllLinesAsync(r.FilePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for single-file preview: {r.FilePath}", ex); }
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
        SetPerFileToolbarVisibility(Visibility.Visible);
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
        SetPerFileToolbarVisibility(Visibility.Collapsed);
        // Sections have their own per-section horizontal scroll; outer viewer stays vertical-only.
        PreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
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

    private RichTextBlock AddPreviewSection(string filePath, string? detail = null)
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

        var sectionScroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ViewModel.PreviewWordWrap
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var expander = new Expander
        {
            Header = BuildPreviewSectionHeader(filePath, detail),
            Content = sectionScroller,
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(expander, filePath);
        PreviewSectionsPanel.Children.Add(expander);
        return block;
    }

    private FrameworkElement BuildPreviewSectionHeader(string filePath, string? detail)
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
        ToolTipService.SetToolTip(editorBtn, "Open in configured editor");
        editorBtn.Click += async (_, _) =>
        {
            var result = ViewModel.ResultGroups
                .FirstOrDefault(g => string.Equals(g.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?.FirstOrDefault();
            if (result is not null)
                await ShowFullFileEditorAsync(result);
        };
        buttonPanel.Children.Add(editorBtn);

        grid.Children.Add(buttonPanel);

        ToolTipService.SetToolTip(grid, filePath);
        return grid;
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
        return AddPreviewSection(target.FilePath, detail);
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
        if (PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty)
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
            _suppressPreviewEditorTextChanged = true;
            PreviewEditor.Text = document.Text;
            _suppressPreviewEditorTextChanged = false;
            _previewEditorDirty = false;

            // Archive entries are read-only (cannot save back into zip)
            bool isArchive = ZipArchiveSearcher.IsArchivePath(result.FilePath);
            PreviewEditor.IsReadOnly = isArchive;

            SetPreviewEditorVisible(true);
            PreviewEditor.Focus(FocusState.Programmatic);

            // Scroll to the match line and highlight the matched text.
            ScrollEditorToMatch(document.Text, result);

            string label = isArchive ? "viewing (read-only)" : "editing";
            ViewModel.StatusText = $"Loaded {Path.GetFileName(result.FilePath)} ({FormatBytes(document.ByteLength)}) for {label}.";
        }
        catch (OperationCanceledException)
        {
            ShowPreviewMessage("Full-file load cancelled.");
        }
        catch (PreviewLoadException ex)
        {
            ShowPreviewMessage(ex.Message);
            ViewModel.StatusText = ex.Message;
        }
        catch (OutOfMemoryException ex)
        {
            const string message = "Not enough memory to load this full file into the right-panel editor.";
            LogService.Instance.Warning("Preview", message, ex);
            ShowPreviewMessage(message);
            ViewModel.StatusText = message;
        }
        catch (Exception ex)
        {
            var message = $"Could not load full file: {ex.Message}";
            LogService.Instance.Warning("Preview", $"Could not load full file: {result.FilePath}", ex);
            ShowPreviewMessage(message);
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
            await File.WriteAllTextAsync(_previewEditorPath, PreviewEditor.Text, _previewEditorEncoding).ConfigureAwait(true);
            _previewEditorDirty = false;
            UpdatePreviewEditorButtons();
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

    private async Task<bool> ConfirmDiscardPreviewEditAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Discard unsaved changes?",
            Content = "The right-panel editor has unsaved changes.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep Editing",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        if (_previewEditorDirty)
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
        SavePreviewEditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ClosePreviewEditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApplyWordWrap(ViewModel.PreviewWordWrap);
    }

    private void UpdatePreviewEditorButtons()
    {
        SavePreviewEditButton.IsEnabled = PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && _previewEditorPath is not null;
    }

    private void ShowPreviewMessage(string message)
    {
        SetPreviewEditorVisible(false);
        ShowPreviewBlockSurface();
        PreviewBlock.Blocks.Clear();
        PreviewToolbarContent.Visibility = Visibility.Collapsed;
        var para = new Paragraph();
        para.Inlines.Add(new Run { Text = message });
        PreviewBlock.Blocks.Add(para);
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
                var pointer = targetPara.ContentStart;
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

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        // Minimal placeholder — full splitter implementation would track pointer move.
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
}
