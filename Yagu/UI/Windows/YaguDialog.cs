using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;

namespace Yagu;

internal enum YaguDialogResult
{
    None,
    Primary,
    Secondary,
    Close,
}

internal enum YaguDialogDefaultButton
{
    None,
    Primary,
    Secondary,
    Close,
}

internal sealed record YaguDialogOptions
{
    public required string Title { get; init; }
    public required object Content { get; init; }
    public string? PrimaryButtonText { get; init; }
    public string? SecondaryButtonText { get; init; }
    public string? CloseButtonText { get; init; } = "Close";
    public YaguDialogDefaultButton DefaultButton { get; init; } = YaguDialogDefaultButton.Primary;
    public ElementTheme RequestedTheme { get; init; } = ElementTheme.Default;
    public int Width { get; init; } = 560;
    public int Height { get; init; } = 300;
    public double MinContentHeight { get; init; }
    public double MaxContentHeight { get; init; } = 520;
    public bool IsResizable { get; init; }
    public bool ShowTitle { get; init; } = true;
    public bool ShowTitleBar { get; init; } = true;
    public bool ShowTopRightCloseButton { get; init; }

    /// <summary>Optional Segoe Fluent/MDL2 glyph shown to the left of the title (e.g. a warning icon). Null = no glyph.</summary>
    public string? TitleGlyph { get; init; }
    /// <summary>Optional color for <see cref="TitleGlyph"/>. Null uses the default foreground.</summary>
    public Windows.UI.Color? TitleGlyphColor { get; init; }
}

internal sealed class YaguDialog : Window
{
    private static readonly HashSet<YaguDialog> OpenWindows = new();

    private readonly TaskCompletionSource<YaguDialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private readonly YaguDialogOptions _options;
    private readonly AppWindow _appWindow;
    private YaguDialogResult _result = YaguDialogResult.Close;

    private YaguDialog(IntPtr ownerHwnd, YaguDialogOptions options)
    {
        _ownerHwnd = ownerHwnd;
        _options = options;
        Title = options.Title;
        Content = BuildContent(options);
        Closed += OnClosed;

        // Hide the OS title bar reliably when requested. Setting ExtendsContentIntoTitleBar
        // directly on the Window guarantees the caption strip is not drawn even if the
        // OverlappedPresenter.SetBorderAndTitleBar call below fails to apply — matching the
        // title-bar-less pattern used by MainWindow/SettingsWindow/ResultStoreTempLocationWindow.
        // No SetTitleBar() call is made, so no drag region is created and all content (including
        // a top-right close button) stays interactive.
        if (!options.ShowTitleBar)
            ExtendsContentIntoTitleBar = true;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);

        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        appWindow.Title = Title;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, options.Width, options.Height);
        TryConfigurePresenter(appWindow, options.IsResizable, options.ShowTitleBar);
        TrySetIcon(appWindow);
    }

    public static Task<YaguDialogResult> ShowAsync(
        IntPtr ownerHwnd,
        YaguDialogOptions options,
        Action<YaguDialog>? configure = null)
    {
        var dialog = new YaguDialog(ownerHwnd, options);
        configure?.Invoke(dialog);
        return dialog.ShowModalAsync();
    }

    public static bool HasOpenOwnedWindow(IntPtr ownerHwnd)
    {
        foreach (var window in OpenWindows)
        {
            if (window._ownerHwnd == ownerHwnd)
                return true;
        }

        return false;
    }

    public void AcceptPrimary() => Complete(YaguDialogResult.Primary);

    public void AcceptSecondary() => Complete(YaguDialogResult.Secondary);

    public void AcceptClose() => Complete(YaguDialogResult.Close);

    private Task<YaguDialogResult> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);

        // Re-apply the title-bar-less presenter config after activation. When a dialog is shown very
        // early (e.g. the Everything Search prompts raised during startup), SetBorderAndTitleBar can
        // silently fail to apply before the window is realized, leaving the OS caption visible. Doing
        // it again once the window is live — and once more on the next dispatcher tick — makes the
        // no-title-bar state stick regardless of when the dialog is opened.
        if (!_options.ShowTitleBar)
        {
            TryConfigurePresenter(_appWindow, _options.IsResizable, _options.ShowTitleBar);
            DispatcherQueue.TryEnqueue(() => TryConfigurePresenter(_appWindow, _options.IsResizable, _options.ShowTitleBar));
        }

        return _completion.Task;
    }

    private Grid BuildContent(YaguDialogOptions options)
    {
        var root = new Grid
        {
            Padding = new Thickness(28, 24, 28, 24),
        };
        Yagu.Services.AppThemeService.ApplyThemedDialogSurface(root, options.RequestedTheme);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (options.ShowTitle)
        {
            var title = new TextBlock
            {
                Text = options.Title,
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.WrapWholeWords,
            };

            if (options.TitleGlyph is { Length: > 0 } glyph)
            {
                // Render the glyph (e.g. a warning icon) to the left of the title text.
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var titleIcon = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (options.TitleGlyphColor is { } glyphColor)
                    titleIcon.Foreground = new SolidColorBrush(glyphColor);
                title.VerticalAlignment = VerticalAlignment.Center;
                titleRow.Children.Add(titleIcon);
                titleRow.Children.Add(title);
                Grid.SetRow(titleRow, 0);
                root.Children.Add(titleRow);
            }
            else
            {
                Grid.SetRow(title, 0);
                root.Children.Add(title);
            }
        }

        if (options.ShowTopRightCloseButton)
        {
            var topRightClose = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                Width = 32,
                Height = 32,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            ToolTipService.SetToolTip(topRightClose, "Close");
            topRightClose.Click += (_, _) => Complete(YaguDialogResult.Close);
            Grid.SetRow(topRightClose, 0);
            root.Children.Add(topRightClose);
        }

        var bodyContent = CreateBodyContent(options.Content);
        if (options.MinContentHeight > 0)
            bodyContent.MinHeight = options.MinContentHeight;
        if (options.MaxContentHeight > 0)
            bodyContent.MaxHeight = options.MaxContentHeight;
        bodyContent.Margin = options.ShowTitle
            ? new Thickness(0, 16, 0, 20)
            : new Thickness(0, 0, 0, 20);
        Grid.SetRow(bodyContent, 1);
        root.Children.Add(bodyContent);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };

        Button? secondaryButton = AddFooterButton(footer, options.SecondaryButtonText, YaguDialogResult.Secondary, accent: false);
        Button? closeButton = AddFooterButton(footer, options.CloseButtonText, YaguDialogResult.Close, accent: false);
        Button? primaryButton = AddFooterButton(footer, options.PrimaryButtonText, YaguDialogResult.Primary, accent: true);

        root.Loaded += (_, _) =>
        {
            Button? defaultButton = options.DefaultButton switch
            {
                YaguDialogDefaultButton.Primary => primaryButton,
                YaguDialogDefaultButton.Secondary => secondaryButton,
                YaguDialogDefaultButton.Close => closeButton,
                _ => null,
            };
            defaultButton?.Focus(FocusState.Programmatic);
        };

        root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape && closeButton is not null)
            {
                args.Handled = true;
                Complete(YaguDialogResult.Close);
            }
        };

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static FrameworkElement CreateBodyContent(object content)
    {
        return content switch
        {
            FrameworkElement element => element,
            string text => new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 14,
                LineHeight = 21,
                Opacity = 0.9,
            },
            _ => new TextBlock
            {
                Text = content.ToString() ?? string.Empty,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 14,
                LineHeight = 21,
                Opacity = 0.9,
            },
        };
    }

    private Button? AddFooterButton(StackPanel footer, string? text, YaguDialogResult result, bool accent)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Padding = new Thickness(18, 8, 18, 8),
        };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            button.Style = accentStyle;

        button.Click += (_, _) => Complete(result);
        footer.Children.Add(button);
        return button;
    }

    private void Complete(YaguDialogResult result)
    {
        _result = result;
        Close();
    }

    private static void TryConfigurePresenter(AppWindow appWindow, bool isResizable, bool showTitleBar)
    {
        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = isResizable;
                presenter.IsMinimizable = false;
                presenter.IsResizable = isResizable;
                if (!showTitleBar)
                    presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
        }
        catch { }
    }

    private static void TrySetIcon(AppWindow appWindow)
    {
        try
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
            if (System.IO.File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OpenWindows.Remove(this);

        if (_ownerHwnd != IntPtr.Zero)
        {
            EnableWindow(_ownerHwnd, true);
            SetForegroundWindow(_ownerHwnd);
        }

        _completion.TrySetResult(_result);
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

}