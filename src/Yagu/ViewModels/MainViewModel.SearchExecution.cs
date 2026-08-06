using CommunityToolkit.Mvvm.Input;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Ocr;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Running a search: building the per-root options, starting and cancelling the run, resetting
/// state between runs, creating the result store, the single-file-path fast path, and the low
/// disk-space monitor.
/// </summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    public async Task StartSearchAsync()
    {
        if (_shutdownRequested)
            return;

        // A complete file path typed into the Traditional search box (and nothing else) is a request
        // to show exactly that file, regardless of the Directory box. Detect and short-circuit here,
        // before any directory validation, so the Directory box never affects this lookup.
        if (!IsSemanticQueryMode && Yagu.Helpers.SingleFilePathQueryDetector.Resolve(Query) is { } singleFilePath)
        {
            await RunSingleFilePathDisplayAsync(singleFilePath).ConfigureAwait(true);
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        string normalizedDirectory = DriveEnumerator.NormalizeSearchRoot(Directory);
        bool directorySpecified = normalizedDirectory.Length > 0;
        if (directorySpecified && !string.Equals(Directory, normalizedDirectory, StringComparison.Ordinal))
            Directory = normalizedDirectory;
        if (directorySpecified && !System.IO.Directory.Exists(normalizedDirectory))
        {
            ErrorText = $"Directory does not exist: {normalizedDirectory}";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }
        // An empty directory means "search all drives" — resolve the eligible roots now.
        var targetRoots = ResolveTargetRoots();
        if (targetRoots.Count == 0)
        {
            ErrorText = "No drives are available to search.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }
        if (string.IsNullOrEmpty(Query))
        {
            ErrorText = "Enter a search query.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        // Validate: skip extensions must not contradict archive extensions when archive search is on.
        if (SearchInsideArchives)
        {
            var skipSet = BuildEffectiveSkipExtensionSet();
            var archiveSet = ParseExtensionSet(ArchiveExtensions);
            var conflicts = skipSet.Intersect(archiveSet, StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            if (conflicts.Count > 0)
            {
                ErrorText = $"Conflicting extensions found in both Skip and Archive lists: {string.Join(", ", conflicts.Select(e => $".{e}"))}. " +
                            "Remove them from the Skip list or the Archive list to proceed.";
                ResumeContentIndexWarmupAfterSearch();
                return;
            }
        }

        long effectiveMinFileSizeBytes = MinFileSizeBytes;
        long effectiveMaxFileSizeBytes = MaxFileSizeBytes;
        if (effectiveMinFileSizeBytes > 0 && effectiveMaxFileSizeBytes > 0 && effectiveMinFileSizeBytes > effectiveMaxFileSizeBytes)
        {
            ErrorText = "Minimum file size cannot be larger than maximum file size.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        if (IsDateRangeInvalid(CreatedAfterDate, CreatedBeforeDate))
        {
            ErrorText = "Created after date cannot be later than created before date.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        if (IsDateRangeInvalid(ModifiedAfterDate, ModifiedBeforeDate))
        {
            ErrorText = "Modified after date cannot be later than modified before date.";
            ResumeContentIndexWarmupAfterSearch();
            return;
        }

        int runId = Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);
        ResetRuntimeIndexStatus(runId);

        // Fire-and-forget: refresh the main-window content-index availability indicator for the roots
        // this search covers (plan §6.2). Presence-only, runs off the UI thread, and never blocks or
        // delays the search — filename-first results are unaffected.
        _ = RefreshIndexStatusAsync(targetRoots, UseContentIndex && _settings.EnableContentIndex);

        await _searchLifecycleGate.WaitAsync();

        CancellationTokenSource? cts = null;
        Task? lowDiskMonitorTask = null;
        try
        {
            if (_shutdownRequested || runId != Volatile.Read(ref _searchRunId))
                return;

            ResetStateForNewSearch();

            if (directorySpecified)
                SettingsService.PushRecent(_settings.RecentDirectories, _settings.RecentDirectoryTimes, Directory, MaxRecentItems);
            // In Semantic mode the user-typed natural-language query (captured before translation)
            // goes to the separate Semantic history; Traditional searches use the literal Query.
            if (IsSemanticQueryMode)
            {
                if (!string.IsNullOrWhiteSpace(_pendingSemanticHistoryEntry))
                    SettingsService.PushRecent(_settings.SemanticSearchHistory, _settings.SemanticSearchHistoryTimes, _pendingSemanticHistoryEntry!, MaxSemanticRecentItems);
            }
            else
            {
                SettingsService.PushRecent(_settings.SearchHistory, _settings.SearchHistoryTimes, Query, MaxRecentItems);
            }
            _pendingSemanticHistoryEntry = null;
            SyncRecent();

            var effectiveSkipExtensions = BuildEffectiveSkipExtensionSet();

            int baseParallelism = ResolveParallelism(ParallelismIndex);
            // One-shot HDD parallelism override chosen in the warning dialog; applies to this search
            // only. Consume it now so it never leaks into a later search.
            int? hddParallelismOverride = _hddParallelismOverrideIndexForNextSearch;
            _hddParallelismOverrideIndexForNextSearch = null;
            SearchOptions BuildOptionsForRoot(string dir, int parallelism, FileListerBackend? backendOverride, bool isHardDisk) => new SearchOptions
            {
                Directory = dir,
                Query = Query,
                CaseSensitive = CaseSensitive,
                UseRegex = UseRegex,
                ExactMatch = ExactMatch,
                Multiline = Multiline,
                MultilineDotAll = MultilineDotAll,
                MultilineEngine = (MultilineEngineKind)_settings.MultilineEngine,
                ContextLines = ContextLines,
                SearchMode = (SearchMode)SearchModeIndex,
                IncludeGlobs = SplitFilterPatterns(IncludeGlobs, IncludeFilterMode),
                ExcludeGlobs = SplitFilterPatterns(EffectiveExcludeGlobsText, ExcludeFilterMode),
                IncludeFilterMode = IncludeFilterMode,
                ExcludeFilterMode = ExcludeFilterMode,
                MinFileSizeBytes = effectiveMinFileSizeBytes,
                MaxFileSizeBytes = effectiveMaxFileSizeBytes,
                CreatedAfterDate = CreatedAfterDate,
                CreatedBeforeDate = CreatedBeforeDate,
                ModifiedAfterDate = ModifiedAfterDate,
                ModifiedBeforeDate = ModifiedBeforeDate,
                MaxResults = MaxResults,
                MaxMatchesPerLine = MaxMatchesPerLine,
                FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(FileIoTimeoutSeconds),
                AbsoluteMaxResults = AbsoluteMaxResults,
                SkipBinary = SkipBinary,
                AvoidSourceMemoryMap = DriveEnumerator.ShouldAvoidSourceMemoryMap(
                    DriveEnumerator.GetDriveTypeForPath(dir)),
                SearchOnlineOnlyFiles = SearchOnlineOnlyFiles,
                SearchHiddenFiles = SearchHiddenFiles,
                ObeyGitignore = ObeyGitignore,
                GitignoreTakesPrecedence = GitignoreTakesPrecedence,
                SkipExtensions = effectiveSkipExtensions,
                SearchInsideArchives = SearchInsideArchives,
                ArchiveExtensions = ParseDottedExtensionSet(ArchiveExtensions),
                SearchImageText = SearchImageText,
                ImageOcrExtensions = ParseExtensionSet(AppSettings.DefaultImageOcrExtensions),
                ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),
                ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel),
                ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide),
                ImageOcrWorkerParallelism = OcrWorkerParallelism.Resolve(
                    ImageOcrWorkerParallelism,
                    AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),
                    Environment.ProcessorCount,
                    LimitParallelismOnHdd,
                    isHardDisk),
                SearchPdfText = SearchPdfText,
                PdfTextExtensions = ParseExtensionSet(AppSettings.DefaultPdfTextExtensions),
                MaxDegreeOfParallelism = parallelism,
                FileListerBackendOverride = backendOverride,
                IoOversubscriptionIndex = IoOversubscriptionIndex,
                MaxProcessMemoryBytes = MemoryLimitMB > 0 ? (long)MemoryLimitMB * 1024 * 1024 : 0,
                MemoryPressurePercent = MemoryPressurePercent,
                SdkChannelBufferSize = SdkChannelBufferSize,
                ExcludeAdminProtectedPaths = ExcludeAdminProtectedPaths,
                MaxSearchDepth = double.IsNaN(MaxSearchDepth) ? 0 : (int)MaxSearchDepth,
                DegradedResultStore = _resultStore,
                // Session-only content-index opt-in, gated by the master feature (plan §5/§6.1). Only
                // prunes the ordinary-text candidate set; orthogonal to the image/PDF/archive toggles.
                UseContentIndex = UseContentIndex && _settings.EnableContentIndex,
            };

            // Attaches the content-index pruning gate factory to a per-root options set (plan §5). The
            // factory is a closure invoked later, off the UI thread, at the start of that root's discovery,
            // so no index/journal I/O runs here. A null factory (feature off) leaves the live-scan path
            // untouched.
            void AttachContentIndexGateFactory(SearchOptions rootOptions, string root)
            {
                if (!rootOptions.UseContentIndex)
                    return;

                AppSettings settings = _settings;
                string storageDir = settings.IndexStorageDirectory;
                int retained = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount);
                // Opt-in: route the query through the isolated out-of-process worker (identical results, but a
                // native/read fault is contained in the worker). Falls back in-process on any worker failure.
                Yagu.Services.Index.IIndexCandidateSource? candidateSource =
                    settings.IndexUseNativeWorker ? GetOrCreateIndexWorkerSource() : null;
                int maxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(settings.IndexMaxInProcessSizeMB);
                int maxWorkerQuerySizeMB = AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(settings.IndexMaxWorkerQuerySizeMB);
                string ResolveIndexRoot(IContentIndexPathProvider pathProvider)
                    => new ContentIndexManager(pathProvider, retained)
                        .ResolveBestAvailableIndexRoot(root, settings.IndexedRoots);
                rootOptions.ContentIndexGateFactory = () =>
                {
                    // Stage-5 (plan §5.8): when the worker PRUNING path is enabled it supersedes the
                    // in-process gate — never open the index in-process (the worker path's whole purpose is a
                    // bounded host footprint, so a large scope is served by the worker or live-scanned).
                    if (settings.IndexUseWorkerQuerySessions)
                        return null;
                    var pathProvider = DefaultContentIndexPathProvider.Create(storageDir);
                    string indexRoot = ResolveIndexRoot(pathProvider);
                    // Size gate (plan §6.1): an index whose on-disk size exceeds the in-process limit is NEVER
                    // loaded into memory. Deserializing a multi-GB layered index leaves a multi-GB resident
                    // footprint that trips the search memory monitor into degraded mode, making the search
                    // SLOWER than a plain live scan — so such a scope always live-scans and is never warmed.
                    if (!ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(pathProvider, indexRoot, retained, maxInProcessSizeMB))
                    {
                        long activeBytes = new ContentIndexStore(
                            pathProvider,
                            ContentIndexManager.ScopeIdForRoot(indexRoot),
                            retained).GetCurrentLayeredIndexSizeBytes();
                        string reason = activeBytes <= 0
                            ? "no trusted index is available"
                            : $"active index size {ResourceUsageMonitor.FormatBytes(activeBytes)} exceeds the configured {ResourceUsageMonitor.FormatBytes((long)maxInProcessSizeMB * 1024 * 1024)} in-process limit; enable memory-mapped worker query sessions with format-v3 data to serve this large index";
                        ReportContentIndexAttempt(runId, root, false, reason);
                        return null;
                    }
                    // Don't block the first result on a COLD index open. A large layered index (a multi-GB
                    // base + delta segments) can take tens of seconds to deserialize — far slower than simply
                    // live-scanning — so if it isn't already warm (deserialized in the query-mode cache) for
                    // this scope, live-scan THIS search and warm the index in the background so the NEXT search
                    // is index-accelerated.
                    if (!ContentIndexSearchGate.IsScopeWarm(pathProvider, indexRoot, retained))
                    {
                        StartContentIndexWarmup(indexRoot);
                        return null;
                    }
                    var gate = ContentIndexSearchGate.TryCreate(
                        pathProvider,
                        indexRoot,
                        rootOptions,
                        settings,
                        retained,
                        journalReader: null,
                        candidateSource: candidateSource,
                        onAttempt: (active, reason) =>
                            ReportContentIndexAttempt(runId, root, active, reason));
                    // Capture the live gate so InitializeResultGroup can classify per-file provenance.
                    if (gate is not null)
                        lock (_indexGatesLock)
                            _activeIndexGates.Add(gate);
                    return gate;
                };

                // Stage-5 worker PRUNING path (plan §5.8): when the user-selectable mapped-worker setting
                // is on, prune this root via the isolated worker over its memory-mapped v3 WITHOUT loading the
                // index into the host — so a large scope over IndexMaxInProcessSizeMB is served with a bounded
                // host footprint (the in-process gate above returns null when this is on — mutually exclusive).
                // The factory takes the search's survivor sink (its pending-file writer); it forwards survivors
                // and prunes proven-nonmembers, rescuing the dirty subset at B1. Returns null → live-scan when
                // the worker cannot serve the scope (never a large in-process deserialize). Reuses the single
                // long-lived worker client.
                if (settings.IndexUseWorkerQuerySessions)
                {
                    Yagu.Services.Index.IndexWorkerClient pruningClient = GetOrCreateIndexWorkerClient();
                    var workerPathProvider = DefaultContentIndexPathProvider.Create(storageDir);
                    string spoolDir = Yagu.Services.Index.ContentIndexRecoverySpool.ResolveDirectory(workerPathProvider);
                    int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(settings.IndexMaxJournalCatchupRecords);
                    int queryWorkerParallelism = Yagu.Services.Index.IndexWorkerParallelism.ResolveQueryDegree(
                        settings.IndexQueryWorkerParallelism,
                        Environment.ProcessorCount,
                        settings.LimitParallelismOnHdd,
                        Yagu.Helpers.DiskTypeDetector.IsHardDisk(root));
                    rootOptions.ContentIndexPruningScanFactory = survivorSink =>
                    {
                        // Out-of-process size cap (IndexMaxWorkerQuerySizeMB, default 30 GB): the worker MAPS
                        // rather than deserializes the index, so it serves far larger scopes than the in-process
                        // cap — but is still bounded. An index over this size (or none) live-scans instead.
                        string indexRoot = ResolveIndexRoot(workerPathProvider);
                        var store = new Yagu.Services.Index.ContentIndexStore(
                            workerPathProvider,
                            Yagu.Services.Index.ContentIndexManager.ScopeIdForRoot(indexRoot),
                            retained);
                        if (!ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(workerPathProvider, indexRoot, retained, maxWorkerQuerySizeMB))
                        {
                            long mappedBytes = store.GetCurrentLayeredMappedQuerySizeBytes();
                            string reason = mappedBytes <= 0
                                ? "no trusted format-v3 query index is available"
                                : $"mapped query index size {ResourceUsageMonitor.FormatBytes(mappedBytes)} exceeds the configured {ResourceUsageMonitor.FormatBytes((long)maxWorkerQuerySizeMB * 1024 * 1024)} worker limit";
                            ReportContentIndexAttempt(runId, root, false, reason);
                            return null;
                        }
                        var scan = Yagu.Services.Index.ContentIndexShadowScopeBuilder.TryCreatePruningScan(
                            pruningClient,
                            store,
                            rootOptions,
                            System.Threading.Interlocked.Increment(ref _shadowQuerySessionId),
                            Yagu.Services.Index.ContentIndexFreshnessEvaluator.CreateReader(
                                maxCatchupRecords,
                                TimeSpan.FromSeconds(AppSettings.NormalizeFileIoTimeoutSeconds(settings.FileIoTimeoutSeconds))),
                            spoolDir,
                            survivorSink,
                            workerParallelism: queryWorkerParallelism,
                            onAttempt: (active, reason) =>
                                ReportContentIndexAttempt(runId, root, active, reason));
                        // Capture the live scan so InitializeResultGroup can badge index-member result files.
                        if (scan is not null)
                            lock (_indexGatesLock)
                                _activePruningScans.Add(scan);
                        return scan;
                    };
                }

                // Extended-source (PDF-text) pruning (plan §7 Phase 4): skip PDFs whose extracted text cannot
                // contain a match. Off by default; only engages when a determinism-proven PDF namespace was
                // built for this root AND this search extracts PDF text. Fail-safe: null → extract every PDF.
                if ((settings.IndexBuildPdfTextExtendedSource && rootOptions.SearchPdfText)
                    || (settings.IndexBuildImageTextExtendedSource && rootOptions.SearchImageText))
                {
                    rootOptions.ExtendedSourceGateFactory = () =>
                    {
                        var extendedPathProvider = DefaultContentIndexPathProvider.Create(storageDir);
                        string indexRoot = ResolveIndexRoot(extendedPathProvider);
                        return Yagu.Services.Index.ExtendedSourceSearchGate.TryCreate(
                            extendedPathProvider,
                            indexRoot,
                            rootOptions,
                            settings);
                    };
                }
            }

            // One options set per target root. When searching all drives, each root gets its own
            // parallelism: HDD roots are forced to 1 (avoid thrashing) while other drives use the
            // configured value. Backend stays Auto so each root uses the fast Everything index when
            // it covers that drive (including drives the user added manually in Everything's settings)
            // and automatically falls back to the managed walker only for drives Everything does not
            // index — except when "force full scan" is enabled, which walks every drive directly.
            var perRootOptions = new List<SearchOptions>(targetRoots.Count);
            FileListerBackend? allDrivesBackendOverride =
                (!directorySpecified && SearchAllDrivesForceFullScan) ? FileListerBackend.Managed : null;
            // Drop any gates captured by a previous search before this one's factories start populating them.
            lock (_indexGatesLock)
            {
                _activeIndexGates.Clear();
                _activePruningScans.Clear();
            }
            foreach (var root in targetRoots)
            {
                int parallelism = baseParallelism;
                bool isHardDisk = Yagu.Helpers.DiskTypeDetector.IsHardDisk(root);
                if (LimitParallelismOnHdd && isHardDisk)
                    parallelism = hddParallelismOverride is int overrideIndex ? ResolveParallelism(overrideIndex) : 1;
                var rootOptions = BuildOptionsForRoot(root, parallelism, allDrivesBackendOverride, isHardDisk);
                AttachContentIndexGateFactory(rootOptions, root);
                perRootOptions.Add(rootOptions);
            }

            // Capture the parameters THIS search actually ran with, for preview/editor match
            // highlighting (the model's resolved literal pattern + flags).
            LastSearchPattern = Query;
            LastSearchCaseSensitive = CaseSensitive;
            LastSearchUseRegex = UseRegex;
            LastSearchExactMatch = ExactMatch;
            LastSearchMultiline = Multiline;
            LastSearchMultilineDotAll = MultilineDotAll;

            // A semantic plan's resolved settings stay applied to this view-model so they are VISIBLE in
            // Advanced Options (the user wanted to see what the AI search applied). They are NOT written
            // to the saved defaults: while the resolution is visible, PersistSettingsAsync persists the
            // pre-search defaults from the snapshot instead; the next search resets the view-model back
            // to those defaults. (Traditional searches have no snapshot and persist their own values.)
            if (_semanticDefaultsSnapshot is not null)
                _semanticResolutionVisible = true;
            await PersistSettingsAsync();

            cts = new CancellationTokenSource();
            _cts = cts;
            _activeSearchRoots = targetRoots.ToArray();
            var token = cts.Token;
            lowDiskMonitorTask = StartLowDiskSpaceMonitor(runId, cts, _resultStore);
            YaguLog.For("Search").LogWarning("Starting search #{RunId}: query='{Query}', dir='{Dir}', regex={UseRegex}, caseSensitive={CaseSensitive}, mode={SearchModeIndex}", runId, Query, directorySpecified ? Directory : "<all drives: " + targetRoots.Count + ">", UseRegex, CaseSensitive, SearchModeIndex);

            // Yield to the UI message pump periodically so the app stays responsive
            // when the events channel is draining many buffered items synchronously.
            // Without this, the await foreach completes synchronously for thousands of
            // already-buffered items, starving the WinUI message pump and freezing the UI.
            long yieldTimestamp = Stopwatch.GetTimestamp();
            // Yield about twice per frame (not once) so the UI thread gets frequent breathing room to
            // render smooth scrolling of the results list while heavy result batches stream in.
            long yieldIntervalTicks = Stopwatch.Frequency / 120; // ~8ms

            // UI consumer diagnostics
            long uiEventsReceived = 0;
            long uiMatchesReceived = 0;
            long uiYieldCount = 0;
            long uiLastLogTicks = Stopwatch.GetTimestamp();
            long uiLastStatusRefreshTicks = uiLastLogTicks;
            const long UiLogIntervalSec = 10;
            long uiStatusRefreshIntervalTicks = Stopwatch.Frequency / 4;
            var uiEventSw = new Stopwatch();

            void RefreshStatusFromReceivedMatches(bool force = false)
            {
                long statusNow = Stopwatch.GetTimestamp();
                if (!force && statusNow - uiLastStatusRefreshTicks < uiStatusRefreshIntervalTicks)
                    return;

                uiLastStatusRefreshTicks = statusNow;
                int receivedMatches = ClampMatchCount(uiMatchesReceived);
                if (receivedMatches > MatchesFound)
                    MatchesFound = receivedMatches;
                UpdateFilesPerSecond();
            }

            await foreach (var evt in _search.SearchManyAsync(perRootOptions, token).ConfigureAwait(true))
            {
                uiEventsReceived++;
                long now = Stopwatch.GetTimestamp();
                if (now - yieldTimestamp >= yieldIntervalTicks)
                {
                    uiYieldCount++;
                    // Yield to the dispatcher's higher-priority work (pending pointer/scroll input,
                    // layout, and rendering) instead of a fixed Task.Delay, so buffered result batches
                    // can never starve smooth scrolling. Resumes as soon as the pump is idle, so a
                    // non-interactive full-drive scan still drains at full speed.
                    await YieldToUiPumpAsync().ConfigureAwait(true);
                    yieldTimestamp = Stopwatch.GetTimestamp();
                }

                if (!IsCurrentSearch(runId, cts))
                {
                    YaguLog.For("Search").LogWarning("Ignoring stale search #{RunId} event after a newer search started", runId);
                    break;
                }

                // Periodic UI consumer throughput log
                now = Stopwatch.GetTimestamp();
                if ((now - uiLastLogTicks) >= Stopwatch.Frequency * UiLogIntervalSec)
                {
                    uiLastLogTicks = now;
                    YaguLog.For("UIConsumer").LogWarning(
                        "Events received={Events:N0}, matchesReceived={Matches:N0}, " +
                        "groups={Groups:N0}, yields={Yields:N0}, " +
                        "degraded={Degraded}, diskEvicted={DiskEvicted:N0}",
                        uiEventsReceived, uiMatchesReceived, _resultCollection.AllGroups.Count, uiYieldCount, Degraded, _resultStore?.EvictedCount ?? 0);
                }

                switch (evt)
                {
                    case SearchEvent.Fallback f:
                        // "Everything SDK returned no results" is an internal tiered-fallback
                        // diagnostic that is never useful on the main screen: when matches exist it
                        // looks like an error, and when none exist the status already shows 0 matches.
                        // Suppress it; any other fallback reason still surfaces.
                        if (f.Reason is null ||
                            !f.Reason.StartsWith("Everything SDK returned no results", StringComparison.Ordinal))
                            FallbackReason = f.Reason;
                        break;
                    case SearchEvent.DiscoveryComplete d:
                        TotalFiles = d.TotalFiles;
                        SearchInNameFirstPhase = false; // full total known — determinate bar from here
                        StatusText = $"Searching {d.TotalFiles:N0} files…";
                        break;
                    case SearchEvent.Match m:
                        uiMatchesReceived++;
                        await AddMatchAsync(m.Result, token).ConfigureAwait(true);
                        RefreshStatusFromReceivedMatches();
                        break;
                    case SearchEvent.MatchBatch mb:
                        // Drain the whole batch under a single dispatcher tick. AddMatch is
                        // O(1) per result; doing them in a tight loop keeps allocations and
                        // PropertyChanged churn from each ResultGroups.Add to the absolute
                        // minimum. The list itself was produced by the discovery thread —
                        // we own it now and don't need a copy.
                        uiMatchesReceived += mb.Results.Count;
                        uiEventSw.Restart();
                        await AddMatchesAsync(mb.Results, token).ConfigureAwait(true);
                        uiEventSw.Stop();
                        RefreshStatusFromReceivedMatches();
                        if (uiEventSw.ElapsedMilliseconds > 200)
                        {
                            YaguLog.For("UIConsumer").LogWarning(
                                "Slow AddMatches: {Count} results took {ElapsedMs}ms " +
                                "(groups={Groups:N0})",
                                mb.Results.Count, uiEventSw.ElapsedMilliseconds, _resultCollection.AllGroups.Count);
                        }
                        break;
                    case SearchEvent.SourceBackedMatchBatch sb:
                        uiMatchesReceived += sb.Results.Count;
                        uiEventSw.Restart();
                        await AddSourceBackedMatchesAsync(sb.Results, token).ConfigureAwait(true);
                        uiEventSw.Stop();
                        RefreshStatusFromReceivedMatches();
                        if (uiEventSw.ElapsedMilliseconds > 200)
                        {
                            YaguLog.For("UIConsumer").LogWarning(
                                "Slow AddSourceBackedMatches: {Count} results took {ElapsedMs}ms " +
                                "(groups={Groups:N0})",
                                sb.Results.Count, uiEventSw.ElapsedMilliseconds, _resultCollection.AllGroups.Count);
                        }
                        break;
                    case SearchEvent.Progress p:
                        FilesScanned = p.Snapshot.FilesScanned;
                        TotalFiles = p.Snapshot.TotalFiles;
                        // Latch out of the indeterminate name-first phase once the full-scan total is live.
                        if (SearchInNameFirstPhase && !p.Snapshot.NameFirstPhase)
                            SearchInNameFirstPhase = false;
                        UpdateSearchProgressPhaseLabel(p.Snapshot);
                        MatchesFound = Math.Max(p.Snapshot.MatchesFound, ClampMatchCount(uiMatchesReceived));
                        FilesSkipped = p.Snapshot.FilesSkipped;
                        AccessDeniedCount = p.Snapshot.AccessDenied;
                        _bytesScanned = p.Snapshot.BytesScanned;
                        UpdateSkipBreakdown(p.Snapshot.SkipReasons);
                        UpdateFilesPerSecond();
                        break;
                    case SearchEvent.SearchError e:
                        ErrorText = e.Message;
                        break;
                    case SearchEvent.MemoryPressure mp:
                        DegradedNoticeText = "Memory pressure — paging results to disk";
                        Degraded = true;
                        YaguLog.For("ViewModel").LogWarning("Memory pressure event received — starting async eviction ({Groups:N0} groups, {Matches:N0} matches)", _resultCollection.AllGroups.Count, MatchesFound);
                        // Fire-and-forget from the UI thread: the background task may wait
                        // for ResultStore queue space so existing payloads do not pile up
                        // in RAM while the disk writer catches up.
                        _ = Task.Run(() =>
                        {
                            var evictSw = Stopwatch.StartNew();
                            int enqueued = EvictAllResults();
                            evictSw.Stop();
                            YaguLog.For("ViewModel").LogWarning("Eviction enqueued {Enqueued:N0} results in {ElapsedMs}ms (drain continues in background)", enqueued, evictSw.ElapsedMilliseconds);

                            // Acknowledge immediately so SearchService leaves eviction-in-flight
                            // state and can fire the next pressure cycle if memory is still high.
                            try { mp.AcknowledgeEviction(enqueued); }
                            catch (Exception ex) { YaguLog.For("ViewModel").LogWarning(ex, "AcknowledgeEviction threw"); }

                            // Wait for the background drain to flush bytes to disk before
                            // triggering the compacting GC — otherwise we'd compact while
                            // the match-line/context strings are still rooted by the channel.
                            try { _resultStore?.Drain(); }
                            catch (Exception ex) { YaguLog.For("ViewModel").LogWarning(ex, "ResultStore drain failed"); }

                            // A zero-result eviction freed no managed payload. Forcing a full GC and
                            // trimming the working set in that case only causes page-fault churn while
                            // the native scanner continues reading files. The SearchService callback
                            // already applies the same productive-eviction guard; keep the post-drain GC
                            // here only when this pass actually queued payloads for eviction.
                            if (IsSearching && enqueued > 0)
                                SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(3));
                            else
                                CollectPostEvictionIfDue();
                        });
                        break;
                    case SearchEvent.MemoryPressureRelieved relieved:
                        Degraded = false;
                        DegradedNoticeText = string.Empty;
                        UpdateFilesPerSecond();
                        YaguLog.For("ViewModel").LogWarning("Memory pressure relieved — leaving memory-saving mode ({Diagnostics})", relieved.Diagnostics);
                        break;
                    case SearchEvent.ScanCompleted sc:
                        var scanElapsed = StopSearchTimer();
                        FilesScanned = sc.Summary.FilesScanned;
                        TotalFiles = sc.Summary.TotalFiles;
                        SearchInNameFirstPhase = false;
                        MatchesFound = Math.Max(sc.Summary.TotalMatches, ClampMatchCount(uiMatchesReceived));
                        FilesSkipped = sc.Summary.FilesSkipped;
                        AccessDeniedCount = sc.Summary.SkipReasons?.AccessDenied ?? 0;
                        _bytesScanned = sc.Summary.BytesScanned;
                        UpdateSkipBreakdown(sc.Summary.SkipReasons);
                        Truncated = sc.Summary.Truncated;
                        Degraded = sc.Summary.Degraded;
                        StatusText = $"Finalizing results... {MatchesFound:N0} matches in {_resultCollection.AllGroups.Count:N0} files ({FormatElapsed(scanElapsed)})";
                        break;
                    case SearchEvent.Completed c:
                        YaguLog.For("UIConsumer").LogWarning(
                            "Search #{RunId} completed: uiEvents={Events:N0}, uiMatches={Matches:N0}, " +
                            "groups={Groups:N0}, yields={Yields:N0}, " +
                            "diskEvicted={DiskEvicted:N0}",
                            runId, uiEventsReceived, uiMatchesReceived, _resultCollection.AllGroups.Count, uiYieldCount, _resultStore?.EvictedCount ?? 0);
                        var completedElapsed = StopSearchTimer();
                        int actualTotalMatches = Math.Max(c.Summary.TotalMatches, ClampMatchCount(uiMatchesReceived));
                        FilesScanned = c.Summary.FilesScanned;
                        TotalFiles = c.Summary.TotalFiles;
                        SearchInNameFirstPhase = false;
                        MatchesFound = actualTotalMatches;
                        FilesSkipped = c.Summary.FilesSkipped;
                        AccessDeniedCount = c.Summary.SkipReasons?.AccessDenied ?? 0;
                        UpdateSkipBreakdown(c.Summary.SkipReasons);
                        Truncated = c.Summary.Truncated;
                        Degraded = c.Summary.Degraded;
                        // Use the actual file-group count so the status bar matches
                        // the clipboard export. Filename-only matches create UI
                        // groups but aren't tracked by the engine's filesWithMatches
                        // counter when content search is also active.
                        var actualFileCount = Math.Max(c.Summary.FilesWithMatches, _resultCollection.AllGroups.Count);
                        var displaySummary = c.Summary with { TotalMatches = actualTotalMatches, FilesWithMatches = actualFileCount };
                        StatusText = BuildCompletionStatus(displaySummary, completedElapsed);
                        ApplySortAndFilter();
                        UpdateIndexCoverageStatus(c.Summary.IndexAcceleration);
                        ShowSearchCompleteToast(displaySummary, completedElapsed);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                var cancelledElapsed = StopSearchTimer();
                if (_lowDiskSpaceCancellation is { } lowDiskSpace)
                {
                    var message = LowDiskSpaceMonitor.BuildTerminationMessage(lowDiskSpace);
                    StatusText = message;
                    ErrorText = message;
                    YaguLog.For("Search").LogWarning("Search #{RunId} terminated because temp-file drive {Drive} is {UsedPercent:F1}% full", runId, lowDiskSpace.DriveDisplayName, lowDiskSpace.UsedPercent);
                    SearchTerminatedByLowDiskSpace?.Invoke(message);
                }
                else
                {
                    StatusText = BuildCancelledStatus(cancelledElapsed);
                    YaguLog.For("Search").LogInformation("Search #{RunId} cancelled", runId);
                    ShowSearchCancelledToast(cancelledElapsed);
                }
                DegradedNoticeText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                StopSearchTimer();
                ErrorText = $"Search failed: {ex.Message}";
                YaguLog.For("Search").LogCritical(ex, "Search #{RunId} failed", runId);
            }
        }
        finally
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                IsSearching = false;
                FilesPerSecondText = string.Empty;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
                _cts = null;
                _activeSearchRoots = Array.Empty<string>();
            }

            try { cts?.Cancel(); } catch { }
            if (lowDiskMonitorTask is not null)
                await lowDiskMonitorTask.ConfigureAwait(true);

            cts?.Dispose();
            _searchLifecycleGate.Release();
            ResumeContentIndexWarmupAfterSearch();
        }
    }

    /// <summary>Cancels only active work whose captured root lies on a removed volume. This is a transient
    /// device-loss response, not the user-visible indexing pause state.</summary>
    public void CancelOperationsForRemovedVolumes(IReadOnlyList<string> removedVolumeRoots)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => CancelOperationsForRemovedVolumes(removedVolumeRoots));
            return;
        }
        if (removedVolumeRoots is null || removedVolumeRoots.Count == 0)
            return;

        if (_activeSearchRoots.Any(root => DeviceVolumeChange.IntersectsAnyRoot(root, removedVolumeRoots)))
        {
            try { _cts?.Cancel(); } catch { }
            StatusText = "Search cancelled because a source drive was removed.";
        }

        if (DeviceVolumeChange.IntersectsAnyRoot(_activeIndexBuildFolder, removedVolumeRoots)
            || DeviceVolumeChange.IntersectsAnyRoot(_activeIndexWarmFolder, removedVolumeRoots))
        {
            try { _indexBuildCancellation?.Cancel(); } catch { }
            try { _indexWarmCancellation?.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Shows exactly one file as a file-name match, bypassing the search engine entirely. Used when the
    /// Traditional query is a complete file path: the file is displayed regardless of the Directory box.
    /// Reuses the normal search lifecycle (run id, gate, state reset, history, result collection) so the
    /// results list, status bar, and clipboard export behave just like any other completed search.
    /// </summary>
    private async Task RunSingleFilePathDisplayAsync(string filePath)
    {
        int runId = Interlocked.Increment(ref _searchRunId);
        CancelPreviousSearchForNewRun(runId);

        await _searchLifecycleGate.WaitAsync();

        CancellationTokenSource? cts = null;
        try
        {
            if (_shutdownRequested || runId != Volatile.Read(ref _searchRunId))
                return;

            ResetStateForNewSearch();
            cts = new CancellationTokenSource();
            _cts = cts;

            // The query was a complete path, not a content pattern: highlight nothing in the preview.
            LastSearchPattern = string.Empty;
            LastSearchCaseSensitive = CaseSensitive;
            LastSearchUseRegex = false;
            LastSearchExactMatch = false;
            LastSearchMultiline = false;
            LastSearchMultilineDotAll = false;

            var result = new SearchResult(
                FilePath: filePath,
                LineNumber: 0,
                MatchLine: string.Empty,
                MatchStartColumn: 0,
                MatchLength: 0,
                ContextBefore: Array.Empty<string>(),
                ContextAfter: Array.Empty<string>());
            await AddMatchAsync(result, cts.Token).ConfigureAwait(true);

            var elapsed = StopSearchTimer();
            FilesScanned = 1;
            TotalFiles = 1;
            MatchesFound = 1;
            Truncated = false;
            Degraded = false;
            StatusText = $"1 file matched the path \u2014 {Path.GetFileName(filePath)} ({FormatElapsed(elapsed)})";
            ApplySortAndFilter();

            // Record the typed path in Traditional search history (mirrors StartSearchAsync).
            SettingsService.PushRecent(_settings.SearchHistory, _settings.SearchHistoryTimes, Query, MaxRecentItems);
            _pendingSemanticHistoryEntry = null;
            SyncRecent();
            await PersistSettingsAsync();
        }
        catch (Exception ex)
        {
            StopSearchTimer();
            ErrorText = $"Search failed: {ex.Message}";
            YaguLog.For("Search").LogCritical(ex, "Single-file-path display failed");
        }
        finally
        {
            if (cts is not null && IsCurrentSearch(runId, cts))
            {
                IsSearching = false;
                FilesPerSecondText = string.Empty;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyState));
                _cts = null;
            }

            try { cts?.Cancel(); } catch { }
            cts?.Dispose();
            _searchLifecycleGate.Release();
        }
    }

    private void CancelPreviousSearchForNewRun(int runId)
    {
        var previous = _cts;
        if (previous is null) return;

        try
        {
            StatusText = "Cleaning up previous search…";
            previous.Cancel();
            YaguLog.For("Search").LogInformation("Cancelling previous search before starting search #{RunId}", runId);
        }
        catch (Exception ex)
        {
            YaguLog.For("Search").LogWarning(ex, "Previous search cleanup cancellation failed");
        }
    }

    private void ResetStateForNewSearch()
    {
        _cts = null;
        _lastSearchSortRefreshTicks = 0;
        _searchSortRefreshQueued = false;
        _searchSortRefreshIntervalSec = SearchSortRefreshIntervalBaseSec;

        // Cancel pending metadata tasks first so fire-and-forget closures
        // release their FileGroup references promptly.
        _metadataCts.Cancel();
        _metadataCts.Dispose();
        _metadataCts = new CancellationTokenSource();

        _expandedResultGroupKeys.Clear();
        _resultCollection.Clear();
        RebuildResultRows();
        FileMetadataCache.Clear();

        _resultStore?.Dispose();
        _resultStore = CreateResultStore();

        // Reclaim the previous search's result graph on the threadpool so the
        // UI thread isn't blocked by a full compacting GC.
        // Use blocking: false so search workers aren't suspended for seconds
        // when the heap is large (e.g. millions of evicted result shells).
        _ = Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            GC.WaitForPendingFinalizers();
        });

        ErrorText = null;
        FallbackReason = null;
        _searchProgressPhaseLabel = string.Empty;
        _sourceBackedSearchProgress = null;
        OnPropertyChanged(nameof(SearchProgressRightLabel));
        FilesScanned = 0;
        TotalFiles = 0;
        MatchesFound = 0;
        FilesSkipped = 0;
        HasPerformedSearch = true;
        AccessDeniedCount = 0;
        FilesPerSecondText = string.Empty;
        UpdateSkipBreakdown(null);
        Truncated = false;
        Degraded = false;
        DegradedNoticeText = string.Empty;
        _lowDiskSpaceCancellation = null;
        IsSearching = true;
        IsPreparingSearch = false;   // the scan committed — hand feedback off to IsSearching
        // Stay indeterminate seamlessly from the preparing phase through the name-first pass; a progress
        // snapshot reporting the full phase (or discovery completion) latches this false for the content scan.
        SearchInNameFirstPhase = true;
        _bytesScanned = 0;
        _prevBytesScanned = 0;
        _prevFilesScanned = 0;
        _prevSampleTime = 0;
        _prevDisplayTime = 0;
        _prevDisplayFiles = 0;
        _prevDisplayBytes = 0;
        _instantFilesPerSec = 0;
        _instantMbPerSec = 0;
        ThroughputSamples.Clear();
        _searchStartedUtc = DateTime.UtcNow;
        _searchTimer = Stopwatch.StartNew();
        StartSearchStatusHeartbeat();
        StatusText = "Searching…";

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private bool IsCurrentSearch(int runId, CancellationTokenSource cts) =>
        runId == Volatile.Read(ref _searchRunId) && ReferenceEquals(_cts, cts);

    private Task StartLowDiskSpaceMonitor(int runId, CancellationTokenSource cts, ResultStore? resultStore)
    {
        var tempFilePath = resultStore?.TempFilePath;
        if (string.IsNullOrWhiteSpace(tempFilePath))
            return Task.CompletedTask;

        var fullThreshold = LowDiskSpaceMonitor.PercentToThreshold(LowDiskSpaceWarningPercent);

        return LowDiskSpaceMonitor.StartAsync(
            tempFilePath,
            fullThreshold,
            LowDiskSpaceMonitor.DefaultCheckInterval,
            lowDiskSpace =>
        {
            if (!IsCurrentSearch(runId, cts))
                return;

            _lowDiskSpaceCancellation = lowDiskSpace;
            try { cts.Cancel(); }
            catch (Exception ex) { YaguLog.For("Search").LogWarning(ex, "Low disk-space cancellation failed"); }
        }, cts.Token);
    }

    private ResultStore CreateResultStore()
    {
        string? tempDir = ChooseResultStoreTempDir();
        try
        {
            return new ResultStore(tempDir);
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(tempDir))
        {
            YaguLog.For("ResultStore").LogWarning(ex, "Could not create result store in '{TempDir}', falling back to Windows temp", tempDir);
            return new ResultStore();
        }
    }

    /// <summary>Pick the configured temp directory for disk-backed search results.</summary>
    private string? ChooseResultStoreTempDir()
    {
        // Override via environment variable (e.g. for profiling on the same fast SSD)
        string? envOverride = Environment.GetEnvironmentVariable("YAGU_RESULTSTORE_TEMP");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        if (!string.IsNullOrWhiteSpace(SearchResultTempDirectory))
            return SearchResultTempDirectory;

        return Path.GetTempPath();
    }
}
