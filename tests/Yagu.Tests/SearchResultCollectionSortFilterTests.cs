using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Comprehensive coverage for SearchResultCollection sorting, filtering,
/// grouping, date range classification, and file size bucket classification.
/// </summary>
public sealed class SearchResultCollectionSortFilterTests
{
    private static SearchResult MakeResult(string filePath, string matchLine = "needle", int matchStart = 0, int matchLength = 6)
        => new(filePath, 1, matchLine, matchStart, matchLength, [], []);

    // ═══════════════════════════════════════════════════════════════
    //  Sorting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SortByMatchCount_Ascending()
    {
        var c = new SearchResultCollection { SortModeIndex = 1, SortDirectionIndex = 1 };
        c.Add(MakeResult(@"C:\a.cs")); // 1 match
        c.Add(MakeResult(@"C:\b.cs"));
        c.Add(MakeResult(@"C:\b.cs")); // 2 matches
        c.ApplySortAndFilter();

        Assert.Equal(@"C:\a.cs", c.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\b.cs", c.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void SortByMatchCount_Descending()
    {
        var c = new SearchResultCollection { SortModeIndex = 1, SortDirectionIndex = 0 };
        c.Add(MakeResult(@"C:\a.cs")); // 1 match
        c.Add(MakeResult(@"C:\b.cs"));
        c.Add(MakeResult(@"C:\b.cs")); // 2 matches
        c.ApplySortAndFilter();

        Assert.Equal(@"C:\b.cs", c.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\a.cs", c.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void SortByFileName_Ascending()
    {
        var c = new SearchResultCollection { SortModeIndex = 4, SortDirectionIndex = 1 };
        c.Add(MakeResult(@"C:\dir\zebra.cs"));
        c.Add(MakeResult(@"C:\dir\alpha.cs"));
        c.ApplySortAndFilter();

        Assert.Equal(@"C:\dir\alpha.cs", c.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\dir\zebra.cs", c.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void SortByFileName_Descending()
    {
        var c = new SearchResultCollection { SortModeIndex = 4, SortDirectionIndex = 0 };
        c.Add(MakeResult(@"C:\dir\zebra.cs"));
        c.Add(MakeResult(@"C:\dir\alpha.cs"));
        c.ApplySortAndFilter();

        Assert.Equal(@"C:\dir\zebra.cs", c.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\dir\alpha.cs", c.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void MultiCriteriaSorting_PrimaryThenSecondary()
    {
        var c = new SearchResultCollection();
        c.SetSortCriteria([
            new SortCriterion(1, 0), // match count descending
            new SortCriterion(4, 1), // file name ascending as tiebreaker
        ]);

        c.Add(MakeResult(@"C:\z.cs"));
        c.Add(MakeResult(@"C:\a.cs"));
        // Both have 1 match, so name decides
        c.ApplySortAndFilter();

        Assert.Equal(@"C:\a.cs", c.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\z.cs", c.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void ClearSortCriteria_ReturnsToNaturalOrder()
    {
        var c = new SearchResultCollection();
        c.SetSortCriteria([new SortCriterion(4, 1)]);
        c.ClearSortCriteria();
        c.SortModeIndex = 0;
        c.Add(MakeResult(@"C:\z.cs"));
        c.Add(MakeResult(@"C:\a.cs"));
        c.ApplySortAndFilter();

        // Natural insertion order preserved when no sort
        Assert.Equal(@"C:\z.cs", c.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\a.cs", c.VisibleGroups[1].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Filtering by FileName
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FileNameFilter_FiltersByFileName()
    {
        var c = new SearchResultCollection { FileNameFilter = "alpha" };
        c.Add(MakeResult(@"C:\dir\alpha.cs"));
        c.Add(MakeResult(@"C:\dir\beta.cs"));
        c.ApplySortAndFilter();

        Assert.Single(c.VisibleGroups);
        Assert.Equal(@"C:\dir\alpha.cs", c.VisibleGroups[0].FilePath);
    }

    [Fact]
    public void FileNameFilter_IsCaseInsensitive()
    {
        var c = new SearchResultCollection { FileNameFilter = "ALPHA" };
        c.Add(MakeResult(@"C:\dir\alpha.cs"));
        c.ApplySortAndFilter();

        Assert.Single(c.VisibleGroups);
    }

    [Fact]
    public void FileNameFilter_MatchesPath()
    {
        var c = new SearchResultCollection { FileNameFilter = "special-dir" };
        c.Add(MakeResult(@"C:\special-dir\file.cs"));
        c.Add(MakeResult(@"C:\other-dir\file.cs"));
        c.ApplySortAndFilter();

        Assert.Single(c.VisibleGroups);
        Assert.Equal(@"C:\special-dir\file.cs", c.VisibleGroups[0].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Glob Filtering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IncludeGlobs_FiltersToMatchingFiles()
    {
        var c = new SearchResultCollection { IncludeGlobs = "*.cs" };
        c.Add(MakeResult(@"C:\dir\file.cs"));
        c.Add(MakeResult(@"C:\dir\file.txt"));
        c.ApplySortAndFilter();

        Assert.Single(c.VisibleGroups);
        Assert.Equal(@"C:\dir\file.cs", c.VisibleGroups[0].FilePath);
    }

    [Fact]
    public void ExcludeGlobs_RemovesMatchingFiles()
    {
        var c = new SearchResultCollection { ExcludeGlobs = "*.txt" };
        c.Add(MakeResult(@"C:\dir\file.cs"));
        c.Add(MakeResult(@"C:\dir\file.txt"));
        c.ApplySortAndFilter();

        Assert.Single(c.VisibleGroups);
        Assert.Equal(@"C:\dir\file.cs", c.VisibleGroups[0].FilePath);
    }

    [Fact]
    public void RegexFilterMode_IncludeUsesRegex()
    {
        var c = new SearchResultCollection
        {
            IncludeGlobs = @"\.cs$",
            IncludeFilterMode = FilterPatternMode.Regex,
        };
        c.Add(MakeResult(@"C:\dir\file.cs"));
        c.Add(MakeResult(@"C:\dir\file.txt"));
        c.ApplySortAndFilter();

        Assert.Single(c.VisibleGroups);
        Assert.Equal(@"C:\dir\file.cs", c.VisibleGroups[0].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Extension Filtering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SetExtensionFilters_FiltersToMatchingExtensions()
    {
        var c = new SearchResultCollection();
        c.Add(MakeResult(@"C:\a.cs"));
        c.Add(MakeResult(@"C:\b.txt"));
        c.Add(MakeResult(@"C:\c.cs"));
        c.SetExtensionFilters(["cs"]);
        c.ApplySortAndFilter();

        Assert.Equal(2, c.VisibleGroups.Count);
        Assert.All(c.VisibleGroups, g => Assert.EndsWith(".cs", g.FilePath));
    }

    [Fact]
    public void GetExtensionFilterOptions_ReturnsAllExtensions()
    {
        var c = new SearchResultCollection();
        c.Add(MakeResult(@"C:\a.cs"));
        c.Add(MakeResult(@"C:\b.cs"));
        c.Add(MakeResult(@"C:\c.txt"));
        var options = c.GetExtensionFilterOptions();

        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.Extension == "cs");
        Assert.Contains(options, o => o.Extension == "txt");
    }

    // ═══════════════════════════════════════════════════════════════
    //  NormalizeExtensionFilter / FormatExtensionDisplayName
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("cs", "cs")]
    [InlineData(".cs", "cs")]
    [InlineData("*.cs", "cs")]
    [InlineData("  .txt  ", "txt")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeExtensionFilter_NormalizesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, SearchResultCollection.NormalizeExtensionFilter(input));
    }

    [Fact]
    public void NormalizeExtensionFilter_NoExtensionLabel_PreservesLabel()
    {
        string label = FileGroup.NoExtensionLabel;
        Assert.Equal(label, SearchResultCollection.NormalizeExtensionFilter(label));
    }

    [Theory]
    [InlineData("cs", ".cs")]
    [InlineData("txt", ".txt")]
    public void FormatExtensionDisplayName_PrependsDot(string extension, string expected)
    {
        Assert.Equal(expected, SearchResultCollection.FormatExtensionDisplayName(extension));
    }

    [Fact]
    public void FormatExtensionDisplayName_NoExtensionLabel_ReturnsLabelUnchanged()
    {
        Assert.Equal(FileGroup.NoExtensionLabel, SearchResultCollection.FormatExtensionDisplayName(FileGroup.NoExtensionLabel));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Grouping - Folder
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GroupByFolder_AssignsDirectoryHeaders()
    {
        var c = new SearchResultCollection { GroupMode = GroupMode.Folder };
        c.Add(MakeResult(@"C:\src\file1.cs"));
        c.Add(MakeResult(@"C:\src\file2.cs"));
        c.Add(MakeResult(@"C:\lib\file3.cs"));
        c.ApplySortAndFilter();

        var headers = c.VisibleGroups
            .Where(g => g.GroupHeaderText is not null)
            .Select(g => g.GroupHeaderText!)
            .ToList();

        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void GroupByExtension_AssignsExtensionHeaders()
    {
        var c = new SearchResultCollection { GroupMode = GroupMode.Extension };
        c.Add(MakeResult(@"C:\dir\a.cs"));
        c.Add(MakeResult(@"C:\dir\b.txt"));
        c.Add(MakeResult(@"C:\dir\c.cs"));
        c.ApplySortAndFilter();

        var headers = c.VisibleGroups
            .Where(g => g.GroupHeaderText is not null)
            .Select(g => g.GroupHeaderText!)
            .ToList();

        Assert.Equal(2, headers.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ClassifyFileSizeBucket
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, "< 1MB")]
    [InlineData(500_000, "< 1MB")]
    [InlineData(1_048_576, "1 - 5 MB")]
    [InlineData(5_242_880, "5 - 10MB")]
    [InlineData(10_485_760, "10 - 50 MB")]
    [InlineData(52_428_800, "50 - 100MB")]
    [InlineData(104_857_600, "100 - 500MB")]
    [InlineData(536_870_912, "500 - 1GB")]
    [InlineData(1_073_741_824, "1GB - 2GB")]
    [InlineData(2_147_483_648, "2GB - 5GB")]
    [InlineData(5_368_709_120, "5GB - 50GB")]
    [InlineData(53_687_091_200, "50GB - 100GB")]
    [InlineData(107_374_182_400, "100GB - 500GB")]
    [InlineData(549_755_813_888, "500GB - 1TB")]
    [InlineData(1_099_511_627_776, "1TB - 2TB")]
    [InlineData(11_000_000_000_000, "10TB+")]
    public void ClassifyFileSizeBucket_ReturnsExpectedBucket(long size, string expected)
    {
        Assert.Equal(expected, SearchResultCollection.ClassifyFileSizeBucket(size));
    }

    // ═══════════════════════════════════════════════════════════════
    //  MatchesDateRange
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MatchesDateRange_None_AlwaysTrue()
    {
        Assert.True(SearchResultCollection.MatchesDateRange(DateTime.MinValue, DateRangeFilter.None));
    }

    [Fact]
    public void MatchesDateRange_Default_ReturnsFalse()
    {
        Assert.False(SearchResultCollection.MatchesDateRange(default, DateRangeFilter.PastDay));
    }

    [Fact]
    public void MatchesDateRange_PastDay_RecentTrue()
    {
        Assert.True(SearchResultCollection.MatchesDateRange(DateTime.Now.AddHours(-12), DateRangeFilter.PastDay));
    }

    [Fact]
    public void MatchesDateRange_PastDay_OldFalse()
    {
        Assert.False(SearchResultCollection.MatchesDateRange(DateTime.Now.AddDays(-3), DateRangeFilter.PastDay));
    }

    [Theory]
    [InlineData(DateRangeFilter.PastWeek, -5, true)]
    [InlineData(DateRangeFilter.PastWeek, -10, false)]
    [InlineData(DateRangeFilter.PastMonth, -20, true)]
    [InlineData(DateRangeFilter.PastMonth, -40, false)]
    [InlineData(DateRangeFilter.PastYear, -300, true)]
    [InlineData(DateRangeFilter.PastYear, -400, false)]
    public void MatchesDateRange_VariousFilters(DateRangeFilter filter, int daysAgo, bool expected)
    {
        Assert.Equal(expected, SearchResultCollection.MatchesDateRange(DateTime.Now.AddDays(daysAgo), filter));
    }

    // ═══════════════════════════════════════════════════════════════
    //  ClassifyDateRangeBucket
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyDateRangeBucket_Default_ReturnsUnknown()
    {
        Assert.Equal("Unknown date", SearchResultCollection.ClassifyDateRangeBucket(default));
    }

    [Fact]
    public void ClassifyDateRangeBucket_Recent_ReturnsPastDay()
    {
        string bucket = SearchResultCollection.ClassifyDateRangeBucket(DateTime.Now.AddHours(-6));
        Assert.Equal("Modified/Created past day", bucket);
    }

    [Fact]
    public void ClassifyDateRangeBucket_WeekAgo_ReturnsPastWeek()
    {
        string bucket = SearchResultCollection.ClassifyDateRangeBucket(DateTime.Now.AddDays(-3));
        Assert.Equal("Modified/Created past week", bucket);
    }

    [Fact]
    public void ClassifyDateRangeBucket_VeryOld_ReturnsLongTimeAgo()
    {
        string bucket = SearchResultCollection.ClassifyDateRangeBucket(DateTime.Now.AddYears(-10));
        Assert.Equal("a long time ago", bucket);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ClassifyDateBucket (legacy)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ClassifyDateBucket_Today()
    {
        Assert.Equal("Today", SearchResultCollection.ClassifyDateBucket(DateTime.Now, GroupMode.None));
    }

    [Fact]
    public void ClassifyDateBucket_Yesterday()
    {
        Assert.Equal("Yesterday", SearchResultCollection.ClassifyDateBucket(DateTime.Now.AddDays(-1).Date, GroupMode.None));
    }

    [Fact]
    public void ClassifyDateBucket_Default_ReturnsUnknown()
    {
        Assert.Equal("Unknown date", SearchResultCollection.ClassifyDateBucket(default, GroupMode.None));
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetAllSelectedResults
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetAllSelectedResults_ReturnsOnlySelected()
    {
        var c = new SearchResultCollection();
        var r1 = MakeResult(@"C:\a.cs");
        var r2 = MakeResult(@"C:\b.cs");
        c.Add(r1);
        c.Add(r2);
        c.ApplySortAndFilter();
        r1.IsSelected = true;

        var selected = c.GetAllSelectedResults();
        Assert.Single(selected);
        Assert.Equal(r1, selected[0]);
    }

    [Fact]
    public void GetAllSelectedResults_EmptyWhenNoneSelected()
    {
        var c = new SearchResultCollection();
        c.Add(MakeResult(@"C:\a.cs"));
        c.ApplySortAndFilter();

        Assert.Empty(c.GetAllSelectedResults());
    }

    // ═══════════════════════════════════════════════════════════════
    //  RemoveGroup
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RemoveGroup_RemovesFromAllCollections()
    {
        var c = new SearchResultCollection();
        c.Add(MakeResult(@"C:\a.cs"));
        c.Add(MakeResult(@"C:\b.cs"));
        c.ApplySortAndFilter();

        var group = c.FindGroup(@"C:\a.cs")!;
        c.RemoveGroup(group);

        Assert.Single(c.AllGroups);
        Assert.Single(c.VisibleGroups);
        Assert.Null(c.FindGroup(@"C:\a.cs"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  AddRange batch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AddRange_EmptyList_ReturnsFalse()
    {
        var c = new SearchResultCollection();
        Assert.False(c.AddRange([]));
    }

    [Fact]
    public void AddRange_MultipleResults_GroupsCorrectly()
    {
        var c = new SearchResultCollection();
        var results = new[]
        {
            MakeResult(@"C:\a.cs"),
            MakeResult(@"C:\a.cs"),
            MakeResult(@"C:\b.cs"),
        };
        bool wasFirst = c.AddRange(results);
        Assert.True(wasFirst);
        Assert.Equal(2, c.AllGroups.Count);
        Assert.Equal(2, c.AllGroups[0].Count); // a.cs has 2 results
        Assert.Equal(1, c.AllGroups[1].Count); // b.cs has 1
    }

    // ═══════════════════════════════════════════════════════════════
    //  SetSortCriteria edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SetSortCriteria_IgnoresDuplicateModes()
    {
        var c = new SearchResultCollection();
        c.SetSortCriteria([
            new SortCriterion(1, 0),
            new SortCriterion(1, 1), // duplicate mode
            new SortCriterion(4, 0),
        ]);
        Assert.Equal(2, c.SortCriteria.Count);
    }

    [Fact]
    public void SetSortCriteria_IgnoresZeroMode()
    {
        var c = new SearchResultCollection();
        c.SetSortCriteria([new SortCriterion(0, 0)]);
        Assert.Empty(c.SortCriteria);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GroupByFileSize
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GroupByFileSize_AssignsBucketHeaders()
    {
        using var temp = new TemporaryDirectory();
        // Create files of varying sizes
        var small = temp.CreateFileWithSize("small.txt", 100);
        var medium = temp.CreateFileWithSize("medium.txt", 2_000_000);

        var c = new SearchResultCollection { GroupMode = GroupMode.FileSize };
        c.Add(MakeResult(small), g => g.LoadMetadata());
        c.Add(MakeResult(medium), g => g.LoadMetadata());
        c.ApplySortAndFilter();

        var headers = c.VisibleGroups
            .Where(g => g.GroupHeaderText is not null)
            .Select(g => g.GroupHeaderText!)
            .ToList();

        Assert.Equal(2, headers.Count);
        Assert.Contains("< 1MB", headers);
        Assert.Contains("1 - 5 MB", headers);
    }
}

/// <summary>Helper for tests that need real files on disk.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; }

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yagu-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string name, DateTime lastWrite, DateTime created)
    {
        var path = System.IO.Path.Combine(Path, name);
        File.WriteAllText(path, "content");
        File.SetLastWriteTime(path, lastWrite);
        File.SetCreationTime(path, created);
        return path;
    }

    public string CreateFileWithSize(string name, int sizeBytes)
    {
        var path = System.IO.Path.Combine(Path, name);
        using var fs = new FileStream(path, FileMode.Create);
        fs.SetLength(sizeBytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
