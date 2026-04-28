using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Net.Http;
using System.Security.Principal;
using QuickGrep.Helpers;
using QuickGrep.Models;
using QuickGrep.Services;
using QuickGrep.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace QuickGrep;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private CancellationTokenSource? _debounceCts;
    private bool _suppressPreviewUpdate;
    private bool _autoScrollEnabled = true;
    private DispatcherTimer? _autoScrollTimer;
    private const long FullFilePreviewLimitBytes = 1L * 1024 * 1024 * 1024;
    private CancellationTokenSource? _previewLoadCts;
    private string? _previewEditorPath;
    private Encoding? _previewEditorEncoding;
    private bool _previewEditorDirty;
    private bool _suppressPreviewEditorTextChanged;

    public MainWindow(string? startupDirectory)
    {
        ViewModel = new MainViewModel();
        ViewModel.SetDirectoryFromArgs(startupDirectory);
        InitializeComponent();
        Title = string.IsNullOrEmpty(startupDirectory) ? "QuickGrep" : $"QuickGrep — {startupDirectory}";

        // Extend content into the title bar for a modern Windows 11 look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (!string.IsNullOrEmpty(startupDirectory))
            AppTitleText.Text = $"QuickGrep — {startupDirectory}";

        // Reserve room on the right for system caption buttons (min/max/close)
        // so the gear icon doesn't overlap with them.
        UpdateTitleBarInsets();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange) UpdateTitleBarInsets();
        };

        // Set the window icon (unpackaged WinUI 3 doesn't pick up ApplicationIcon automatically)
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "quickgrep.ico");
        if (File.Exists(icoPath))
            AppWindow.SetIcon(icoPath);

        ViewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.Query) && ViewModel.SearchAsYouType)
            {
                await DebouncedSearchAsync();
            }
        };

        ((FrameworkElement)Content).Loaded += OnContentLoaded;

        // Auto-scroll the file list as new results arrive (timer-based to avoid
        // per-Add overhead on the hot CollectionChanged path).
        ResultsList.PointerWheelChanged += (_, e) =>
        {
            if (e.GetCurrentPoint(ResultsList).Properties.MouseWheelDelta > 0)
                _autoScrollEnabled = false; // user scrolled up — stop auto-scroll
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModel.IsSearching)) return;
            if (ViewModel.IsSearching)
            {
                _autoScrollEnabled = true;
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
            LogService.Instance.Info("MainWindow", "Window closing — flushing logs");
            LogService.Instance.Flush();
        };

        // Show admin banner when not elevated
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
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
            AppTitleBar.Padding = new Thickness(16, 0, rightDip, 0);
        }
        catch { /* AppWindow not always available; ignore */ }
    }

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_autoScrollEnabled || ViewModel.ResultGroups.Count == 0) return;
        ResultsList.ScrollIntoView(ViewModel.ResultGroups[^1]);
    }

    private async Task DebouncedSearchAsync()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
                await ViewModel.StartSearchAsync();
        }
        catch (TaskCanceledException) { /* debounce — expected */ }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
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

        await ViewModel.StartSearchAsync();
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !IsShiftDown())
        {
            e.Handled = true;
            await ViewModel.StartSearchAsync();
        }
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
        dialog.PrimaryButtonClick += (_, _) => ViewModel.PersistSettings();
        _ = dialog.ShowAsync();
    }

    private FrameworkElement BuildSettingsPanel()
    {
        var sp = new StackPanel { Spacing = 8, Width = 480 };

        // ── Search Defaults ──
        sp.Children.Add(new TextBlock { Text = "Search Defaults", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 4, 0, 0) });

        var searchAsYouType = new CheckBox { Content = "Search as you type", IsChecked = ViewModel.SearchAsYouType };
        searchAsYouType.Checked += (_, _) => ViewModel.SearchAsYouType = true;
        searchAsYouType.Unchecked += (_, _) => ViewModel.SearchAsYouType = false;
        sp.Children.Add(searchAsYouType);

        sp.Children.Add(new TextBlock { Text = "Context lines (before & after matches):" });
        var ctx = new NumberBox { Value = ViewModel.ContextLines, Minimum = 0, Maximum = 50 };
        ctx.ValueChanged += (_, args) => ViewModel.ContextLines = (int)args.NewValue;
        sp.Children.Add(ctx);

        sp.Children.Add(new TextBlock { Text = "Preview context lines:" });
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

        sp.Children.Add(new TextBlock { Text = "Max results (0 = unlimited):" });
        var max = new NumberBox { Value = ViewModel.MaxResults, Minimum = 0 };
        max.ValueChanged += (_, args) => ViewModel.MaxResults = (int)args.NewValue;
        sp.Children.Add(max);

        sp.Children.Add(new TextBlock { Text = "Max file size (MB, 0 = unlimited):" });
        var size = new NumberBox { Value = ViewModel.MaxFileSizeBytes / (1024d * 1024d), Minimum = 0 };
        size.ValueChanged += (_, args) => ViewModel.MaxFileSizeBytes = (long)(args.NewValue * 1024 * 1024);
        sp.Children.Add(size);

        sp.Children.Add(new TextBlock { Text = "Skip extensions (semicolon-separated, no dots):", Margin = new Thickness(0, 4, 0, 0) });
        var skipExt = new TextBox { Text = ViewModel.SkipExtensions, PlaceholderText = "e.g. exe;dll;zip;png;pdf", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
        skipExt.TextChanged += (_, _) => ViewModel.SkipExtensions = skipExt.Text;
        sp.Children.Add(skipExt);
        sp.Children.Add(new TextBlock { Text = "Files with these extensions are skipped entirely — no binary check, no content read.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        // ── Performance ──
        sp.Children.Add(new TextBlock { Text = "Performance", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Content-search parallelism:" });
        var parallelism = new ComboBox();
        parallelism.Items.Add($"Auto (safe cap · up to {Math.Min(8, Environment.ProcessorCount)})");
        parallelism.Items.Add("1 thread (sequential, HDD safe)");
        parallelism.Items.Add($"Half cores ({Math.Max(1, Environment.ProcessorCount / 2)})");
        parallelism.Items.Add($"2× cores ({Environment.ProcessorCount * 2}, I/O heavy)");
        parallelism.SelectedIndex = ViewModel.ParallelismIndex;
        parallelism.SelectionChanged += (_, _) => ViewModel.ParallelismIndex = parallelism.SelectedIndex;
        sp.Children.Add(parallelism);

        sp.Children.Add(new TextBlock { Text = "File-listing backend:" });
        var backend = new ComboBox();
        backend.Items.Add("Auto (SDK → es.exe → .NET)");
        backend.Items.Add("Everything SDK only");
        backend.Items.Add("es.exe only");
        backend.Items.Add(".NET enumeration only (no Everything)");
        backend.SelectedIndex = ViewModel.FileListerBackendIndex;
        backend.SelectionChanged += (_, _) => ViewModel.FileListerBackendIndex = backend.SelectedIndex;
        sp.Children.Add(backend);

        sp.Children.Add(new TextBlock { Text = "Process memory hard cap (MB, 0 = auto):" });
        var memLimit = new NumberBox { Value = ViewModel.MemoryLimitMB, Minimum = 0, Maximum = 65536 };
        memLimit.ValueChanged += (_, args) => ViewModel.MemoryLimitMB = (int)args.NewValue;
        sp.Children.Add(memLimit);

        sp.Children.Add(new TextBlock { Text = "System memory pressure limit (%, 0 = disabled):", TextWrapping = TextWrapping.Wrap });
        var memPressure = new NumberBox { Value = ViewModel.MemoryPressurePercent, Minimum = 0, Maximum = 100 };
        memPressure.ValueChanged += (_, args) => ViewModel.MemoryPressurePercent = (int)args.NewValue;
        sp.Children.Add(memPressure);
        sp.Children.Add(new TextBlock { Text = "Search stops when total machine RAM usage exceeds this %.", FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

        // ── Display ──
        sp.Children.Add(new TextBlock { Text = "Display", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        sp.Children.Add(new TextBlock { Text = "Line truncation length (chars):" });
        var trunc = new NumberBox { Value = ViewModel.LineTruncationLength, Minimum = 50, Maximum = 10000 };
        trunc.ValueChanged += (_, args) => ViewModel.LineTruncationLength = (int)args.NewValue;
        sp.Children.Add(trunc);

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

        sp.Children.Add(new TextBlock { Text = "Editor command (use {file} and {line} placeholders):" });
        var editor = new TextBox { Text = ViewModel.EditorCommand };
        editor.TextChanged += (_, _) => ViewModel.EditorCommand = editor.Text;
        sp.Children.Add(editor);

        // ── General ──
        sp.Children.Add(new TextBlock { Text = "General", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });

        var hotkey = new CheckBox { Content = "Enable global hotkey (Ctrl+Shift+G)", IsChecked = ViewModel.GlobalHotkeyEnabled };
        hotkey.Checked += (_, _) => ViewModel.GlobalHotkeyEnabled = true;
        hotkey.Unchecked += (_, _) => ViewModel.GlobalHotkeyEnabled = false;
        sp.Children.Add(hotkey);

        sp.Children.Add(new TextBlock { Text = "Max recent directories / queries:" });
        var recent = new NumberBox { Value = ViewModel.MaxRecentItems, Minimum = 1, Maximum = 100 };
        recent.ValueChanged += (_, args) => ViewModel.MaxRecentItems = (int)args.NewValue;
        sp.Children.Add(recent);

        sp.Children.Add(new TextBlock { Text = "Log level:" });
        var logLevel = new ComboBox();
        logLevel.Items.Add("Critical");
        logLevel.Items.Add("Warning");
        logLevel.Items.Add("Info");
        logLevel.Items.Add("Verbose");
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

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileGroup g && g.Count > 0)
            UpdatePreview(g[0]);
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is FileGroup g && g.Count > 0)
            ViewModel.OpenInEditor(g[0]);
    }

    private void OnMatchLineTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't trigger preview when user clicks the checkbox itself
        if (e.OriginalSource is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton or CheckBox)
            return;

        if (sender is FrameworkElement fe && fe.DataContext is SearchResult r)
        {
            var selected = ViewModel.GetAllSelectedResults();
            if (selected.Count >= 2)
                UpdateMultiSelectPreview();
            else
                UpdatePreview(r);
        }
    }

    private async void OnShowFullFile(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        await ShowFullFileEditorAsync(_previewResult);
    }

    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        ViewModel.PersistSettings();
    }

    private void ApplyWordWrap(bool wrap)
    {
        PreviewBlock.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PreviewEditor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PreviewScrollViewer.HorizontalScrollBarVisibility =
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
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
        if (_previewResult is not null)
            ShowSingleFilePreview(_previewResult, fullFile: false);
    }

    private void OnPreviewEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPreviewEditorTextChanged) return;
        if (PreviewEditor.Visibility != Visibility.Visible) return;
        _previewEditorDirty = true;
        UpdatePreviewEditorButtons();
    }

    private async Task<bool> ClearPreviewPanelForNewSearchAsync()
    {
        if (PreviewEditor.Visibility == Visibility.Visible && _previewEditorDirty && !await ConfirmDiscardPreviewEditAsync())
            return false;

        _previewResult = null;
        SetPreviewFileLabel(string.Empty);
        ClosePreviewEditor();
        PreviewBlock.Blocks.Clear();
        FullFileButton.IsEnabled = false;
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

    private void OnOpenInEditor(object sender, RoutedEventArgs e)
    {
        if (_previewResult is null) return;
        ViewModel.OpenInEditor(_previewResult);
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

    private void OnSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.SelectAll();
            _suppressPreviewUpdate = false;
            UpdateMultiSelectPreview();
        }
    }

    private void OnSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileGroup g)
        {
            _suppressPreviewUpdate = true;
            g.DeselectAll();
            _suppressPreviewUpdate = false;
            UpdateMultiSelectPreview();
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
        picker.SuggestedFileName = "QuickGrep_Export";

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

    private void UpdatePreview(SearchResult r)
    {
        if (!TryLeavePreviewEditorForPreviewChange()) return;

        // Hydrate from disk if this result was evicted during memory pressure.
        ViewModel.HydrateResult(r);
        _previewResult = r;
        SetPreviewFileLabel(r.FilePath);
        ShowSingleFilePreview(r, fullFile: false);
    }

    private void UpdateMultiSelectPreview()
    {
        if (!TryLeavePreviewEditorForPreviewChange()) return;

        var selected = ViewModel.GetAllSelectedResults();
        if (selected.Count == 0) return;

        // Hydrate any evicted results before rendering the preview.
        foreach (var r in selected)
            ViewModel.HydrateResult(r);

        if (ViewModel.PreviewModeIndex == 1)
            ShowMultiHighlightPreview(selected);
        else
            ShowConcatenatedPreview(selected);
    }

    private void ShowConcatenatedPreview(List<SearchResult> selected)
    {
        PreviewBlock.Blocks.Clear();
        int previewLines = ViewModel.PreviewContextLines;
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

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

        bool firstFile = true;
        foreach (var (filePath, results) in byFile)
        {
            // File header
            if (!firstFile)
            {
                var spacer = new Paragraph();
                spacer.Inlines.Add(new Run { Text = " " });
                PreviewBlock.Blocks.Add(spacer);
            }
            firstFile = false;

            var header = new Paragraph();
            var headerRun = new Run { Text = $"── {Path.GetFileName(filePath)} ──" };
            headerRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue);
            headerRun.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            header.Inlines.Add(headerRun);
            PreviewBlock.Blocks.Add(header);

            var pathPara = new Paragraph();
            var pathRun = new Run { Text = filePath, FontSize = 11 };
            pathRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            pathPara.Inlines.Add(pathRun);
            PreviewBlock.Blocks.Add(pathPara);

            string[]? allLines = null;
            try { allLines = File.ReadAllLines(filePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for concatenated preview: {filePath}", ex); }

            foreach (var r in results)
            {
                // Separator between matches in same file
                var sep = new Paragraph();
                var label = $" Line {r.LineNumber} ";
                var lineChar = '\u2500'; // ─ box-drawing horizontal
                var (left, right) = ComputeDividerWidths(label.Length);
                var sepRun = new Run { Text = $"{new string(lineChar, left)}{label}{new string(lineChar, right)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                sep.Margin = new Thickness(0, 8, 0, 4);
                PreviewBlock.Blocks.Add(sep);

                var lines = GetPreviewLines(r, allLines, previewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    bool isMatchLine = lineNum == r.LineNumber;
                    var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx);
                    PreviewBlock.Blocks.Add(para);
                }
            }
        }

        SetPreviewFileLabel(
            selected.Count == 1
                ? selected[0].FilePath
                : $"{selected.Count} selected matches across {byFile.Count} file(s)",
            selected.Count == 1 ? selected[0].FilePath : string.Join(Environment.NewLine, byFile.Keys));
        _previewResult = selected[0];
    }

    private void ShowMultiHighlightPreview(List<SearchResult> selected)
    {
        PreviewBlock.Blocks.Clear();
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

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

        bool firstFile = true;
        foreach (var (filePath, results) in byFile)
        {
            if (!firstFile)
            {
                var spacer = new Paragraph();
                spacer.Inlines.Add(new Run { Text = " " });
                PreviewBlock.Blocks.Add(spacer);
            }
            firstFile = false;

            // File header
            var header = new Paragraph();
            var headerRun = new Run { Text = $"── {Path.GetFileName(filePath)} ──" };
            headerRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue);
            headerRun.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            header.Inlines.Add(headerRun);
            PreviewBlock.Blocks.Add(header);

            var pathPara = new Paragraph();
            var pathRun = new Run { Text = filePath, FontSize = 11 };
            pathRun.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);
            pathPara.Inlines.Add(pathRun);
            PreviewBlock.Blocks.Add(pathPara);

            // Collect all match line numbers in this file
            var matchLines = new HashSet<int>(results.Select(r => r.LineNumber));

            string[]? allLines = null;
            try { allLines = File.ReadAllLines(filePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for multi-highlight preview: {filePath}", ex); }

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
                        PreviewBlock.Blocks.Add(gap);
                    }
                    firstRange = false;

                    for (int i = start; i <= end; i++)
                    {
                        int lineNum = i + 1;
                        bool isMatchLine = matchLines.Contains(lineNum);
                        // Use first matching result for this line (for column-based highlighting)
                        var matchResult = results.FirstOrDefault(r => r.LineNumber == lineNum) ?? results[0];
                        var para = MakePreviewParagraph(allLines[i], lineNum, isMatchLine, matchResult, rx);
                        PreviewBlock.Blocks.Add(para);
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
                        PreviewBlock.Blocks.Add(para);
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

    private void ShowSingleFilePreview(SearchResult r, bool fullFile)
    {
        PreviewBlock.Blocks.Clear();
        Regex? rx = BuildHighlightRegex(ViewModel.Query, ViewModel.CaseSensitive, ViewModel.UseRegex);

        string[]? allLines = null;
        if (fullFile)
        {
            try { allLines = File.ReadAllLines(r.FilePath); } catch (Exception ex) { LogService.Instance.Verbose("Preview", $"Cannot read file for single-file preview: {r.FilePath}", ex); }
        }

        var lines = GetPreviewLines(r, allLines, ViewModel.PreviewContextLines, fullFile);
        foreach (var (line, lineNum) in lines)
        {
            bool isMatchLine = lineNum == r.LineNumber;
            var para = MakePreviewParagraph(line, lineNum, isMatchLine, r, rx, truncate: !fullFile);
            PreviewBlock.Blocks.Add(para);
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

            SetPreviewEditorVisible(true);
            PreviewEditor.Focus(FocusState.Programmatic);
            ViewModel.StatusText = $"Loaded {Path.GetFileName(result.FilePath)} ({FormatBytes(document.ByteLength)}) for editing.";
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

    private static async Task<PreviewTextDocument> LoadPreviewDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
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
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return new PreviewTextDocument(text, reader.CurrentEncoding, info.Length);
        }
        catch (PreviewLoadException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new PreviewLoadException($"Could not load full file: access denied. {ex.Message}"); }
        catch (DecoderFallbackException ex) { throw new PreviewLoadException($"Could not load full file: unsupported text encoding. {ex.Message}"); }
        catch (IOException ex) { throw new PreviewLoadException($"Could not load full file: {ex.Message}"); }
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
        PreviewBlock.Blocks.Clear();
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

    /// <summary>
    /// Computes left/right divider widths. When word wrap is on, sizes to the viewport
    /// so the divider doesn't overflow. Otherwise uses generous fixed widths.
    /// </summary>
    private (int left, int right) ComputeDividerWidths(int labelLength)
    {
        const int fixedLeft = 20;
        const int fixedRight = 120;

        if (!ViewModel.PreviewWordWrap)
            return (fixedLeft, fixedRight);

        // Estimate how many monospace characters fit in the viewport.
        // Consolas at default size (~13.333px) with padding subtracted.
        double viewportWidth = PreviewScrollViewer.ActualWidth;
        if (viewportWidth <= 0) return (fixedLeft, fixedRight);

        // Subtract horizontal padding (16 each side from ScrollViewer Padding)
        viewportWidth -= 32;

        // Approximate character width for Consolas at the default font size.
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        double fontSize = PreviewBlock.FontSize > 0 ? PreviewBlock.FontSize : 13.333;
        double charWidth = fontSize * 0.6; // Consolas is roughly 0.6× em-width
        int totalChars = Math.Max(20, (int)(viewportWidth / charWidth));

        int available = totalChars - labelLength;
        if (available <= 4) return (2, 2);

        int left = available / 4;
        int right = available - left;
        return (left, right);
    }

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

        // Highlight matches in this line
        if (rx != null)
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

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        // Minimal placeholder — full splitter implementation would track pointer move.
    }

    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnContentLoaded;
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        await CheckEverythingAsync();
        FocusSearchBox();
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
                        // Give it a moment to start
                        await Task.Delay(2000);
                        ViewModel.StatusText = Process.GetProcessesByName("Everything").Length > 0
                            ? "Everything Search started \u2014 fast file discovery enabled."
                            : "Everything Search is starting\u2026";
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

            ViewModel.StatusText = FileLister.FindEsExe() != null
                ? "Everything Search installed \u2014 fast file discovery enabled."
                : "Installer completed. Restart QuickGrep if Everything was installed to a custom location.";
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
}
