using Yagu.Models;
using Yagu.Services;
using System.Collections;

namespace Yagu.ViewModels;

/// <summary>
/// .yagu-session save/load — round-trips the visible result graph to disk without re-running the
/// search, hydrating and re-evicting one group at a time so payloads are never all held in memory.
/// </summary>
public sealed partial class MainViewModel
{
    // -----------------------------------------------------------------------
    // .yagu-session save/load — round-trips the visible result graph to disk
    // without re-running the search.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Save the current results plus search query / stats to a <c>.yagu-session</c>
    /// file. Evicted results are hydrated one group at a time and re-evicted after
    /// writing to avoid holding all payloads in memory simultaneously.
    /// </summary>
    public async Task<int> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        BeginSessionProgress($"Preparing to save {Path.GetFileName(path)}…");
        try
        {
            // Snapshot the group list so we can iterate without UI-thread mutation interference.
            var groupsSnapshot = _resultCollection.AllGroups.ToArray();
            int totalGroups = groupsSnapshot.Length;

            // Pre-count total results (materializing evicted stubs so Count is accurate)
            // without hydrating payloads — this is cheap (just expands compact stub pages).
            int totalResults = 0;
            for (int gi = 0; gi < totalGroups; gi++)
            {
                groupsSnapshot[gi].MaterializeEvictedStubs();
                totalResults += groupsSnapshot[gi].Count;
            }

            ReportSessionProgress(0.05, $"Writing {totalResults:N0} match(es) to {Path.GetFileName(path)} (streaming)…");

            var stats = new SessionFileService.SessionStats(
                _searchStartedUtc,
                _lastSearchElapsed,
                FilesScanned,
                _bytesScanned,
                MatchesFound);

            await using var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            var store = _resultStore;

            await SessionFileService.WriteStreamingAsync(
                fs,
                Query ?? string.Empty,
                Directory ?? string.Empty,
                stats,
                totalResults,
                totalGroups,
                prepareGroup: gi =>
                {
                    var g = groupsSnapshot[gi];
                    int count = g.Count;
                    // Hydrate evicted results for this group so WriteResult sees full payloads.
                    if (store is not null)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var r = g[i];
                            if (r.IsEvicted)
                                HydrateResult(r);
                        }
                    }
                    // Return a lightweight wrapper that indexes into the group directly.
                    return new FileGroupResultList(g);
                },
                releaseGroup: gi =>
                {
                    // Re-evict the group's results back to disk so memory is freed
                    // before we hydrate the next group.
                    if (store is null) return;
                    var g = groupsSnapshot[gi];
                    int count = g.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var r = g[i];
                        if (!r.IsEvicted)
                            r.Evict(store);
                    }
                },
                progress: new Progress<double>(p =>
                    ReportSessionProgress(0.05 + 0.95 * p,
                        $"Writing session: {p * 100:N0}% ({totalResults:N0} match(es))")),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var savedStatus = $"Saved session: {totalResults:N0} match(es) → {Path.GetFileName(path)}";
            if (!_dispatcher.TryEnqueue(() => StatusText = savedStatus))
                StatusText = savedStatus;
            return totalResults;
        }
        finally
        {
            EndSessionProgress();
        }
    }

    /// <summary>
    /// Lightweight <see cref="IReadOnlyList{SearchResult}"/> wrapper around a
    /// <see cref="FileGroup"/> so we don't allocate a copy of its items array
    /// just to pass it to the streaming writer.
    /// </summary>
    private sealed class FileGroupResultList(FileGroup group) : IReadOnlyList<SearchResult>
    {
        public SearchResult this[int index] => group[index];
        public int Count => group.Count;
        public IEnumerator<SearchResult> GetEnumerator()
        {
            for (int i = 0; i < group.Count; i++)
                yield return group[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Load a <c>.yagu-session</c> file into the result list. Cancels any
    /// in-progress search, clears existing state, then streams results into
    /// the collection in batches so very large sessions don't block the UI.
    /// </summary>
    public async Task<SessionFileService.SessionHeader> LoadSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsSearching)
            await CancelAsync().ConfigureAwait(true);

        BeginSessionProgress($"Opening {Path.GetFileName(path)}…");
        try
        {
            _resultCollection.Clear();
            FileMetadataCache.Clear();
            _resultStore?.Dispose();
            _resultStore = null;

            ErrorText = null;
            FallbackReason = null;
            FilesScanned = 0;
            TotalFiles = 0;
            MatchesFound = 0;
            FilesSkipped = 0;
            HasPerformedSearch = false;
            AccessDeniedCount = 0;
            Truncated = false;
            Degraded = false;
            DegradedNoticeText = string.Empty;
            FilesPerSecondText = string.Empty;
            ThroughputSamples.Clear();

            bool firstBatch = true;
            int loadedCount = 0;
            string fileName = Path.GetFileName(path);

            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);

            var readProgress = new Progress<double>(p =>
                ReportSessionProgress(p, $"Loading {fileName}: {p * 100:N0}%"));

            var header = await SessionFileService.ReadAsync(
                fs,
                h =>
                {
                    void apply()
                    {
                        Query = h.Query ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(h.SearchRoot))
                            Directory = h.SearchRoot;
                        _searchStartedUtc = h.Stats.StartedUtc;
                        _lastSearchElapsed = h.Stats.Elapsed;
                        FilesScanned = h.Stats.FilesScanned;
                        _bytesScanned = h.Stats.BytesScanned;
                    }
                    if (!_dispatcher.TryEnqueue(apply))
                        apply();
                },
                async batch =>
                {
                    // Hop to UI thread for the collection mutation.
                    var tcs = new TaskCompletionSource();
                    bool enqueued = _dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            bool resultAvailabilityChanged = _resultCollection.AddRange(
                                batch,
                                InitializeResultGroup,
                                evictNewResults: false,
                                resultStore: null);

                            loadedCount += batch.Count;
                            MatchesFound = loadedCount;

                            if (firstBatch || resultAvailabilityChanged)
                            {
                                firstBatch = false;
                                NotifyResultAvailabilityChanged();
                            }
                        }
                        finally
                        {
                            tcs.SetResult();
                        }
                    });

                    if (!enqueued)
                    {
                        // Dispatcher unavailable (e.g. tests without a UI thread) —
                        // fall back to a direct call.
                        _resultCollection.AddRange(batch, InitializeResultGroup, evictNewResults: false, resultStore: null);
                        loadedCount += batch.Count;
                        MatchesFound = loadedCount;
                        return;
                    }

                    await tcs.Task.ConfigureAwait(false);
                },
                readProgress,
                cancellationToken).ConfigureAwait(false);

            void finish()
            {
                int actualFileCount = _resultCollection.AllGroups.Count;
                var displaySummary = new SearchSummary(
                    TotalFiles: header.Stats.FilesScanned,
                    FilesScanned: header.Stats.FilesScanned,
                    FilesSkipped: 0,
                    FilesWithMatches: actualFileCount,
                    TotalMatches: loadedCount,
                    BytesScanned: header.Stats.BytesScanned,
                    Elapsed: header.Stats.Elapsed,
                    Cancelled: false,
                    Truncated: false,
                    Degraded: false,
                    FallbackReason: null);
                StatusText = BuildCompletionStatus(displaySummary, header.Stats.Elapsed);
                ApplySortAndFilter();
                NotifyResultAvailabilityChanged();
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
            if (!_dispatcher.TryEnqueue(finish))
                finish();

            return header;
        }
        finally
        {
            EndSessionProgress();
        }
    }

    private void BeginSessionProgress(string initialText)
    {
        void apply()
        {
            IsSessionBusy = true;
            SessionProgressPercent = 0;
            SessionProgressText = initialText;
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }

    private void ReportSessionProgress(double fraction, string text)
    {
        double pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        void apply()
        {
            SessionProgressPercent = pct;
            SessionProgressText = text;
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }

    private void EndSessionProgress()
    {
        void apply()
        {
            IsSessionBusy = false;
            SessionProgressPercent = 0;
            SessionProgressText = string.Empty;
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }
}
