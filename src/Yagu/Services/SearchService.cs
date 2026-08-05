using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services.Logging;
using System.Runtime.CompilerServices;

namespace Yagu.Services;

public sealed class SearchService
{
    private const int MemoryPressureRecoveryMarginPercent = 5;
    private const double ProcessMemoryRecoveryRatio = 0.90;
    // How often (seconds) to log a machine-wide memory heartbeat while a search is active, so the log
    // carries a fine-grained WS + system-memory trail up to any native crash. 0.5s ≈ a few lines even on
    // a fast (multi-GB/s) memory balloon, without spamming ordinary sub-second searches.
    private const double MemoryHeartbeatSeconds = 0.5;
    private const int EventChannelCapacity = 64;
    private const int PendingFileChannelCapacity = 32_768;
    private const int UnlimitedContentResultChannelCapacity = 2_048;
    private const int SourceBackedResultChannelCapacity = 65_536;
    private const int MaxContentResultChannelCapacity = 4_096;
    private const long AutoProcessMemoryCapFloor = 512L * 1024 * 1024;
    private const long AutoProcessMemoryCapCeiling = 768L * 1024 * 1024;
    private const long AutoProcessMemoryCapFallback = AutoProcessMemoryCapCeiling;
    private const int MemorySavingNativeBatchSize = 256;
    private const int FutileEvictionCooldownSeconds = 5;
    private static readonly TimeSpan PeriodicMemoryPressureCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NativePartialBatchFlushDelay = TimeSpan.FromMilliseconds(100);

    private static int s_memoryPressureGcInFlight;
    private static long s_lastMemoryPressureGcTicks;
    private static readonly object s_trimLock = new();

    internal static ISystemMemoryProvider SystemMemoryProvider = new WindowsSystemMemoryProvider();
    internal static Action WorkingSetTrimmer = ProcessMemoryTrimmer.TrimCurrentProcess;

    private readonly IFileLister _fileLister;
    private readonly ContentSearcher _searcher;

    public SearchService() : this(new FileLister(), new ContentSearcher()) { }
    public SearchService(IFileLister fileLister, ContentSearcher searcher)
    {
        _fileLister = fileLister;
        _searcher = searcher;
    }

    /// <summary>
    /// Runs one logical search across several root directories (the "search all drives" case),
    /// streaming a single unified event sequence. Each element of <paramref name="perRootOptions"/>
    /// is a fully-built <see cref="SearchOptions"/> for one root — crucially allowing a different
    /// <see cref="SearchOptions.MaxDegreeOfParallelism"/> per root (e.g. 1 for an HDD while SSDs use
    /// the configured value). Roots are scanned sequentially so the configured parallelism is not
    /// multiplied across drives. Intermediate <see cref="SearchEvent.ScanCompleted"/> and
    /// <see cref="SearchEvent.Completed"/> events are suppressed and replaced by a single aggregated
    /// pair at the end; <see cref="SearchEvent.DiscoveryComplete"/> totals are accumulated. The first
    /// root's <see cref="SearchOptions.MaxResults"/> acts as the global cap: once reached, no further
    /// roots are started.
    /// </summary>
    public IAsyncEnumerable<SearchEvent> SearchManyAsync(
        IReadOnlyList<SearchOptions> perRootOptions,
        CancellationToken cancellationToken)
    {
        return CanPrioritizeNameMatchesAcrossRoots(perRootOptions)
            ? PrioritizeNameMatchesAcrossRootsAsync(perRootOptions, SearchAsync, cancellationToken)
            : AggregateManyAsync(perRootOptions, SearchAsync, cancellationToken);
    }

    internal bool CanPrioritizeNameMatchesAcrossRoots(IReadOnlyList<SearchOptions> perRootOptions) =>
        perRootOptions is { Count: > 1 }
            && _fileLister is FileLister
            && FileLister.SdkAvailable
            && perRootOptions.Any(IsNameFirstQueryEligible);

    /// <summary>
    /// True when an Everything filename query can safely represent this search's literal name predicate.
    /// Regex and operator-bearing/whitespace terms fall back to the normal complete discovery path.
    /// </summary>
    internal static bool IsNameFirstBackendEligible(FileListerBackend? backend) =>
        backend is null or FileListerBackend.Auto or FileListerBackend.EverythingSdk;

    private static bool IsNameFirstQueryEligible(SearchOptions options)
    {
        if (options.SearchMode is not (SearchMode.Both or SearchMode.FileNames)
            || options.UseRegex
            || !IsNameFirstBackendEligible(options.FileListerBackendOverride))
            return false;

        IReadOnlyList<string> terms = SearchQueryParser.ParseLiteralTerms(options.Query, options.ExactMatch);
        return terms.Count > 0 && FileLister.BuildEverythingFileNameFilter(terms) is not null;
    }

    /// <summary>
    /// All-drives two-phase search. Phase 1 performs the fast Everything name query for EVERY eligible
    /// root before any full content sweep. In Both mode each name-hit path is also content-scanned by that
    /// prepass, so its existing filename-only group is upgraded with content matches immediately. Phase 2
    /// runs the normal roots sequentially (preserving the no-parallel-drives I/O contract), suppressing the
    /// already-emitted filename rows and already-scanned priority paths.
    /// </summary>
    internal static async IAsyncEnumerable<SearchEvent> PrioritizeNameMatchesAcrossRootsAsync(
        IReadOnlyList<SearchOptions> perRootOptions,
        Func<SearchOptions, CancellationToken, IAsyncEnumerable<SearchEvent>> runRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (perRootOptions.Count == 0)
        {
            yield return new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null));
            yield break;
        }

        var overall = Stopwatch.StartNew();
        int hardCap = EffectiveHardCap(perRootOptions[0]);
        int priorityMatches = 0;
        long priorityBytesScanned = 0;
        bool priorityDegraded = false;
        bool priorityCancelled = false;
        bool priorityTruncated = false;
        var priorityMatchFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preEmittedFileNamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preScannedContentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namePathsByRoot = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string? recordingNameRoot = null;

        bool RecordResult(SearchResult result)
        {
            if (hardCap > 0 && priorityMatches >= hardCap)
            {
                priorityTruncated = true;
                return false;
            }

            priorityMatches++;
            priorityMatchFiles.Add(result.FilePath);
            if (result.LineNumber == 0)
            {
                preEmittedFileNamePaths.Add(result.FilePath);
                if (recordingNameRoot is not null)
                {
                    if (!namePathsByRoot.TryGetValue(recordingNameRoot, out HashSet<string>? paths))
                    {
                        paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        namePathsByRoot[recordingNameRoot] = paths;
                    }
                    paths.Add(result.FilePath);
                }
            }
            return true;
        }

        async IAsyncEnumerable<SearchEvent> RunPriorityPass(
            SearchOptions passOptions,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (SearchEvent ev in runRoot(passOptions, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case SearchEvent.Match m when RecordResult(m.Result):
                        yield return ev;
                        break;
                    case SearchEvent.Match:
                        break;
                    case SearchEvent.MatchBatch mb:
                    {
                        var accepted = new List<SearchResult>(mb.Results.Count);
                        foreach (SearchResult result in mb.Results)
                        {
                            if (!RecordResult(result)) break;
                            accepted.Add(result);
                        }
                        if (accepted.Count > 0)
                            yield return new SearchEvent.MatchBatch(accepted);
                        break;
                    }
                    case SearchEvent.SourceBackedMatchBatch sb:
                    {
                        // Source-backed matches are content matches. A priority name row for the same path
                        // was already recorded before the path was queued, so only cap/count and forward here.
                        var accepted = new List<SourceBackedMatch>(sb.Results.Count);
                        foreach (SourceBackedMatch result in sb.Results)
                        {
                            if (hardCap > 0 && priorityMatches >= hardCap)
                            {
                                priorityTruncated = true;
                                break;
                            }
                            priorityMatches++;
                            priorityMatchFiles.Add(result.FilePath);
                            accepted.Add(result);
                        }
                        if (accepted.Count > 0)
                            yield return new SearchEvent.SourceBackedMatchBatch(accepted);
                        break;
                    }
                    case SearchEvent.Completed c:
                        priorityBytesScanned += c.Summary.BytesScanned;
                        priorityDegraded |= c.Summary.Degraded;
                        priorityCancelled |= c.Summary.Cancelled;
                        priorityTruncated |= c.Summary.Truncated;
                        break;
                    case SearchEvent.ScanCompleted or SearchEvent.DiscoveryComplete or SearchEvent.Progress or SearchEvent.Fallback:
                        break;
                    default:
                        yield return ev;
                        break;
                }
            }
        }

        // Phase 1A: filename results from EVERY drive first. SearchMode.FileNames uses the Everything-only
        // fast path and cannot be held up by content reads or index initialization.
        foreach (SearchOptions rootOptions in perRootOptions)
        {
            if (cancellationToken.IsCancellationRequested || (hardCap > 0 && priorityMatches >= hardCap))
                break;
            if (!IsNameFirstQueryEligible(rootOptions))
                continue;

            int remaining = hardCap > 0 ? Math.Max(1, hardCap - priorityMatches) : rootOptions.MaxResults;
            SearchOptions nameOptions = CopyOptions(
                rootOptions,
                maxResults: remaining,
                searchMode: SearchMode.FileNames,
                useContentIndex: false,
                stopAfterNameFirstPass: true,
                suppressNameFirstPass: false,
                preEmittedFileNamePaths: null,
                preScannedContentPaths: null);
            recordingNameRoot = rootOptions.Directory;
            await foreach (SearchEvent ev in RunPriorityPass(nameOptions, cancellationToken).ConfigureAwait(false))
                yield return ev;
            recordingNameRoot = null;
        }

        bool filenameOnly = perRootOptions.All(options => options.SearchMode == SearchMode.FileNames);
        bool hardCapReached = hardCap > 0 && priorityMatches >= hardCap;
        if (filenameOnly || hardCapReached || priorityTruncated || cancellationToken.IsCancellationRequested)
        {
            var summary = new SearchSummary(
                TotalFiles: preEmittedFileNamePaths.Count,
                FilesScanned: filenameOnly ? preEmittedFileNamePaths.Count : preScannedContentPaths.Count,
                FilesSkipped: 0,
                FilesWithMatches: priorityMatchFiles.Count,
                TotalMatches: priorityMatches,
                BytesScanned: priorityBytesScanned,
                Elapsed: overall.Elapsed,
                Cancelled: priorityCancelled || cancellationToken.IsCancellationRequested,
                Truncated: hardCapReached || priorityTruncated,
                Degraded: priorityDegraded,
                FallbackReason: null);
            yield return new SearchEvent.ScanCompleted(summary);
            yield return new SearchEvent.Completed(summary);
            yield break;
        }

        // Phase 1B: now that filename rows from every drive are visible, content-scan only those
        // name-hit files. FileNameThenContent pushes the same literal name predicate into Everything,
        // emits no duplicate filename rows, and completes before the sequential full sweeps start.
        foreach (SearchOptions rootOptions in perRootOptions)
        {
            if (cancellationToken.IsCancellationRequested || (hardCap > 0 && priorityMatches >= hardCap))
                break;
            if (rootOptions.SearchMode != SearchMode.Both
                || !namePathsByRoot.TryGetValue(rootOptions.Directory, out HashSet<string>? namePaths))
                continue;

            int remaining = hardCap > 0 ? Math.Max(1, hardCap - priorityMatches) : rootOptions.MaxResults;
            SearchOptions contentPriorityOptions = CopyOptions(
                rootOptions,
                maxResults: remaining,
                searchMode: SearchMode.FileNameThenContent,
                useContentIndex: false,
                stopAfterNameFirstPass: false,
                suppressNameFirstPass: true,
                preEmittedFileNamePaths: preEmittedFileNamePaths,
                preScannedContentPaths: null);
            await foreach (SearchEvent ev in RunPriorityPass(contentPriorityOptions, cancellationToken).ConfigureAwait(false))
                yield return ev;

            // That narrowed pass has completed; the later full sweep can safely skip these paths.
            preScannedContentPaths.UnionWith(namePaths);
        }

        int remainingForFull = hardCap > 0 ? Math.Max(1, hardCap - priorityMatches) : perRootOptions[0].MaxResults;
        var fullOptions = new List<SearchOptions>(perRootOptions.Count);
        foreach (SearchOptions rootOptions in perRootOptions)
        {
            fullOptions.Add(IsNameFirstQueryEligible(rootOptions)
                ? CopyOptions(
                    rootOptions,
                    maxResults: remainingForFull,
                    suppressNameFirstPass: true,
                    preEmittedFileNamePaths: preEmittedFileNamePaths,
                    preScannedContentPaths: preScannedContentPaths)
                : rootOptions);
        }

        SearchSummary AugmentSummary(SearchSummary summary)
            => summary with
            {
                FilesWithMatches = Math.Min(summary.TotalFiles, summary.FilesWithMatches + priorityMatchFiles.Count),
                TotalMatches = summary.TotalMatches > int.MaxValue - priorityMatches
                    ? int.MaxValue
                    : summary.TotalMatches + priorityMatches,
                BytesScanned = summary.BytesScanned > long.MaxValue - priorityBytesScanned
                    ? long.MaxValue
                    : summary.BytesScanned + priorityBytesScanned,
                Elapsed = overall.Elapsed,
                Cancelled = summary.Cancelled || priorityCancelled,
                Truncated = summary.Truncated || priorityTruncated,
                Degraded = summary.Degraded || priorityDegraded,
            };

        await foreach (SearchEvent ev in AggregateManyAsync(fullOptions, runRoot, cancellationToken).ConfigureAwait(false))
        {
            if (ev is SearchEvent.ScanCompleted scanCompleted)
                yield return new SearchEvent.ScanCompleted(AugmentSummary(scanCompleted.Summary));
            else if (ev is SearchEvent.Completed completed)
                yield return new SearchEvent.Completed(AugmentSummary(completed.Summary));
            else
                yield return ev;
        }
    }

    /// <summary>
    /// Pure orchestration core for <see cref="SearchManyAsync"/>: aggregates the per-root event
    /// streams produced by <paramref name="runRoot"/> into one unified sequence. Decoupled from the
    /// real search pipeline (which <see cref="SearchManyAsync"/> injects) so every branch — the
    /// count==0 / count==1 fast paths, the per-event forwarding/suppression, the global
    /// <see cref="SearchOptions.MaxResults"/> cap, and cancellation — is unit-testable with a
    /// synthetic event stream.
    /// </summary>
    internal static async IAsyncEnumerable<SearchEvent> AggregateManyAsync(
        IReadOnlyList<SearchOptions> perRootOptions,
        Func<SearchOptions, CancellationToken, IAsyncEnumerable<SearchEvent>> runRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (perRootOptions is null || perRootOptions.Count == 0)
        {
            yield return new SearchEvent.Completed(new SearchSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero, false, false, false, null));
            yield break;
        }

        // A single root behaves exactly like the normal pipeline — delegate verbatim so the
        // heavily-tested single-root path is untouched.
        if (perRootOptions.Count == 1)
        {
            await foreach (var ev in runRoot(perRootOptions[0], cancellationToken).ConfigureAwait(false))
                yield return ev;
            yield break;
        }

        var sw = Stopwatch.StartNew();
        int cap = EffectiveHardCap(perRootOptions[0]); // > 0 = global cap; <= 0 = unlimited (both MaxResults and AbsoluteMaxResults disabled).
        long forwardedMatches = 0;
        int totalFiles = 0, filesScanned = 0, filesSkipped = 0, filesWithMatches = 0, totalMatches = 0;
        long bytesScanned = 0;
        bool truncated = false, degraded = false, cancelled = false;
        string? fallbackReason = null;
        int idxRequestedRoots = 0, idxAcceleratedRoots = 0, idxFilesPruned = 0, idxFilesRescued = 0;

        foreach (var rootOptions in perRootOptions)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

            await foreach (var ev in runRoot(rootOptions, cancellationToken).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case SearchEvent.DiscoveryComplete dc:
                        totalFiles += dc.TotalFiles;
                        yield return new SearchEvent.DiscoveryComplete(totalFiles);
                        break;
                    case SearchEvent.ScanCompleted:
                        break; // suppressed; aggregated below
                    case SearchEvent.Completed c:
                        var s = c.Summary;
                        filesScanned += s.FilesScanned;
                        filesSkipped += s.FilesSkipped;
                        filesWithMatches += s.FilesWithMatches;
                        totalMatches += s.TotalMatches;
                        bytesScanned += s.BytesScanned;
                        truncated |= s.Truncated;
                        degraded |= s.Degraded;
                        cancelled |= s.Cancelled;
                        fallbackReason ??= s.FallbackReason;
                        if (s.IndexAcceleration is { } ia)
                        {
                            idxRequestedRoots += ia.RequestedRoots;
                            idxAcceleratedRoots += ia.AcceleratedRoots;
                            idxFilesPruned += ia.FilesPruned;
                            idxFilesRescued += ia.FilesRescued;
                        }
                        break;
                    case SearchEvent.Fallback:
                        // Per-root fallback notices (e.g. one empty drive reporting "Everything SDK
                        // returned no results") must NOT surface mid-stream while other roots are
                        // producing results — that made the warning appear next to a full result set.
                        // Suppress them here; the aggregated reason comes from the per-root Completed
                        // summaries and is only re-emitted at the end if the whole search found nothing.
                        break;
                    case SearchEvent.Match:
                        forwardedMatches++;
                        yield return ev;
                        break;
                    case SearchEvent.MatchBatch mb:
                        forwardedMatches += mb.Results.Count;
                        yield return ev;
                        break;
                    case SearchEvent.SourceBackedMatchBatch sb:
                        forwardedMatches += sb.Results.Count;
                        yield return ev;
                        break;
                    default:
                        yield return ev;
                        break;
                }
            }

            if (cap > 0 && forwardedMatches >= cap) { truncated = true; break; }
        }

        // Only surface a fallback notice for the whole multi-root run when it produced no results at
        // all. When any root returned matches, a single empty drive's "no results" reason is noise.
        if (forwardedMatches == 0 && fallbackReason is not null)
            yield return new SearchEvent.Fallback(fallbackReason);

        var summary = new SearchSummary(
            totalFiles, filesScanned, filesSkipped, filesWithMatches, totalMatches,
            bytesScanned, sw.Elapsed, cancelled, truncated, degraded, fallbackReason,
            IndexAcceleration: idxRequestedRoots > 0
                ? new IndexAccelerationInfo(idxRequestedRoots, idxAcceleratedRoots, idxFilesPruned, idxFilesRescued)
                : null);
        yield return new SearchEvent.ScanCompleted(summary);
        yield return new SearchEvent.Completed(summary);
    }

    internal static async IAsyncEnumerable<SearchEvent> DrainRemainingEventsAsync(
        ChannelReader<SearchEvent> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (SearchEvent searchEvent in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return searchEvent;
    }

    /// <summary>
    /// Stream search results. Caller iterates the channel; the returned task completes
    /// when all files are scanned, the search is cancelled, or the result cap is hit.
    /// </summary>

    public async IAsyncEnumerable<SearchEvent> SearchAsync(
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
            try { regex = SearchRegexFactory.Build(options.Query, options); }
            catch (ArgumentException ex) { regexError = $"Invalid regex: {ex.Message}"; YaguLog.For("SearchService").LogWarning("Invalid regex: {Error}", ex.Message); }
        }
        else
        {
            literalTerms = SearchQueryParser.ParseLiteralTerms(options.Query, options.ExactMatch);
            if (options.ExactMatch)
            {
                // Whole-word match: wrap the literal query in word boundaries and
                // run it through the regex path so the native and managed scanners
                // agree. "async" matches the word "async" but not "asynchronously".
                string wordPattern = SearchQueryParser.BuildLiteralRegexPattern(options.Query, exactMatch: true)!;
                regex = SearchRegexFactory.Build(wordPattern, options);
                patternOptions = CopyOptions(options, query: wordPattern, useRegex: true);
            }
            else if (literalTerms.Count > 1)
            {
                string alternation = SearchQueryParser.BuildLiteralAlternation(literalTerms);
                regex = SearchRegexFactory.Build(alternation, options);
                patternOptions = CopyOptions(options, query: alternation, useRegex: true);
            }
            else if (options.Multiline)
            {
                // Multiline: a bare literal must be PROMOTED to an escaped regex and run through
                // the multiline engine, or it silently falls back to line matching (§4/§11).
                string literalPattern = Regex.Escape(literalTerms[0]);
                regex = SearchRegexFactory.Build(literalPattern, options);
                patternOptions = CopyOptions(options, query: literalPattern, useRegex: true);
            }
            else
            {
                literal = literalTerms[0];
                patternOptions = CopyOptions(options, query: literal);
            }
        }
        if (regexError is not null)
        {
            yield return new SearchEvent.SearchError(regexError);
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
            // can open them as archives. Likewise, when image-text (OCR) search is
            // enabled, don't skip image extensions — they need to reach the OCR queue.
            // Same for PDF-text search: don't skip "pdf" so PDFs reach the extraction queue.
            var skipExts = options.SkipExtensions;
            bool bypassArchives = options.SearchInsideArchives && skipExts.Count > 0 && options.ArchiveExtensions.Count > 0;
            bool bypassImages = options.SearchImageText && skipExts.Count > 0 && options.ImageOcrExtensions.Count > 0;
            bool bypassPdfs = options.SearchPdfText && skipExts.Count > 0 && options.PdfTextExtensions.Count > 0;
            if (bypassArchives || bypassImages || bypassPdfs)
            {
                var filtered = new HashSet<string>(skipExts, StringComparer.OrdinalIgnoreCase);
                // ArchiveExtensions uses ".zip" format; SkipExtensions uses "zip" (no dot).
                if (bypassArchives)
                    foreach (var ext in options.ArchiveExtensions)
                        filtered.Remove(ext.TrimStart('.'));
                // ImageOcrExtensions uses dotless format, matching SkipExtensions.
                if (bypassImages)
                    foreach (var ext in options.ImageOcrExtensions)
                        filtered.Remove(ext.TrimStart('.'));
                // PdfTextExtensions uses dotless format, matching SkipExtensions.
                if (bypassPdfs)
                    foreach (var ext in options.PdfTextExtensions)
                        filtered.Remove(ext.TrimStart('.'));
                skipExts = filtered;
            }
            concreteLister.EarlySkipExtensions = skipExts;

            concreteLister.EarlyExcludeGlobs = options.ExcludeFilterMode == FilterPatternMode.GlobPath
                ? options.ExcludeGlobs
                : Array.Empty<string>();
            concreteLister.EarlyIncludeFileNameGlobs = options.IncludeFilterMode == FilterPatternMode.GlobPath
                ? options.IncludeGlobs
                : Array.Empty<string>();
            concreteLister.EarlyFileNameLiteralTerms = options.SearchMode == SearchMode.FileNameThenContent
                && !options.UseRegex
                ? literalTerms
                : [];
            concreteLister.SdkChannelBufferSize = options.SdkChannelBufferSize;
            concreteLister.ExcludeAdminProtectedPaths = options.ExcludeAdminProtectedPaths;
            concreteLister.AdminProtectedPathSegmentsOverride = options.AdminProtectedPathSegments;
            concreteLister.MaxSearchDepth = options.MaxSearchDepth;
            concreteLister.SearchOnlineOnlyFiles = options.SearchOnlineOnlyFiles;
            concreteLister.SearchHiddenFiles = options.SearchHiddenFiles;
            concreteLister.BackendOverride = options.FileListerBackendOverride;

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

        // Name-first pass (Both + filename-only modes): before the full content scan, run a quick Everything
        // name-filtered query so filename matches surface immediately instead of waiting for the
        // whole tree to be content-scanned. This preserves Both semantics — the full discovery
        // below still queues every other file for content scanning; the pass front-loads filename hits
        // and their own content scans. Filename-only mode ends after this query instead of enumerating
        // the entire root. Skipped for regex/empty queries and when the backend can't push the term.
        bool nameFirstPass = !options.SuppressNameFirstPass
            && options.SearchMode is SearchMode.Both or SearchMode.FileNames
            && _fileLister is FileLister
            && FileLister.SdkAvailable
            && IsNameFirstBackendEligible(options.FileListerBackendOverride)
            && literalTerms.Count >= 1
            && FileLister.BuildEverythingFileNameFilter(literalTerms) is not null;

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
        var pending = Channel.CreateBounded<string>(new BoundedChannelOptions(PendingFileChannelCapacity)
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
        int sourceBackedCap = options.DegradedResultStore != null
            ? SourceBackedResultChannelCapacity
            : contentCap;
        var sourceBackedResults = Channel.CreateBounded<SourceBackedMatch>(new BoundedChannelOptions(sourceBackedCap)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        YaguLog.For("SearchService").LogInformation("Pipeline channels created: events={EventCapacity}, pending={PendingCapacity}, contentResults={ContentCapacity}, sourceBackedResults={SourceBackedCapacity}", EventChannelCapacity, PendingFileChannelCapacity, contentCap, sourceBackedCap);

        int filesScanned = 0;
        int filesSkipped = 0;
        int filesWithMatches = 0;
        int totalMatches = 0;
        int totalDiscovered = 0;
        long bytesScanned = 0;
        int truncated = 0;
        int degraded = options.DegradedResultStore != null ? 1 : 0; // start degraded immediately when a result store is available
        int everDegraded = degraded;   // 1 once memory-saving mode was used during this search
        // Content-index acceleration accounting for this root (plan §6.2). Set once at the end of
        // discovery from the pruning gate; read only when the final summary is built (after discovery
        // joins), so no volatile access is needed. Pure diagnostics — never affects results.
        int indexAccelerationRequested = options.UseContentIndex ? 1 : 0;
        int indexGateEngaged = 0;
        int indexFilesPruned = 0;
        int indexFilesRescued = 0;
        int evictionInFlight = 0;    // 1 while an eviction event is being processed by the UI
        int pressureCycles = 0;      // total number of memory pressure events emitted
        int consecutiveFutileEvictions = 0; // eviction cycles that freed 0 — used to stop futile loops
        long lastPressureCheckTicks = 0;    // timestamp of last pressure event emission
        long memHeartbeatTicks = 0;         // timestamp of last machine-wide memory heartbeat
        int nativeBatchesProcessed = 0; // total native batches flushed
        long forwarderItemsForwarded = 0; // total results forwarded from contentResults → events
        long forwarderWriteStallMs = 0;   // cumulative ms the forwarder was blocked writing to events channel
        string? fallbackReason = null;
        // Skip-reason tallies
        int skipBinary = 0, skipAccessDenied = 0, skipIOError = 0, skipTooLarge = 0;
        int skipIoTimeout = 0;
        int skipNotFound = 0, skipEncoding = 0, skipOther = 0, skipByExtension = 0, skipDirectories = 0;
        int skipGlobExcluded = 0;
        int skipSizeFiltered = 0;
        int skipTooSmall = 0;      // below --min-size (discovery filter + ContentSearcher SkipTooSmall)
        int skipDateFiltered = 0;  // outside a created/modified date filter
        int skipCloudOnly = 0;
        int skipOcrCache = 0;
        int skipMultiline = 0; // multiline over-cap + per-file timeout + unsupported-surface skips
        int ocrFilesQueued = 0, ocrFilesProcessed = 0;
        int pdfFilesQueued = 0, pdfFilesProcessed = 0;
        int discoveryCompleted = 0;
        // 1 while the fast filename name-first pass (and its brief priority content scan) runs, before the
        // full-drive scan total is established. Surfaced on each progress snapshot so the UI keeps the bar
        // indeterminate through this phase instead of flashing to 100% against the tiny name-first total.
        int nameFirstPhaseActive = 0;
        // Fresh provider-liveness decisions per search (a provider may have been
        // installed/uninstalled/signed-out since the last run).
        CloudFileHelper.ResetProviderCache();
        StreamingScanSink? activeStreamingSink = null; // promoted to outer scope so CheckMemoryPressure can toggle degraded mode
        IntPtr activeFilesScannedPtr = IntPtr.Zero; // unmanaged counter updated atomically during streaming scan
        IntPtr activeTotalMatchesPtr = IntPtr.Zero; // unmanaged counter updated atomically during streaming scan
        IntPtr activeFilesWithMatchesPtr = IntPtr.Zero;

        int CurrentDirectorySkips() => Math.Max(Volatile.Read(ref skipDirectories), _fileLister.SkippedDirectories);
        int CurrentAccessDeniedSkips() => Volatile.Read(ref skipAccessDenied) + _fileLister.AccessDeniedDirectories;
        int CurrentEarlySkips() => _fileLister.EarlySkippedFiles;
        int CurrentEarlyTooLargeSkips() => _fileLister.EarlySkippedTooLargeFiles;
        int CurrentEarlyTooSmallSkips() => _fileLister.EarlySkippedTooSmallFiles;
        int CurrentEarlyDateSkips() => _fileLister.EarlySkippedByDateFiles;
        int CurrentCloudOnlySkips() => Volatile.Read(ref skipCloudOnly) + _fileLister.CloudOnlySkippedFiles;
        int CurrentFilesSkipped() => Volatile.Read(ref filesSkipped) + CurrentDirectorySkips() + CurrentEarlySkips();

        // Single classification point for a negative ContentSearcher skip code. Every managed scan
        // surface (ordinary files and archive entries) routes through here so no skip can increment the
        // headline count without also landing in a status-bar bucket.
        void TallyContentSkipReason(int code, string file)
        {
            switch (code)
            {
                case ContentSearcher.SkipBinary: Interlocked.Increment(ref skipBinary); break;
                case ContentSearcher.SkipAccessDenied:
                    Interlocked.Increment(ref skipAccessDenied);
                    YaguLog.For("ContentSearcher").LogDebug("Access denied: {File}", file);
                    break;
                case ContentSearcher.SkipIOError: Interlocked.Increment(ref skipIOError); break;
                case ContentSearcher.SkipIoTimeout: Interlocked.Increment(ref skipIoTimeout); break;
                case ContentSearcher.SkipTooLarge: Interlocked.Increment(ref skipTooLarge); break;
                case ContentSearcher.SkipTooSmall:
                    Interlocked.Increment(ref skipSizeFiltered);
                    Interlocked.Increment(ref skipTooSmall);
                    break;
                case ContentSearcher.SkipNotFound: Interlocked.Increment(ref skipNotFound); break;
                case ContentSearcher.SkipEncoding: Interlocked.Increment(ref skipEncoding); break;
                case ContentSearcher.SkipByExtension: Interlocked.Increment(ref skipByExtension); break;
                case ContentSearcher.SkipCloudOnly: Interlocked.Increment(ref skipCloudOnly); break;
                case ContentSearcher.SkipMultilineTooLarge:
                case ContentSearcher.SkipMultilineTimeout:
                    Interlocked.Increment(ref skipMultiline);
                    break;
                default: Interlocked.Increment(ref skipOther); break;
            }
        }
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
                Volatile.Read(ref skipGlobExcluded) + Volatile.Read(ref skipOcrCache),
                _fileLister.GitignoreSkipped,
                CurrentCloudOnlySkips(),
                Volatile.Read(ref skipMultiline),
                Volatile.Read(ref skipIoTimeout),
                Volatile.Read(ref skipTooSmall) + CurrentEarlyTooSmallSkips(),
                Volatile.Read(ref skipDateFiltered) + CurrentEarlyDateSkips(),
                Volatile.Read(ref skipOcrCache),
                _fileLister.EarlyExcludedByExtensionFiles,
                _fileLister.CloudOnlySkippedFiles);
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
            return new SearchProgress(
                CurrentFilesProcessed(),
                CurrentTotalFiles(),
                currentTotalMatches,
                currentFilesWithMatches,
                CurrentFilesSkipped(),
                Volatile.Read(ref bytesScanned),
                sw.Elapsed,
                accessDenied,
                breakdown)
            {
                SourceBacked = new SourceBackedSearchProgress(
                    Volatile.Read(ref ocrFilesProcessed),
                    Volatile.Read(ref ocrFilesQueued),
                    Volatile.Read(ref pdfFilesProcessed),
                    Volatile.Read(ref pdfFilesQueued),
                    Volatile.Read(ref discoveryCompleted) != 0),
                NameFirstPhase = Volatile.Read(ref nameFirstPhaseActive) != 0,
            };
        }

        SearchSummary CreateSummarySnapshot(TimeSpan elapsed)
        {
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
            var skipReasons = new SkipBreakdown(
                skipBinary,
                accessDeniedSkips,
                skipIOError,
                skipTooLarge + earlyTooLargeSkips,
                skipNotFound,
                skipEncoding,
                skipOther,
                skipByExtension,
                nonAccessDeniedDirectorySkips,
                earlySkips + discoverySizeSkips,
                skipGlobExcluded + Volatile.Read(ref skipOcrCache),
                _fileLister.GitignoreSkipped,
                CurrentCloudOnlySkips(),
                skipMultiline,
                skipIoTimeout,
                skipTooSmall + CurrentEarlyTooSmallSkips(),
                skipDateFiltered + CurrentEarlyDateSkips(),
                Volatile.Read(ref skipOcrCache),
                _fileLister.EarlyExcludedByExtensionFiles,
                _fileLister.CloudOnlySkippedFiles);
            return new SearchSummary(
                TotalFiles: totalFiles,                FilesScanned: CurrentFilesProcessed(),
                FilesSkipped: totalSkipped,
                FilesWithMatches: filesWithMatches,
                TotalMatches: totalMatches,
                BytesScanned: bytesScanned,
                Elapsed: elapsed,
                Cancelled: cancellationToken.IsCancellationRequested,
                Truncated: wasTruncated,
                Degraded: wasDegraded,
                FallbackReason: fallbackReason,
                SkipReasons: skipReasons,
                IndexAcceleration: indexAccelerationRequested != 0
                    ? new IndexAccelerationInfo(1, indexGateEngaged, indexFilesPruned, indexFilesRescued)
                    : null);
        }

        // Captures the search locals directly so workers and the progress timer can
        // trigger the same degradation path without waiting for a native batch to end.
        void CheckMemoryPressure()
        {
            // Machine-wide memory heartbeat (fixed cadence, independent of the pressure threshold). A
            // native fail-fast in yagu_core.dll (e.g. a failed huge allocation → handle_alloc_error →
            // abort as 0xc0000409) leaves NO final snapshot, and the pressure-cycle log below only fires
            // after the threshold trips and holds the eviction lock. This heartbeat guarantees the log
            // always carries a high-resolution trail of process WS + system available/total right up to
            // the moment the process dies. CAS so only one of the many worker threads logs per interval.
            {
                long nowHb = Stopwatch.GetTimestamp();
                long lastHb = Volatile.Read(ref memHeartbeatTicks);
                if ((lastHb == 0 || (double)(nowHb - lastHb) / Stopwatch.Frequency >= MemoryHeartbeatSeconds)
                    && Interlocked.CompareExchange(ref memHeartbeatTicks, nowHb, lastHb) == lastHb)
                {
                    YaguLog.For("SearchService").LogInformation(
                        "Memory heartbeat: {Diagnostics}, scanned={Scanned:N0}, matches={Matches:N0}, OCR={OcrProcessed:N0}/{OcrQueued:N0}, PDF={PdfProcessed:N0}/{PdfQueued:N0}",
                        GetMemoryDiagnostics(),
                        filesScanned,
                        totalMatches,
                        Volatile.Read(ref ocrFilesProcessed),
                        Volatile.Read(ref ocrFilesQueued),
                        Volatile.Read(ref pdfFilesProcessed),
                        Volatile.Read(ref pdfFilesQueued));
                }
            }

            if (IsMemoryPressureHigh(options.MaxProcessMemoryBytes, options.MemoryPressurePercent))
            {
                Volatile.Write(ref degraded, 1);
                Volatile.Write(ref everDegraded, 1);
                activeStreamingSink?.SetDegraded(true);

                // Trim WS to release soft-faulted mmap pages — but back off once evictions keep freeing
                // nothing (consecutiveFutileEvictions >= 3). That "futile" state means the resident memory
                // is non-sheddable (e.g. a large content-index deserialize is in flight): trimming then only
                // soft-faults pages that are immediately re-touched, which dramatically slows the very
                // operation causing the pressure (a cold layered-index open ballooned to ~53s this way).
                int futile = Volatile.Read(ref consecutiveFutileEvictions);
                if (futile < 3)
                    TrimProcessWorkingSet();

                // After several consecutive evictions that freed nothing, slow down
                // pressure events to avoid futile GC churn. But use a short cooldown
                // so we don't let memory grow unchecked for long.
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
                    YaguLog.For("SearchService").LogWarning(
                        "Memory pressure cycle #{Cycle}: {Diagnostics} - shedding Yagu memory (scanned={Scanned:N0}, matches={Matches:N0})", cycle, diagnostics, filesScanned, totalMatches);
                    try
                    {
                        var memoryPressureEvent = new SearchEvent.MemoryPressure(
                            (evictedCount) =>
                            {
                                if (evictedCount == 0)
                                    Interlocked.Increment(ref consecutiveFutileEvictions);
                                else
                                    Volatile.Write(ref consecutiveFutileEvictions, 0);
                                YaguLog.For("SearchService").LogWarning(
                                    "Eviction acknowledged: freed {EvictedCount}; continuing in memory-saving mode", evictedCount);
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
                YaguLog.For("SearchService").LogWarning(
                    "Memory pressure relieved: {Diagnostics} - leaving memory-saving mode", diagnostics);
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

        // ── Discovery ──
        // The content-index pruning gate (plan §5) is declared here in method scope — though it is created
        // at barrier B0 inside the discovery task below (off the UI thread) — so the after-scan-drain B1
        // reconciliation (plan §5.4, option (b)) in the content-workers task can rescue any pruned path that
        // changed before the whole search ends, not merely before discovery ended.
        Services.Index.ContentIndexSearchGate? contentIndexGate = null;
        Services.Index.IContentIndexPruningScan? pruningScan = null;
        var discovery = Task.Run(async () =>
        {
            Services.Index.IContentIndexShadowScan? shadowScan = null;

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
                if (options.DirectOutputStream is not null && options.DirectOutputLock is not null)
                {
                    DirectOutputSink.WriteFileNameMatches(
                        options.DirectOutputStream,
                        options.DirectOutputColor,
                        batch,
                        options.DirectOutputLock);
                }
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
                // Yagu's own OCR text cache (one .txt per OCR'd image) lives under this directory. Those
                // files are an internal implementation detail — never surface them as their own result
                // rows. An OCR'd image must only ever appear under its image path (set by OcrTextMatcher),
                // never under the cache text file's path.
                string ocrCacheDirPrefix = Ocr.OcrTextCache.DefaultBaseDirectory() + Path.DirectorySeparatorChar;

                // ── Name-first pass (Both + filename-only modes) ──
                // Run a quick name-filtered Everything query first so filename matches appear
                // immediately. In Both mode, queue each name-hit path for content scanning NOW — before
                // the full root enumeration — so its filename-only group is upgraded with content hits
                // first. Paths emitted/scanned by an all-roots priority prepass seed these sets so the
                // later full sweep neither emits nor scans them twice.
                HashSet<string>? nameFirstEmitted = options.PreEmittedFileNamePaths is { Count: > 0 }
                    ? new HashSet<string>(options.PreEmittedFileNamePaths, StringComparer.OrdinalIgnoreCase)
                    : null;
                HashSet<string>? priorityContentQueued = null;
                if (nameFirstPass)
                {
                    Volatile.Write(ref nameFirstPhaseActive, 1);
                    nameFirstEmitted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    priorityContentQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int nameFirstCap = options.MaxResults > 0 ? Math.Min(options.MaxResults, 50_000) : 50_000;
                    var nameFirstLister = (FileLister)_fileLister;
                    var nameFirstBackendOverride = nameFirstLister.BackendOverride;
                    nameFirstLister.EarlyFileNameLiteralTerms = literalTerms;
                    // Force SDK-only for the name pass. The name-scoped query is a fast-path
                    // optimization: if it matches nothing we want an instant empty result, NOT a
                    // multi-minute es.exe/managed full-tree walk (the slow lower tiers that Auto would
                    // fall through to on a 0-result SDK query). The full discovery pass below still
                    // runs with the caller's normal tiering, so coverage/correctness is unaffected.
                    nameFirstLister.BackendOverride = FileListerBackend.EverythingSdk;
                    // Do NOT push !attrib:h on this broad name query — a common term like "a" matches
                    // ~1.2M files and !attrib:h then forces a ~35s un-indexed attribute scan inside the
                    // SDK's blocking Query (the cause of the long "no results" gap). Hidden files are
                    // instead excluded in-process on the few emitted matches below.
                    nameFirstLister.EarlySuppressHiddenAttributeFilter = true;
                    try
                    {
                        await foreach (var path in _fileLister.ListFilesAsync(options.Directory, includeExts, maxFiles: nameFirstCap, cancellationToken).WithCancellation(cancellationToken))
                        {
                            if (Volatile.Read(ref truncated) != 0) break;
                            if (path.StartsWith(ocrCacheDirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!globMatcher.Matches(path)) continue;
                            if (ShouldSkipByFileMetadata(path, options, out _,
                                checkSize: !fileListerAlreadyCheckedMetadata,
                                checkDates: !fileListerAlreadyCheckedMetadata)) continue;

                            var fileName = Path.GetFileName(path);
                            int fnStart = -1, fnLen = 0;
                            if (regex is not null)
                            {
                                var m = regex.Match(fileName);
                                if (m.Success) { fnStart = m.Index; fnLen = m.Length; }
                            }
                            else
                            {
                                int idx = fileName.IndexOf(literal!, cmp);
                                if (idx >= 0) { fnStart = idx; fnLen = literal!.Length; }
                            }
                            if (fnStart >= 0)
                            {
                                // Hidden-file exclusion is done here (the query no longer pushes
                                // !attrib:h on this broad pass). Only exact-name matches reach this
                                // point, so the per-match attribute check is cheap.
                                if (!options.SearchHiddenFiles && IsHiddenFile(path)) continue;
                                if (nameFirstEmitted.Add(path))
                                {
                                    (filenameBatch ??= new List<SearchResult>(FilenameBatchSize)).Add(new SearchResult(
                                        FilePath: path, LineNumber: 0, MatchLine: fileName,
                                        MatchStartColumn: fnStart, MatchLength: fnLen,
                                        ContextBefore: [], ContextAfter: [])
                                    { SourceMatchStartColumn = fnStart });
                                    if (filenameBatch.Count >= FilenameBatchSize)
                                        await FlushFilenameBatchAsync().ConfigureAwait(false);

                                    if (searchContent)
                                        priorityContentQueued.Add(path);

                                    if (!searchContent)
                                    {
                                        Interlocked.Increment(ref filesScanned);
                                        Interlocked.Increment(ref filesWithMatches);
                                        int n = Interlocked.Increment(ref totalMatches);
                                        if (options.MaxResults > 0 && n >= options.MaxResults)
                                            Volatile.Write(ref truncated, 1);
                                    }
                                }
                            }
                        }
                        await FlushFilenameBatchAsync().ConfigureAwait(false);
                        // Filename rows are now in the event stream. Only after publishing them, queue
                        // the same paths for priority content scans so the UI always creates the file-name
                        // group first and then upgrades it as content matches arrive.
                        if (priorityContentQueued is not null)
                        {
                            foreach (string path in priorityContentQueued)
                                await WritePendingFileAsync(path).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        nameFirstLister.EarlyFileNameLiteralTerms = [];
                        nameFirstLister.BackendOverride = nameFirstBackendOverride;
                        nameFirstLister.EarlySuppressHiddenAttributeFilter = false;
                    }
                    YaguLog.For("Discovery").LogInformation(
                        "Name-first pass: emitted {Emitted:N0} filename match(es) in {ElapsedSeconds:F2}s", nameFirstEmitted.Count, sw.Elapsed.TotalSeconds);
                }

                bool runFullDiscovery = !(nameFirstPass
                    && (options.StopAfterNameFirstPass || options.SearchMode == SearchMode.FileNames));
                // No full content scan follows (filename-only / name-first-only), so the name-first result
                // IS the final result — leave the determinate bar alone (nothing will re-base the total).
                if (!runFullDiscovery)
                    Volatile.Write(ref nameFirstPhaseActive, 0);
                if (runFullDiscovery)
                {
                    // Content-index setup deliberately happens AFTER the Everything name pass. A cold/mapped
                    // index open can take seconds; it must never delay a filename hit already known to Everything.
                    try { contentIndexGate = options.ContentIndexGateFactory?.Invoke(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { YaguLog.For("ContentIndex").LogWarning(ex, "Search gate init failed; live-scanning"); }

                    try { shadowScan = options.ContentIndexShadowScanFactory?.Invoke(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { YaguLog.For("ContentIndex").LogDebug(ex, "Shadow pipeline init failed; skipping shadow."); }

                    try
                    {
                        pruningScan = options.ContentIndexPruningScanFactory?.Invoke((p, _) => WritePendingFileAsync(p));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { YaguLog.For("ContentIndex").LogDebug(ex, "Worker pruning pipeline init failed; not pruning via worker."); }
                    if (pruningScan is not null)
                        contentIndexGate = null;
                }

                if (runFullDiscovery)
                await foreach (var path in _fileLister.ListFilesAsync(options.Directory, includeExts, maxFiles: 0, cancellationToken).WithCancellation(cancellationToken))
                {
                    if (Volatile.Read(ref truncated) != 0) break;
                    // The full-drive total is now established (Everything returns it before the first yield),
                    // so the name-first phase is over — let the UI switch from the indeterminate bar to the
                    // determinate one, which now climbs 0→100% against the real total exactly once.
                    if (Volatile.Read(ref nameFirstPhaseActive) != 0)
                        Volatile.Write(ref nameFirstPhaseActive, 0);
                    Interlocked.Increment(ref totalDiscovered);

                    if (path.StartsWith(ocrCacheDirPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipOcrCache);
                        continue;
                    }

                    if (!globMatcher.Matches(path))
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipGlobExcluded);
                        continue;
                    }

                        var metadataSkip = ClassifyMetadataSkip(path, options,
                            checkSize: !fileListerAlreadyCheckedMetadata,
                            checkDates: !fileListerAlreadyCheckedMetadata);
                        if (metadataSkip != MetadataSkipReason.None)
                    {
                        Interlocked.Increment(ref filesScanned);
                        Interlocked.Increment(ref filesSkipped);
                        Interlocked.Increment(ref skipSizeFiltered);
                        switch (metadataSkip)
                        {
                            case MetadataSkipReason.TooLarge: Interlocked.Increment(ref skipTooLarge); break;
                            case MetadataSkipReason.TooSmall: Interlocked.Increment(ref skipTooSmall); break;
                            default: Interlocked.Increment(ref skipDateFiltered); break;
                        }
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
                            if (emitFileNameMatches && (nameFirstEmitted is null || !nameFirstEmitted.Contains(path)))
                            {
                                (filenameBatch ??= new List<SearchResult>(FilenameBatchSize)).Add(new SearchResult(
                                    FilePath: path, LineNumber: 0, MatchLine: fileName,
                                    MatchStartColumn: fnMatchStart, MatchLength: fnMatchLen,
                                    ContextBefore: [], ContextAfter: [])
                                { SourceMatchStartColumn = fnMatchStart });
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

                    bool scannedByEarlierAllRootsPass = options.PreScannedContentPaths?.Contains(path) == true;
                    bool queuedByThisNamePass = priorityContentQueued?.Contains(path) == true;
                    if (scannedByEarlierAllRootsPass)
                    {
                        // The priority pass completed before this full sweep started. Count this path once
                        // for progress/summary, but do not emit duplicate content matches.
                        Interlocked.Increment(ref filesScanned);
                    }
                    else if (queuedByThisNamePass)
                    {
                        // Its content worker is already running in THIS search and owns the processed count.
                    }
                    else if (searchContent)
                    {
                        if (!requireFileNameMatchForContent || fileNameMatched)
                        {
                            // Stage-3 shadow (plan §5.3): offer this content-scan candidate to the mapped-worker
                            // classifier. It never prunes → the scan below still processes every path → result
                            // unchanged; a shadow fault disables shadow, never the search. Cancellation propagates.
                            string? normalizedForIndex = null;
                            if (shadowScan is not null)
                            {
                                normalizedForIndex = Services.Index.IndexScopeIdentity.NormalizePath(path);
                                try { await shadowScan.OfferAsync(normalizedForIndex, cancellationToken).ConfigureAwait(false); }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex) when (ex is not OutOfMemoryException)
                                {
                                    shadowScan = null;
                                    YaguLog.For("ContentIndex").LogDebug(ex, "Shadow offer failed; disabling shadow.");
                                }
                            }

                            // Content-index pruning (plan §5): skip ordinary-text files the index proves
                            // cannot match. NormalizePath is only evaluated when a gate/pipeline is active, so a
                            // disabled search keeps its exact current cost. A pruned file still counts as
                            // processed for progress.
                            bool handledByPruning = false;
                            if (RequiresAuthoritativeSpecialSourceScan(path, options))
                            {
                                // Raw-file trigrams (including the bounded binary ASCII representation)
                                // cannot prove absence inside an archive entry, OCR output, or extracted PDF
                                // text. Always forward these candidates to the authoritative extractor lane.
                                await WritePendingFileAsync(path).ConfigureAwait(false);
                                handledByPruning = true;
                            }
                            else if (pruningScan is not null)
                            {
                                // Stage-4 worker pruning: offer (original path, normalized path). The pipeline
                                // forwards survivors to WritePendingFileAsync (its sink) and prunes nonmembers;
                                // pruned files are counted as processed at B1. Any offer fault → scan this path
                                // live but KEEP the pipeline so its B1 spool replay still rescues earlier prunes.
                                normalizedForIndex ??= Services.Index.IndexScopeIdentity.NormalizePath(path);
                                try
                                {
                                    await pruningScan.OfferAsync(path, normalizedForIndex, cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex) when (ex is not OutOfMemoryException)
                                {
                                    YaguLog.For("ContentIndex").LogDebug(ex, "Worker pruning offer failed; scanning this path live.");
                                    await WritePendingFileAsync(path).ConfigureAwait(false);
                                }
                                handledByPruning = true;
                            }

                            if (!handledByPruning)
                            {
                                if (contentIndexGate is null
                                    || contentIndexGate.ShouldContentScan(path, normalizedForIndex ??= Services.Index.IndexScopeIdentity.NormalizePath(path)))
                                    await WritePendingFileAsync(path).ConfigureAwait(false);
                                else
                                    Interlocked.Increment(ref filesScanned);
                            }
                        }
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
                        YaguLog.For("Discovery").LogInformation("Progress: {Enumerated:N0} files enumerated, {Discovered:N0} discovered, elapsed={ElapsedSeconds:F1}s", discoveryLogCounter, Volatile.Read(ref totalDiscovered), sw.Elapsed.TotalSeconds);
                        discoveryLogTimer.Restart();
                    }
                }
                await FlushFilenameBatchAsync().ConfigureAwait(false);

                // Content-index B1 reconciliation (plan §5.4, option (b)) is deferred to the content-workers
                // task's finally — it runs AFTER the pending-scan channel drains, so a pruned file edited
                // during the content scan (not merely during discovery) is still rescued before the search
                // ends. Feeding rescue paths into `pending` here would fix the barrier too early (at end of
                // discovery, while content scans are still running).

                Volatile.Write(ref skipDirectories, _fileLister.SkippedDirectories);
                fallbackReason = _fileLister.FallbackReason;
                if (fallbackReason is not null)
                {
                    await events.Writer.WriteAsync(new SearchEvent.Fallback(fallbackReason), cancellationToken).ConfigureAwait(false);
                }
                Volatile.Write(ref discoveryCompleted, 1);
                await events.Writer.WriteAsync(new SearchEvent.DiscoveryComplete(CurrentTotalFiles()), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { YaguLog.For("SearchService").LogWarning("Discovery cancelled"); }
            catch (Exception ex) { YaguLog.For("SearchService").LogWarning(ex, "Discovery failed"); }
            finally
            {
                // Stage-3 shadow (plan §5.3): drain + close the shadow classifier once discovery ends (even on
                // cancel/error). Fail-safe — CompleteAsync never throws — and it never touched the result set.
                if (shadowScan is not null)
                    await shadowScan.CompleteAsync(cancellationToken).ConfigureAwait(false);
                // Stage-4 worker pruning (plan §5.3): drain the pruning classifier so every survivor is forwarded
                // to the pending-scan channel BEFORE it is completed below. Fail-safe — never throws; a pump
                // fault leaves the B1 spool replay to rescue anything pruned before it.
                if (pruningScan is not null)
                    await pruningScan.CompleteOfferingAsync().ConfigureAwait(false);
                YaguLog.For("SearchService").LogInformation(
                    "Discovery finished: {Discovered:N0} files discovered, total={Total:N0}, OCR queued={OcrQueued:N0}, PDF queued={PdfQueued:N0}, {ElapsedSeconds:F2}s elapsed",
                    Volatile.Read(ref totalDiscovered),
                    CurrentTotalFiles(),
                    Volatile.Read(ref ocrFilesQueued),
                    Volatile.Read(ref pdfFilesQueued),
                    sw.Elapsed.TotalSeconds);
                pending.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // ── Extended-source (archive/PDF/OCR) pruning gate ──
        // The §7 Phase 4 analogue of the content-index gate, in method scope so the content-workers
        // task can consult it before enqueuing an image/PDF candidate. Null unless an extended-source
        // namespace exists for this scope; when present it only skips a deterministic (PDF/archive)
        // source a required-superset trigram query provably cannot match and USN proves unchanged.
        // OCR/changed/mismatched sources always extract, and its end-of-discovery B1 reconciliation
        // re-extracts anything that changed after B0 — so a match can never be silently hidden.
        Services.Index.ExtendedSourceSearchGate? extendedSourceGate = null;
        try { extendedSourceGate = options.ExtendedSourceGateFactory?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { YaguLog.For("ContentIndex").LogWarning(ex, "Extended-source gate init failed; extracting all sources"); }

        // ── Image-text (OCR) search session ──
        // When enabled, images discovered during the scan are routed to a background OCR
        // queue that runs decoupled from the (Rust) file scan so it never slows discovery.
        // Recognized text is searched for the query and any matches stream into the same
        // content-results channel as ordinary file matches, appearing as results are found.
        Ocr.ImageOcrSearchSession? imageOcr = null;
        Ocr.IOcrEngine? ocrEngine = null;
        if (searchContent && options.SearchImageText)
        {
            int ocrWorkerCount = Math.Clamp(
                options.ImageOcrWorkerParallelism,
                Ocr.OcrWorkerParallelism.Minimum,
                Ocr.OcrWorkerParallelism.Maximum);
            ocrEngine = options.ImageOcrEngineFactory?.Invoke()
                ?? Ocr.OcrEngineFactory.Create(
                    options.ImageOcrEngine,
                    options.ImageOcrModel,
                    options.ImageOcrMaxSide,
                    ocrWorkerCount);
            // Clear OCR text left over from crashed/older runs before this search starts writing fresh
            // entries, so a previous run's partial output can never be mistaken for this search's results.
            Ocr.OcrTextCache.Cleanup();
            var ocrCache = new Ocr.OcrTextCache();
            imageOcr = new Ocr.ImageOcrSearchSession(
                ocrEngine,
                ocrCache,
                regex,
                literal,
                cmp,
                options.ContextLines,
                options.MaxMatchesPerFile,
                contentResults.Writer,
                onFileProcessed: () =>
                {
                    Interlocked.Increment(ref ocrFilesProcessed);
                    Interlocked.Increment(ref filesScanned);
                },
                onFileMatched: matchCount =>
                {
                    Interlocked.Increment(ref filesWithMatches);
                    int newTotal = Interlocked.Add(ref totalMatches, matchCount);
                    if (options.MaxResults > 0 && newTotal >= options.MaxResults)
                        Volatile.Write(ref truncated, 1);
                },
                workerCount: ocrWorkerCount,
                cancellationToken: cancellationToken,
                shouldStop: () => Volatile.Read(ref truncated) != 0);
            YaguLog.For("SearchService").LogInformation(
                "Image-text OCR enabled: engine={Engine}, workers={Workers}, extensions={Extensions}", ocrEngine.Id, ocrWorkerCount, options.ImageOcrExtensions.Count);
        }

        static bool RequiresAuthoritativeSpecialSourceScan(string path, SearchOptions options) =>
            (options.SearchInsideArchives && ZipArchiveSearcher.HasArchiveExtension(path, options.ArchiveExtensions)) ||
            (options.SearchImageText && Ocr.ImageOcrSupport.IsImageCandidate(path, options.ImageOcrExtensions)) ||
            (options.SearchPdfText && Pdf.PdfTextSupport.IsPdfCandidate(path, options.PdfTextExtensions));

        // ── PDF-text search session ──
        // When enabled, PDFs discovered during the scan are routed to a background queue that runs
        // the bundled Xpdf pdftotext on each file (decoupled from the Rust scan, like the OCR queue).
        // Extracted text is searched for the query and matches stream into the same content-results
        // channel as ordinary file matches.
        Pdf.PdfTextSearchSession? pdfText = null;
        Pdf.PdfTextExtractor? pdfExtractor = null;
        if (searchContent && options.SearchPdfText)
        {
            pdfExtractor = options.PdfTextExtractorFactory?.Invoke() ?? new Pdf.PdfTextExtractor();
            // Extracted PDF text is cached under the same PID-scoped, discovery-excluded cache dir as
            // OCR text, keyed by the pdftotext engine id so it never collides with image OCR entries.
            Ocr.OcrTextCache.Cleanup();
            var pdfCache = new Ocr.OcrTextCache();
            int pdfWorkerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
            pdfText = new Pdf.PdfTextSearchSession(
                pdfExtractor,
                pdfCache,
                regex,
                literal,
                cmp,
                options.ContextLines,
                options.MaxMatchesPerFile,
                contentResults.Writer,
                onFileProcessed: () =>
                {
                    Interlocked.Increment(ref pdfFilesProcessed);
                    Interlocked.Increment(ref filesScanned);
                },
                onFileMatched: matchCount =>
                {
                    Interlocked.Increment(ref filesWithMatches);
                    int newTotal = Interlocked.Add(ref totalMatches, matchCount);
                    if (options.MaxResults > 0 && newTotal >= options.MaxResults)
                        Volatile.Write(ref truncated, 1);
                },
                workerCount: pdfWorkerCount,
                cancellationToken: cancellationToken,
                shouldStop: () => Volatile.Read(ref truncated) != 0);
            YaguLog.For("SearchService").LogInformation(
                "PDF-text search enabled: workers={Workers}, extensions={Extensions}", pdfWorkerCount, options.PdfTextExtensions.Count);
        }

        // ── Content workers ──
        var workers = Task.CompletedTask;
        if (searchContent)
        {
            workers = Task.Run(async () =>
            {
                try
                {
                    imageOcr?.Start();
                    pdfText?.Start();
                    bool nativeAvailable = Native.NativeSearcher.IsAvailable && !options.Multiline;
                    int parallelism;
                    if (options.Multiline)
                    {
                        // Multiline holds whole files in memory (≈2× the UTF-16 blowup: original
                        // decoded string + LF shadow copy), so it MUST run at a dedicated, lower,
                        // memory-derived degree (~2–4) — never the line path's up-to-64-way — and
                        // independently of whether the native engine is available (§9).
                        parallelism = SearchOptions.ResolveMultilineParallelism(
                            Environment.ProcessorCount, GetAvailablePhysicalMemoryBytes(), options.MaxMultilineBytes);
                    }
                    else
                    {
                        parallelism = options.MaxDegreeOfParallelism > 0
                            ? options.MaxDegreeOfParallelism
                            : nativeAvailable
                                ? Math.Max(1, Math.Min(64, Environment.ProcessorCount * 2))
                                : Math.Max(1, Math.Min(16, Environment.ProcessorCount));
                    }
                    YaguLog.For("SearchService").LogInformation("Content scan parallelism = {Parallelism}{MultilineSuffix}", parallelism, options.Multiline ? " (multiline)" : "");

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
                                YaguLog.For("SearchService").LogWarning("Native session creation failed — falling back to managed per-file path");
                                goto managedFallback;
                            }

                            YaguLog.For("SearchService").LogInformation("Streaming native scanning enabled");

                            IntPtr cancelPtr = Marshal.AllocHGlobal(sizeof(int));
                            try
                            {
                                unsafe { *(int*)cancelPtr = 0; }
                                using var ctr = cancellationToken.Register(static state =>
                                {
                                    unsafe { Interlocked.Exchange(ref *(int*)(IntPtr)state!, 1); }
                                }, cancelPtr);

                                // When archive search is enabled, zip files must go through the managed
                                // ContentSearcher rather than the native Rust scanner.
                                async Task ScanZipViaManagedAsync(string zipFile)
                                {
                                    var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : patternOptions;
                                    try
                                    {
                                        var outcome = await ContentSearcher.SearchFileWithStatsAsync(
                                            zipFile, regex, literal, cmp, effectiveOptions,
                                            contentResults.Writer, session: null, cancellationToken).ConfigureAwait(false);
                                        int produced = outcome.MatchCount;
                                        int fileCount = Math.Max(1, outcome.EntriesScanned);
                                        Interlocked.Add(ref filesScanned, fileCount);

                                        if (produced < 0)
                                        {
                                            Interlocked.Increment(ref filesSkipped);
                                            TallyContentSkipReason(produced, zipFile);
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
                                        YaguLog.For("SearchService").LogWarning(ex, "Managed ZIP scan failed for {ZipFile}", zipFile);
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
                                int filesScannedBaseline = Volatile.Read(ref filesScanned);
                                int totalMatchesBaseline = Volatile.Read(ref totalMatches);
                                int filesWithMatchesBaseline = Volatile.Read(ref filesWithMatches);
                                IntPtr filesScannedAlloc = Marshal.AllocHGlobal(sizeof(int));
                                IntPtr totalMatchesAlloc = Marshal.AllocHGlobal(sizeof(int));
                                IntPtr filesWithMatchesAlloc = Marshal.AllocHGlobal(sizeof(int));
                                unsafe
                                {
                                    *(int*)filesScannedAlloc = filesScannedBaseline;
                                    *(int*)totalMatchesAlloc = totalMatchesBaseline;
                                    *(int*)filesWithMatchesAlloc = filesWithMatchesBaseline;
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
                                                pathsByIndex, EffectiveHardCap(options), Volatile.Read(ref totalMatches),
                                                cancelPtr, (int*)filesScannedAlloc,
                                                contextEnabled: options.ContextLines > 0,
                                                outputLock: options.DirectOutputLock);
                                            sinkInstance = directSink;
                                        }
                                        else
                                        {
                                            streamingSink = new StreamingScanSink(
                                                pathsByIndex,
                                                contentResults.Writer, EffectiveHardCap(options), Volatile.Read(ref totalMatches),
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

                                    // Create streaming scanner — Rust spawns persistent worker threads.
                                    // The streaming workers each perform *blocking* file opens/reads, so on a
                                    // cold full-drive sweep they spend most of their time parked on disk and
                                    // per-file filter-driver latency rather than burning CPU. Oversubscribing
                                    // the worker count relative to the CPU-scan width keeps the NVMe queue
                                    // depth high (more outstanding reads overlap that latency) and puts the
                                    // otherwise-idle CPU headroom to productive use. This raises concurrency
                                    // only — it imposes no cap on results, files, or memory. The native side
                                    // already clamps to MAX_WORKERS (64).
                                    //
                                    // The multiplier is configurable (IoOversubscriptionIndex). 2x is the
                                    // measured sweet spot on a *cold* full-C:\ sweep (24-core box): 24->48
                                    // workers raised bytes-read +62% and >=600 MB/s samples 11%->20% while
                                    // keeping working set well under the 2 GB ceiling. But on a warm cache
                                    // (re-search of cached data) those reads return instantly, so the extra
                                    // threads become pure CPU/heat. Auto therefore uses 1x on SSD/NVMe and 2x
                                    // only on rotational HDDs; the user can force 1x/2x/3x in Settings.
                                    bool targetIsHardDisk = options.IoOversubscriptionIndex == 0
                                        && !string.IsNullOrEmpty(options.Directory)
                                        && DiskTypeDetector.IsHardDisk(options.Directory);
                                    int oversubscription = SearchOptions.ResolveIoOversubscriptionMultiplier(
                                        options.IoOversubscriptionIndex, targetIsHardDisk);
                                    int streamingWorkers = Math.Min(64, Math.Max(1, parallelism) * oversubscription);
                                    YaguLog.For("SearchService").LogInformation("Streaming scanner IO workers = {Workers} (cpu parallelism {Parallelism}, oversubscription {Oversubscription}x, mode {Mode})", streamingWorkers, parallelism, oversubscription, options.IoOversubscriptionIndex);
                                    IntPtr scanner;
                                    GCHandle sinkHandle;
                                    unsafe
                                    {
                                        scanner = Native.NativeSearcher.CreateStreamingScanner(
                                            activeSession, streamingWorkers, (int*)cancelPtr, sinkInstance, out sinkHandle);
                                    }

                                    if (scanner == IntPtr.Zero)
                                    {
                                        YaguLog.For("SearchService").LogWarning("Streaming scanner creation failed — falling back to managed path");
                                        if (sinkHandle.IsAllocated) sinkHandle.Free();
                                        streamingFailed = true;
                                    }

                                    if (!streamingFailed)
                                    {
                                        bool scannerFinished = false;
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
                                                    else if (imageOcr != null && Ocr.ImageOcrSupport.IsImageCandidate(file, options.ImageOcrExtensions))
                                                    {
                                                        // Route images to the background OCR queue instead of the
                                                        // native content scanner; matches stream in asynchronously.
                                                        // The extended-source gate can prune only a proven-fresh
                                                        // deterministic nonmember (never OCR) → short-circuits harmlessly here.
                                                        bool ocrPrioritized = false;
                                                        if (extendedSourceGate is null || extendedSourceGate.ShouldExtract(Services.Index.SpecialSourceKind.ImageOcr, file, out ocrPrioritized))
                                                        {
                                                            if (imageOcr.TryEnqueue(file, ocrPrioritized))
                                                                Interlocked.Increment(ref ocrFilesQueued);
                                                        }
                                                        else
                                                            Interlocked.Increment(ref filesScanned);
                                                    }
                                                    else if (pdfText != null && Pdf.PdfTextSupport.IsPdfCandidate(file, options.PdfTextExtensions))
                                                    {
                                                        // Route PDFs to the background pdftotext queue instead of the
                                                        // native content scanner; matches stream in asynchronously.
                                                        if (extendedSourceGate is null || extendedSourceGate.ShouldExtract(Services.Index.SpecialSourceKind.PdfText, file))
                                                        {
                                                            if (pdfText.TryEnqueue(file))
                                                                Interlocked.Increment(ref pdfFilesQueued);
                                                        }
                                                        else
                                                            Interlocked.Increment(ref filesScanned);
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
                                                        YaguLog.For("Workers").LogInformation(
                                                            "Streaming: pushed={Pushed:N0} | scanned={Scanned:N0}, matches={Matches:N0}, withMatches={WithMatches:N0}, skipped={Skipped:N0}, degraded={Degraded}, parallelism={Parallelism}, elapsed={ElapsedSeconds:F1}s, {Diagnostics}",
                                                            fileIndexCounter, CurrentFilesScanned(), Volatile.Read(ref totalMatches), Volatile.Read(ref filesWithMatches), Volatile.Read(ref filesSkipped), Volatile.Read(ref degraded) != 0, parallelism, sw.Elapsed.TotalSeconds, memDiag);
                                                    }
                                                    continue;
                                                }

                                                // No items available — wait for more
                                                if (!await pending.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                                                    break;
                                            }

                                            // Signal Rust workers: no more paths coming. Blocks until all drain.
                                            Native.NativeSearcher.FinishStreamingScanner(scanner);
                                            scannerFinished = true;

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
                                                Interlocked.Add(ref skipIoTimeout, directSink.SkipIoTimeout);
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
                                                            case Native.NativeSearcher.StatusIoTimeout:
                                                                Interlocked.Increment(ref skipIoTimeout);
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
                                            if (!scannerFinished)
                                            {
                                                try
                                                {
                                                    Native.NativeSearcher.FinishStreamingScanner(scanner);
                                                }
                                                catch (Exception ex)
                                                {
                                                    YaguLog.For("SearchService").LogWarning(ex, "Streaming scanner finish during cleanup failed");
                                                }
                                            }
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
                                        int filesScannedDelta = *(int*)filesScannedAlloc - filesScannedBaseline;
                                        int totalMatchesDelta = *(int*)totalMatchesAlloc - totalMatchesBaseline;
                                        int filesWithMatchesDelta = *(int*)filesWithMatchesAlloc - filesWithMatchesBaseline;
                                        if (filesScannedDelta != 0) Interlocked.Add(ref filesScanned, filesScannedDelta);
                                        if (totalMatchesDelta != 0) Interlocked.Add(ref totalMatches, totalMatchesDelta);
                                        if (filesWithMatchesDelta != 0) Interlocked.Add(ref filesWithMatches, filesWithMatchesDelta);
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
                        if (Native.NativeSearcher.IsAvailable && !options.Multiline)
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
                            if (imageOcr != null && Ocr.ImageOcrSupport.IsImageCandidate(file, options.ImageOcrExtensions))
                            {
                                // Route images to the background OCR queue; the session counts
                                // them and emits any matches asynchronously. The extended-source gate
                                // can prune only a proven-fresh deterministic nonmember (never OCR).
                                bool ocrPrioritized = false;
                                if (extendedSourceGate is null || extendedSourceGate.ShouldExtract(Services.Index.SpecialSourceKind.ImageOcr, file, out ocrPrioritized))
                                {
                                    if (imageOcr.TryEnqueue(file, ocrPrioritized))
                                        Interlocked.Increment(ref ocrFilesQueued);
                                }
                                else
                                    Interlocked.Increment(ref filesScanned);
                                return;
                            }
                            if (pdfText != null && Pdf.PdfTextSupport.IsPdfCandidate(file, options.PdfTextExtensions))
                            {
                                // Route PDFs to the background pdftotext queue; the session counts
                                // them and emits any matches asynchronously.
                                if (extendedSourceGate is null || extendedSourceGate.ShouldExtract(Services.Index.SpecialSourceKind.PdfText, file))
                                {
                                    if (pdfText.TryEnqueue(file))
                                        Interlocked.Increment(ref pdfFilesQueued);
                                }
                                else
                                    Interlocked.Increment(ref filesScanned);
                                return;
                            }
                            var effectiveOptions = Volatile.Read(ref degraded) != 0 ? degradedOptions : patternOptions;
                            FileSearchOutcome outcome;
                            int produced;
                            try
                            {
                                outcome = await ContentSearcher.SearchFileWithStatsAsync(file, regex, literal, cmp, effectiveOptions, contentResults.Writer, sessionPool?.Value, ct).ConfigureAwait(false);
                                produced = outcome.MatchCount;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                YaguLog.For("SearchService").LogWarning(ex, "Scan failed for {File}", file);
                                Interlocked.Increment(ref filesScanned);
                                Interlocked.Increment(ref filesSkipped);
                                Interlocked.Increment(ref skipOther);
                                return;
                            }
                            Interlocked.Increment(ref filesScanned);
                            if (produced < 0)
                            {
                                Interlocked.Increment(ref filesSkipped);
                                TallyContentSkipReason(produced, file);
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
                catch (OperationCanceledException) { YaguLog.For("SearchService").LogWarning("Content workers cancelled"); }
                catch (Exception ex) { YaguLog.For("SearchService").LogWarning(ex, "Content workers failed"); }
                finally
                {
                    // ── Content-index B1 reconciliation (plan §5.4, option (b)) ──
                    // Now that the pending-scan channel has fully drained, replay the USN journal over
                    // [B0, now) and live-scan any pruned path whose content changed since B0 — INCLUDING an
                    // edit made during the content scan, not merely during discovery. A bounded
                    // rescue-and-re-drain loop repeats until a pass finds no newly-dirty pruned path (or the
                    // pass budget is reached), so "changed during the search" is caught right up to the end of
                    // the whole search. Rescue sets are normally empty for a selective query, so the added
                    // work is negligible. Any error or uncertainty makes the gate return every pruned path.
                    if (contentIndexGate is not null || pruningScan is not null)
                    {
                        // The pruned file was already counted as processed (filesScanned) at prune time, so a
                        // B1 rescue re-scans it for matches without re-counting it. The hot-loop degraded
                        // options are scoped to the try block above, so rebuild the effective options here.
                        var rescueOptions = Volatile.Read(ref degraded) != 0 && patternOptions.ContextLines > 0
                            ? CopyOptions(patternOptions, contextLines: 0)
                            : patternOptions;

                        async ValueTask RescueContentScanAsync(string file)
                        {
                            FileSearchOutcome outcome;
                            try
                            {
                                outcome = await ContentSearcher.SearchFileWithStatsAsync(
                                    file, regex, literal, cmp, rescueOptions, contentResults.Writer, session: null, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                YaguLog.For("SearchService").LogWarning(ex, "B1 rescue scan failed for {File}", file);
                                return;
                            }
                            if (outcome.MatchCount > 0)
                            {
                                Interlocked.Add(ref bytesScanned, outcome.BytesScanned);
                                Interlocked.Increment(ref filesWithMatches);
                                int newTotal = Interlocked.Add(ref totalMatches, outcome.MatchCount);
                                if (options.MaxResults > 0 && newTotal >= options.MaxResults)
                                    Volatile.Write(ref truncated, 1);
                            }
                        }

                        int rescued = 0;
                        const int MaxB1RescuePasses = 4;
                        if (contentIndexGate is not null)
                        {
                            try
                            {
                                for (int pass = 0; pass < MaxB1RescuePasses; pass++)
                                {
                                    if (Volatile.Read(ref truncated) != 0) break;
                                    Services.Index.B1RescuePass b1 = contentIndexGate.ReconcileB1Pass();
                                    foreach (string rescuePath in b1.PathsToScan)
                                    {
                                        if (Volatile.Read(ref truncated) != 0) break;
                                        await RescueContentScanAsync(rescuePath).ConfigureAwait(false);
                                        rescued++;
                                    }
                                    if (!b1.MorePassesUseful) break;
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                YaguLog.For("ContentIndex").LogWarning(ex, "Content-index B1 rescue loop failed.");
                            }

                            int grossPruned = contentIndexGate.TotalPruned;
                            int netPruned = Math.Max(0, grossPruned - rescued);
                            indexGateEngaged = netPruned > 0 ? 1 : 0;
                            indexFilesPruned = netPruned;
                            indexFilesRescued = rescued;
                            if (LogService.Instance.IsVerboseEnabled)
                                YaguLog.For("ContentIndex").LogDebug(
                                    "Content index evaluated '{Directory}': grossPruned={GrossPruned}, rescued={Rescued}, netPruned={NetPruned}, pruningDisabled={PruningDisabled}.",
                                    options.Directory, grossPruned, rescued, netPruned, contentIndexGate.PruningDisabled);
                        }
                        else
                        {
                            // ── Stage-4 worker pruning B1 (plan §5.5) ──
                            // Reconcile once: the worker replays [B0, now) over its provisional prune set (or the
                            // pipeline replays its whole recovery spool on any fault / uncertainty), returning the
                            // paths to live-scan. The pruned files were not scanned, so count them as processed
                            // here (once); a re-scanned rescue path is not re-counted, matching the gate path.
                            try
                            {
                                Services.Index.PruningScanResult result = await pruningScan!.ReconcileAtB1Async(cancellationToken).ConfigureAwait(false);
                                foreach (string rescuePath in result.RescuePaths)
                                {
                                    if (Volatile.Read(ref truncated) != 0) break;
                                    await RescueContentScanAsync(rescuePath).ConfigureAwait(false);
                                }
                                if (result.GrossPruned > 0)
                                    Interlocked.Add(ref filesScanned, (int)result.GrossPruned);
                                indexGateEngaged = result.NetPruned > 0 ? 1 : 0;
                                indexFilesPruned = (int)result.NetPruned;
                                indexFilesRescued = (int)result.Rescued;
                                if (LogService.Instance.IsVerboseEnabled)
                                    YaguLog.For("ContentIndex").LogDebug(
                                        "Worker pruning evaluated '{Directory}': grossPruned={GrossPruned}, rescued={Rescued}, netPruned={NetPruned}, accelerated={Accelerated}.",
                                        options.Directory, result.GrossPruned, result.Rescued, result.NetPruned, result.Accelerated);
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                YaguLog.For("ContentIndex").LogWarning(ex, "Worker pruning B1 reconcile failed.");
                            }
                        }
                    }

                    // Barrier B1 for extended sources: re-extract any pruned image/PDF whose backing file
                    // changed after B0 (or all pruned, on any journal discontinuity), before the extractor
                    // queues are closed. Route each rescued path back to its kind's session by extension.
                    if (extendedSourceGate is not null && (imageOcr != null || pdfText != null))
                    {
                        foreach (string rescue in extendedSourceGate.GetSourcesToRescan())
                        {
                            if (imageOcr != null && Ocr.ImageOcrSupport.IsImageCandidate(rescue, options.ImageOcrExtensions))
                            {
                                if (imageOcr.TryEnqueue(rescue))
                                    Interlocked.Increment(ref ocrFilesQueued);
                            }
                            else if (pdfText != null && Pdf.PdfTextSupport.IsPdfCandidate(rescue, options.PdfTextExtensions))
                            {
                                if (pdfText.TryEnqueue(rescue))
                                    Interlocked.Increment(ref pdfFilesQueued);
                            }
                        }
                    }

                    // Finish OCR before closing the content channel so all OCR matches are
                    // flushed into the results stream. The OCR queue ran alongside the scan;
                    // signal no-more-images and await the workers draining the backlog.
                    if (imageOcr != null)
                    {
                        imageOcr.Complete();
                        try { await imageOcr.DrainAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { YaguLog.For("SearchService").LogWarning(ex, "OCR drain failed"); }
                    }

                    // Likewise finish PDF-text extraction before closing the content channel so all
                    // pdftotext matches are flushed into the results stream.
                    if (pdfText != null)
                    {
                        pdfText.Complete();
                        try { await pdfText.DrainAsync().ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { YaguLog.For("SearchService").LogWarning(ex, "PDF-text drain failed"); }
                    }

                    // Release the OCR engine after draining (terminates the out-of-process worker, if any).
                    if (ocrEngine is IAsyncDisposable ocrEngineAsyncDisposable)
                    {
                        try { await ocrEngineAsyncDisposable.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception ex) { YaguLog.For("SearchService").LogWarning(ex, "OCR engine dispose failed"); }
                    }
                    else if (ocrEngine is IDisposable ocrEngineDisposable)
                    {
                        try { ocrEngineDisposable.Dispose(); }
                        catch (Exception ex) { YaguLog.For("SearchService").LogWarning(ex, "OCR engine dispose failed"); }
                    }

                    string finishMemDiag = GetMemoryDiagnostics();
                    YaguLog.For("SearchService").LogInformation(
                        "Content workers finished: scanned={Scanned:N0}, withMatches={WithMatches:N0}, totalMatches={TotalMatches:N0}, skipped={Skipped:N0}, batches={Batches}, pressureCycles={PressureCycles}, elapsed={ElapsedSeconds:F2}s, {Diagnostics}",
                        filesScanned, filesWithMatches, totalMatches, Volatile.Read(ref filesSkipped), Volatile.Read(ref nativeBatchesProcessed), Volatile.Read(ref pressureCycles), sw.Elapsed.TotalSeconds, finishMemDiag);
                    contentResults.Writer.TryComplete();
                    sourceBackedResults.Writer.TryComplete();
                }
            }, CancellationToken.None);
        }
        else
        {
            contentResults.Writer.TryComplete();
            sourceBackedResults.Writer.TryComplete();
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
                        YaguLog.For("Forwarder").LogWarning(
                            "Backpressure: WriteAsync to events channel took {StallMs}ms (batch={BatchCount} items, totalForwarded={TotalForwarded:N0})",
                            stallMs, batch.Count, Volatile.Read(ref forwarderItemsForwarded));
                    }

                    // Periodic throughput log
                    long now = Stopwatch.GetTimestamp();
                    if ((now - fwdLogLastTicks) >= Stopwatch.Frequency * FwdLogIntervalSec)
                    {
                        fwdLogLastTicks = now;
                        YaguLog.For("Forwarder").LogInformation(
                            "Throughput: forwarded={Forwarded:N0}, batchesFlushed={BatchesFlushed}, cumulativeStallMs={StallMs}, elapsed={ElapsedSeconds:F1}s",
                            Volatile.Read(ref forwarderItemsForwarded), fwdBatchesFlushed, Volatile.Read(ref forwarderWriteStallMs), sw.Elapsed.TotalSeconds);
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
                YaguLog.For("Forwarder").LogInformation(
                    "Completed: forwarded={Forwarded:N0}, batchesFlushed={BatchesFlushed}, cumulativeStallMs={StallMs}, elapsed={ElapsedSeconds:F1}s",
                    Volatile.Read(ref forwarderItemsForwarded), fwdBatchesFlushed, Volatile.Read(ref forwarderWriteStallMs), sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) { YaguLog.For("Forwarder").LogWarning("Cancelled"); }
            catch (Exception ex) { YaguLog.For("Forwarder").LogWarning(ex, "Failed"); }
        }, CancellationToken.None);

        var sourceBackedForwarder = Task.Run(async () =>
        {
            try
            {
                const int SourceBackedBatchSize = 16_384;
                List<SourceBackedMatch>? sourceBackedBatch = null;
                long fwdLogLastTicks = Stopwatch.GetTimestamp();
                const long FwdLogIntervalSec = 10;
                long fwdBatchesFlushed = 0;
                var fwdWriteSw = new Stopwatch();

                async ValueTask FlushSourceBackedBatchAsync()
                {
                    if (sourceBackedBatch is null || sourceBackedBatch.Count == 0) return;
                    var batch = sourceBackedBatch;
                    sourceBackedBatch = null;
                    fwdWriteSw.Restart();
                    await events.Writer.WriteAsync(new SearchEvent.SourceBackedMatchBatch(batch), cancellationToken).ConfigureAwait(false);
                    fwdWriteSw.Stop();
                    long stallMs = fwdWriteSw.ElapsedMilliseconds;
                    Interlocked.Add(ref forwarderItemsForwarded, batch.Count);
                    Interlocked.Add(ref forwarderWriteStallMs, stallMs);
                    fwdBatchesFlushed++;

                    if (stallMs > 500)
                    {
                        YaguLog.For("SourceBackedForwarder").LogWarning(
                            "Backpressure: WriteAsync to events channel took {StallMs}ms (batch={BatchCount} items, totalForwarded={TotalForwarded:N0})",
                            stallMs, batch.Count, Volatile.Read(ref forwarderItemsForwarded));
                    }

                    long now = Stopwatch.GetTimestamp();
                    if ((now - fwdLogLastTicks) >= Stopwatch.Frequency * FwdLogIntervalSec)
                    {
                        fwdLogLastTicks = now;
                        YaguLog.For("SourceBackedForwarder").LogInformation(
                            "Throughput: forwarded={Forwarded:N0}, batchesFlushed={BatchesFlushed}, cumulativeStallMs={StallMs}, elapsed={ElapsedSeconds:F1}s",
                            Volatile.Read(ref forwarderItemsForwarded), fwdBatchesFlushed, Volatile.Read(ref forwarderWriteStallMs), sw.Elapsed.TotalSeconds);
                    }
                }

                const int PartialFlushDelayMs = 250;
                CancellationTokenSource? delayCts = null;
                try
                {
                    while (await sourceBackedResults.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (sourceBackedResults.Reader.TryRead(out var r))
                        {
                            (sourceBackedBatch ??= new List<SourceBackedMatch>(SourceBackedBatchSize)).Add(r);
                            if (sourceBackedBatch.Count >= SourceBackedBatchSize)
                                await FlushSourceBackedBatchAsync().ConfigureAwait(false);
                        }

                        if (sourceBackedBatch is { Count: > 0 })
                        {
                            if (delayCts is null || !delayCts.TryReset())
                            {
                                delayCts?.Dispose();
                                delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            }
                            delayCts.CancelAfter(PartialFlushDelayMs);
                            try
                            {
                                if (await sourceBackedResults.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false))
                                {
                                    delayCts.CancelAfter(Timeout.Infinite);
                                    continue;
                                }
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                            }
                            await FlushSourceBackedBatchAsync().ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    delayCts?.Dispose();
                }

                await FlushSourceBackedBatchAsync().ConfigureAwait(false);
                YaguLog.For("SourceBackedForwarder").LogInformation(
                    "Completed: batchesFlushed={BatchesFlushed}, cumulativeStallMs={StallMs}, elapsed={ElapsedSeconds:F1}s",
                    fwdBatchesFlushed, Volatile.Read(ref forwarderWriteStallMs), sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) { YaguLog.For("SourceBackedForwarder").LogWarning("Cancelled"); }
            catch (Exception ex) { YaguLog.For("SourceBackedForwarder").LogWarning(ex, "Failed"); }
        }, CancellationToken.None);

        var scanComplete = new TaskCompletionSource<SearchSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressEmitter = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                long lastMemoryPressurePollTicks = 0;
                long memoryPressurePollIntervalTicks = (long)(PeriodicMemoryPressureCheckInterval.TotalSeconds * Stopwatch.Frequency);
                while (!scanComplete.Task.IsCompleted &&
                       await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (scanComplete.Task.IsCompleted || Volatile.Read(ref truncated) != 0) break;
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
            catch (Exception ex) { YaguLog.For("SearchService").LogDebug(ex, "Progress emitter stopped"); }
        }, CancellationToken.None);

        // Close the events channel once everything upstream is done.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(discovery, workers).ConfigureAwait(false);
                var summary = CreateSummarySnapshot(sw.Elapsed);
                scanComplete.TrySetResult(summary);
                YaguLog.For("SearchService").LogInformation(
                    "Scan complete: scanned={Scanned:N0}, matches={Matches:N0}, elapsed={ElapsedSeconds:F2}s; result batches are finalizing",
                    summary.FilesScanned, summary.TotalMatches, summary.Elapsed.TotalSeconds);
                await Task.WhenAll(forwarder, sourceBackedForwarder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                scanComplete.TrySetResult(CreateSummarySnapshot(sw.Elapsed));
                YaguLog.For("SearchService").LogDebug(ex, "Pipeline task exception");
            }
            finally
            {
                if (pruningScan is not null)
                    await pruningScan.CleanupAsync().ConfigureAwait(false);
                scanComplete.TrySetResult(CreateSummarySnapshot(sw.Elapsed));
                try { await progressEmitter.ConfigureAwait(false); }
                catch (Exception ex) { YaguLog.For("SearchService").LogDebug(ex, "Progress emitter completion failed"); }
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // 3. Stream events to caller. Progress snapshots are emitted by a timer so
        // quiet no-match stretches still update the UI.
        while (!scanComplete.Task.IsCompletedSuccessfully)
        {
            if (events.Reader.TryRead(out var evt))
            {
                yield return evt;
            }
            else
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<bool> waitForEventTask = events.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                Task completedTask = await Task.WhenAny(waitForEventTask, scanComplete.Task).ConfigureAwait(false);
                if (ReferenceEquals(completedTask, scanComplete.Task))
                {
                    waitCts.Cancel();
                    try { await waitForEventTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }
                else
                {
                    await waitForEventTask.ConfigureAwait(false);
                }
            }
        }

        yield return new SearchEvent.ScanCompleted(scanComplete.Task.Result);
        await foreach (SearchEvent searchEvent in DrainRemainingEventsAsync(events.Reader, cancellationToken).ConfigureAwait(false))
            yield return searchEvent;

        SearchSummary completedSummary = scanComplete.Task.Result;
        YaguLog.For("SearchService").LogInformation(
            "Search complete: {TotalMatches} matches in {FilesWithMatches} files, {FilesScanned} scanned, {FilesSkipped} skipped ({SkipReasons}), earlyFiltered={EarlyFiltered}, degraded={Degraded}, truncated={Truncated}, batches={Batches}, pressureCycles={PressureCycles}, forwarderItems={ForwarderItems:N0}, forwarderStallMs={ForwarderStallMs}, {ElapsedSeconds:F2}s",
            completedSummary.TotalMatches, completedSummary.FilesWithMatches, completedSummary.FilesScanned, completedSummary.FilesSkipped, completedSummary.SkipReasons, completedSummary.SkipReasons!.EarlyFiltered, completedSummary.Degraded, completedSummary.Truncated, Volatile.Read(ref nativeBatchesProcessed), pressureCycles, Volatile.Read(ref forwarderItemsForwarded), Volatile.Read(ref forwarderWriteStallMs), sw.Elapsed.TotalSeconds);
        yield return new SearchEvent.Completed(completedSummary);
    }

    internal static bool IsHiddenFile(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            // Can't determine (deleted/locked) → don't hide it; the full content sweep would show it too.
            return false;
        }
    }

    /// <summary>
    /// Why <see cref="ClassifyMetadataSkip"/> rejected a file. Mirrors the user-facing skip categories so
    /// the status-bar breakdown can report min-size and date filters separately from the size ceiling.
    /// </summary>
    internal enum MetadataSkipReason
    {
        None = 0,
        TooLarge,
        TooSmall,
        DateRange,
    }

    /// <summary>
    /// Back-compat wrapper over <see cref="ClassifyMetadataSkip"/>. Kept with this exact signature
    /// because tests reach it by reflection.
    /// </summary>
    internal static bool ShouldSkipByFileMetadata(
        string path,
        SearchOptions options,
        out bool tooLarge,
        bool checkSize = true,
        bool checkDates = true)
    {
        var reason = ClassifyMetadataSkip(path, options, checkSize, checkDates);
        tooLarge = reason == MetadataSkipReason.TooLarge;
        return reason != MetadataSkipReason.None;
    }

    internal static MetadataSkipReason ClassifyMetadataSkip(
        string path,
        SearchOptions options,
        bool checkSize = true,
        bool checkDates = true)
    {
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
            return MetadataSkipReason.None;

        // A size/date FILTER requires accurate metadata, so stat on a cache miss. The content-size
        // CEILING does NOT justify a stat here: every enumeration backend already enforces it
        // (Everything via a `size:<=ceiling` query predicate; the managed walker pre-caches size
        // from the directory entry). Statting the entire sweep just to re-check the ceiling was a
        // major source of latency, so for a ceiling-only check we trust a cache hit and otherwise
        // leave the file to be scanned (the backend already excluded oversized files).
        bool needAccurateMetadata = hasSizeFilter || hasDateFilter;

        FileMetadata metadata;
        if (FileMetadataCache.TryGet(path, out var cached))
        {
            metadata = cached;
        }
        else if (needAccurateMetadata)
        {
            FileInfo fileInfo;
            try { fileInfo = new FileInfo(path); }
            catch (Exception ex)
            {
                YaguLog.For("SearchService").LogDebug(ex, "Cannot stat file for size filter: {Path}", path);
                return MetadataSkipReason.None;
            }
            if (!fileInfo.Exists)
                return MetadataSkipReason.None;

            metadata = new FileMetadata(fileInfo.Length, fileInfo.LastWriteTime, fileInfo.CreationTime);
            FileMetadataCache.Set(path, metadata);
        }
        else
        {
            // Ceiling-only check with no cached size: the backend already excluded oversized files.
            return MetadataSkipReason.None;
        }

        if (checkSize && minBytes > 0 && metadata.Length < minBytes)
            return MetadataSkipReason.TooSmall;

        if (checkSize && maxBytes > 0 && metadata.Length > maxBytes)
            return MetadataSkipReason.TooLarge;

        // Built-in ceiling: skip files > 100MB when no explicit max is set.
        if (hasCeiling && metadata.Length > FileLister.ContentSearchFileSizeCeiling)
            return MetadataSkipReason.TooLarge;

        if (checkDates && FileLister.IsOutsideDateRange(metadata.Created, options.CreatedAfterDate, options.CreatedBeforeDate))
            return MetadataSkipReason.DateRange;

        if (checkDates && FileLister.IsOutsideDateRange(metadata.LastModified, options.ModifiedAfterDate, options.ModifiedBeforeDate))
            return MetadataSkipReason.DateRange;

        return MetadataSkipReason.None;
    }

    /// <summary>
    /// The effective absolute stop-count for a scan. When <see cref="SearchOptions.MaxResults"/> is
    /// greater than 0 that user cap wins; when it is 0 (unlimited) the
    /// <see cref="SearchOptions.AbsoluteMaxResults"/> safety ceiling applies so an unbounded content
    /// search (e.g. a match-everything regex over huge minified files) cannot exhaust memory. Returns
    /// 0 only when BOTH are disabled — truly unbounded.
    /// </summary>
    internal static int EffectiveHardCap(SearchOptions options)
        => options.MaxResults > 0
            ? options.MaxResults
            : Math.Max(0, options.AbsoluteMaxResults);

    private static SearchOptions CopyOptions(
        SearchOptions options,
        string? query = null,
        bool? useRegex = null,
        int? contextLines = null,
        int? maxResults = null,
        SearchMode? searchMode = null,
        bool? useContentIndex = null,
        bool? stopAfterNameFirstPass = null,
        bool? suppressNameFirstPass = null,
        IReadOnlySet<string>? preEmittedFileNamePaths = null,
        IReadOnlySet<string>? preScannedContentPaths = null)
        => new()
        {
            Directory = options.Directory,
            Query = query ?? options.Query,
            CaseSensitive = options.CaseSensitive,
            UseRegex = useRegex ?? options.UseRegex,
            ExactMatch = options.ExactMatch,
            Multiline = options.Multiline,
            MultilineDotAll = options.MultilineDotAll,
            MaxMultilineBytes = options.MaxMultilineBytes,
            MultilineEngine = options.MultilineEngine,
            ContextLines = contextLines ?? options.ContextLines,
            SearchMode = searchMode ?? options.SearchMode,
            StopAfterNameFirstPass = stopAfterNameFirstPass ?? options.StopAfterNameFirstPass,
            SuppressNameFirstPass = suppressNameFirstPass ?? options.SuppressNameFirstPass,
            PreEmittedFileNamePaths = preEmittedFileNamePaths ?? options.PreEmittedFileNamePaths,
            PreScannedContentPaths = preScannedContentPaths ?? options.PreScannedContentPaths,
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
            MaxMatchesPerLine = options.MaxMatchesPerLine,
            AbsoluteMaxResults = options.AbsoluteMaxResults,
            SkipBinary = options.SkipBinary,
            AvoidSourceMemoryMap = options.AvoidSourceMemoryMap,
            FileIoTimeoutSeconds = options.FileIoTimeoutSeconds,
            UseContentIndex = useContentIndex ?? options.UseContentIndex,
            ContentIndexGateFactory = useContentIndex == false ? null : options.ContentIndexGateFactory,
            ContentIndexShadowScanFactory = useContentIndex == false ? null : options.ContentIndexShadowScanFactory,
            ContentIndexPruningScanFactory = useContentIndex == false ? null : options.ContentIndexPruningScanFactory,
            ExtendedSourceGateFactory = options.ExtendedSourceGateFactory,
            SearchHiddenFiles = options.SearchHiddenFiles,
            SearchOnlineOnlyFiles = options.SearchOnlineOnlyFiles,
            MaxSearchDepth = options.MaxSearchDepth,
            ObeyGitignore = options.ObeyGitignore,
            GitignoreTakesPrecedence = options.GitignoreTakesPrecedence,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            FileListerBackendOverride = options.FileListerBackendOverride,
            IoOversubscriptionIndex = options.IoOversubscriptionIndex,
            MaxProcessMemoryBytes = options.MaxProcessMemoryBytes,
            MemoryPressurePercent = options.MemoryPressurePercent,
            SkipExtensions = options.SkipExtensions,
            SearchInsideArchives = options.SearchInsideArchives,
            ArchiveExtensions = options.ArchiveExtensions,
            SearchImageText = options.SearchImageText,
            ImageOcrExtensions = options.ImageOcrExtensions,
            ImageOcrEngine = options.ImageOcrEngine,
            ImageOcrModel = options.ImageOcrModel,
            ImageOcrMaxSide = options.ImageOcrMaxSide,
            ImageOcrWorkerParallelism = options.ImageOcrWorkerParallelism,
            SearchPdfText = options.SearchPdfText,
            PdfTextExtensions = options.PdfTextExtensions,
            SdkChannelBufferSize = options.SdkChannelBufferSize,
            ExcludeAdminProtectedPaths = options.ExcludeAdminProtectedPaths,
            AdminProtectedPathSegments = options.AdminProtectedPathSegments,
            DirectOutputStream = options.DirectOutputStream,
            DirectOutputColor = options.DirectOutputColor,
            DirectOutputLock = options.DirectOutputLock,
            DegradedResultStore = options.DegradedResultStore,
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
            YaguLog.For("SearchService").LogInformation(
                "GC diag: managedHeap={ManagedHeapMB}MB, committed={CommittedMB}MB, heapSize={HeapSizeMB}MB, gen0={Gen0}, gen1={Gen1}, gen2={Gen2}",
                managedHeap / (1024*1024), gcInfo.TotalCommittedBytes / (1024*1024), gcInfo.HeapSizeBytes / (1024*1024), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            YaguLog.For("SearchService").LogInformation("Requesting GC for memory pressure relief");
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
        if (SystemMemoryProvider.TryGetSnapshot(out SystemMemorySnapshot snapshot))
        {
            systemLoadPercent = snapshot.LoadPercent;
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
            if (SystemMemoryProvider.TryGetSnapshot(out SystemMemorySnapshot snapshot))
            {
                long availMB = (long)(snapshot.AvailablePhysicalBytes / (1024UL * 1024));
                long totalMB = (long)(snapshot.TotalPhysicalBytes / (1024UL * 1024));
                long autoCapMB = AutoProcessMemoryCap() / (1024 * 1024);
                return $"system={snapshot.LoadPercent}% ({availMB:N0}/{totalMB:N0} MB avail), process WS={wsMB:N0} MB, autoCap={autoCapMB:N0} MB";
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
            if (SystemMemoryProvider.TryGetSnapshot(out SystemMemorySnapshot snapshot))
                return ComputeAutoProcessMemoryCap(snapshot.TotalPhysicalBytes);
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

    /// <summary>
    /// Returns currently-available physical memory in bytes (Win32 <c>ullAvailPhys</c>), or a
    /// conservative 2 GB fallback if the query fails. Used to size the dedicated multiline
    /// file-concurrency degree so whole-file buffering stays within the RAM budget.
    /// </summary>
    internal static long GetAvailablePhysicalMemoryBytes()
    {
        try
        {
            if (SystemMemoryProvider.TryGetSnapshot(out SystemMemorySnapshot snapshot))
                return (long)snapshot.AvailablePhysicalBytes;
        }
        catch { }
        return 2L * 1024 * 1024 * 1024;
    }

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
        lock (s_trimLock)
        {
            long now = Stopwatch.GetTimestamp();
            long last = s_lastTrimTicks;
            if (last != 0 && (now - last) < TrimMinIntervalTicks)
                return;
            s_lastTrimTicks = now;
        }
        try
        {
            WorkingSetTrimmer();
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Sink for the streaming scanner. Per-file tracking grows dynamically as
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
        private int _totalEmitted;

        public bool Truncated { get; private set; }
        public int TotalEmitted => _totalEmitted;
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
            int lineBytes = ClampNativeByteLength(view.LineLen);
            int matchStartBytes = view.MatchStart > int.MaxValue ? lineBytes : (int)view.MatchStart;
            int? sourceMatchStartBytes = view.SourceMatchStart > int.MaxValue ? (int?)null : (int)view.SourceMatchStart;
            int matchLenBytes = view.MatchLen > int.MaxValue ? 0 : (int)view.MatchLen;
            int lineNum = view.LineNumber > int.MaxValue ? int.MaxValue : (int)view.LineNumber;

            // ── Degraded fast-path: write raw UTF-8 directly to disk, skip String alloc ──
            // PERF-CRITICAL HOT PATH. This callback runs once per match on a
            // full-disk degraded scan (millions of matches/sec across 24 native
            // workers); it is the throughput-defining region. Keep per-match work
            // bounded and avoid scanning the whole line: never run a full-line
            // GetCharCount / UTF-16 column over a giant non-ASCII prefix here (it
            // regressed full-`C:\` "test" ~4x — see SOURCE_MATCH_START note in
            // repo memory `yagu-profiling.md`). Derive columns from the already-
            // computed display window or a cheap ASCII fast path instead.
            //
            // Pre-evicts each match to the ResultStore so memory stays bounded (the
            // payload lives on disk; only a lightweight pre-evicted SearchResult is
            // kept). This is what keeps degraded full-disk scans fast and lets the
            // memory-pressure path actually free memory instead of livelocking.
            if (Volatile.Read(ref _degraded) != 0 && _resultStore != null)
            {
                // Truncate match line bytes the same way DecodeMatchLine would (window around match)
                int maxDisplayBytes = (LineTruncator.MaxDisplayLength + 1) * 4;
                ReadOnlySpan<byte> matchLineUtf8;
                int charMatchStart, charMatchLen;
                if (lineBytes <= maxDisplayBytes)
                {
                    matchLineUtf8 = new ReadOnlySpan<byte>(view.LinePtr, lineBytes);
                    int safeStart = Math.Min(matchStartBytes, lineBytes);
                    int safeLen = Math.Min(matchLenBytes, lineBytes - safeStart);
                    if (Ascii.IsValid(matchLineUtf8[..Math.Min(safeStart + safeLen, lineBytes)]))
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
                    int windowBytes = Math.Max(matchLenBytes, maxDisplayBytes);
                    int contextBytes = Math.Max(0, (windowBytes - matchLenBytes) / 2);
                    int windowStart = Math.Max(0, matchStartBytes - contextBytes);
                    int windowEnd = Math.Min(lineBytes, windowStart + windowBytes);
                    if (windowEnd - windowStart < windowBytes)
                        windowStart = Math.Max(0, windowEnd - windowBytes);
                    while (windowStart < lineBytes && (view.LinePtr[windowStart] & 0xC0) == 0x80)
                        windowStart++;
                    while (windowEnd > windowStart && windowEnd < lineBytes && (view.LinePtr[windowEnd] & 0xC0) == 0x80)
                        windowEnd--;
                    matchLineUtf8 = new ReadOnlySpan<byte>(view.LinePtr + windowStart, windowEnd - windowStart);
                    int matchBytesFromWindow = Math.Max(0, matchStartBytes - windowStart);
                    int safeLenW = Math.Min(matchLenBytes, matchLineUtf8.Length - matchBytesFromWindow);
                    if (Ascii.IsValid(matchLineUtf8[..Math.Min(matchBytesFromWindow + safeLenW, matchLineUtf8.Length)]))
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

                // Full-line source column. When native provides it (metadata-only
                // mode), use it directly; otherwise derive it here cheaply. For a
                // non-windowed line the display column already is the full-line
                // column; for a windowed line take the ASCII fast path and fall
                // back to the byte offset rather than scanning a giant non-ASCII
                // prefix on the hot degraded path.
                int sourceMatchStart;
                if (sourceMatchStartBytes.HasValue)
                {
                    sourceMatchStart = Math.Max(0, sourceMatchStartBytes.Value);
                }
                else if (lineBytes <= maxDisplayBytes)
                {
                    sourceMatchStart = charMatchStart;
                }
                else
                {
                    int safeStartForCol = Math.Min(matchStartBytes, lineBytes);
                    sourceMatchStart = Ascii.IsValid(new ReadOnlySpan<byte>(view.LinePtr, safeStartForCol))
                        ? safeStartForCol
                        : matchStartBytes;
                }

                var result = SearchResult.CreatePreEvicted(filePath, lineNum, charMatchStart, charMatchLen, offset, sourceMatchStart);
                if (!TryWriteWithBackpressure(result))
                {
                    _stopped = true;
                    return 1;
                }

                if (_emitted[idx]++ == 0 && _filesWithMatchesPtr != null)
                    Interlocked.Increment(ref *_filesWithMatchesPtr);
                Interlocked.Increment(ref _totalEmitted);
                if (_totalMatchesPtr != null)
                    Interlocked.Increment(ref *_totalMatchesPtr);
                int totalDegraded = Interlocked.Increment(ref _runningTotal);
                if (_maxResults > 0 && totalDegraded >= _maxResults)
                {
                    Truncated = true;
                    _stopped = true;
                    if (_cancelPtr != null) *_cancelPtr = 1; // global hard stop across all native workers
                    return 1;
                }
                return 0;
            }

            // ── Normal path: decode to managed strings ──
            var matchLine = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(
                view.LinePtr, lineBytes, matchStartBytes, matchLenBytes, sourceMatchStartBytes);
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
                ContextAfter: after)
            { SourceMatchStartColumn = matchLine.SourceMatchStart };

            if (!TryWriteWithBackpressure(normalResult))
            {
                _stopped = true;
                return 1;
            }

            if (_emitted[idx]++ == 0 && _filesWithMatchesPtr != null)
                Interlocked.Increment(ref *_filesWithMatchesPtr);
            Interlocked.Increment(ref _totalEmitted);
            if (_totalMatchesPtr != null)
                Interlocked.Increment(ref *_totalMatchesPtr);
            int totalNormal = Interlocked.Increment(ref _runningTotal);
            if (_maxResults > 0 && totalNormal >= _maxResults)
            {
                Truncated = true;
                _stopped = true;
                if (_cancelPtr != null) *_cancelPtr = 1; // global hard stop across all native workers
                return 1;
            }
            return 0;
        }

        internal static int ClampNativeByteLength(nuint length)
            => length > (nuint)int.MaxValue ? int.MaxValue : (int)length;

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
    public sealed record SourceBackedMatchBatch(IReadOnlyList<SourceBackedMatch> Results) : SearchEvent;
    public sealed record Progress(SearchProgress Snapshot) : SearchEvent;
    public sealed record SearchError(string Message) : SearchEvent;
    public sealed record ScanCompleted(SearchSummary Summary) : SearchEvent;
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
