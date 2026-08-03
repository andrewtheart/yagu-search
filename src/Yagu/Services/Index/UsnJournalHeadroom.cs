namespace Yagu.Services.Index;

/// <summary>
/// How close an index root's freshness checkpoint is to being purged by USN-journal wrap.
/// <see cref="SurvivalFraction"/> is 1.0 right after a refresh and trends toward 0.0 as the journal's
/// oldest surviving record (<see cref="UsnJournalInfo.FirstUsn"/>) climbs toward the checkpoint; once it
/// passes the checkpoint the records the next search needs are gone (<see cref="CheckpointPurged"/>) and
/// that search bypasses the index. <see cref="ShouldRefreshSoon"/> is the actionable trigger.
/// </summary>
public readonly record struct UsnHeadroomVerdict(
    double SurvivalFraction,
    bool CheckpointPurged,
    bool JournalIdMismatch,
    bool ShouldRefreshSoon)
{
    /// <summary>A checkpoint with the full window ahead of it (just-refreshed / nothing to purge).</summary>
    public static UsnHeadroomVerdict Healthy { get; } = new(1.0, CheckpointPurged: false, JournalIdMismatch: false, ShouldRefreshSoon: false);
}

/// <summary>
/// Pure, side-effect-free evaluation of how much USN-journal headroom an index root's freshness checkpoint
/// still has before it is purged by wrap (plan §3.5 diagnostics / proactive re-anchor).
/// <para>
/// The NTFS/ReFS change journal is a fixed-size circular log per volume: records older than
/// <see cref="UsnJournalInfo.FirstUsn"/> have already been overwritten. A search proves an index root is
/// fresh by replaying the journal from the root's build checkpoint (<c>checkpoint.NextUsn</c>); that only
/// works while the checkpoint stays at or above <c>FirstUsn</c>. Because <c>FirstUsn</c> advances with
/// <b>all</b> volume activity — not just writes under the indexed root — a completely unchanging root can
/// still have its checkpoint silently purged, at which point the next search over it falls back to a full
/// live scan (the "Index: bypassed / JournalDiscontinuity (GapDetected)" case). This helper lets the
/// refresh scheduler re-anchor the checkpoint <i>before</i> that happens rather than discovering the gap at
/// search time. It is deliberately dependency-free so it is fully unit-testable without a real volume.
/// </para>
/// </summary>
public static class UsnJournalHeadroom
{
    /// <summary>
    /// Default survival fraction below which a proactive refresh is recommended: once the checkpoint has
    /// fallen into the oldest quarter of the live journal window, wrap is close enough to act on.
    /// </summary>
    public const double DefaultRefreshBelowFraction = 0.25;

    /// <summary>
    /// Evaluates <paramref name="checkpoint"/> against the volume's current journal state
    /// <paramref name="journal"/>. <paramref name="refreshBelowFraction"/> (0..1) is the survival-fraction
    /// threshold at or below which <see cref="UsnHeadroomVerdict.ShouldRefreshSoon"/> is set.
    /// </summary>
    public static UsnHeadroomVerdict Evaluate(
        UsnJournalInfo journal,
        UsnCheckpoint checkpoint,
        double refreshBelowFraction = DefaultRefreshBelowFraction)
    {
        // A different journal identity means the journal was deleted/recreated since the checkpoint was
        // taken — the old cursor is meaningless and the next search would already bypass. Re-anchor now.
        if (checkpoint.JournalId != journal.UsnJournalId)
            return new UsnHeadroomVerdict(0.0, CheckpointPurged: false, JournalIdMismatch: true, ShouldRefreshSoon: true);

        long window = journal.NextUsn - journal.FirstUsn;
        long survived = checkpoint.NextUsn - journal.FirstUsn;

        // The records the checkpoint needs have already been overwritten → a search would bypass right now.
        if (survived < 0)
            return new UsnHeadroomVerdict(0.0, CheckpointPurged: true, JournalIdMismatch: false, ShouldRefreshSoon: true);

        // Degenerate/empty window (no records yet, or NextUsn not ahead of FirstUsn): nothing has been
        // purged, so the checkpoint is safe for now.
        if (window <= 0)
            return UsnHeadroomVerdict.Healthy;

        double fraction = Math.Clamp((double)survived / window, 0.0, 1.0);
        bool refreshSoon = fraction < refreshBelowFraction;
        return new UsnHeadroomVerdict(fraction, CheckpointPurged: false, JournalIdMismatch: false, ShouldRefreshSoon: refreshSoon);
    }
}
