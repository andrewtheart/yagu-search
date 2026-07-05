using Yagu.Models;

namespace Yagu.Tests;

/// <summary>
/// Tests for SearchResultCollection multi-criteria sort (ThenByCriterion)
/// and ClassifyDateRangeBucket older date buckets.
/// </summary>
public class SearchResultCollectionSortTests
{
    private static SearchResult MakeResult(string path, string line, int lineNum = 1) =>
        new(path, lineNum, line, 0, line.Length, [], []);

    // ─── Multi-criteria sort: exercises ThenByCriterion(IOrderedEnumerable<FileGroup>) ───

    [Fact]
    public void SetSortCriteria_TwoFields_AppliesBothSortLevels()
    {
        var coll = new SearchResultCollection();
        // Two files with same match count but different names
        coll.Add(MakeResult(@"C:\zebra.txt", "m"));
        coll.Add(MakeResult(@"C:\alpha.txt", "m"));
        coll.Add(MakeResult(@"C:\alpha.txt", "m2")); // alpha has 2 matches

        // Primary: match count desc (mode 1, dir 0), Secondary: name asc (mode 4, dir 1)
        coll.SetSortCriteria([
            new SortCriterion(1, 0), // match count descending
            new SortCriterion(4, 1), // then name ascending
        ]);
        coll.ApplySortAndFilter();

        Assert.Equal(2, coll.VisibleGroups.Count);
        Assert.Equal(@"C:\alpha.txt", coll.VisibleGroups[0].FilePath); // 2 matches
        Assert.Equal(@"C:\zebra.txt", coll.VisibleGroups[1].FilePath); // 1 match
    }

    [Fact]
    public void SetSortCriteria_SortByDateThenSize_AppliesThenBy()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\a.txt", "m"));
        coll.Add(MakeResult(@"C:\b.txt", "m"));

        // Primary: date asc (mode 2, dir 1), Secondary: size desc (mode 3, dir 0)
        coll.SetSortCriteria([
            new SortCriterion(2, 1),
            new SortCriterion(3, 0),
        ]);
        coll.ApplySortAndFilter();

        Assert.Equal(2, coll.VisibleGroups.Count);
    }

    [Fact]
    public void SetSortCriteria_SortBySizeThenName_Ascending()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\z.txt", "m"));
        coll.Add(MakeResult(@"C:\a.txt", "m"));

        // Primary: size ascending, Secondary: name ascending
        coll.SetSortCriteria([
            new SortCriterion(3, 1),
            new SortCriterion(4, 1),
        ]);
        coll.ApplySortAndFilter();

        // Same size (0), so secondary sort by name ascending
        Assert.Equal(@"C:\a.txt", coll.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\z.txt", coll.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void SetSortCriteria_SortByNameThenMatchCount_Descending()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\a.txt", "m1"));
        coll.Add(MakeResult(@"C:\a.txt", "m2"));
        coll.Add(MakeResult(@"C:\b.txt", "m1"));

        // Primary: name desc (mode 4, dir 0), Secondary: match count desc (mode 1, dir 0)
        coll.SetSortCriteria([
            new SortCriterion(4, 0),
            new SortCriterion(1, 0),
        ]);
        coll.ApplySortAndFilter();

        Assert.Equal(@"C:\b.txt", coll.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\a.txt", coll.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void SetSortCriteria_DefaultMode_FallsThrough()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\a.txt", "m"));
        coll.Add(MakeResult(@"C:\b.txt", "m"));
        coll.Add(MakeResult(@"C:\b.txt", "m2"));

        // Primary: date, Secondary: default (mode 99 → falls to _ case = match count)
        coll.SetSortCriteria([
            new SortCriterion(2, 1),
            new SortCriterion(99, 0), // unrecognized mode → default branch (match count desc)
        ]);
        coll.ApplySortAndFilter();

        Assert.Equal(2, coll.VisibleGroups.Count);
    }

    // ─── Multi-criteria sort with DateRange grouping (tuple ThenByCriterion overload) ───

    [Fact]
    public void SetSortCriteria_WithDateRangeGroup_TupleThenBy_ByDate()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\a.txt", "m"));
        coll.Add(MakeResult(@"C:\b.txt", "m"));
        coll.GroupMode = GroupMode.DateRangeModifiedCreated;

        coll.SetSortCriteria([
            new SortCriterion(2, 1), // date ascending
            new SortCriterion(3, 0), // then size descending
        ]);
        coll.ApplySortAndFilter();

        Assert.NotEmpty(coll.VisibleGroups);
    }

    [Fact]
    public void SetSortCriteria_WithDateRangeGroup_TupleThenBy_ByName()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\z.txt", "m"));
        coll.Add(MakeResult(@"C:\a.txt", "m"));
        coll.GroupMode = GroupMode.DateRangeModified;

        coll.SetSortCriteria([
            new SortCriterion(4, 1), // name ascending
            new SortCriterion(1, 0), // then match count descending
        ]);
        coll.ApplySortAndFilter();

        Assert.NotEmpty(coll.VisibleGroups);
    }

    [Fact]
    public void SetSortCriteria_WithDateRangeGroup_TupleThenBy_Default()
    {
        var coll = new SearchResultCollection();
        coll.Add(MakeResult(@"C:\a.txt", "m"));
        coll.GroupMode = GroupMode.DateRangeCreated;

        coll.SetSortCriteria([
            new SortCriterion(3, 0), // size descending
            new SortCriterion(99, 1), // unrecognized → default (match count asc)
        ]);
        coll.ApplySortAndFilter();

        Assert.NotEmpty(coll.VisibleGroups);
    }

    // ─── ClassifyDateRangeBucket older date branches ───

    [Theory]
    [InlineData(-400, "past two years")]    // > 1 year, < 2 years
    [InlineData(-800, "past three years")]  // > 2 years, < 3 years
    [InlineData(-1500, "past 5 years")]     // > 3 years, < 5 years
    [InlineData(-2500, "a long time ago")]  // > 5 years
    public void ClassifyDateRangeBucket_OlderDates_CorrectBucket(int daysAgo, string expectedSuffix)
    {
        var date = DateTime.Now.AddDays(daysAgo);
        var bucket = SearchResultCollection.ClassifyDateRangeBucket(date);
        Assert.EndsWith(expectedSuffix, bucket);
    }

    [Fact]
    public void ClassifyDateRangeBucket_DefaultDate_ReturnsUnknown()
    {
        var bucket = SearchResultCollection.ClassifyDateRangeBucket(default);
        Assert.Equal("Unknown date", bucket);
    }

    [Fact]
    public void ClassifyDateRangeBucket_PastNineMonths()
    {
        var date = DateTime.Now.AddMonths(-7); // between 6 and 9 months
        var bucket = SearchResultCollection.ClassifyDateRangeBucket(date);
        Assert.EndsWith("past 9 months", bucket);
    }

    [Fact]
    public void ClassifyDateRangeBucket_PastYear()
    {
        var date = DateTime.Now.AddMonths(-10); // between 9 months and 1 year
        var bucket = SearchResultCollection.ClassifyDateRangeBucket(date);
        Assert.EndsWith("past year", bucket);
    }

    // ─── SetSortCriteria deduplication ───

    [Fact]
    public void SetSortCriteria_DuplicateModes_KeepsFirstOnly()
    {
        var coll = new SearchResultCollection();
        coll.SetSortCriteria([
            new SortCriterion(2, 1),
            new SortCriterion(2, 0), // duplicate mode 2 — should be skipped
            new SortCriterion(3, 1),
        ]);
        Assert.Equal(2, coll.SortCriteria.Count);
        Assert.Equal(2, coll.SortCriteria[0].SortModeIndex);
        Assert.Equal(3, coll.SortCriteria[1].SortModeIndex);
    }

    [Fact]
    public void SetSortCriteria_ZeroMode_Skipped()
    {
        var coll = new SearchResultCollection();
        coll.SetSortCriteria([
            new SortCriterion(0, 1), // mode 0 → skipped
            new SortCriterion(3, 1),
        ]);
        Assert.Single(coll.SortCriteria);
        Assert.Equal(3, coll.SortCriteria[0].SortModeIndex);
    }

    [Fact]
    public void ClearSortCriteria_EmptiesTheList()
    {
        var coll = new SearchResultCollection();
        coll.SetSortCriteria([new SortCriterion(2, 1)]);
        Assert.Single(coll.SortCriteria);
        coll.ClearSortCriteria();
        Assert.Empty(coll.SortCriteria);
    }
}
