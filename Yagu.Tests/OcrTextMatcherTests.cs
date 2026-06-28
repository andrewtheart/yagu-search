using System.Text.RegularExpressions;
using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class OcrTextMatcherTests
{
    [Fact]
    public void Match_EmptyOrNullText_ReturnsEmpty()
    {
        Assert.Empty(OcrTextMatcher.Match("a.png", null, null, "x", StringComparison.OrdinalIgnoreCase, 0, 0));
        Assert.Empty(OcrTextMatcher.Match("a.png", "", null, "x", StringComparison.OrdinalIgnoreCase, 0, 0));
    }

    [Fact]
    public void Match_LiteralFindsMatchesWithOneBasedLineNumbers()
    {
        string text = "alpha needle beta\nno hits here\nneedle again";

        var results = OcrTextMatcher.Match("img.png", text, null, "needle",
            StringComparison.OrdinalIgnoreCase, contextLines: 0, maxMatches: 0);

        Assert.Equal(2, results.Count);
        Assert.Equal("img.png", results[0].FilePath);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Equal(3, results[1].LineNumber);
        Assert.All(results, r => Assert.Equal("needle".Length, r.MatchLength));
    }

    [Fact]
    public void Match_IsCaseInsensitiveWhenRequested()
    {
        var results = OcrTextMatcher.Match("img.png", "A NEEDLE here", null, "needle",
            StringComparison.OrdinalIgnoreCase, 0, 0);

        Assert.Single(results);
    }

    [Fact]
    public void Match_RespectsMaxMatchesCap()
    {
        string text = "needle one\nneedle two\nneedle three";

        var results = OcrTextMatcher.Match("img.png", text, null, "needle",
            StringComparison.OrdinalIgnoreCase, 0, maxMatches: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Match_IncludesContextLines()
    {
        string text = "line one\nhas needle\nline three";

        var results = OcrTextMatcher.Match("img.png", text, null, "needle",
            StringComparison.OrdinalIgnoreCase, contextLines: 1, maxMatches: 0);

        var r = Assert.Single(results);
        Assert.Equal(2, r.LineNumber);
        Assert.Equal(new[] { "line one" }, r.ContextBefore);
        Assert.Equal(new[] { "line three" }, r.ContextAfter);
    }

    [Fact]
    public void Match_SupportsRegex()
    {
        var rx = new Regex("n..dle");

        var results = OcrTextMatcher.Match("img.png", "a noodle here", rx, null,
            StringComparison.Ordinal, 0, 0);

        Assert.Single(results);
    }

    [Fact]
    public void Match_MultipleMatchesOnSameLineProduceSeparateResults()
    {
        var results = OcrTextMatcher.Match("img.png", "needle and needle", null, "needle",
            StringComparison.OrdinalIgnoreCase, 0, 0);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(1, r.LineNumber));
    }

    [Fact]
    public void Match_NormalizesCrlfAndCrLineEndings()
    {
        string text = "first\r\nneedle\rthird";

        var results = OcrTextMatcher.Match("img.png", text, null, "needle",
            StringComparison.OrdinalIgnoreCase, 0, 0);

        var r = Assert.Single(results);
        Assert.Equal(2, r.LineNumber);
    }
}
