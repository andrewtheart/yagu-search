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
    int GlobExcluded = 0)
{
    public override string ToString() =>
        $"binary={Binary}, accessDenied={AccessDenied}, ioError={IOError}, tooLarge={TooLarge}, notFound={NotFound}, encoding={Encoding}, other={Other}, byExtension={ByExtension}, directories={Directories}, earlyFiltered={EarlyFiltered}, globExcluded={GlobExcluded}";
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
    SkipBreakdown? SkipReasons = null);

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
    SkipBreakdown? SkipReasons = null);
