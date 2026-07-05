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
    public void WarningDialog_UsesSharedCustomDialogWindow()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "FontContrastWarningDialog.cs"));

        Assert.Contains("YaguDialog.ShowAsync", source);
        Assert.Contains("YaguDialogResult.Primary", source);
        Assert.Contains("YaguDialogResult.Secondary", source);
        Assert.DoesNotContain("ContentDialog", source);
        Assert.DoesNotContain("XamlRoot", source);
        Assert.DoesNotContain("SetBorderAndTitleBar", source);
    }

    [Fact]
    public void ViewModel_CreatesSelectedAndUnselectedPreviewContrastCandidates()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "ViewModels", "MainViewModel.cs"));

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

public sealed class FontContrastColorTests
{
    [Fact]
    public void Parse_ReturnsColorFrom8CharHex()
    {
        var color = FontContrastColor.Parse("#80FF0000", FontContrastColor.FromArgb(0, 0, 0, 0));
        Assert.Equal(0x80, color.A);
        Assert.Equal(0xFF, color.R);
        Assert.Equal(0x00, color.G);
        Assert.Equal(0x00, color.B);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("#GGG")]
    public void Parse_InvalidInput_ReturnsFallback(string? value)
    {
        var fallback = FontContrastColor.FromArgb(0xFF, 0xAA, 0xBB, 0xCC);
        var result = FontContrastColor.Parse(value, fallback);
        Assert.Equal(fallback, result);
    }

    [Fact]
    public void Parse_6CharHex_SetsFullAlpha()
    {
        var color = FontContrastColor.Parse("#FF8800", FontContrastColor.FromArgb(0, 0, 0, 0));
        Assert.Equal(0xFF, color.A);
        Assert.Equal(0xFF, color.R);
        Assert.Equal(0x88, color.G);
        Assert.Equal(0x00, color.B);
    }

    [Fact]
    public void ToHex_FormatsCorrectly()
    {
        var color = FontContrastColor.FromArgb(0x80, 0x10, 0x20, 0x30);
        Assert.Equal("#80102030", color.ToHex());
    }
}

public sealed class FontContrastWarningServiceContrastTests
{
    [Fact]
    public void FindFirstIssue_ReturnsNullWhenNoCandidatesHaveIssues()
    {
        var candidates = new[]
        {
            new FontContrastCandidate("a", "surface", "label", "#FF000000", FontContrastColor.FromArgb(0xFF, 0, 0, 0)),
        };

        var issue = FontContrastWarningService.FindFirstIssue(candidates, FontContrastTheme.Light);
        Assert.Null(issue);
    }

    [Fact]
    public void FindFirstIssue_ReturnsFirstBadCandidate()
    {
        var candidates = new[]
        {
            new FontContrastCandidate("good", "surface", "label", "#FF000000", FontContrastColor.FromArgb(0xFF, 0, 0, 0)),
            new FontContrastCandidate("bad", "surface", "label", "#FFF3F3F3", FontContrastColor.FromArgb(0xFF, 0xF3, 0xF3, 0xF3)),
        };

        var issue = FontContrastWarningService.FindFirstIssue(candidates, FontContrastTheme.Light);
        Assert.NotNull(issue);
        Assert.Equal("bad", issue.Candidate.Key);
    }

    [Fact]
    public void GetContrastRatio_BlackOnWhite_HighContrast()
    {
        var black = FontContrastColor.FromArgb(0xFF, 0, 0, 0);
        var white = FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double ratio = FontContrastWarningService.GetContrastRatio(black, white);
        Assert.True(ratio > 20);
    }

    [Fact]
    public void GetContrastRatio_WhiteOnWhite_LowContrast()
    {
        var white = FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double ratio = FontContrastWarningService.GetContrastRatio(white, white);
        Assert.Equal(1.0, ratio, precision: 2);
    }

    [Fact]
    public void GetContrastRatio_SemiTransparentForeground_BlendsCorrectly()
    {
        var semiBlack = FontContrastColor.FromArgb(0x80, 0, 0, 0);
        var white = FontContrastColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double ratio = FontContrastWarningService.GetContrastRatio(semiBlack, white);
        Assert.True(ratio > 1.0);
        Assert.True(ratio < 21.0);
    }

    [Fact]
    public void GetThemeSampleBackground_ReturnsExpectedValues()
    {
        var dark = FontContrastWarningService.GetThemeSampleBackground(FontContrastTheme.Dark);
        Assert.Equal(0x20, dark.R);
        Assert.Equal(0x20, dark.G);
        Assert.Equal(0x20, dark.B);

        var light = FontContrastWarningService.GetThemeSampleBackground(FontContrastTheme.Light);
        Assert.Equal(0xF3, light.R);
        Assert.Equal(0xF3, light.G);
        Assert.Equal(0xF3, light.B);
    }

    [Fact]
    public void ResolveBackground_NullBackground_ReturnsThemeBackground()
    {
        var bg = FontContrastWarningService.ResolveBackground(null, FontContrastTheme.Dark);
        Assert.Equal(FontContrastWarningService.GetThemeSampleBackground(FontContrastTheme.Dark), bg);
    }

    [Fact]
    public void ResolveBackground_OpaqueBackground_ReturnsAsIs()
    {
        var custom = FontContrastColor.FromArgb(0xFF, 0x40, 0x40, 0x40);
        var bg = FontContrastWarningService.ResolveBackground(custom, FontContrastTheme.Dark);
        Assert.Equal(custom, bg);
    }

    [Fact]
    public void TryCreateIssue_EqualLuminance_RecommendsDarkerForLightBackground()
    {
        var lightBg = FontContrastWarningService.GetThemeSampleBackground(FontContrastTheme.Light);
        var candidate = new FontContrastCandidate(
            "test", "surface", "label",
            $"#{lightBg.A:X2}{lightBg.R:X2}{lightBg.G:X2}{lightBg.B:X2}",
            lightBg);

        bool created = FontContrastWarningService.TryCreateIssue(candidate, FontContrastTheme.Light, out var issue);
        Assert.True(created);
        Assert.NotNull(issue);
        Assert.Equal(FontContrastDirection.Darker, issue.RecommendedDirection);
    }
}