using Yagu.Models;

namespace Yagu.Tests;

public sealed class SearchResultCollectionExtendedCoverageTests
{
    private static SearchResult MakeResult(string filePath, int lineNumber, string matchLine, int matchStart = 0, int matchLength = 5)
        => new(filePath, lineNumber, matchLine, matchStart, matchLength, [], []);

    [Fact]
    public void Add_FirstResult_ReturnsTrue()
    {
        var collection = new SearchResultCollection();
        var result = MakeResult(@"C:\src\file.cs", 10, "hello match");
        bool wasFirst = collection.Add(result);
        Assert.True(wasFirst);
    }

    [Fact]
    public void Add_SubsequentResult_SameFile_ReturnsFalse()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "line 1"));
        bool wasFirst = collection.Add(MakeResult(@"C:\src\file.cs", 2, "line 2"));
        Assert.False(wasFirst);
    }

    [Fact]
    public void Add_DifferentFiles_CreatesMultipleGroups()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\a.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\b.cs", 2, "match"));
        Assert.Equal(2, collection.AllGroups.Count);
    }

    [Fact]
    public void Add_GroupsResultsBySameFile()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match1"));
        collection.Add(MakeResult(@"C:\src\file.cs", 5, "match2"));
        Assert.Single(collection.AllGroups);
        Assert.Equal(2, collection.AllGroups[0].Count);
    }

    [Fact]
    public void FindGroup_ExistingPath_ReturnsGroup()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        var group = collection.FindGroup(@"C:\src\file.cs");
        Assert.NotNull(group);
        Assert.Equal(@"C:\src\file.cs", group!.FilePath);
    }

    [Fact]
    public void FindGroup_NonExistentPath_ReturnsNull()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        Assert.Null(collection.FindGroup(@"C:\src\other.cs"));
    }

    [Fact]
    public void FindGroup_CaseInsensitive()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\SRC\File.cs", 1, "match"));
        var group = collection.FindGroup(@"c:\src\file.cs");
        Assert.NotNull(group);
    }

    [Fact]
    public void Clear_RemovesAllGroups()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\a.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\b.cs", 2, "match"));
        collection.Clear();
        Assert.Empty(collection.AllGroups);
        Assert.Empty(collection.VisibleGroups);
    }

    [Fact]
    public void RemoveGroup_RemovesSpecificGroup()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\a.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\b.cs", 2, "match"));
        var group = collection.FindGroup(@"C:\src\a.cs")!;
        collection.RemoveGroup(group);
        Assert.Single(collection.AllGroups);
        Assert.Null(collection.FindGroup(@"C:\src\a.cs"));
    }

    [Fact]
    public void SetExtensionFilters_FiltersGroups()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\file.txt", 2, "match"));
        collection.SetExtensionFilters(["cs"]);
        collection.ApplySortAndFilter();
        // Extension filter should show only .cs files
        Assert.Contains(collection.VisibleGroups, g => g.FilePath == @"C:\src\file.cs");
    }

    [Fact]
    public void SetExtensionFilters_EmptyClears()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        collection.SetExtensionFilters(["cs"]);
        collection.ApplySortAndFilter();
        Assert.Single(collection.VisibleGroups);

        collection.SetExtensionFilters([]);
        collection.ApplySortAndFilter();
        Assert.Single(collection.VisibleGroups); // all visible again
    }

    [Fact]
    public void GetExtensionFilterOptions_ReturnsDistinctExtensions()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\a.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\b.cs", 2, "match"));
        collection.Add(MakeResult(@"C:\src\c.txt", 3, "match"));
        var options = collection.GetExtensionFilterOptions();
        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.Extension == "cs");
        Assert.Contains(options, o => o.Extension == "txt");
    }

    [Fact]
    public void FileNameFilter_FiltersGroups()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\Program.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\Helper.cs", 2, "match"));
        collection.FileNameFilter = "Program";
        collection.ApplySortAndFilter();
        Assert.Single(collection.VisibleGroups);
    }

    [Fact]
    public void IncludeGlobs_FiltersGroups()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\file.txt", 2, "match"));
        collection.IncludeGlobs = "cs";
        collection.ApplySortAndFilter();
        Assert.Single(collection.VisibleGroups);
    }

    [Fact]
    public void ExcludeGlobs_FiltersGroups()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\node_modules\file.cs", 2, "match"));
        collection.ExcludeGlobs = "node_modules";
        collection.ApplySortAndFilter();
        Assert.Contains(collection.VisibleGroups, g => g.FilePath == @"C:\src\file.cs");
        Assert.DoesNotContain(collection.VisibleGroups, g => g.FilePath == @"C:\src\node_modules\file.cs");
    }

    [Fact]
    public void SetSortCriteria_SkipsDuplicateAndInvalid()
    {
        var collection = new SearchResultCollection();
        collection.SetSortCriteria([
            new SortCriterion(1, 0),
            new SortCriterion(1, 1), // duplicate mode, should be skipped
            new SortCriterion(0, 0), // mode 0 = invalid, should be skipped
            new SortCriterion(2, 1),
        ]);
        Assert.Equal(2, collection.SortCriteria.Count);
        Assert.Equal(1, collection.SortCriteria[0].SortModeIndex);
        Assert.Equal(2, collection.SortCriteria[1].SortModeIndex);
    }

    [Fact]
    public void ClearSortCriteria_Clears()
    {
        var collection = new SearchResultCollection();
        collection.SetSortCriteria([new SortCriterion(1, 0)]);
        collection.ClearSortCriteria();
        Assert.Empty(collection.SortCriteria);
    }

    [Fact]
    public void ApplySortAndFilter_SortByFileName()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\Zebra.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\Apple.cs", 2, "match"));
        // SortModeIndex=4 is FileName, SortDirectionIndex=1 is ascending
        collection.SetSortCriteria([new SortCriterion(4, 1)]);
        collection.ApplySortAndFilter();
        Assert.Equal(2, collection.VisibleGroups.Count);
        Assert.Equal(@"C:\src\Apple.cs", collection.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\src\Zebra.cs", collection.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void AddRange_MultipleResults_BatchAdded()
    {
        var collection = new SearchResultCollection();
        var results = new List<SearchResult>
        {
            MakeResult(@"C:\src\a.cs", 1, "match1"),
            MakeResult(@"C:\src\a.cs", 2, "match2"),
            MakeResult(@"C:\src\b.cs", 3, "match3"),
        };
        bool wasFirst = collection.AddRange(results);
        Assert.True(wasFirst);
        Assert.Equal(2, collection.AllGroups.Count);
        Assert.Equal(2, collection.AllGroups[0].Count);
        Assert.Single(collection.AllGroups[1]);
    }

    [Fact]
    public void AddRange_EmptyList_ReturnsFalse()
    {
        var collection = new SearchResultCollection();
        bool wasFirst = collection.AddRange([]);
        Assert.False(wasFirst);
    }

    [Fact]
    public void AddSourceBackedRange_GroupsAndCompactsMatches()
    {
        var collection = new SearchResultCollection();
        var matches = new List<SourceBackedMatch>
        {
            new(@"C:\src\a.cs", 1, 2, 4, 2),
            new(@"C:\src\a.cs", 2, 3, 5, 3),
            new(@"C:\src\b.cs", 3, 1, 6, 1),
        };

        bool wasFirst = collection.AddSourceBackedRange(matches);

        Assert.True(wasFirst);
        Assert.Equal(2, collection.AllGroups.Count);
        Assert.Equal(2, collection.VisibleGroups.Count);

        var firstGroup = collection.FindGroup(@"C:\src\a.cs")!;
        Assert.Equal(0, firstGroup.Count);
        Assert.Equal(2, firstGroup.MatchCount);
        Assert.True(firstGroup.HasMore);

        firstGroup.IsExpanded = true;

        Assert.Equal(2, firstGroup.Count);
        Assert.All(firstGroup, result => Assert.True(result.IsEvicted));
        Assert.Equal([1, 2], firstGroup.Select(result => result.LineNumber).ToArray());
    }

    [Fact]
    public void GroupMode_Folder_GroupsByDirectory()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\dir1\a.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\dir2\b.cs", 2, "match"));
        collection.GroupMode = GroupMode.Folder;
        collection.ApplySortAndFilter();
        // Both should still be visible, just grouped differently
        Assert.Equal(2, collection.VisibleGroups.Count);
    }

    [Fact]
    public void SortDirection_Descending_ReversesOrder()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\Apple.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\Zebra.cs", 2, "match"));
        // SortModeIndex=4 is FileName, SortDirectionIndex=0 is descending
        collection.SetSortCriteria([new SortCriterion(4, 0)]);
        collection.ApplySortAndFilter();
        Assert.Equal(2, collection.VisibleGroups.Count);
        Assert.Equal(@"C:\src\Zebra.cs", collection.VisibleGroups[0].FilePath);
        Assert.Equal(@"C:\src\Apple.cs", collection.VisibleGroups[1].FilePath);
    }

    [Fact]
    public void Add_InvokesInitializeNewGroupCallback()
    {
        var collection = new SearchResultCollection();
        bool callbackInvoked = false;
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"), group =>
        {
            callbackInvoked = true;
        });
        Assert.True(callbackInvoked);
    }

    [Fact]
    public void IncludeGlobs_RegexMode_Works()
    {
        var collection = new SearchResultCollection();
        collection.Add(MakeResult(@"C:\src\file.cs", 1, "match"));
        collection.Add(MakeResult(@"C:\src\file.txt", 2, "match"));
        collection.IncludeGlobs = @"\.cs$";
        collection.IncludeFilterMode = FilterPatternMode.Regex;
        collection.ApplySortAndFilter();
        Assert.Single(collection.VisibleGroups);
        Assert.Equal(@"C:\src\file.cs", collection.VisibleGroups[0].FilePath);
    }
}
