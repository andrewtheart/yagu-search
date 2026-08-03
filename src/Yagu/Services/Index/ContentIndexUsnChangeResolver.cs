namespace Yagu.Services.Index;

/// <summary>The resolved change set for one incremental pass: files to (re)index and paths to tombstone.</summary>
public readonly record struct ResolvedChangeSet(
    IReadOnlyList<IncrementalChange> Changed,
    IReadOnlyList<string> Deleted);

/// <summary>
/// Turns name-less USN change records into the concrete created/modified/deleted paths an incremental update
/// needs (plan §3.5/§11.4). For each changed identity it resolves the <b>current</b> path via an
/// <see cref="IFileIdPathResolver"/>; a path that still exists under the root is (re)read and indexed, while
/// an identity that no longer resolves (or resolved outside the root) tombstones the path(s) the active
/// layered index held for it. A rename within the root both re-indexes the new path and tombstones the old one.
/// Pure: the path resolver and byte reader are injected, so every branch is unit-testable without a volume.
/// </summary>
public static class ContentIndexUsnChangeResolver
{
    /// <summary>
    /// Resolves <paramref name="changes"/> against <paramref name="baseGeneration"/> (used to recover the
    /// prior path of a deleted/renamed identity). <paramref name="readAndClassify"/> reads and classifies a
    /// file through the optimized content reader. A null/unreadable result tombstones the current and prior
    /// paths, forcing that file to live-scan after the journal checkpoint advances instead of trusting stale
    /// older content. <paramref name="isUnderRoot"/> scopes resolved paths to the
    /// indexed root. <paramref name="progress"/> (when supplied) is invoked periodically with
    /// <c>(recordsProcessed, totalRecords)</c> so a caller can show a percent-complete during this (per-file
    /// read) phase.
    /// </summary>
    public static ResolvedChangeSet Resolve(
        IReadOnlyList<UsnChange> changes,
        ContentIndexGeneration baseGeneration,
        IFileIdPathResolver resolver,
        Func<string, IncrementalFileRead?> readAndClassify,
        Func<string, bool> isUnderRoot,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(baseGeneration);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(readAndClassify);
        ArgumentNullException.ThrowIfNull(isUnderRoot);

        // Compatibility overload for callers/tests with an in-memory generation. Production incremental
        // maintenance uses the lightweight identity→paths overload below and never loads posting data.
        FileIdMap fileIds = baseGeneration.BuildFileIdMap();
        var pathsByContent = new Dictionary<long, List<string>>();
        foreach (var (path, entry) in baseGeneration.Aliases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pathsByContent.TryGetValue(entry.ContentId, out List<string>? list))
                pathsByContent[entry.ContentId] = list = new List<string>();
            list.Add(path); // aliases keys are already normalized
        }

        var pathsByIdentity = new Dictionary<UsnFileIdentity, IReadOnlyList<string>>();
        foreach (UsnChange change in changes)
        {
            if (fileIds.TryGetContentId(change.Identity, out long contentId))
                pathsByIdentity[change.Identity] = pathsByContent[contentId];
        }
        return Resolve(
            changes,
            pathsByIdentity,
            resolver,
            readAndClassify,
            isUnderRoot,
            progress,
            cancellationToken);
    }

    /// <summary>Resolves journal changes using only the active layered index's durable identity-to-prior-
    /// path map. This is the production maintenance overload: callers can read that metadata without
    /// loading document trigram data or posting indexes.</summary>
    public static ResolvedChangeSet Resolve(
        IReadOnlyList<UsnChange> changes,
        IReadOnlyDictionary<UsnFileIdentity, IReadOnlyList<string>> priorPathsByIdentity,
        IFileIdPathResolver resolver,
        Func<string, IncrementalFileRead?> readAndClassify,
        Func<string, bool> isUnderRoot,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(priorPathsByIdentity);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(readAndClassify);
        ArgumentNullException.ThrowIfNull(isUnderRoot);

        var changed = new List<IncrementalChange>();
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var seenIdentities = new HashSet<UsnFileIdentity>();

        int total = changes.Count;
        int processed = 0;
        foreach (UsnChange change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (progress is not null && (processed % 512 == 0 || processed == total))
                progress(processed, total);

            if (!seenIdentities.Add(change.Identity))
                continue; // one decision per identity (reasons are already OR-folded upstream)

            IReadOnlyList<string> priorPaths = priorPathsByIdentity.TryGetValue(change.Identity, out IReadOnlyList<string>? paths)
                ? paths
                : Array.Empty<string>();

            string? currentPath = resolver.TryResolvePath(change.Identity);

            if (currentPath is not null && isUnderRoot(currentPath))
            {
                string normalizedCurrent = IndexScopeIdentity.NormalizePath(currentPath);
                IncrementalFileRead? read = readAndClassify(currentPath);
                if (read is { } r)
                    changed.Add(new IncrementalChange(currentPath, r.Classification, r.Identity));
                else
                    deleted.Add(normalizedCurrent); // unreadable now → never retain a stale same-path alias

                // A rename within the root: tombstone prior paths that are no longer the current path.
                foreach (string basePath in priorPaths)
                {
                    if (read is null || !string.Equals(basePath, normalizedCurrent, StringComparison.Ordinal))
                        deleted.Add(basePath);
                }
            }
            else
            {
                // Deleted, moved out of the root, or unresolvable → tombstone whatever the active index held.
                foreach (string basePath in priorPaths)
                    deleted.Add(basePath);
            }
        }

        return new ResolvedChangeSet(changed, deleted.Count == 0 ? Array.Empty<string>() : new List<string>(deleted));
    }
}
