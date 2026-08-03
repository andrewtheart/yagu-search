namespace Yagu.Services.Index;

/// <summary>A single skipped/failed path in a build report (plan §6.2).</summary>
public readonly record struct IndexBuildReportEntry(string Path, IndexSkipReason Reason);

/// <summary>
/// The structured, per-path build report (plan §6.2), modeled on DocFetcher's <c>IndexingReporter</c>.
/// It accumulates how many files were indexed and, for each skipped/failed file, the typed reason —
/// so a user can see which files always fall back to a live scan and why. The retained per-path entry
/// list is <b>bounded</b> (plan §3.4: never one unbounded report), while the per-reason counts are
/// exact. Diagnostics only; it never changes search results.
/// </summary>
public sealed class IndexBuildReport
{
    /// <summary>Default cap on retained per-path entries.</summary>
    public const int DefaultMaxEntries = 1000;

    private readonly int _maxEntries;
    private readonly Dictionary<IndexSkipReason, int> _skipCounts = new();
    private readonly List<IndexBuildReportEntry> _entries = new();

    public IndexBuildReport(int maxEntries = DefaultMaxEntries)
        => _maxEntries = Math.Max(0, maxEntries);

    /// <summary>Number of files admitted into the index.</summary>
    public int IndexedCount { get; private set; }

    /// <summary>Total number of files skipped or failed (across all reasons; exact, not capped).</summary>
    public int TotalSkipped { get; private set; }

    /// <summary>The retained per-path skip/fail entries (bounded by the configured max).</summary>
    public IReadOnlyList<IndexBuildReportEntry> Entries => _entries;

    /// <summary>Exact per-reason skip counts.</summary>
    public IReadOnlyDictionary<IndexSkipReason, int> SkipCounts => _skipCounts;

    /// <summary>True when some skip entries were dropped because the retained-entry cap was reached.</summary>
    public bool EntriesTruncated => TotalSkipped > _entries.Count;

    /// <summary>Records that a file was successfully indexed.</summary>
    public void RecordIndexed() => IndexedCount++;

    /// <summary>
    /// Records the outcome for a path. <see cref="IndexSkipReason.None"/> counts as indexed; any other
    /// reason increments the exact per-reason count and appends a bounded entry.
    /// </summary>
    public void Record(string path, IndexSkipReason reason)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (reason == IndexSkipReason.None)
        {
            RecordIndexed();
            return;
        }

        TotalSkipped++;
        _skipCounts[reason] = _skipCounts.GetValueOrDefault(reason) + 1;
        if (_entries.Count < _maxEntries)
            _entries.Add(new IndexBuildReportEntry(path, reason));
    }

    /// <summary>The exact number of files skipped for a specific reason.</summary>
    public int SkipCount(IndexSkipReason reason) => _skipCounts.GetValueOrDefault(reason);

    /// <summary>A short one-line summary suitable for the Indexing tab status / <c>--index-status</c>.</summary>
    public string Summarize()
    {
        if (TotalSkipped == 0)
            return $"{IndexedCount} indexed, 0 skipped.";
        var reasons = _skipCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}={kv.Value}");
        return $"{IndexedCount} indexed, {TotalSkipped} skipped ({string.Join(", ", reasons)}).";
    }
}
