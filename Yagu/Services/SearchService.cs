using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Services;

public sealed class SearchService
{
    private const int MemoryPressureRecoveryMarginPercent = 5;
    private const double ProcessMemoryRecoveryRatio = 0.90;
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
            options = new SearchOptions
            {
                Directory = options.Directory,
                Query = options.Query,
                CaseSensitive = options.CaseSensitive,
                UseRegex = options.UseRegex,
                ContextLines = options.ContextLines,
                SearchMode = options.SearchMode,
                IncludeGlobs = options.IncludeGlobs,
                ExcludeGlobs = options.ExcludeGlobs,
                MinFileSizeBytes = options.MinFileSizeBytes,
                MaxFileSizeBytes = options.MaxFileSizeBytes,
                MaxResults = SearchOptions.MaxResultsCeiling,
                MaxMatchesPerFile = options.MaxMatchesPerFile,
                SkipBinary = options.SkipBinary,
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
        }

        var sw = Stopwatch.StartNew();
        FileMetadataCache.Clear();

        // Validate regex up front so the UI gets a clear error.
        Regex? regex = null;
        string? regexError = null;
        if (options.UseRegex)
        {
            RegexOptions regexOpts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (!options.CaseSensitive) regexOpts |= RegexOptions.IgnoreCase;
            try { regex = new Regex(options.Query, regexOpts); }
            catch (ArgumentException ex) { regexError = $"Invalid regex: {ex.Message}"; LogService.Instance.Warning("SearchService", regexError); }
        }
        if (regexError is not null)
        {
            yield return new SearchEvent.Error(regexError);
            yield return new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, sw.Elapsed, false, false, false, null));
            yield break;
        }
        var literal = options.UseRegex ? null : options.Query;
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
        var includeExts = ExtractExtensions(options.IncludeGlobs);
        var globMatcher = new GlobMatcher(options.IncludeGlobs, options.ExcludeGlobs);

        // Push skip settings to the lister so the Everything SDK path can
        // pre-filter by size/extension without per-file FileInfo calls.
        if (_fileLister is FileLister concreteLister)
        {
            concreteLister.EarlyMinFileSizeBytes = options.MinFileSizeBytes;
            concreteLister.EarlyMaxFileSizeBytes = options.MaxFileSizeBytes;

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

            concreteLister.EarlyExcludeGlobs = options.ExcludeGlobs;
            concreteLister.SdkChannelBufferSize = options.SdkChannelBufferSize;
            concreteLister.ExcludeAdminProtectedPaths = options.ExcludeAdminProtectedPaths;
            concreteLister.AdminProtectedPathSegmentsOverride = options.AdminProtectedPathSegments;
        }

        bool searchContent = options.SearchMode != SearchMode.FileNames;
        bool searchFileNames = options.SearchMode != SearchMode.Content;

        // Push the configurable archive-extension set to the searcher so it
        // can bypass extension-based skip for ZIP-like containers.
        _searcher.ZipLikeExtensions = options.ArchiveExtensions;

        var events = Channel.CreateBounded<SearchEvent>(new BoundedChannelOptions(2048)
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
        // consumer, preventing unbounded memory growth. The native FFI uses TryWrite
        // (stops scanning when full); the managed path uses WriteAsync (backpressure).
        // Channel buffer size — independent of total result limit.
        int contentCap = options.MaxResults > 0 ? Math.Clamp(options.MaxResults, 512, 16_384) : 16_384;
        var contentResults = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(contentCap)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        LogService.Instance.Warning("SearchService", $"Pipeline channels created: events=2048, pending=1024, contentResults={contentCap}");

        int filesScanned = 0;
        int filesSkipped = 0;
        int filesWithMatches = 0;
        int totalMatches = 0;
        int totalDiscovered = 0;
        long bytesScanned = 0;
        int truncated = 0;
        int degraded = 0;            // 0 = normal, ≥1 = actively degraded (0 context, evicting to disk)
        int everDegraded = 0;        // 1 once memory-saving mode was used during this search
        int evictionInFlight = 0;    // 1 while an eviction event is being processed by the UI
        int pressureCycles = 0;      // total number of memory pressure events emitted
        int nativeBatchesProcessed = 0; // total native batches flushed
        int contentChannelDrops = 0;    // times TryWrite to contentResults failed (channel full)
        long forwarderItemsForwarded = 0; // total results forwarded from contentResults → events
        long forwarderWriteStallMs = 0;   // cumulative ms the forwarder was blocked writing to events channel
        string? fallbackReason = null;
        // Skip-reason tallies
        int skipBinary = 0, skipAccessDenied = 0, skipIOError = 0, skipTooLarge = 0;
        int skipNotFound = 0, skipEncoding = 0, skipOther = 0, skipByExtension = 0, skipDirectories = 0;
        int skipGlobExcluded = 0;
        int skipSizeFiltered = 0;

        int CurrentDirectorySkips() => Math.Max(Volatile.Read(ref skipDirectories), _fileLister.SkippedDirectories);
        int CurrentAccessDeniedSkips() => Volatile.Read(ref skipAccessDenied) + _fileLister.AccessDeniedDirectories;
        int CurrentEarlySkips() => _fileLister.EarlySkippedFiles;
        int CurrentEarlyTooLargeSkips() => _fileLister.EarlySkippedTooLargeFiles;
        int CurrentFilesSkipped() => Volatile.Read(ref filesSkipped) + CurrentDirectorySkips() + CurrentEarlySkips();
        int CurrentTotalFiles()
        {
            int knownTotal = _fileLister.KnownTotalFiles;
            int earlySkips = CurrentEarlySkips();
            // Subtract early-skipped files so progress reflects only files that
            // actually enter the content pipeline.
            int effectiveKnown = Math.Max(0, knownTotal - earlySkips);
            int discoveredTotal = Volatile.Read(ref totalDiscovered);
            int completedTotal = Volatile.Read(ref filesScanned);
            return Math.Max(effectiveKnown, Math.Max(discoveredTotal, completedTotal));
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
                Volatile.Read(ref skipGlobExcluded));
            return new(
                Volatile.Read(ref filesScanned),
                CurrentTotalFiles(),
                Volatile.Read(ref totalMatches),
                Volatile.Read(ref filesWithMatches),
                CurrentFilesSkipped(),
                Volatile.Read(ref bytesScanned),
                sw.Elapsed,
                accessDenied,
                breakdown);
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

                    var waitToWrite = pending.Writer.WaitToWriteAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(waitToWrite, Task.Delay(25, cancellationToken)).ConfigureAwait(false);
                    if (completed == waitToWrite && !await waitToWrite.ConfigureAwait(false))
                        return;
                }
            }

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

                    if (_fileLister is not FileLister
                        && ShouldSkipByFileSize(path, options, out bool tooLarge))
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipSizeFiltered);
                        if (tooLarge)
                            Interlocked.Increment(ref skipTooLarge);
                        continue;
                    }

                    if (searchFileNames)
                    {
                        var fileName = Path.GetFileName(path);
                        int fnMatchStart = -1;
                        int fnMatchLen = 0;
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

                    if (searchContent)
                    {
                        await WritePendingFileAsync(path).ConfigureAwait(false);
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
                        // Native scanning overlaps file open/read work in Rust.
                        // ETL profiling shows 1,744 opens/sec with median disk
                        // latency 0.072ms — NVMe is far from saturated. Raising
                        // the cap to 64 (matching Rust MAX_WORKERS) lets more
                        // workers overlap I/O with scanning.
                        : nativeAvailable
                            ? Math.Max(1, Math.Min(64, Environment.ProcessorCount * 2))
                            : Math.Max(1, Math.Min(16, Environment.ProcessorCount));
                    LogService.Instance.Info("SearchService", $"Content scan parallelism = {parallelism}");

                    // Pre-compute the degraded options once so we don't allocate a new
                    // SearchOptions per file inside the hot loop.
                    var degradedOptions = options.ContextLines > 0
                        ? new SearchOptions
                        {
                            Directory = options.Directory,
                            Query = options.Query,
                            CaseSensitive = options.CaseSensitive,
                            UseRegex = options.UseRegex,
                            ContextLines = 0,
                            SearchMode = options.SearchMode,
                            IncludeGlobs = options.IncludeGlobs,
                            ExcludeGlobs = options.ExcludeGlobs,
                            MaxFileSizeBytes = options.MaxFileSizeBytes,
                            MaxResults = options.MaxResults,
                            MaxMatchesPerFile = options.MaxMatchesPerFile,
                            SkipBinary = options.SkipBinary,
                            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                            MaxProcessMemoryBytes = options.MaxProcessMemoryBytes,
                            MemoryPressurePercent = options.MemoryPressurePercent,
                            SkipExtensions = options.SkipExtensions,
                            SearchInsideArchives = options.SearchInsideArchives,
                        }
                        : options;

                    // Local function that captures the enclosing method's locals directly
                    // (avoids the C# restriction on capturing ref params in lambdas).
                    void CheckMemoryPressure()
                    {
                        if (IsMemoryPressureHigh(options.MaxProcessMemoryBytes, options.MemoryPressurePercent))
                        {
                            Volatile.Write(ref degraded, 1);
                            Volatile.Write(ref everDegraded, 1);

                            if (Interlocked.CompareExchange(ref evictionInFlight, 1, 0) == 0)
                            {
                                int cycle = Interlocked.Increment(ref pressureCycles);
                                string diagnostics = GetMemoryDiagnostics();
                                LogService.Instance.Warning("SearchService",
                                    $"Memory pressure cycle #{cycle}: {diagnostics} - shedding Yagu memory (scanned={filesScanned:N0}, matches={totalMatches:N0})");
                                try
                                {
                                    var memoryPressureEvent = new SearchEvent.MemoryPressure(
                                        (evictedCount) =>
                                        {
                                            LogService.Instance.Warning("SearchService",
                                                $"Eviction acknowledged: freed {evictedCount}; continuing in memory-saving mode");
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

                            CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(10));
                        }
                        else if (Volatile.Read(ref degraded) != 0 &&
                                 Volatile.Read(ref evictionInFlight) == 0 &&
                                 IsMemoryPressureRelieved(options.MaxProcessMemoryBytes, options.MemoryPressurePercent))
                        {
                            Volatile.Write(ref degraded, 0);
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

                    if (nativeAvailable)
                    {
                        // ── Batch native path ──
                        // Feed batches of paths to the Rust parallel scanner instead
                        // of one FFI call per file. Eliminates per-file Task.Run,
                        // GCHandle, semaphore, and cancel-int overhead.
                        Native.NativeSession? batchSession = null;
                        Native.NativeSession? degradedSession = null;
                        try
                        {
                            batchSession = Native.NativeSearcher.CreateSession(options.Query, options);
                            if (options.ContextLines > 0)
                                degradedSession = Native.NativeSearcher.CreateSession(options.Query, degradedOptions);

                            if (batchSession == null)
                            {
                                LogService.Instance.Warning("SearchService", "Native session creation failed — falling back to managed per-file path");
                                goto managedFallback;
                            }

                            int nativeBatchSize = ResolveNativeBatchSize(parallelism);
                            LogService.Instance.Info("SearchService", $"Batch native scanning enabled (batchSize={nativeBatchSize})");

                            // Single cancel-int shared across all batches.
                            IntPtr cancelPtr = Marshal.AllocHGlobal(sizeof(int));
                            try
                            {
                                unsafe { *(int*)cancelPtr = 0; }
                                using var ctr = cancellationToken.Register(static state =>
                                {
                                    unsafe { System.Threading.Interlocked.Exchange(ref *(int*)(IntPtr)state!, 1); }
                                }, cancelPtr);

                                var batch = new List<string>(nativeBatchSize);

                                // When archive search is enabled, zip files must go through the managed
                                // ContentSearcher (which knows how to open ZIPs) rather than the native
                                // Rust scanner (which would treat them as binary and skip them).
                                async Task ScanZipViaManagedAsync(string zipFile)
                                {
                                    var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : options;
                                    try
                                    {
                                        var outcome = await _searcher.SearchFileWithStatsAsync(
                                            zipFile, regex, literal, cmp, effectiveOptions,
                                            contentResults.Writer, cancellationToken, session: null).ConfigureAwait(false);
                                        int produced = outcome.MatchCount;

                                        // Count individual archive entries as files so the
                                        // files/sec metric stays meaningful during archive scans.
                                        // Fall back to 1 for non-archive files or errors.
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

                                var batchFlushSw = new Stopwatch();
                                long lastBatchLogTicks = Stopwatch.GetTimestamp();
                                const long BatchLogIntervalSec = 10;

                                void FlushNativeBatch()
                                {
                                    if (batch.Count == 0) return;
                                    int batchNum = Interlocked.Increment(ref nativeBatchesProcessed);
                                    int batchFileCount = batch.Count;
                                    batchFlushSw.Restart();
                                    ProcessNativeBatch(batch, batchSession, degradedSession, parallelism, cancelPtr,
                                        options, contentResults.Writer,
                                        ref filesScanned, ref filesSkipped, ref filesWithMatches, ref totalMatches,
                                        ref bytesScanned, ref truncated, ref degraded, ref contentChannelDrops,
                                        ref skipBinary, ref skipAccessDenied, ref skipIOError,
                                        ref skipTooLarge, ref skipNotFound, ref skipOther);
                                    batchFlushSw.Stop();
                                    CheckMemoryPressure();
                                    batch.Clear();

                                    // Periodic batch processing summary (every BatchLogIntervalSec seconds)
                                    long now = Stopwatch.GetTimestamp();
                                    if ((now - lastBatchLogTicks) >= Stopwatch.Frequency * BatchLogIntervalSec)
                                    {
                                        lastBatchLogTicks = now;
                                        string memDiag = GetMemoryDiagnostics();
                                        LogService.Instance.Warning("Workers",
                                            $"Batch #{batchNum}: {batchFileCount} files in {batchFlushSw.ElapsedMilliseconds}ms | " +
                                            $"scanned={Volatile.Read(ref filesScanned):N0}, matches={Volatile.Read(ref totalMatches):N0}, " +
                                            $"withMatches={Volatile.Read(ref filesWithMatches):N0}, skipped={Volatile.Read(ref filesSkipped):N0}, " +
                                            $"degraded={Volatile.Read(ref degraded) != 0}, channelDrops={Volatile.Read(ref contentChannelDrops)}, " +
                                            $"elapsed={sw.Elapsed.TotalSeconds:F1}s, {memDiag}");
                                    }
                                }

                                // Track in-flight ZIP tasks so they don't block native batching.
                                var zipTasks = new List<Task>();

                                while (Volatile.Read(ref truncated) == 0)
                                {
                                    while (batch.Count < nativeBatchSize && pending.Reader.TryRead(out var bufferedFile))
                                    {
                                        // Route ZIP archives to the managed searcher.
                                        // Use extension-based check (no I/O) instead of opening
                                        // every file for a 4-byte header peek — the old IsZipByHeader
                                        // approach consumed 24% of CPU and doubled file opens.
                                        // ContentSearcher still validates the ZIP header when it
                                        // opens the file, so false positives are harmless.
                                        if (options.SearchInsideArchives && ZipArchiveSearcher.HasZipExtension(bufferedFile, options.ArchiveExtensions))
                                        {
                                            zipTasks.Add(ScanZipViaManagedAsync(bufferedFile));
                                        }
                                        else
                                        {
                                            batch.Add(bufferedFile);
                                        }
                                    }

                                    if (batch.Count >= nativeBatchSize)
                                    {
                                        FlushNativeBatch();
                                        continue;
                                    }

                                    var waitToRead = pending.Reader.WaitToReadAsync(cancellationToken).AsTask();
                                    if (batch.Count > 0)
                                    {
                                        var completed = await Task.WhenAny(waitToRead, Task.Delay(NativePartialBatchFlushDelay)).ConfigureAwait(false);
                                        if (completed != waitToRead)
                                        {
                                            FlushNativeBatch();
                                            continue;
                                        }
                                    }

                                    if (!await waitToRead.ConfigureAwait(false))
                                        break;
                                }
                                // Process remaining files.
                                if (batch.Count > 0 && Volatile.Read(ref truncated) == 0)
                                {
                                    FlushNativeBatch();
                                }

                                // Wait for any in-flight ZIP searches to complete.
                                if (zipTasks.Count > 0)
                                {
                                    try { await Task.WhenAll(zipTasks).ConfigureAwait(false); }
                                    catch (OperationCanceledException) { }
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(cancelPtr);
                            }
                        }
                        finally
                        {
                            batchSession?.Dispose();
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
                                () => Native.NativeSearcher.CreateSession(options.Query, options),
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
                            var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : options;
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
                                    case ContentSearcher.SkipAccessDenied: Interlocked.Increment(ref skipAccessDenied); break;
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
                        $"channelDrops={Volatile.Read(ref contentChannelDrops)}, " +
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
                while (await contentResults.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (contentResults.Reader.TryRead(out var r))
                    {
                        (contentBatch ??= new List<SearchResult>(ContentBatchSize)).Add(r);
                        if (contentBatch.Count >= ContentBatchSize)
                            await FlushContentBatchAsync().ConfigureAwait(false);
                    }

                    // If we have a partial batch, wait briefly for more items before flushing.
                    if (contentBatch is { Count: > 0 })
                    {
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        delayCts.CancelAfter(PartialFlushDelayMs);
                        try
                        {
                            if (await contentResults.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                                continue; // More items available — loop back to drain them.
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Timeout expired, not a real cancellation — flush what we have.
                        }
                        await FlushContentBatchAsync().ConfigureAwait(false);
                    }
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
                while (!pipelineComplete.Task.IsCompleted &&
                       await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (pipelineComplete.Task.IsCompleted || Volatile.Read(ref truncated) != 0) break;
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
        var skipReasons = new SkipBreakdown(skipBinary, accessDeniedSkips, skipIOError, skipTooLarge + earlyTooLargeSkips, skipNotFound, skipEncoding, skipOther, skipByExtension, nonAccessDeniedDirectorySkips, earlySkips + discoverySizeSkips, skipGlobExcluded);
        LogService.Instance.Warning("SearchService", $"Search complete: {totalMatches} matches in {filesWithMatches} files, {filesScanned} scanned, {totalSkipped} skipped ({skipReasons}), earlyFiltered={earlySkips + discoverySizeSkips}, degraded={wasDegraded}, truncated={wasTruncated}, " +
            $"batches={Volatile.Read(ref nativeBatchesProcessed)}, pressureCycles={pressureCycles}, forwarderItems={Volatile.Read(ref forwarderItemsForwarded):N0}, forwarderStallMs={Volatile.Read(ref forwarderWriteStallMs)}, channelDrops={Volatile.Read(ref contentChannelDrops)}, {sw.Elapsed.TotalSeconds:F2}s");
        yield return new SearchEvent.Completed(new SearchSummary(
            TotalFiles: totalFiles,
            FilesScanned: filesScanned,
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

    private static bool ShouldSkipByFileSize(string path, SearchOptions options, out bool tooLarge)
    {
        tooLarge = false;
        long minBytes = Math.Max(0, options.MinFileSizeBytes);
        long maxBytes = Math.Max(0, options.MaxFileSizeBytes);
        if (minBytes == 0 && maxBytes == 0)
            return false;

        long length;
        if (FileMetadataCache.TryGet(path, out var cached))
        {
            length = cached.Length;
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

            length = fileInfo.Length;
            FileMetadataCache.Set(path, new FileMetadata(length, fileInfo.LastWriteTime, fileInfo.CreationTime));
        }

        if (minBytes > 0 && length < minBytes)
            return true;

        if (maxBytes > 0 && length > maxBytes)
        {
            tooLarge = true;
            return true;
        }

        return false;
    }

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
                if (p.StartsWith("*.")) p = p[2..];
                if (p.StartsWith(".")) p = p[1..];
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
            LogService.Instance.Info("SearchService", "Running coordinated GC for memory pressure relief");
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: false);
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
    /// 25% of total physical RAM (min 2 GB) so the process never runs uncapped.
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

    // When the user has not set an explicit process cap (0), rely solely on the
    // pressure-percent threshold.  The old auto-cap (25%/50% of RAM) fired
    // independently of pressure-percent and triggered memory-saving mode long
    // before the user's configured system-pressure threshold was reached.
    private static long EffectiveProcessMemoryCap(long maxProcessBytes) =>
        maxProcessBytes > 0 ? maxProcessBytes : long.MaxValue;

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

    /// <summary>Auto-calculates a process memory cap: 25% of total physical RAM, minimum 2 GB.</summary>

    internal static long AutoProcessMemoryCap()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return ComputeAutoProcessMemoryCap(status.ullTotalPhys);
        }
        catch { }
        return 4L * 1024 * 1024 * 1024; // fallback: 4 GB
    }

    internal static long ComputeAutoProcessMemoryCap(ulong totalPhysicalBytes)
    {
        const long minCap = 2L * 1024 * 1024 * 1024; // 2 GB floor
        long half = (long)(totalPhysicalBytes / 2);
        return Math.Max(half, minCap);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

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
        ref int degraded, ref int contentChannelDrops,
        ref int skipBinary, ref int skipAccessDenied, ref int skipIOError,
        ref int skipTooLarge, ref int skipNotFound, ref int skipOther)
    {
        var session = (Volatile.Read(ref degraded) != 0 && degradedSession != null)
            ? degradedSession
            : batchSession;

        using var sink = new BatchScanSink(batch, contentWriter, options.MaxResults, Volatile.Read(ref totalMatches), cancelPtr);

        Native.NativeSearcher.ScanPathsParallel(
            session, batch, parallelism, (int*)cancelPtr, sink);

        // Apply sink results back to the outer counters.
        if (sink.TotalEmitted > 0)
            Interlocked.Add(ref totalMatches, sink.TotalEmitted);
        if (sink.Truncated)
            Volatile.Write(ref truncated, 1);
        if (sink.ChannelFullDrops > 0)
        {
            Interlocked.Add(ref contentChannelDrops, sink.ChannelFullDrops);
            LogService.Instance.Warning("Workers",
                $"Content channel full: {sink.ChannelFullDrops} results could not be written (TryWrite failed). " +
                $"Scanner stopped for this batch. Total drops={Volatile.Read(ref contentChannelDrops)}");
        }

        // Post-batch: reconcile per-file stats.
        for (int i = 0; i < batch.Count; i++)
        {
            int status = sink.GetStatus(i);
            int emitted = sink.GetEmitted(i);

            Interlocked.Increment(ref filesScanned);

            if (status != Native.NativeSearcher.StatusOk)
            {
                Interlocked.Increment(ref filesSkipped);
                switch (status)
                {
                    case Native.NativeSearcher.StatusBinarySkipped: Interlocked.Increment(ref skipBinary); break;
                    case Native.NativeSearcher.StatusOpenFailed: Interlocked.Increment(ref skipAccessDenied); break;
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
    }

    /// <summary>
    /// Sink for the batch native parallel scanner. The Rust side serialises callbacks
    /// under a mutex, so this does not need to be thread-safe.
    /// Arrays are rented from ArrayPool to avoid per-batch allocations (ETL showed
    /// 112 MB of SearchResult[] + int[] churn). Results reuse the indexed path
    /// string from the input batch instead of hashing per match.
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
        // Per-file result buffers: accumulate matches for each file and flush them
        // atomically in OnFileDone so the channel never contains interleaved results
        // from different files (which would corrupt ripgrep-style grouped output).
        // The buffer array is pooled because this object is created for every batch.
        private readonly List<SearchResult>?[] _buffers;
        private readonly unsafe int* _cancelPtr; // Rust cancel flag — checked during backpressure waits
        private int _runningTotal; // starts from outer totalMatches at batch start
        private bool _stopped;

        public bool Truncated { get; private set; }
        public int TotalEmitted { get; private set; }
        public int ChannelFullDrops { get; private set; }
        public Exception? CapturedException { get; set; }
        public string? ErrorMessage { get; set; }

        public unsafe BatchScanSink(
            IReadOnlyList<string> paths,
            ChannelWriter<SearchResult> writer,
            int maxResults,
            int currentTotalMatches,
            IntPtr cancelPtr)
        {
            _paths = paths;
            _writer = writer;
            _maxResults = maxResults;
            _count = paths.Count;
            _runningTotal = currentTotalMatches;
            _cancelPtr = (int*)cancelPtr;
            _emitted = ArrayPool<int>.Shared.Rent(paths.Count);
            _statuses = ArrayPool<int>.Shared.Rent(paths.Count);
            _fileLength = ArrayPool<long>.Shared.Rent(paths.Count);
            _buffers = ArrayPool<List<SearchResult>?>.Shared.Rent(paths.Count);
            Array.Clear(_emitted, 0, paths.Count);
            Array.Clear(_statuses, 0, paths.Count);
            Array.Clear(_fileLength, 0, paths.Count);
            Array.Clear(_buffers, 0, paths.Count);
        }

        public void Dispose()
        {
            for (int i = 0; i < _count; i++)
                _buffers[i]?.Clear();
            Array.Clear(_buffers, 0, _count);
            ArrayPool<int>.Shared.Return(_emitted);
            ArrayPool<int>.Shared.Return(_statuses);
            ArrayPool<long>.Shared.Return(_fileLength);
            ArrayPool<List<SearchResult>?>.Shared.Return(_buffers);
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

            // Buffer the result; we write to the channel atomically in OnFileDone
            // so that all results for a file arrive contiguously and never interleave
            // with results from other files processed in parallel by the Rust scanner.
            (_buffers[idx] ??= new List<SearchResult>()).Add(result);

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

        public void OnFileDone(uint fileIndex, int status, ulong fileLength, ulong lastModifiedFileTime)
        {
            int idx = (int)fileIndex;
            _statuses[idx] = status;
            _fileLength[idx] = fileLength > long.MaxValue ? long.MaxValue : (long)fileLength;

            // Pre-populate the metadata cache so FileGroup.BeginLoadMetadata
            // gets a synchronous hit and skips the secondary FileInfo syscall.
            if (status == Native.NativeSearcher.StatusOk && fileLength > 0 && lastModifiedFileTime > 0)
            {
                var lastMod = DateTime.FromFileTime((long)lastModifiedFileTime);
                var created = FileMetadataCache.TryGet(_paths[idx], out var cached) ? cached.Created : default;
                FileMetadataCache.Set(_paths[idx], new FileMetadata((long)fileLength, lastMod, created));
            }

            // Flush this file's buffered results to the channel as a contiguous run.
            var buf = _buffers[idx];
            if (buf != null && !_stopped)
            {
                foreach (var r in buf)
                {
                    if (_writer.TryWrite(r))
                        continue;

                    // Channel full — apply backpressure. Spin-wait for the forwarder to
                    // drain space instead of silently dropping results. This runs on a
                    // thread-pool thread inside ProcessNativeBatch, so blocking is safe
                    // and produces the desired effect: the scanner slows to match the
                    // consumer's pace.
                    bool written = false;
                    var spinWait = new SpinWait();
                    while (true)
                    {
                        // Check for cancellation (Rust cancel flag) to avoid deadlock
                        // when the search is cancelled while we're waiting.
                        unsafe
                        {
                            if (_cancelPtr != null && Volatile.Read(ref *_cancelPtr) != 0)
                                break;
                        }
                        spinWait.SpinOnce(sleep1Threshold: 2);
                        if (_writer.TryWrite(r))
                        {
                            written = true;
                            break;
                        }
                    }
                    if (!written)
                    {
                        _stopped = true;
                        break;
                    }
                }
                buf.Clear(); // keep the list for potential reuse within this batch
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
