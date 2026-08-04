using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Appearance settings: theme mode, preview/editor/result-list fonts and colors, drawer and
/// sticky-header typography, and the font-contrast candidates used by the low-contrast warning.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    [ObservableProperty] public partial int ThemeModeIndex { get; set; } // 0 = Auto (system theme), 1 = Dark, 2 = Light
    partial void OnThemeModeIndexChanged(int value)
        => AppThemeService.CurrentThemeModeIndex = AppThemeService.NormalizeThemeModeIndex(value);
    [ObservableProperty] public partial bool PreviewWordWrap { get; set; }
    [ObservableProperty] public partial int PreviewWrapModeIndex { get; set; } = 2; // 0 = Wrap, 1 = legacy PartialWrap, 2 = NoWrap
    [ObservableProperty] public partial int PreviewAutoLoadMatches { get; set; } = 50;
    [ObservableProperty] public partial string SelectedPreviewContentBackgroundColor { get; set; } = AppSettings.DefaultSelectedPreviewContentBackgroundColor;
    [ObservableProperty] public partial string UnselectedPreviewContentBackgroundColor { get; set; } = AppSettings.DefaultUnselectedPreviewContentBackgroundColor;
    [ObservableProperty] public partial string PreviewGutterContextColor { get; set; } = AppSettings.DefaultPreviewGutterContextColor;
    [ObservableProperty] public partial string PreviewGutterMatchColor { get; set; } = AppSettings.DefaultPreviewGutterMatchColor;
    [ObservableProperty] public partial string PreviewEditorGutterColor { get; set; } = AppSettings.DefaultPreviewEditorGutterColor;
    // Empty string = "Auto" (follow the app/system theme); a non-empty ARGB hex is an explicit override.
    [ObservableProperty] public partial string PreviewEditorTextColor { get; set; } = AppSettings.DefaultPreviewEditorTextColor;
    [ObservableProperty] public partial string PreviewMatchTextColor { get; set; } = AppSettings.DefaultPreviewMatchTextColor;
    [ObservableProperty] public partial string PreviewOverlayColor { get; set; } = AppSettings.DefaultPreviewOverlayColor;
    [ObservableProperty] public partial string PreviewMatchLineColor { get; set; } = AppSettings.DefaultPreviewMatchLineColor;
    [ObservableProperty] public partial string PreviewShowMoreEllipsisColor { get; set; } = AppSettings.DefaultPreviewShowMoreEllipsisColor;
    [ObservableProperty] public partial int PreviewShowMoreEllipsisFontSize { get; set; } = AppSettings.DefaultPreviewShowMoreEllipsisFontSize;
    [ObservableProperty] public partial string PreviewTextFontFamily { get; set; } = AppSettings.DefaultPreviewTextFontFamily;
    [ObservableProperty] public partial int PreviewTextFontSize { get; set; } = AppSettings.DefaultPreviewTextFontSize;
    [ObservableProperty] public partial string PreviewEditorFontFamily { get; set; } = AppSettings.DefaultPreviewEditorFontFamily;
    [ObservableProperty] public partial int PreviewEditorFontSize { get; set; } = AppSettings.DefaultPreviewEditorFontSize;
    // Long-line warning preference: 0 = Ask every time, 1 = Always open without word wrap, 2 = Always open with word wrap.
    [ObservableProperty] public partial int PreviewLongLineWarningIndex { get; set; }
    [ObservableProperty] public partial string ResultListMatchTextFontFamily { get; set; } = AppSettings.DefaultResultListMatchTextFontFamily;
    [ObservableProperty] public partial int ResultListMatchTextFontSize { get; set; } = AppSettings.DefaultResultListMatchTextFontSize;
    [ObservableProperty] public partial string ResultListMatchHighlightColor { get; set; } = AppSettings.DefaultResultListMatchHighlightColor;

    // ── File list overlay settings ──
    [ObservableProperty] public partial int FileListOverlayHeight { get; set; } = AppSettings.DefaultFileListOverlayHeight;
    [ObservableProperty] public partial int FileListOverlayFontSize { get; set; } = AppSettings.DefaultFileListOverlayFontSize;
    [ObservableProperty] public partial string FileListOverlayFontColor { get; set; } = AppSettings.DefaultFileListOverlayFontColor;
    [ObservableProperty] public partial string FileListOverlayFontFamily { get; set; } = AppSettings.DefaultFileListOverlayFontFamily;

    // ── Preview sticky file header overlay settings ──
    [ObservableProperty] public partial int PreviewStickyHeaderHeight { get; set; } = AppSettings.DefaultPreviewStickyHeaderHeight;
    [ObservableProperty] public partial int PreviewStickyHeaderFileNameFontSize { get; set; } = AppSettings.DefaultPreviewStickyHeaderFileNameFontSize;
    [ObservableProperty] public partial string PreviewStickyHeaderFileNameFontColor { get; set; } = AppSettings.DefaultPreviewStickyHeaderFileNameFontColor;
    [ObservableProperty] public partial string PreviewStickyHeaderFileNameFontFamily { get; set; } = AppSettings.DefaultPreviewStickyHeaderFileNameFontFamily;
    [ObservableProperty] public partial int PreviewStickyHeaderDetailFontSize { get; set; } = AppSettings.DefaultPreviewStickyHeaderDetailFontSize;
    [ObservableProperty] public partial string PreviewStickyHeaderDetailFontColor { get; set; } = AppSettings.DefaultPreviewStickyHeaderDetailFontColor;
    [ObservableProperty] public partial string PreviewStickyHeaderDetailFontFamily { get; set; } = AppSettings.DefaultPreviewStickyHeaderDetailFontFamily;

    // ── File list drawer label settings ──
    [ObservableProperty] public partial int DrawerFileNameFontSize { get; set; } = AppSettings.DefaultDrawerFileNameFontSize;
    [ObservableProperty] public partial string DrawerFileNameFontColor { get; set; } = AppSettings.DefaultDrawerFileNameFontColor;
    [ObservableProperty] public partial string DrawerFileNameFontFamily { get; set; } = AppSettings.DefaultDrawerFileNameFontFamily;
    [ObservableProperty] public partial int DrawerDirectoryFontSize { get; set; } = AppSettings.DefaultDrawerDirectoryFontSize;
    [ObservableProperty] public partial string DrawerDirectoryFontColor { get; set; } = AppSettings.DefaultDrawerDirectoryFontColor;
    [ObservableProperty] public partial string DrawerDirectoryFontFamily { get; set; } = AppSettings.DefaultDrawerDirectoryFontFamily;
    [ObservableProperty] public partial int DrawerMetadataFontSize { get; set; } = AppSettings.DefaultDrawerMetadataFontSize;
    [ObservableProperty] public partial string DrawerMetadataFontColor { get; set; } = AppSettings.DefaultDrawerMetadataFontColor;
    [ObservableProperty] public partial string DrawerMetadataFontFamily { get; set; } = AppSettings.DefaultDrawerMetadataFontFamily;

    public IReadOnlyList<FontContrastCandidate> GetFontContrastCandidates()
    {
        var selectedPreviewBackground = FontContrastColor.Parse(
            SelectedPreviewContentBackgroundColor,
            FontContrastColor.FromArgb(0xFF, 0x00, 0x00, 0x00));
        var unselectedPreviewBackground = FontContrastColor.Parse(
            UnselectedPreviewContentBackgroundColor,
            FontContrastColor.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));

        return
        [
            new(nameof(PreviewGutterContextColor), "selected preview content", "Preview gutter text", PreviewGutterContextColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), selectedPreviewBackground),
            new(nameof(PreviewGutterContextColor), "unselected preview content", "Preview gutter text", PreviewGutterContextColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), unselectedPreviewBackground),
            new(nameof(PreviewGutterMatchColor), "selected preview content", "Matched preview gutter text", PreviewGutterMatchColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), selectedPreviewBackground),
            new(nameof(PreviewGutterMatchColor), "unselected preview content", "Matched preview gutter text", PreviewGutterMatchColor, FontContrastColor.FromArgb(0xFF, 0x9C, 0xDC, 0xFE), unselectedPreviewBackground),
            new(nameof(PreviewMatchTextColor), "selected preview content", "Match highlight text", PreviewMatchTextColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xD7, 0x00), selectedPreviewBackground),
            new(nameof(PreviewMatchTextColor), "unselected preview content", "Match highlight text", PreviewMatchTextColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xD7, 0x00), unselectedPreviewBackground),
            new(nameof(PreviewMatchLineColor), "selected preview content", "Matched line text", PreviewMatchLineColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), selectedPreviewBackground),
            new(nameof(PreviewMatchLineColor), "unselected preview content", "Matched line text", PreviewMatchLineColor, FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), unselectedPreviewBackground),
            new(nameof(PreviewEditorGutterColor), "built-in editor", "Editor gutter text", PreviewEditorGutterColor, FontContrastColor.FromArgb(0xFF, 0x3A, 0x8F, 0xD6)),
            new(nameof(ResultListMatchHighlightColor), "file list", "Highlighted match text", ResultListMatchHighlightColor, FontContrastColor.FromArgb(0xFF, 0xB8, 0x86, 0x0B)),
        ];
    }

    public void ApplyFontContrastColor(string key, string colorHex)
    {
        switch (key)
        {
            case nameof(PreviewGutterContextColor):
                PreviewGutterContextColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
                break;
            case nameof(PreviewGutterMatchColor):
                PreviewGutterMatchColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xDC, 0xFE));
                break;
            case nameof(PreviewMatchTextColor):
                PreviewMatchTextColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
                break;
            case nameof(PreviewMatchLineColor):
                PreviewMatchLineColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
                break;
            case nameof(PreviewEditorGutterColor):
                PreviewEditorGutterColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x8F, 0xD6));
                break;
            case nameof(ResultListMatchHighlightColor):
                ResultListMatchHighlightColor = ColorStringHelper.Normalize(colorHex, Windows.UI.Color.FromArgb(0xFF, 0xB8, 0x86, 0x0B));
                break;
        }
    }

    public void ResetFontContrastReminderState()
    {
        SuppressFontContrastWarnings = false;
        FontContrastReminderAfterUtc = null;
    }
}
