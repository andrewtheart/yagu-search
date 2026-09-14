using Yagu.Services.Cli;

namespace Yagu.Tests;

/// <summary>
/// Covers the <c>--lines</c> window used by the CLI <c>--read</c> command. Reads are always bounded, so
/// an unparseable or inverted range must fail loudly instead of silently widening to the whole file.
/// </summary>
public class CliLineRangeTests
{
    [Theory]
    [InlineData("1-10", 1, 10)]
    [InlineData(" 25 - 40 ", 25, 40)]
    [InlineData("7", 7, 7)]
    public void TryParse_BoundedWindow_IsAccepted(string raw, int first, int last)
    {
        Assert.True(CliLineRange.TryParse(raw, out var range, out string? error));

        Assert.Null(error);
        Assert.Equal(first, range.First);
        Assert.Equal(last, range.Last);
        Assert.False(range.IsOpenEnded);
    }

    [Fact]
    public void TryParse_OpenEndedWindow_LetsTheBudgetDecideWhereToStop()
    {
        Assert.True(CliLineRange.TryParse("500-", out var range, out _));

        Assert.Equal(500, range.First);
        Assert.True(range.IsOpenEnded);
        Assert.Equal(int.MaxValue, range.Last);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("0-10")]
    [InlineData("-5")]
    [InlineData("3-x")]
    [InlineData("10-2")]
    public void TryParse_UnusableValue_IsRejectedWithAnExplanation(string? raw)
    {
        Assert.False(CliLineRange.TryParse(raw, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Contains_MatchesTheInclusiveWindow()
    {
        var range = new CliLineRange(10, 12);

        Assert.False(range.Contains(9));
        Assert.True(range.Contains(10));
        Assert.True(range.Contains(12));
        Assert.False(range.Contains(13));
    }

    [Fact]
    public void Count_SaturatesForAnOpenEndedWindow()
    {
        Assert.Equal(3, new CliLineRange(10, 12).Count);
        Assert.Equal(int.MaxValue, CliLineRange.All.Count);
    }
}
