using Yagu.Models;
using Yagu.Services;
using System.Diagnostics;
using System.Runtime;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Streaming matches into the UI: batched adds, source-backed (index) adds, memory-pressure
/// eviction before results reach the UI, group initialization and provenance, the throttled
/// sort/filter refresh, and clearing results.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Yields the UI thread to the dispatcher's higher-priority work — pending pointer/scroll input,
    /// layout, and rendering — before resuming, so a long run of buffered search-result batches cannot
    /// starve smooth scrolling of the results list. The Low-priority continuation resumes only after the
    /// pump has drained higher-priority work; when the UI is idle (e.g. a non-interactive full-drive
    /// scan) it resumes almost immediately, so result draining still runs at full speed.
    /// </summary>
    private Task YieldToUiPumpAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
            tcs.TrySetResult();
        return tcs.Task;
    }

    private async Task AddMatchAsync(SearchResult result, CancellationToken cancellationToken)
    {
        if (Degraded && _resultStore is not null && !result.IsEvicted)
            await EvictNewResultsBeforeUiAsync([result], cancellationToken).ConfigureAwait(true);

        bool resultAvailabilityChanged = AddMatchCore(result, evictedResultWriter: null);

        QueueSearchSortRefreshIfDue();

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();
    }

    private async Task AddMatchesAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken)
    {
        if (Degraded && _resultStore is not null && ContainsInMemoryPayload(results))
            await EvictNewResultsBeforeUiAsync(results, cancellationToken).ConfigureAwait(true);

        bool resultAvailabilityChanged = _resultCollection.AddRange(
            results,
            InitializeResultGroup,
            evictNewResults: false,
            resultStore: null);

        QueueSearchSortRefreshIfDue();

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();
    }

    private Task AddSourceBackedMatchesAsync(IReadOnlyList<SourceBackedMatch> results, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool resultAvailabilityChanged = _resultCollection.AddSourceBackedRange(
            results,
            InitializeResultGroup);

        QueueSearchSortRefreshIfDue();

        if (resultAvailabilityChanged)
            NotifyResultAvailabilityChanged();

        return Task.CompletedTask;
    }

    private static bool ContainsInMemoryPayload(IReadOnlyList<SearchResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (!results[i].IsEvicted)
                return true;
        }

        return false;
    }

    private async Task EvictNewResultsBeforeUiAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken)
    {
        if (_resultStore is null || results.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        int evicted = await Task.Run(() => _resultStore.EvictManyNow(results), cancellationToken).ConfigureAwait(true);
        sw.Stop();
        if (sw.ElapsedMilliseconds >= 500)
        {
            YaguLog.For("ViewModel").LogWarning(
                "Pre-evicted {Evicted:N0}/{Total:N0} new result payload(s) before UI insertion in {ElapsedMs}ms",
                evicted, results.Count, sw.ElapsedMilliseconds);
        }
    }

    private bool AddMatchCore(
        SearchResult result,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, long>? evictedResultWriter)
    {
        // FilePath comes from FileLister and is already a full path on Windows.
        // Avoiding Path.GetFullPath here removes a per-match string allocation +
        // PInvoke that was running on the UI dispatcher.
        var path = result.FilePath;
        bool watched = Yagu.Services.FileWatchDiagnostics.IsWatched(path);
        if (watched)
            Yagu.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-ENTER", -1, $"line={result.LineNumber} groups={_resultCollection.AllGroups.Count}");

        bool resultAvailabilityChanged = _resultCollection.Add(
            result,
            InitializeResultGroup,
            evictNewResult: Degraded && evictedResultWriter is not null,
            evictedResultWriter);

        if (watched)
            Yagu.Services.FileWatchDiagnostics.Checkpoint(path, "UI-ADDMATCH-EXIT", -1, $"groupCount={_resultCollection.AllGroups.Count} visibleGroups={ResultGroups.Count}");
        // MatchesFound is updated via throttled Progress / Completed events to avoid
        // pumping a PropertyChanged for every single result on huge searches.
        return resultAvailabilityChanged;
    }

    private void InitializeResultGroup(FileGroup group)
    {
        // Tag the file's content-index candidacy provenance for the results-list badge (plan §6.2), if the
        // index participated in this search. Read-only + fast (a dict lookup per captured gate); safe on
        // the UI thread concurrently with the discovery loop.
        TrySetIndexProvenance(group);

        // Load metadata on a worker thread — the FileInfo syscall on the UI
        // dispatcher was a measurable stall on searches with thousands of
        // distinct files.
        group.BeginLoadMetadata(action => _dispatcher.TryEnqueue(() => action()), OnResultGroupMetadataLoaded, _metadataCts.Token);
    }

    /// <summary>
    /// Sets <see cref="FileGroup.Provenance"/> from the captured per-root pruning gates (plan §6.2). Only
    /// runs when the master feature and the provenance setting are on and at least one gate accelerated
    /// this search; a file the index selected as a candidate is tagged index-accelerated, everything else
    /// live-scanned. Never throws — a classification failure just leaves the group unbadged.
    /// </summary>
    private void TrySetIndexProvenance(FileGroup group)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexProvenanceInResults)
            return;

        Yagu.Services.Index.ContentIndexSearchGate[] gates;
        Yagu.Services.Index.IContentIndexPruningScan[] pruningScans;
        lock (_indexGatesLock)
        {
            if (_activeIndexGates.Count == 0 && _activePruningScans.Count == 0)
                return;
            gates = _activeIndexGates.ToArray();
            pruningScans = _activePruningScans.ToArray();
        }

        try
        {
            string normalized = Yagu.Services.Index.IndexScopeIdentity.NormalizePath(group.FilePath);
            var provenance = Yagu.Services.Index.IndexProvenanceKind.LiveScanned;
            foreach (var gate in gates)
            {
                if (gate.ClassifyProvenance(normalized) == Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated)
                {
                    provenance = Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated;
                    break;
                }
            }
            // Stage-5 worker pruning path: a file the worker classified as an index member is badged too.
            if (provenance != Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated)
            {
                foreach (var scan in pruningScans)
                {
                    if (scan.WasIndexMember(normalized))
                    {
                        provenance = Yagu.Services.Index.IndexProvenanceKind.IndexAccelerated;
                        break;
                    }
                }
            }
            group.Provenance = provenance;
        }
        catch
        {
            // Provenance is a cosmetic hint — never let a classification error affect results.
        }
    }

    private void OnResultGroupMetadataLoaded(FileGroup group)
    {
        if (!IsMetadataSensitiveView)
            return;

        if (_metadataSortFilterRefreshQueued)
            return;

        _metadataSortFilterRefreshQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _metadataSortFilterRefreshQueued = false;
            ApplySortAndFilter();
        });
    }

    private bool IsMetadataSensitiveView =>
        DateRangeFilter != DateRangeFilter.None
        || GroupMode is GroupMode.DateRangeModified or GroupMode.DateRangeCreated or GroupMode.DateRangeModifiedCreated
        || GroupMode == GroupMode.FileSize
        || SortModeIndex is 2 or 3;

    private void QueueSearchSortRefreshIfDue()
    {
        int groupCount = _resultCollection.AllGroups.Count;
        if (!IsSearching || _searchSortRefreshQueued || groupCount < 2)
            return;

        long now = Stopwatch.GetTimestamp();
        long intervalTicks = (long)(Stopwatch.Frequency * _searchSortRefreshIntervalSec);

        if (Degraded && groupCount >= SearchSortRefreshDegradedDeferGroupThreshold)
        {
            _searchSortRefreshIntervalSec = SearchSortRefreshIntervalMaxSec;
            if (_lastSearchSortRefreshTicks == 0 || now - _lastSearchSortRefreshTicks >= intervalTicks)
            {
                _lastSearchSortRefreshTicks = now;
                YaguLog.For("ViewModel").LogDebug(
                    "Deferring periodic in-search sort refresh for degraded large result set: {Groups:N0} group(s); final refresh will run on completion",
                    groupCount);
            }

            return;
        }

        if (_lastSearchSortRefreshTicks != 0 && now - _lastSearchSortRefreshTicks < intervalTicks)
            return;

        // Don't reorder/rebuild the results list while the user has a file group
        // expanded. The periodic refresh goes through ApplySortAndFilter ->
        // VisibleGroups.ReplaceAll -> a Reset that tears down and re-creates every
        // ListView container, which makes the open drawer visibly collapse and
        // re-expand (flicker) and loses the user's scroll position. The final
        // ApplySortAndFilter on search completion still sorts everything.
        if (AnyResultGroupExpanded())
        {
            // Defer the next check by one interval so we don't rescan every batch.
            _lastSearchSortRefreshTicks = now;
            return;
        }

        _searchSortRefreshQueued = true;
        _lastSearchSortRefreshTicks = now;

        if (!_dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            _searchSortRefreshQueued = false;
            int currentGroupCount = _resultCollection.AllGroups.Count;
            if (!IsSearching || currentGroupCount < 2)
                return;

            // The user may have expanded a drawer between queueing and execution;
            // skip the rebuild so the open drawer doesn't flicker.
            if (AnyResultGroupExpanded())
                return;

            var sw = Stopwatch.StartNew();
            try
            {
                ApplySortAndFilter();
            }
            catch (Exception ex)
            {
                YaguLog.For("ViewModel").LogWarning("Periodic in-search sort refresh threw: {ExceptionType}: {Error}", ex.GetType().Name, ex.Message);
                return;
            }
            sw.Stop();
            YaguLog.For("ViewModel").LogDebug(
                "Periodic in-search sort refresh: {Groups:N0} group(s) in {ElapsedMs}ms (degraded={Degraded}, nextInterval={NextIntervalSec:F1}s)",
                currentGroupCount, sw.ElapsedMilliseconds, Degraded, _searchSortRefreshIntervalSec);

            // Adaptive backoff: if the pass was slow, double the interval (capped); if fast, halve it back toward base.
            if (sw.ElapsedMilliseconds >= SearchSortRefreshSlowBudgetMs)
            {
                _searchSortRefreshIntervalSec = Math.Min(SearchSortRefreshIntervalMaxSec, _searchSortRefreshIntervalSec * 2.0);
            }
            else if (sw.ElapsedMilliseconds < SearchSortRefreshSlowBudgetMs / 2 && _searchSortRefreshIntervalSec > SearchSortRefreshIntervalBaseSec)
            {
                _searchSortRefreshIntervalSec = Math.Max(SearchSortRefreshIntervalBaseSec, _searchSortRefreshIntervalSec / 2.0);
            }
        }))
        {
            _searchSortRefreshQueued = false;
        }
    }

    /// <summary>
    /// True if any visible file group is currently expanded. Used to suppress the
    /// periodic in-search sort refresh, whose ReplaceAll/Reset would otherwise tear
    /// down and re-create the open drawer's container (visible flicker).
    /// </summary>
    private bool AnyResultGroupExpanded()
    {
        var groups = _resultCollection.VisibleGroups;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].IsExpanded)
                return true;
        }

        return false;
    }

    private void NotifyResultAvailabilityChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Evict all in-memory results to the disk-backed store to free memory.</summary>
    /// <returns>The number of results actually evicted.</returns>
    private int EvictAllResults()
    {
        int evicted = _resultCollection.EvictAll(_resultStore);
        YaguLog.For("ViewModel").LogInformation("Evicted {Evicted:N0} results to disk ({TotalOnDisk:N0} total on disk)", evicted, _resultStore?.EvictedCount ?? 0);
        // GC is now triggered by the worker threads after the eviction signal,
        // keeping the UI thread responsive.
        return evicted;
    }

    private static void CollectPostEvictionIfDue()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastPostEvictionCompactingGcTicks);
        if (last != 0)
        {
            double secondsSinceLast = (double)(now - last) / Stopwatch.Frequency;
            if (secondsSinceLast < PostEvictionCompactingGcCooldown.TotalSeconds)
                return;
        }

        if (Interlocked.CompareExchange(ref s_postEvictionCompactingGcInFlight, 1, 0) != 0)
            return;

        var gcStopwatch = Stopwatch.StartNew();
        try
        {
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            YaguLog.For("ViewModel").LogWarning(ex, "Post-eviction compacting GC failed");
        }
        finally
        {
            gcStopwatch.Stop();
            Volatile.Write(ref s_lastPostEvictionCompactingGcTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref s_postEvictionCompactingGcInFlight, 0);

            if (gcStopwatch.ElapsedMilliseconds >= 500)
                YaguLog.For("ViewModel").LogWarning("Post-eviction compacting GC took {ElapsedMs:N0}ms", gcStopwatch.ElapsedMilliseconds);
            else
                YaguLog.For("ViewModel").LogInformation("Post-eviction compacting GC took {ElapsedMs:N0}ms", gcStopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Clear all search results, dispose the disk-backed temp store,
    /// and perform a compacting GC.
    /// </summary>
    public async Task ClearResultsAsync()
    {
        if (IsSearching)
            await CancelAsync();

        _resultCollection.Clear();
        FileMetadataCache.Clear();

        var oldStore = _resultStore;
        _resultStore = null;

        MatchesFound = 0;
        FilesScanned = 0;
        TotalFiles = 0;
        ResetDisplayedSearchProgress();
        FilesSkipped = 0;
        HasPerformedSearch = false;
        AccessDeniedCount = 0;
        ErrorText = null;
        FallbackReason = null;
        Truncated = false;
        Degraded = false;
        DegradedNoticeText = string.Empty;
        FilesPerSecondText = string.Empty;
        StatusText = string.Empty;
        ThroughputSamples.Clear();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));

        // Dispose the old store (deletes temp file) and GC on the threadpool
        // so the UI stays responsive.
        await Task.Run(() =>
        {
            oldStore?.Dispose();

            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }).ConfigureAwait(true);
    }
}
