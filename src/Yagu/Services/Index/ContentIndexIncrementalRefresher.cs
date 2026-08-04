using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

internal readonly record struct IncrementalRefreshResult(
    IncrementalUpdateOutcome Outcome,
    int JournalChangeCount,
    bool ChangeCountComplete,
    bool ThresholdExceeded);

/// <summary>
/// Drives one end-to-end Phase 3 incremental refresh for a scope (plan §3.5/§11.4): it reads the change
/// journal since the newest layer's checkpoint, resolves each name-less USN record to a created/modified/
/// deleted path (<see cref="ContentIndexUsnChangeResolver"/> + an <see cref="IFileIdPathResolver"/>), and
/// applies the result as a delta segment (with auto-compaction) via <see cref="ContentIndexIncrementalUpdater"/>.
/// <para>
/// Every dependency (journal reader, path-resolver factory, content read-and-classify, identity provider)
/// is injected, so the whole flow is unit-testable without a real volume; production wires the USN journal +
/// a <see cref="FileIdPathResolver"/> + <see cref="ContentIndexIncrementalUpdater.CreateFileReadClassifier"/>
/// (the optimized single-open content reader). It never throws and preserves the "index never suppresses a
/// live scan" invariant: any journal discontinuity, missing base, or resolver failure returns
/// <see cref="IncrementalUpdateOutcome.NeedsFullRebuild"/> so the scheduler falls back to a full rebuild
/// instead of trusting a partial incremental result.
/// </para>
/// </summary>
public sealed partial class ContentIndexIncrementalRefresher
{
    private readonly ContentIndexStore _store;
    private readonly IndexIngestionPolicy _policy;
    private readonly string _excludedStorageRoot;
    private readonly ContentIndexFreshnessEvaluator.JournalReader _journalReader;
    private readonly Func<string, IFileIdPathResolver?> _resolverFactory;
    private readonly Func<string, IncrementalFileRead?> _readAndClassify;
    private readonly Func<string, FileIdentity?>? _identityProvider;
    private readonly Func<string, VolumeBinding?> _volumeBindingReader;

    public ContentIndexIncrementalRefresher(
        ContentIndexStore store,
        IndexIngestionPolicy policy,
        string excludedStorageRoot,
        ContentIndexFreshnessEvaluator.JournalReader journalReader,
        Func<string, IFileIdPathResolver?> resolverFactory,
        Func<string, IncrementalFileRead?> readAndClassify,
        Func<string, FileIdentity?>? identityProvider = null,
        Func<string, VolumeBinding?>? volumeBindingReader = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedStorageRoot);
        _excludedStorageRoot = IndexScopeIdentity.NormalizePath(excludedStorageRoot);
        _journalReader = journalReader ?? throw new ArgumentNullException(nameof(journalReader));
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _readAndClassify = readAndClassify ?? throw new ArgumentNullException(nameof(readAndClassify));
        _identityProvider = identityProvider;
        _volumeBindingReader = volumeBindingReader ?? VolumeBindingReader.TryCapture;
    }

    /// <summary>Runs an incremental refresh; never throws. <paramref name="progress"/> (when supplied)
    /// receives a 0–99 percent-complete estimate during the change-resolution phase (the per-file read work),
    /// 100 when resolution is complete and the delta is being finalized, or -1 when the total is unknown.</summary>
    public IncrementalUpdateOutcome Refresh(
        string scopeId,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using IndexMutationContext mutation = _store.AcquireMutationContext();
        return RefreshUnderLease(mutation, scopeId, settings, builtUtc, progress, cancellationToken);
    }

    internal IncrementalUpdateOutcome RefreshUnderLease(
        IndexMutationContext mutation,
        string scopeId,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
        => RefreshWithDetailsUnderLease(
            mutation,
            scopeId,
            settings,
            builtUtc,
            minimumJournalChanges: null,
            progress,
            cancellationToken).Outcome;

    internal IncrementalRefreshResult RefreshIfJournalChangeCountExceedsUnderLease(
        IndexMutationContext mutation,
        string scopeId,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        int minimumJournalChanges,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
        => RefreshWithDetailsUnderLease(
            mutation,
            scopeId,
            settings,
            builtUtc,
            Math.Max(0, minimumJournalChanges),
            progress,
            cancellationToken);

    internal IncrementalRefreshResult RefreshWithDetailsUnderLease(
        IndexMutationContext mutation,
        string scopeId,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        int? minimumJournalChanges,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        IndexManifest? activeManifest = _store.TryReadCurrentIncrementalManifest();
        if (activeManifest is null)
        {
            YaguLog.For("ContentIndex").LogDebug("Incremental refresh: no trusted active layer manifests for scope {Scope} → needs full rebuild.", scopeId);
            return Incomplete(IncrementalUpdateOutcome.NeedsFullRebuild);
        }

        if (_store.TryReadCurrentFreshnessInputs() is not { } freshnessInputs)
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental refresh: scope {Scope} has unreadable file-identity metadata -> needs full rebuild.",
                scopeId);
            return Incomplete(IncrementalUpdateOutcome.NeedsFullRebuild);
        }

        if (freshnessInputs.FileIds.HasExtendedIdentities)
        {
            // Pre-fix ReFS layers used FILE_ID_128 while journal replay supplied unrelated V2 IDs. A
            // deletion cannot be mapped back to its old path, so an incremental checkpoint advance would
            // preserve stale content. Fail closed and let the scheduler perform one compatibility rebuild.
            YaguLog.For("ContentIndex").LogInformation(
                "Incremental refresh: scope {Scope} has legacy extended file identities -> needs one compatibility rebuild.",
                scopeId);
            return Incomplete(IncrementalUpdateOutcome.NeedsCompatibilityRebuild);
        }

        string root = activeManifest.NormalizedRootPath;
        string volumeIdentity = activeManifest.VolumeIdentity;
        UsnCheckpoint since = activeManifest.FreshnessCheckpoint;
        VolumeBinding? mounted = string.IsNullOrWhiteSpace(activeManifest.VolumeGuidPath)
            ? null
            : _volumeBindingReader(root);
        string volumeReason = "source volume unavailable";
        if (!string.IsNullOrWhiteSpace(activeManifest.VolumeGuidPath)
            && (mounted is not { } currentVolume
                || !VolumeBindingReader.MatchesManifest(activeManifest, currentVolume, out volumeReason)))
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental refresh: mounted volume mismatch for scope {Scope}: {Reason}.",
                scopeId,
                mounted is null ? "source volume unavailable" : volumeReason);
            return Incomplete(IncrementalUpdateOutcome.NeedsFullRebuild);
        }

        YaguLog.For("ContentIndex").LogDebug(
            "Incremental refresh starting: scope={Scope} root='{Root}' since={JournalId}/{NextUsn} baseSegments={SegmentCount}.",
            scopeId, root, since.JournalId, since.NextUsn, _store.ActiveSegmentCount());

        UsnReadResult read;
        try
        {
            read = _journalReader(root, since);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Incremental refresh: journal read threw for scope {Scope} → needs full rebuild.", scopeId);
            return Incomplete(IncrementalUpdateOutcome.NeedsFullRebuild);
        }

        // Any discontinuity (gap/journal-id change/unavailable) → can't trust an incremental step → full rebuild.
        if (read.Status != UsnReadStatus.Ok)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Incremental refresh: journal status {Status} for scope {Scope} (not continuous) → needs full rebuild.",
                read.Status, scopeId);
            return new IncrementalRefreshResult(
                IncrementalUpdateOutcome.NeedsFullRebuild,
                read.Changes.Count,
                ChangeCountComplete: false,
                ThresholdExceeded: minimumJournalChanges is { } threshold && read.Changes.Count > threshold);
        }

        bool thresholdExceeded = minimumJournalChanges is { } minimum
            && read.Changes.Count > minimum;
        if (minimumJournalChanges is not null && !thresholdExceeded)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Incremental refresh: scope={Scope} found {ChangeCount} journal change(s), at or below the post-build threshold {Threshold}; leaving them for normal maintenance.",
                scopeId,
                read.Changes.Count,
                minimumJournalChanges.Value);
            return Complete(IncrementalUpdateOutcome.NoChanges, read.Changes.Count, thresholdExceeded);
        }

        if (read.Changes.Count == 0)
        {
            YaguLog.For("ContentIndex").LogDebug("Incremental refresh: no journal changes for scope {Scope} → no-op.", scopeId);
            return Complete(IncrementalUpdateOutcome.NoChanges, 0, thresholdExceeded);
        }

        var changedIdentities = new HashSet<UsnFileIdentity>();
        foreach (UsnChange change in read.Changes)
            changedIdentities.Add(change.Identity);
        var metadata = _store.TryReadCurrentIncrementalMetadata(changedIdentities, cancellationToken);
        if (metadata is null)
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental refresh: active identity/path metadata was unreadable for scope {Scope} → needs full rebuild.",
                scopeId);
            return Complete(IncrementalUpdateOutcome.NeedsFullRebuild, read.Changes.Count, thresholdExceeded);
        }

        IFileIdPathResolver? resolver = _resolverFactory(root);
        if (resolver is null)
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental refresh: could not open a file-id path resolver for '{Root}' (scope {Scope}) → needs full rebuild.",
                root, scopeId);
            return Complete(IncrementalUpdateOutcome.NeedsFullRebuild, read.Changes.Count, thresholdExceeded);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedChangeSet resolved = ContentIndexUsnChangeResolver.Resolve(
                read.Changes,
                metadata.Value.PathsByIdentity,
                resolver,
                _readAndClassify,
                path => IndexedRootsPolicy.Covers(root, path)
                    && !IndexedRootsPolicy.Covers(_excludedStorageRoot, path),
                progress is null ? null : (done, total) => progress(total <= 0 ? -1 : Math.Clamp(done * 99 / total, 0, 99)),
                cancellationToken);

            YaguLog.For("ContentIndex").LogInformation(
                "Incremental refresh: scope={Scope} resolved {ChangeCount} journal change(s) → {ChangedCount} changed, {DeletedCount} deleted.",
                scopeId, read.Changes.Count, resolved.Changed.Count, resolved.Deleted.Count);
            progress?.Invoke(100);

            var updater = new ContentIndexIncrementalUpdater(_store, _policy, _identityProvider);
            VolumeBinding? beforePublish = mounted is null ? null : _volumeBindingReader(root);
            if (mounted is { } expectedVolume
                && (beforePublish is not { } publishVolume || !VolumeBindingReader.Matches(expectedVolume, publishVolume)))
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "Incremental refresh: mounted volume changed before publication for scope {Scope}; the previous checkpoint remains active.",
                    scopeId);
                return Complete(IncrementalUpdateOutcome.NeedsFullRebuild, read.Changes.Count, thresholdExceeded);
            }
            IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
                mutation, scopeId, volumeIdentity, root, resolved.Changed, resolved.Deleted,
                read.NextCheckpoint, settings, builtUtc, cancellationToken);
            YaguLog.For("ContentIndex").LogInformation("Incremental refresh: scope={Scope} outcome={Outcome}.", scopeId, outcome);
            return Complete(outcome, read.Changes.Count, thresholdExceeded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Incremental refresh: applying changes threw for scope {Scope} → needs full rebuild.", scopeId);
            return Complete(IncrementalUpdateOutcome.NeedsFullRebuild, read.Changes.Count, thresholdExceeded);
        }
        finally
        {
            (resolver as IDisposable)?.Dispose();
        }
    }

    private static IncrementalRefreshResult Incomplete(IncrementalUpdateOutcome outcome)
        => new(outcome, 0, ChangeCountComplete: false, ThresholdExceeded: false);

    private static IncrementalRefreshResult Complete(
        IncrementalUpdateOutcome outcome,
        int journalChangeCount,
        bool thresholdExceeded)
        => new(outcome, journalChangeCount, ChangeCountComplete: true, thresholdExceeded);
}
