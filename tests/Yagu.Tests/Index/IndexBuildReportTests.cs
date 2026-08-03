using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="IndexBuildReport"/> (plan §6.2): indexed/skipped accounting, exact per-reason
/// counts, bounded per-path entries, truncation flag, and the one-line summary.
/// </summary>
public sealed class IndexBuildReportTests
{
    [Fact]
    public void Record_None_CountsAsIndexed()
    {
        var report = new IndexBuildReport();
        report.Record("a.txt", IndexSkipReason.None);
        report.RecordIndexed();
        Assert.Equal(2, report.IndexedCount);
        Assert.Equal(0, report.TotalSkipped);
        Assert.Empty(report.Entries);
    }

    [Fact]
    public void Record_Skips_AccumulateCountsAndEntries()
    {
        var report = new IndexBuildReport();
        report.Record(@"C:\a.bin", IndexSkipReason.Binary);
        report.Record(@"C:\b.bin", IndexSkipReason.Binary);
        report.Record(@"C:\c.big", IndexSkipReason.OverSizeCap);

        Assert.Equal(3, report.TotalSkipped);
        Assert.Equal(2, report.SkipCount(IndexSkipReason.Binary));
        Assert.Equal(1, report.SkipCount(IndexSkipReason.OverSizeCap));
        Assert.Equal(0, report.SkipCount(IndexSkipReason.Hidden));
        Assert.Equal(3, report.Entries.Count);
        Assert.False(report.EntriesTruncated);

        // The exact per-reason map is exposed for the status UI.
        Assert.Equal(2, report.SkipCounts[IndexSkipReason.Binary]);
        Assert.Equal(1, report.SkipCounts[IndexSkipReason.OverSizeCap]);
        Assert.False(report.SkipCounts.ContainsKey(IndexSkipReason.Hidden));
    }

    [Fact]
    public void Entries_AreBounded_AndTruncationFlagged()
    {
        var report = new IndexBuildReport(maxEntries: 2);
        report.Record("a", IndexSkipReason.Binary);
        report.Record("b", IndexSkipReason.Binary);
        report.Record("c", IndexSkipReason.Binary); // dropped from entries

        Assert.Equal(3, report.TotalSkipped);          // exact count still increments
        Assert.Equal(3, report.SkipCount(IndexSkipReason.Binary));
        Assert.Equal(2, report.Entries.Count);          // capped
        Assert.True(report.EntriesTruncated);
    }

    [Fact]
    public void Summarize_NoSkips()
    {
        var report = new IndexBuildReport();
        report.RecordIndexed();
        report.RecordIndexed();
        Assert.Equal("2 indexed, 0 skipped.", report.Summarize());
    }

    [Fact]
    public void Summarize_WithSkips_ListsReasonsByFrequency()
    {
        var report = new IndexBuildReport();
        report.RecordIndexed();
        report.Record("a", IndexSkipReason.Binary);
        report.Record("b", IndexSkipReason.Binary);
        report.Record("c", IndexSkipReason.OverSizeCap);

        string summary = report.Summarize();
        Assert.Contains("1 indexed", summary);
        Assert.Contains("3 skipped", summary);
        Assert.Contains("Binary=2", summary);
        Assert.Contains("OverSizeCap=1", summary);
        // Most frequent reason listed first.
        Assert.True(summary.IndexOf("Binary=2", System.StringComparison.Ordinal)
            < summary.IndexOf("OverSizeCap=1", System.StringComparison.Ordinal));
    }
}
