using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Tests;

// ─── SearchResultCollection ─────────────────────────────────────────────

public class SearchResultCollectionCoverageTests
{
    private static SearchResult MakeResult(string path, string matchLine = "match")
    {
        return new SearchResult(path, 1, matchLine, 0, matchLine.Length,
            Array.Empty<string>(), Array.Empty<string>());
    }

    [Fact]
    public void Add_CreatesGroup_ReturnsTrue_OnFirstAdd()
    {
        var col = new SearchResultCollection();
        bool changed = col.Add(MakeResult(@"C:\a.txt"));
        Assert.True(changed);
        Assert.Single(col.AllGroups);
        Assert.Single(col.VisibleGroups);
    }

    [Fact]
    public void Add_SameFile_DoesNotCreateNewGroup()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt", "match1"));
        bool changed = col.Add(MakeResult(@"C:\a.txt", "match2"));
        Assert.False(changed);
        Assert.Single(col.AllGroups);
        Assert.Equal(2, col.AllGroups[0].Count);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt"));
        col.Add(MakeResult(@"C:\b.txt"));
        col.Clear();
        Assert.Empty(col.AllGroups);
        Assert.Empty(col.VisibleGroups);
    }

    [Fact]
    public void FileNameFilter_FiltersVisibleGroups()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\src\app.cs"));
        col.Add(MakeResult(@"C:\src\readme.md"));
        col.FileNameFilter = "app";
        col.ApplySortAndFilter();
        Assert.Single(col.VisibleGroups);
        Assert.Contains("app.cs", col.VisibleGroups[0].FileName);
    }

    [Fact]
    public void ResultFilter_FiltersByMatchLine()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt", "hello world"));
        col.Add(MakeResult(@"C:\b.txt", "goodbye world"));
        col.ResultFilter = "hello";
        col.ApplySortAndFilter();
        Assert.Single(col.VisibleGroups);
    }

    [Fact]
    public void ResultFilter_MatchesFilePath()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\special\file.txt", "no match here"));
        col.ResultFilter = "special";
        col.ApplySortAndFilter();
        Assert.Single(col.VisibleGroups);
    }

    [Theory]
    [InlineData(0)] // match count
    [InlineData(1)] // date
    [InlineData(2)] // size
    public void SortModeIndex_AllModes_DoNotThrow(int mode)
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt"));
        col.Add(MakeResult(@"C:\b.txt"));
        col.SortModeIndex = mode;
        col.SortDirectionIndex = 0; // descending
        col.ApplySortAndFilter();
        Assert.Equal(2, col.VisibleGroups.Count);
    }

    [Theory]
    [InlineData(0)] // descending
    [InlineData(1)] // ascending
    public void SortDirectionIndex_BothDirections(int dir)
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt"));
        col.Add(MakeResult(@"C:\b.txt"));
        col.SortDirectionIndex = dir;
        col.ApplySortAndFilter();
        Assert.Equal(2, col.VisibleGroups.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GroupByDirectory_WithAllSortModes(int mode)
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\dir1\a.txt"));
        col.Add(MakeResult(@"C:\dir2\b.txt"));
        col.GroupByDirectory = true;
        col.SortModeIndex = mode;
        col.ApplySortAndFilter();
        Assert.Equal(2, col.VisibleGroups.Count);
        // First group should have a header set
        Assert.NotNull(col.VisibleGroups[0].GroupHeaderText);
    }

    [Fact]
    public void GroupByDirectory_Ascending()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\zzz\a.txt"));
        col.Add(MakeResult(@"C:\aaa\b.txt"));
        col.GroupByDirectory = true;
        col.SortDirectionIndex = 1; // ascending
        col.ApplySortAndFilter();
        Assert.Equal(2, col.VisibleGroups.Count);
    }

    [Fact]
    public void EvictAll_NullStore_ReturnsZero()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt"));
        int evicted = col.EvictAll(null);
        Assert.Equal(0, evicted);
    }

    [Fact]
    public void EvictAll_WithStore_EvictsAllResults()
    {
        using var store = new ResultStore();
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt", "line1"));
        col.Add(MakeResult(@"C:\b.txt", "line2"));
        int evicted = col.EvictAll(store);
        Assert.Equal(2, evicted);
        // Already evicted, second call evicts nothing
        Assert.Equal(0, col.EvictAll(store));
    }

    [Fact]
    public void AddRange_WithEviction()
    {
        using var store = new ResultStore();
        var col = new SearchResultCollection();
        var results = new[]
        {
            MakeResult(@"C:\a.txt", "match1"),
            MakeResult(@"C:\b.txt", "match2"),
        };
        bool changed = col.AddRange(results, evictNewResults: true, resultStore: store);
        Assert.True(changed);
        Assert.Equal(2, col.AllGroups.Count);
        // Results should be evicted
        Assert.True(col.AllGroups[0][0].IsEvicted);
        Assert.True(col.AllGroups[1][0].IsEvicted);
    }

    [Fact]
    public void AddRange_WithoutEviction()
    {
        var col = new SearchResultCollection();
        var results = new[]
        {
            MakeResult(@"C:\a.txt"),
            MakeResult(@"C:\a.txt"),
        };
        bool changed = col.AddRange(results);
        Assert.True(changed);
        Assert.Single(col.AllGroups);
        Assert.Equal(2, col.AllGroups[0].Count);
    }

    [Fact]
    public void GetAllSelectedResults_ReturnsOnlySelected()
    {
        var col = new SearchResultCollection();
        var r1 = MakeResult(@"C:\a.txt");
        var r2 = MakeResult(@"C:\a.txt", "other");
        col.Add(r1);
        col.Add(r2);
        r1.IsSelected = true;

        var selected = col.GetAllSelectedResults();
        Assert.Single(selected);
        Assert.Same(r1, selected[0]);
    }

    [Fact]
    public void GetAllSelectedResults_NoneSelected_ReturnsEmpty()
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt"));
        Assert.Empty(col.GetAllSelectedResults());
    }
}

// ─── SearchResultCollection: sort branches ──────────────────────────────

public class SearchResultCollectionSortBranchTests
{
    private static SearchResult MakeResult(string path) =>
        new(path, 1, "x", 0, 1, [], []);

    [Theory]
    [InlineData(1, true)]  // LastModified ascending, grouped
    [InlineData(2, true)]  // FileSize ascending, grouped
    [InlineData(1, false)] // LastModified ascending, non-grouped
    [InlineData(2, false)] // FileSize ascending, non-grouped
    public void AscendingSortModes_AllCombinations(int mode, bool grouped)
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\dir1\a.txt"));
        col.Add(MakeResult(@"C:\dir2\b.txt"));
        col.GroupByDirectory = grouped;
        col.SortModeIndex = mode;
        col.SortDirectionIndex = 1; // ascending
        col.ApplySortAndFilter();
        Assert.Equal(2, col.VisibleGroups.Count);
    }
}

// ─── SearchResultCollection: Add with initializeNewGroup ────────────────

public class SearchResultCollectionInitGroupTests
{
    [Fact]
    public void Add_WithInitializeNewGroup_InvokesDelegate()
    {
        var col = new SearchResultCollection();
        bool invoked = false;
        col.Add(
            new SearchResult(@"C:\a.txt", 1, "x", 0, 1, [], []),
            initializeNewGroup: g => { invoked = true; });
        Assert.True(invoked);
    }
}
