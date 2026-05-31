using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services;

public sealed class SearchService
{
    private const int MemoryPressureRecoveryMarginPercent = 5;
    private const double ProcessMemoryRecoveryRatio = 0.90;
    private const int EventChannelCapacity = 64;
    private const int UnlimitedContentResultChannelCapacity = 2_048;
    private const int MaxContentResultChannelCapacity = 4_096;
    private const long AutoProcessMemoryCapFloor = 512L * 1024 * 1024;
    private const long AutoProcessMemoryCapCeiling = 768L * 1024 * 1024;
    private const long AutoProcessMemoryCapFallback = AutoProcessMemoryCapCeiling;
    private const int MemorySavingNativeBatchSize = 256;
    private const int FutileEvictionCooldownSeconds = 5;
    private const int CriticalMemoryMultiplier = 4;
    private const int MemoryThrottleMaxWaitMs = 2_000;
    private static readonly TimeSpan PeriodicMemoryPressureCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NativePartialBatchFlushDelay = TimeSpan.FromMilliseconds(100);

    private static int s_memoryPressureGcInFlight;
    private static long s_lastMemoryPressureGcTicks;

    private readonly IFileLister _fileLister;
    private readonly ContentSearcher _searcher;

    public SearchService() : this(new FileLister(), new ContentSearcher()) { }
    public SearchService(IFileLister fileLister, ContentSearcher searcher)
    {
        _fileLister = fileLister;
        _searcher = searcher;
    }

    /// <summary>
    /// Stream search results. Caller iterates the channel; the returned task completes
    /// when all files are scanned, the search is cancelled, or the result cap is hit.
    /// </summary>

    public async IAsyncEnumerable<SearchEvent> SearchAsync(
        SearchOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            yield return new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null));
            yield break;
        }

        // MaxResults <= 0 means unlimited — rely solely on memory-pressure degradation.
        // Only clamp positive values that exceed the suggested ceiling.
        if (options.MaxResults > SearchOptions.MaxResultsCeiling)
        {
            options = CopyOptions(options, maxResults: SearchOptions.MaxResultsCeiling);
        }

        var sw = Stopwatch.StartNew();
        FileMetadataCache.Clear();

        // Validate regex up front so the UI gets a clear error.
        Regex? regex = null;
        string? literal = null;
        IReadOnlyList<string> literalTerms = Array.Empty<string>();
        SearchOptions patternOptions = options;
        string? regexError = null;
        if (options.UseRegex)
        {
            RegexOptions regexOpts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (!options.CaseSensitive) regexOpts |= RegexOptions.IgnoreCase;
            try { regex = new Regex(options.Query, regexOpts); }
            catch (ArgumentException ex) { regexError = $"Invalid regex: {ex.Message}"; LogService.Instance.Warning("SearchService", regexError); }
        }
        else
        {
            literalTerms = SearchQueryParser.ParseLiteralTerms(options.Query, options.ExactMatch);
            if (literalTerms.Count == 0)
            {
                yield return new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null));
                yield break;
            }

            if (literalTerms.Count > 1)
            {
                string alternation = SearchQueryParser.BuildLiteralAlternation(literalTerms);
                RegexOptions regexOpts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (!options.CaseSensitive) regexOpts |= RegexOptions.IgnoreCase;
                regex = new Regex(alternation, regexOpts);
                patternOptions = CopyOptions(options, query: alternation, useRegex: true);
            }
            else
            {
                literal = literalTerms[0];
                patternOptions = CopyOptions(options, query: literal);
            }
        }
        if (regexError is not null)
        {
            yield return new SearchEvent.Error(regexError);
            yield return new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, sw.Elapsed, false, false, false, null));
            yield break;
        }
        var cmp = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Pipelined design:
        //   - Discovery task lists files, applies globs, emits filename matches, and pushes
        //     paths into a bounded channel for the content workers.
        //   - Content workers (Parallel.ForEachAsync) scan files concurrently while discovery
        //     is still running. Their SearchResults flow into a shared content channel.
        //   - A forwarder converts SearchResults into SearchEvent.Match and writes them into
        //     the unified event channel that this method yields from.
        // This overlaps I/O-bound discovery with CPU-bound content scanning, instead of
        // draining the full file list before scanning starts.
        IReadOnlyList<string> includeExts = options.IncludeFilterMode == FilterPatternMode.GlobPath
            ? ExtractExtensions(options.IncludeGlobs)
            : Array.Empty<string>();
        var globMatcher = new GlobMatcher(
            options.IncludeGlobs,
            options.ExcludeGlobs,
            options.IncludeFilterMode,
            options.ExcludeFilterMode);

        // Push skip settings to the lister so the Everything SDK path can
        // pre-filter by size/extension without per-file FileInfo calls.
        if (_fileLister is FileLister concreteLister)
        {
            concreteLister.EarlyMinFileSizeBytes = options.MinFileSizeBytes;
            concreteLister.EarlyMaxFileSizeBytes = options.MaxFileSizeBytes;
            concreteLister.EarlyCreatedAfterDate = options.CreatedAfterDate;
            concreteLister.EarlyCreatedBeforeDate = options.CreatedBeforeDate;
            concreteLister.EarlyModifiedAfterDate = options.ModifiedAfterDate;
            concreteLister.EarlyModifiedBeforeDate = options.ModifiedBeforeDate;

            // When archive search is enabled, don't let the file lister skip
            // zip-like extensions — they need to reach ContentSearcher so it
            // can open them as archives.
            var skipExts = options.SkipExtensions;
            if (options.SearchInsideArchives && skipExts.Count > 0 && options.ArchiveExtensions.Count > 0)
            {
                var filtered = new HashSet<string>(skipExts, StringComparer.OrdinalIgnoreCase);
                // ArchiveExtensions uses ".zip" format; SkipExtensions uses "zip" (no dot).
                foreach (var ext in options.ArchiveExtensions)
                    filtered.Remove(ext.TrimStart('.'));
                skipExts = filtered;
            }
            concreteLister.EarlySkipExtensions = skipExts;

            concreteLister.EarlyExcludeGlobs = options.ExcludeFilterMode == FilterPatternMode.GlobPath
                ? options.ExcludeGlobs
                : Array.Empty<string>();
            concreteLister.EarlyFileNameLiteralTerms = options.SearchMode == SearchMode.FileNameThenContent
                && !options.UseRegex
                && !options.CaseSensitive
                ? literalTerms
                : [];
            concreteLister.SdkChannelBufferSize = options.SdkChannelBufferSize;
            concreteLister.ExcludeAdminProtectedPaths = options.ExcludeAdminProtectedPaths;
            concreteLister.AdminProtectedPathSegmentsOverride = options.AdminProtectedPathSegments;
            concreteLister.MaxSearchDepth = options.MaxSearchDepth;

            // Dynamic gitignore: create a matcher that loads .gitignore files
            // lazily as directories are encountered during the scan.
            if (options.ObeyGitignore)
            {
                var matcher = new DynamicGitignoreMatcher(options.Directory);
                if (!options.GitignoreTakesPrecedence && includeExts.Count > 0)
                {
                    matcher.IncludeExtensionOverrides = new HashSet<string>(
                        includeExts.Select(e => e.TrimStart('.')),
                        StringComparer.OrdinalIgnoreCase);
                }
                concreteLister.GitignoreMatcher = matcher;
            }
            else
            {
                concreteLister.GitignoreMatcher = null;
            }
        }

        bool searchContent = options.SearchMode != SearchMode.FileNames;
        bool evaluateFileName = options.SearchMode != SearchMode.Content;
        bool emitFileNameMatches = options.SearchMode is SearchMode.Both or SearchMode.FileNames;
        bool requireFileNameMatchForContent = options.SearchMode == SearchMode.FileNameThenContent;

        // Push the configurable archive-extension set to the searcher so it
        // can bypass extension-based skip for ZIP-like containers.
        _searcher.ZipLikeExtensions = options.ArchiveExtensions;

        var events = Channel.CreateBounded<SearchEvent>(new BoundedChannelOptions(EventChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // Bounded so discovery applies back-pressure when workers are saturated.
        var pending = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // Bounded for streaming: limits in-flight results between workers and the
        // consumer, preventing unbounded memory growth. Native and managed paths
        // both wait for space instead of dropping matches when the channel is full.
        // Channel buffer size — independent of total result limit.
        int contentCap = options.MaxResults > 0
            ? Math.Clamp(options.MaxResults, 256, MaxContentResultChannelCapacity)
            : UnlimitedContentResultChannelCapacity;
        var contentResults = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(contentCap)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        LogService.Instance.Warning("SearchService", $"Pipeline channels created: events={EventChannelCapacity}, pending=1024, contentResults={contentCap}");

        int filesScanned = 0;
        int filesSkipped = 0;
        int filesWithMatches = 0;
        int totalMatches = 0;
        int totalDiscovered = 0;
        long bytesScanned = 0;
        int truncated = 0;
        int degraded = options.DegradedResultStore != null ? 1 : 0; // start degraded immediately when a result store is available
        int everDegraded = degraded;   // 1 once memory-saving mode was used during this search
        int evictionInFlight = 0;    // 1 while an eviction event is being processed by the UI
        int pressureCycles = 0;      // total number of memory pressure events emitted
        int consecutiveFutileEvictions = 0; // eviction cycles that freed 0 — used to stop futile loops
        long lastPressureCheckTicks = 0;    // timestamp of last pressure event emission
        int nativeBatchesProcessed = 0; // total native batches flushed
        long forwarderItemsForwarded = 0; // total results forwarded from contentResults → events
        long forwarderWriteStallMs = 0;   // cumulative ms the forwarder was blocked writing to events channel
        string? fallbackReason = null;
        // Skip-reason tallies
        int skipBinary = 0, skipAccessDenied = 0, skipIOError = 0, skipTooLarge = 0;
        int skipNotFound = 0, skipEncoding = 0, skipOther = 0, skipByExtension = 0, skipDirectories = 0;
        int skipGlobExcluded = 0;
        int skipSizeFiltered = 0;
        StreamingScanSink? activeStreamingSink = null; // promoted to outer scope so CheckMemoryPressure can toggle degraded mode
        IntPtr activeFilesScannedPtr = IntPtr.Zero; // unmanaged counter updated atomically during streaming scan
        IntPtr activeTotalMatchesPtr = IntPtr.Zero; // unmanaged counter updated atomically during streaming scan
        IntPtr activeFilesWithMatchesPtr = IntPtr.Zero;

        int CurrentDirectorySkips() => Math.Max(Volatile.Read(ref skipDirectories), _fileLister.SkippedDirectories);
        int CurrentAccessDeniedSkips() => Volatile.Read(ref skipAccessDenied) + _fileLister.AccessDeniedDirectories;
        int CurrentEarlySkips() => _fileLister.EarlySkippedFiles;
        int CurrentEarlyTooLargeSkips() => _fileLister.EarlySkippedTooLargeFiles;
        int CurrentFilesSkipped() => Volatile.Read(ref filesSkipped) + CurrentDirectorySkips() + CurrentEarlySkips();
        // Total files processed (content-scanned + early-filtered + discovery-filtered)
        // so the progress bar increments for every file that has been "dealt with".
        int CurrentFilesScanned()
        {
            unsafe
            {
                return activeFilesScannedPtr != IntPtr.Zero
                    ? Volatile.Read(ref *(int*)activeFilesScannedPtr)
                    : Volatile.Read(ref filesScanned);
            }
        }

        int CurrentFilesProcessed() => CurrentFilesScanned() + CurrentEarlySkips() + Volatile.Read(ref skipSizeFiltered);
        int CurrentTotalFiles()
        {
            int knownTotal = _fileLister.KnownTotalFiles;
            int discoveredTotal = Volatile.Read(ref totalDiscovered) + CurrentEarlySkips();
            int completedTotal = CurrentFilesProcessed();
            return Math.Max(knownTotal, Math.Max(discoveredTotal, completedTotal));
        }

        SearchProgress CreateProgressSnapshot()
        {
            int accessDenied = CurrentAccessDeniedSkips();
            int dirSkips = CurrentDirectorySkips();
            int nonAccessDeniedDirSkips = Math.Max(0, dirSkips - _fileLister.AccessDeniedDirectories);
            var breakdown = new SkipBreakdown(
                Volatile.Read(ref skipBinary),
                accessDenied,
                Volatile.Read(ref skipIOError),
                Volatile.Read(ref skipTooLarge) + CurrentEarlyTooLargeSkips(),
                Volatile.Read(ref skipNotFound),
                Volatile.Read(ref skipEncoding),
                Volatile.Read(ref skipOther),
                Volatile.Read(ref skipByExtension),
                nonAccessDeniedDirSkips,
                CurrentEarlySkips() + Volatile.Read(ref skipSizeFiltered),
                Volatile.Read(ref skipGlobExcluded),
                _fileLister.GitignoreSkipped);
            int currentTotalMatches;
            int currentFilesWithMatches;
            unsafe
            {
                currentTotalMatches = activeTotalMatchesPtr != IntPtr.Zero
                    ? Volatile.Read(ref *(int*)activeTotalMatchesPtr)
                    : Volatile.Read(ref totalMatches);
                currentFilesWithMatches = activeFilesWithMatchesPtr != IntPtr.Zero
                    ? Volatile.Read(ref *(int*)activeFilesWithMatchesPtr)
                    : Volatile.Read(ref filesWithMatches);
            }
            return new(
                CurrentFilesProcessed(),
                CurrentTotalFiles(),
                currentTotalMatches,
                currentFilesWithMatches,
                CurrentFilesSkipped(),
                Volatile.Read(ref bytesScanned),
                sw.Elapsed,
                accessDenied,
                breakdown);
        }

        // Captures the search locals directly so workers and the progress timer can
        // trigger the same degradation path without waiting for a native batch to end.
        void CheckMemoryPressure()
        {
            if (IsMemoryPressureHigh(options.MaxProcessMemoryBytes, options.MemoryPressurePercent))
            {
                Volatile.Write(ref degraded, 1);
                Volatile.Write(ref everDegraded, 1);
                activeStreamingSink?.SetDegraded(true);

                // Immediately trim WS on every pressure check to release soft-faulted
                // mmap pages. This is cheap (no-op if pages are actively used) and keeps
                // WS below target between GC collection cooldown windows.
                TrimProcessWorkingSet();

                // After several consecutive evictions that freed nothing, slow down
                // pressure events to avoid futile GC churn. But use a short cooldown
                // so we don't let memory grow unchecked for long.
                int futile = Volatile.Read(ref consecutiveFutileEvictions);
                if (futile >= 3)
                {
                    long now = Stopwatch.GetTimestamp();
                    long last = Volatile.Read(ref lastPressureCheckTicks);
                    double secSinceLast = last == 0 ? double.MaxValue : (double)(now - last) / Stopwatch.Frequency;
                    if (secSinceLast < FutileEvictionCooldownSeconds)
                        return;
                }

                if (Interlocked.CompareExchange(ref evictionInFlight, 1, 0) == 0)
                {
                    Volatile.Write(ref lastPressureCheckTicks, Stopwatch.GetTimestamp());
                    int cycle = Interlocked.Increment(ref pressureCycles);
                    string diagnostics = GetMemoryDiagnostics();
                    LogService.Instance.Warning("SearchService",
                        $"Memory pressure cycle #{cycle}: {diagnostics} - shedding Yagu memory (scanned={filesScanned:N0}, matches={totalMatches:N0})");
                    try
                    {
                        var memoryPressureEvent = new SearchEvent.MemoryPressure(
                            (evictedCount) =>
                            {
                                if (evictedCount == 0)
                                    Interlocked.Increment(ref consecutiveFutileEvictions);
                                else
                                    Volatile.Write(ref consecutiveFutileEvictions, 0);
                                LogService.Instance.Warning("SearchService",
                                    $"Eviction acknowledged: freed {evictedCount}; continuing in memory-saving mode");
                                if (evictedCount > 0)
                                    _ = Task.Run(() => CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(3)));
                                Volatile.Write(ref evictionInFlight, 0);
                            },
                            options.MemoryPressurePercent,
                            diagnostics);

                        if (!events.Writer.TryWrite(memoryPressureEvent))
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await events.Writer.WriteAsync(memoryPressureEvent, cancellationToken).ConfigureAwait(false); }
                                catch { Volatile.Write(ref evictionInFlight, 0); }
                            }, CancellationToken.None);
                        }
                    }
                    catch
                    {
                        Volatile.Write(ref evictionInFlight, 0);
                    }
                }

                // Only trigger GC if eviction has been productive recently.
                if (futile < 3)
                    CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(10));
            }
            else if (options.DegradedResultStore == null && // never leave degraded if we started there by design
                     Volatile.Read(ref degraded) != 0 &&
                     Volatile.Read(ref evictionInFlight) == 0 &&
                     IsMemoryPressureRelieved(options.MaxProcessMemoryBytes, options.MemoryPressurePercent))
            {
                Volatile.Write(ref degraded, 0);
                activeStreamingSink?.SetDegraded(false);
                string diagnostics = GetMemoryDiagnostics();
                LogService.Instance.Warning("SearchService",
                    $"Memory pressure relieved: {diagnostics} - leaving memory-saving mode");
                var relievedEvent = new SearchEvent.MemoryPressureRelieved(diagnostics);
                if (!events.Writer.TryWrite(relievedEvent))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await events.Writer.WriteAsync(relievedEvent, cancellationToken).ConfigureAwait(false); }
                        catch { }
                    }, CancellationToken.None);
                }
            }
        }

        // Blocks the calling worker thread when memory is critically over cap,
        // giving eviction + GC time to reclaim before more results are produced.
        void WaitForMemoryRelief(CancellationToken ct)
        {
            long ws = Environment.WorkingSet;
            long cap = EffectiveProcessMemoryCap(options.MaxProcessMemoryBytes);
            long criticalThreshold = cap * CriticalMemoryMultiplier;
            if (ws <= criticalThreshold)
                return;

            LogService.Instance.Warning("SearchService",
                $"Scanner throttled: working set {ws / (1024*1024)} MB exceeds {criticalThreshold / (1024*1024)} MB — waiting for relief");

            // Request a non-blocking GC to free evicted strings without freezing I/O threads.
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            TrimProcessWorkingSet();

            var deadline = Stopwatch.GetTimestamp() + (long)(MemoryThrottleMaxWaitMs / 1000.0 * Stopwatch.Frequency);
            while (!ct.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(200);
                ws = Environment.WorkingSet;
                if (ws <= criticalThreshold)
                {
                    LogService.Instance.Info("SearchService",
                        $"Scanner resumed: working set {ws / (1024*1024)} MB ≤ {criticalThreshold / (1024*1024)} MB");
                    return;
                }
            }

            LogService.Instance.Warning("SearchService",
                $"Scanner throttle timeout after {MemoryThrottleMaxWaitMs}ms — resuming (WS={ws / (1024*1024)} MB)");
        }

        // ── Discovery ──
        var discovery = Task.Run(async () =>
        {
            // Filename-match batch buffer. Filename hits can fire millions of times against
            // a 2M-file Everything index; emitting one event per hit saturates the UI dispatcher.
            // We coalesce into batches of FilenameBatchSize so the consumer pays one dispatch
            // cost per batch instead of per result.
            const int FilenameBatchSize = 256;
            List<SearchResult>? filenameBatch = null;
            async ValueTask FlushFilenameBatchAsync()
            {
                if (filenameBatch is null || filenameBatch.Count == 0) return;
                var batch = filenameBatch;
                filenameBatch = null;
                await events.Writer.WriteAsync(new SearchEvent.MatchBatch(batch), cancellationToken).ConfigureAwait(false);
            }

            async ValueTask WritePendingFileAsync(string path)
            {
                while (Volatile.Read(ref truncated) == 0)
                {
                    if (pending.Writer.TryWrite(path))
                        return;

                    // Await space directly — avoids Task.WhenAny + Task.Delay allocations per file.
                    if (!await pending.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                        return;
                }
            }

            bool fileListerAlreadyCheckedMetadata = _fileLister is FileLister;

            try
            {
                int discoveryLogCounter = 0;
                var discoveryLogTimer = Stopwatch.StartNew();
                await foreach (var path in _fileLister.ListFilesAsync(options.Directory, includeExts, maxFiles: 0, cancellationToken).WithCancellation(cancellationToken))
                {
                    if (Volatile.Read(ref truncated) != 0) break;
                    Interlocked.Increment(ref totalDiscovered);

                    if (!globMatcher.Matches(path))
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipGlobExcluded);
                        continue;
                    }

                        if (ShouldSkipByFileMetadata(path, options, out bool tooLarge,
                            checkSize: !fileListerAlreadyCheckedMetadata,
                            checkDates: !fileListerAlreadyCheckedMetadata))
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipSizeFiltered);
                        if (tooLarge)
                            Interlocked.Increment(ref skipTooLarge);
                        continue;
                    }

                    bool fileNameMatched = false;
                    int fnMatchStart = -1;
                    int fnMatchLen = 0;
                    if (evaluateFileName)
                    {
                        var fileName = Path.GetFileName(path);
                        if (regex is not null)
                        {
                            var m = regex.Match(fileName);
                            if (m.Success)
                            {
                                fnMatchStart = m.Index;
                                fnMatchLen = m.Length;
                            }
                        }
                        else
                        {
                            int idx = fileName.IndexOf(literal!, cmp);
                            if (idx >= 0)
                            {
                                fnMatchStart = idx;
                                fnMatchLen = literal!.Length;
                            }
                        }
                        if (fnMatchStart >= 0)
                        {
                            fileNameMatched = true;
                            if (emitFileNameMatches)
                            {
                                (filenameBatch ??= new List<SearchResult>(FilenameBatchSize)).Add(new SearchResult(
                                    FilePath: path, LineNumber: 0, MatchLine: fileName,
                                    MatchStartColumn: fnMatchStart, MatchLength: fnMatchLen,
                                    ContextBefore: [], ContextAfter: []));
                                if (filenameBatch.Count >= FilenameBatchSize)
                                    await FlushFilenameBatchAsync().ConfigureAwait(false);
                                if (!searchContent)
                                {
                                    Interlocked.Increment(ref filesWithMatches);
                                    int n = Interlocked.Increment(ref totalMatches);
                                    if (options.MaxResults > 0 && n >= options.MaxResults) Volatile.Write(ref truncated, 1);
                                }
                            }
                        }
                    }

                    if (searchContent)
                    {
                        if (!requireFileNameMatchForContent || fileNameMatched)
                            await WritePendingFileAsync(path).ConfigureAwait(false);
                        else
                            Interlocked.Increment(ref filesScanned);
                    }
                    else
                    {
                        Interlocked.Increment(ref filesScanned);
                    }

                    // Periodic discovery progress (every 100k files or 5s)
                    discoveryLogCounter++;
                    if (discoveryLogCounter % 100_000 == 0 || discoveryLogTimer.ElapsedMilliseconds >= 5000)
                    {
                        LogService.Instance.Warning("Discovery", $"Progress: {discoveryLogCounter:N0} files enumerated, {Volatile.Read(ref totalDiscovered):N0} discovered, elapsed={sw.Elapsed.TotalSeconds:F1}s");
                        discoveryLogTimer.Restart();
                    }
                }
                await FlushFilenameBatchAsync().ConfigureAwait(false);
                Volatile.Write(ref skipDirectories, _fileLister.SkippedDirectories);
                fallbackReason = _fileLister.FallbackReason;
                if (fallbackReason is not null)
                {
                    await events.Writer.WriteAsync(new SearchEvent.Fallback(fallbackReason), cancellationToken).ConfigureAwait(false);
                }
                await events.Writer.WriteAsync(new SearchEvent.DiscoveryComplete(CurrentTotalFiles()), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { LogService.Instance.Warning("SearchService", "Discovery cancelled"); }
            catch (Exception ex) { LogService.Instance.Warning("SearchService", "Discovery failed", ex); }
            finally
            {
                LogService.Instance.Warning("SearchService", $"Discovery finished: {Volatile.Read(ref totalDiscovered):N0} files discovered, total={CurrentTotalFiles():N0}, {sw.Elapsed.TotalSeconds:F2}s elapsed");
                pending.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // ── Content workers ──
        var workers = Task.CompletedTask;
        if (searchContent)
        {
            workers = Task.Run(async () =>
            {
                try
                {
                    bool nativeAvailable = Native.NativeSearcher.IsAvailable;
                    int parallelism = options.MaxDegreeOfParallelism > 0
                        ? options.MaxDegreeOfParallelism
                        : nativeAvailable
                            ? Math.Max(1, Math.Min(64, Environment.ProcessorCount * 2))
                            : Math.Max(1, Math.Min(16, Environment.ProcessorCount));
                    LogService.Instance.Info("SearchService", $"Content scan parallelism = {parallelism}");

                    // Pre-compute the degraded options once so we don't allocate a new
                    // SearchOptions per file inside the hot loop.
                    var degradedOptions = patternOptions.ContextLines > 0
                        ? CopyOptions(patternOptions, contextLines: 0)
                        : patternOptions;

                    if (nativeAvailable)
                    {
                        // ── Streaming native path ──
                        // Persistent Rust worker threads pull paths from an internal
                        // queue as C# feeds them from the discovery channel. Eliminates
                        // batch-boundary idle time and per-batch thread creation cost.
                        Native.NativeSession? streamSession = null;
                        Native.NativeSession? degradedSession = null;
                        try
                        {
                            streamSession = Native.NativeSearcher.CreateSession(patternOptions.Query, patternOptions);
                            if (patternOptions.ContextLines > 0)
                                degradedSession = Native.NativeSearcher.CreateSession(patternOptions.Query, degradedOptions);

                            if (streamSession == null)
                            {
                                LogService.Instance.Warning("SearchService", "Native session creation failed — falling back to managed per-file path");
                                goto managedFallback;
                            }

                            LogService.Instance.Info("SearchService", "Streaming native scanning enabled");

                            IntPtr cancelPtr = Marshal.AllocHGlobal(sizeof(int));
                            try
                            {
                                unsafe { *(int*)cancelPtr = 0; }
                                using var ctr = cancellationToken.Register(static state =>
                                {
                                    unsafe { System.Threading.Interlocked.Exchange(ref *(int*)(IntPtr)state!, 1); }
                                }, cancelPtr);

                                // When archive search is enabled, zip files must go through the managed
                                // ContentSearcher rather than the native Rust scanner.
                                async Task ScanZipViaManagedAsync(string zipFile)
                                {
                                    var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : patternOptions;
                                    try
                                    {
                                        var outcome = await _searcher.SearchFileWithStatsAsync(
                                            zipFile, regex, literal, cmp, effectiveOptions,
                                            contentResults.Writer, cancellationToken, session: null).ConfigureAwait(false);
                                        int produced = outcome.MatchCount;
                                        int fileCount = Math.Max(1, outcome.EntriesScanned);
                                        Interlocked.Add(ref filesScanned, fileCount);

                                        if (produced < 0)
                                        {
                                            Interlocked.Increment(ref filesSkipped);
                                        }
                                        else
                                        {
                                            Interlocked.Add(ref bytesScanned, outcome.BytesScanned);
                                            if (produced > 0)
                                            {
                                                Interlocked.Increment(ref filesWithMatches);
                                                int newTotal = Interlocked.Add(ref totalMatches, produced);
                                                if (options.MaxResults > 0 && newTotal >= options.MaxResults)
                                                    Volatile.Write(ref truncated, 1);
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException) { }
                                    catch (Exception ex)
                                    {
                                        LogService.Instance.Warning("SearchService", $"Managed ZIP scan failed for {zipFile}", ex);
                                        Interlocked.Increment(ref filesScanned);
                                        Interlocked.Increment(ref filesSkipped);
                                        Interlocked.Increment(ref skipOther);
                                    }
                                }

                                // Choose which session the streaming scanner uses (degraded strips context).
                                var activeSession = (Volatile.Read(ref degraded) != 0 && degradedSession != null)
                                    ? degradedSession : streamSession;

                                bool streamingFailed = false;
                                // Use unmanaged alloc for counters (can't use fixed in async).
                                // We sync back to the local counters after the scan completes.
                                IntPtr filesScannedAlloc = Marshal.AllocHGlobal(sizeof(int));
                                IntPtr totalMatchesAlloc = Marshal.AllocHGlobal(sizeof(int));
                                IntPtr filesWithMatchesAlloc = Marshal.AllocHGlobal(sizeof(int));
                                unsafe
                                {
                                    *(int*)filesScannedAlloc = Volatile.Read(ref filesScanned);
                                    *(int*)totalMatchesAlloc = Volatile.Read(ref totalMatches);
                                    *(int*)filesWithMatchesAlloc = Volatile.Read(ref filesWithMatches);
                                }
                                activeFilesScannedPtr = filesScannedAlloc;
                                try
                                {
                                    // Track paths by file index for post-scan stats reconciliation
                                    var pathsByIndex = new List<string>(4096);
                                    Native.NativeSearcher.IParallelSink sinkInstance;
                                    DirectOutputSink? directSink = null;
                                    StreamingScanSink? streamingSink = null;
                                    unsafe
                                    {
                                        if (options.DirectOutputStream != null)
                                        {
                                            directSink = new DirectOutputSink(
                                                options.DirectOutputStream, options.DirectOutputColor,
                                                pathsByIndex, options.MaxResults, Volatile.Read(ref totalMatches),
                                                cancelPtr, (int*)filesScannedAlloc);
                                            sinkInstance = directSink;
                                        }
                                        else
                                        {
                                            streamingSink = new StreamingScanSink(
                                                pathsByIndex,
                                                contentResults.Writer, options.MaxResults, Volatile.Read(ref totalMatches),
                                                cancelPtr, (int*)filesScannedAlloc,
                                                (int*)totalMatchesAlloc, (int*)filesWithMatchesAlloc,
                                                options.DegradedResultStore);
                                            if (Volatile.Read(ref degraded) != 0)
                                                streamingSink.SetDegraded(true);
                                            activeStreamingSink = streamingSink;
                                            activeTotalMatchesPtr = totalMatchesAlloc;
                                            activeFilesWithMatchesPtr = filesWithMatchesAlloc;
                                            sinkInstance = streamingSink;
                                        }
                                    }

                                    // Create streaming scanner — Rust spawns persistent worker threads
                                    IntPtr scanner;
                                    GCHandle sinkHandle;
                                    unsafe
                                    {
                                        scanner = Native.NativeSearcher.CreateStreamingScanner(
                                            activeSession, parallelism, (int*)cancelPtr, sinkInstance, out sinkHandle);
                                    }

                                    if (scanner == IntPtr.Zero)
                                    {
                                        LogService.Instance.Warning("SearchService", "Streaming scanner creation failed — falling back to managed path");
                                        if (sinkHandle.IsAllocated) sinkHandle.Free();
                                        streamingFailed = true;
                                    }

                                    if (!streamingFailed)
                                    {
                                    try
                                    {
                                        // Feed paths from discovery channel to the streaming scanner.
                                        // Push in small batches to amortize FFI overhead while keeping
                                        // the pipeline fed continuously.
                                        const int PushBatchSize = 64;
                                        var pushBatch = new List<string>(PushBatchSize);
                                        var zipTasks = new List<Task>();
                                        int fileIndexCounter = 0;
                                        long lastLogTicks = Stopwatch.GetTimestamp();
                                        const long LogIntervalSec = 10;

                                        while (Volatile.Read(ref truncated) == 0)
                                        {
                                            // Drain available items from the channel
                                            while (pushBatch.Count < PushBatchSize && pending.Reader.TryRead(out var file))
                                            {
                                                if (options.SearchInsideArchives && ZipArchiveSearcher.HasArchiveExtension(file, options.ArchiveExtensions))
                                                {
                                                    zipTasks.Add(ScanZipViaManagedAsync(file));
                                                }
                                                else
                                                {
                                                    pushBatch.Add(file);
                                                }
                                            }

                                            if (pushBatch.Count > 0)
                                            {
                                                // Add paths to the shared list (sink uses these for callbacks)
                                                for (int pi = 0; pi < pushBatch.Count; pi++)
                                                    pathsByIndex.Add(pushBatch[pi]);

                                                Native.NativeSearcher.PushPaths(scanner, pushBatch, fileIndexCounter);
                                                fileIndexCounter += pushBatch.Count;
                                                Interlocked.Increment(ref nativeBatchesProcessed);
                                                pushBatch.Clear();

                                                // Periodic progress log
                                                long now = Stopwatch.GetTimestamp();
                                                if ((now - lastLogTicks) >= Stopwatch.Frequency * LogIntervalSec)
                                                {
                                                    lastLogTicks = now;
                                                    string memDiag = GetMemoryDiagnostics();
                                                    LogService.Instance.Warning("Workers",
                                                        $"Streaming: pushed={fileIndexCounter:N0} | " +
                                                        $"scanned={CurrentFilesScanned():N0}, matches={Volatile.Read(ref totalMatches):N0}, " +
                                                        $"withMatches={Volatile.Read(ref filesWithMatches):N0}, skipped={Volatile.Read(ref filesSkipped):N0}, " +
                                                        $"degraded={Volatile.Read(ref degraded) != 0}, parallelism={parallelism}, " +
                                                        $"elapsed={sw.Elapsed.TotalSeconds:F1}s, {memDiag}");
                                                }
                                                continue;
                                            }

                                            // No items available — wait for more
                                            if (!await pending.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                                                break;
                                        }

                                        // Signal Rust workers: no more paths coming. Blocks until all drain.
                                        Native.NativeSearcher.FinishStreamingScanner(scanner);

                                        // Wait for any in-flight ZIP searches
                                        if (zipTasks.Count > 0)
                                        {
                                            try { await Task.WhenAll(zipTasks).ConfigureAwait(false); }
                                            catch (OperationCanceledException) { }
                                        }

                                        // Reconcile per-file stats from the sink
                                        if (directSink != null)
                                        {
                                            // DirectOutputSink tracks all stats internally
                                            directSink.Flush();
                                            Interlocked.Add(ref totalMatches, directSink.TotalMatches);
                                            Interlocked.Add(ref filesWithMatches, directSink.FilesWithMatches);
                                            Interlocked.Add(ref bytesScanned, directSink.BytesScanned);
                                            Interlocked.Add(ref filesSkipped, directSink.FilesSkipped);
                                            Interlocked.Add(ref skipBinary, directSink.SkipBinary);
                                            Interlocked.Add(ref skipAccessDenied, directSink.SkipAccessDenied);
                                            Interlocked.Add(ref skipTooLarge, directSink.SkipTooLarge);
                                            Interlocked.Add(ref skipNotFound, directSink.SkipNotFound);
                                            Interlocked.Add(ref skipOther, directSink.SkipOther);
                                            if (directSink.Truncated)
                                                Volatile.Write(ref truncated, 1);
                                        }
                                        else if (streamingSink != null)
                                        {
                                            // totalMatches already updated atomically via _totalMatchesPtr during scan
                                            if (streamingSink.Truncated)
                                                Volatile.Write(ref truncated, 1);

                                            for (int i = 0; i < Math.Min(fileIndexCounter, pathsByIndex.Count); i++)
                                            {
                                                int status = streamingSink.GetStatus(i);
                                                int emitted = streamingSink.GetEmitted(i);

                                                if (status != Native.NativeSearcher.StatusOk)
                                                {
                                                    Interlocked.Increment(ref filesSkipped);
                                                    switch (status)
                                                    {
                                                        case Native.NativeSearcher.StatusBinarySkipped:
                                                            Interlocked.Increment(ref skipBinary);
                                                            break;
                                                        case Native.NativeSearcher.StatusOpenFailed:
                                                            Interlocked.Increment(ref skipAccessDenied);
                                                            break;
                                                        case Native.NativeSearcher.StatusTooLarge:
                                                            Interlocked.Increment(ref skipTooLarge);
                                                            break;
                                                        case Native.NativeSearcher.StatusInvalidPath:
                                                            Interlocked.Increment(ref skipNotFound);
                                                            break;
                                                        default:
                                                            Interlocked.Increment(ref skipOther);
                                                            break;
                                                    }
                                                }
                                                else if (emitted > 0)
                                                {
                                                    // filesWithMatches already updated atomically via _filesWithMatchesPtr during scan
                                                    Interlocked.Add(ref bytesScanned, streamingSink.GetFileLength(i));
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Native.NativeSearcher.DestroyStreamingScanner(scanner);
                                        if (sinkHandle.IsAllocated) sinkHandle.Free();
                                    }
                                    } // if (!streamingFailed)

                                    directSink?.Dispose();
                                    streamingSink?.Dispose();
                                    activeStreamingSink = null;
                                    activeFilesScannedPtr = IntPtr.Zero;
                                    activeTotalMatchesPtr = IntPtr.Zero;
                                    activeFilesWithMatchesPtr = IntPtr.Zero;
                                    // Sync back counters from unmanaged memory
                                    unsafe
                                    {
                                        filesScanned = *(int*)filesScannedAlloc;
                                        totalMatches = *(int*)totalMatchesAlloc;
                                        filesWithMatches = *(int*)filesWithMatchesAlloc;
                                    }
                                }
                                finally
                                {
                                    activeStreamingSink = null;
                                    activeFilesScannedPtr = IntPtr.Zero;
                                    activeTotalMatchesPtr = IntPtr.Zero;
                                    activeFilesWithMatchesPtr = IntPtr.Zero;
                                    Marshal.FreeHGlobal(filesScannedAlloc);
                                    Marshal.FreeHGlobal(totalMatchesAlloc);
                                    Marshal.FreeHGlobal(filesWithMatchesAlloc);
                                }
                                if (streamingFailed) goto managedFallback;
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(cancelPtr);
                            }
                        }
                        finally
                        {
                            streamSession?.Dispose();
                            degradedSession?.Dispose();
                        }
                        goto workersDone;
                    }

                    managedFallback:
                    {
                        // ── Per-file managed fallback ──
                        // Used when the native engine is unavailable.
                        ThreadLocal<Native.NativeSession?>? sessionPool = null;
                        if (Native.NativeSearcher.IsAvailable)
                        {
                            sessionPool = new ThreadLocal<Native.NativeSession?>(
                                () => Native.NativeSearcher.CreateSession(patternOptions.Query, patternOptions),
                                trackAllValues: true);
                        }
                        try
                        {
                        await Parallel.ForEachAsync(pending.Reader.ReadAllAsync(cancellationToken), new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelism,
                            CancellationToken = cancellationToken,
                        }, async (file, ct) =>
                        {
                            if (Volatile.Read(ref truncated) != 0) return;
                            var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : patternOptions;
                            FileSearchOutcome outcome;
                            int produced;
                            try
                            {
                                outcome = await _searcher.SearchFileWithStatsAsync(file, regex, literal, cmp, effectiveOptions, contentResults.Writer, ct, sessionPool?.Value).ConfigureAwait(false);
                                produced = outcome.MatchCount;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                LogService.Instance.Warning("SearchService", $"Scan failed for {file}", ex);
                                Interlocked.Increment(ref filesScanned);
                                Interlocked.Increment(ref filesSkipped);
                                Interlocked.Increment(ref skipOther);
                                return;
                            }
                            Interlocked.Increment(ref filesScanned);
                            if (produced < 0)
                            {
                                Interlocked.Increment(ref filesSkipped);
                                switch (produced)
                                {
                                    case ContentSearcher.SkipBinary: Interlocked.Increment(ref skipBinary); break;
                                    case ContentSearcher.SkipAccessDenied:
                                        Interlocked.Increment(ref skipAccessDenied);
                                        LogService.Instance.Verbose("ContentSearcher", $"Access denied: {file}");
                                        break;
                                    case ContentSearcher.SkipIOError: Interlocked.Increment(ref skipIOError); break;
                                    case ContentSearcher.SkipTooLarge: Interlocked.Increment(ref skipTooLarge); break;
                                    case ContentSearcher.SkipTooSmall: Interlocked.Increment(ref skipSizeFiltered); break;
                                    case ContentSearcher.SkipNotFound: Interlocked.Increment(ref skipNotFound); break;
                                    case ContentSearcher.SkipEncoding: Interlocked.Increment(ref skipEncoding); break;
                                    case ContentSearcher.SkipByExtension: Interlocked.Increment(ref skipByExtension); break;
                                    default: Interlocked.Increment(ref skipOther); break;
                                }
                            }
                            else
                            {
                                Interlocked.Add(ref bytesScanned, outcome.BytesScanned);
                                if (produced > 0)
                                {
                                    Interlocked.Increment(ref filesWithMatches);
                                    int newTotal = Interlocked.Add(ref totalMatches, produced);
                                    if (options.MaxResults > 0 && newTotal >= options.MaxResults)
                                        Volatile.Write(ref truncated, 1);
                                }
                            }

                            CheckMemoryPressure();
                        }).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (sessionPool != null)
                            {
                                foreach (var s in sessionPool.Values)
                                    s?.Dispose();
                                sessionPool.Dispose();
                            }
                        }
                    }

                    workersDone:;
                }
                catch (OperationCanceledException) { LogService.Instance.Warning("SearchService", "Content workers cancelled"); }
                catch (Exception ex) { LogService.Instance.Warning("SearchService", "Content workers failed", ex); }
                finally
                {
                    string finishMemDiag = GetMemoryDiagnostics();
                    LogService.Instance.Warning("SearchService",
                        $"Content workers finished: scanned={filesScanned:N0}, withMatches={filesWithMatches:N0}, " +
                        $"totalMatches={totalMatches:N0}, skipped={Volatile.Read(ref filesSkipped):N0}, " +
                        $"batches={Volatile.Read(ref nativeBatchesProcessed)}, pressureCycles={Volatile.Read(ref pressureCycles)}, " +
                        $"elapsed={sw.Elapsed.TotalSeconds:F2}s, {finishMemDiag}");
                    contentResults.Writer.TryComplete();
                }
            }, CancellationToken.None);
        }
        else
        {
            contentResults.Writer.TryComplete();
        }

        // ── Forwarder: content results → unified event stream ──
        var forwarder = Task.Run(async () =>
        {
            try
            {
                const int ContentBatchSize = 256;
                const int DegradedContentBatchSize = ContentBatchSize;
                List<SearchResult>? contentBatch = null;
                long fwdLogLastTicks = Stopwatch.GetTimestamp();
                const long FwdLogIntervalSec = 10;
                long fwdBatchesFlushed = 0;
                var fwdWriteSw = new Stopwatch();

                async ValueTask FlushContentBatchAsync()
                {
                    if (contentBatch is null || contentBatch.Count == 0) return;
                    var batch = contentBatch;
                    contentBatch = null;
                    fwdWriteSw.Restart();
                    await events.Writer.WriteAsync(new SearchEvent.MatchBatch(batch), cancellationToken).ConfigureAwait(false);
                    fwdWriteSw.Stop();
                    long stallMs = fwdWriteSw.ElapsedMilliseconds;
                    Interlocked.Add(ref forwarderItemsForwarded, batch.Count);
                    Interlocked.Add(ref forwarderWriteStallMs, stallMs);
                    fwdBatchesFlushed++;

                    // Log if the write took a long time (events channel full — UI not draining fast enough)
                    if (stallMs > 500)
                    {
                        LogService.Instance.Warning("Forwarder",
                            $"Backpressure: WriteAsync to events channel took {stallMs}ms " +
                            $"(batch={batch.Count} items, totalForwarded={Volatile.Read(ref forwarderItemsForwarded):N0})");
                    }

                    // Periodic throughput log
                    long now = Stopwatch.GetTimestamp();
                    if ((now - fwdLogLastTicks) >= Stopwatch.Frequency * FwdLogIntervalSec)
                    {
                        fwdLogLastTicks = now;
                        LogService.Instance.Warning("Forwarder",
                            $"Throughput: forwarded={Volatile.Read(ref forwarderItemsForwarded):N0}, " +
                            $"batchesFlushed={fwdBatchesFlushed}, cumulativeStallMs={Volatile.Read(ref forwarderWriteStallMs)}, " +
                            $"elapsed={sw.Elapsed.TotalSeconds:F1}s");
                    }
                }

                // Drain the content channel, flushing partial batches after a
                // short timeout so the UI sees results promptly even when matches
                // arrive infrequently (e.g. a rare query across millions of files).
                const int PartialFlushDelayMs = 250;
                // Reuse a single linked CTS across iterations to eliminate the
                // ~389K Linked1CancellationTokenSource + TimerQueueTimer + CallbackNode
                // allocations measured in the previous profiling iteration.
                CancellationTokenSource? delayCts = null;
                try
                {
                    while (await contentResults.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (contentResults.Reader.TryRead(out var r))
                        {
                            (contentBatch ??= new List<SearchResult>(ContentBatchSize)).Add(r);
                            int targetBatchSize = Volatile.Read(ref degraded) != 0 ? DegradedContentBatchSize : ContentBatchSize;
                            if (contentBatch.Count >= targetBatchSize)
                                await FlushContentBatchAsync().ConfigureAwait(false);
                        }

                        // If we have a partial batch, wait briefly for more items before flushing.
                        if (contentBatch is { Count: > 0 })
                        {
                            // Reset (or recreate if reset fails) the linked CTS+timer instead of allocating new each iteration.
                            if (delayCts is null || !delayCts.TryReset())
                            {
                                delayCts?.Dispose();
                                delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            }
                            delayCts.CancelAfter(PartialFlushDelayMs);
                            try
                            {
                                if (await contentResults.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                                {
                                    // More items available — disarm timer to avoid spurious cancellation, then loop back.
                                    delayCts.CancelAfter(Timeout.Infinite);
                                    continue;
                                }
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Timeout expired, not a real cancellation — flush what we have.
                            }
                            await FlushContentBatchAsync().ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    delayCts?.Dispose();
                }

                await FlushContentBatchAsync().ConfigureAwait(false);
                LogService.Instance.Warning("Forwarder",
                    $"Completed: forwarded={Volatile.Read(ref forwarderItemsForwarded):N0}, " +
                    $"batchesFlushed={fwdBatchesFlushed}, cumulativeStallMs={Volatile.Read(ref forwarderWriteStallMs)}, " +
                    $"elapsed={sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (OperationCanceledException) { LogService.Instance.Warning("Forwarder", "Cancelled"); }
            catch (Exception ex) { LogService.Instance.Warning("Forwarder", "Failed", ex); }
        }, CancellationToken.None);

        var pipelineComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressEmitter = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                long lastMemoryPressurePollTicks = 0;
                long memoryPressurePollIntervalTicks = (long)(PeriodicMemoryPressureCheckInterval.TotalSeconds * Stopwatch.Frequency);
                while (!pipelineComplete.Task.IsCompleted &&
                       await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (pipelineComplete.Task.IsCompleted || Volatile.Read(ref truncated) != 0) break;
                    long now = Stopwatch.GetTimestamp();
                    if (lastMemoryPressurePollTicks == 0 ||
                        now - lastMemoryPressurePollTicks >= memoryPressurePollIntervalTicks)
                    {
                        lastMemoryPressurePollTicks = now;
                        CheckMemoryPressure();
                    }
                    events.Writer.TryWrite(new SearchEvent.Progress(CreateProgressSnapshot()));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService.Instance.Verbose("SearchService", "Progress emitter stopped", ex); }
        }, CancellationToken.None);

        // Close the events channel once everything upstream is done.
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(discovery, workers, forwarder).ConfigureAwait(false); }
            catch (Exception ex) { LogService.Instance.Verbose("SearchService", "Pipeline task exception", ex); }
            finally
            {
                pipelineComplete.TrySetResult();
                try { await progressEmitter.ConfigureAwait(false); }
                catch (Exception ex) { LogService.Instance.Verbose("SearchService", "Progress emitter completion failed", ex); }
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // 3. Stream events to caller. Progress snapshots are emitted by a timer so
        // quiet no-match stretches still update the UI.
        await foreach (var evt in events.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }

        bool wasTruncated = Volatile.Read(ref truncated) != 0;
        bool wasDegraded = Volatile.Read(ref everDegraded) != 0;

        int totalFiles = CurrentTotalFiles();
        int directorySkips = CurrentDirectorySkips();
        int earlySkips = CurrentEarlySkips();
        int discoverySizeSkips = Volatile.Read(ref skipSizeFiltered);
        int earlyTooLargeSkips = CurrentEarlyTooLargeSkips();
        int accessDeniedSkips = CurrentAccessDeniedSkips();
        int totalSkipped = Volatile.Read(ref filesSkipped) + directorySkips + earlySkips;
        int nonAccessDeniedDirectorySkips = Math.Max(0, directorySkips - _fileLister.AccessDeniedDirectories);
        var skipReasons = new SkipBreakdown(skipBinary, accessDeniedSkips, skipIOError, skipTooLarge + earlyTooLargeSkips, skipNotFound, skipEncoding, skipOther, skipByExtension, nonAccessDeniedDirectorySkips, earlySkips + discoverySizeSkips, skipGlobExcluded, _fileLister.GitignoreSkipped);
        LogService.Instance.Warning("SearchService", $"Search complete: {totalMatches} matches in {filesWithMatches} files, {filesScanned} scanned, {totalSkipped} skipped ({skipReasons}), earlyFiltered={earlySkips + discoverySizeSkips}, degraded={wasDegraded}, truncated={wasTruncated}, " +
            $"batches={Volatile.Read(ref nativeBatchesProcessed)}, pressureCycles={pressureCycles}, forwarderItems={Volatile.Read(ref forwarderItemsForwarded):N0}, forwarderStallMs={Volatile.Read(ref forwarderWriteStallMs)}, {sw.Elapsed.TotalSeconds:F2}s");
        yield return new SearchEvent.Completed(new SearchSummary(
            TotalFiles: totalFiles,
            FilesScanned: CurrentFilesProcessed(),
            FilesSkipped: totalSkipped,
            FilesWithMatches: filesWithMatches,
            TotalMatches: totalMatches,
            BytesScanned: bytesScanned,
            Elapsed: sw.Elapsed,
            Cancelled: cancellationToken.IsCancellationRequested,
            Truncated: wasTruncated,
            Degraded: wasDegraded,
            FallbackReason: fallbackReason,
            SkipReasons: skipReasons));
    }

    private static bool ShouldSkipByFileMetadata(
        string path,
        SearchOptions options,
        out bool tooLarge,
        bool checkSize = true,
        bool checkDates = true)
    {
        tooLarge = false;
        long minBytes = Math.Max(0, options.MinFileSizeBytes);
        long maxBytes = Math.Max(0, options.MaxFileSizeBytes);
        bool hasSizeFilter = checkSize && (minBytes > 0 || maxBytes > 0);
        bool hasDateFilter = checkDates && (options.CreatedAfterDate.HasValue
            || options.CreatedBeforeDate.HasValue
            || options.ModifiedAfterDate.HasValue
            || options.ModifiedBeforeDate.HasValue);
        // Always apply the content-search ceiling even when checkSize is false (unless ceiling is 0 = disabled).
        bool hasCeiling = (maxBytes == 0) && FileLister.ContentSearchFileSizeCeiling > 0;
        if (!hasSizeFilter && !hasDateFilter && !hasCeiling)
            return false;

        FileMetadata metadata;
        if (FileMetadataCache.TryGet(path, out var cached))
        {
            metadata = cached;
        }
        else
        {
            FileInfo fileInfo;
            try { fileInfo = new FileInfo(path); }
            catch (Exception ex)
            {
                LogService.Instance.Verbose("SearchService", $"Cannot stat file for size filter: {path}", ex);
                return false;
            }
            if (!fileInfo.Exists)
                return false;

            metadata = new FileMetadata(fileInfo.Length, fileInfo.LastWriteTime, fileInfo.CreationTime);
            FileMetadataCache.Set(path, metadata);
        }

        if (checkSize && minBytes > 0 && metadata.Length < minBytes)
            return true;

        if (checkSize && maxBytes > 0 && metadata.Length > maxBytes)
        {
            tooLarge = true;
            return true;
        }

        // Built-in ceiling: skip files > 100MB when no explicit max is set.
        if (hasCeiling && metadata.Length > FileLister.ContentSearchFileSizeCeiling)
        {
            tooLarge = true;
            return true;
        }

        if (checkDates && FileLister.IsOutsideDateRange(metadata.Created, options.CreatedAfterDate, options.CreatedBeforeDate))
            return true;

        if (checkDates && FileLister.IsOutsideDateRange(metadata.LastModified, options.ModifiedAfterDate, options.ModifiedBeforeDate))
            return true;

        return false;
    }

    private static SearchOptions CopyOptions(
        SearchOptions options,
        string? query = null,
        bool? useRegex = null,
        int? contextLines = null,
        int? maxResults = null)
        => new()
        {
            Directory = options.Directory,
            Query = query ?? options.Query,
            CaseSensitive = options.CaseSensitive,
            UseRegex = useRegex ?? options.UseRegex,
            ExactMatch = options.ExactMatch,
            ContextLines = contextLines ?? options.ContextLines,
            SearchMode = options.SearchMode,
            IncludeGlobs = options.IncludeGlobs,
            ExcludeGlobs = options.ExcludeGlobs,
            IncludeFilterMode = options.IncludeFilterMode,
            ExcludeFilterMode = options.ExcludeFilterMode,
            MinFileSizeBytes = options.MinFileSizeBytes,
            MaxFileSizeBytes = options.MaxFileSizeBytes,
            CreatedAfterDate = options.CreatedAfterDate,
            CreatedBeforeDate = options.CreatedBeforeDate,
            ModifiedAfterDate = options.ModifiedAfterDate,
            ModifiedBeforeDate = options.ModifiedBeforeDate,
            MaxResults = maxResults ?? options.MaxResults,
            MaxMatchesPerFile = options.MaxMatchesPerFile,
            SkipBinary = options.SkipBinary,
            ObeyGitignore = options.ObeyGitignore,
            GitignoreTakesPrecedence = options.GitignoreTakesPrecedence,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            MaxProcessMemoryBytes = options.MaxProcessMemoryBytes,
            MemoryPressurePercent = options.MemoryPressurePercent,
            SkipExtensions = options.SkipExtensions,
            SearchInsideArchives = options.SearchInsideArchives,
            ArchiveExtensions = options.ArchiveExtensions,
            SdkChannelBufferSize = options.SdkChannelBufferSize,
            ExcludeAdminProtectedPaths = options.ExcludeAdminProtectedPaths,
            AdminProtectedPathSegments = options.AdminProtectedPathSegments,
        };

    internal static List<string> ExtractExtensions(IReadOnlyList<string> includeGlobs)
    {
        var exts = new List<string>();
        foreach (var raw in includeGlobs ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Try to extract an extension if the pattern is a simple "*.ext" or "ext".
                var p = part;
                if (p.StartsWith("*.", StringComparison.Ordinal)) p = p[2..];
                if (p.StartsWith('.')) p = p[1..];
                if (p.Length > 0 && p.All(c => char.IsLetterOrDigit(c) || c == '_'))
                    exts.Add(p);
            }
        }
        return exts;
    }


    internal static void CollectForMemoryPressureIfDue(TimeSpan cooldown)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastMemoryPressureGcTicks);
        if (last != 0)
        {
            double secondsSinceLast = (double)(now - last) / Stopwatch.Frequency;
            if (secondsSinceLast < cooldown.TotalSeconds)
                return;
        }

        if (Interlocked.CompareExchange(ref s_memoryPressureGcInFlight, 1, 0) != 0)
            return;

        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            long managedHeap = GC.GetTotalMemory(false);
            LogService.Instance.Info("SearchService",
                $"GC diag: managedHeap={managedHeap / (1024*1024)}MB, committed={gcInfo.TotalCommittedBytes / (1024*1024)}MB, " +
                $"heapSize={gcInfo.HeapSizeBytes / (1024*1024)}MB, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}");
            LogService.Instance.Info("SearchService", "Requesting GC for memory pressure relief");
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            TrimProcessWorkingSet();
            Volatile.Write(ref s_lastMemoryPressureGcTicks, Stopwatch.GetTimestamp());
        }
        finally
        {
            Volatile.Write(ref s_memoryPressureGcInFlight, 0);
        }
    }

    /// <summary>
    /// Returns true when memory limits are exceeded.
    /// <paramref name="maxProcessBytes"/>: hard process working-set cap. When 0, auto-calculates
    /// a sub-GB working-set target so the process never runs uncapped.
    /// <paramref name="pressurePercent"/>: system-wide memory pressure threshold 0-100 (0 = disabled).
    /// </summary>

    internal static bool IsMemoryPressureHigh(long maxProcessBytes, int pressurePercent)
    {
        try
        {
            long effectiveCap = EffectiveProcessMemoryCap(maxProcessBytes);
            long ws = Environment.WorkingSet;
            bool hasSystemLoad = TryGetSystemMemoryLoadPercent(out var systemLoadPercent);
            var gcInfo = GC.GetGCMemoryInfo();
            return IsMemoryPressureHighForSnapshot(ws, effectiveCap,
                hasSystemLoad, systemLoadPercent, pressurePercent,
                gcInfo.MemoryLoadBytes, gcInfo.TotalAvailableMemoryBytes);
        }
        catch { return false; }
    }

    internal static bool IsMemoryPressureHighForSnapshot(
        long workingSet, long effectiveCap,
        bool hasSystemLoad, uint systemLoadPercent,
        int pressurePercent,
        long gcMemoryLoadBytes, long gcTotalAvailableBytes)
    {
        if (workingSet > effectiveCap) return true;

        if (pressurePercent > 0 && pressurePercent <= 100)
        {
            if (hasSystemLoad)
                return systemLoadPercent >= (uint)pressurePercent;

            double threshold = gcTotalAvailableBytes * (pressurePercent / 100.0);
            return gcMemoryLoadBytes > (long)threshold;
        }

        return false;
    }


    internal static bool IsMemoryPressureRelieved(long maxProcessBytes, int pressurePercent)
    {
        try
        {
            long effectiveCap = EffectiveProcessMemoryCap(maxProcessBytes);
            long workingSet = Environment.WorkingSet;
            if (pressurePercent > 0 && pressurePercent <= 100)
            {
                if (TryGetSystemMemoryLoadPercent(out var systemLoadPercent))
                {
                    return IsMemoryPressureRelievedForSnapshot(
                        workingSet, effectiveCap, systemLoadPercent,
                        pressurePercent, MemoryPressureRecoveryMarginPercent);
                }

                var info = GC.GetGCMemoryInfo();
                return IsMemoryPressureRelievedGcFallback(
                    workingSet, effectiveCap, pressurePercent,
                    MemoryPressureRecoveryMarginPercent,
                    info.MemoryLoadBytes, info.TotalAvailableMemoryBytes);
            }

            return IsMemoryPressureRelievedForSnapshot(
                workingSet, effectiveCap,
                systemMemoryLoadPercent: 0, pressurePercent: 0,
                MemoryPressureRecoveryMarginPercent);
        }
        catch { return false; }
    }

    internal static bool IsMemoryPressureRelievedGcFallback(
        long workingSetBytes, long effectiveProcessCapBytes,
        int pressurePercent, int recoveryMarginPercent,
        long gcMemoryLoadBytes, long gcTotalAvailableBytes)
    {
        bool processRelieved = IsProcessMemoryRelieved(workingSetBytes, effectiveProcessCapBytes);
        int reliefPercent = Math.Max(0, pressurePercent - recoveryMarginPercent);
        double reliefThreshold = gcTotalAvailableBytes * (reliefPercent / 100.0);
        return processRelieved && gcMemoryLoadBytes <= reliefThreshold;
    }

    internal static bool IsMemoryPressureRelievedForSnapshot(
        long workingSetBytes,
        long effectiveProcessCapBytes,
        uint systemMemoryLoadPercent,
        int pressurePercent,
        int recoveryMarginPercent)
    {
        if (!IsProcessMemoryRelieved(workingSetBytes, effectiveProcessCapBytes))
            return false;

        if (pressurePercent <= 0 || pressurePercent > 100)
            return true;

        int reliefPercent = Math.Max(0, pressurePercent - Math.Clamp(recoveryMarginPercent, 0, 100));
        return systemMemoryLoadPercent <= reliefPercent;
    }

    private static bool IsProcessMemoryRelieved(long workingSetBytes, long effectiveProcessCapBytes) =>
        effectiveProcessCapBytes <= 0 || workingSetBytes <= effectiveProcessCapBytes * ProcessMemoryRecoveryRatio;

    internal static int ResolveNativeBatchSize(int parallelism)
    {
        int workers = Math.Max(1, parallelism);
        return Math.Clamp(workers * 128, 1024, 4096);
    }

    internal static int ResolveNativeBatchTarget(int currentBatchTarget, bool memorySaving)
        => memorySaving ? MemorySavingNativeBatchSize : currentBatchTarget;

    internal static bool TryGetSystemMemoryLoadPercent(out uint systemLoadPercent)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            systemLoadPercent = status.dwMemoryLoad;
            return true;
        }

        systemLoadPercent = 0;
        return false;
    }

    // When the user has not set an explicit process cap (0), fall back to a
    // sub-GB auto-cap. This triggers disk paging based on Yagu's own working
    // set instead of waiting for machine-wide memory pressure on large-RAM hosts.
    private static long EffectiveProcessMemoryCap(long maxProcessBytes) =>
        maxProcessBytes > 0 ? maxProcessBytes : AutoProcessMemoryCap();

    /// <summary>Returns a human-readable snapshot of current memory usage for diagnostics.</summary>

    internal static string GetMemoryDiagnostics()
    {
        try
        {
            long wsMB = Environment.WorkingSet / (1024 * 1024);
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                long availMB = (long)(status.ullAvailPhys / (1024UL * 1024));
                long totalMB = (long)(status.ullTotalPhys / (1024UL * 1024));
                long autoCapMB = AutoProcessMemoryCap() / (1024 * 1024);
                return $"system={status.dwMemoryLoad}% ({availMB:N0}/{totalMB:N0} MB avail), process WS={wsMB:N0} MB, autoCap={autoCapMB:N0} MB";
            }
            return $"process WS={wsMB:N0} MB";
        }
        catch { return "unknown"; }
    }

    /// <summary>Auto-calculates a sub-GB process memory target for default searches.</summary>

    internal static long AutoProcessMemoryCap()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return ComputeAutoProcessMemoryCap(status.ullTotalPhys);
        }
        catch { }
        return AutoProcessMemoryCapFallback;
    }

    internal static long ComputeAutoProcessMemoryCap(ulong totalPhysicalBytes)
    {
        // ~25% of physical RAM, clamped to a sub-GB target. This is a paging
        // threshold, not a result limit: matches keep streaming and evicted
        // payloads move to the disk-backed ResultStore.
        long quarter = (long)(totalPhysicalBytes / 4);
        return Math.Clamp(quarter, AutoProcessMemoryCapFloor, AutoProcessMemoryCapCeiling);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // Debounce TrimProcessWorkingSet — Iter 14 profile showed 1420 inclusive samples
    // (2.1% of process CPU) from this called on every memory-pressure check. EmptyWorkingSet
    // walks the page table; back-to-back calls within the same pressure cycle do almost
    // nothing useful. Limit to once per 5 s across the whole process.
    private const long TrimMinIntervalTicks = 5L * 10_000_000L; // 5 s in 100-ns ticks (Stopwatch frequency is 10MHz on Windows)
    private static long s_lastTrimTicks;

    /// <summary>
    /// Trims the process working set, releasing soft-faulted pages (e.g. unmapped
    /// mmap regions) back to the OS. Pages still actively used will soft-fault
    /// back cheaply on next access. This is the primary mechanism for reducing WS
    /// when native memory (Rust mmap) dominates.
    /// </summary>
    internal static void TrimProcessWorkingSet()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastTrimTicks);
        if (last != 0 && (now - last) < TrimMinIntervalTicks)
            return;
        if (Interlocked.CompareExchange(ref s_lastTrimTicks, now, last) != last)
            return;
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(proc.Handle);
        }
        catch { /* best-effort */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;          // % of physical memory in use
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // ── Batch native scanning ──────────────────────────────────────

    /// <summary>
    /// Process a batch of files through the Rust parallel scanner, then update all
    /// stats counters. One cancel-int, one GCHandle, no Task.Run per file.
    /// </summary>

    internal static unsafe void ProcessNativeBatch(
        IReadOnlyList<string> batch,
        Native.NativeSession batchSession,
        Native.NativeSession? degradedSession,
        int parallelism,
        IntPtr cancelPtr,
        SearchOptions options,
        ChannelWriter<SearchResult> contentWriter,
        ref int filesScanned, ref int filesSkipped, ref int filesWithMatches,
        ref int totalMatches, ref long bytesScanned, ref int truncated,
        ref int degraded,
        ref int skipBinary, ref int skipAccessDenied, ref int skipIOError,
        ref int skipTooLarge, ref int skipNotFound, ref int skipOther)
    {
        var session = (Volatile.Read(ref degraded) != 0 && degradedSession != null)
            ? degradedSession
            : batchSession;

        fixed (int* filesScannedPtr = &filesScanned)
        {
        using var sink = new BatchScanSink(batch, contentWriter, options.MaxResults, Volatile.Read(ref totalMatches), cancelPtr, filesScannedPtr);

        Native.NativeSearcher.ScanPathsParallel(
            session, batch, parallelism, (int*)cancelPtr, sink);

        // Apply sink results back to the outer counters.
        if (sink.TotalEmitted > 0)
            Interlocked.Add(ref totalMatches, sink.TotalEmitted);
        if (sink.Truncated)
            Volatile.Write(ref truncated, 1);
        // Post-batch: reconcile per-file stats (filesScanned already incremented in OnFileDone).
        for (int i = 0; i < batch.Count; i++)
        {
            int status = sink.GetStatus(i);
            int emitted = sink.GetEmitted(i);

            if (status != Native.NativeSearcher.StatusOk)
            {
                Interlocked.Increment(ref filesSkipped);
                switch (status)
                {
                    case Native.NativeSearcher.StatusBinarySkipped:
                        Interlocked.Increment(ref skipBinary);
                        LogService.Instance.Verbose("ContentSearcher", $"Binary detected (batch native): {batch[i]}");
                        break;
                    case Native.NativeSearcher.StatusOpenFailed:
                        Interlocked.Increment(ref skipAccessDenied);
                        LogService.Instance.Verbose("ContentSearcher", $"Access denied (batch native): {batch[i]}");
                        break;
                    case Native.NativeSearcher.StatusTooLarge: Interlocked.Increment(ref skipTooLarge); break;
                    case Native.NativeSearcher.StatusInvalidPath: Interlocked.Increment(ref skipNotFound); break;
                    default: Interlocked.Increment(ref skipOther); break;
                }
            }
            else if (emitted > 0)
            {
                Interlocked.Increment(ref filesWithMatches);
                Interlocked.Add(ref bytesScanned, sink.GetFileLength(i));
            }
        }
        } // fixed (filesScannedPtr)
    }

    /// <summary>
    /// Sink for the batch native parallel scanner. The Rust side serialises callbacks
    /// under a mutex, so this does not need to be thread-safe.
    /// Per-file counters are rented from ArrayPool, and matches are written through
    /// the bounded result channel immediately so a single match-heavy file cannot
    /// accumulate an unbounded managed buffer before backpressure applies.
    /// </summary>

    internal sealed class BatchScanSink : Native.NativeSearcher.IParallelSink, IDisposable
    {
        private readonly IReadOnlyList<string> _paths;
        private readonly ChannelWriter<SearchResult> _writer;
        private readonly int _maxResults;
        private readonly int _count;
        private readonly int[] _emitted;
        private readonly int[] _statuses;
        private readonly long[] _fileLength;
        private readonly unsafe int* _cancelPtr; // Rust cancel flag — checked during backpressure waits
        private readonly unsafe int* _filesScannedPtr; // incremented per file for live progress
        private int _runningTotal; // starts from outer totalMatches at batch start
        private bool _stopped;

        public bool Truncated { get; private set; }
        public int TotalEmitted { get; private set; }
        public Exception? CapturedException { get; set; }
        public string? ErrorMessage { get; set; }

        public unsafe BatchScanSink(
            IReadOnlyList<string> paths,
            ChannelWriter<SearchResult> writer,
            int maxResults,
            int currentTotalMatches,
            IntPtr cancelPtr,
            int* filesScannedPtr)
        {
            _paths = paths;
            _writer = writer;
            _maxResults = maxResults;
            _count = paths.Count;
            _runningTotal = currentTotalMatches;
            _cancelPtr = (int*)cancelPtr;
            _filesScannedPtr = filesScannedPtr;
            _emitted = ArrayPool<int>.Shared.Rent(paths.Count);
            _statuses = ArrayPool<int>.Shared.Rent(paths.Count);
            _fileLength = ArrayPool<long>.Shared.Rent(paths.Count);
            Array.Clear(_emitted, 0, paths.Count);
            Array.Clear(_statuses, 0, paths.Count);
            Array.Clear(_fileLength, 0, paths.Count);
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_emitted);
            ArrayPool<int>.Shared.Return(_statuses);
            ArrayPool<long>.Shared.Return(_fileLength);
        }

        public int GetEmitted(int i) => _emitted[i];
        public int GetStatus(int i) => _statuses[i];
        public long GetFileLength(int i) => _fileLength[i];

        // IStreamingSink.OnMatch — not used in parallel path.
        public unsafe int OnMatch(Native.NativeSearcher.QgMatchView* m) => 1;

        public unsafe int OnMatchForFile(uint fileIndex, Native.NativeSearcher.QgMatchView* m)
        {
            if (_stopped) return 1;

            int idx = (int)fileIndex;
            string filePath = _paths[idx];

            var view = *m;
            int lineBytes = view.LineLen > (nuint)int.MaxValue ? int.MaxValue : (int)view.LineLen;
            int matchStartBytes = view.MatchStart > int.MaxValue ? lineBytes : (int)view.MatchStart;
            int matchLenBytes = view.MatchLen > int.MaxValue ? 0 : (int)view.MatchLen;
            var matchLine = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                view.LinePtr, lineBytes, matchStartBytes, matchLenBytes);
            var before = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(
                view.CtxBeforePtr, view.CtxBeforeBytes, view.CtxBeforeCount);
            var after = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(
                view.CtxAfterPtr, view.CtxAfterBytes, view.CtxAfterCount);

            int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;

            var result = new SearchResult(
                FilePath: filePath,
                LineNumber: lineNum,
                MatchLine: matchLine.Line,
                MatchStartColumn: matchLine.MatchStart,
                MatchLength: matchLine.MatchLength,
                ContextBefore: before,
                ContextAfter: after);

            if (!TryWriteWithBackpressure(result))
            {
                _stopped = true;
                return 1;
            }

            _emitted[idx]++;
            TotalEmitted++;
            _runningTotal++;
            if (_maxResults > 0 && _runningTotal >= _maxResults)
            {
                Truncated = true;
                _stopped = true;
                return 1;
            }
            return 0;
        }

        private unsafe bool TryWriteWithBackpressure(SearchResult result)
        {
            if (_writer.TryWrite(result))
                return true;

            var spinWait = new SpinWait();
            while (true)
            {
                if (_cancelPtr != null && Volatile.Read(ref *_cancelPtr) != 0)
                    return false;

                spinWait.SpinOnce(sleep1Threshold: 2);
                if (_writer.TryWrite(result))
                    return true;
            }
        }

        public void OnFileDone(uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
        {
            int idx = (int)fileIndex;
            _statuses[idx] = status;
            _fileLength[idx] = fileLength > long.MaxValue ? long.MaxValue : (long)fileLength;

            // Increment filesScanned immediately so the progress bar updates per-file
            // rather than waiting until the entire batch completes.
            unsafe
            {
                if (_filesScannedPtr != null)
                    Interlocked.Increment(ref *_filesScannedPtr);
            }

            // Pre-populate the metadata cache so FileGroup.BeginLoadMetadata
            // gets a synchronous hit and skips the secondary FileInfo syscall.
            if (status == Native.NativeSearcher.StatusOk && fileLength > 0 && lastModifiedFileTime > 0)
            {
                var lastMod = DateTime.FromFileTime((long)lastModifiedFileTime);
                var created = FileMetadataCache.TryGet(_paths[idx], out var cached) ? cached.Created : default;
                FileMetadataCache.Set(_paths[idx], new FileMetadata((long)fileLength, lastMod, created));
            }

        }
    }

    /// <summary>
    /// Sink for the streaming scanner. Unlike <see cref="BatchScanSink"/> which
    /// pre-allocates arrays for a known batch size, this grows dynamically as
    /// new paths are pushed to the scanner.
    /// </summary>
    internal sealed class StreamingScanSink : Native.NativeSearcher.IParallelSink, IDisposable
    {
        private readonly List<string> _paths; // shared reference; grows as paths pushed
        private readonly ChannelWriter<SearchResult> _writer;
        private readonly int _maxResults;
        private readonly unsafe int* _cancelPtr;
        private readonly unsafe int* _filesScannedPtr;
        private readonly unsafe int* _totalMatchesPtr;
        private readonly unsafe int* _filesWithMatchesPtr;
        private readonly ResultStore? _resultStore;
        private int _degraded; // volatile-accessed; toggled externally via SetDegraded
        private int[] _emitted;
        private int[] _statuses;
        private long[] _fileLength;
        private int _capacity;
        private readonly object _resizeLock = new();
        private int _runningTotal;
        private bool _stopped;

        public bool Truncated { get; private set; }
        public int TotalEmitted { get; private set; }
        public Exception? CapturedException { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>Enable/disable degraded fast-path (raw-to-disk, no String alloc).</summary>
        public void SetDegraded(bool value) => Volatile.Write(ref _degraded, value ? 1 : 0);

        public unsafe StreamingScanSink(
            List<string> paths,
            ChannelWriter<SearchResult> writer,
            int maxResults,
            int currentTotalMatches,
            IntPtr cancelPtr,
            int* filesScannedPtr,
            int* totalMatchesPtr,
            int* filesWithMatchesPtr,
            ResultStore? resultStore,
            int initialCapacity = 4096)
        {
            _paths = paths;
            _writer = writer;
            _maxResults = maxResults;
            _runningTotal = currentTotalMatches;
            _cancelPtr = (int*)cancelPtr;
            _filesScannedPtr = filesScannedPtr;
            _totalMatchesPtr = totalMatchesPtr;
            _filesWithMatchesPtr = filesWithMatchesPtr;
            _resultStore = resultStore;
            _capacity = initialCapacity;
            _emitted = new int[initialCapacity];
            _statuses = new int[initialCapacity];
            _fileLength = new long[initialCapacity];
        }

        public void Dispose() { /* arrays are GC-managed */ }

        private void EnsureCapacity(int index)
        {
            if (index < _capacity) return;
            lock (_resizeLock)
            {
                if (index < _capacity) return;
                int newCap = Math.Max(_capacity * 2, index + 1);
                var newEmitted = new int[newCap];
                var newStatuses = new int[newCap];
                var newFileLength = new long[newCap];
                Array.Copy(_emitted, newEmitted, _capacity);
                Array.Copy(_statuses, newStatuses, _capacity);
                Array.Copy(_fileLength, newFileLength, _capacity);
                _emitted = newEmitted;
                _statuses = newStatuses;
                _fileLength = newFileLength;
                _capacity = newCap;
            }
        }

        public int GetEmitted(int i) => i < _capacity ? _emitted[i] : 0;
        public int GetStatus(int i) => i < _capacity ? _statuses[i] : 0;
        public long GetFileLength(int i) => i < _capacity ? _fileLength[i] : 0;

        public unsafe int OnMatch(Native.NativeSearcher.QgMatchView* m) => 1;

        public unsafe int OnMatchForFile(uint fileIndex, Native.NativeSearcher.QgMatchView* m)
        {
            if (_stopped) return 1;
            int idx = (int)fileIndex;
            EnsureCapacity(idx);

            string filePath = idx < _paths.Count ? _paths[idx] : string.Empty;

            var view = *m;
            int lineBytes = view.LineLen > (nuint)int.MaxValue ? int.MaxValue : (int)view.LineLen;
            int matchStartBytes = view.MatchStart > int.MaxValue ? lineBytes : (int)view.MatchStart;
            int matchLenBytes = view.MatchLen > int.MaxValue ? 0 : (int)view.MatchLen;
            int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;

            // ── Degraded fast-path: write raw UTF-8 directly to disk, skip String alloc ──
            if (Volatile.Read(ref _degraded) != 0 && _resultStore != null)
            {
                // Truncate match line bytes the same way DecodeMatchLine would (window around match)
                int maxDisplayBytes = (LineTruncator.MaxDisplayLength + 1) * 4;
                ReadOnlySpan<byte> matchLineUtf8;
                int charMatchStart, charMatchLen;
                if (lineBytes <= maxDisplayBytes)
                {
                    matchLineUtf8 = new ReadOnlySpan<byte>(view.LinePtr, lineBytes);
                    // Convert byte offsets to char offsets so highlighting is correct after hydration.
                    int safeStart = Math.Min(matchStartBytes, lineBytes);
                    int safeLen = Math.Min(matchLenBytes, lineBytes - safeStart);
                    if (System.Text.Ascii.IsValid(matchLineUtf8[..Math.Min(safeStart + safeLen, lineBytes)]))
                    {
                        charMatchStart = safeStart;
                        charMatchLen = safeLen;
                    }
                    else
                    {
                        charMatchStart = Encoding.UTF8.GetCharCount(view.LinePtr, safeStart);
                        charMatchLen = Encoding.UTF8.GetCharCount(view.LinePtr + safeStart, safeLen);
                    }
                }
                else
                {
                    // Take a window around the match, same logic as DecodeMatchLine truncation
                    int windowBytes = Math.Max(matchLenBytes, maxDisplayBytes);
                    int contextBytes = Math.Max(0, (windowBytes - matchLenBytes) / 2);
                    int windowStart = Math.Max(0, matchStartBytes - contextBytes);
                    int windowEnd = Math.Min(lineBytes, windowStart + windowBytes);
                    int minEnd = Math.Min(lineBytes, matchStartBytes + matchLenBytes);
                    if (windowEnd < minEnd) windowEnd = minEnd;
                    if (windowEnd - windowStart < windowBytes)
                        windowStart = Math.Max(0, windowEnd - windowBytes);
                    // Align to UTF-8 boundaries
                    while (windowStart < lineBytes && (view.LinePtr[windowStart] & 0xC0) == 0x80)
                        windowStart++;
                    while (windowEnd > windowStart && windowEnd < lineBytes && (view.LinePtr[windowEnd] & 0xC0) == 0x80)
                        windowEnd--;
                    matchLineUtf8 = new ReadOnlySpan<byte>(view.LinePtr + windowStart, windowEnd - windowStart);
                    // Convert byte offsets within the window to char offsets.
                    int matchBytesFromWindow = Math.Max(0, matchStartBytes - windowStart);
                    int safeLenW = Math.Min(matchLenBytes, matchLineUtf8.Length - matchBytesFromWindow);
                    if (System.Text.Ascii.IsValid(matchLineUtf8[..Math.Min(matchBytesFromWindow + safeLenW, matchLineUtf8.Length)]))
                    {
                        charMatchStart = matchBytesFromWindow;
                        charMatchLen = safeLenW;
                    }
                    else
                    {
                        charMatchStart = Encoding.UTF8.GetCharCount(view.LinePtr + windowStart, matchBytesFromWindow);
                        charMatchLen = Encoding.UTF8.GetCharCount(view.LinePtr + matchStartBytes, safeLenW);
                    }
                }

                long offset = _resultStore.WriteRawUtf8(
                    matchLineUtf8,
                    view.CtxBeforePtr, (int)view.CtxBeforeBytes, (int)view.CtxBeforeCount,
                    view.CtxAfterPtr, (int)view.CtxAfterBytes, (int)view.CtxAfterCount);

                var result = SearchResult.CreatePreEvicted(filePath, lineNum, charMatchStart, charMatchLen, offset);
                if (!TryWriteWithBackpressure(result))
                {
                    _stopped = true;
                    return 1;
                }

                if (_emitted[idx]++ == 0 && _filesWithMatchesPtr != null)
                    Interlocked.Increment(ref *_filesWithMatchesPtr);
                TotalEmitted++;
                if (_totalMatchesPtr != null)
                    Interlocked.Increment(ref *_totalMatchesPtr);
                _runningTotal++;
                if (_maxResults > 0 && _runningTotal >= _maxResults)
                {
                    Truncated = true;
                    _stopped = true;
                    return 1;
                }
                return 0;
            }

            // ── Normal path: decode to managed strings ──
            var matchLine = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                view.LinePtr, lineBytes, matchStartBytes, matchLenBytes);
            var before = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(
                view.CtxBeforePtr, view.CtxBeforeBytes, view.CtxBeforeCount);
            var after = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(
                view.CtxAfterPtr, view.CtxAfterBytes, view.CtxAfterCount);

            var normalResult = new SearchResult(
                FilePath: filePath,
                LineNumber: lineNum,
                MatchLine: matchLine.Line,
                MatchStartColumn: matchLine.MatchStart,
                MatchLength: matchLine.MatchLength,
                ContextBefore: before,
                ContextAfter: after);

            if (!TryWriteWithBackpressure(normalResult))
            {
                _stopped = true;
                return 1;
            }

            if (_emitted[idx]++ == 0 && _filesWithMatchesPtr != null)
                Interlocked.Increment(ref *_filesWithMatchesPtr);
            TotalEmitted++;
            if (_totalMatchesPtr != null)
                Interlocked.Increment(ref *_totalMatchesPtr);
            _runningTotal++;
            if (_maxResults > 0 && _runningTotal >= _maxResults)
            {
                Truncated = true;
                _stopped = true;
                return 1;
            }
            return 0;
        }

        private unsafe bool TryWriteWithBackpressure(SearchResult result)
        {
            if (_writer.TryWrite(result))
                return true;

            var spinWait = new SpinWait();
            while (true)
            {
                if (_cancelPtr != null && Volatile.Read(ref *_cancelPtr) != 0)
                    return false;
                spinWait.SpinOnce(sleep1Threshold: 2);
                if (_writer.TryWrite(result))
                    return true;
            }
        }

        public void OnFileDone(uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
        {
            int idx = (int)fileIndex;
            EnsureCapacity(idx);

            _statuses[idx] = status;
            _fileLength[idx] = fileLength > long.MaxValue ? long.MaxValue : (long)fileLength;

            unsafe
            {
                if (_filesScannedPtr != null)
                    Interlocked.Increment(ref *_filesScannedPtr);
            }

            if (status == Native.NativeSearcher.StatusOk && fileLength > 0 && lastModifiedFileTime > 0)
            {
                string filePath = idx < _paths.Count ? _paths[idx] : string.Empty;
                if (!string.IsNullOrEmpty(filePath))
                {
                    var lastMod = DateTime.FromFileTime((long)lastModifiedFileTime);
                    var created = FileMetadataCache.TryGet(filePath, out var cached) ? cached.Created : default;
                    FileMetadataCache.Set(filePath, new FileMetadata((long)fileLength, lastMod, created));
                }
            }
        }
    }
}

/// <summary>Discriminated event returned by <see cref="SearchService.SearchAsync"/>.</summary>
public abstract record SearchEvent
{
    public sealed record Fallback(string Reason) : SearchEvent;
    public sealed record DiscoveryComplete(int TotalFiles) : SearchEvent;
    public sealed record Match(SearchResult Result) : SearchEvent;
    /// <summary>A batch of matches emitted together to amortize cross-thread / dispatcher cost
    /// when the producer is generating very high match rates (e.g. filename matches against
    /// millions of paths).</summary>
    public sealed record MatchBatch(IReadOnlyList<SearchResult> Results) : SearchEvent;
    public sealed record Progress(SearchProgress Snapshot) : SearchEvent;
    public sealed record Error(string Message) : SearchEvent;
    public sealed record Completed(SearchSummary Summary) : SearchEvent;
    /// <summary>Emitted when memory pressure triggers degradation. The consumer should evict heavy data to disk
    /// and then call <see cref="AcknowledgeEviction"/> with the count of results actually evicted. Workers keep
    /// scanning in memory-saving mode instead of waiting for system memory to fall below the threshold.</summary>
    public sealed record MemoryPressure(
        Action<int> AcknowledgeEviction,
        int ThresholdPercent = 0,
        string? Diagnostics = null) : SearchEvent;
    public sealed record MemoryPressureRelieved(string? Diagnostics = null) : SearchEvent;
}
