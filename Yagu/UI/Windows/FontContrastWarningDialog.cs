using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Yagu.Helpers;
using Yagu.Services;
using Yagu.ViewModels;

namespace Yagu;

internal static class FontContrastWarningDialog
{
    private static int s_dialogOpen;

    public static async Task<bool> ShowIfNeededAsync(IntPtr ownerHwnd, MainViewModel viewModel, FontContrastTheme theme)
    {
        if (Interlocked.CompareExchange(ref s_dialogOpen, 1, 0) != 0)
            return false;

        try
        {
            if (!FontContrastWarningService.ShouldCheck(
                viewModel.SuppressFontContrastWarnings,
                viewModel.FontContrastReminderAfterUtc,
                DateTimeOffset.UtcNow))
            {
                return false;
            }

            var issue = FontContrastWarningService.FindFirstIssue(viewModel.GetFontContrastCandidates(), theme);
            if (issue is null)
                return false;

            var selectedColor = ToWindowsColor(issue.CurrentColor);
            var result = await FontContrastWarningWindow.ShowAsync(ownerHwnd, issue, color => selectedColor = color);

            switch (result)
            {
                case ContentDialogResult.Primary:
                    viewModel.ApplyFontContrastColor(issue.Candidate.Key, ColorStringHelper.ToHex(selectedColor));
                    viewModel.SuppressFontContrastWarnings = false;
                    viewModel.FontContrastReminderAfterUtc = null;
                    await viewModel.PersistSettingsAsync();
                    return true;

                case ContentDialogResult.Secondary:
                    viewModel.SuppressFontContrastWarnings = true;
                    viewModel.FontContrastReminderAfterUtc = null;
                    await viewModel.PersistSettingsAsync();
                    return true;

                default:
                    viewModel.FontContrastReminderAfterUtc = DateTimeOffset.UtcNow.Add(FontContrastWarningService.RemindLaterDelay);
                    await viewModel.PersistSettingsAsync();
                    return true;
            }
        }
        finally
        {
            Interlocked.Exchange(ref s_dialogOpen, 0);
        }
    }

    internal static Windows.UI.Color ToWindowsColor(FontContrastColor color)
        => Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);

    internal static FontContrastColor FromWindowsColor(Windows.UI.Color color)
        => FontContrastColor.FromArgb(color.A, color.R, color.G, color.B);
}

internal sealed class FontContrastWarningWindow : Window
{
    private const int DialogWidth = 780;
    private const int DialogHeight = 720;
    private const uint MonitorDefaultToNearest = 2;

    private static readonly HashSet<FontContrastWarningWindow> OpenWindows = new();

    private readonly TaskCompletionSource<ContentDialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private ContentDialogResult _result = ContentDialogResult.None;

    private static readonly Windows.UI.Color ReadableGreen = Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43);
    private static readonly Windows.UI.Color UnreadableRed = Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38);

    private FontContrastWarningWindow(
        IntPtr ownerHwnd,
        FontContrastIssue issue,
        Action<Windows.UI.Color> onColorChanged)
    {
        _ownerHwnd = ownerHwnd;
        Title = "Font color may be hard to read";
        Content = BuildContent(issue, onColorChanged);
        Closed += OnClosed;

        var hwnd = WindowForegroundHelper.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = Title;
        SizeAndCenter(appWindow, _ownerHwnd, DialogWidth, DialogHeight);

        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
            }
        }
        catch { }

        try
        {
            AppThemeService.ApplyTitleBarButtonTheme(appWindow, ToElementTheme(issue.Theme));
        }
        catch { }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);
    }

    public static Task<ContentDialogResult> ShowAsync(
        IntPtr ownerHwnd,
        FontContrastIssue issue,
        Action<Windows.UI.Color> onColorChanged)
    {
        var window = new FontContrastWarningWindow(ownerHwnd, issue, onColorChanged);
        return window.ShowModalAsync();
    }

    private Task<ContentDialogResult> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);
        return _completion.Task;
    }

    private Grid BuildContent(FontContrastIssue issue, Action<Windows.UI.Color> onColorChanged)
    {
        bool isLight = issue.Theme == FontContrastTheme.Light;
        string themeName = isLight ? "light" : "dark";
        string direction = issue.RecommendedDirection == FontContrastDirection.Darker ? "darker" : "lighter";

        var foreground = new SolidColorBrush(isLight
            ? ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)
            : Colors.White);
        var mutedForeground = new SolidColorBrush(isLight
            ? ColorHelper.FromArgb(0xCC, 0x1A, 0x1A, 0x1A)
            : ColorHelper.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        var windowBackground = new SolidColorBrush(isLight
            ? ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xF8)
            : ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20));
        var panelBackground = new SolidColorBrush(isLight
            ? Colors.White
            : ColorHelper.FromArgb(0xFF, 0x28, 0x28, 0x28));
        var strokeBrush = new SolidColorBrush(isLight
            ? ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00)
            : ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        var root = new Grid
        {
            Padding = new Thickness(30, 26, 30, 24),
            RowSpacing = 18,
            Background = windowBackground,
            RequestedTheme = ToElementTheme(issue.Theme),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"The current {issue.Candidate.SettingLabel} color for the {issue.Candidate.SurfaceName} may not contrast properly with {themeName} mode. Try a {direction} color.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
            Foreground = mutedForeground,
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var previewText = new TextBlock
        {
            Text = $"{issue.Candidate.SettingLabel} sample text",
            Foreground = new SolidColorBrush(FontContrastWarningDialog.ToWindowsColor(issue.CurrentColor)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 17,
            TextWrapping = TextWrapping.Wrap,
        };

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(FontContrastWarningDialog.ToWindowsColor(issue.BackgroundColor)),
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = previewText,
        };

        var contrastText = new TextBlock
        {
            FontSize = 13,
            Foreground = mutedForeground,
            TextWrapping = TextWrapping.Wrap,
        };

        var contrastStatusIcon = new FontIcon
        {
            FontSize = 15,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var contrastStatusText = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var contrastStatusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        contrastStatusRow.Children.Add(contrastStatusIcon);
        contrastStatusRow.Children.Add(contrastStatusText);

        var picker = new ColorPicker
        {
            Color = FontContrastWarningDialog.ToWindowsColor(issue.CurrentColor),
            IsAlphaEnabled = true,
            Width = 620,
            MinWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        void updatePreview(Windows.UI.Color color)
        {
            onColorChanged(color);
            previewText.Foreground = new SolidColorBrush(color);
            double ratio = FontContrastWarningService.GetContrastRatio(
                FontContrastWarningDialog.FromWindowsColor(color),
                issue.BackgroundColor);
            contrastText.Text = $"Contrast ratio: {ratio:F1}:1 on the current {themeName} theme background";
            UpdateContrastStatus(contrastStatusIcon, contrastStatusText, ratio);
        }

        picker.ColorChanged += (_, args) => updatePreview(args.NewColor);
        updatePreview(FontContrastWarningDialog.ToWindowsColor(issue.CurrentColor));

        var pickerPanel = new Border
        {
            Padding = new Thickness(16),
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = panelBackground,
            Child = picker,
        };

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(previewBorder);
        body.Children.Add(contrastStatusRow);
        body.Children.Add(contrastText);
        body.Children.Add(pickerPanel);

        var bodyScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };
        Grid.SetRow(bodyScroll, 1);
        root.Children.Add(bodyScroll);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        footer.Children.Add(CreateButton("Remind me later", ContentDialogResult.None, false, 136));
        footer.Children.Add(CreateButton("Don't remind me again", ContentDialogResult.Secondary, false, 184));
        footer.Children.Add(CreateButton("Save", ContentDialogResult.Primary, true, 104));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private Button CreateButton(string text, ContentDialogResult result, bool accent, double minWidth)
    {
        var button = new Button
        {
            Content = text,
            ClickMode = ClickMode.Press,
            MinWidth = minWidth,
            Padding = new Thickness(16, 8, 16, 8),
        };

        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            button.Style = accentStyle;

        button.Click += (_, _) => CloseWith(result);
        return button;
    }

    private static void UpdateContrastStatus(FontIcon icon, TextBlock text, double ratio)
    {
        bool readable = ratio >= FontContrastWarningService.MinimumReadableContrastRatio;
        var statusColor = readable ? ReadableGreen : UnreadableRed;
        icon.Glyph = readable ? "\uE73E" : "\uE711";
        icon.Foreground = new SolidColorBrush(statusColor);
        text.Text = readable ? "Readable" : "Low contrast";
        text.Foreground = new SolidColorBrush(statusColor);
    }

    private void CloseWith(ContentDialogResult result)
    {
        _result = result;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OpenWindows.Remove(this);

        if (_ownerHwnd != IntPtr.Zero)
        {
            EnableWindow(_ownerHwnd, true);
        }

        _completion.TrySetResult(_result);
    }

    private static ElementTheme ToElementTheme(FontContrastTheme theme)
        => theme == FontContrastTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

    private static void SizeAndCenter(AppWindow appWindow, IntPtr ownerHwnd, int desiredWidth, int desiredHeight)
    {
        int width = desiredWidth;
        int height = desiredHeight;
        int left = 100;
        int top = 100;

        var monitor = ownerHwnd == IntPtr.Zero
            ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, MonitorDefaultToNearest)
            : MonitorFromWindow(ownerHwnd, MonitorDefaultToNearest);

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref monitorInfo))
        {
            var workArea = monitorInfo.rcWork;
            width = Math.Min(width, Math.Max(1, workArea.Right - workArea.Left));
            height = Math.Min(height, Math.Max(1, workArea.Bottom - workArea.Top));

            if (ownerHwnd != IntPtr.Zero && GetWindowRect(ownerHwnd, out var ownerRect))
            {
                int ownerCenterX = (ownerRect.Left + ownerRect.Right) / 2;
                int ownerCenterY = (ownerRect.Top + ownerRect.Bottom) / 2;
                left = ownerCenterX - width / 2;
                top = ownerCenterY - height / 2;
            }
            else
            {
                left = workArea.Left + ((workArea.Right - workArea.Left) - width) / 2;
                top = workArea.Top + ((workArea.Bottom - workArea.Top) - height) / 2;
            }

            if (left < workArea.Left) left = workArea.Left;
            if (top < workArea.Top) top = workArea.Top;
            if (left + width > workArea.Right) left = workArea.Right - width;
            if (top + height > workArea.Bottom) top = workArea.Bottom - height;
        }

        appWindow.MoveAndResize(new RectInt32(left, top, width, height));
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}