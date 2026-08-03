using System.Linq;

namespace Yagu.Services.Index;

/// <summary>
/// Folds a layered index (base + delta segments) into a single fresh base generation (plan §11.4). It
/// resolves every path's authoritative entry with the same newest-first, tombstone-aware semantics as
/// <see cref="LayeredContentIndexQuerySession"/>, so a compacted base is query-equivalent to the layered
/// index it replaces — no file is re-read (the existing per-document trigram sets are reused), and hard
/// links that shared content within a layer keep sharing it. The compacted base takes the newest layer's
/// freshness checkpoint (segments are only ever appended from continuous USN replays, so every retained
/// document is proven fresh as of that point).
/// </summary>
public static class ContentIndexCompactor
{
    /// <summary>Builds the compacted base generation for <paramref name="handle"/>.</summary>
    public static ContentIndexGeneration Compact(
        ContentIndexStore.LayeredIndexHandle handle,
        IndexIngestionPolicy policy,
        DateTimeOffset builtUtc)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(policy);

        var builder = new ContentIndexGenerationBuilder(policy);
        // A path is "decided" once the newest layer that owns (or tombstones) it has been processed.
        var decided = new HashSet<string>(StringComparer.Ordinal);
        IndexManifest baseManifest = handle.Base.Manifest;
        if (!string.IsNullOrWhiteSpace(baseManifest.VolumeGuidPath)
            && VolumeBindingReader.TryCapture(baseManifest.NormalizedRootPath) is { } volumeBinding
            && VolumeBindingReader.MatchesManifest(baseManifest, volumeBinding, out _))
        {
            builder.SeedVolumeBinding(volumeBinding);
        }
        else
        {
            builder.SeedVolumeSerialNumber(baseManifest.VolumeSerialNumber);
        }

        // Newest → oldest: newest segment … oldest segment … base.
        for (int i = handle.Segments.Count - 1; i >= 0; i--)
        {
            ContentIndexDeltaSegment seg = handle.Segments[i];
            // Tombstones decide (and delete) their paths before this segment's own additions.
            foreach (string removed in seg.RemovedPaths)
                decided.Add(removed);
            AddLayerDocuments(builder, seg.Added, decided);
        }
        AddLayerDocuments(builder, handle.Base, decided);

        UsnCheckpoint checkpoint = handle.Segments.Count > 0
            ? handle.Segments[^1].FreshnessCheckpoint
            : baseManifest.FreshnessCheckpoint;
        DateTimeOffset createdUtc = baseManifest.CreatedUtc ?? baseManifest.BuiltUtc;
        DateTimeOffset? lastIncrementalUpdateUtc = LatestIncrementalUpdate(
            baseManifest.LastIncrementalUpdateUtc,
            handle.Segments.Select(segment => segment.Added.Manifest.LastIncrementalUpdateUtc));

        return builder.Build(
            baseManifest.ScopeId,
            baseManifest.VolumeIdentity,
            baseManifest.NormalizedRootPath,
            checkpoint,
            builtUtc,
            createdUtc,
            lastIncrementalUpdateUtc);
    }

    private static DateTimeOffset? LatestIncrementalUpdate(
        DateTimeOffset? current,
        IEnumerable<DateTimeOffset?> candidates)
    {
        foreach (DateTimeOffset? candidate in candidates)
        {
            if (Nullable.Compare(candidate, current) > 0)
                current = candidate;
        }
        return current;
    }

    private static void AddLayerDocuments(ContentIndexGenerationBuilder builder, ContentIndexGeneration layer, HashSet<string> decided)
    {
        IReadOnlyList<IReadOnlyCollection<Trigram>> docs = layer.Documents;
        IReadOnlyList<UsnFileIdentity?> identities = layer.ContentIdentities;
        // Content-id local to this layer → the new content id in the compacted base, so hard links share.
        var contentIdMap = new Dictionary<long, long>();

        // Deterministic order (content id, then path) so hard links are added right after their content.
        foreach (var (path, entry) in layer.Aliases.OrderBy(kv => kv.Value.ContentId).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!decided.Add(path))
                continue; // a newer layer already decided this path

            long layerContentId = entry.ContentId;
            if (contentIdMap.TryGetValue(layerContentId, out long newContentId))
            {
                builder.AddHardLink(path, newContentId);
            }
            else
            {
                IReadOnlyCollection<Trigram> trigrams = docs[(int)layerContentId];
                UsnFileIdentity? identity = identities[(int)layerContentId];
                long assigned = builder.AddClassifiedDocument(path, trigrams, identity);
                contentIdMap[layerContentId] = assigned;
            }
        }
    }
}
