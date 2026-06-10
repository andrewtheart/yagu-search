using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

            if (ownerHwnd == IntPtr.Zero)
                return false;

            var selectedColor = ToWindowsColor(issue.CurrentColor);
            YaguDialogResult result;
            try
            {
                result = await FontContrastWarningDialogView.ShowAsync(ownerHwnd, issue, color => selectedColor = color);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("FontContrast", $"Warning dialog failed: {ex.Message}", ex);
                try
                {
                    viewModel.FontContrastReminderAfterUtc = DateTimeOffset.UtcNow.Add(FontContrastWarningService.RemindLaterDelay);
                    await viewModel.PersistSettingsAsync();
                }
                catch (Exception persistEx)
                {
                    LogService.Instance.Warning("FontContrast", $"Unable to persist warning snooze after dialog failure: {persistEx.Message}", persistEx);
                }
                return true;
            }

            switch (result)
            {
                case YaguDialogResult.Primary:
                    viewModel.ApplyFontContrastColor(issue.Candidate.Key, ColorStringHelper.ToHex(selectedColor));
                    viewModel.SuppressFontContrastWarnings = false;
                    viewModel.FontContrastReminderAfterUtc = null;
                    await viewModel.PersistSettingsAsync();
                    return true;

                case YaguDialogResult.Secondary:
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

internal static class FontContrastWarningDialogView
{
    private const string DialogTitle = "Font color may be hard to read";

    private static readonly Windows.UI.Color ReadableGreen = Windows.UI.Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43);
    private static readonly Windows.UI.Color UnreadableRed = Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38);

    public static async Task<YaguDialogResult> ShowAsync(
        IntPtr ownerHwnd,
        FontContrastIssue issue,
        Action<Windows.UI.Color> onColorChanged)
    {
        return await YaguDialog.ShowAsync(
            ownerHwnd,
            new YaguDialogOptions
            {
                Title = DialogTitle,
                Content = BuildContent(issue, onColorChanged),
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Don't remind me again",
                CloseButtonText = "Remind me later",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = ToElementTheme(issue.Theme),
                Width = 760,
                Height = 720,
                MaxContentHeight = 560,
                IsResizable = true,
            });
    }

    private static ScrollViewer BuildContent(FontContrastIssue issue, Action<Windows.UI.Color> onColorChanged)
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
        var panelBackground = new SolidColorBrush(isLight
            ? Colors.White
            : ColorHelper.FromArgb(0xFF, 0x28, 0x28, 0x28));
        var strokeBrush = new SolidColorBrush(isLight
            ? ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00)
            : ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        var body = new StackPanel
        {
            Spacing = 14,
            Width = 660,
            MaxWidth = 660,
            RequestedTheme = ToElementTheme(issue.Theme),
        };
        body.Children.Add(new TextBlock
        {
            Text = $"The current {issue.Candidate.SettingLabel} color for the {issue.Candidate.SurfaceName} may not contrast properly with {themeName} mode. Try a {direction} color.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
            Foreground = mutedForeground,
        });

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
            Width = 600,
            MaxWidth = 600,
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

        body.Children.Add(previewBorder);
        body.Children.Add(contrastStatusRow);
        body.Children.Add(contrastText);
        body.Children.Add(pickerPanel);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 520,
            Content = body,
        };
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

    private static ElementTheme ToElementTheme(FontContrastTheme theme)
        => theme == FontContrastTheme.Light ? ElementTheme.Light : ElementTheme.Dark;
}