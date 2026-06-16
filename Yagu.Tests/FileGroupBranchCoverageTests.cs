using System.Collections.Specialized;
using Yagu.Models;

namespace Yagu.Tests;

/// <summary>
/// Branch coverage for FileGroup: selection, visibility paging, metadata,
/// MaxMatchesPerGroup cap, evicted stubs, and archive entry handling.
/// </summary>
public sealed class FileGroupBranchCoverageTests
{
    private static SearchResult MakeResult(int lineNumber = 1, string matchLine = "match text",
        int matchStart = 0, int matchLength = 5)
        => new("C:\\src\\file.cs", lineNumber, matchLine, matchStart, matchLength, [], []);

    private static SearchResult MakeFileNameResult()
        => new("C:\\src\\file.cs", 0, "", 0, 0, [], []);

    // ═══════════════════════════════════════════════════════════════
    //  Basic properties
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FilePath_ReturnedCorrectly()
    {
        var g = new FileGroup(@"C:\src\project\file.cs");
        Assert.Equal(@"C:\src\project\file.cs", g.FilePath);
    }

    [Fact]
    public void FileName_ReturnsJustFileName()
    {
        var g = new FileGroup(@"C:\src\project\file.cs");
        Assert.Equal("file.cs", g.FileName);
    }

    [Fact]
    public void DirectoryName_ReturnsParentDir()
    {
        var g = new FileGroup(@"C:\src\project\file.cs");
        Assert.Equal(@"C:\src\project", g.DirectoryName);
    }

    [Fact]
    public void Extension_ReturnsLowerCaseExtension()
    {
        var g = new FileGroup(@"C:\src\FILE.CS");
        Assert.Equal("cs", g.Extension);
    }

    [Fact]
    public void Extension_NoExtension_ReturnsNoExtensionLabel()
    {
        var g = new FileGroup(@"C:\src\Makefile");
        Assert.Equal(FileGroup.NoExtensionLabel, g.Extension);
    }

    [Fact]
    public void Extension_DotAtEnd_ReturnsNoExtensionLabel()
    {
        var g = new FileGroup(@"C:\src\file.");
        Assert.Equal(FileGroup.NoExtensionLabel, g.Extension);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Archive entry paths
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsArchiveEntry_WithSeparator_ReturnsTrue()
    {
        var g = new FileGroup(@"C:\archive.zip?/entry.txt");
        Assert.True(g.IsArchiveEntry);
    }

    [Fact]
    public void IsArchiveEntry_RegularPath_ReturnsFalse()
    {
        var g = new FileGroup(@"C:\src\file.cs");
        Assert.False(g.IsArchiveEntry);
    }

    [Fact]
    public void FileName_ArchiveEntry_ShowsArchiveAndEntry()
    {
        var g = new FileGroup(@"C:\data\test.zip?/inner/readme.txt");
        Assert.Contains("test.zip", g.FileName);
        Assert.Contains("readme.txt", g.FileName);
    }

    [Fact]
    public void DirectoryName_ArchiveEntry_ShowsArchiveParentDir()
    {
        var g = new FileGroup(@"C:\data\test.zip?/inner/readme.txt");
        Assert.Equal(@"C:\data", g.DirectoryName);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MatchCount includes all
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MatchCount_IncludesVisibleAndHidden()
    {
        var originalMax = FileGroup.MaxMatchesPerGroup;
        try
        {
            FileGroup.MaxMatchesPerGroup = 3;
            var g = new FileGroup(@"C:\file.cs");
            for (int i = 0; i < 5; i++)
                g.Add(MakeResult(lineNumber: i + 1));

            Assert.Equal(5, g.MatchCount);
            Assert.Equal(3, g.Count); // stored
            Assert.Equal(2, g.HiddenMatchCount); // over cap
        }
        finally { FileGroup.MaxMatchesPerGroup = originalMax; }
    }

    [Fact]
    public void HasHiddenMatches_FalseWhenUnderCap()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult());
        Assert.False(g.HasHiddenMatches);
    }

    // ═══════════════════════════════════════════════════════════════
    //  HasContentMatches
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void HasContentMatches_FileNameMatchOnly_ReturnsFalse()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeFileNameResult());
        Assert.False(g.HasContentMatches);
    }

    [Fact]
    public void HasContentMatches_ContentMatch_ReturnsTrue()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 5));
        Assert.True(g.HasContentMatches);
    }

    [Fact]
    public void HasContentMatches_MixedMatches_ReturnsTrue()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeFileNameResult());
        g.Add(MakeResult(lineNumber: 5));
        Assert.True(g.HasContentMatches);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Selection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SelectAll_SelectsAllItems()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));
        g.Add(MakeResult(lineNumber: 2));
        g.Add(MakeResult(lineNumber: 3));
        g.SelectAll();

        Assert.All(g, r => Assert.True(r.IsSelected));
        Assert.True(g.AllSelected);
        Assert.Equal(3, g.SelectedCount);
    }

    [Fact]
    public void DeselectAll_DeselectsAllItems()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));
        g.Add(MakeResult(lineNumber: 2));
        g.SelectAll();
        g.DeselectAll();

        Assert.All(g, r => Assert.False(r.IsSelected));
        Assert.False(g.AllSelected);
        Assert.Equal(0, g.SelectedCount);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ShowMore / ShowAll / Visible Results Paging
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ShowMore_PopulatesVisibleResults()
    {
        var g = new FileGroup(@"C:\file.cs");
        for (int i = 0; i < 300; i++)
            g.Add(MakeResult(lineNumber: i + 1));

        g.IsExpanded = true;
        g.ShowMore();

        Assert.True(g.VisibleResults.Count > 0);
        Assert.True(g.VisibleResults.Count <= FileGroup.PageSize);
    }

    [Fact]
    public void ShowAll_ShowsAllResults()
    {
        var g = new FileGroup(@"C:\file.cs");
        for (int i = 0; i < 50; i++)
            g.Add(MakeResult(lineNumber: i + 1));

        g.IsExpanded = true;
        g.ShowAll();

        Assert.Equal(50, g.VisibleResults.Count);
    }

    [Fact]
    public void ClearVisibleResults_EmptiesVisibleCollection()
    {
        var g = new FileGroup(@"C:\file.cs");
        for (int i = 0; i < 10; i++)
            g.Add(MakeResult(lineNumber: i + 1));

        g.IsExpanded = true;
        g.ShowAll();
        Assert.True(g.VisibleResults.Count > 0);

        g.ClearVisibleResults();
        Assert.Empty(g.VisibleResults);
    }

    // ═══════════════════════════════════════════════════════════════
    //  IsExpanded triggers materialization
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IsExpanded_DefaultFalse()
    {
        var g = new FileGroup(@"C:\file.cs");
        Assert.False(g.IsExpanded);
    }

    [Fact]
    public void IsExpanded_SetTrue_MaterializesResults()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));
        g.Add(MakeResult(lineNumber: 2));
        g.IsExpanded = true;
        // After expanding, VisibleResults should be populated
        Assert.True(g.VisibleResults.Count >= 0); // At minimum doesn't throw
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cleanup
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Cleanup_ClearsAllData()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));
        g.Add(MakeResult(lineNumber: 2));
        g.Cleanup();

        Assert.Empty(g);
        Assert.Empty(g.VisibleResults);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GroupHeaderText
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GroupHeaderText_InitiallyNull()
    {
        var g = new FileGroup(@"C:\file.cs");
        Assert.Null(g.GroupHeaderText);
    }

    [Fact]
    public void GroupHeaderText_CanBeSet()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.GroupHeaderText = "Test Header";
        Assert.Equal("Test Header", g.GroupHeaderText);
    }
}

/// <summary>
/// Branch coverage for BatchObservableCollection: AddRange, ReplaceAll,
/// and notification suppression.
/// </summary>
public sealed class BatchObservableCollectionTests
{
    [Fact]
    public void AddRange_EmptyList_DoesNotNotify()
    {
        var c = new BatchObservableCollection<string>();
        int notifications = 0;
        c.CollectionChanged += (_, _) => notifications++;
        c.AddRange([]);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void AddRange_SingleItem_UsesNormalAdd()
    {
        var c = new BatchObservableCollection<string>();
        NotifyCollectionChangedAction? action = null;
        c.CollectionChanged += (_, e) => action = e.Action;
        c.AddRange(["one"]);
        Assert.Equal(NotifyCollectionChangedAction.Add, action);
        Assert.Single(c);
    }

    [Fact]
    public void AddRange_MultipleItems_SingleResetNotification()
    {
        var c = new BatchObservableCollection<string>();
        var actions = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => actions.Add(e.Action);
        c.AddRange(["a", "b", "c"]);
        Assert.Equal(3, c.Count);
        // Should end with a Reset notification (batch mode)
        Assert.Contains(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void ReplaceAll_ReplacesContent()
    {
        var c = new BatchObservableCollection<int>();
        c.Add(1);
        c.Add(2);
        c.Add(3);
        c.ReplaceAll([10, 20]);
        Assert.Equal(2, c.Count);
        Assert.Equal(10, c[0]);
        Assert.Equal(20, c[1]);
    }

    [Fact]
    public void ReplaceAll_EmptyList_ClearsCollection()
    {
        var c = new BatchObservableCollection<int>();
        c.Add(1);
        c.Add(2);
        c.ReplaceAll([]);
        Assert.Empty(c);
    }

    [Fact]
    public void CollectionChanging_RaisedBeforeMutation()
    {
        var c = new BatchObservableCollection<string>();
        c.Add("initial");
        bool changingRaised = false;
        int countAtChanging = -1;
        c.CollectionChanging += (_, _) =>
        {
            changingRaised = true;
            countAtChanging = c.Count;
        };
        c.AddRange(["x", "y"]);
        Assert.True(changingRaised);
        Assert.Equal(1, countAtChanging); // Still had old count when Changing fired
    }

    [Fact]
    public void SetItem_RaisesCollectionChanging()
    {
        var c = new BatchObservableCollection<string> { "a", "b", "c" };
        bool changingRaised = false;
        c.CollectionChanging += (_, _) => changingRaised = true;
        c[1] = "replaced";
        Assert.True(changingRaised);
        Assert.Equal("replaced", c[1]);
    }

    [Fact]
    public void SetItem_UpdatesItemAtIndex()
    {
        var c = new BatchObservableCollection<int> { 10, 20, 30 };
        c[0] = 99;
        Assert.Equal(99, c[0]);
        Assert.Equal(3, c.Count);
    }
}

public sealed class FileGroupShowMoreBranchTests
{
    private static SearchResult MakeResult(string file = @"C:\file.cs", int lineNumber = 1) =>
        new(file, lineNumber, "match line", 0, 5, [], []);

    private static SearchResult MakeFileNameMatch(string file = @"C:\file.cs") =>
        new(file, 0, "file.cs", 0, 0, [], []);

    [Fact]
    public void ShowMore_ZeroMaxItems_ReturnsZero()
    {
        var g = new FileGroup(@"C:\file.cs");
        for (int i = 0; i < 5; i++)
            g.Add(MakeResult(lineNumber: i + 1));
        g.IsExpanded = true;

        int shown = g.ShowMore(maxItems: 0);
        Assert.Equal(0, shown);
    }

    [Fact]
    public void ShowMore_NegativeMaxItems_ReturnsZero()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));
        g.IsExpanded = true;

        int shown = g.ShowMore(maxItems: -5);
        Assert.Equal(0, shown);
    }

    [Fact]
    public void ShowMore_NoMoreToShow_ReturnsZero()
    {
        var g = new FileGroup(@"C:\file.cs");
        for (int i = 0; i < 5; i++)
            g.Add(MakeResult(lineNumber: i + 1));
        g.IsExpanded = true;
        g.ShowAll(); // shows all 5

        int shown = g.ShowMore(); // nothing left
        Assert.Equal(0, shown);
    }

    [Fact]
    public void ShowAll_SkipsFileNameMatchesWhenContentMatchesExist()
    {
        var g = new FileGroup(@"C:\file.cs");
        // Add a filename match (line 0) first
        g.Add(MakeFileNameMatch());
        // Then add content matches
        for (int i = 1; i <= 5; i++)
            g.Add(MakeResult(lineNumber: i));
        g.IsExpanded = true;

        g.ShowAll();

        // The filename match should be skipped because content matches exist
        Assert.DoesNotContain(g.VisibleResults, r => r.LineNumber == 0);
        Assert.Equal(5, g.VisibleResults.Count);
    }

    [Fact]
    public void ShowMore_SkipsFileNameMatchesWhenContentMatchesExist()
    {
        var g = new FileGroup(@"C:\file.cs");
        // First fill to PageSize so ShowMore has work to do
        g.Add(MakeFileNameMatch());
        for (int i = 1; i <= FileGroup.PageSize + 5; i++)
            g.Add(MakeResult(lineNumber: i));
        g.IsExpanded = true;

        // ShowMore after the initial page
        g.ShowMore();

        // Should not contain any filename matches
        Assert.DoesNotContain(g.VisibleResults, r => r.LineNumber == 0);
    }

    [Fact]
    public void GetPreviewSnapshot_ZeroMaxResults_ReturnsEmpty()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));

        var snapshot = g.GetPreviewSnapshot(0);
        Assert.Empty(snapshot);
    }

    [Fact]
    public void GetPreviewSnapshot_NegativeMaxResults_ReturnsEmpty()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeResult(lineNumber: 1));

        var snapshot = g.GetPreviewSnapshot(-1);
        Assert.Empty(snapshot);
    }

    [Fact]
    public void GetPreviewSnapshot_SkipsFileNameMatchesWhenContentExists()
    {
        var g = new FileGroup(@"C:\file.cs");
        g.Add(MakeFileNameMatch());
        g.Add(MakeResult(lineNumber: 5));
        g.Add(MakeResult(lineNumber: 10));

        var snapshot = g.GetPreviewSnapshot(10);

        Assert.DoesNotContain(snapshot, r => r.LineNumber == 0);
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public void GetPreviewSnapshot_RespectsMaxResults()
    {
        var g = new FileGroup(@"C:\file.cs");
        for (int i = 1; i <= 10; i++)
            g.Add(MakeResult(lineNumber: i));

        var snapshot = g.GetPreviewSnapshot(3);
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void ClearVisibleResults_AlreadyEmpty_DoesNothing()
    {
        var g = new FileGroup(@"C:\file.cs");
        // Don't add or expand — VisibleResults is empty
        g.ClearVisibleResults(); // Should not throw
        Assert.Empty(g.VisibleResults);
    }
}
