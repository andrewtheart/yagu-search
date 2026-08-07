namespace Yagu.Tests;

public sealed class MainViewModelSearchStatusRegressionTests
{
    private static readonly string MainViewModelSource = MainViewModelPartials.Text;

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
    public void ProgressTooltip_ShowsActiveDiscoveryWhenTotalUnknown()
    {
        int start = MainViewModelSource.IndexOf("public string ProgressTooltip", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected ProgressTooltip in MainViewModel.cs");
        string body = MainViewModelSource.Substring(start, Math.Min(2000, MainViewModelSource.Length - start));

        // Known total -> percentage, clamped so it never reads over 100% on a stale snapshot.
        Assert.Contains("if (TotalFiles > 0)", body);
        Assert.Contains("Math.Min(100.0", body);
        // Unknown total while a search is running -> an active "Discovering files" state with the running
        // processed count, NOT a static "Waiting for file list" that looks frozen during a long full-tree
        // enumeration (the reported "stuck waiting for file list" symptom).
        Assert.Contains("if (IsSearching)", body);
        Assert.Contains("Discovering files", body);
        Assert.Contains("found so far", body);
        // The static idle text is only the fallback when NOT searching (after the IsSearching branch).
        // Match the actual `return "Waiting for file list…"` statement, not the same phrase quoted in the
        // explanatory comment that precedes the IsSearching branch.
        int searching = body.IndexOf("if (IsSearching)", StringComparison.Ordinal);
        int waiting = body.IndexOf("return \"Waiting for file list", StringComparison.Ordinal);
        Assert.True(waiting > searching,
            "\"Waiting for file list\" must be the idle fallback after the IsSearching branch.");

        // The tooltip recomputes when any of its inputs change.
        Assert.Contains("partial void OnFilesScannedChanged(int value)", MainViewModelSource);
        Assert.Contains("partial void OnTotalFilesChanged(int value)", MainViewModelSource);
        Assert.Contains("OnPropertyChanged(nameof(ProgressTooltip));", MainViewModelSource);
        Assert.Contains("OnPropertyChanged(nameof(SearchProgressRightLabel));", MainViewModelSource);
        Assert.Contains("OnFilesSkippedChanged(int value) { OnPropertyChanged(nameof(OtherSkippedCount)); OnPropertyChanged(nameof(ProgressTooltip));", MainViewModelSource);
        Assert.Contains("[NotifyPropertyChangedFor(nameof(ProgressTooltip))]", MainViewModelSource);
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
    public void SearchLoop_UsesMonotonicDisplayedProgressAndResetsItForEachSearch()
    {
        string progressCase = ExtractWindow(MainViewModelSource, "case SearchEvent.Progress p:", "case SearchEvent.SearchError e:");
        Assert.Contains("UpdateDisplayedSearchProgress(", progressCase);
        Assert.Contains("p.Snapshot.FilesScanned", progressCase);
        Assert.Contains("p.Snapshot.TotalFiles", progressCase);
        Assert.Contains("indeterminate: !p.Snapshot.TotalFilesKnown", progressCase);
        Assert.Contains("if (SearchInNameFirstPhase && p.Snapshot.TotalFilesKnown)", progressCase);

        string resetForSearch = ExtractWindow(MainViewModelSource, "private void ResetStateForNewSearch()", "private bool IsCurrentSearch");
        Assert.Contains("ResetDisplayedSearchProgress();", resetForSearch);

        Assert.Contains("private readonly SearchProgressDisplayTracker _searchProgressDisplayTracker = new();", MainViewModelSource);
        Assert.Contains("public double DisplayedSearchProgressPercent => _searchProgressDisplayTracker.Percent;", MainViewModelSource);
        Assert.Contains("_searchProgressDisplayTracker.Update(filesProcessed, totalFiles, indeterminate)", MainViewModelSource);
        Assert.Contains("_searchProgressDisplayTracker.Reset()", MainViewModelSource);
    }

    [Fact]
    public void SearchProgress_ReportsLiveFileCountWhileTheTotalIsStillUnknown()
    {
        // An indeterminate bar with a blank label left a multi-minute discovery looking like no progress.
        string label = ExtractWindow(
            MainViewModelSource,
            "public string SearchProgressRightLabel => SearchProgressIndeterminate",
            "partial void OnFilesScannedChanged");
        Assert.Contains("? DiscoveryProgressLabel", label);
        Assert.DoesNotContain("? string.Empty", label);
        Assert.Contains("FilesScanned > 0 ? $\"{FilesScanned:N0} files\" : \"Discovering", MainViewModelSource);
    }

    [Fact]
    public void SearchStatusHeartbeat_UpdatesElapsedEvenBeforeFilesScannedProgressArrives()
    {
        string updateMethod = ExtractWindow(MainViewModelSource, "private void UpdateFilesPerSecond()", "partial void OnFileNameFilterChanged");
        Assert.Contains("if (_searchTimer is null)", updateMethod);
        Assert.DoesNotContain("_searchTimer is null || FilesScanned == 0", updateMethod);
        Assert.Contains("_sourceBackedSearchProgress?.BuildPhaseLabel(FilesScanned, TotalFiles)", updateMethod);
        Assert.Contains("string phaseSuffix = sourcePhase is null ? string.Empty : $\" — {sourcePhase}\";", updateMethod);
        Assert.Contains("StatusText = $\"{MatchesFound:N0} matches in", updateMethod);
        Assert.Contains("displayDt >= 2.0 && FilesScanned > 0", updateMethod);
        Assert.Contains("dt >= 0.15 && FilesScanned > 0", updateMethod);
    }

    [Fact]
    public void SkippedInfo_AppearsOnlyForPositiveCount_AndReusesBreakdownOverlay()
    {
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        Assert.DoesNotContain("x:Name=\"SkipCountBlock\"", mainWindowXaml);
        Assert.Contains("x:Name=\"SkippedInfoButton\"", mainWindowXaml);
        Assert.Contains("<Grid MinWidth=\"32\" Height=\"26\">", mainWindowXaml);
        Assert.Contains("HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\"", mainWindowXaml);
        Assert.Contains("Margin=\"17,0,0,0\"", mainWindowXaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.SkippedInfoVisibility, Mode=OneWay}\"", mainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.FilesSkipped, Mode=OneWay}\"", mainWindowXaml);
        Assert.Contains("Foreground=\"{ThemeResource SystemFillColorCautionBrush}\"", mainWindowXaml);
        Assert.Contains("PointerEntered=\"OnSkipInfoPointerEntered\"", mainWindowXaml);
        Assert.Contains("PointerExited=\"OnSkipInfoPointerExited\"", mainWindowXaml);
        Assert.Contains("Click=\"OnSkipInfoClicked\"", mainWindowXaml);
        Assert.Contains("x:Name=\"SkipBreakdownOverlay\"", mainWindowXaml);

        AssertContainsInOrder(MainViewModelSource,
            "SkippedInfoVisibility =>",
            "FilesSkipped > 0",
            "Microsoft.UI.Xaml.Visibility.Visible",
            "Microsoft.UI.Xaml.Visibility.Collapsed");
        Assert.Contains("OnPropertyChanged(nameof(SkippedInfoVisibility))", MainViewModelSource);

        string mainWindowSource = string.Join(Environment.NewLine,
            Directory.GetFiles(
                Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow"),
                "MainWindow*.cs")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));
        string pointerEntered = ExtractWindow(mainWindowSource, "private void OnSkipInfoPointerEntered", "private void OnSkipInfoPointerExited");
        Assert.Contains("ShowSkipBreakdownOverlay();", pointerEntered);
        string pointerExited = ExtractWindow(mainWindowSource, "private void OnSkipInfoPointerExited", "private void OnSkipBreakdownOverlayPointerEntered");
        Assert.Contains("ScheduleSkipBreakdownHoverHide();", pointerExited);
        string clicked = ExtractWindow(mainWindowSource, "private void OnSkipInfoClicked", "private void OnSkipInfoPointerEntered");
        Assert.Contains("_skipBreakdownPinned = !_skipBreakdownPinned;", clicked);
        AssertContainsInOrder(clicked,
            "if (_skipBreakdownPinned)",
            "ShowSkipBreakdownOverlay();",
            "HideSkipBreakdownOverlay();");

        // The overlay only auto-hides once the pointer is over neither the icon nor the panel, so the
        // close button stays reachable across the gap between them.
        string scheduleHide = ExtractWindow(mainWindowSource, "private void ScheduleSkipBreakdownHoverHide()", "private void CancelSkipBreakdownHoverHide()");
        Assert.Contains("if (_skipBreakdownPinned)", scheduleHide);
        Assert.Contains("!_skipBreakdownPointerOverIcon && !_skipBreakdownPointerOverPanel", scheduleHide);

        string resetForSearch = ExtractWindow(MainViewModelSource, "private void ResetStateForNewSearch()", "private bool IsCurrentSearch");
        AssertContainsInOrder(resetForSearch,
            "FilesSkipped = 0;",
            "HasPerformedSearch = true;",
            "AccessDeniedCount = 0;");

        string clearResults = ExtractWindow(MainViewModelSource, "public async Task ClearResultsAsync()", "OnPropertyChanged(nameof(HasResults));");
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
    public void SkipBreakdownOverlay_IsCompactAnchoredUnderTheIcon_AndDismissesWithCloseOrEscape()
    {
        string repoRoot = FindRepoRoot();
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(repoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
        string overlay = ExtractWindow(mainWindowXaml, "x:Name=\"SkipBreakdownOverlay\"", "<!-- Embedded terminal panel");

        // Compact panel: the table is rendered as a real grid (a monospaced font cannot align rows whose
        // category emoji have different advance widths) while the long footnote wraps in a bounded width.
        Assert.Contains("MaxWidth=\"420\"", overlay);
        Assert.Contains("x:Name=\"SkipBreakdownContent\"", overlay);
        Assert.Contains("Text=\"{x:Bind ViewModel.SkipFootnoteText}\"", overlay);
        Assert.Contains("TextWrapping=\"Wrap\"", overlay);
        Assert.DoesNotContain("Text=\"{x:Bind ViewModel.SkipTooltip, Mode=OneWay}\"", overlay);

        // Every category shares one grid's columns, and the headline total is its own summary section
        // below a divider that spans those columns, so both stay vertically aligned.
        string settingsMenus = File.ReadAllText(
            Path.Combine(repoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SettingsMenus.cs"));
        string table = ExtractWindow(settingsMenus, "private static Grid BuildSkipTable", "private static void AddSkipCells");
        AssertContainsInOrder(table,
            "grid.ColumnDefinitions.Add",
            "AddSkipCells(grid, row++, entry.Glyph, entry.Label, entry.Count, emphasized: false);",
            "Grid.SetColumnSpan(divider, 3);",
            "AddSkipCells(grid, row, glyph: string.Empty, label: \"Total skipped\", count: totalCount, emphasized: true);");

        // Anchored in code under the icon, so it spans the rows and is positioned from the window origin.
        Assert.Contains("Grid.Row=\"0\" Grid.RowSpan=\"7\"", overlay);
        Assert.Contains("HorizontalAlignment=\"Left\"", overlay);
        Assert.Contains("VerticalAlignment=\"Top\"", overlay);

        Assert.Contains("x:Name=\"SkipBreakdownCloseButton\"", overlay);
        Assert.Contains("Click=\"OnSkipBreakdownCloseClicked\"", overlay);
        Assert.Contains("PointerEntered=\"OnSkipBreakdownOverlayPointerEntered\"", overlay);
        Assert.Contains("PointerExited=\"OnSkipBreakdownOverlayPointerExited\"", overlay);

        // The footnote is a separate property so it can wrap; SkipTooltip still carries both blocks for
        // the icon's accessible description.
        Assert.Contains("public string SkipFootnoteText => SkipFootnote;", MainViewModelSource);
        Assert.Contains("public string SkipBreakdownDetails", MainViewModelSource);
        Assert.Contains("public string SkipTooltip =>", MainViewModelSource);
        Assert.Contains("OnPropertyChanged(nameof(SkipBreakdownDetails));", MainViewModelSource);

        string mainWindowSource = string.Join(Environment.NewLine,
            Directory.GetFiles(
                Path.Combine(repoRoot, "src", "Yagu", "UI", "Windows", "MainWindow"),
                "MainWindow*.cs")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));

        string position = ExtractWindow(mainWindowSource, "private void PositionSkipBreakdownOverlay()", "private void ScheduleSkipBreakdownHoverHide()");
        AssertContainsInOrder(position,
            "SkipBreakdownOverlay.UpdateLayout();",
            "SkippedInfoButton.TransformToVisual(RootGrid)",
            "(SkippedInfoButton.ActualWidth / 2) - (overlayWidth / 2)",
            "anchor.Y + SkippedInfoButton.ActualHeight",
            "SkipBreakdownOverlay.Margin = new Thickness(Math.Clamp(left, 8, maxLeft), top, 0, 0);");

        // Esc closes the topmost hand-built overlay, and returns false otherwise so it still cancels a
        // running search / closes the find bar.
        string escape = ExtractWindow(mainWindowSource, "private bool TryDismissOpenOverlayOnEscape()", "private bool TryHandlePreviewMatchEnter");
        AssertContainsInOrder(escape,
            "SkipBreakdownOverlay?.Visibility == Visibility.Visible",
            "HideSkipBreakdownOverlay();",
            "IndexStatusHoverOverlay?.Visibility == Visibility.Visible",
            "HideIndexStatusHoverOverlay();",
            "PreviewShowMoreTooltipOverlay?.Visibility == Visibility.Visible",
            "HidePreviewShowMoreTooltip();",
            "return false;");
        Assert.Contains("if (e.Key == Windows.System.VirtualKey.Escape && TryDismissOpenOverlayOnEscape())", mainWindowSource);
    }

    [Fact]
    public void SessionBusyOverlay_ShowsTranslucentProgressWhileSavingOrLoading()
    {
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

        // A full-content translucent overlay (mirroring the preview loading overlays) covers the
        // working area whenever a .yagu-session save/load is in progress, driven by IsSessionBusy.
        string overlay = ExtractWindow(mainWindowXaml, "x:Name=\"SessionBusyOverlay\"", "</Grid>");
        AssertContainsInOrder(overlay,
            "Grid.Row=\"2\" Grid.RowSpan=\"4\"",
            "Visibility=\"{x:Bind ViewModel.IsSessionBusy, Mode=OneWay}\"",
            "AcrylicBackgroundFillColorBaseBrush",
            "<ProgressRing IsActive=\"{x:Bind ViewModel.IsSessionBusy, Mode=OneWay}\"",
            "Text=\"{x:Bind ViewModel.SessionProgressText, Mode=OneWay}\"",
            "Text=\"{x:Bind ViewModel.SessionProgressPercentLabel, Mode=OneWay}\"");

        // The big percent label is derived from SessionProgressPercent and refreshed when it changes.
        Assert.Contains("public string SessionProgressPercentLabel => $\"{SessionProgressPercent:F0}%\";", MainViewModelSource);
        Assert.Contains("partial void OnSessionProgressPercentChanged(double value) => OnPropertyChanged(nameof(SessionProgressPercentLabel));", MainViewModelSource);

        // The busy flag the overlay binds to is toggled around BOTH save and load by the shared helpers.
        Assert.Contains("[ObservableProperty] public partial bool IsSessionBusy { get; set; }", MainViewModelSource);
        AssertContainsInOrder(MainViewModelSource,
            "private void BeginSessionProgress(string initialText)",
            "IsSessionBusy = true;");
        AssertContainsInOrder(MainViewModelSource,
            "private void EndSessionProgress()",
            "IsSessionBusy = false;");
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

    [Fact]
    public void SearchSortRefresh_SkipsRebuildWhileAFileGroupIsExpanded()
    {
        // A periodic in-search sort refresh goes through ApplySortAndFilter ->
        // VisibleGroups.ReplaceAll -> a Reset that rebuilds every ListView container,
        // which makes an open drawer flicker (collapse + re-expand). The refresh must
        // be skipped while any visible file group is expanded.
        string refreshMethod = ExtractWindow(MainViewModelSource, "private void QueueSearchSortRefreshIfDue()", "private bool AnyResultGroupExpanded()");

        // Gate before queuing: the due refresh bails out when a drawer is expanded.
        AssertContainsInOrder(refreshMethod,
            "now - _lastSearchSortRefreshTicks < intervalTicks",
            "if (AnyResultGroupExpanded())",
            "_lastSearchSortRefreshTicks = now;",
            "return;",
            "_searchSortRefreshQueued = true;");

        // Race guard inside the queued callback (user expands during the delay).
        AssertContainsInOrder(refreshMethod,
            "_searchSortRefreshQueued = false;",
            "if (AnyResultGroupExpanded())",
            "return;",
            "ApplySortAndFilter();");

        // The helper scans visible groups for any expanded drawer.
        string helper = ExtractWindow(MainViewModelSource, "private bool AnyResultGroupExpanded()", "private void NotifyResultAvailabilityChanged()");
        Assert.Contains("_resultCollection.VisibleGroups", helper);
        Assert.Contains("groups[i].IsExpanded", helper);
        Assert.Contains("return true;", helper);
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
            if (File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Yagu.slnx from the test output directory.");
    }
}