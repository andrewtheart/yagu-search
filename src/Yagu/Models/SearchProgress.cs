namespace Yagu.Models;

/// <summary>
/// Breakdown of why files were skipped during a search.
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
    int IoTimeout = 0)
{
    public override string ToString() =>
        $"binary={Binary}, accessDenied={AccessDenied}, ioError={IOError}, ioTimeout={IoTimeout}, tooLarge={TooLarge}, notFound={NotFound}, encoding={Encoding}, other={Other}, byExtension={ByExtension}, directories={Directories}, earlyFiltered={EarlyFiltered}, globExcluded={GlobExcluded}, gitignoreExcluded={GitignoreExcluded}, cloudOnly={CloudOnly}, multilineSkipped={MultilineSkipped}";
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
