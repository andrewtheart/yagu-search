using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;

namespace Yagu;

internal sealed class AdminProtectedPathsDialog : Window
{
    private const int DialogWidth = 720;
    private const int DialogHeight = 540;

    private static readonly HashSet<AdminProtectedPathsDialog> OpenWindows = new();

    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;

    private AdminProtectedPathsDialog(IntPtr ownerHwnd, IReadOnlyList<string> segments)
    {
        _ownerHwnd = ownerHwnd;
        Title = "Admin-protected paths";
        Content = BuildContent(segments);
        Closed += OnClosed;

        IntPtr dialogHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(dialogHwnd, _ownerHwnd);

        var windowId = Win32Interop.GetWindowIdFromWindow(dialogHwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = Title;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, DialogWidth, DialogHeight, minHeight: 300);
        TryConfigurePresenter(appWindow);
        TrySetIcon(appWindow);
    }

    public static async Task ShowAsync(IntPtr ownerHwnd, IReadOnlyList<string> segments)
    {
        var dialog = new AdminProtectedPathsDialog(ownerHwnd, segments);
        await dialog.ShowModalAsync();
    }

    private Task<bool> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);
        return _completion.Task;
    }

    private Grid BuildContent(IReadOnlyList<string> segments)
    {
        var root = new Grid
        {
            Padding = new Thickness(28, 24, 28, 24),
            Background = ResourceBrush("ApplicationPageBackgroundThemeBrush", ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 16),
        };
        header.Children.Add(new TextBlock
        {
            Text = "Admin-protected paths",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Some paths are not accessible by non-administrative processes. Yagu is configured to skip these administrator-only paths while running in non-administrator mode:",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.82,
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var listPanel = new StackPanel { Spacing = 4 };
        if (segments.Count == 0)
        {
            listPanel.Children.Add(CreatePathText("No admin-protected path segments are configured."));
        }
        else
        {
            foreach (string segment in segments)
                listPanel.Children.Add(CreatePathText(segment));
        }

        var listBorder = new Border
        {
            Background = ResourceBrush("LayerFillColorDefaultBrush", ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ResourceBrush("ControlElevationBorderBrush", ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                Content = listPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
        Grid.SetRow(listBorder, 1);
        root.Children.Add(listBorder);

        var footer = new Grid
        {
            Margin = new Thickness(0, 16, 0, 0),
            ColumnSpacing = 16,
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var notes = new StackPanel { Spacing = 4 };
        notes.Children.Add(CreateMutedText("This list is not exhaustive, and some other protected paths may be inaccessible and fail during search."));
        notes.Children.Add(CreateMutedText("To modify this list, open Settings from the gear button."));
        Grid.SetColumn(notes, 0);
        footer.Children.Add(notes);

        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 96,
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            closeButton.Style = accentStyle;
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(closeButton);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static TextBlock CreatePathText(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        TextWrapping = TextWrapping.NoWrap,
        IsTextSelectionEnabled = true,
    };

    private static TextBlock CreateMutedText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.72,
    };

    private static Brush ResourceBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
    }

    private static void TryConfigurePresenter(AppWindow appWindow)
    {
        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
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

        _completion.TrySetResult(true);
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

}