namespace Yagu.Services.Index;

/// <summary>
/// The result of reading the change journal for a generation over an interval (plan §3.5): the freshness
/// verdict for the root, the content dirtied over that interval, and the advanced checkpoint. A verdict
/// other than <see cref="RootFreshnessVerdict.Continuous"/> means the caller must not prune (live-scan).
/// </summary>
public sealed record FreshnessRead(RootFreshnessVerdict Verdict, DirtyContentSet Dirty, UsnCheckpoint NextCheckpoint)
{
    /// <summary>True when the journal was continuous over the interval and the dirty set is authoritative.</summary>
    public bool IsContinuous => Verdict == RootFreshnessVerdict.Continuous;

    /// <summary>
    /// The raw journal-read status behind <see cref="Verdict"/> (<see cref="UsnReadStatus.Ok"/> when the
    /// read succeeded or no read was performed). This preserves the specific cause that <see cref="MapVerdict"/>
    /// otherwise collapses — e.g. a <see cref="RootFreshnessVerdict.JournalDiscontinuity"/> verdict can be a
    /// <see cref="UsnReadStatus.JournalIdChanged"/>, <see cref="UsnReadStatus.GapDetected"/>,
    /// <see cref="UsnReadStatus.UnknownRecordVersion"/>, or <see cref="UsnReadStatus.Error"/> — so a bypass is
    /// diagnosable from the log without re-running the search.
    /// </summary>
    public UsnReadStatus RawStatus { get; init; } = UsnReadStatus.Ok;

    /// <summary>Total journal records observed over a continuous interval, including identities not yet
    /// present in the index (for example, a newly copied file). Zero for a non-continuous/failed read.</summary>
    public int JournalChangeCount { get; init; }

    /// <summary>Journal records whose identity mapped to this layer's persisted file-id table.</summary>
    public int ResolvedJournalChangeCount { get; init; }
}

/// <summary>
/// Computes the dirty-content set for a loaded <see cref="ContentIndexGeneration"/> by replaying its
/// USN change journal (plan §3.5). It resolves each changed file identity to a content id via the
/// generation's <see cref="ContentIndexGeneration.BuildFileIdMap"/>, maps the journal read status onto a
/// <see cref="RootFreshnessVerdict"/> the trust surface understands, and <b>fails closed</b>: a missing
/// build checkpoint, journal-id change, wrap/gap, unknown record version, or unavailability all yield a
/// non-continuous verdict with an empty dirty set, so the caller live-scans the whole root rather than
/// trusting stale postings. The journal reader is injectable so the logic is unit-testable without the
/// real journal; production uses <see cref="UsnJournalReader.TryCollectChanges"/>.
/// </summary>
public static class ContentIndexFreshnessEvaluator
{
    /// <summary>Reads changes for a volume root since a checkpoint. Matches <see cref="UsnJournalReader.TryCollectChanges"/>.</summary>
    public delegate UsnReadResult JournalReader(string rootPath, UsnCheckpoint since);

    /// <summary>
    /// Builds a <see cref="JournalReader"/> that reads the real USN journal with the configured catch-up
    /// record cap (<c>AppSettings.IndexMaxJournalCatchupRecords</c>). When the change delta since the
    /// checkpoint exceeds the cap, the read returns <see cref="UsnReadStatus.Incomplete"/> → a non-continuous
    /// verdict → the caller live-scans (never trusts a partial delta). Pass the normalized setting value.
    /// </summary>
    public static JournalReader CreateReader(int maxCatchupRecords)
        => (rootPath, since) => UsnJournalReader.TryCollectChanges(rootPath, since, maxRecords: maxCatchupRecords);

    public static JournalReader CreateReader(int maxCatchupRecords, TimeSpan ioTimeout)
        => (rootPath, since) => UsnJournalReader.TryCollectChangesBounded(
            rootPath,
            since,
            ioTimeout,
            maxRecords: maxCatchupRecords);

    /// <summary>
    /// Reads the content dirtied in <c>[since.NextUsn, now)</c> for <paramref name="generation"/>. When
    /// <paramref name="since"/> has no journal identity (the build had no readable journal), freshness
    /// cannot be proven and the verdict is <see cref="RootFreshnessVerdict.CheckpointInvalid"/>.
    /// </summary>
    public static FreshnessRead ReadDirtySince(ContentIndexGeneration generation, UsnCheckpoint since, JournalReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return ReadDirtySince(generation.Manifest.NormalizedRootPath, since, generation.BuildFileIdMap(), reader);
    }

    /// <summary>
    /// Lightweight overload for a staleness check that has read only the manifest + fileids (a
    /// <see cref="FileIdMap"/>) and NOT the full generation: replays the journal since <paramref name="since"/>
    /// for <paramref name="normalizedRootPath"/> and resolves the changes against <paramref name="fileIds"/>.
    /// Same fail-closed semantics as the generation overload.
    /// </summary>
    public static FreshnessRead ReadDirtySince(string normalizedRootPath, UsnCheckpoint since, FileIdMap fileIds, JournalReader? reader = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedRootPath);
        ArgumentNullException.ThrowIfNull(fileIds);
        var dirty = new DirtyContentSet();

        // No usable checkpoint (the journal was unavailable at build time) → freshness is unprovable.
        if (since.JournalId == 0)
            return new FreshnessRead(RootFreshnessVerdict.CheckpointInvalid, dirty, since);

        UsnReadResult read = (reader ?? DefaultReader)(normalizedRootPath, since);
        return ResolveDirty(read, fileIds);
    }

    /// <summary>
    /// Resolves one already-collected journal result through a layer-local <see cref="FileIdMap"/>. This is
    /// the pure mapping half of <see cref="ReadDirtySince(string, UsnCheckpoint, FileIdMap, JournalReader?)"/>:
    /// callers with multiple active layers can read the shared root/checkpoint interval once, then map the
    /// same immutable change list into each layer's independent content-id namespace without another kernel
    /// journal read or change-list materialization. Non-continuous statuses remain fail closed and never map
    /// partial changes.
    /// </summary>
    public static FreshnessRead ResolveDirty(UsnReadResult read, FileIdMap fileIds)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(fileIds);
        var dirty = new DirtyContentSet();

        RootFreshnessVerdict verdict = MapVerdict(read.Status);
        if (verdict != RootFreshnessVerdict.Continuous)
            return new FreshnessRead(verdict, dirty, read.NextCheckpoint) { RawStatus = read.Status };

        int resolved = fileIds.ResolveDirty(read.Changes, dirty);
        bool legacyIdentityMismatch = fileIds.HasExtendedIdentities
            && read.Changes.Any(change => change.Identity.High == 0
                && !fileIds.TryGetContentId(change.Identity, out _));
        if (legacyIdentityMismatch)
        {
            // Older ReFS indexes persisted FILE_ID_128, but unprivileged journal replay returns V2 64-bit
            // reference numbers. An unknown V2 record could be a deletion of an old indexed path, so never
            // advance/prune from this layer; maintenance must perform one compatibility rebuild.
            return new FreshnessRead(RootFreshnessVerdict.JournalDiscontinuity, dirty, read.NextCheckpoint)
            {
                RawStatus = UsnReadStatus.IdentityMismatch,
                JournalChangeCount = read.Changes.Count,
                ResolvedJournalChangeCount = resolved,
            };
        }
        return new FreshnessRead(RootFreshnessVerdict.Continuous, dirty, read.NextCheckpoint)
        {
            JournalChangeCount = read.Changes.Count,
            ResolvedJournalChangeCount = resolved,
        };
    }

    /// <summary>
    /// Mapped-reader overload (plan §5.1 / §6 Stage 2): replays the journal since <paramref name="since"/>
    /// and resolves each change to a content id through the <b>memory-mapped format-v3 reverse identity
    /// index</b> (<see cref="ContentIndexV3Reader.TryReverseIdentity"/>) instead of a deserialized
    /// <see cref="FileIdMap"/>. The dirty set is identical to the <see cref="FileIdMap"/> overload — both
    /// resolve the same captured <c>FILE_ID_128</c> identities — and the fail-closed semantics are the same,
    /// so a scope's B0 freshness can be computed in the out-of-process worker without deserializing
    /// <c>fileids.bin</c> (it reads only the mapped reverse-index pages a change touches). A torn/corrupt
    /// reverse-index block surfaces as an <see cref="InvalidDataException"/> from the reader; the caller
    /// treats that as "not query-ready" and live-scans (same contract as the mapped candidate/path lookups).
    /// </summary>
    public static FreshnessRead ReadDirtySince(string normalizedRootPath, UsnCheckpoint since, ContentIndexV3Reader v3Reader, JournalReader? reader = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedRootPath);
        ArgumentNullException.ThrowIfNull(v3Reader);
        var dirty = new DirtyContentSet();

        // No usable checkpoint (the journal was unavailable at build time) → freshness is unprovable.
        if (since.JournalId == 0)
            return new FreshnessRead(RootFreshnessVerdict.CheckpointInvalid, dirty, since);

        UsnReadResult read = (reader ?? DefaultReader)(normalizedRootPath, since);
        RootFreshnessVerdict verdict = MapVerdict(read.Status);
        if (verdict != RootFreshnessVerdict.Continuous)
            return new FreshnessRead(verdict, dirty, read.NextCheckpoint) { RawStatus = read.Status };

        int resolved = ResolveDirtyFromReader(v3Reader, read.Changes, dirty);
        return new FreshnessRead(RootFreshnessVerdict.Continuous, dirty, read.NextCheckpoint)
        {
            JournalChangeCount = read.Changes.Count,
            ResolvedJournalChangeCount = resolved,
        };
    }

    /// <summary>
    /// Marks every indexed content whose durable file identity appears in <paramref name="changes"/> as
    /// dirty, resolving each identity through the mapped v3 reverse index — the mapped equivalent of
    /// <see cref="FileIdMap.ResolveDirty"/>. Changes to files not in the index are ignored.
    /// </summary>
    private static int ResolveDirtyFromReader(ContentIndexV3Reader v3Reader, IReadOnlyList<UsnChange> changes, DirtyContentSet dirty)
    {
        int resolved = 0;
        foreach (UsnChange change in changes)
        {
            if (v3Reader.TryReverseIdentity(change.Identity, out int contentId))
            {
                dirty.MarkDirty(contentId);
                resolved++;
            }
        }
        return resolved;
    }

    /// <summary>
    /// Convenience for the initial barrier B0: reads the content dirtied since the generation was built
    /// (<see cref="IndexManifest.FreshnessCheckpoint"/>). The returned <see cref="FreshnessRead.Verdict"/>
    /// feeds <see cref="ContentIndexQuerySession.CanAccelerate"/> and the dirty set feeds
    /// <see cref="ContentIndexQuerySession.Begin"/>.
    /// </summary>
    public static FreshnessRead ReadDirtyAtBuildBarrier(ContentIndexGeneration generation, JournalReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return ReadDirtySince(generation, generation.Manifest.FreshnessCheckpoint, reader);
    }

    private static RootFreshnessVerdict MapVerdict(UsnReadStatus status) => status switch
    {
        UsnReadStatus.Ok => RootFreshnessVerdict.Continuous,
        UsnReadStatus.Unavailable => RootFreshnessVerdict.JournalUnavailable,
        // Journal-id change, wrap/gap, an unknown record version, or any read error all break continuity:
        // fail closed so the whole root is live-scanned rather than trusting stale postings.
        _ => RootFreshnessVerdict.JournalDiscontinuity,
    };

    private static UsnReadResult DefaultReader(string rootPath, UsnCheckpoint since)
        => UsnJournalReader.TryCollectChanges(rootPath, since);
}
