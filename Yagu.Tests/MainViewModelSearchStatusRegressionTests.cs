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