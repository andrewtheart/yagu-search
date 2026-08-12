using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>The outcome of an incremental update pass (plan §11.4).</summary>
public enum IncrementalUpdateOutcome
{
    /// <summary>No changes were supplied — nothing to do.</summary>
    NoChanges,

    /// <summary>A delta segment was appended over the existing base.</summary>
    SegmentAppended,

    /// <summary>A segment was appended and then the layered index was compacted into a fresh base.</summary>
    Compacted,

    /// <summary>No trusted base exists to append to — the caller should do a full rebuild instead.</summary>
    NeedsFullRebuild,

    /// <summary>A pre-fix ReFS layer uses incompatible extended identities. Automatic maintenance should
    /// perform one compatibility rebuild; an explicitly incremental-only action may decline it.</summary>
    NeedsCompatibilityRebuild,

    /// <summary>The index is at its configured storage budget and could not be reclaimed within its
    /// size-management mode, so no segment was appended. The existing index stays valid and queryable; the
    /// files it no longer covers are simply live-scanned until it is rebuilt.</summary>
    SizeBudgetReached,
}

/// <summary>
/// A changed file's content classification + durable identity, produced by reading it <b>once</b> through
/// the optimized build content reader (plan §5.4): the same reason/trigrams the full build would compute,
/// and the identity taken from the very same handle whose bytes were read — no second open, and binary/BOM
/// files are decided after at most the 8 KB prefix.
/// </summary>
public readonly record struct IncrementalFileRead(
    IndexContentClassification Classification,
    FileIdentity? Identity,
    long BytesRead = 0);

/// <summary>A single changed (created or modified) file the incremental updater should re-index, already
/// classified by the content reader (the classification + captured identity, not the raw bytes).</summary>
public readonly record struct IncrementalChange(string Path, IndexContentClassification Classification, FileIdentity? Identity);

/// <summary>
/// Orchestrates one Phase 3 incremental-update pass (plan §11.4): it turns a resolved set of created/
/// modified files and deleted paths into an immutable delta segment, appends it to the current base via the
/// <see cref="ContentIndexStore"/>, and — when the segment/size bounds are exceeded — folds the whole layered
/// index into a fresh base with <see cref="ContentIndexCompactor"/>.
/// <para>
/// It is deliberately pure w.r.t. change discovery: the caller supplies the already-resolved changes (from a
/// continuous USN replay), so this class is fully unit-testable without the journal. It never throws to the
/// caller — a missing base returns <see cref="IncrementalUpdateOutcome.NeedsFullRebuild"/> so the scheduler
/// can fall back to a full rebuild, preserving the "index never suppresses a live scan" invariant.
/// </para>
/// </summary>
public sealed partial class ContentIndexIncrementalUpdater
{
    // A small-run merge is deliberately independent of total scope size: it merges a bounded contiguous run
    // without opening the base or unrelated segments, so a 30+ GiB layered index can shed layers without
    // recreating the catastrophic full-compaction memory spike the automatic size cap prevents. The bounds
    // come from the per-index EffectiveIndexSizePolicy, because fixed bounds that no real whole-drive index
    // could satisfy left those indexes with no reclamation path at all.
    internal const int SmallSegmentMaximumRun = EffectiveIndexSizePolicy.MaximumCoalesceRun;

    private readonly ContentIndexStore _store;
    private readonly IndexIngestionPolicy _policy;
    private readonly Func<string, FileIdentity?>? _identityProvider;

    public ContentIndexIncrementalUpdater(
        ContentIndexStore store,
        IndexIngestionPolicy policy,
        Func<string, FileIdentity?>? identityProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _identityProvider = identityProvider;
    }

    /// <summary>
    /// Builds the read-and-classify delegate an incremental refresh feeds to
    /// <see cref="ContentIndexUsnChangeResolver.Resolve"/> (plan §5.4/Stage 6): each changed path is read
    /// once through the optimized <see cref="IIndexFileContentReader"/> — one handle for its bytes and its
    /// durable identity, with binary/BOM files rejected after the 8 KB prefix and text streamed. Returns
    /// null for an unreadable path; the resolver then tombstones its current/prior aliases so the advanced
    /// journal checkpoint can never make stale older content look fresh. The physical file is live-scanned.
    /// </summary>
    public static Func<string, IncrementalFileRead?> CreateFileReadClassifier(IndexIngestionPolicy policy)
    {
        Func<string, CancellationToken, IncrementalFileRead?> cancellable =
            CreateCancellableFileReadClassifier(policy);
        return path => cancellable(path, CancellationToken.None);
    }

    /// <summary>Cancellation-aware form used by the bounded maintenance I/O lane. The token reaches
    /// every streamed chunk read; the lane additionally uses <c>CancelSynchronousIo</c> when the initial
    /// synchronous file open itself exceeds its deadline.</summary>
    internal static Func<string, CancellationToken, IncrementalFileRead?> CreateCancellableFileReadClassifier(
        IndexIngestionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reader = new IndexFileContentReader();
        return (path, cancellationToken) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                long expectedLength = 0;
                try { expectedLength = new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* hint only */ }

                IndexFileReadResult read = reader.Read(path, expectedLength, policy, cancellationToken);
                return new IncrementalFileRead(read.Classification, read.Identity, read.BytesRead);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // unreadable → resolver tombstones aliases → file live-scans
            }
        };
    }

    /// <summary>
    /// Applies <paramref name="changed"/> (created/modified files) and <paramref name="deletedPaths"/>
    /// against the current base at USN <paramref name="checkpoint"/>, then compacts if the settings bounds
    /// are exceeded. Returns the outcome; never throws.
    /// </summary>
    public IncrementalUpdateOutcome Apply(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        IReadOnlyList<IncrementalChange> changed,
        IReadOnlyList<string> deletedPaths,
        UsnCheckpoint checkpoint,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        CancellationToken cancellationToken = default)
    {
        using IndexMutationContext mutation = _store.AcquireMutationContext();
        return ApplyUnderLease(
            mutation, scopeId, volumeIdentity, normalizedRootPath, changed, deletedPaths,
            checkpoint, settings, builtUtc, cancellationToken);
    }

    internal IncrementalUpdateOutcome ApplyUnderLease(
        IndexMutationContext mutation,
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        IReadOnlyList<IncrementalChange> changed,
        IReadOnlyList<string> deletedPaths,
        UsnCheckpoint checkpoint,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        CancellationToken cancellationToken = default,
        bool commitCheckpointWhenUnchanged = false,
        Action<int, string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(deletedPaths);
        ArgumentNullException.ThrowIfNull(settings);

        // A rescan that proves nothing changed still has to publish its barrier, otherwise the root stays
        // permanently unprovable and every later pass repeats the rescan.
        if (changed.Count == 0 && deletedPaths.Count == 0 && !commitCheckpointWhenUnchanged)
            return IncrementalUpdateOutcome.NoChanges;

        cancellationToken.ThrowIfCancellationRequested();

        // Must have a trusted active pointer/layer metadata to append to. Never deserialize the layered
        // content here: the publisher validates the fresh segment and atomically extends that pointer.
        if (!_store.TryGetCurrentLayerDirectories(out string? baseDir, out _)
            || baseDir is null
            || ContentIndexGenerationSerializer.TryReadManifest(baseDir) is not { } baseManifest)
        {
            YaguLog.For("ContentIndex").LogWarning("Incremental update for scope {Scope}: no trusted base to layer over → needs full rebuild.", scopeId);
            return IncrementalUpdateOutcome.NeedsFullRebuild;
        }

        VolumeBinding? mounted = string.IsNullOrWhiteSpace(baseManifest.VolumeGuidPath)
            ? null
            : VolumeBindingReader.TryCapture(normalizedRootPath);
        string volumeReason = "source volume unavailable";
        if (!string.IsNullOrWhiteSpace(baseManifest.VolumeGuidPath)
            && (mounted is not { } currentVolume
                || !VolumeBindingReader.MatchesManifest(baseManifest, currentVolume, out volumeReason)))
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental update for scope {Scope}: mounted volume mismatch ({Reason}) → previous checkpoint retained.",
                scopeId,
                mounted is null ? "source volume unavailable" : volumeReason);
            return IncrementalUpdateOutcome.NeedsFullRebuild;
        }

        EffectiveIndexSizePolicy size = settings.ResolveSizePolicy(normalizedRootPath);
        int budgetMaxSegments = Math.Clamp(settings.MaxDeltaSegments, 1, 64);
        if (size.SizeBudgetMB > 0 && size.ExceedsBudget(_store.TotalActiveIndexBytes()))
        {
            // Over the storage ceiling. Try the bounded, low-memory reclamation first; only if that cannot
            // bring the index back under budget do we stop appending. Halting is the safe way to bound
            // growth: the existing index stays valid and queryable, and anything it no longer covers is
            // live-scanned, whereas folding an oversized index would trade disk growth for a memory spike.
            try
            {
                CoalesceSmallSegmentsUnderLease(mutation, budgetMaxSegments, cancellationToken, size);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Budget-triggered coalescing failed for scope {Scope}; keeping the valid layered index.", scopeId);
            }

            long afterCoalesce = _store.TotalActiveIndexBytes();
            if (size.ExceedsBudget(afterCoalesce) && !size.AllowsCompactingIndexOf(afterCoalesce))
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "Scope {Scope} for '{Root}' is {IndexMB} MB, at or over its {BudgetMB} MB size budget, and mode '{Mode}' cannot reclaim further — pausing index updates for this root. Searches still return every match (uncovered files are read live); rebuild this index to reclaim the space.",
                    scopeId, normalizedRootPath, afterCoalesce / (1024 * 1024), size.SizeBudgetMB, size.Mode);
                return IncrementalUpdateOutcome.SizeBudgetReached;
            }
        }

        var segmentBuilder = new ContentIndexDeltaSegmentBuilder(_policy, identityProvider: _identityProvider);
        if (mounted is { } boundVolume)
            segmentBuilder.SeedVolumeBinding(boundVolume);
        else
            segmentBuilder.SeedVolumeSerialNumber(baseManifest.VolumeSerialNumber);

        var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
        int mergeTotal = changed.Count + deletedPaths.Count;
        int merged = 0;
        int lastMergePercent = -1;
        progress?.Invoke(IndexUpdateStages.MergeFloor, IndexUpdateStages.Merging);
        foreach (IncrementalChange change in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentBuilder.AddChangedClassified(change.Path, change.Classification, change.Identity);
            ReportMerge(++merged);
        }
        foreach (string deleted in deletedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentBuilder.AddTombstone(deleted);
            ReportMerge(++merged);
        }
        long mergeMs = phaseTimer.ElapsedMilliseconds;

        progress?.Invoke(IndexUpdateStages.WriteFloor, IndexUpdateStages.Writing);
        phaseTimer.Restart();
        ContentIndexDeltaSegment segment = segmentBuilder.Build(scopeId, volumeIdentity, normalizedRootPath, checkpoint, builtUtc);
        long buildMs = phaseTimer.ElapsedMilliseconds;

        progress?.Invoke(IndexUpdateStages.PublishFloor, IndexUpdateStages.Publishing);
        phaseTimer.Restart();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.PublishSegmentFastUnderLease(mutation, segment);
        }
        catch (InvalidOperationException ex)
        {
            // Lost the base between the check and publish → let the scheduler do a full rebuild.
            YaguLog.For("ContentIndex").LogWarning(ex, "Incremental update for scope {Scope}: base was lost between check and publish → needs full rebuild.", scopeId);
            return IncrementalUpdateOutcome.NeedsFullRebuild;
        }

        YaguLog.For("ContentIndex").LogInformation(
            "Incremental update for scope {Scope}: appended delta segment ({ChangedCount} changed, {DeletedCount} deleted) — merge {MergeMs} ms, serialize {BuildMs} ms, publish {PublishMs} ms.",
            scopeId, changed.Count, deletedPaths.Count, mergeMs, buildMs, phaseTimer.ElapsedMilliseconds);

        int maxSegments = budgetMaxSegments;
        int thresholdMB = Math.Clamp(settings.CompactionThresholdMB, 1, 8192);
        if (_store.ActiveSegmentCount() > maxSegments && size.AllowsCoalescing)
        {
            progress?.Invoke(IndexUpdateStages.CompactFloor, IndexUpdateStages.Compacting);
            try
            {
                CoalesceSmallSegmentsUnderLease(mutation, maxSegments, cancellationToken, size);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Coalescing is an optimization after the new delta was durably appended. Any failure leaves
                // that complete pointer active and falls through to the existing full-compaction decision.
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Small-segment coalescing failed for scope {Scope}; keeping the valid layered index.", scopeId);
            }
        }

        if (_store.ShouldCompact(maxSegments, thresholdMB))
        {
            // Folding a large layered index re-materializes every layer's documents plus a combined posting
            // index and a serialization buffer, so the per-index cap decides whether that is affordable.
            // Exceeding the storage budget lifts the cap, because the only alternative left at that point is
            // letting this index grow without limit.
            long indexBytes = _store.TotalActiveIndexBytes();
            if (!size.AllowsCompactingIndexOf(indexBytes))
            {
                if (size.ExceedsBudget(indexBytes))
                {
                    YaguLog.For("ContentIndex").LogWarning(
                        "Scope {Scope} is {IndexMB} MB, over its {BudgetMB} MB size budget, but its '{Mode}' size-management mode cannot reclaim further — the appended segment remains active. Rebuild this index to reclaim the space.",
                        scopeId, indexBytes / (1024 * 1024), size.SizeBudgetMB, size.Mode);
                }
                else
                {
                    YaguLog.For("ContentIndex").LogInformation(
                        "Skipping post-incremental auto-compaction for scope {Scope}: the index is {IndexMB} MB (> {MaxCompactMB} MB cap, mode '{Mode}') — the appended segment remains active.",
                        scopeId, indexBytes / (1024 * 1024), size.MaxAutoCompactionSizeMB, size.Mode);
                }
                return IncrementalUpdateOutcome.SegmentAppended;
            }

            if (_store.TryOpenLayered(cancellationToken: cancellationToken) is not { } handle)
                return IncrementalUpdateOutcome.SegmentAppended;

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(IndexUpdateStages.CompactFloor, IndexUpdateStages.Compacting);
            YaguLog.For("ContentIndex").LogInformation("Incremental update for scope {Scope}: compaction bounds exceeded (maxSegments={MaxSegments}, thresholdMB={ThresholdMB}) → compacting layered index into a fresh base.", scopeId, maxSegments, thresholdMB);
            ContentIndexGeneration compacted = ContentIndexCompactor.Compact(handle, _policy, builtUtc);
            cancellationToken.ThrowIfCancellationRequested();
            _store.CompactUnderLease(mutation, compacted);
            YaguLog.For("ContentIndex").LogInformation("Incremental update for scope {Scope}: compaction complete.", scopeId);
            return IncrementalUpdateOutcome.Compacted;
        }

        return IncrementalUpdateOutcome.SegmentAppended;

        void ReportMerge(int done)
        {
            if (progress is null || mergeTotal <= 0)
                return;
            int percent = IndexUpdateStages.MergeFloor
                + (int)((long)done * (IndexUpdateStages.MergeCeiling - IndexUpdateStages.MergeFloor) / mergeTotal);
            if (percent == lastMergePercent)
                return;
            lastMergePercent = percent;
            progress(percent, IndexUpdateStages.Merging);
        }
    }

    internal int CoalesceSmallSegmentsUnderLease(
        IndexMutationContext mutation,
        int maxSegments,
        CancellationToken cancellationToken,
        EffectiveIndexSizePolicy? sizePolicy = null)
    {
        EffectiveIndexSizePolicy size = sizePolicy ?? EffectiveIndexSizePolicy.Default;
        if (!size.AllowsCoalescing)
            return 0;

        int mergedRuns = 0;
        int removedLayers = 0;
        while (mergedRuns < size.CoalesceMaxRunsPerPass
               && _store.ActiveSegmentCount() > maxSegments
               && _store.TryFindSmallSegmentRun(
                   size.CoalesceMinRun,
                   SmallSegmentMaximumRun,
                   size.CoalesceMaxSegmentBytes,
                   size.CoalesceMaxBatchBytes,
                   out ContentIndexStore.SegmentCoalesceRun? run)
               && run is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = new List<ContentIndexDeltaSegment>(run.SegmentDirectories.Count);
            foreach (string directory in run.SegmentDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContentIndexDeltaSegment? segment = ContentIndexDeltaSegmentSerializer.TryRead(
                    directory, retainDocuments: true, cancellationToken);
                if (segment is null)
                    return removedLayers; // fail safe: leave every existing pointer/layer unchanged
                inputs.Add(segment);
            }

            ContentIndexDeltaSegment merged = MergeSegmentRun(inputs, cancellationToken);
            if (!_store.TryReplaceSegmentRunUnderLease(mutation, run, merged))
                return removedLayers;

            mergedRuns++;
            removedLayers += run.SegmentIds.Count - 1;
        }

        if (removedLayers > 0)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Bounded small-segment maintenance removed {RemovedLayerCount} layer(s) across {MergedRunCount} run(s); {RemainingSegmentCount} active segment(s) remain.",
                removedLayers, mergedRuns, _store.ActiveSegmentCount());
        }
        return removedLayers;
    }

    /// <summary>
    /// Produces one segment with the exact newest-wins meaning of a contiguous input run. Inputs are walked
    /// newest-to-oldest like the layered query: the first alias/tombstone deciding a path wins; documents are
    /// copied from their existing trigram sets (never re-read from source); hard links within a layer remain
    /// shared. The newest input checkpoint/time becomes the merged segment's logical barrier.
    /// </summary>
    private ContentIndexDeltaSegment MergeSegmentRun(
        IReadOnlyList<ContentIndexDeltaSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count < 2)
            throw new ArgumentException("At least two segments are required to coalesce a run.", nameof(segments));

        var builder = new ContentIndexGenerationBuilder(_policy);
        var decided = new HashSet<string>(StringComparer.Ordinal);
        var tombstones = new HashSet<string>(StringComparer.Ordinal);
        IndexManifest firstManifest = segments[0].Added.Manifest;
        UsnCheckpoint priorCheckpoint = firstManifest.FreshnessCheckpoint;
        for (int i = 1; i < segments.Count; i++)
        {
            IndexManifest manifest = segments[i].Added.Manifest;
            if (!string.Equals(manifest.ScopeId, firstManifest.ScopeId, StringComparison.Ordinal)
                || !string.Equals(manifest.NormalizedRootPath, firstManifest.NormalizedRootPath, StringComparison.OrdinalIgnoreCase)
                || manifest.VolumeSerialNumber != firstManifest.VolumeSerialNumber
                || !string.Equals(manifest.VolumeGuidPath, firstManifest.VolumeGuidPath, StringComparison.OrdinalIgnoreCase)
                || manifest.FreshnessCheckpoint.JournalId != firstManifest.FreshnessCheckpoint.JournalId
                || manifest.FreshnessCheckpoint.NextUsn < priorCheckpoint.NextUsn)
            {
                throw new InvalidDataException("Small-segment coalescing inputs disagree on scope, root, volume, journal, or checkpoint order.");
            }
            priorCheckpoint = manifest.FreshnessCheckpoint;
        }
        if (!string.IsNullOrWhiteSpace(firstManifest.VolumeGuidPath)
            && VolumeBindingReader.TryCapture(firstManifest.NormalizedRootPath) is { } volumeBinding
            && VolumeBindingReader.MatchesManifest(firstManifest, volumeBinding, out _))
        {
            builder.SeedVolumeBinding(volumeBinding);
        }
        else
        {
            builder.SeedVolumeSerialNumber(firstManifest.VolumeSerialNumber);
        }

        for (int i = segments.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContentIndexDeltaSegment segment = segments[i];
            foreach (string path in segment.RemovedPaths)
            {
                if (decided.Add(path))
                    tombstones.Add(path);
            }

            ContentIndexGeneration layer = segment.Added;
            var contentIdMap = new Dictionary<long, long>();
            foreach (var (path, entry) in layer.Aliases
                         .OrderBy(static item => item.Value.ContentId)
                         .ThenBy(static item => item.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!decided.Add(path))
                    continue;

                if (contentIdMap.TryGetValue(entry.ContentId, out long mergedContentId))
                {
                    builder.AddHardLink(path, mergedContentId);
                    continue;
                }

                IReadOnlyCollection<Trigram> trigrams = layer.Documents[(int)entry.ContentId];
                UsnFileIdentity? identity = entry.ContentId < layer.ContentIdentities.Count
                    ? layer.ContentIdentities[(int)entry.ContentId]
                    : null;
                long assigned = builder.AddClassifiedDocument(path, trigrams, identity);
                contentIdMap[entry.ContentId] = assigned;
            }
        }

        IndexManifest newest = segments[^1].Added.Manifest;
        DateTimeOffset? lastIncrementalUpdateUtc = null;
        foreach (ContentIndexDeltaSegment segment in segments)
        {
            DateTimeOffset? candidate = segment.Added.Manifest.LastIncrementalUpdateUtc;
            if (candidate is { } timestamp && (lastIncrementalUpdateUtc is null || timestamp > lastIncrementalUpdateUtc))
                lastIncrementalUpdateUtc = timestamp;
        }
        ContentIndexGeneration added = builder.Build(
            newest.ScopeId,
            newest.VolumeIdentity,
            newest.NormalizedRootPath,
            newest.FreshnessCheckpoint,
            newest.BuiltUtc,
            newest.CreatedUtc,
            lastIncrementalUpdateUtc);
        return new ContentIndexDeltaSegment(added, tombstones);
    }
}
