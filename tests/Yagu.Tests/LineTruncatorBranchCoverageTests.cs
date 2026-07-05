using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Comprehensive branch coverage for LineTruncator including edge cases,
/// boundary values, and TruncateAroundMatch positioning.
/// </summary>
public sealed class LineTruncatorBranchCoverageTests : IDisposable
{
    private readonly int _originalLength;

    public LineTruncatorBranchCoverageTests()
    {
        _originalLength = LineTruncator.TruncatedLength;
    }

    public void Dispose()
    {
        LineTruncator.TruncatedLength = _originalLength;
    }

    // ═══════════════════════════════════════════════════════════════
    //  TruncatedLength property
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TruncatedLength_SetToZero_DisablesTruncation()
    {
        LineTruncator.TruncatedLength = 0;
        Assert.Equal(0, LineTruncator.TruncatedLength);

        var longLine = new string('x', 5000);
        Assert.Equal(longLine, LineTruncator.Truncate(longLine));
    }

    [Fact]
    public void TruncatedLength_SetBelowMinimum_ClampsTo50()
    {
        LineTruncator.TruncatedLength = 10;
        Assert.Equal(50, LineTruncator.TruncatedLength);
    }

    [Fact]
    public void MaxDisplayLength_IsTwiceTruncatedLength()
    {
        LineTruncator.TruncatedLength = 200;
        Assert.Equal(400, LineTruncator.MaxDisplayLength);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Truncate simple
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Truncate_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LineTruncator.Truncate(null!));
    }

    [Fact]
    public void Truncate_ShortLine_ReturnsUnchanged()
    {
        LineTruncator.TruncatedLength = 500;
        var line = "short line";
        Assert.Equal(line, LineTruncator.Truncate(line));
    }

    [Fact]
    public void Truncate_ExactlyMaxDisplayLength_ReturnsUnchanged()
    {
        LineTruncator.TruncatedLength = 100;
        var line = new string('a', 200); // exactly MaxDisplayLength
        Assert.Equal(line, LineTruncator.Truncate(line));
    }

    [Fact]
    public void Truncate_LongLine_TruncatesWithEllipsis()
    {
        LineTruncator.TruncatedLength = 100;
        var line = new string('a', 300); // > MaxDisplayLength (200)
        var result = LineTruncator.Truncate(line);
        Assert.Equal(100 + LineTruncator.Ellipsis.Length, result.Length);
        Assert.EndsWith(LineTruncator.Ellipsis, result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TruncateAroundMatch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TruncateAroundMatch_NullLine_ReturnsEmpty()
    {
        var result = LineTruncator.TruncateAroundMatch(null, 0, 5);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_ShortLine_ReturnsAsIs()
    {
        LineTruncator.TruncatedLength = 500;
        var line = "hello world";
        var result = LineTruncator.TruncateAroundMatch(line, 6, 5);
        Assert.Equal(line, result.Text);
        Assert.Equal(6, result.MatchStart);
    }

    [Fact]
    public void TruncateAroundMatch_DisabledTruncation_ReturnsFullLine()
    {
        LineTruncator.TruncatedLength = 0;
        var line = new string('x', 5000);
        var result = LineTruncator.TruncateAroundMatch(line, 2500, 10);
        Assert.Equal(line, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_MatchAtStart_NoPrefixEllipsis()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 200);
        var result = LineTruncator.TruncateAroundMatch(line, 0, 5);
        Assert.False(result.Text.StartsWith(LineTruncator.Ellipsis));
        Assert.EndsWith(LineTruncator.Ellipsis, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_MatchAtEnd_NoSuffixEllipsis()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 200);
        var result = LineTruncator.TruncateAroundMatch(line, 195, 5);
        Assert.StartsWith(LineTruncator.Ellipsis, result.Text);
        Assert.False(result.Text.EndsWith(LineTruncator.Ellipsis));
    }

    [Fact]
    public void TruncateAroundMatch_MatchInMiddle_BothEllipses()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 200);
        var result = LineTruncator.TruncateAroundMatch(line, 100, 5);
        Assert.StartsWith(LineTruncator.Ellipsis, result.Text);
        Assert.EndsWith(LineTruncator.Ellipsis, result.Text);
    }

    [Fact]
    public void TruncateAroundMatch_MatchStartPreserved()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 50) + "MATCH" + new string('b', 150);
        var result = LineTruncator.TruncateAroundMatch(line, 50, 5);
        // The match start in the display string should be >= 0
        Assert.True(result.MatchStart >= 0);
        Assert.True(result.MatchStart < result.Text.Length);
    }

    [Fact]
    public void TruncateAroundMatch_NegativeMatchStart_FallsBackToSimpleTruncate()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 200);
        var result = LineTruncator.TruncateAroundMatch(line, -1, 5);
        // Falls back to simple Truncate behavior
        Assert.Equal(-1, result.MatchStart);
    }

    [Fact]
    public void TruncateAroundMatch_ZeroMatchLength_FallsBackToSimpleTruncate()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 200);
        var result = LineTruncator.TruncateAroundMatch(line, 50, 0);
        // Falls back to simple Truncate
        Assert.Equal(50, result.MatchStart);
    }

    [Fact]
    public void TruncateAroundMatch_MatchStartBeyondLine_FallsBackToSimpleTruncate()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('a', 200);
        var result = LineTruncator.TruncateAroundMatch(line, 500, 5);
        Assert.Equal(500, result.MatchStart);
    }

    [Fact]
    public void TruncateAroundMatch_MatchLargerThanTruncatedLength()
    {
        LineTruncator.TruncatedLength = 50;
        var line = new string('M', 200);
        var result = LineTruncator.TruncateAroundMatch(line, 10, 100);
        // Should still produce a valid result
        Assert.True(result.Text.Length > 0);
        Assert.True(result.MatchStart >= 0);
    }
}
