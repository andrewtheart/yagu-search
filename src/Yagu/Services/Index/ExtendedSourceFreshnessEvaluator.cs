namespace Yagu.Services.Index;

/// <summary>
/// The result of reading the change journal for an <see cref="ExtendedSourceNamespace"/> over an interval
/// (plan §3.5/§7): the freshness verdict for the covering root, the source keys dirtied over that interval,
/// and the advanced checkpoint. A verdict other than <see cref="RootFreshnessVerdict.Continuous"/> marks
/// <b>every</b> source dirty, so the whole namespace live-extracts.
/// </summary>
public sealed record ExtendedSourceFreshness(RootFreshnessVerdict Verdict, IReadOnlySet<string> DirtyKeys, UsnCheckpoint NextCheckpoint)
{
    /// <summary>True when the journal was continuous over the interval and the dirty set is authoritative.</summary>
    public bool IsContinuous => Verdict == RootFreshnessVerdict.Continuous;

    /// <summary>
    /// True when <paramref name="sourceKey"/> was not dirtied over the interval (its backing file did not
    /// change) — the per-source <c>sourceFresh</c> input for <see cref="ExtendedSourceNamespace.ClassifyCandidate"/>.
    /// </summary>
    public bool IsFresh(string sourceKey) => !DirtyKeys.Contains(sourceKey);
}

/// <summary>
/// Computes the dirty-source-key set for a loaded <see cref="ExtendedSourceNamespace"/> by replaying its
/// USN change journal (plan §3.5/§7). It mirrors <see cref="ContentIndexFreshnessEvaluator"/> but resolves
/// file-identity changes to <em>source keys</em> (via the namespace's stored per-source identities) rather
/// than raw-text content ids, and it <b>fails closed</b>: a missing build checkpoint, journal-id change,
/// wrap/gap, unknown record version, or unavailability all mark every source dirty, so the whole namespace
/// live-extracts rather than trusting stale postings. The journal reader is injectable so the logic is
/// unit-testable without the real journal; production uses <see cref="UsnJournalReader.TryCollectChanges"/>.
/// </summary>
public static class ExtendedSourceFreshnessEvaluator
{
    /// <summary>Reads changes for a volume root since a checkpoint. Matches <see cref="UsnJournalReader.TryCollectChanges"/>.</summary>
    public delegate UsnReadResult JournalReader(string rootPath, UsnCheckpoint since);

    /// <summary>
    /// Reads the sources dirtied in <c>[since.NextUsn, now)</c> for <paramref name="ns"/>. When
    /// <paramref name="since"/> has no journal identity (the build had no readable journal), freshness
    /// cannot be proven and <b>every</b> source is dirtied (verdict <see cref="RootFreshnessVerdict.CheckpointInvalid"/>).
    /// </summary>
    public static ExtendedSourceFreshness ReadDirtySince(ExtendedSourceNamespace ns, UsnCheckpoint since, JournalReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(ns);

        // No usable checkpoint (the journal was unavailable at build time) → freshness is unprovable.
        if (since.JournalId == 0)
            return AllDirty(ns, RootFreshnessVerdict.CheckpointInvalid, since);

        UsnReadResult read = (reader ?? DefaultReader)(ns.NormalizedRootPath, since);
        RootFreshnessVerdict verdict = MapVerdict(read.Status);
        if (verdict != RootFreshnessVerdict.Continuous)
            return AllDirty(ns, verdict, read.NextCheckpoint);

        return new ExtendedSourceFreshness(RootFreshnessVerdict.Continuous, ns.ResolveDirtyKeys(read.Changes), read.NextCheckpoint);
    }

    /// <summary>
    /// Convenience for the initial barrier B0: reads the sources dirtied since the namespace was built
    /// (<see cref="ExtendedSourceNamespace.FreshnessCheckpoint"/>).
    /// </summary>
    public static ExtendedSourceFreshness ReadDirtyAtBuildBarrier(ExtendedSourceNamespace ns, JournalReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(ns);
        return ReadDirtySince(ns, ns.FreshnessCheckpoint, reader);
    }

    private static ExtendedSourceFreshness AllDirty(ExtendedSourceNamespace ns, RootFreshnessVerdict verdict, UsnCheckpoint checkpoint)
        => new(verdict, ns.AllSourceKeys, checkpoint);

    private static RootFreshnessVerdict MapVerdict(UsnReadStatus status) => status switch
    {
        UsnReadStatus.Ok => RootFreshnessVerdict.Continuous,
        UsnReadStatus.Unavailable => RootFreshnessVerdict.JournalUnavailable,
        // Journal-id change, wrap/gap, an unknown record version, or any read error all break continuity:
        // fail closed so the whole namespace is live-extracted rather than trusting stale postings.
        _ => RootFreshnessVerdict.JournalDiscontinuity,
    };

    private static UsnReadResult DefaultReader(string rootPath, UsnCheckpoint since)
        => UsnJournalReader.TryCollectChanges(rootPath, since);
}
