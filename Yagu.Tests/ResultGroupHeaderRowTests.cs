using Yagu.Models;

namespace Yagu.Tests;

public sealed class ResultGroupHeaderRowTests
{
    [Theory]
    [InlineData(1, 1, "1 file | 1 match")]
    [InlineData(2, 1, "2 files | 1 match")]
    [InlineData(1, 5, "1 file | 5 matches")]
    [InlineData(100, 200, "100 files | 200 matches")]
    public void SummaryText_PluralizeCorrectly(int fileCount, int matchCount, string expected)
    {
        var row = new ResultGroupHeaderRow("key", "Title", fileCount, matchCount, isExpanded: true);
        Assert.Equal(expected, row.SummaryText);
    }

    [Fact]
    public void ChevronGlyph_ReflectsExpandedState()
    {
        var row = new ResultGroupHeaderRow("k", "t", 1, 1, isExpanded: true);
        Assert.Equal("\uE70D", row.ChevronGlyph);

        row.IsExpanded = false;
        Assert.Equal("\uE76C", row.ChevronGlyph);
    }

    [Fact]
    public void IsExpanded_SameValue_DoesNotFirePropertyChanged()
    {
        var row = new ResultGroupHeaderRow("k", "t", 1, 1, isExpanded: true);
        var fired = new List<string>();
        row.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        row.IsExpanded = true; // same value
        Assert.Empty(fired);
    }

    [Fact]
    public void IsExpanded_DifferentValue_FiresPropertyChangedForBothProperties()
    {
        var row = new ResultGroupHeaderRow("k", "t", 1, 1, isExpanded: false);
        var fired = new List<string>();
        row.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        row.IsExpanded = true;

        Assert.Contains(nameof(ResultGroupHeaderRow.IsExpanded), fired);
        Assert.Contains(nameof(ResultGroupHeaderRow.ChevronGlyph), fired);
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var row = new ResultGroupHeaderRow("myKey", "My Title", 7, 42, isExpanded: false);

        Assert.Equal("myKey", row.Key);
        Assert.Equal("My Title", row.Title);
        Assert.Equal(7, row.FileCount);
        Assert.Equal(42, row.MatchCount);
        Assert.False(row.IsExpanded);
    }
}
