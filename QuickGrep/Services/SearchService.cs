using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using QuickGrep.Helpers;
using QuickGrep.Models;

namespace QuickGrep.Services;

public sealed class SearchService
{
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
                MaxFileSizeBytes = options.MaxFileSizeBytes,
                MaxResults = SearchOptions.MaxResultsCeiling,
                MaxMatchesPerFile = options.MaxMatchesPerFile,
                SkipBinary = options.SkipBinary,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                MaxProcessMemoryBytes = options.MaxProcessMemoryBytes,
                MemoryPressurePercent = options.MemoryPressurePercent,
                SkipExtensions = options.SkipExtensions,
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

        bool searchContent = options.SearchMode != SearchMode.FileNames;
        bool searchFileNames = options.SearchMode != SearchMode.Content;

        var events = Channel.CreateBounded<SearchEvent>(new BoundedChannelOptions(8192)
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
        int contentCap = options.MaxResults > 0 ? Math.Clamp(options.MaxResults, 1_000, 50_000) : 50_000;
        var contentResults = Channel.CreateBounded<SearchResult>(new BoundedChannelOptions(contentCap)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        int filesScanned = 0;
        int filesSkipped = 0;
        int filesWithMatches = 0;
        int totalMatches = 0;
        int totalDiscovered = 0;
        long bytesScanned = 0;
        int truncated = 0;
        int degraded = 0;            // 0 = normal, ≥1 = degraded (0 context, evicting to disk)
        int evictionInFlight = 0;    // 1 while an eviction event is being processed by the UI
        int lowYieldEvictions = 0;   // consecutive evictions that freed < 100 results
        int pressureCycles = 0;      // total number of memory pressure events emitted
        long lastLargeEvictionTicks = 0; // Stopwatch.GetTimestamp() of last eviction that freed >= 100
        var evictionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? fallbackReason = null;
        // Skip-reason tallies
        int skipBinary = 0, skipAccessDenied = 0, skipIOError = 0, skipTooLarge = 0;
        int skipNotFound = 0, skipEncoding = 0, skipOther = 0, skipByExtension = 0, skipDirectories = 0;

        int CurrentDirectorySkips() => Math.Max(Volatile.Read(ref skipDirectories), _fileLister.SkippedDirectories);
        int CurrentAccessDeniedSkips() => Volatile.Read(ref skipAccessDenied) + _fileLister.AccessDeniedDirectories;
        int CurrentFilesSkipped() => Volatile.Read(ref filesSkipped) + CurrentDirectorySkips();
        int CurrentTotalFiles()
        {
            int knownTotal = _fileLister.KnownTotalFiles;
            int discoveredTotal = Volatile.Read(ref totalDiscovered);
            int completedTotal = Volatile.Read(ref filesScanned);
            return Math.Max(knownTotal, Math.Max(discoveredTotal, completedTotal));
        }

        SearchProgress CreateProgressSnapshot() => new(
            Volatile.Read(ref filesScanned),
            CurrentTotalFiles(),
            Volatile.Read(ref totalMatches),
            Volatile.Read(ref filesWithMatches),
            CurrentFilesSkipped(),
            Volatile.Read(ref bytesScanned),
            sw.Elapsed,
            CurrentAccessDeniedSkips());

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
            try
            {
                await foreach (var path in _fileLister.ListFilesAsync(options.Directory, includeExts, maxFiles: 0, cancellationToken).WithCancellation(cancellationToken))
                {
                    if (Volatile.Read(ref truncated) != 0) break;
                    Interlocked.Increment(ref totalDiscovered);

                    if (!globMatcher.Matches(path))
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipOther);
                        continue;
                    }

                    if (searchFileNames)
                    {
                        var fileName = Path.GetFileName(path);
                        bool isMatch = regex is not null ? regex.IsMatch(fileName) : fileName.Contains(literal!, cmp);
                        if (isMatch)
                        {
                            (filenameBatch ??= new List<SearchResult>(FilenameBatchSize)).Add(new SearchResult(
                                FilePath: path, LineNumber: 0, MatchLine: fileName,
                                MatchStartColumn: 0, MatchLength: 0,
                                ContextBefore: [], ContextAfter: []));
                            if (filenameBatch.Count >= FilenameBatchSize)
                                await FlushFilenameBatchAsync().ConfigureAwait(false);
                            if (!searchContent)
                            {
                                int n = Interlocked.Increment(ref totalMatches);
                                if (options.MaxResults > 0 && n >= options.MaxResults) Volatile.Write(ref truncated, 1);
                            }
                        }
                    }

                    if (searchContent)
                    {
                        await pending.Writer.WriteAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        Interlocked.Increment(ref filesScanned);
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
            catch (OperationCanceledException) { LogService.Instance.Info("SearchService", "Discovery cancelled"); }
            catch (Exception ex) { LogService.Instance.Warning("SearchService", "Discovery failed", ex); }
            finally
            {
                LogService.Instance.Info("SearchService", $"Discovery finished: {Volatile.Read(ref totalDiscovered):N0} files discovered, total={CurrentTotalFiles():N0}, {sw.Elapsed.TotalSeconds:F2}s elapsed");
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
                    int parallelism = options.MaxDegreeOfParallelism > 0
                        ? options.MaxDegreeOfParallelism
                        // Default cap of 8: content scanning is mostly I/O-bound
                        // (mmap fault-ins) and 24-way fan-out on high-core boxes
                        // drove unmanaged working set above 22 GB without
                        // proportional throughput gains. ripgrep ships with a
                        // similar cap.
                        : Math.Max(1, Math.Min(8, Environment.ProcessorCount));
                    LogService.Instance.Info("SearchService", $"Content scan parallelism = {parallelism}");

                    // Create per-THREAD native sessions. Each thread compiles the
                    // regex once (instead of once per file). We use thread-local
                    // sessions rather than a single shared session because the regex
                    // crate's Regex type uses an internal thread-cache pool — sharing
                    // one Regex across many threads causes mutex contention on every
                    // find() call inside the hot line-scanning loop.
                    ThreadLocal<Native.NativeSession?>? sessionPool = null;
                    if (Native.NativeSearcher.IsAvailable)
                    {
                        sessionPool = new ThreadLocal<Native.NativeSession?>(
                            () => Native.NativeSearcher.CreateSession(options.Query, options),
                            trackAllValues: true);
                        LogService.Instance.Info("SearchService", "Thread-local native sessions enabled — pattern compiled once per thread");
                    }
                    // Pre-compute the degraded options once so we don't allocate a new
                    // SearchOptions per file inside the hot Parallel.ForEachAsync loop.
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
                        }
                        : options;

                    try
                    {
                    await Parallel.ForEachAsync(pending.Reader.ReadAllAsync(cancellationToken), new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = cancellationToken,
                    }, async (file, ct) =>
                    {
                        if (Volatile.Read(ref truncated) != 0) return;
                        bool watched = FileWatchDiagnostics.IsWatched(file);
                        var workerSw = watched ? Stopwatch.StartNew() : null;
                        if (watched) FileWatchDiagnostics.Checkpoint(file, "WORKER-DEQUEUE", extra: $"channelReady={contentResults.Reader.Count}");
                        // In degraded mode, search with 0 context lines to reduce memory for new results.
                        var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : options;
                        FileSearchOutcome outcome;
                        int produced;
                        try
                        {
                            outcome = await _searcher.SearchFileWithStatsAsync(file, regex, literal, cmp, effectiveOptions, contentResults.Writer, ct, sessionPool?.Value).ConfigureAwait(false);
                            produced = outcome.MatchCount;
                            if (watched) FileWatchDiagnostics.Checkpoint(file, "WORKER-RETURN", workerSw!.ElapsedMilliseconds, $"produced={produced}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // One bad file (e.g. native FFI corrupt buffer, IO error) must not kill the whole pool.
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
                            // Tally by skip reason
                            switch (produced)
                            {
                                case ContentSearcher.SkipBinary: Interlocked.Increment(ref skipBinary); break;
                                case ContentSearcher.SkipAccessDenied: Interlocked.Increment(ref skipAccessDenied); break;
                                case ContentSearcher.SkipIOError: Interlocked.Increment(ref skipIOError); break;
                                case ContentSearcher.SkipTooLarge: Interlocked.Increment(ref skipTooLarge); break;
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
                                {
                                    Volatile.Write(ref truncated, 1);
                                }
                            }
                        }

                        // Memory pressure check — every file, whether it matched or not.
                        if (IsMemoryPressureHigh(options.MaxProcessMemoryBytes, options.MemoryPressurePercent))
                        {
                            // Mark degraded (may already be) and signal UI to evict.
                            Volatile.Write(ref degraded, 1);

                            // Only emit one MemoryPressure event at a time; skip if one is already in flight.
                            if (Interlocked.CompareExchange(ref evictionInFlight, 1, 0) == 0)
                            {
                                int cycle = Interlocked.Increment(ref pressureCycles);
                                LogService.Instance.Warning("SearchService",
                                    $"Memory pressure cycle #{cycle}: {GetMemoryDiagnostics()} — signalling eviction (scanned={filesScanned:N0}, matches={totalMatches:N0})");
                                // Fresh signal for this eviction cycle.
                                evictionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                var currentSignal = evictionDone;
                                try
                                {
                                    await events.Writer.WriteAsync(
                                        new SearchEvent.MemoryPressure((evictedCount) =>
                                        {
                                            // Eviction ran on the UI thread. Do NOT block it with GC.
                                            // Instead, let workers handle GC on their own threads.
                                            if (evictedCount < 100)
                                            {
                                                // Grace period: after a large eviction the GC needs time
                                                // to reclaim freed .NET memory. Rapid-fire zero-yield
                                                // cycles during this window are expected, not a sign of
                                                // external pressure — don't count them toward truncation.
                                                long lastLarge = Volatile.Read(ref lastLargeEvictionTicks);
                                                double secondsSinceLargeEviction = lastLarge == 0
                                                    ? double.MaxValue
                                                    : (double)(Stopwatch.GetTimestamp() - lastLarge) / Stopwatch.Frequency;

                                                if (secondsSinceLargeEviction < 5.0)
                                                {
                                                    LogService.Instance.Info("SearchService",
                                                        $"Low-yield eviction: freed {evictedCount}, but within {secondsSinceLargeEviction:F1}s of large eviction — resetting counter");
                                                    Volatile.Write(ref lowYieldEvictions, 0);
                                                }
                                                else
                                                {
                                                    int consecutive = Interlocked.Increment(ref lowYieldEvictions);
                                                    LogService.Instance.Info("SearchService",
                                                        $"Low-yield eviction: freed {evictedCount}, consecutive={consecutive}");
                                                    if (consecutive >= 5)
                                                    {
                                                        LogService.Instance.Warning("SearchService",
                                                            $"Truncating: {consecutive} consecutive low-yield evictions (last freed {evictedCount}); memory pressure is external");
                                                        Volatile.Write(ref truncated, 1);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                Volatile.Write(ref lowYieldEvictions, 0);
                                                Volatile.Write(ref lastLargeEvictionTicks, Stopwatch.GetTimestamp());
                                            }
                                            LogService.Instance.Info("SearchService",
                                                $"Eviction acknowledged: freed {evictedCount} — signalling workers to resume");
                                            Volatile.Write(ref evictionInFlight, 0);
                                            currentSignal.TrySetResult();
                                        }),
                                        ct).ConfigureAwait(false);
                                }
                                catch
                                {
                                    Volatile.Write(ref evictionInFlight, 0);
                                    currentSignal.TrySetResult();
                                }
                            }

                            // Back-pressure: wait for eviction acknowledgment (up to 5s),
                            // then resume. Use Task.WaitAsync rather than Task.Delay so we
                            // don't allocate a fresh timer per worker per pressure cycle
                            // (was driving Active Timer count to ~980).
                            var pressureStart = sw.Elapsed;
                            try { await evictionDone.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                            catch (TimeoutException) { /* expected: eviction took longer than 5s */ }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* race: WaitAsync cancelled by completed task replacement */ }
                            catch (OperationCanceledException) { throw; }

                            // Don't force a per-worker GC. With many workers entering
                            // pressure simultaneously, back-to-back Gen2 collections were
                            // pushing time-in-GC to 92%. The eviction drops .NET references;
                            // let the runtime decide when to collect.

                            bool relieved = !IsMemoryPressureHigh(options.MaxProcessMemoryBytes, options.MemoryPressurePercent);
                            var pauseDuration = sw.Elapsed - pressureStart;
                            LogService.Instance.Info("SearchService",
                                $"Worker resumed after {pauseDuration.TotalSeconds:F1}s back-pressure, relieved={relieved}, {GetMemoryDiagnostics()}");
                        }
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
                catch (OperationCanceledException) { LogService.Instance.Info("SearchService", "Content workers cancelled"); }
                catch (Exception ex) { LogService.Instance.Warning("SearchService", "Content workers failed", ex); }
                finally
                {
                    LogService.Instance.Info("SearchService", $"Content workers finished: {filesScanned:N0} scanned, {filesWithMatches:N0} with matches, {totalMatches:N0} total matches, {Volatile.Read(ref pressureCycles)} pressure cycles, {sw.Elapsed.TotalSeconds:F2}s elapsed");
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
                await foreach (var r in contentResults.Reader.ReadAllAsync(cancellationToken))
                {
                    await events.Writer.WriteAsync(new SearchEvent.Match(r), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogService.Instance.Warning("SearchService", "Forwarder failed", ex); }
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
            if (Volatile.Read(ref truncated) != 0) break;
        }

        bool wasTruncated = Volatile.Read(ref truncated) != 0;
        bool wasDegraded = Volatile.Read(ref degraded) != 0;
        int totalFiles = CurrentTotalFiles();
        int directorySkips = CurrentDirectorySkips();
        int accessDeniedSkips = CurrentAccessDeniedSkips();
        int totalSkipped = Volatile.Read(ref filesSkipped) + directorySkips;
        int nonAccessDeniedDirectorySkips = Math.Max(0, directorySkips - _fileLister.AccessDeniedDirectories);
        var skipReasons = new SkipBreakdown(skipBinary, accessDeniedSkips, skipIOError, skipTooLarge, skipNotFound, skipEncoding, skipOther, skipByExtension, nonAccessDeniedDirectorySkips);
        LogService.Instance.Info("SearchService", $"Search complete: {totalMatches} matches in {filesWithMatches} files, {filesScanned} scanned, {totalSkipped} skipped ({skipReasons}), degraded={wasDegraded}, truncated={wasTruncated}, {sw.Elapsed.TotalSeconds:F2}s");
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

    private static List<string> ExtractExtensions(IReadOnlyList<string> includeGlobs)
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

    /// <summary>
    /// Returns true when memory limits are exceeded.
    /// <paramref name="maxProcessBytes"/>: hard process working-set cap. When 0, auto-calculates
    /// 25% of total physical RAM (min 2 GB) so the process never runs uncapped.
    /// <paramref name="pressurePercent"/>: system-wide memory pressure threshold 0-100 (0 = disabled).
    /// </summary>
    private static bool IsMemoryPressureHigh(long maxProcessBytes, int pressurePercent)
    {
        try
        {
            // Hard cap: stop if this process alone is consuming too much.
            // When the user sets 0 ("auto"), derive a cap from total physical RAM.
            long effectiveCap = maxProcessBytes;
            if (effectiveCap <= 0)
            {
                effectiveCap = AutoProcessMemoryCap();
            }
            {
                long ws = Environment.WorkingSet;
                if (ws > effectiveCap) return true;
            }

            // System-wide memory pressure via OS API (accurate, unlike GC.GetGCMemoryInfo
            // which only reflects the managed heap and misses native/channel/UI allocations).
            if (pressurePercent > 0 && pressurePercent <= 100)
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    return status.dwMemoryLoad >= (uint)pressurePercent;
                }
                // Fallback to GC info if P/Invoke fails.
                var info = GC.GetGCMemoryInfo();
                double threshold = info.TotalAvailableMemoryBytes * (pressurePercent / 100.0);
                return info.MemoryLoadBytes > (long)threshold;
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>Returns a human-readable snapshot of current memory usage for diagnostics.</summary>
    private static string GetMemoryDiagnostics()
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
    private static long AutoProcessMemoryCap()
    {
        const long minCap = 2L * 1024 * 1024 * 1024; // 2 GB floor
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                long quarter = (long)(status.ullTotalPhys / 4);
                return Math.Max(quarter, minCap);
            }
        }
        catch { }
        return 4L * 1024 * 1024 * 1024; // fallback: 4 GB
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
    /// and then call <see cref="AcknowledgeEviction"/> with the count of results actually evicted so the
    /// search engine can detect when eviction is no longer effective.</summary>
    public sealed record MemoryPressure(Action<int> AcknowledgeEviction) : SearchEvent;
}
