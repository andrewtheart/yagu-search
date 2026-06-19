namespace Yagu.Tests;

public sealed class MainViewModelSearchStatusRegressionTests
{
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "Yagu", "ViewModels", "MainViewModel.cs"));

    [Fact]
    public void SearchLoop_RefreshesStatusFromConsumedMatches_WhenProgressEventsAreBacklogged()
    {
        string matchCase = ExtractWindow(MainViewModelSource, "case SearchEvent.Match m:", "case SearchEvent.MatchBatch mb:");
        Assert.Contains("uiMatchesReceived++;", matchCase);
        Assert.Contains("await AddMatchAsync(m.Result, token).ConfigureAwait(true);", matchCase);
        Assert.Contains("RefreshStatusFromReceivedMatches();", matchCase);

        string matchBatchCase = ExtractWindow(MainViewModelSource, "case SearchEvent.MatchBatch mb:", "case SearchEvent.Progress p:");
        Assert.Contains("uiMatchesReceived += mb.Results.Count;", matchBatchCase);
        Assert.Contains("await AddMatchesAsync(mb.Results, token).ConfigureAwait(true);", matchBatchCase);
        Assert.Contains("RefreshStatusFromReceivedMatches();", matchBatchCase);

        string refreshFunction = ExtractWindow(MainViewModelSource, "void RefreshStatusFromReceivedMatches(bool force = false)", "await foreach");
        Assert.Contains("int receivedMatches = ClampMatchCount(uiMatchesReceived);", refreshFunction);
        Assert.Contains("if (receivedMatches > MatchesFound)", refreshFunction);
        Assert.Contains("MatchesFound = receivedMatches;", refreshFunction);
        Assert.Contains("UpdateFilesPerSecond();", refreshFunction);
    }

    [Fact]
    public void SearchLoop_DoesNotLetStaleProgressOrCompletionLowerVisibleMatchCount()
    {
        string progressCase = ExtractWindow(MainViewModelSource, "case SearchEvent.Progress p:", "case SearchEvent.SearchError e:");
        Assert.Contains("MatchesFound = Math.Max(p.Snapshot.MatchesFound, ClampMatchCount(uiMatchesReceived));", progressCase);

        string completedCase = ExtractWindow(MainViewModelSource, "case SearchEvent.Completed c:", "break;");
        Assert.Contains("int actualTotalMatches = Math.Max(c.Summary.TotalMatches, ClampMatchCount(uiMatchesReceived));", completedCase);
        Assert.Contains("MatchesFound = actualTotalMatches;", completedCase);
        Assert.Contains("TotalMatches = actualTotalMatches", completedCase);
    }

    [Fact]
    public void SearchStatusHeartbeat_UpdatesElapsedEvenBeforeFilesScannedProgressArrives()
    {
        string updateMethod = ExtractWindow(MainViewModelSource, "private void UpdateFilesPerSecond()", "partial void OnFileNameFilterChanged");
        Assert.Contains("if (_searchTimer is null)", updateMethod);
        Assert.DoesNotContain("_searchTimer is null || FilesScanned == 0", updateMethod);
        Assert.Contains("StatusText = $\"{MatchesFound:N0} matches in", updateMethod);
        Assert.Contains("displayDt >= 2.0 && FilesScanned > 0", updateMethod);
        Assert.Contains("dt >= 0.15 && FilesScanned > 0", updateMethod);
    }

    [Fact]
    public void SkippedCount_RemainsHiddenUntilSearchStarts()
    {
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        Assert.Contains("Visibility=\"{x:Bind ViewModel.SkippedCountVisibility, Mode=OneWay}\"", mainWindowXaml);

        Assert.Contains("[ObservableProperty] public partial bool HasPerformedSearch { get; set; }", MainViewModelSource);
        AssertContainsInOrder(MainViewModelSource,
            "SkippedCountVisibility =>",
            "HasPerformedSearch",
            "Microsoft.UI.Xaml.Visibility.Visible",
            "Microsoft.UI.Xaml.Visibility.Collapsed");
        Assert.Contains("partial void OnHasPerformedSearchChanged(bool value) => OnPropertyChanged(nameof(SkippedCountVisibility));", MainViewModelSource);

        string resetForSearch = ExtractWindow(MainViewModelSource, "private void ResetStateForNewSearch()", "private bool IsCurrentSearch");
        AssertContainsInOrder(resetForSearch,
            "FilesSkipped = 0;",
            "HasPerformedSearch = true;",
            "AccessDeniedCount = 0;");

        string clearResults = ExtractWindow(MainViewModelSource, "public async Task ClearResultsAsync()", "public void HydrateResult");
        AssertContainsInOrder(clearResults,
            "FilesSkipped = 0;",
            "HasPerformedSearch = false;",
            "AccessDeniedCount = 0;");

        string loadSession = ExtractWindow(MainViewModelSource, "public async Task<SessionFileService.SessionHeader> LoadSessionAsync", "bool firstBatch = true;");
        AssertContainsInOrder(loadSession,
            "FilesSkipped = 0;",
            "HasPerformedSearch = false;",
            "AccessDeniedCount = 0;");
    }

    [Fact]
    public void SearchStatusHeartbeat_EnqueuesHighPriorityRefreshWhileSearchIsActive()
    {
        Assert.Contains("private CancellationTokenSource? _searchStatusHeartbeatCts;", MainViewModelSource);
        Assert.Contains("StartSearchStatusHeartbeat();", MainViewModelSource);

        string runHeartbeatMethod = ExtractWindow(MainViewModelSource, "private async Task RunSearchStatusHeartbeatAsync", "private void UpdateSearchStatusHeartbeat()");
        Assert.Contains("new PeriodicTimer(TimeSpan.FromMilliseconds(250))", runHeartbeatMethod);
        Assert.Contains("_dispatcher.TryEnqueue(DispatcherQueuePriority.High, UpdateSearchStatusHeartbeat)", runHeartbeatMethod);

        string stopTimerMethod = ExtractWindow(MainViewModelSource, "private TimeSpan StopSearchTimer()", "private string BuildCancelledStatus");
        Assert.Contains("StopSearchStatusHeartbeat();", stopTimerMethod);

        string heartbeatMethod = ExtractWindow(MainViewModelSource, "private void UpdateSearchStatusHeartbeat()", "private string BuildCancelledStatus");
        Assert.Contains("_searchTimer is null", heartbeatMethod);
        Assert.Contains("!IsSearching", heartbeatMethod);
        Assert.Contains("UpdateFilesPerSecond();", heartbeatMethod);
    }

    [Fact]
    public void SearchLoop_StopsElapsedTimerWhenScanCompletesBeforeFinalResultDrain()
    {
        string scanCompletedCase = ExtractWindow(MainViewModelSource, "case SearchEvent.ScanCompleted sc:", "case SearchEvent.Completed c:");
        Assert.Contains("var scanElapsed = StopSearchTimer();", scanCompletedCase);
        Assert.Contains("Finalizing results...", scanCompletedCase);
        Assert.DoesNotContain("IsSearching = false", scanCompletedCase);

        string stopTimerMethod = ExtractWindow(MainViewModelSource, "private TimeSpan StopSearchTimer()", "private string BuildCancelledStatus");
        Assert.Contains("if (timer is null)", stopTimerMethod);
        Assert.Contains("return _lastSearchElapsed;", stopTimerMethod);
    }

    [Fact]
    public void SearchSortRefresh_DefersLargeDegradedRefreshesDuringActiveSearch()
    {
        Assert.Contains("SearchSortRefreshDegradedDeferGroupThreshold = 20_000", MainViewModelSource);

        string refreshMethod = ExtractWindow(MainViewModelSource, "private void QueueSearchSortRefreshIfDue()", "private void NotifyResultAvailabilityChanged()");
        Assert.Contains("if (Degraded && groupCount >= SearchSortRefreshDegradedDeferGroupThreshold)", refreshMethod);
        Assert.Contains("_searchSortRefreshIntervalSec = SearchSortRefreshIntervalMaxSec;", refreshMethod);
        Assert.Contains("Deferring periodic in-search sort refresh for degraded large result set", refreshMethod);
        AssertContainsInOrder(refreshMethod,
            "if (Degraded && groupCount >= SearchSortRefreshDegradedDeferGroupThreshold)",
            "return;",
            "ApplySortAndFilter();");

        string completedCase = ExtractWindow(MainViewModelSource, "case SearchEvent.Completed c:", "break;");
        Assert.Contains("ApplySortAndFilter();", completedCase);
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int searchFrom = 0;
        foreach (string item in expected)
        {
            int index = text.IndexOf(item, searchFrom, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find '{item}' after offset {searchFrom}.");
            searchFrom = index + item.Length;
        }
    }

    private static string ExtractWindow(string source, string startMarker, string endMarker, int occurrence = 1)
    {
        int start = IndexOfOccurrence(source, startMarker, occurrence);
        Assert.True(start >= 0, $"Could not find marker '{startMarker}'.");

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find marker '{endMarker}' after '{startMarker}'.");

        return source[start..end];
    }

    private static int IndexOfOccurrence(string source, string marker, int occurrence)
    {
        int index = -1;
        for (int current = 0; current < occurrence; current++)
        {
            index = source.IndexOf(marker, index + 1, StringComparison.Ordinal);
            if (index < 0)
                return -1;
        }

        return index;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yagu.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Yagu.sln from the test output directory.");
    }
}