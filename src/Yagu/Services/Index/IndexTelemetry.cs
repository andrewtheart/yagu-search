using Yagu.Services.Telemetry;

namespace Yagu.Services.Index;

/// <summary>Outcome of one refresh/build pass, reported as an aggregate telemetry dimension (no path data).</summary>
public enum IndexRefreshKind
{
    FullBuild,
    IncrementalSegment,
    Compaction,
    NoChange,
    FullRebuildFallback,
}

/// <summary>
/// Aggregate-only, opt-in telemetry for the content index (plan §6.4 / §11.4). Emits <b>only counts and
/// timings</b> — build/refresh duration, segment/compaction counts, and index-used-vs-bypassed — and
/// <b>never</b> roots, paths, queries, trigrams, file identities, or any content-derived value. It is inert
/// unless the user opted into both global telemetry <em>and</em> the index-telemetry share
/// (<see cref="ShouldShare"/>), so it honors the same offline-by-default guard as the rest of telemetry
/// (<see cref="TelemetryGate.ShouldSendTelemetry"/> already requires <see cref="TelemetryConfig.IsConfigured"/>).
/// All reporting methods are hard no-ops when sharing is off, so they are safe to call from any pass.
/// </summary>
public static class IndexTelemetry
{
    /// <summary>
    /// Whether aggregate index telemetry may be shared: the user opted into index telemetry
    /// (<c>ShareAggregateIndexTelemetry</c>) <b>and</b> global telemetry is actually sendable (consented,
    /// configured, non-headless). The index opt-in never sends on its own.
    /// </summary>
    public static bool ShouldShare(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ShareAggregateIndexTelemetry && TelemetryGate.ShouldSendTelemetry;
    }

    /// <summary>
    /// Reports an aggregate build/refresh measurement. No-op unless <see cref="ShouldShare"/>. Only the kind
    /// (an enum name), the duration, and small non-identifying counts are sent — nothing derived from a path
    /// or file content.
    /// </summary>
    public static void ReportRefresh(
        AppSettings settings,
        IndexRefreshKind kind,
        double durationMs,
        int rootsBuilt = 0,
        int rootsSkipped = 0,
        int rootsFailed = 0,
        int segmentsAppended = 0,
        int compactions = 0)
    {
        if (!ShouldShare(settings))
            return;

        TelemetryService.Instance.TrackPerformance(
            "content_index_refresh",
            durationMs,
            extraMeasurements: new Dictionary<string, double>
            {
                ["rootsBuilt"] = rootsBuilt,
                ["rootsSkipped"] = rootsSkipped,
                ["rootsFailed"] = rootsFailed,
                ["segmentsAppended"] = segmentsAppended,
                ["compactions"] = compactions,
            },
            properties: new Dictionary<string, string> { ["kind"] = kind.ToString() });
    }

    /// <summary>
    /// Reports whether the index participated in a search (the hit-rate numerator/denominator), once per
    /// search — not per file, so it never touches the scan hot path. No-op unless <see cref="ShouldShare"/>.
    /// Sends only a boolean-as-count and a coarse mode string, never the query or any candidate path.
    /// </summary>
    public static void ReportQueryOutcome(AppSettings settings, bool indexUsed, bool layered)
    {
        if (!ShouldShare(settings))
            return;

        TelemetryService.Instance.TrackPerformance(
            "content_index_query",
            0,
            extraMeasurements: new Dictionary<string, double> { ["indexUsed"] = indexUsed ? 1 : 0 },
            properties: new Dictionary<string, string> { ["mode"] = indexUsed ? (layered ? "layered" : "base") : "bypassed" });
    }
}
