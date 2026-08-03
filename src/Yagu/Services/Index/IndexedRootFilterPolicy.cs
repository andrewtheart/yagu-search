namespace Yagu.Services.Index;

/// <summary>
/// Pure helpers for the per-root build-time glob overrides (<see cref="IndexedRootFilter"/>). Canonicalizes
/// the persisted list (normalize each <see cref="IndexedRootFilter.Path"/>, trim the globs, drop blank-path
/// or globs-empty entries, de-duplicate by path last-wins, cap the count), finds the override for a given
/// root, and resolves the <b>effective</b> <see cref="IndexIngestionPolicy"/> for a root by merging the
/// global settings with that root's override. Nothing here touches disk. Because these only shape what a
/// build ingests — and any unindexed path is live-scanned at query time — a mis-configured override can
/// never hide a search match (plan §6.1).
/// </summary>
public static class IndexedRootFilterPolicy
{
    /// <summary>Maximum retained per-root overrides (bounds the settings list and the Indexing-tab UI).</summary>
    public const int MaxFilters = 256;

    /// <summary>
    /// Canonicalizes a list of overrides: normalize each path, trim the glob strings, drop entries whose
    /// path is blank or whose globs are both empty (an override with no globs is inert), de-duplicate by
    /// normalized path (last wins), and cap at <see cref="MaxFilters"/>. Order-preserved.
    /// </summary>
    public static List<IndexedRootFilter> Normalize(IEnumerable<IndexedRootFilter>? filters)
    {
        var result = new List<IndexedRootFilter>();
        if (filters is null)
            return result;

        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (IndexedRootFilter? filter in filters)
        {
            if (filter is null)
                continue;
            string path = IndexScopeIdentity.NormalizePath(filter.Path ?? string.Empty);
            if (path.Length == 0)
                continue;
            string include = (filter.IncludeGlobs ?? string.Empty).Trim();
            string exclude = (filter.ExcludeGlobs ?? string.Empty).Trim();
            if (include.Length == 0 && exclude.Length == 0)
                continue; // no globs -> inert -> drop

            var entry = new IndexedRootFilter { Path = path, IncludeGlobs = include, ExcludeGlobs = exclude };
            if (byPath.TryGetValue(path, out int existing))
            {
                result[existing] = entry; // last wins
                continue;
            }
            if (result.Count >= MaxFilters)
                continue;
            byPath[path] = result.Count;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>The override registered for <paramref name="root"/> (matched by canonical path), or null.</summary>
    public static IndexedRootFilter? Find(IEnumerable<IndexedRootFilter>? filters, string root)
    {
        if (filters is null || string.IsNullOrWhiteSpace(root))
            return null;
        string key = IndexScopeIdentity.NormalizePath(root);
        foreach (IndexedRootFilter? filter in filters)
        {
            if (filter is null)
                continue;
            if (string.Equals(IndexScopeIdentity.NormalizePath(filter.Path ?? string.Empty), key, StringComparison.OrdinalIgnoreCase))
                return filter;
        }
        return null;
    }

    /// <summary>
    /// The effective ingestion policy for <paramref name="root"/>: the global settings merged with the
    /// root's override (global excludes + the root's extra excludes as the baseline, with the root's
    /// include globs re-admitting). Roots without an override get the global-only policy.
    /// </summary>
    public static IndexIngestionPolicy ResolvePolicy(AppSettings settings, string root, int maxDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IndexIngestionPolicy.FromSettings(settings, Find(settings.IndexedRootFilters, root), maxDepth);
    }
}
