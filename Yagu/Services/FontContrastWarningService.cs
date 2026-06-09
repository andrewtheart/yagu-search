using System.Globalization;

namespace Yagu.Services;

public enum FontContrastTheme
{
    Dark,
    Light,
}

public enum FontContrastDirection
{
    Darker,
    Lighter,
}

public readonly record struct FontContrastColor(byte A, byte R, byte G, byte B)
{
    public static FontContrastColor FromArgb(byte alpha, byte red, byte green, byte blue)
        => new(alpha, red, green, blue);

    public static FontContrastColor Parse(string? value, FontContrastColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return new FontContrastColor(
                0xFF,
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
        }

        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return new FontContrastColor(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        return fallback;
    }

    public string ToHex()
        => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

public sealed record FontContrastCandidate(
    string Key,
    string SurfaceName,
    string SettingLabel,
    string CurrentHex,
    FontContrastColor FallbackColor,
    FontContrastColor? BackgroundColor = null);

public sealed record FontContrastIssue(
    FontContrastCandidate Candidate,
    FontContrastTheme Theme,
    FontContrastDirection RecommendedDirection,
    FontContrastColor CurrentColor,
    FontContrastColor BackgroundColor,
    double ContrastRatio);

public static class FontContrastWarningService
{
    public static readonly TimeSpan RemindLaterDelay = TimeSpan.FromHours(24);
    public const double MinimumReadableContrastRatio = 2.75;

    public static bool ShouldCheck(bool suppressWarnings, DateTimeOffset? remindAfterUtc, DateTimeOffset nowUtc)
        => !suppressWarnings && (remindAfterUtc is null || remindAfterUtc <= nowUtc);

    public static FontContrastIssue? FindFirstIssue(IEnumerable<FontContrastCandidate> candidates, FontContrastTheme theme)
    {
        foreach (var candidate in candidates)
        {
            if (TryCreateIssue(candidate, theme, out var issue))
                return issue;
        }

        return null;
    }

    public static bool TryCreateIssue(FontContrastCandidate candidate, FontContrastTheme theme, out FontContrastIssue? issue)
    {
        var color = FontContrastColor.Parse(candidate.CurrentHex, candidate.FallbackColor);
        var background = ResolveCandidateBackground(candidate, theme);
        double contrastRatio = GetContrastRatio(color, background);

        if (contrastRatio >= MinimumReadableContrastRatio)
        {
            issue = null;
            return false;
        }

        issue = new FontContrastIssue(
            candidate,
            theme,
            ChooseRecommendedDirection(color, background),
            color,
            background,
            contrastRatio);
        return true;
    }

    public static FontContrastColor ResolveCandidateBackground(FontContrastCandidate candidate, FontContrastTheme theme)
        => ResolveBackground(candidate.BackgroundColor, theme);

    public static FontContrastColor ResolveBackground(FontContrastColor? backgroundColor, FontContrastTheme theme)
    {
        var themeBackground = GetThemeSampleBackground(theme);
        return backgroundColor is { } background
            ? BlendOver(background, themeBackground)
            : themeBackground;
    }

    public static FontContrastColor GetThemeSampleBackground(FontContrastTheme theme)
        => theme == FontContrastTheme.Light
            ? FontContrastColor.FromArgb(0xFF, 0xF3, 0xF3, 0xF3)
            : FontContrastColor.FromArgb(0xFF, 0x20, 0x20, 0x20);

    public static double GetContrastRatio(FontContrastColor foreground, FontContrastColor background)
    {
        var blendedForeground = BlendOver(foreground, background);
        double foregroundLuminance = GetRelativeLuminance(blendedForeground);
        double backgroundLuminance = GetRelativeLuminance(background);
        double lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        double darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static FontContrastColor BlendOver(FontContrastColor foreground, FontContrastColor background)
    {
        double alpha = foreground.A / 255d;
        byte red = BlendChannel(foreground.R, background.R, alpha);
        byte green = BlendChannel(foreground.G, background.G, alpha);
        byte blue = BlendChannel(foreground.B, background.B, alpha);
        return FontContrastColor.FromArgb(0xFF, red, green, blue);
    }

    private static byte BlendChannel(byte foreground, byte background, double alpha)
        => (byte)Math.Round((foreground * alpha) + (background * (1d - alpha)));

    private static double GetRelativeLuminance(FontContrastColor color)
    {
        double red = Linearize(color.R / 255d);
        double green = Linearize(color.G / 255d);
        double blue = Linearize(color.B / 255d);
        return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
    }

    private static FontContrastDirection ChooseRecommendedDirection(FontContrastColor foreground, FontContrastColor background)
    {
        var blendedForeground = BlendOver(foreground, background);
        double foregroundLuminance = GetRelativeLuminance(blendedForeground);
        double backgroundLuminance = GetRelativeLuminance(background);

        if (Math.Abs(foregroundLuminance - backgroundLuminance) < 0.000001)
            return backgroundLuminance >= 0.5
                ? FontContrastDirection.Darker
                : FontContrastDirection.Lighter;

        return foregroundLuminance > backgroundLuminance
            ? FontContrastDirection.Darker
            : FontContrastDirection.Lighter;
    }

    private static double Linearize(double channel)
        => channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
}