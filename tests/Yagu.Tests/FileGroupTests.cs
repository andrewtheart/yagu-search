using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public class FileGroupTests
{
    private static SearchResult MakeResult(string filePath, int line, string matchLine = "needle")
        => new(FilePath: filePath, LineNumber: line, MatchLine: matchLine,
               MatchStartColumn: 0, MatchLength: matchLine.Length,
               ContextBefore: Array.Empty<string>(), ContextAfter: Array.Empty<string>());

    private static SearchResult MakeResultWithContext(string filePath, int line, int contextRadius)
    {
        var before = new string[contextRadius];
        var after = new string[contextRadius];
        for (int i = 0; i < contextRadius; i++)
        {
            before[i] = $"before-{line}-{i}";
            after[i] = $"after-{line}-{i}";
        }
        return new(FilePath: filePath, LineNumber: line, MatchLine: "needle",
                   MatchStartColumn: 0, MatchLength: 6,
                   ContextBefore: before, ContextAfter: after);
    }

    // Flattens the file-list rendering order: each visible result's before-context line numbers,
    // then the match line number, then its after-context line numbers.
    private static List<int> FlattenDisplayedLineNumbers(FileGroup group)
    {
        var lines = new List<int>();
        foreach (var r in group.VisibleResults)
        {
            foreach (var c in r.NumberedBefore) lines.Add(c.LineNum);
            lines.Add(r.LineNumber);
            foreach (var c in r.NumberedAfter) lines.Add(c.LineNum);
        }
        return lines;
    }

    [Fact]
    public void OverlappingContext_StreamedExpanded_DoesNotRepeatLineNumbers()
    {
        var group = new FileGroup(@"D:\ocr.png");
        group.IsExpanded = true;
        // Matches on adjacent lines with overlapping +/-2 context windows (the OCR case).
        foreach (int line in new[] { 3, 5, 7 })
            group.Add(MakeResultWithContext(@"D:\ocr.png", line, contextRadius: 2));

        var displayed = FlattenDisplayedLineNumbers(group);

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, displayed);
        // Strictly increasing => no repeats.
        for (int i = 1; i < displayed.Count; i++)
            Assert.True(displayed[i] > displayed[i - 1], $"Line {displayed[i]} not greater than {displayed[i - 1]}");
    }

    [Fact]
    public void OverlappingContext_ShowMore_DoesNotRepeatLineNumbers()
    {
        var group = new FileGroup(@"D:\ocr.png");
        foreach (int line in new[] { 3, 5, 7, 9, 11 })
            group.Add(MakeResultWithContext(@"D:\ocr.png", line, contextRadius: 2));

        group.IsExpanded = true;
        group.ShowAll();

        var displayed = FlattenDisplayedLineNumbers(group);

        Assert.NotEmpty(displayed);
        for (int i = 1; i < displayed.Count; i++)
            Assert.True(displayed[i] > displayed[i - 1], $"Line {displayed[i]} not greater than {displayed[i - 1]}");
    }

    [Fact]
    public void FilenameOnlyMatch_Expanded_ShowsNoZeroLineRow()
    {
        // A group whose only match is the file NAME (LineNumber == 0) is conveyed by the header
        // "file name" pill, so the "0" line row is never rendered — even when the group is expanded.
        var group = new FileGroup(@"D:\name-hit.txt");
        group.IsExpanded = true;
        group.Add(MakeResult(@"D:\name-hit.txt", 0));

        Assert.False(group.HasContentMatches);
        Assert.True(group.HasFileNameMatch);
        Assert.Empty(group.VisibleResults);
    }

    [Fact]
    public void OverlappingContext_ReExpand_RecomputesTrimWithoutRepeats()
    {
        var group = new FileGroup(@"D:\ocr.png");
        group.IsExpanded = true;
        foreach (int line in new[] { 3, 5, 7 })
            group.Add(MakeResultWithContext(@"D:\ocr.png", line, contextRadius: 2));

        group.ClearVisibleResults();
        group.ShowAll();

        var displayed = FlattenDisplayedLineNumbers(group);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, displayed);
    }

    [Fact]
    public void FileName_ExtractsFileName()
    {
        var group = new FileGroup(@"D:\repo\src\file.txt");
        Assert.Equal("file.txt", group.FileName);
    }

    [Fact]
    public void DirectoryName_ExtractsDirectory()
    {
        var group = new FileGroup(@"D:\repo\src\file.txt");
        Assert.Equal(@"D:\repo\src", group.DirectoryName);
    }

    [Fact]
    public void MultilineResult_ContextTrim_AccountsForSpanEndLine()
    {
        var group = new FileGroup(@"D:\ml.txt");
        group.IsExpanded = true;

        // A cross-line match on lines 2..4 (after-context numbered from the END line 4),
        // followed by a later single-line match on line 8.
        var multi = new SearchResult(
            FilePath: @"D:\ml.txt", LineNumber: 2, MatchLine: "START",
            MatchStartColumn: 0, MatchLength: 5,
            ContextBefore: Array.Empty<string>(), ContextAfter: new[] { "l5", "l6" })
        { MatchEndLineNumber = 4, MatchEndColumn = 3 };
        group.Add(multi);
        group.Add(MakeResult(@"D:\ml.txt", 8));

        var first = group.VisibleResults[0];
        Assert.Equal(2, first.LineNumber);
        Assert.Equal(4, first.MatchEndLineNumber);
        // Trailing context is numbered from the END line (4): lines 5 and 6 — not from line 2,
        // and not repeating any line owned by the next (line-8) match.
        Assert.Equal(new[] { 5, 6 }, first.NumberedAfter.Select(c => c.LineNum).ToArray());
    }

    [Fact]
    public void MultilineResult_SurvivesStubMaterialization_WithSpanIntact()
    {
        // Phase 1b: a pre-evicted cross-line match added to a collapsed group is compacted into a
        // stub (its SearchResult is dropped). Materializing the stub must restore the span from the
        // per-group sidecar, while an interleaved single-line stub stays single-line.
        var group = new FileGroup(@"D:\ml.txt"); // collapsed by default

        var multi = SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 2, matchStartColumn: 0, matchLength: 5, diskOffset: 100L);
        multi.MatchEndLineNumber = 4;
        multi.MatchEndColumn = 3;
        Assert.True(multi.IsEvicted);
        group.Add(multi); // collapsed + evicted => stub path records span in the sidecar

        var single = SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 9, matchStartColumn: 0, matchLength: 1, diskOffset: 200L);
        group.Add(single);

        group.MaterializeEvictedStubs();

        var results = group.ToList();
        var mlResult = results.Single(r => r.LineNumber == 2);
        Assert.True(mlResult.IsMultilineMatch);
        Assert.Equal(4, mlResult.MatchEndLineNumber);
        Assert.Equal(3, mlResult.MatchEndColumn);

        var slResult = results.Single(r => r.LineNumber == 9);
        Assert.False(slResult.IsMultilineMatch);
        Assert.Null(slResult.MatchEndLineNumber);
    }

    [Fact]
    public void MultilineResult_PreviewSnapshotFromStub_CarriesSpan()
    {
        // Phase 1b: the non-destructive preview snapshot rebuilt from evicted stubs must also
        // re-apply the cross-line span so a collapsed multiline match previews correctly.
        var group = new FileGroup(@"D:\ml.txt"); // collapsed by default

        var single = SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 1, matchStartColumn: 0, matchLength: 1, diskOffset: 50L);
        group.Add(single);

        var multi = SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 6, matchStartColumn: 0, matchLength: 4, diskOffset: 150L);
        multi.MatchEndLineNumber = 8;
        multi.MatchEndColumn = 2;
        group.Add(multi);

        var snapshot = group.GetPreviewSnapshot(maxResults: 10);

        var mlResult = snapshot.Single(r => r.LineNumber == 6);
        Assert.True(mlResult.IsMultilineMatch);
        Assert.Equal(8, mlResult.MatchEndLineNumber);
        Assert.Equal(2, mlResult.MatchEndColumn);
        Assert.False(snapshot.Single(r => r.LineNumber == 1).IsMultilineMatch);
    }

    [Fact]
    public void MultilineResult_MultipleStubs_AllSpansRestored_IncludingNullEndColumn()
    {
        // Exercises the sidecar's reuse arm (a second multiline stub in the same group hits
        // `_evictedStubSpans ??= []` non-null) and a multiline result whose MatchEndColumn is
        // null (the `?? 0` null arm) so the stored end column defaults to 0.
        var group = new FileGroup(@"D:\ml.txt"); // collapsed

        var a = SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 2, matchStartColumn: 0, matchLength: 5, diskOffset: 10L);
        a.MatchEndLineNumber = 4;
        a.MatchEndColumn = 3;
        group.Add(a); // first multiline stub allocates the sidecar

        group.Add(SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 6, matchStartColumn: 0, matchLength: 1, diskOffset: 20L));

        var b = SearchResult.CreatePreEvicted(@"D:\ml.txt", lineNumber: 8, matchStartColumn: 0, matchLength: 2, diskOffset: 30L);
        b.MatchEndLineNumber = 9; // MatchEndColumn deliberately left null => stored as 0
        group.Add(b); // second multiline stub reuses the sidecar

        group.MaterializeEvictedStubs();
        var results = group.ToList();

        var ra = results.Single(r => r.LineNumber == 2);
        Assert.Equal(4, ra.MatchEndLineNumber);
        Assert.Equal(3, ra.MatchEndColumn);

        var rb = results.Single(r => r.LineNumber == 8);
        Assert.True(rb.IsMultilineMatch);
        Assert.Equal(9, rb.MatchEndLineNumber);
        Assert.Equal(0, rb.MatchEndColumn); // null end column defaulted to 0 at stub time

        Assert.Null(results.Single(r => r.LineNumber == 6).MatchEndLineNumber);
    }

    [Fact]
    public void VisibleResults_PagesAtPageSize()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        for (int i = 0; i < FileGroup.PageSize + 50; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        Assert.Equal(FileGroup.PageSize, group.VisibleResults.Count);
        Assert.True(group.HasMore);
        Assert.Equal(50, group.RemainingCount);
    }

    [Fact]
    public void ShowMore_AddsNextPage()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        for (int i = 0; i < FileGroup.PageSize + 50; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        group.ShowMore();
        Assert.Equal(FileGroup.PageSize + 50, group.VisibleResults.Count);
        Assert.False(group.HasMore);
        Assert.Equal(0, group.RemainingCount);
    }

    [Fact]
    public void ShowAll_AddsAllRemaining()
    {
        var group = new FileGroup(@"D:\file.txt");
        for (int i = 0; i < FileGroup.PageSize * 3; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        group.ShowAll();
        Assert.Equal(FileGroup.PageSize * 3, group.VisibleResults.Count);
        Assert.False(group.HasMore);
    }

    [Fact]
    public void ShowMoreText_IncludesRemainingCount()
    {
        var group = new FileGroup(@"D:\file.txt");
        for (int i = 0; i < FileGroup.PageSize + 10; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        Assert.Contains("10", group.ShowMoreText);
        Assert.Contains("remaining", group.ShowMoreText);
    }

    [Fact]
    public void Clear_ResetsVisibleResults()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        group.Add(MakeResult(@"D:\file.txt", 1));
        Assert.Single(group.VisibleResults);

        group.Clear();
        Assert.Empty(group.VisibleResults);
        Assert.False(group.HasMore);
    }

    [Fact]
    public void SelectAll_SetsAllSelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));

        group.SelectAll();
        Assert.True(group.AllSelected);
        Assert.Equal(2, group.SelectedCount);
        Assert.All(group, r => Assert.True(r.IsSelected));
    }

    [Fact]
    public void AddAfterSelectAll_SelectsNewResultAndKeepsFileChecked()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        group.SelectAll();

        var incoming = MakeResult(@"D:\file.txt", 3);
        group.Add(incoming);

        Assert.True(incoming.IsSelected);
        Assert.True(group.AllSelected);
        Assert.Equal(3, group.SelectedCount);
        Assert.Equal("3/3 selected", group.SelectedCountText);
    }

    [Fact]
    public void AddAfterDeselectAll_DoesNotSelectNewResult()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.SelectAll();
        group.DeselectAll();

        var incoming = MakeResult(@"D:\file.txt", 2);
        group.Add(incoming);

        Assert.False(incoming.IsSelected);
        Assert.False(group.AllSelected);
        Assert.Equal(0, group.SelectedCount);
    }

    [Fact]
    public void PreEvictedResultAddedAfterSelectAll_IsSelectedWhenMaterialized()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.SelectAll();

        group.Add(SearchResult.CreatePreEvicted(@"D:\file.txt", 2, 0, 5, diskOffset: 100));

        Assert.Equal(1, group.Count);
        Assert.True(group.AllSelected);
        Assert.Equal(2, group.SelectedCount);
        var snapshot = group.GetPreviewSnapshot(2);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(1, group.Count);
        Assert.True(snapshot[1].IsEvicted);
        Assert.True(snapshot[1].IsSelected);

        group.IsExpanded = true;

        Assert.Equal(2, group.Count);
        Assert.True(group[1].IsSelected);
        Assert.True(group.AllSelected);
        Assert.Equal(2, group.SelectedCount);
    }

    [Fact]
    public void SourceBackedMatchAddedToCollapsedGroup_IsCompactedAndMaterializedOnExpand()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.SelectAll();

        group.AddSourceBackedMatch(lineNumber: 2, matchStartColumn: 3, matchLength: 5, sourceMatchStartColumn: 3);

        Assert.Equal(1, group.Count);
        Assert.True(group.HasMore);
        Assert.Equal(2, group.RemainingCount);
        Assert.Equal(2, group.SelectedCount);

        var snapshot = group.GetPreviewSnapshot(2);
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot[1].IsEvicted);
        Assert.True(snapshot[1].IsSelected);
        Assert.Equal(2, snapshot[1].LineNumber);

        group.IsExpanded = true;

        Assert.Equal(2, group.Count);
        Assert.True(group[1].IsEvicted);
        Assert.True(group[1].IsSelected);
        Assert.Equal(3, group[1].MatchStartColumn);
        Assert.Equal(5, group[1].MatchLength);
    }

    [Fact]
    public void SourceBackedMatchesAddedToCollapsedGroup_AreCompactedInBulk()
    {
        var group = new FileGroup(@"D:\file.txt");
        var matches = new List<SourceBackedMatch>
        {
            new(@"D:\file.txt", 1, 2, 4, 2),
            new(@"D:\file.txt", 2, 3, 5, 3),
            new(@"D:\file.txt", 3, 4, 6, 4),
        };

        group.AddSourceBackedMatches(matches, 0, matches.Count);

        Assert.Equal(0, group.Count);
        Assert.Equal(3, group.MatchCount);
        Assert.True(group.HasMore);

        group.IsExpanded = true;

        Assert.Equal(3, group.Count);
        Assert.All(group, result => Assert.True(result.IsEvicted));
        Assert.Equal([1, 2, 3], group.Select(result => result.LineNumber).ToArray());
    }

    [Fact]
    public void PreviewSnapshot_DecodesBoundedPreEvictedRowsWithoutMaterializingGroup()
    {
        var group = new FileGroup(@"D:\file.txt");
        for (int i = 0; i < 10; i++)
            group.Add(SearchResult.CreatePreEvicted(@"D:\file.txt", i + 1, i, 5, diskOffset: (i + 1) * 100L));

        group.SelectAll();
        var snapshot = group.GetPreviewSnapshot(3);

        Assert.Equal(0, group.Count);
        Assert.Equal(10, group.SelectedCount);
        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, result => Assert.True(result.IsEvicted));
        Assert.All(snapshot, result => Assert.True(result.IsSelected));
        Assert.Equal([1, 2, 3], snapshot.Select(result => result.LineNumber).ToArray());
    }

    [Fact]
    public void DeselectAll_ClearsAllSelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.SelectAll();
        group.DeselectAll();

        Assert.False(group.AllSelected);
        Assert.Equal(0, group.SelectedCount);
        Assert.All(group, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public void NotifySelectionChanged_UpdatesAllSelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));

        group[0].IsSelected = true;
        group[1].IsSelected = true;
        group.NotifySelectionChanged();
        Assert.True(group.AllSelected);

        group[0].IsSelected = false;
        group.NotifySelectionChanged();
        Assert.False(group.AllSelected);
    }

    [Fact]
    public void SelectedCountText_FormatsCorrectly()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        group[0].IsSelected = true;
        group.NotifySelectionChanged();

        Assert.Equal("1/2 selected", group.SelectedCountText);
    }

    [Fact]
    public void GetSelectedResults_ReturnsOnlySelected()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        group[1].IsSelected = true;

        var selected = group.GetSelectedResults();
        Assert.Single(selected);
        Assert.Equal(2, selected[0].LineNumber);
    }

    [Fact]
    public void MatchCount_EqualsCount()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));
        Assert.Equal(2, group.MatchCount);
    }

    [Fact]
    public void FileNameOnlyResults_DoNotCountAsContentMatchesUntilContentResultArrives()
    {
        var group = new FileGroup(@"D:\file.txt");
        var changed = new List<string>();
        ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        group.Add(MakeResult(@"D:\file.txt", 0, "file.txt"));

        Assert.False(group.HasContentMatches);
        Assert.Equal(1, group.MatchCount);
        Assert.DoesNotContain(nameof(FileGroup.HasContentMatches), changed);

        group.Add(MakeResult(@"D:\file.txt", 12));

        Assert.True(group.HasContentMatches);
        Assert.Equal(1, group.MatchCount);
        Assert.Contains(nameof(FileGroup.HasContentMatches), changed);
        Assert.Contains(nameof(FileGroup.MatchCount), changed);
    }

    [Fact]
    public void FormatSize_Bytes()
    {
        var group = new FileGroup(@"D:\file.txt");
        Assert.Equal("0 B", group.FormattedSize);
    }

    [Fact]
    public void GroupHeaderText_GetSet()
    {
        var group = new FileGroup(@"D:\file.txt");
        Assert.Null(group.GroupHeaderText);
        Assert.False(group.HasGroupHeader);

        group.GroupHeaderText = @"D:\repo";
        Assert.Equal(@"D:\repo", group.GroupHeaderText);
        Assert.True(group.HasGroupHeader);

        // Setting same value again should not change
        group.GroupHeaderText = @"D:\repo";
        Assert.Equal(@"D:\repo", group.GroupHeaderText);
    }

    [Fact]
    public void LoadMetadata_FromDisk()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "hello");
            var group = new FileGroup(tempFile);
            group.LoadMetadata();

            Assert.True(group.FileSize > 0);
            Assert.NotEqual(default, group.LastModified);
            Assert.Contains("B", group.FormattedSize);
            Assert.NotEmpty(group.FormattedDate);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadMetadata_NonexistentFile_NoThrow()
    {
        var group = new FileGroup(@"Z:\nonexistent\path\file.txt");
        group.LoadMetadata(); // should not throw
    }

    [Fact]
    public void LoadMetadata_CachesResult()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "hello");
            FileMetadataCache.Clear();
            var group = new FileGroup(tempFile);
            group.LoadMetadata();
            Assert.True(group.FileSize > 0);

            // Second call should use cache
            var group2 = new FileGroup(tempFile);
            group2.LoadMetadata();
            Assert.Equal(group.FileSize, group2.FileSize);
        }
        finally
        {
            File.Delete(tempFile);
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public async Task BeginLoadMetadata_DispatchesToUiThread()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "content");
            FileMetadataCache.Clear();
            var group = new FileGroup(tempFile);
            var tcs = new TaskCompletionSource();
            group.BeginLoadMetadata(action =>
            {
                action(); // execute directly
                tcs.SetResult();
            });
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(group.FileSize > 0);
        }
        finally
        {
            File.Delete(tempFile);
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public void BeginLoadMetadata_WithCache_SynchronousReturn()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test");
            FileMetadataCache.Clear();

            // Pre-populate cache
            var fi = new FileInfo(tempFile);
            FileMetadataCache.Set(tempFile, new FileMetadata(fi.Length, fi.LastWriteTime));

            var group = new FileGroup(tempFile);
            bool dispatched = false;
            group.BeginLoadMetadata(_ => dispatched = true);
            Assert.False(dispatched); // Should use cache synchronously
            Assert.True(group.FileSize > 0);
        }
        finally
        {
            File.Delete(tempFile);
            FileMetadataCache.Clear();
        }
    }

    [Fact]
    public void FormattedDate_DefaultIsEmpty()
    {
        var group = new FileGroup(@"D:\file.txt");
        Assert.Equal(string.Empty, group.FormattedDate);
    }

    [Fact]
    public void AllSelected_PropertyChangeNotification()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.Add(MakeResult(@"D:\file.txt", 1));

        string? changedProp = null;
        ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, e) => changedProp = e.PropertyName;

        group.AllSelected = true;
        Assert.Equal(nameof(group.AllSelected), changedProp);

        // AllSelected setter always fires PropertyChanged (for TwoWay-bound CheckBox sync)
        changedProp = null;
        group.AllSelected = true;
        Assert.Equal(nameof(group.AllSelected), changedProp);
    }

    [Fact]
    public void HiddenNotificationInterval_NotifiesAtMultiples()
    {
        var group = new FileGroup(@"D:\file.txt");
        // Add exactly PageSize items (all visible)
        for (int i = 0; i < FileGroup.PageSize; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        // 201st item triggers first "more" notification
        bool hasMoreChanged = false;
        ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(group.HasMore)) hasMoreChanged = true;
        };
        group.Add(MakeResult(@"D:\file.txt", FileGroup.PageSize + 1));
        Assert.True(hasMoreChanged);
    }

    /// <summary>
    /// Regression: pre-evicted items beyond PageSize must still be retained in the
    /// collection so that HasMore is true and "Show More" remains visible.
    /// Previously, InsertItem dropped pre-evicted stubs past PageSize, making Count == PageSize
    /// and HasMore == false, hiding the button entirely.
    /// </summary>
    [Fact]
    public void PreEvictedItems_BeyondPageSize_RetainedAndHasMoreIsTrue()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;

        // Add PageSize normal items (these will appear in VisibleResults)
        for (int i = 0; i < FileGroup.PageSize; i++)
            group.Add(MakeResult(@"D:\file.txt", i + 1));

        Assert.Equal(FileGroup.PageSize, group.VisibleResults.Count);
        Assert.False(group.HasMore);

        // Add pre-evicted items beyond the page size
        int extraCount = 50;
        for (int i = 0; i < extraCount; i++)
        {
            var preEvicted = SearchResult.CreatePreEvicted(@"D:\file.txt", FileGroup.PageSize + i + 1, 0, 5, diskOffset: (i + 1) * 100L);
            group.Add(preEvicted);
        }

        // The pre-evicted items must be retained in the collection
        Assert.Equal(FileGroup.PageSize + extraCount, group.Count);
        // They should NOT be added to VisibleResults (they are evicted stubs)
        Assert.Equal(FileGroup.PageSize, group.VisibleResults.Count);
        // HasMore must be true so the "Show More" button appears
        Assert.True(group.HasMore);
        Assert.Equal(extraCount, group.RemainingCount);
    }

    [Fact]
    public void HiddenMatchCount_PeriodicNotification_FiresAt256()
    {
        int oldMax = FileGroup.MaxMatchesPerGroup;
        try
        {
            FileGroup.MaxMatchesPerGroup = 2;
            var group = new FileGroup(@"D:\file.txt");
            // Fill up to the cap
            group.Add(MakeResult(@"D:\file.txt", 1));
            group.Add(MakeResult(@"D:\file.txt", 2));

            var propertyNames = new List<string>();
            ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

            // Add 256 more to trigger the (HiddenMatchCount & 0xFF) == 0 notification
            for (int i = 0; i < 256; i++)
                group.Add(MakeResult(@"D:\file.txt", i + 3));

            Assert.Equal(256, group.HiddenMatchCount);
            // Should have raised HiddenMatchCount and MatchCount notifications
            Assert.Contains(nameof(FileGroup.HiddenMatchCount), propertyNames);
            Assert.Contains(nameof(FileGroup.MatchCount), propertyNames);
        }
        finally
        {
            FileGroup.MaxMatchesPerGroup = oldMax;
        }
    }

    [Fact]
    public void InsertItem_PreEvicted_Collapsed_TracksEvictedCount()
    {
        var group = new FileGroup(@"D:\file.txt");
        // Group starts collapsed (_isExpanded = false by default)
        // Add pre-evicted items
        group.Add(SearchResult.CreatePreEvicted(@"D:\file.txt", 1, 0, 5, diskOffset: 100));
        group.Add(SearchResult.CreatePreEvicted(@"D:\file.txt", 2, 0, 5, diskOffset: 200));
        group.Add(SearchResult.CreatePreEvicted(@"D:\file.txt", 3, 0, 5, diskOffset: 300));

        // Items collection should be empty (evicted stubs don't go into Items)
        Assert.Equal(0, group.Count);
        // TotalStoredCount should reflect evicted stubs
        Assert.True(group.HasMore);
    }

    [Fact]
    public void InsertItem_Normal_Collapsed_GoesIntoItems()
    {
        var group = new FileGroup(@"D:\file.txt");
        // Group starts collapsed — normal (non-evicted) items still go into Items
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));

        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void InsertItem_Expanded_UsesBaseInsertItem()
    {
        var group = new FileGroup(@"D:\file.txt");
        group.IsExpanded = true;
        group.Add(MakeResult(@"D:\file.txt", 1));
        group.Add(MakeResult(@"D:\file.txt", 2));

        Assert.Equal(2, group.Count);
    }
}

// ─── FileGroup: FormatSize branches (MB, GB) ────────────────────────────

public class FileGroupFormatSizeTests
{
    private static SearchResult MakeResult(string path)
        => new(path, 1, "line", 0, 4, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void FormattedSize_KB()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[2048]);
            var group = new FileGroup(tmp);
            group.LoadMetadata();
            Assert.Contains("KB", group.FormattedSize);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void FormattedSize_MB_ViaCache()
    {
        var path = @"C:\fake\large_file.dat";
        FileMetadataCache.Clear();
        try
        {
            FileMetadataCache.Set(path, new FileMetadata(5L * 1024 * 1024, DateTime.Now));
            var group = new FileGroup(path);
            group.LoadMetadata();
            Assert.Contains("MB", group.FormattedSize);
        }
        finally { FileMetadataCache.Clear(); }
    }

    [Fact]
    public void FormattedSize_GB_ViaCache()
    {
        var path = @"C:\fake\huge_file.dat";
        FileMetadataCache.Clear();
        try
        {
            FileMetadataCache.Set(path, new FileMetadata(2L * 1024 * 1024 * 1024, DateTime.Now));
            var group = new FileGroup(path);
            group.LoadMetadata();
            Assert.Contains("GB", group.FormattedSize);
        }
        finally { FileMetadataCache.Clear(); }
    }

    [Fact]
    public void FormattedSize_Bytes_SmallFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[100]);
            var group = new FileGroup(tmp);
            group.LoadMetadata();
            Assert.Contains("B", group.FormattedSize);
            Assert.DoesNotContain("KB", group.FormattedSize);
        }
        finally { File.Delete(tmp); }
    }
}

// ─── FileGroup: catch blocks ────────────────────────────────────────────

public class FileGroupExceptionTests
{
    [Fact]
    public void BeginLoadMetadata_NonexistentFile_DispatchesSafely()
    {
        var group = new FileGroup(@"Z:\nonexistent\totally\fake\file.txt");
        bool dispatched = false;
        group.BeginLoadMetadata(action =>
        {
            dispatched = true;
            action();
        });
        Assert.True(group.FileSize == 0 || dispatched);
    }
}

// ─── FileGroup: DirectoryName null coalesce ─────────────────────────────

public class FileGroupRootPathTests
{
    [Fact]
    public void DirectoryName_RootPath_ReturnsEmpty()
    {
        var group = new FileGroup(@"C:\");
        Assert.Equal(string.Empty, group.DirectoryName);
    }
}

// ─── FileGroup: LoadMetadata + BeginLoadMetadata ────────────────────

public class FileGroupMetadataTests : IDisposable
{
    private readonly string _root;

    public FileGroupMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-fgmeta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        FileMetadataCache.Clear();
    }

    public void Dispose()
    {
        FileMetadataCache.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void LoadMetadata_RealFile_SetsFileSizeAndLastModified()
    {
        var path = Path.Combine(_root, "test.txt");
        File.WriteAllText(path, "hello world");
        var group = new FileGroup(path);
        group.LoadMetadata();
        Assert.True(group.FileSize > 0);
        Assert.NotEqual(default, group.LastModified);
    }

    [Fact]
    public void LoadMetadata_CacheHit_ReturnsCachedValues()
    {
        var path = Path.Combine(_root, "cached.txt");
        var expected = new FileMetadata(42, new DateTime(2024, 1, 1));
        FileMetadataCache.Set(path, expected);

        var group = new FileGroup(path);
        group.LoadMetadata();
        Assert.Equal(42, group.FileSize);
        Assert.Equal(new DateTime(2024, 1, 1), group.LastModified);
    }

    [Fact]
    public void LoadMetadata_NonExistentFile_DoesNotThrow()
    {
        var group = new FileGroup(Path.Combine(_root, "nope.txt"));
        group.LoadMetadata(); // should not throw
        Assert.Equal(0, group.FileSize);
    }

    [Fact]
    public void BeginLoadMetadata_CacheHit_AppliesSynchronously()
    {
        var path = Path.Combine(_root, "cached2.txt");
        FileMetadataCache.Set(path, new FileMetadata(99, new DateTime(2025, 6, 15)));

        var group = new FileGroup(path);
        group.BeginLoadMetadata(action => action());
        Assert.Equal(99, group.FileSize);
        Assert.Equal(new DateTime(2025, 6, 15), group.LastModified);
    }

    [Fact]
    public async Task BeginLoadMetadata_RealFile_DispatchesThenApplies()
    {
        var path = Path.Combine(_root, "real.txt");
        File.WriteAllText(path, new string('x', 100));

        var group = new FileGroup(path);
        var tcs = new TaskCompletionSource();
        group.BeginLoadMetadata(action =>
        {
            action();
            tcs.SetResult();
        });

        // Wait for the background Task.Run + dispatch callback
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.True(tcs.Task.IsCompleted);
        Assert.True(group.FileSize > 0);
    }

    [Fact]
    public void BeginLoadMetadata_NonExistentFile_DoesNotThrow()
    {
        var group = new FileGroup(Path.Combine(_root, "ghost.txt"));
        // dispatch should not be called for non-existent file
        group.BeginLoadMetadata(action => { action(); });
        // Allow background task to finish
        Thread.Sleep(200);
        // No exception should have been thrown
    }
}
