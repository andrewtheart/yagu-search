using Yagu.Helpers;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Exhaustive branch/line coverage for <see cref="MatchNavMath"/> — the pure match-navigation
/// arithmetic and label formatting extracted from <c>MainWindow.MatchNav.cs</c>.
/// </summary>
public class MatchNavMathTests
{
    // ── FormatOccurrenceLabel ────────────────────────────────────────────────────

    [Fact]
    public void FormatOccurrenceLabel_UsesOneBasedIndexAndSingularFileWord()
    {
        Assert.Equal("Occurrence 1/3 (1 file)", MatchNavMath.FormatOccurrenceLabel(0, 3, 1));
    }

    [Theory]
    [InlineData(0, "Occurrence 3/9 (0 files)")]   // zero files -> plural
    [InlineData(2, "Occurrence 3/9 (2 files)")]   // many files -> plural
    public void FormatOccurrenceLabel_PluralizesWhenFileCountIsNotOne(int fileCount, string expected)
    {
        Assert.Equal(expected, MatchNavMath.FormatOccurrenceLabel(2, 9, fileCount));
    }

    // ── FormatSectionOccurrenceLabel ─────────────────────────────────────────────

    [Fact]
    public void FormatSectionOccurrenceLabel_ClampsDisplayIndexLowAndPluralizes()
    {
        // currentIndex + 1 == 0, clamped up to 1; total > 1 -> "matches".
        Assert.Equal("Occurrence 1/5 (5 matches in file)", MatchNavMath.FormatSectionOccurrenceLabel(-1, 5));
    }

    [Fact]
    public void FormatSectionOccurrenceLabel_PassesThroughInRangeIndex()
    {
        Assert.Equal("Occurrence 3/5 (5 matches in file)", MatchNavMath.FormatSectionOccurrenceLabel(2, 5));
    }

    [Fact]
    public void FormatSectionOccurrenceLabel_ClampsDisplayIndexHighToTotal()
    {
        // currentIndex + 1 == 11, clamped down to total (5).
        Assert.Equal("Occurrence 5/5 (5 matches in file)", MatchNavMath.FormatSectionOccurrenceLabel(10, 5));
    }

    [Fact]
    public void FormatSectionOccurrenceLabel_UsesSingularMatchWordForSingleMatch()
    {
        Assert.Equal("Occurrence 1/1 (1 match in file)", MatchNavMath.FormatSectionOccurrenceLabel(0, 1));
    }

    // ── StableFileCount ──────────────────────────────────────────────────────────

    [Fact]
    public void StableFileCount_PrefersRegisteredTotalWhenPositive()
    {
        Assert.Equal(5, MatchNavMath.StableFileCount(previewTotalFileCount: 5, renderedFileCount: 2, deferredFiles: 3));
    }

    [Theory]
    [InlineData(0, 2, 3, 5)]    // no registered total -> rendered + deferred
    [InlineData(-1, 4, 1, 5)]   // negative registered total -> fallback branch
    public void StableFileCount_FallsBackToRenderedPlusDeferred(int registered, int rendered, int deferred, int expected)
    {
        Assert.Equal(expected, MatchNavMath.StableFileCount(registered, rendered, deferred));
    }

    // ── ResolveCurrentIndex ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveCurrentIndex_ActiveMatchWins()
    {
        Assert.Equal(3, MatchNavMath.ResolveCurrentIndex(activeIndex: 3, currentIndex: 0, matchCount: 10));
    }

    [Fact]
    public void ResolveCurrentIndex_NoActive_ClampsNegativeToFirstMatch()
    {
        Assert.Equal(0, MatchNavMath.ResolveCurrentIndex(activeIndex: -1, currentIndex: -1, matchCount: 5));
    }

    [Fact]
    public void ResolveCurrentIndex_NoActive_ClampsOverflowToLastMatch()
    {
        Assert.Equal(4, MatchNavMath.ResolveCurrentIndex(activeIndex: -1, currentIndex: 10, matchCount: 5));
    }

    [Fact]
    public void ResolveCurrentIndex_NoActive_KeepsInRangeIndex()
    {
        Assert.Equal(2, MatchNavMath.ResolveCurrentIndex(activeIndex: -1, currentIndex: 2, matchCount: 5));
    }

    [Fact]
    public void ResolveCurrentIndex_NoMatches_ResetsNegativeIndexToZero()
    {
        Assert.Equal(0, MatchNavMath.ResolveCurrentIndex(activeIndex: -1, currentIndex: -1, matchCount: 0));
    }

    [Fact]
    public void ResolveCurrentIndex_NoMatches_PreservesNonNegativeIndex()
    {
        Assert.Equal(7, MatchNavMath.ResolveCurrentIndex(activeIndex: -1, currentIndex: 7, matchCount: 0));
    }

    // ── PrevIndexWithWrap ────────────────────────────────────────────────────────

    [Fact]
    public void PrevIndexWithWrap_WrapsFromFirstToLastAndReportsWrap()
    {
        var (index, wrappedToEnd) = MatchNavMath.PrevIndexWithWrap(currentIndex: 0, count: 5);
        Assert.Equal(4, index);
        Assert.True(wrappedToEnd);
    }

    [Theory]
    [InlineData(3, 5, 2)]
    [InlineData(1, 5, 0)]
    public void PrevIndexWithWrap_StepsBackWithoutWrapping(int currentIndex, int count, int expectedIndex)
    {
        var (index, wrappedToEnd) = MatchNavMath.PrevIndexWithWrap(currentIndex, count);
        Assert.Equal(expectedIndex, index);
        Assert.False(wrappedToEnd);
    }

    [Fact]
    public void PrevIndexWithWrap_SingleMatchStaysAtZeroAndWraps()
    {
        var (index, wrappedToEnd) = MatchNavMath.PrevIndexWithWrap(currentIndex: 0, count: 1);
        Assert.Equal(0, index);
        Assert.True(wrappedToEnd);
    }
}
