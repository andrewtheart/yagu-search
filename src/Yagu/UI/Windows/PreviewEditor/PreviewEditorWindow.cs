using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Immutable state handed to a <see cref="PreviewEditorWindow"/> when the user pops the
/// built-in editor out of the main window. The pop-out TRANSFERS the already-loaded editor
/// state (no re-load pipeline) so unsaved edits move with it.
/// </summary>
internal sealed record PreviewEditorWindowContext
{
    public required IntPtr OwnerHwnd { get; init; }

    /// <summary>Effective (resolved) theme — never <see cref="ElementTheme.Default"/>.</summary>
    public required ElementTheme Theme { get; init; }

    public required string FilePath { get; init; }

    /// <summary>Current editor text (possibly edited) that the window opens with.</summary>
    public required string Text { get; init; }

    /// <summary>On-disk baseline used for dirty comparison (the originally-loaded text).</summary>
    public required string DiskText { get; init; }

    public required Encoding Encoding { get; init; }
    public required bool WordWrap { get; init; }
    public required int ZoomFactor { get; init; }
    public required string FontFamily { get; init; }
    public required int FontSize { get; init; }
    public required Windows.UI.Color TextColor { get; init; }
    public required Windows.UI.Color GutterColor { get; init; }
    public required TextControlBoxNS.SyntaxHighlightID SyntaxHighlightId { get; init; }
    public required bool ShowLineNumbers { get; init; }
    public required bool ShowLineHighlighter { get; init; }
    public required bool BackupBeforeSave { get; init; }

    /// <summary>When true the window opens as a read-only PREVIEW (with an "Edit file" button that
    /// unlocks editing in the same window). When false it opens directly in editable mode.</summary>
    public bool StartReadOnly { get; init; }

    /// <summary>1-based line to center on after load (0 = none). Used to jump to the first match when
    /// a preview drawer is popped out.</summary>
    public int ScrollToLine { get; init; }

    /// <summary>Literal search term to highlight after load (null/empty = none). Only applied for
    /// non-regex searches; regex previews just scroll to <see cref="ScrollToLine"/>.</summary>
    public string? HighlightWord { get; init; }

    public bool HighlightWholeWord { get; init; }
    public bool HighlightMatchCase { get; init; }

    /// <summary>How this and sibling pop-out windows auto-arrange on screen.</summary>
    public Yagu.Helpers.PopOutArrangement Arrangement { get; init; } = Yagu.Helpers.PopOutArrangement.Grid;
}

/// <summary>
/// A popped-out, fully-independent single-file editor window. Unlike the in-pane preview editor,
/// this window has NO connection to the main window's global match paginator / match-navigation
/// state — it owns its own <see cref="TextControlBoxNS.TextControlBox"/>, dirty tracking, save,
/// word-wrap, and zoom. One or more may be open at a time.
/// </summary>
/// <remarks>
/// This is a real resizable/maximizable tool window (a "full window"), so — unlike Yagu's modals —
/// it intentionally keeps the standard OS title bar for move/resize/maximize/close. The
/// modal-no-title-bar convention targets modal dialogs, not persistent tool windows.
/// </remarks>
internal sealed class PreviewEditorWindow : Window
{
    private const int ZoomMin = 30;
    private const int ZoomMax = 400;
    private const int ZoomStep = 10;
    private const int SavedOverlayDurationMs = 700;

    private static readonly List<PreviewEditorWindow> OpenWindows = new();

    private readonly PreviewEditorWindowContext _context;
    private readonly IntPtr _ownerHwnd;
    private readonly PopOutArrangement _arrangement;
    private readonly TextControlBoxNS.TextControlBox _editor;
    private readonly TextBlock _pathText;
    private readonly TextBlock _dirtyAsterisk;
    private readonly Button _saveButton;
    private readonly Button _editButton;
    private readonly ToggleButton _wrapToggle;
    private readonly Border _savedOverlay;

    private Encoding _encoding;
    private string _originalText;
    private bool _dirty;
    private bool _readOnly;
    private bool _suppressTextChanged;
    private bool _editorInitialized;
    private bool _forceClose;
    private IntPtr _hwnd;
    private AppWindow? _appWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _savedOverlayTimer;

    private PreviewEditorWindow(PreviewEditorWindowContext context)
    {
        _context = context;
        _ownerHwnd = context.OwnerHwnd;
        _arrangement = context.Arrangement;
        _encoding = context.Encoding;
        _originalText = context.DiskText;
        _readOnly = context.StartReadOnly;

        _editor = new TextControlBoxNS.TextControlBox
        {
            FontFamily = SafeFontFamily(context.FontFamily),
            FontSize = context.FontSize,
            ShowLineNumbers = context.ShowLineNumbers,
            ShowLineHighlighter = context.ShowLineHighlighter,
            EnableSyntaxHighlighting = context.SyntaxHighlightId != TextControlBoxNS.SyntaxHighlightID.None,
        };

        var root = BuildContent(out _pathText, out _dirtyAsterisk, out _saveButton, out _editButton, out _wrapToggle, out _savedOverlay);

        Title = BuildTitle(context.FilePath);
        Content = root;
        Closed += OnClosed;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // NOTE: deliberately NOT owned (no GWL_HWNDPARENT to the main window). A popped-out editor is
        // an independent top-level window so it survives the main window minimizing / hiding to tray
        // and can live on a second monitor. (An owned window is hidden by Windows whenever its owner
        // hides — which made the pop-out silently vanish in minimize-to-tray focus mode.)
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = Title;

        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);
        }
        catch { }

        try { ThemeStandardTitleBar(context.Theme); }
        catch { }

        _appWindow.Closing += OnAppWindowClosing;

        // Initial size + placement. Tiling modes are re-applied by ArrangeOpenWindows in Open(); this
        // just gives a sensible first position (and is the final position for Cascade / Manual modes).
        WindowForegroundHelper.CenterWindowOverOwner(_appWindow, _ownerHwnd, 1000, 760, minWidth: 480, minHeight: 320);
        if (_arrangement == PopOutArrangement.Cascade)
            OffsetForCascade();

        // Apply the editor's own state (wrap/zoom/syntax) then load its text once the control is in
        // the visual tree. Doing this in the constructor — before the TextControlBox has a render
        // context — can fault; the main window likewise applies these at runtime, not at construction.
        _dirty = !string.Equals(context.Text, context.DiskText, StringComparison.Ordinal);
        _editor.Loaded += OnEditorLoaded;

        _wrapToggle.IsChecked = context.WordWrap;
        UpdateModeUi();
        UpdateDirtyUi();
        WireAccelerators();
    }

    private void OnEditorLoaded(TextControlBoxNS.TextControlBox sender)
    {
        if (_editorInitialized)
            return;
        _editorInitialized = true;

        _editor.LineNumberColor = _context.GutterColor;
        _editor.TextColor = _context.TextColor;
        _editor.WordWrap = _context.WordWrap;
        if (_context.SyntaxHighlightId != TextControlBoxNS.SyntaxHighlightID.None)
        {
            try { _editor.SelectSyntaxHighlightingById(_context.SyntaxHighlightId); }
            catch { }
        }

        _suppressTextChanged = true;
        _editor.LoadText(_context.Text, autodetectTabsSpaces: false);
        _editor.ClearUndoRedoHistory();
        _suppressTextChanged = false;

        _editor.IsReadOnly = _readOnly;

        try { _editor.ZoomFactor = ClampZoom(_context.ZoomFactor); } catch { }

        // Highlight the search term (literal searches only) and jump to the first match so a popped-out
        // preview drawer lands the user on the match, just like the in-app drawer does.
        if (!string.IsNullOrEmpty(_context.HighlightWord))
        {
            try { _editor.BeginSearch(_context.HighlightWord, _context.HighlightWholeWord, _context.HighlightMatchCase); }
            catch { }
        }
        if (_context.ScrollToLine > 0)
        {
            try { _editor.ScrollLineToCenter(_context.ScrollToLine - 1); }
            catch { }
        }

        // Subscribe AFTER the load so the initial LoadText does not flip the dirty flag (the flag was
        // already seeded from the transferred edit state above).
        _editor.TextChanged += OnEditorTextChanged;
        try { _editor.Focus(FocusState.Programmatic); } catch { }
    }

    /// <summary>Opens a new independent editor window for the given transferred state.</summary>
    public static PreviewEditorWindow Open(PreviewEditorWindowContext context)
    {
        LogService.Instance.Info("PreviewEditorWindow", $"Opening pop-out editor for '{context.FilePath}' (open before={OpenWindows.Count}).");
        var window = new PreviewEditorWindow(context);
        OpenWindows.Add(window);
        window.Activate();
        window.BringToFront();
        ArrangeOpenWindows(context.OwnerHwnd, context.Arrangement);
        LogService.Instance.Info("PreviewEditorWindow", $"Pop-out editor activated (open now={OpenWindows.Count}).");
        return window;
    }

    /// <summary>
    /// Auto-tiles every open pop-out window that shares <paramref name="ownerHwnd"/> into the owner
    /// monitor's work area per <paramref name="mode"/>. No-op for Cascade/Manual modes (windows keep
    /// their own positions). Called when a window opens or closes so the grid reflows.
    /// </summary>
    private static void ArrangeOpenWindows(IntPtr ownerHwnd, PopOutArrangement mode)
    {
        if (!PopOutTileLayout.IsTiling(mode))
            return;

        var windows = OpenWindows.Where(w => w._ownerHwnd == ownerHwnd && w._appWindow is not null).ToList();
        if (windows.Count == 0)
            return;

        if (!WindowForegroundHelper.TryGetWorkArea(ownerHwnd, out int wx, out int wy, out int ww, out int wh))
            return;

        var tiles = PopOutTileLayout.Compute(windows.Count, mode, new TileRect(wx, wy, ww, wh));
        for (int i = 0; i < windows.Count && i < tiles.Length; i++)
        {
            var t = tiles[i];
            try
            {
                windows[i]._appWindow!.MoveAndResize(new Windows.Graphics.RectInt32(t.X, t.Y, t.Width, t.Height));
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("PreviewEditorWindow", $"Tile move failed: {ex.Message}");
            }
        }
    }

    private void BringToFront()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        try
        {
            _ = ShowWindow(_hwnd, SwShow);
            _ = SetForegroundWindow(_hwnd);
        }
        catch { }
    }

    /// <summary>True when at least one pop-out editor opened from <paramref name="ownerHwnd"/> is open.
    /// Used by the main window to keep itself visible (not hide to tray) while a pop-out is up.</summary>
    public static bool HasOpenOwnedWindow(IntPtr ownerHwnd)
    {
        foreach (var window in OpenWindows)
        {
            if (window._ownerHwnd == ownerHwnd)
                return true;
        }
        return false;
    }

    /// <summary>Number of pop-out editor windows currently open (test/diagnostic hook).</summary>
    public static int OpenWindowCount => OpenWindows.Count;

    private Grid BuildContent(
        out TextBlock pathText,
        out TextBlock dirtyAsterisk,
        out Button saveButton,
        out Button editButton,
        out ToggleButton wrapToggle,
        out Border savedOverlay)
    {
        var root = new Grid();
        AppThemeService.ApplyThemedDialogSurface(root, _context.Theme);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Path bar (row 0) ----------------------------------------------------------------
        var pathBar = new Border
        {
            Padding = new Thickness(12, 7, 12, 7),
            MinHeight = 50,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var stroke) && stroke is Brush strokeBrush)
            pathBar.BorderBrush = strokeBrush;

        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fileIcon = new FontIcon { Glyph = "\uE7C3", FontSize = 14, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fileIcon, 0);
        bar.Children.Add(fileIcon);

        var pathLine = new Grid { VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 3 };
        pathLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathText = new TextBlock
        {
            Text = _context.FilePath,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(pathText, _context.FilePath);
        Grid.SetColumn(pathText, 0);
        pathLine.Children.Add(pathText);
        dirtyAsterisk = new TextBlock
        {
            Text = "*",
            Visibility = Visibility.Collapsed,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xA1, 0x00)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(dirtyAsterisk, "Unsaved changes");
        Grid.SetColumn(dirtyAsterisk, 1);
        pathLine.Children.Add(dirtyAsterisk);
        Grid.SetColumn(pathLine, 1);
        bar.Children.Add(pathLine);

        // Toolbar: Copy path, Word-wrap toggle, Save.
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(toolbar, 2);

        var copyPathButton = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
        };
        ToolTipService.SetToolTip(copyPathButton, "Copy the file path");
        copyPathButton.Click += (_, _) => CopyPathToClipboard();
        toolbar.Children.Add(copyPathButton);

        wrapToggle = new ToggleButton
        {
            Width = 28,
            Height = 28,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8B3", FontSize = 12 },
        };
        ToolTipService.SetToolTip(wrapToggle, "Toggle word wrap");
        wrapToggle.Click += (_, _) => ApplyWordWrap(_wrapToggle.IsChecked == true);
        toolbar.Children.Add(wrapToggle);

        editButton = new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };
        var editContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        editContent.Children.Add(new FontIcon { Glyph = "\uE70F", FontSize = 12 });
        editContent.Children.Add(new TextBlock { Text = "Edit file" });
        editButton.Content = editContent;
        ToolTipService.SetToolTip(editButton, "Unlock editing in this window");
        editButton.Click += (_, _) => EnterEditMode();
        toolbar.Children.Add(editButton);

        saveButton = new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            IsEnabled = false,
        };
        var saveContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        saveContent.Children.Add(new FontIcon { Glyph = "\uE74E", FontSize = 12 });
        saveContent.Children.Add(new TextBlock { Text = "Save" });
        saveButton.Content = saveContent;
        ToolTipService.SetToolTip(saveButton, "Save changes to the file (Ctrl+S)");
        saveButton.Click += async (_, _) => await SaveAsync();
        toolbar.Children.Add(saveButton);

        bar.Children.Add(toolbar);
        pathBar.Child = bar;
        Grid.SetRow(pathBar, 0);
        root.Children.Add(pathBar);

        // ---- Editor + saved toast (row 1) ---------------------------------------------------
        var editorHost = new Grid();
        editorHost.Children.Add(_editor);

        savedOverlay = new Border
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x18, 0x18, 0x18)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0x22, 0xC5, 0x5E)),
            BorderThickness = new Thickness(1),
        };
        Canvas.SetZIndex(savedOverlay, 10);
        var savedContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        savedContent.Children.Add(new FontIcon { Glyph = "\uE8FB", FontSize = 14, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)) });
        savedContent.Children.Add(new TextBlock { Text = "Saved", Foreground = new SolidColorBrush(Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        savedOverlay.Child = savedContent;
        editorHost.Children.Add(savedOverlay);

        Grid.SetRow(editorHost, 1);
        root.Children.Add(editorHost);

        return root;
    }

    private void WireAccelerators()
    {
        _editor.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, async () =>
        {
            if (_dirty && !_readOnly) await SaveAsync();
        });
        AddAccelerator((VirtualKey)187 /* OemPlus */, VirtualKeyModifiers.Control, () => AdjustZoom(+ZoomStep));
        AddAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () => AdjustZoom(+ZoomStep));
        AddAccelerator((VirtualKey)189 /* OemMinus */, VirtualKeyModifiers.Control, () => AdjustZoom(-ZoomStep));
        AddAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => AdjustZoom(-ZoomStep));
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetZoom(100));
        AddAccelerator(VirtualKey.NumberPad0, VirtualKeyModifiers.Control, () => SetZoom(100));
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => { args.Handled = true; action(); };
        _editor.KeyboardAccelerators.Add(accelerator);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, args) => { args.Handled = true; await action(); };
        _editor.KeyboardAccelerators.Add(accelerator);
    }

    private void OnEditorTextChanged(TextControlBoxNS.TextControlBox sender)
    {
        if (_suppressTextChanged) return;
        HideSavedOverlay();
        _dirty = true;
        UpdateDirtyUi();
    }

    private void ApplyWordWrap(bool wrap)
    {
        _editor.WordWrap = wrap;
        if (wrap)
            _editor.HorizontalScroll = 0;
        _editor.UpdateLayout();
    }

    private void AdjustZoom(int delta) => SetZoom(_editor.ZoomFactor + delta);

    private void SetZoom(int percent)
    {
        try { _editor.ZoomFactor = ClampZoom(percent); } catch { }
    }

    private static int ClampZoom(int percent) => Math.Clamp(percent, ZoomMin, ZoomMax);

    private void CopyPathToClipboard()
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(_context.FilePath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditorWindow", $"CopyPath failed: {ex.Message}");
        }
    }

    private bool HasRealChanges() =>
        _dirty && !string.Equals(_editor.GetText(), _originalText, StringComparison.Ordinal);

    private async Task<bool> SaveAsync()
    {
        _saveButton.IsEnabled = false;
        try
        {
            if (_context.BackupBeforeSave && File.Exists(_context.FilePath))
                WriteBackup(_context.FilePath);

            var textToSave = _editor.GetText();
            if (EditorEncodingHelper.HasUnencodableCharacters(textToSave, _encoding))
            {
                var choice = await YaguDialog.ShowAsync(
                    _hwnd,
                    new YaguDialogOptions
                    {
                        Title = "Encoding Warning",
                        TitleGlyph = "\uE7BA", // Warning
                        Content = "This file contains characters that cannot be represented in its original encoding. Save as UTF-8 instead?",
                        PrimaryButtonText = "Save as UTF-8",
                        CloseButtonText = "Cancel",
                        DefaultButton = YaguDialogDefaultButton.Primary,
                        Width = 560,
                        Height = 280,
                        ShowTitleBar = false,
                    });
                if (choice != YaguDialogResult.Primary)
                    return false;
                _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            await File.WriteAllTextAsync(_context.FilePath, textToSave, _encoding).ConfigureAwait(true);
            _originalText = textToSave;
            _dirty = false;
            UpdateDirtyUi();
            ShowSavedOverlay();
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("PreviewEditorWindow", $"Save failed: {_context.FilePath}", ex);
            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Could not save",
                    TitleGlyph = "\uE783", // Error
                    Content = $"Could not save file: {ex.Message}",
                    CloseButtonText = "OK",
                    Width = 520,
                    Height = 240,
                    ShowTitleBar = false,
                });
            return false;
        }
        finally
        {
            _saveButton.IsEnabled = _dirty && !_readOnly;
        }
    }

    private static void WriteBackup(string path)
    {
        var bakPath = path + ".yagubak";
        if (!File.Exists(bakPath))
        {
            File.Copy(path, bakPath, overwrite: false);
            return;
        }
        int suffix = 2;
        while (File.Exists($"{path}.yagubak-{suffix}"))
            suffix++;
        File.Copy(path, $"{path}.yagubak-{suffix}", overwrite: false);
    }

    private void UpdateDirtyUi()
    {
        _saveButton.IsEnabled = _dirty && !_readOnly;
        _dirtyAsterisk.Visibility = _dirty && !_readOnly ? Visibility.Visible : Visibility.Collapsed;
        if (_appWindow is not null)
            _appWindow.Title = (_dirty && !_readOnly ? "* " : string.Empty) + Title;
    }

    /// <summary>Reflects read-only (preview) vs. editable mode in the toolbar: preview shows an
    /// "Edit file" button; editable shows the Save button.</summary>
    private void UpdateModeUi()
    {
        _editButton.Visibility = _readOnly ? Visibility.Visible : Visibility.Collapsed;
        _saveButton.Visibility = _readOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Unlocks editing in the same window (the "Edit file" action on a popped-out preview).</summary>
    private void EnterEditMode()
    {
        if (!_readOnly)
            return;
        _readOnly = false;
        try { _editor.IsReadOnly = false; } catch { }
        UpdateModeUi();
        UpdateDirtyUi();
        try { _editor.Focus(FocusState.Programmatic); } catch { }
    }

    private void ShowSavedOverlay()
    {
        _savedOverlay.Visibility = Visibility.Visible;
        _savedOverlayTimer ??= DispatcherQueue.CreateTimer();
        _savedOverlayTimer.Interval = TimeSpan.FromMilliseconds(SavedOverlayDurationMs);
        _savedOverlayTimer.IsRepeating = false;
        _savedOverlayTimer.Tick -= OnSavedOverlayTick;
        _savedOverlayTimer.Tick += OnSavedOverlayTick;
        _savedOverlayTimer.Start();
    }

    private void OnSavedOverlayTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => HideSavedOverlay();

    private void HideSavedOverlay()
    {
        _savedOverlayTimer?.Stop();
        if (_savedOverlay is not null)
            _savedOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !HasRealChanges())
            return;

        args.Cancel = true;
        var choice = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Unsaved changes",
                TitleGlyph = "\uE74E", // Save
                Content = "This editor window has unsaved changes.",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 500,
                Height = 270,
                ShowTitleBar = false,
            });

        bool proceed = choice switch
        {
            YaguDialogResult.Primary => await SaveAsync(),
            YaguDialogResult.Secondary => true,
            _ => false,
        };

        if (proceed)
        {
            _forceClose = true;
            Close();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OpenWindows.Remove(this);
        _savedOverlayTimer?.Stop();
        try { _editor.TextChanged -= OnEditorTextChanged; } catch { }
        if (_appWindow is not null)
            _appWindow.Closing -= OnAppWindowClosing;

        // Reflow the remaining tiled windows so the grid closes the gap this window left.
        ArrangeOpenWindows(_ownerHwnd, _arrangement);
    }

    /// <summary>
    /// Themes the STANDARD OS title bar for this (non-extended) window. Unlike Yagu's extended
    /// title-bar windows — which set the caption-button background to <c>Transparent</c> so the dark
    /// content shows through — this window has nothing behind the caption strip, so a transparent
    /// button background renders as the system default (a white bar). We therefore paint the caption
    /// buttons with a SOLID background matching the title bar.
    /// </summary>
    private void ThemeStandardTitleBar(ElementTheme theme)
    {
        if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
            return;

        var titleBar = _appWindow.TitleBar;
        bool dark = theme != ElementTheme.Light;

        var background = dark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        var foreground = dark ? Colors.White : Colors.Black;
        var hover = dark
            ? ColorHelper.FromArgb(0xFF, 0x3A, 0x3A, 0x3A)
            : ColorHelper.FromArgb(0xFF, 0xE5, 0xE5, 0xE5);
        var pressed = dark
            ? ColorHelper.FromArgb(0xFF, 0x30, 0x30, 0x30)
            : ColorHelper.FromArgb(0xFF, 0xDA, 0xDA, 0xDA);

        titleBar.BackgroundColor = background;
        titleBar.InactiveBackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = foreground;

        // SOLID (not Transparent) so the caption-button strip matches the title bar instead of
        // showing the system default white.
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static string BuildTitle(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return string.IsNullOrEmpty(name) ? "Yagu Editor" : $"{name} — Yagu";
    }

    private static FontFamily SafeFontFamily(string family)
    {
        try
        {
            return string.IsNullOrWhiteSpace(family) ? new FontFamily("Consolas") : new FontFamily(family);
        }
        catch
        {
            return new FontFamily("Consolas");
        }
    }

    private void OffsetForCascade()
    {
        if (_appWindow is null)
            return;
        int step = (OpenWindows.Count % 6) * 28;
        if (step == 0)
            return;
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        _appWindow.Move(new Windows.Graphics.PointInt32(pos.X + step, pos.Y + step));
        _ = size;
    }

    private const int SwShow = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
