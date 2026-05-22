using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Yagu.Models;
using Yagu.Services;
using Xunit.Abstractions;

namespace Yagu.Tests;

[ExcludeFromCodeCoverage]
public sealed class UiResultPerformanceTests
{
    private const int LargeResultCount = 100_000;
    private const int LargeFileCount = 5_000;
    private const long Megabyte = 1024L * 1024L;

    private readonly ITestOutputHelper _output;

    public UiResultPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void LargeResultIngestion_StaysWithinUiPerformanceBudget()
    {
        var results = CreateLargeResultSet(LargeResultCount, LargeFileCount);
        var collection = new SearchResultCollection();

        var measurement = Measure(() => collection.AddRange(results));

        WriteMetric(nameof(LargeResultIngestion_StaysWithinUiPerformanceBudget), measurement);
        Assert.Equal(LargeFileCount, collection.AllGroups.Count);
        Assert.Equal(LargeFileCount, collection.VisibleGroups.Count);
        Assert.All(collection.AllGroups, group => Assert.True(group.VisibleResults.Count <= FileGroup.PageSize));
        AssertWithinBudget(
            measurement,
            maxElapsed: TimeSpan.FromMilliseconds(GetBudget("YAGU_UI_PERF_INGEST_MS", 5_000)),
            maxAllocatedBytes: GetBudget("YAGU_UI_PERF_INGEST_MB", 128) * Megabyte);
    }

    [Fact]
    public void LargeResultSortAndFilter_StaysWithinUiPerformanceBudget()
    {
        var collection = new SearchResultCollection
        {
            GroupMode = GroupMode.Folder,
            SortModeIndex = 0,
            SortDirectionIndex = 0,
        };
        collection.AddRange(CreateLargeResultSet(LargeResultCount, LargeFileCount));

        var sortMeasurement = Measure(collection.ApplySortAndFilter);
        WriteMetric("LargeResultSort", sortMeasurement);
        Assert.Equal(LargeFileCount, collection.VisibleGroups.Count);
        Assert.True(collection.VisibleGroups.Count(group => group.HasGroupHeader) > 1);
        AssertWithinBudget(
            sortMeasurement,
            maxElapsed: TimeSpan.FromMilliseconds(GetBudget("YAGU_UI_PERF_SORT_MS", 2_500)),
            maxAllocatedBytes: GetBudget("YAGU_UI_PERF_SORT_MB", 32) * Megabyte);

        collection.FileNameFilter = $"file-{LargeFileCount - 1:D5}";
        var filterMeasurement = Measure(collection.ApplySortAndFilter);
        WriteMetric("LargeResultFileNameFilter", filterMeasurement);
        Assert.Single(collection.VisibleGroups);
        AssertWithinBudget(
            filterMeasurement,
            maxElapsed: TimeSpan.FromMilliseconds(GetBudget("YAGU_UI_PERF_FILTER_MS", 2_500)),
            maxAllocatedBytes: GetBudget("YAGU_UI_PERF_FILTER_MB", 32) * Megabyte);
    }

    private static SearchResult[] CreateLargeResultSet(int resultCount, int fileCount)
    {
        var results = new SearchResult[resultCount];
        for (int resultIndex = 0; resultIndex < resultCount; resultIndex++)
        {
            int fileIndex = resultIndex % fileCount;
            int directoryIndex = fileIndex / 100;
            results[resultIndex] = new SearchResult(
                FilePath: $@"D:\yagu-perf\folder-{directoryIndex:D3}\file-{fileIndex:D5}.txt",
                LineNumber: resultIndex + 1,
                MatchLine: $"needle-{resultIndex:D6} lorem ipsum dolor sit amet",
                MatchStartColumn: 0,
                MatchLength: 6,
                ContextBefore: [],
                ContextAfter: []);
        }

        return results;
    }

    private static PerformanceMeasurement Measure(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new PerformanceMeasurement(stopwatch.Elapsed, allocatedBytes);
    }

    private void WriteMetric(string scenario, PerformanceMeasurement measurement)
    {
        _output.WriteLine($"{scenario}: elapsed={measurement.Elapsed.TotalMilliseconds:F1}ms, allocated={measurement.AllocatedBytes / (double)Megabyte:F1}MB");
    }

    private static void AssertWithinBudget(PerformanceMeasurement measurement, TimeSpan maxElapsed, long maxAllocatedBytes)
    {
        Assert.True(
            measurement.Elapsed <= maxElapsed,
            $"UI performance regression: elapsed {measurement.Elapsed.TotalMilliseconds:F1}ms exceeded budget {maxElapsed.TotalMilliseconds:F1}ms.");
        Assert.True(
            measurement.AllocatedBytes <= maxAllocatedBytes,
            $"UI allocation regression: allocated {measurement.AllocatedBytes / (double)Megabyte:F1}MB exceeded budget {maxAllocatedBytes / (double)Megabyte:F1}MB.");
    }

    private static int GetBudget(string environmentVariable, int defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(environmentVariable);
        return int.TryParse(rawValue, out int parsedValue) && parsedValue > 0 ? parsedValue : defaultValue;
    }

    private readonly record struct PerformanceMeasurement(TimeSpan Elapsed, long AllocatedBytes);
}

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

    [Theory]
    [InlineData(0)] // none
    [InlineData(1)] // match count
    [InlineData(2)] // date
    [InlineData(3)] // size
    [InlineData(4)] // file name
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

    [Fact]
    public void SortModeIndex_None_PreservesInsertionOrder()
    {
        var col = new SearchResultCollection { SortModeIndex = 0 };
        col.Add(MakeResult(@"C:\src\b.txt"));
        col.Add(MakeResult(@"C:\src\a.txt"));

        col.ApplySortAndFilter();

        Assert.Equal("b.txt", col.VisibleGroups[0].FileName);
        Assert.Equal("a.txt", col.VisibleGroups[1].FileName);
    }

    [Fact]
    public void SortModeIndex_FileName_SortsByFileName()
    {
        var col = new SearchResultCollection
        {
            SortModeIndex = 4,
            SortDirectionIndex = 1,
        };
        col.Add(MakeResult(@"C:\src\b.txt"));
        col.Add(MakeResult(@"C:\src\a.txt"));

        col.ApplySortAndFilter();

        Assert.Equal("a.txt", col.VisibleGroups[0].FileName);
        Assert.Equal("b.txt", col.VisibleGroups[1].FileName);
    }

    [Fact]
    public void SortCriteria_ThenByFileSize_PreservesMatchCountPrimarySort()
    {
        FileMetadataCache.Clear();
        try
        {
            string largeTie = @"C:\src\large-tie.txt";
            string smallTie = @"C:\src\small-tie.txt";
            string mostMatches = @"C:\src\most-matches.txt";
            FileMetadataCache.Set(largeTie, new FileMetadata(30, DateTime.Now));
            FileMetadataCache.Set(smallTie, new FileMetadata(10, DateTime.Now));
            FileMetadataCache.Set(mostMatches, new FileMetadata(20, DateTime.Now));

            var col = new SearchResultCollection();
            col.Add(MakeResult(largeTie, "m1"), group => group.LoadMetadata());
            col.Add(MakeResult(largeTie, "m2"));
            col.Add(MakeResult(smallTie, "m1"), group => group.LoadMetadata());
            col.Add(MakeResult(smallTie, "m2"));
            col.Add(MakeResult(mostMatches, "m1"), group => group.LoadMetadata());
            col.Add(MakeResult(mostMatches, "m2"));
            col.Add(MakeResult(mostMatches, "m3"));
            col.SetSortCriteria(new[]
            {
                new SortCriterion(1, 0),
                new SortCriterion(3, 1),
            });

            col.ApplySortAndFilter();

            Assert.Equal(new[] { mostMatches, smallTie, largeTie }, col.VisibleGroups.Select(group => group.FilePath).ToArray());
        }
        finally
        {
            FileMetadataCache.Clear();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GroupByDirectory_WithAllSortModes(int mode)
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\dir1\a.txt"));
        col.Add(MakeResult(@"C:\dir2\b.txt"));
        col.GroupMode = GroupMode.Folder;
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
        col.GroupMode = GroupMode.Folder;
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
        store.Drain();
        Assert.Equal(2, evicted);
        // Already evicted, second call evicts nothing
        Assert.Equal(0, col.EvictAll(store));
    }

    [Fact]
    public void EvictAll_LargerThanEvictionBatch_EvictsAllResults()
    {
        using var store = new ResultStore();
        var col = new SearchResultCollection();
        int resultCount = SearchResultCollection.EvictionBatchSize + 3;

        for (int i = 0; i < resultCount; i++)
            col.Add(MakeResult($@"C:\batch\file-{i:D5}.txt", $"line{i}"));

        int evicted = col.EvictAll(store);
        store.Drain();

        Assert.Equal(resultCount, evicted);
        Assert.Equal(resultCount, store.EvictedCount);
        Assert.All(col.AllGroups, group => Assert.All(group, result => Assert.True(result.IsEvicted)));
    }

    [Fact]
    public void SearchResult_EvictWith_ConcurrentCallsOnlyWritesOnce()
    {
        var result = MakeResult(@"C:\a.txt", "line1");
        int writes = 0;

        long Writer(string matchLine, IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            Interlocked.Increment(ref writes);
            Thread.Sleep(10);
            return 123;
        }

        Parallel.For(0, 32, _ => result.EvictWith(Writer));

        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.True(result.IsEvicted);
        Assert.Equal(123, result.DiskOffset);
    }

    [Fact]
    public void EvictAll_MultipleResultsInSameGroup_EvictsAll()
    {
        using var store = new ResultStore();
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt", "line1"));
        col.Add(MakeResult(@"C:\a.txt", "line2"));
        col.Add(MakeResult(@"C:\a.txt", "line3"));
        Assert.Single(col.AllGroups);
        Assert.Equal(3, col.AllGroups[0].Count);

        int evicted = col.EvictAll(store);
        store.Drain();
        Assert.Equal(3, evicted);
        Assert.True(col.AllGroups[0][0].IsEvicted);
        Assert.True(col.AllGroups[0][1].IsEvicted);
        Assert.True(col.AllGroups[0][2].IsEvicted);
    }

    [Fact]
    public void EvictAll_SnapshotsGroups_DoesNotEvictGroupsAddedAfterSnapshot()
    {
        using var store = new ResultStore();
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt", "line1"));

        // Evict existing — this tests that snapshot captures the current state
        int evicted = col.EvictAll(store);
        store.Drain();
        Assert.Equal(1, evicted);

        // Add a new result after eviction
        col.Add(MakeResult(@"C:\b.txt", "line2"));
        // The new result should NOT be evicted yet
        Assert.False(col.AllGroups[1][0].IsEvicted);

        // Second evict picks up the new result
        int evicted2 = col.EvictAll(store);
        store.Drain();
        Assert.Equal(1, evicted2);
        Assert.True(col.AllGroups[1][0].IsEvicted);
    }

    [Fact]
    public void EvictAll_SkipsAlreadyEvictedResults_Idempotent()
    {
        using var store = new ResultStore();
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\a.txt", "line1"));
        col.Add(MakeResult(@"C:\a.txt", "line2"));

        // Evict once
        int evicted1 = col.EvictAll(store);
        store.Drain();
        Assert.Equal(2, evicted1);

        // Add another result to same group
        col.Add(MakeResult(@"C:\a.txt", "line3"));

        // Second evict only evicts the new one
        int evicted2 = col.EvictAll(store);
        store.Drain();
        Assert.Equal(1, evicted2);
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

        store.Drain();

        // Results should be evicted
        Assert.True(col.AllGroups[0][0].IsEvicted);
        Assert.True(col.AllGroups[1][0].IsEvicted);
    }

    [Fact]
    public void PagingSource_QueuesSingleMatchEvictionAndChunksBulkEviction()
    {
        string repoRoot = FindRepoRoot();
        string viewModelSource = File.ReadAllText(Path.Combine(repoRoot, "Yagu", "ViewModels", "MainViewModel.cs"));
        string collectionSource = File.ReadAllText(Path.Combine(repoRoot, "Yagu", "Models", "SearchResultCollection.cs"));
        string storeSource = File.ReadAllText(Path.Combine(repoRoot, "Yagu", "Services", "ResultStore.cs"));

        // Degraded-mode single-match eviction is routed through the ResultStore's
        // single drain task instead of a per-match Task.Run + WriteBatch lock.
        string addMatch = ExtractMethodWindow(viewModelSource, "AddMatch", window: 900);
        Assert.Contains("_resultStore.EnqueueEvict(result)", addMatch);
        Assert.DoesNotContain("Task.Run", addMatch);
        Assert.DoesNotContain("WriteBatch", addMatch);

        // The ResultStore exposes EnqueueEvict + Drain backed by a Channel and a
        // single background drain task — this is what eliminates the writer-lock
        // contention between bulk EvictAll and degraded-mode AddMatch writes.
        Assert.Contains("EnqueueEvict", storeSource);
        Assert.Contains("Channel.CreateUnbounded<SearchResult>", storeSource);
        Assert.Contains("DrainLoopAsync", storeSource);

        // Bulk eviction enqueues into the same queue and returns immediately —
        // no synchronous Drain() inside EvictAll so it never blocks the caller.
        string evictAll = ExtractMethodWindow(collectionSource, "EvictAll", window: 1800);
        Assert.Contains("EnqueueEvict", evictAll);
        Assert.DoesNotContain("resultStore.Drain()", evictAll);
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

    private static string ExtractMethodWindow(string source, string methodName, int window)
    {
        int index = FindMethodDefinition(source, methodName);
        int end = Math.Min(source.Length, index + window);
        return source[index..end];
    }

    private static int FindMethodDefinition(string source, string methodName)
    {
        string needle = methodName + "(";
        int search = 0;
        while (true)
        {
            int index = source.IndexOf(needle, search, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Method '{methodName}' not found.");

            int lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = source.IndexOf('\n', index);
            lineEnd = lineEnd < 0 ? source.Length : lineEnd;
            string line = source[lineStart..lineEnd];
            if (line.Contains("private ", StringComparison.Ordinal)
                || line.Contains("public ", StringComparison.Ordinal)
                || line.Contains("internal ", StringComparison.Ordinal)
                || line.Contains("protected ", StringComparison.Ordinal))
            {
                return lineStart;
            }

            search = index + needle.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}

// ─── SearchResultCollection: sort branches ──────────────────────────────

public class SearchResultCollectionSortBranchTests
{
    private static SearchResult MakeResult(string path) =>
        new(path, 1, "x", 0, 1, [], []);

    [Theory]
    [InlineData(2, true)]  // LastModified ascending, grouped
    [InlineData(3, true)]  // FileSize ascending, grouped
    [InlineData(4, true)]  // FileName ascending, grouped
    [InlineData(2, false)] // LastModified ascending, non-grouped
    [InlineData(3, false)] // FileSize ascending, non-grouped
    [InlineData(4, false)] // FileName ascending, non-grouped
    public void AscendingSortModes_AllCombinations(int mode, bool grouped)
    {
        var col = new SearchResultCollection();
        col.Add(MakeResult(@"C:\dir1\a.txt"));
        col.Add(MakeResult(@"C:\dir2\b.txt"));
        col.GroupMode = grouped ? GroupMode.Folder : GroupMode.None;
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
