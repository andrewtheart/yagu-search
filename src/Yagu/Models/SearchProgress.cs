namespace Yagu.Models;

/// <summary>
/// Breakdown of why files were skipped during a search.
/// <para>
/// Two disjoint families live here. Most members are <b>counted</b> reasons: every one of them is
/// also part of <see cref="SearchSummary.FilesSkipped"/> / <see cref="SearchProgress.FilesSkipped"/>,
/// and together with <see cref="Unclassified"/> they partition that headline total exactly (see
/// <see cref="CountedTotal"/>). <see cref="GitignoreExcluded"/>,
/// <see cref="ExtensionExcludedAtDiscovery"/> and <see cref="CloudOnlyAtDiscovery"/> are
/// <b>discovery filters</b>: those paths never entered the scan set, so they are deliberately NOT
/// part of the headline total and must be reported separately.
/// </para>
/// <para>
/// New members are appended as optional positional parameters so existing positional construction
/// keeps compiling. Nothing here is on a hot path: each counter is incremented only on a branch
/// that is already skipping a file.
/// </para>
/// </summary>
public sealed record SkipBreakdown(
    int Binary,
    int AccessDenied,
    int IOError,
    int TooLarge,
    int NotFound,
    int Encoding,
    int Other,
    int ByExtension = 0,
    int Directories = 0,
    int EarlyFiltered = 0,
    int GlobExcluded = 0,
    int GitignoreExcluded = 0,
    int CloudOnly = 0,
    int MultilineSkipped = 0,
    int IoTimeout = 0,
    int TooSmall = 0,
    int DateFiltered = 0,
    int OcrCacheExcluded = 0,
    int ExtensionExcludedAtDiscovery = 0,
    int CloudOnlyAtDiscovery = 0)
{
    /// <summary>Cloud-only placeholders skipped by the content scanner. <see cref="CloudOnly"/> is the
    /// combined figure (scan-time + discovery-time); only this part is inside the headline skipped total.</summary>
    public int CloudOnlyDuringScan => Math.Max(0, CloudOnly - CloudOnlyAtDiscovery);

    /// <summary>Files excluded by an <c>--exclude</c> glob only. <see cref="GlobExcluded"/> historically also
    /// carried Yagu's own OCR-cache directory, which is now reported separately as <see cref="OcrCacheExcluded"/>.</summary>
    public int GlobOnlyExcluded => Math.Max(0, GlobExcluded - OcrCacheExcluded);

    /// <summary>Sum of every reason that is also part of the headline skipped total. Discovery filters
    /// (gitignore / extension / cloud-only-at-discovery) are excluded because those files never entered
    /// the scan set. <see cref="EarlyFiltered"/> is an aggregate parent of the size/date reasons and is
    /// likewise excluded so nothing is counted twice.</summary>
    public int CountedTotal =>
        Binary + AccessDenied + IOError + IoTimeout + TooLarge + TooSmall + DateFiltered
        + NotFound + Encoding + Other + ByExtension + Directories
        + GlobOnlyExcluded + OcrCacheExcluded + CloudOnlyDuringScan + MultilineSkipped;

    /// <summary>Files inside <paramref name="filesSkipped"/> that no category claimed. Normally 0; a
    /// non-zero value means a skip path exists that this breakdown does not yet classify, and the UI
    /// surfaces it rather than silently under-reporting.</summary>
    public int Unclassified(int filesSkipped) => Math.Max(0, filesSkipped - CountedTotal);

    /// <summary>Files removed by a discovery-time filter. These are intentionally absent from the
    /// headline skipped total — they were filtered before the scan set was formed.</summary>
    public int DiscoveryFilteredTotal =>
        GitignoreExcluded + ExtensionExcludedAtDiscovery + CloudOnlyAtDiscovery;

    public override string ToString() =>
        $"binary={Binary}, accessDenied={AccessDenied}, ioError={IOError}, ioTimeout={IoTimeout}, tooLarge={TooLarge}, tooSmall={TooSmall}, dateFiltered={DateFiltered}, notFound={NotFound}, encoding={Encoding}, other={Other}, byExtension={ByExtension}, directories={Directories}, earlyFiltered={EarlyFiltered}, globExcluded={GlobExcluded}, ocrCacheExcluded={OcrCacheExcluded}, gitignoreExcluded={GitignoreExcluded}, extExcludedAtDiscovery={ExtensionExcludedAtDiscovery}, cloudOnly={CloudOnly}, cloudOnlyAtDiscovery={CloudOnlyAtDiscovery}, multilineSkipped={MultilineSkipped}";
}

/// <summary>
/// Aggregate progress event emitted as a search runs.
/// </summary>
public sealed record SearchProgress(
    int FilesScanned,
    int TotalFiles,
    int MatchesFound,
    int FilesWithMatches,
    int FilesSkipped,
    long BytesScanned,
    TimeSpan Elapsed,
    int AccessDenied = 0,
    SkipBreakdown? SkipReasons = null)
{
    /// <summary>Optional progress for slower extracted-content work (image OCR and PDF text). Kept as
    /// an init-only property so the long-standing positional constructor/deconstruction API is unchanged.</summary>
    public SourceBackedSearchProgress? SourceBacked { get; init; }

    /// <summary>True while the fast filename "name-first" pass (and its brief priority content scan of the
    /// few name-matched files) is running, before the full-drive scan total is established. The UI keeps the
    /// progress bar indeterminate during this phase so it doesn't briefly fill to 100% against the tiny
    /// name-first total and then reset to 0 for the full content scan. Init-only to keep the positional API.</summary>
    public bool NameFirstPhase { get; init; }
}

/// <summary>
/// Progress for files routed away from the native text scanner into slower extraction workers. The
/// overall progress bar naturally reaches the mid/high 90s once ordinary files finish; this snapshot
/// lets the UI identify that phase and show an honest counter instead of an apparently frozen percent.
/// </summary>
public sealed record SourceBackedSearchProgress(
    int OcrProcessed,
    int OcrQueued,
    int PdfProcessed,
    int PdfQueued,
    bool DiscoveryComplete)
{
    public int OcrRemaining => Math.Max(0, OcrQueued - OcrProcessed);
    public int PdfRemaining => Math.Max(0, PdfQueued - PdfProcessed);
    public int Remaining => OcrRemaining + PdfRemaining;
    public int Queued => OcrQueued + PdfQueued;
    public int Processed => Math.Min(OcrProcessed, OcrQueued) + Math.Min(PdfProcessed, PdfQueued);

    /// <summary>Number of non-source-backed files already dealt with. Source-backed files are included
    /// once in <paramref name="filesProcessed"/> only when their extractor finishes, so subtract the
    /// completed source-backed count to isolate ordinary progress.</summary>
    public int OrdinaryProcessed(int filesProcessed) => Math.Max(0, filesProcessed - Processed);

    /// <summary>Total work units for the unified progress bar. The discovered total already includes each
    /// image/PDF once; <see cref="Queued"/> is exposed separately for the phase counter, not added again.</summary>
    public int OverallTotal(int totalFiles) => Math.Max(totalFiles, Queued);

    public double OverallPercent(int filesProcessed, int totalFiles)
    {
        int total = OverallTotal(totalFiles);
        return total <= 0 ? 0 : Math.Min(100.0, (double)Math.Min(filesProcessed, total) / total * 100.0);
    }

    /// <summary>Returns an OCR/PDF phase label only after discovery is complete and every non-source-
    /// backed file has been dealt with. OCR wins while any images remain because it is normally the
    /// dominant tail; PDFs are shown once OCR is complete.</summary>
    public string? BuildPhaseLabel(int filesProcessed, int totalFiles)
    {
        if (!DiscoveryComplete || Remaining == 0 || totalFiles <= 0)
            return null;

        // Source-backed extraction is the visible tail once ordinary work reaches the discovered
        // non-source count. Allow small discovery-total drift (e.g. early-filtered directories or a file
        // disappearing during enumeration) instead of requiring exact equality, which previously left a
        // long OCR run displayed as a frozen rounded percentage.
        int ordinaryTotal = Math.Max(0, totalFiles - Queued);
        int tolerance = Math.Max(32, Math.Min(2048, totalFiles / 1000));
        if (OrdinaryProcessed(filesProcessed) + tolerance < ordinaryTotal)
            return null;
        if (OcrRemaining > 0)
            return $"OCR: {OcrProcessed:N0} / {OcrQueued:N0} images";
        return $"PDF text: {PdfProcessed:N0} / {PdfQueued:N0} files";
    }

    public string? BuildCombinedLabel(int filesProcessed, int totalFiles)
    {
        string? phase = BuildPhaseLabel(filesProcessed, totalFiles);
        return phase is null ? null : $"{OverallPercent(filesProcessed, totalFiles):F0}% [{phase}]";
    }
}

/// <summary>
/// Final summary published when a search ends.
/// </summary>
public sealed record SearchSummary(
    int TotalFiles,
    int FilesScanned,
    int FilesSkipped,
    int FilesWithMatches,
    int TotalMatches,
    long BytesScanned,
    TimeSpan Elapsed,
    bool Cancelled,
    bool Truncated,
    bool Degraded,
    string? FallbackReason,
    SkipBreakdown? SkipReasons = null,
    IndexAccelerationInfo? IndexAcceleration = null);

/// <summary>
/// How the opt-in content index participated in a search (plan §6.2), for the main-window coverage
/// indicator. Counts are per-root and summed across a multi-root search: <see cref="RequestedRoots"/> is
/// how many searched roots opted into the index, <see cref="AcceleratedRoots"/> how many the index
/// actually accelerated, and <see cref="FilesPruned"/>/<see cref="FilesRescued"/> the pruning/rescue
/// totals. It is a pure diagnostic: pruning never changes results (a pruned file cannot contain a match
/// and rescued files are scanned live), so a null value simply means the index did not participate.
/// </summary>
public sealed record IndexAccelerationInfo(int RequestedRoots, int AcceleratedRoots, int FilesPruned, int FilesRescued);
