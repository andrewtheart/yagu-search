using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Yagu;

/// <summary>What the tray menu asks the app to do once the user picks an entry.</summary>
internal sealed class TrayMenuActions
{
    public Action? OpenReset { get; init; }
    public Action? OpenExisting { get; init; }
    public Action? CloseApp { get; init; }

    /// <summary>Runs the inline quick search against this running instance.</summary>
    public Action<TrayQuickSearchRequest>? RunQuickSearch { get; init; }

    /// <summary>Seeds the inline quick-search fields from the app's current search state.</summary>
    public Func<TrayQuickSearchRequest>? ReadCurrentSearch { get; init; }
}

/// <summary>The search the tray quick-search panel describes: scope, query, and every option it exposes.</summary>
internal sealed record TrayQuickSearchRequest(
    string Directory,
    string Query,
    bool UseRegex,
    bool CaseSensitive,
    bool Multiline,
    bool ExactMatch,
    bool Semantic);

/// <summary>
/// The Yagu-styled system-tray context menu. Replaces the Win32 <c>TrackPopupMenu</c> so the menu matches
/// the app's theme and can host the inline <b>Quick search</b> panel — choosing that entry expands the
/// panel in place instead of closing the menu, so scope, query and options can all be set before running.
/// Borderless and title-bar-less per the app's modal convention; dismisses on deactivation or Esc.
/// </summary>
internal sealed partial class TrayMenuWindow : Window
{
    private const int MenuWidthDip = 300;

    private static TrayMenuWindow? s_open;

    private readonly TrayMenuActions _actions;
    private readonly AppWindow _appWindow;
    private readonly Grid _root;
    private readonly StackPanel _quickSearchPanel;
    private readonly TextBox _directoryBox;
    private readonly TextBox _queryBox;
    private readonly ComboBox _modeBox;
    private readonly CheckBox _regexBox;
    private readonly CheckBox _caseBox;
    private readonly CheckBox _multilineBox;
    private readonly CheckBox _exactBox;
    private readonly int _anchorX;
    private readonly int _anchorY;

    private bool _closing;

    /// <summary>Shows the menu with its bottom-right corner anchored near the tray cursor position.</summary>
    public static void ShowAt(int cursorX, int cursorY, ElementTheme theme, TrayMenuActions actions)
    {
        DismissOpen();
        var window = new TrayMenuWindow(cursorX, cursorY, theme, actions);
        s_open = window;
        window.Activate();
    }

    /// <summary>Closes the menu if one is open (used before showing another, and on app teardown).</summary>
    public static void DismissOpen()
    {
        var open = s_open;
        s_open = null;
        try { open?.Close(); }
        catch { }
    }

    private TrayMenuWindow(int cursorX, int cursorY, ElementTheme theme, TrayMenuActions actions)
    {
        _actions = actions;
        _anchorX = cursorX;
        _anchorY = cursorY;
        Title = "Yagu";

        _directoryBox = new TextBox { PlaceholderText = "Leave blank to search all drives" };
        _queryBox = new TextBox { PlaceholderText = "Search pattern" };
        _modeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _modeBox.Items.Add(new ComboBoxItem { Content = "Traditional" });
        _modeBox.Items.Add(new ComboBoxItem { Content = "Semantic" });
        _modeBox.SelectedIndex = 0;
        _regexBox = new CheckBox { Content = "Regex", MinWidth = 0 };
        _caseBox = new CheckBox { Content = "Case", MinWidth = 0 };
        _multilineBox = new CheckBox { Content = "Multiline", MinWidth = 0 };
        _exactBox = new CheckBox { Content = "Exact", MinWidth = 0 };
        _quickSearchPanel = BuildQuickSearchPanel();

        _root = BuildRoot(theme);
        Content = _root;

        // This menu must have no title bar at all, so the frame is stripped to a borderless popup below.
        // Unlike the app's modal windows it deliberately does NOT set ExtendsContentIntoTitleBar: that keeps
        // an AppWindow title bar alive and draws its minimize/maximize/close buttons over the menu.
        Closed += (_, _) => { if (ReferenceEquals(s_open, this)) s_open = null; };
        Activated += OnActivated;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        TryConfigurePresenter();
        ApplyBorderlessPopupFrame(hwnd);
        SeedFromCurrentSearch();
        _root.SizeChanged += (_, _) => ResizeAndPosition();
        ResizeAndPosition();
    }

    private Grid BuildRoot(ElementTheme theme)
    {
        var items = new StackPanel { Spacing = 2 };
        items.Children.Add(BuildMenuItem("\uE721", "Quick search\u2026", ToggleQuickSearchPanel));
        items.Children.Add(_quickSearchPanel);
        items.Children.Add(BuildSeparator());
        items.Children.Add(BuildMenuItem("\uE72C", "Open (reset search)", () => Invoke(_actions.OpenReset)));
        items.Children.Add(BuildMenuItem("\uE8A7", "Open (existing search)", () => Invoke(_actions.OpenExisting)));
        items.Children.Add(BuildSeparator());
        items.Children.Add(BuildMenuItem("\uE711", "Close", () => Invoke(_actions.CloseApp)));

        var root = new Grid
        {
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            RequestedTheme = theme,
        };
        root.Children.Add(items);
        Services.AppThemeService.ApplyThemedDialogSurface(root, theme);
        root.KeyDown += OnRootKeyDown;
        return root;
    }

    private StackPanel BuildQuickSearchPanel()
    {
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        toggles.Children.Add(_regexBox);
        toggles.Children.Add(_caseBox);
        toggles.Children.Add(_multilineBox);
        toggles.Children.Add(_exactBox);

        // Multiline implies regex, and Semantic mode ignores the Traditional option toggles.
        _multilineBox.Checked += (_, _) => _regexBox.IsChecked = true;
        _modeBox.SelectionChanged += (_, _) =>
        {
            bool semantic = _modeBox.SelectedIndex == 1;
            foreach (var box in new[] { _regexBox, _caseBox, _multilineBox, _exactBox })
                box.IsEnabled = !semantic;
        };

        var search = new Button
        {
            Content = "Search",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 4, 14, 4),
            MinWidth = 0,
        };
        search.Click += (_, _) => RunQuickSearch();

        var panel = new StackPanel
        {
            Spacing = 6,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(8, 4, 8, 8),
        };
        panel.Children.Add(new TextBlock { Text = "Directory", FontSize = 11, Opacity = 0.75 });
        panel.Children.Add(_directoryBox);
        panel.Children.Add(new TextBlock { Text = "Pattern", FontSize = 11, Opacity = 0.75 });
        panel.Children.Add(_queryBox);
        panel.Children.Add(new TextBlock { Text = "Mode", FontSize = 11, Opacity = 0.75 });
        panel.Children.Add(_modeBox);
        panel.Children.Add(toggles);
        panel.Children.Add(search);

        void SubmitOnEnter(object _, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;
            e.Handled = true;
            RunQuickSearch();
        }
        _queryBox.KeyDown += SubmitOnEnter;
        _directoryBox.KeyDown += SubmitOnEnter;
        return panel;
    }

    private static Border BuildSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 4, 8, 4),
        Opacity = 0.5,
        Background = Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out object? b)
            ? b as Brush
            : null,
    };

    private static Button BuildMenuItem(string glyph, string text, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ToggleQuickSearchPanel()
    {
        bool opening = _quickSearchPanel.Visibility == Visibility.Collapsed;
        _quickSearchPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening)
            _queryBox.Focus(FocusState.Programmatic);
        ResizeAndPosition();
    }

    private void SeedFromCurrentSearch()
    {
        if (_actions.ReadCurrentSearch?.Invoke() is not { } current)
            return;
        _directoryBox.Text = current.Directory;
        _queryBox.Text = current.Query;
        _modeBox.SelectedIndex = current.Semantic ? 1 : 0;
        _regexBox.IsChecked = current.UseRegex;
        _caseBox.IsChecked = current.CaseSensitive;
        _multilineBox.IsChecked = current.Multiline;
        _exactBox.IsChecked = current.ExactMatch;
    }

    private void RunQuickSearch()
    {
        var request = new TrayQuickSearchRequest(
            _directoryBox.Text ?? string.Empty,
            _queryBox.Text ?? string.Empty,
            _regexBox.IsChecked == true,
            _caseBox.IsChecked == true,
            _multilineBox.IsChecked == true,
            _exactBox.IsChecked == true,
            _modeBox.SelectedIndex == 1);

        var run = _actions.RunQuickSearch;
        CloseMenu();
        run?.Invoke(request);
    }

    private void Invoke(Action? action)
    {
        CloseMenu();
        action?.Invoke();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;
        e.Handled = true;
        // Esc backs out of the quick-search panel first, then closes the menu.
        if (_quickSearchPanel.Visibility == Visibility.Visible)
            ToggleQuickSearchPanel();
        else
            CloseMenu();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            CloseMenu();
            return;
        }

        // Presenter calls can silently no-op before the window is realized, and showing it can restore the
        // frame WinUI wanted, so re-assert now and again once this activation settles.
        ReassertPopupFrame();
        DispatcherQueue.TryEnqueue(ReassertPopupFrame);
    }

    private void ReassertPopupFrame()
    {
        if (_closing)
            return;
        TryConfigurePresenter();
        try { ApplyBorderlessPopupFrame(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
        catch { }
    }

    private void CloseMenu()
    {
        if (_closing)
            return;
        _closing = true;
        if (ReferenceEquals(s_open, this))
            s_open = null;
        try { Close(); }
        catch { }
    }

    /// <summary>
    /// Sizes the window to its content and anchors its bottom-right corner to the tray cursor, clamped to
    /// the display's work area so the menu rests against the taskbar edge rather than under it. The anchor
    /// only lands where the user clicked because the popup frame has no caption and no resize border, so
    /// the window rect and the menu the user sees are the same rectangle.
    /// </summary>
    private void ResizeAndPosition()
    {
        try
        {
            double scale = GetAnchorScale();

            _root.Measure(new Windows.Foundation.Size(MenuWidthDip, double.PositiveInfinity));
            int width = (int)Math.Ceiling(MenuWidthDip * scale);
            int height = (int)Math.Ceiling(Math.Max(_root.DesiredSize.Height, 40) * scale);
            _appWindow.Resize(new SizeInt32(width, height));

            var area = DisplayArea.GetFromPoint(new PointInt32(_anchorX, _anchorY), DisplayAreaFallback.Nearest);
            var work = area?.WorkArea ?? default;

            // The tray sits at a screen edge, so open the menu back toward the desktop.
            int x = _anchorX - width;
            int y = _anchorY - height;
            if (work.Width > 0 && work.Height > 0)
            {
                x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
                y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
            }
            _appWindow.Move(new PointInt32(x, y));
        }
        catch { }
    }

    /// <summary>
    /// The DPI scale of the display the user actually right-clicked on. The window is still sitting on
    /// whichever display it was created on when it is first sized, so scaling by its own DPI would size the
    /// menu for the wrong display and place it away from the click on a mixed-DPI desktop.
    /// </summary>
    private double GetAnchorScale()
    {
        uint dpi = 0;
        try
        {
            IntPtr monitor = MonitorFromPoint(new POINT { X = _anchorX, Y = _anchorY }, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0)
                dpi = dpiX;
        }
        catch { }

        if (dpi == 0)
        {
            try { dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
            catch { }
        }

        return dpi > 0 ? dpi / 96.0 : 1;
    }

    private void TryConfigurePresenter()
    {
        // Separate try blocks: one rejected call must not strand the others. IsAlwaysOnTop especially,
        // since it keeps the menu above the taskbar it is anchored to.
        try { _appWindow.IsShownInSwitchers = false; }
        catch { }

        try
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }
        }
        catch { }
    }

    /// <summary>
    /// Forces a true borderless popup frame, because <see cref="TryConfigurePresenter"/> swallows a
    /// rejected presenter call and would leave this menu with an ordinary window frame. That frame is wrong
    /// twice over: it draws caption buttons (minimize/maximize/close) that a context menu must never show,
    /// and its invisible resize border sits outside the visible edge, so positioning by window rect lands
    /// the menu a few pixels away from where the user right-clicked. Idempotent.
    /// </summary>
    private static void ApplyBorderlessPopupFrame(IntPtr hwnd)
    {
        try
        {
            int style = GetWindowLong(hwnd, GwlStyle);
            // A failed read returns 0; synthesizing a style from it would clear WS_VISIBLE and the clip bits.
            if (style == 0)
                return;

            int popupStyle = (style & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox))
                | WsPopup;
            if (style == popupStyle)
                return;

            _ = SetWindowLong(hwnd, GwlStyle, popupStyle);
            _ = SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch { }
    }

    private const int GwlStyle = -16;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint MonitorDefaultToNearest = 0x0002;
    private const int MdtEffectiveDpi = 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
