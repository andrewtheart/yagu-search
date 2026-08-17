namespace Yagu.Services.Index;

/// <summary>Progress stage names reported while building a complete replacement index.</summary>
public static class IndexBuildStages
{
    /// <summary>Crawling source files, reading content, and building the raw index.</summary>
    public const string RawBuild = "rawBuild";

    /// <summary>Extracting and indexing text from PDF files.</summary>
    public const string Pdf = "pdf";

    /// <summary>Extracting and indexing text from images with OCR.</summary>
    public const string Ocr = "ocr";

    /// <summary>Replaying changes that occurred while the full build was running.</summary>
    public const string PostBuildCatchUp = "postBuildCatchUp";
}

/// <summary>
/// Progress stage names an incremental (delta) index update reports alongside its percent estimate.
/// Every value keeps the <see cref="Incremental"/> prefix so the existing stage contract still holds:
/// any stage starting with it means "updating an existing index", never a full rebuild.
///
/// The phases exist because publishing a delta is not a quick finalization step. Resolving journal
/// changes, merging them into a segment, serializing that segment, and any follow-up coalescing are all
/// separately expensive on a large index, and lumping them together left the UI on one static label for
/// the majority of the update.
/// </summary>
public static class IndexUpdateStages
{
    /// <summary>Unphased incremental update (kept for older workers and generic callers).</summary>
    public const string Incremental = "incremental";

    /// <summary>Mapping journal records to paths and classifying changed file content.</summary>
    public const string Resolving = "incremental.resolving";

    /// <summary>Folding resolved changes and tombstones into the pending delta segment.</summary>
    public const string Merging = "incremental.merging";

    /// <summary>Serializing the delta segment.</summary>
    public const string Writing = "incremental.writing";

    /// <summary>Atomically extending the active pointer with the new segment.</summary>
    public const string Publishing = "incremental.publishing";

    /// <summary>Coalescing or compacting layers after the delta was durably appended.</summary>
    public const string Compacting = "incremental.compacting";

    /// <summary>Reading the active layer metadata an explicit compaction will fold.</summary>
    public const string CompactAnalyzing = "incremental.compacting.analyzing";

    /// <summary>Streaming every layer's records into the merged base.</summary>
    public const string CompactMerging = "incremental.compacting.merging";

    /// <summary>Validating and atomically publishing the compacted base.</summary>
    public const string CompactPublishing = "incremental.compacting.publishing";

    /// <summary>Removing one stored index after the warning dialog hands work to the status bar.</summary>
    public const string Deleting = "incremental.deleting";

    /// <summary>True when the stage denotes an update to an existing index rather than a full build.</summary>
    public static bool IsIncremental(string? stage)
        => stage is not null && stage.StartsWith(Incremental, StringComparison.OrdinalIgnoreCase);

    /// <summary>Percent boundaries so each phase advances a distinct, monotonic slice of the bar.</summary>
    public const int ResolveCeiling = 60;
    public const int MergeFloor = ResolveCeiling;
    public const int MergeCeiling = 88;
    public const int WriteFloor = MergeCeiling;
    public const int PublishFloor = 94;
    public const int CompactFloor = 97;
}

/// <summary>
/// Maps measured work from nested operations into one monotonic integer progress stream. Repeated
/// percentages are suppressed so record-level estimators never flood the worker protocol.
/// </summary>
internal sealed class IndexProgressReporter(Action<int>? callback)
{
    private int _lastPercent = -1;

    public void Report(int percent)
    {
        int bounded = Math.Clamp(percent, 0, 100);
        if (callback is null || bounded <= _lastPercent)
            return;
        _lastPercent = bounded;
        callback(bounded);
    }

    public void ReportFraction(long completed, long total, int startPercent, int endPercent)
        => Report(Scale(completed, total, startPercent, endPercent));

    public Action<int>? Slice(int startPercent, int endPercent)
        => callback is null
            ? null
            : percent => Report(Scale(percent, 100, startPercent, endPercent));

    internal static int Scale(long completed, long total, int startPercent, int endPercent)
    {
        int start = Math.Clamp(startPercent, 0, 100);
        int end = Math.Clamp(endPercent, start, 100);
        if (total <= 0)
            return start;
        long bounded = Math.Clamp(completed, 0, total);
        double ratio = (double)bounded / total;
        return start + (int)Math.Floor(ratio * (end - start));
    }
}
