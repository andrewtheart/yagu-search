using Yagu.Services;

namespace Yagu.Tests;

public sealed class FontContrastWarningServiceTests
{
    [Fact]
    public void TryCreateIssue_FlagsWhiteTextOnLightTheme()
    {
        var candidate = new FontContrastCandidate(
            "PreviewMatchLineColor",
            "preview pane",
            "Matched line text",
            "#FFFFFFFF",
            FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        bool created = FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Light, out var issue);

        Assert.True(created);
        Assert.NotNull(issue);
        Assert.Equal(FontContrastDirection.Darker, issue.RecommendedDirection);
    }

    [Fact]
    public void TryCreateIssue_FlagsBlackTextOnDarkTheme()
    {
        var candidate = new FontContrastCandidate(
            "PreviewMatchLineColor",
            "preview pane",
            "Matched line text",
            "#FF000000",
            FontContrastColor.FromArgb(0xFF, 0x00, 0x00, 0x00));

        bool created = FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Dark, out var issue);

        Assert.True(created);
        Assert.NotNull(issue);
        Assert.Equal(FontContrastDirection.Lighter, issue.RecommendedDirection);
    }

    [Theory]
    [InlineData("#FF202020", FontContrastTheme.Light)]
    [InlineData("#FFFFFFFF", FontContrastTheme.Dark)]
    public void TryCreateIssue_AllowsReadableText(string color, FontContrastTheme theme)
    {
        var candidate = new FontContrastCandidate(
            "PreviewMatchLineColor",
            "preview pane",
            "Matched line text",
            color,
            FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        Assert.False(FontContrastWarningService.TryCreateIssue(candidate, theme, out _));
    }

    [Fact]
    public void TryCreateIssue_AllowsBorderlineMagentaOnLightTheme()
    {
        var candidate = new FontContrastCandidate(
            "PreviewMatchTextColor",
            "preview pane",
            "Match highlight text",
            "#FFFF00FF",
            FontContrastColor.FromArgb(0xFF, 0xFF, 0xD7, 0x00));

        Assert.False(FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Light, out _));
    }

    [Fact]
    public void ShouldCheck_RespectsSuppressAndSnooze()
    {
        var now = new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.False(FontContrastWarningService.ShouldCheck(true, null, now));
        Assert.False(FontContrastWarningService.ShouldCheck(false, now.AddMinutes(1), now));
        Assert.True(FontContrastWarningService.ShouldCheck(false, now.AddMinutes(-1), now));
        Assert.True(FontContrastWarningService.ShouldCheck(false, null, now));
    }
}