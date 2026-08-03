using System.Globalization;

namespace Yagu.Services.Index;

/// <summary>
/// How a search used the content index, for the optional main-window status glyph/label (plan §6.2).
/// Derived from per-root accelerated/live counts so the UI never invents a fourth source of truth.
/// </summary>
public enum IndexSearchCoverage
{
    /// <summary>The index feature is disabled or was not used for this search.</summary>
    Off,

    /// <summary>The index was available but bypassed for every root (ineligible query, over budget, untrusted).</summary>
    Bypassed,

    /// <summary>Some roots were index-accelerated and some were live-scanned.</summary>
    Partial,

    /// <summary>Every covered root was index-accelerated.</summary>
    Full,
}

/// <summary>
/// The per-result candidacy provenance shown as a glyph in the results list and preview header
/// (plan §6.2). This is provenance <em>of candidacy only</em> — match content is always read live from
/// the file at scan time. The three states are derived from the single §3.5 path classification so the
/// UI cannot disagree with routing/status/tests.
/// </summary>
public enum IndexProvenanceKind
{
    /// <summary>A fresh, trusted, posting-selected ordinary-text file.</summary>
    IndexAccelerated,

    /// <summary>Fell back to a live scan (dirty per USN, unindexed, over-cap, or under an untrusted/uncovered root).</summary>
    LiveScanned,

    /// <summary>An archive/OCR/PDF candidate served through its extractor namespace (or the pre-namespace bypass).</summary>
    ExtractedSource,
}

/// <summary>
/// Whether a usable index actually exists for the folder(s) a search is running over (plan §6.2). This
/// is <b>availability</b>, not pruning-coverage: it is knowable today from generation existence alone
/// (no USN journal, no worker, no pruning), so it is safe and honest to show before the deferred
/// hot-path integration lands. It never implies acceleration — the tooltip states plainly that files
/// are still read live in this build.
/// </summary>
public enum IndexAvailability
{
    /// <summary>The feature is off (should not be shown).</summary>
    Off,

    /// <summary>The feature is on but this search opted out (Advanced Options toggle / <c>--no-index</c>).</summary>
    NotRequested,

    /// <summary>Opted in, but no searched folder has an index yet.</summary>
    None,

    /// <summary>Opted in, and some (not all) searched folders have an index.</summary>
    Partial,

    /// <summary>Opted in, and every searched folder has an index.</summary>
    Available,
}

/// <summary>Health state for one ready local drive or explicitly maintained index root in the
/// main-window's launch-time overview.</summary>
public enum IndexRootHealthKind
{
    NotIndexed,
    LeftoverIndex,
    Healthy,
    ChangesPending,
    RebuildRequired,
    FreshnessUnavailable,
    BuildRequired,
    StorageProblem,
}

/// <summary>One immutable row in the launch-time all-drive index-health snapshot.</summary>
public readonly record struct IndexRootHealthEntry(
    string Root,
    IndexRootHealthKind Kind,
    string Status,
    string? RepairRoot = null,
    string? IncrementalRoot = null,
    string? MaintainRoot = null,
    string? DeleteRoot = null)
{
    public bool NeedsAttention => Kind is IndexRootHealthKind.RebuildRequired
        or IndexRootHealthKind.FreshnessUnavailable
        or IndexRootHealthKind.BuildRequired
        or IndexRootHealthKind.StorageProblem;

    public bool HasStoredIndex => Kind is IndexRootHealthKind.LeftoverIndex
        or IndexRootHealthKind.Healthy
        or IndexRootHealthKind.ChangesPending
        or IndexRootHealthKind.RebuildRequired
        or IndexRootHealthKind.FreshnessUnavailable
        or IndexRootHealthKind.StorageProblem;

    public bool IsHealthy => Kind is IndexRootHealthKind.Healthy or IndexRootHealthKind.ChangesPending;

    /// <summary>Whether this row belongs in aggregate health counts. Unregistered ready drives remain
    /// visible for context, but neither an unindexed drive nor leftover unmaintained data is a maintained
    /// index whose health Yagu should score.</summary>
    public bool IsIncludedInOverallHealth => Kind is not (IndexRootHealthKind.NotIndexed or IndexRootHealthKind.LeftoverIndex);

    public bool CanRepair => !string.IsNullOrWhiteSpace(RepairRoot);

    /// <summary>Whether the row can first try a bounded incremental replay instead of requiring a
    /// complete rebuild. Currently used for a change-journal catch-up-limit stop.</summary>
    public bool CanIncrementallyRefresh => !string.IsNullOrWhiteSpace(IncrementalRoot);

    /// <summary>Whether valid leftover data can be enrolled in automatic maintenance without rebuilding it.</summary>
    public bool CanMaintain => !string.IsNullOrWhiteSpace(MaintainRoot);

    /// <summary>Whether this row identifies exact unmaintained scope data that can be deleted.</summary>
    public bool CanDeleteStoredIndex => !string.IsNullOrWhiteSpace(DeleteRoot);
}

/// <summary>
/// Pure presentation helpers for the Indexing feature's UI (plan §6.2). Every decision here is a pure
/// function of the §3.5 classification plus persisted settings, so the WinUI Settings/MainWindow layers
/// stay thin and this logic is unit-tested for coverage. It renders glyphs + tooltips and never mutates
/// state. The tooltips must always state that match content is read live from the file.
/// </summary>
public static class ContentIndexUiStatus
{
    /// <summary>
    /// Maps the single §3.5 path classification to a candidacy provenance. A
    /// <see cref="IndexPathClassification.FreshIndexedNonmember"/> is provisionally pruned and never
    /// surfaces as a result, so it is treated as live-scanned if it is ever asked about.
    /// </summary>
    public static IndexProvenanceKind ProvenanceFor(IndexPathClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return classification switch
        {
            IndexPathClassification.FreshIndexedMember => IndexProvenanceKind.IndexAccelerated,
            IndexPathClassification.SpecialSource => IndexProvenanceKind.ExtractedSource,
            _ => IndexProvenanceKind.LiveScanned,
        };
    }

    /// <summary>A Segoe Fluent/MDL2 glyph for the provenance state (never color-only, per winui-conventions).</summary>
    public static string ProvenanceGlyph(IndexProvenanceKind kind) => kind switch
    {
        IndexProvenanceKind.IndexAccelerated => "\uE9F5", // speedometer — "faster via index"
        IndexProvenanceKind.ExtractedSource => "\uE8A5",  // document — extracted-text source
        _ => "\uE721",                                    // magnifier — live scan
    };

    /// <summary>A short label for the provenance state (used in tooltips and the build report).</summary>
    public static string ProvenanceLabel(IndexProvenanceKind kind) => kind switch
    {
        IndexProvenanceKind.IndexAccelerated => "Index-accelerated",
        IndexProvenanceKind.ExtractedSource => "Extracted source",
        _ => "Live-scanned",
    };

    /// <summary>
    /// The provenance tooltip. It always states that content is read live at scan time so the glyph can
    /// never imply a stale/wrong match (plan §6.2 / §8 "Misleading provenance UI").
    /// </summary>
    public static string ProvenanceTooltip(IndexProvenanceKind kind)
    {
        string how = kind switch
        {
            IndexProvenanceKind.IndexAccelerated => "The content index selected this file as a candidate.",
            IndexProvenanceKind.ExtractedSource => "This file was served through its extractor (archive/image/PDF).",
            _ => "This file was read directly, not via the index.",
        };
        return how + " Match content is always read live from the file at scan time — the index only decides which files are scanned.";
    }

    /// <summary>
    /// Whether the per-result provenance glyph should be shown at all: only when the master feature is
    /// enabled, the setting is on, and the index actually participated in this search (plan §6.2), so
    /// non-index users never see it and the layout is unchanged.
    /// </summary>
    public static bool ShouldShowProvenance(bool enableContentIndex, bool showProvenanceSetting, bool indexParticipated)
        => enableContentIndex && showProvenanceSetting && indexParticipated;

    /// <summary>
    /// Computes overall search coverage from per-root accelerated/live-scanned counts. Off when the
    /// feature is disabled or unused; Bypassed when nothing was accelerated; otherwise Full/Partial.
    /// </summary>
    public static IndexSearchCoverage Coverage(bool enabled, bool usedThisSearch, int acceleratedRoots, int liveScannedRoots)
    {
        if (!enabled || !usedThisSearch)
            return IndexSearchCoverage.Off;
        if (acceleratedRoots <= 0)
            return IndexSearchCoverage.Bypassed;
        return liveScannedRoots > 0 ? IndexSearchCoverage.Partial : IndexSearchCoverage.Full;
    }

    /// <summary>Whether the main-window status glyph should be shown (setting on and feature enabled).</summary>
    public static bool ShouldShowStatus(bool enableContentIndex, bool showStatusSetting)
        => enableContentIndex && showStatusSetting;

    /// <summary>The caution triangle used by every attention-needed status; the indicator paints this
    /// glyph amber, so producers must use this exact value rather than repeating the code point.</summary>
    public const string StatusWarningGlyph = "\uE7BA";

    /// <summary>Characters the fixed-width status-bar index label can render before the layout has to
    /// ellipsize it (Consolas 12 in a 210px slot).</summary>
    public const int StatusLabelMaxLength = 31;

    /// <summary>Clamps a status-bar index label to <see cref="StatusLabelMaxLength"/>, preferring a word
    /// boundary so an over-long label degrades to whole words instead of a mid-word cut.</summary>
    public static string TrimStatusLabel(string? label)
    {
        if (string.IsNullOrEmpty(label) || label.Length <= StatusLabelMaxLength)
            return label ?? string.Empty;

        int cut = StatusLabelMaxLength - 1; // leave room for the ellipsis
        int lastSpace = label.LastIndexOf(' ', cut - 1);
        if (lastSpace > StatusLabelMaxLength / 2)
            cut = lastSpace;
        return string.Concat(label.AsSpan(0, cut).TrimEnd(), "\u2026");
    }

    /// <summary>Classifies a ready drive/root that is no longer in the maintained-root list. Physical
    /// residue is surfaced honestly for cleanup, but is informational and never a freshness warning.</summary>
    internal static IndexRootHealthEntry UnregisteredRootHealth(string root, bool hasStoredIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return hasStoredIndex
            ? new IndexRootHealthEntry(
                root,
                IndexRootHealthKind.LeftoverIndex,
                "leftover index — not maintained; ignored by overall health",
                MaintainRoot: root,
                DeleteRoot: root)
            : new IndexRootHealthEntry(
                root,
                IndexRootHealthKind.NotIndexed,
                "not indexed — not maintained; excluded from overall health");
    }

    /// <summary>Concise status-bar label for the launch-time all-drive health snapshot. A specific
    /// failure label is used only when it describes every health-tracked root; a mixed snapshot reports
    /// the affected count instead. Unregistered drives remain visible in details, but unindexed drives and
    /// leftover unmaintained indexes are excluded from totals so informational rows cannot lower health.</summary>
    public static string AllDriveHealthLabel(
        IReadOnlyCollection<IndexRootHealthEntry> roots,
        IndexSearchCoverage? activeSearchCoverage = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
            return "Index: no ready drives";

        int healthRootCount = roots.Count(static root => root.IsIncludedInOverallHealth);
        if (healthRootCount == 0)
            return "Index: no maintained indexes";

        int attention = roots.Count(static root => root.NeedsAttention);
        if (attention > 0)
        {
            string attentionCount = attention == 1
                ? $"1 of {healthRootCount} needs attention"
                : $"{attention} of {healthRootCount} need attention";
            if (activeSearchCoverage is IndexSearchCoverage.Full or IndexSearchCoverage.Partial)
            {
                string activity = activeSearchCoverage == IndexSearchCoverage.Full
                    ? "accelerating"
                    : "partially accelerating";
                return $"Index: {activity} ({attentionCount})";
            }

            int rebuilds = roots.Count(static root => root.Kind == IndexRootHealthKind.RebuildRequired);
            if (rebuilds == healthRootCount)
                return rebuilds == 1 ? "Index: rebuild required" : $"Index: {rebuilds} rebuilds required";

            int builds = roots.Count(static root => root.Kind == IndexRootHealthKind.BuildRequired);
            if (builds == healthRootCount)
                return builds == 1 ? "Index: 1 drive needs build" : $"Index: {builds} drives need build";

            int unavailable = roots.Count(static root => root.Kind == IndexRootHealthKind.FreshnessUnavailable);
            if (unavailable == healthRootCount)
                return unavailable == 1 ? "Index: freshness unavailable" : $"Index: {unavailable} freshness checks unavailable";

            return $"Index: {attentionCount}";
        }

        int healthy = roots.Count(static root => root.IsHealthy);
        if (healthy == healthRootCount)
            return "Indexes: all healthy";
        if (healthy == 0)
            return "Index: no maintained indexes";
        return $"Index: {healthy}/{healthRootCount} drives healthy";
    }

    /// <summary>Glyph paired with <see cref="AllDriveHealthLabel"/>.</summary>
    public static string AllDriveHealthGlyph(IReadOnlyCollection<IndexRootHealthEntry> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Any(static root => root.NeedsAttention))
            return StatusWarningGlyph;
        return roots.Any(static root => root.HasStoredIndex)
            ? "\uE9F5" // speedometer
            : "\uEA39"; // outline circle
    }

    /// <summary>Plain-language overview shown above the per-drive rows in the status hover surface.</summary>
    public static string AllDriveHealthSummary(IReadOnlyCollection<IndexRootHealthEntry> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
            return "No ready local drives or maintained index roots were found.";

        int leftovers = roots.Count(static root => root.Kind == IndexRootHealthKind.LeftoverIndex);
        int unindexed = roots.Count(static root => root.Kind == IndexRootHealthKind.NotIndexed);
        int healthRootCount = roots.Count(static root => root.IsIncludedInOverallHealth);
        string excludedSummary = ExcludedHealthRowsSummary(unindexed, leftovers);
        if (healthRootCount == 0)
            return "No ready local drive currently has a maintained content index. " + excludedSummary;

        int attention = roots.Count(static root => root.NeedsAttention);
        if (attention > 0)
        {
            string attentionSummary = attention == 1
                ? "One drive or indexed folder needs attention. Its searches safely fall back to live scanning when necessary."
                : $"{attention} drives or indexed folders need attention. Their searches safely fall back to live scanning when necessary.";
            return healthRootCount < roots.Count
                ? attentionSummary + " " + excludedSummary
                : attentionSummary;
        }

        int healthy = roots.Count(static root => root.IsHealthy);
        if (healthy == healthRootCount)
        {
            int pending = roots.Count(static root => root.Kind == IndexRootHealthKind.ChangesPending);
            if (healthRootCount < roots.Count)
            {
                string maintainedSummary = pending == 0
                    ? "Every maintained index included in overall health is healthy and up to date."
                    : "Every maintained index included in overall health is healthy. Recent journal-proven changes remain safe and are scanned live until the next incremental update.";
                return maintainedSummary + " " + excludedSummary;
            }

            return pending == 0
                ? "Every ready local drive and maintained index root has a healthy, up-to-date content index."
                : "Every ready local drive and maintained index root has a healthy content index. Recent journal-proven changes remain safe and are scanned live until the next incremental update.";
        }
        if (healthy == 0)
            return "No ready local drive currently has a maintained content index. " + excludedSummary;
        if (healthRootCount < roots.Count)
            return $"{healthy} of {healthRootCount} maintained indexes are healthy. {excludedSummary}";
        return $"{healthy} of {healthRootCount} ready drives or maintained roots have a healthy content index.";
    }

    private static string ExcludedHealthRowsSummary(int unindexed, int leftovers)
    {
        if (unindexed > 0 && leftovers > 0)
            return "Unindexed unmaintained drives and leftover index data are informational and excluded from overall health totals and warnings; searches scan live or verify leftover data before use.";
        if (unindexed > 0)
            return "Unindexed unmaintained drives are informational and excluded from overall health totals and warnings; their searches scan live.";
        return "Leftover unmaintained index data is informational and excluded from overall health totals and warnings; searches verify it before use or scan live.";
    }

    /// <summary>A short status label for the main window.</summary>
    public static string CoverageLabel(IndexSearchCoverage coverage) => coverage switch
    {
        IndexSearchCoverage.Full => "Index: full",
        IndexSearchCoverage.Partial => "Index: partial coverage",
        IndexSearchCoverage.Bypassed => "Index: bypassed",
        _ => "Index: off",
    };

    /// <summary>A Segoe Fluent/MDL2 glyph for the coverage state.</summary>
    public static string CoverageGlyph(IndexSearchCoverage coverage) => coverage switch
    {
        IndexSearchCoverage.Full => "\uE9F5",       // speedometer
        IndexSearchCoverage.Partial => "\uE7BA",    // caution (partial)
        IndexSearchCoverage.Bypassed => "\uE7BA",   // caution (bypassed)
        _ => "\uE721",                              // magnifier (off)
    };

    /// <summary>
    /// The coverage tooltip shown after a search completes. States how the index participated and how
    /// many files it skipped, and always affirms that matching files are read live from disk so the glyph
    /// can never imply a stale/wrong match (plan §6.2 / §8 "Misleading provenance UI").
    /// </summary>
    public static string CoverageTooltip(IndexSearchCoverage coverage, int filesPruned)
    {
        string head = coverage switch
        {
            IndexSearchCoverage.Full => "The content index accelerated this search — every covered folder used the index.",
            IndexSearchCoverage.Partial => "The content index accelerated part of this search; the other folders were scanned live.",
            IndexSearchCoverage.Bypassed => "The content index was available but not used for this search (e.g. a case-insensitive or ineligible query, or no trusted index for the folder).",
            _ => "The content index was not used for this search.",
        };
        string skipped = (coverage is IndexSearchCoverage.Full or IndexSearchCoverage.Partial) && filesPruned > 0
            ? string.Create(CultureInfo.InvariantCulture, $" It skipped {filesPruned:N0} file(s) that cannot contain a match.")
            : string.Empty;
        return head + skipped + " Matching files are always read live from disk.";
    }

    /// <summary>
    /// A one-line content-index summary for the CLI completion output (plan §6.2 — parity with the GUI
    /// coverage indicator). Returns null when the index did not participate, so the CLI prints nothing
    /// extra for ordinary live-scan searches.
    /// </summary>
    public static string? CoverageCliSummary(IndexSearchCoverage coverage, int filesPruned) => coverage switch
    {
        IndexSearchCoverage.Full => filesPruned > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Content index: accelerated - skipped {filesPruned:N0} file(s) that cannot contain a match.")
            : "Content index: accelerated.",
        IndexSearchCoverage.Partial => string.Create(CultureInfo.InvariantCulture,
            $"Content index: partial - skipped {filesPruned:N0} file(s); other folders were scanned live."),
        IndexSearchCoverage.Bypassed => "Content index: available but not used for this search.",
        _ => null,
    };

    /// <summary>
    /// The effective session default for whether a search uses the index: the master feature must be
    /// on AND the persisted default must opt in (matches the CLI's derivation, plan §6.1/§6.3).
    /// </summary>
    public static bool EffectiveDefaultUseIndex(bool enableContentIndex, bool useContentIndexByDefault)
        => enableContentIndex && useContentIndexByDefault;

    /// <summary>Formats a megabyte quota/floor for display, e.g. <c>4096 MB (4.0 GB)</c>.</summary>
    public static string FormatMegabytes(int megabytes)
    {
        if (megabytes <= 0)
            return "unset (uses default)";
        if (megabytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB");
        double gib = megabytes / 1024.0;
        return string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB ({gib:F1} GB)");
    }

    /// <summary>Formats a byte count as a human-readable size (e.g. <c>0 B</c>, <c>512 KB</c>, <c>1.7 GB</c>).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1} {units[unit]}");
    }

    /// <summary>
    /// Formats the Indexing tab's storage-stats block (plan §6.2): a header with the total size / index
    /// count / stored content-record count, then one indented line per index (largest first) with its root,
    /// size, record count, segment count, and build time. Pure so it is unit-tested; the tab just joins the
    /// lines into a text block.
    /// </summary>
    public static IReadOnlyList<string> FormatStorageLines(IndexStorageSummary summary)
    {
        var lines = new List<string>();
        if (summary.Indexes.Count == 0)
        {
            lines.Add("Index storage: empty (no indexes built yet).");
            return lines;
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"Index storage: {FormatBytes(summary.TotalSizeBytes)} across {summary.Indexes.Count} index(es), {summary.TotalDocuments:N0} stored content records."));

        foreach (IndexStorageStat idx in summary.Indexes)
        {
            string name = !string.IsNullOrEmpty(idx.RootPath) ? idx.RootPath! : idx.ScopeId;
            if (idx.Health != IndexStorageHealth.Healthy)
            {
                string action = idx.CanRepair
                    ? "Select it above and click Repair index."
                    : "The stored index can be deleted; its source cannot be repaired from this location.";
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  {name}: {FormatBytes(idx.SizeBytes)} ({StorageHealthLabel(idx.Health)}. {idx.Problem ?? "No trustworthy root metadata was found."} {action})"));
                continue;
            }

            string layers = idx.SegmentCount == 0
                ? "single generation"
                : string.Create(CultureInfo.InvariantCulture, $"base + {idx.SegmentCount} segment(s)");
            string built = idx.BuiltUtc is { } b
                ? ", active generation built " + b.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {name}: {FormatBytes(idx.SizeBytes)}, {idx.DocumentCount:N0} stored content records, {layers}{built}"));
        }

        return lines;
    }

    /// <summary>A short, actionable label for an on-disk index health state.</summary>
    public static string StorageHealthLabel(IndexStorageHealth health) => health switch
    {
        IndexStorageHealth.SourceMissing => "source folder missing",
        IndexStorageHealth.IncompatibleFormat => "rebuild required: old index format",
        IndexStorageHealth.IncompatibleRepresentation => "rebuild required: old content representation",
        IndexStorageHealth.CorruptOrIncomplete => "repair required: corrupt or incomplete metadata",
        _ => "healthy",
    };

    /// <summary>
    /// A one-line description of the master state for the Indexing tab header (plan §6.1). When the
    /// master is off, per-search overrides are inert and this states so.
    /// </summary>
    public static string MasterStateSummary(bool enableContentIndex, bool useContentIndexByDefault)
    {
        if (!enableContentIndex)
            return "Content indexing is off. Existing index data is kept; enable the master switch to build and use indexes.";
        return useContentIndexByDefault
            ? "Content indexing is on and used by default. Individual searches can opt out with the Advanced Options toggle or --no-index."
            : "Content indexing is on but not used by default. Individual searches can opt in with the Advanced Options toggle or --use-index.";
    }

    /// <summary>
    /// Computes whether a usable index exists for the folders a search is running over, from generation
    /// existence alone (plan §6.2). Off when the feature is disabled; NotRequested when this search opted
    /// out; otherwise None/Partial/Available from how many searched roots have an index.
    /// </summary>
    public static IndexAvailability Availability(bool enableContentIndex, bool useThisSearch, int rootsWithIndex, int rootsTotal)
    {
        if (!enableContentIndex)
            return IndexAvailability.Off;
        if (!useThisSearch)
            return IndexAvailability.NotRequested;
        if (rootsTotal <= 0 || rootsWithIndex <= 0)
            return IndexAvailability.None;
        return rootsWithIndex >= rootsTotal ? IndexAvailability.Available : IndexAvailability.Partial;
    }

    /// <summary>Whether the main-window availability indicator should be shown for this state.</summary>
    public static bool ShouldShowAvailability(IndexAvailability availability)
        => availability != IndexAvailability.Off;

    /// <summary>A short status label for the main-window availability indicator.</summary>
    public static string AvailabilityLabel(IndexAvailability availability) => availability switch
    {
        IndexAvailability.Available => "Index: available",
        IndexAvailability.Partial => "Index: partial",
        IndexAvailability.None => "Index: none for this folder",
        IndexAvailability.NotRequested => "Index: off for this search",
        _ => "Index: off",
    };

    /// <summary>A Segoe Fluent/MDL2 glyph for the availability state.</summary>
    public static string AvailabilityGlyph(IndexAvailability availability) => availability switch
    {
        IndexAvailability.Available => "\uE9F5",     // speedometer
        IndexAvailability.Partial => "\uE7BA",       // caution (partial coverage)
        IndexAvailability.None => "\uEA39",          // outline circle (nothing here)
        IndexAvailability.NotRequested => "\uE721",  // magnifier (live scan)
        _ => "\uE721",
    };

    /// <summary>
    /// The availability tooltip. It always states plainly that files are still read live in this build
    /// (the index is not yet used to skip files), so the indicator never implies an acceleration that
    /// has not happened (plan §6.2 / §8 "Misleading provenance UI").
    /// </summary>
    public static string AvailabilityTooltip(IndexAvailability availability)
    {
        const string live = " When used, the index only skips files that cannot match; matching files are always read live. Build, validate, or manage indexes in Settings ▸ Indexing.";
        string head = availability switch
        {
            IndexAvailability.Available => "A content index exists for the folder(s) this search covers.",
            IndexAvailability.Partial => "A content index exists for some, but not all, of the folders this search covers.",
            IndexAvailability.None => "No content index exists yet for the folder(s) this search covers.",
            IndexAvailability.NotRequested => "This search opted out of the content index (Advanced Options toggle or --no-index).",
            _ => "Content indexing is off.",
        };
        return head + live;
    }

    /// <summary>
    /// A short sentence explaining <em>when</em> content indexing runs, from the configured build trigger(s).
    /// Appended to the status-bar tooltip whenever a build is NOT currently running, so a user who doesn't
    /// see the spinning "Indexing…" state understands why (e.g. it only runs manually, at app startup, on a
    /// schedule, and/or when the PC is idle). Several triggers can be active at once; the sentence lists each
    /// one. Pure/unit-tested; an empty/Manual/unrecognized value falls back to the manual explanation.
    /// </summary>
    public static string SchedulingHint(string? buildTrigger)
    {
        var phrases = new System.Collections.Generic.List<string>(5);
        if (AppSettings.IndexBuildTriggerHas(buildTrigger, ContentIndexBuildScheduler.TriggerAtStartup))
            phrases.Add("at app startup");
        if (AppSettings.IndexBuildTriggerHas(buildTrigger, ContentIndexBuildScheduler.TriggerOnSchedule))
            phrases.Add("on your schedule");
        bool continuous = AppSettings.IndexBuildTriggerHas(
            buildTrigger,
            ContentIndexBuildScheduler.TriggerContinuous);
        if (continuous)
            phrases.Add("continuously while Yagu is open");
        else if (AppSettings.IndexBuildTriggerHas(buildTrigger, ContentIndexBuildScheduler.TriggerWhenIdle))
            phrases.Add("when your PC is idle");
        if (AppSettings.IndexBuildTriggerHas(buildTrigger, ContentIndexBuildScheduler.TriggerWhenEnabled))
            phrases.Add("when the feature is enabled");

        // Manual (the default) or anything unrecognized: nothing runs on its own.
        if (phrases.Count == 0)
            return "Automatic indexing is off — it runs only when you start a build (add a folder to the index, or use Settings ▸ Indexing ▸ Build now).";

        string body = phrases.Count switch
        {
            1 => phrases[0],
            2 => phrases[0] + " and " + phrases[1],
            _ => string.Join(", ", phrases.GetRange(0, phrases.Count - 1)) + ", and " + phrases[phrases.Count - 1],
        };
        return "Automatic indexing runs " + body + " for your indexed folders.";
    }
}
