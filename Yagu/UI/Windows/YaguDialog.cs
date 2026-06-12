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
}

internal sealed class YaguDialog : Window
{
    private static readonly HashSet<YaguDialog> OpenWindows = new();

    private readonly TaskCompletionSource<YaguDialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private readonly YaguDialogOptions _options;
    private YaguDialogResult _result = YaguDialogResult.Close;

    private YaguDialog(IntPtr ownerHwnd, YaguDialogOptions options)
    {
        _ownerHwnd = ownerHwnd;
        _options = options;
        Title = options.Title;
        Content = BuildContent(options);
        Closed += OnClosed;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);

        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
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
        return _completion.Task;
    }

    private Grid BuildContent(YaguDialogOptions options)
    {
        var root = new Grid
        {
            Padding = new Thickness(28, 24, 28, 24),
            Background = ResourceBrush("ApplicationPageBackgroundThemeBrush", ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)),
            RequestedTheme = options.RequestedTheme,
        };
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
            Grid.SetRow(title, 0);
            root.Children.Add(title);
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

    private static Brush ResourceBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
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