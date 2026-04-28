using QuickGrep.Models;

namespace QuickGrep.Tests;

// ─── SearchProgress ─────────────────────────────────────────────────────

public class SearchProgressCoverageTests
{
    [Fact]
    public void AllProperties_Accessible()
    {
        var elapsed = TimeSpan.FromSeconds(5);
        var sp = new SearchProgress(100, 200, 50, 30, 10, 1024L * 1024, elapsed, 3);

        Assert.Equal(100, sp.FilesScanned);
        Assert.Equal(200, sp.TotalFiles);
        Assert.Equal(50, sp.MatchesFound);
        Assert.Equal(30, sp.FilesWithMatches);
        Assert.Equal(10, sp.FilesSkipped);
        Assert.Equal(1024L * 1024, sp.BytesScanned);
        Assert.Equal(elapsed, sp.Elapsed);
        Assert.Equal(3, sp.AccessDenied);
    }

    [Fact]
    public void DefaultAccessDenied_IsZero()
    {
        var sp = new SearchProgress(0, 0, 0, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, sp.AccessDenied);
    }

    [Fact]
    public void RecordEquality()
    {
        var a = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.FromSeconds(1), 7);
        var b = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.FromSeconds(1), 7);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Deconstruction()
    {
        var sp = new SearchProgress(1, 2, 3, 4, 5, 6, TimeSpan.FromSeconds(7), 8);
        var (fs, tf, mf, fwm, fsk, bs, el, ad) = sp;
        Assert.Equal(1, fs);
        Assert.Equal(2, tf);
        Assert.Equal(3, mf);
        Assert.Equal(4, fwm);
        Assert.Equal(5, fsk);
        Assert.Equal(6L, bs);
        Assert.Equal(TimeSpan.FromSeconds(7), el);
        Assert.Equal(8, ad);
    }
}

// ─── SearchSummary + SkipBreakdown ──────────────────────────────────────

public class SearchSummaryCoverageTests
{
    [Fact]
    public void AllProperties_Accessible()
    {
        var skip = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9);
        var ss = new SearchSummary(
            TotalFiles: 100,
            FilesScanned: 80,
            FilesSkipped: 20,
            FilesWithMatches: 50,
            TotalMatches: 300,
            BytesScanned: 999_999,
            Elapsed: TimeSpan.FromMinutes(1),
            Cancelled: true,
            Truncated: true,
            Degraded: true,
            FallbackReason: "Everything not running",
            SkipReasons: skip);

        Assert.Equal(100, ss.TotalFiles);
        Assert.Equal(80, ss.FilesScanned);
        Assert.Equal(20, ss.FilesSkipped);
        Assert.Equal(50, ss.FilesWithMatches);
        Assert.Equal(300, ss.TotalMatches);
        Assert.Equal(999_999L, ss.BytesScanned);
        Assert.Equal(TimeSpan.FromMinutes(1), ss.Elapsed);
        Assert.True(ss.Cancelled);
        Assert.True(ss.Truncated);
        Assert.True(ss.Degraded);
        Assert.Equal("Everything not running", ss.FallbackReason);
        Assert.NotNull(ss.SkipReasons);
        Assert.Equal(1, ss.SkipReasons!.Binary);
        Assert.Equal(2, ss.SkipReasons.AccessDenied);
        Assert.Equal(3, ss.SkipReasons.IOError);
        Assert.Equal(4, ss.SkipReasons.TooLarge);
        Assert.Equal(5, ss.SkipReasons.NotFound);
        Assert.Equal(6, ss.SkipReasons.Encoding);
        Assert.Equal(7, ss.SkipReasons.Other);
        Assert.Equal(8, ss.SkipReasons.ByExtension);
        Assert.Equal(9, ss.SkipReasons.Directories);
    }

    [Fact]
    public void NullFallbackReason_And_NullSkipReasons()
    {
        var ss = new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null);
        Assert.Null(ss.FallbackReason);
        Assert.Null(ss.SkipReasons);
    }

    [Fact]
    public void SkipBreakdown_DefaultOptionalParams()
    {
        var sb = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7);
        Assert.Equal(0, sb.ByExtension);
        Assert.Equal(0, sb.Directories);
    }

    [Fact]
    public void SkipBreakdown_ToString_ContainsAllFields()
    {
        var sb = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9);
        var str = sb.ToString();
        Assert.Contains("binary=1", str);
        Assert.Contains("accessDenied=2", str);
        Assert.Contains("ioError=3", str);
        Assert.Contains("tooLarge=4", str);
        Assert.Contains("notFound=5", str);
        Assert.Contains("encoding=6", str);
        Assert.Contains("other=7", str);
        Assert.Contains("byExtension=8", str);
        Assert.Contains("directories=9", str);
    }

    [Fact]
    public void SkipBreakdown_RecordEquality()
    {
        var a = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9);
        var b = new SkipBreakdown(1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SearchSummary_RecordEquality()
    {
        var a = new SearchSummary(1, 2, 3, 4, 5, 6, TimeSpan.Zero, false, false, false, "reason");
        var b = new SearchSummary(1, 2, 3, 4, 5, 6, TimeSpan.Zero, false, false, false, "reason");
        Assert.Equal(a, b);
    }
}
