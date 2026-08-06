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

        // Title-bar-less: set on the Window directly so the caption strip is never drawn even when the
        // presenter call below fails to apply. No SetTitleBar(), so every control stays interactive.
        ExtendsContentIntoTitleBar = true;
        Closed += (_, _) => { if (ReferenceEquals(s_open, this)) s_open = null; };
        Activated += OnActivated;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        TryConfigurePresenter();
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
            CloseMenu();
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
    /// Sizes the window to its content and keeps the bottom-right corner pinned near the tray cursor,
    /// clamped to the display's work area so it never spills off-screen.
    /// </summary>
    private void ResizeAndPosition()
    {
        try
        {
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale <= 0)
                scale = 1;

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

    private void TryConfigurePresenter()
    {
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
            _appWindow.IsShownInSwitchers = false;
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
