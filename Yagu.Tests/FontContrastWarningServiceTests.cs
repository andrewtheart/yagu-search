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
    public void TryCreateIssue_UsesExplicitCandidateBackground()
    {
        var candidate = new FontContrastCandidate(
            "PreviewMatchLineColor",
            "selected preview content",
            "Matched line text",
            "#FFFFFFFF",
            FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        bool created = FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Dark, out var issue);

        Assert.True(created);
        Assert.NotNull(issue);
        Assert.Equal(FontContrastDirection.Darker, issue.RecommendedDirection);
        Assert.Equal(FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), issue.BackgroundColor);
    }

    [Fact]
    public void ResolveCandidateBackground_BlendsTransparentBackgroundOverTheme()
    {
        var candidate = new FontContrastCandidate(
            "PreviewMatchLineColor",
            "unselected preview content",
            "Matched line text",
            "#FFFFFFFF",
            FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            FontContrastColor.FromArgb(0x00, 0x00, 0x00, 0x00));

        var resolved = FontContrastWarningService.ResolveCandidateBackground(candidate, FontContrastTheme.Light);

        Assert.Equal(FontContrastWarningService.GetThemeSampleBackground(FontContrastTheme.Light), resolved);
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

    [Fact]
    public void WarningDialog_UsesContentDialogWithoutOsTitleBar()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "FontContrastWarningDialog.cs"));

        Assert.Contains("new ContentDialog", source);
        Assert.Contains("XamlRoot = xamlRoot", source);
        Assert.DoesNotContain(": Window", source);
        Assert.DoesNotContain("EnableWindow(", source);
        Assert.DoesNotContain("SetBorderAndTitleBar", source);
    }

    [Fact]
    public void ViewModel_CreatesSelectedAndUnselectedPreviewContrastCandidates()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("selectedPreviewBackground", source);
        Assert.Contains("unselectedPreviewBackground", source);
        Assert.Contains("\"selected preview content\"", source);
        Assert.Contains("\"unselected preview content\"", source);
        Assert.Contains("nameof(PreviewGutterContextColor), \"selected preview content\"", source);
        Assert.Contains("nameof(PreviewGutterContextColor), \"unselected preview content\"", source);
        Assert.Contains("nameof(PreviewMatchLineColor), \"selected preview content\"", source);
        Assert.Contains("nameof(PreviewMatchLineColor), \"unselected preview content\"", source);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}