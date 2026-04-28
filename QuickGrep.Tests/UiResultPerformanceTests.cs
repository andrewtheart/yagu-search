using System.Diagnostics;
using QuickGrep.Models;
using Xunit.Abstractions;

namespace QuickGrep.Tests;

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
            maxElapsed: TimeSpan.FromMilliseconds(GetBudget("QUICKGREP_UI_PERF_INGEST_MS", 5_000)),
            maxAllocatedBytes: GetBudget("QUICKGREP_UI_PERF_INGEST_MB", 128) * Megabyte);
    }

    [Fact]
    public void LargeResultSortAndFilter_StaysWithinUiPerformanceBudget()
    {
        var collection = new SearchResultCollection
        {
            GroupByDirectory = true,
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
            maxElapsed: TimeSpan.FromMilliseconds(GetBudget("QUICKGREP_UI_PERF_SORT_MS", 2_500)),
            maxAllocatedBytes: GetBudget("QUICKGREP_UI_PERF_SORT_MB", 32) * Megabyte);

        collection.ResultFilter = $"needle-{LargeResultCount - 1:D6}";
        var filterMeasurement = Measure(collection.ApplySortAndFilter);
        WriteMetric("LargeResultContentFilter", filterMeasurement);
        Assert.Single(collection.VisibleGroups);
        AssertWithinBudget(
            filterMeasurement,
            maxElapsed: TimeSpan.FromMilliseconds(GetBudget("QUICKGREP_UI_PERF_FILTER_MS", 2_500)),
            maxAllocatedBytes: GetBudget("QUICKGREP_UI_PERF_FILTER_MB", 32) * Megabyte);
    }

    private static SearchResult[] CreateLargeResultSet(int resultCount, int fileCount)
    {
        var results = new SearchResult[resultCount];
        for (int resultIndex = 0; resultIndex < resultCount; resultIndex++)
        {
            int fileIndex = resultIndex % fileCount;
            int directoryIndex = fileIndex / 100;
            results[resultIndex] = new SearchResult(
                FilePath: $@"D:\quickgrep-perf\folder-{directoryIndex:D3}\file-{fileIndex:D5}.txt",
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
