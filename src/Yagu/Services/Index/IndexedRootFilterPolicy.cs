using Yagu.Helpers;

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

    /// <summary>
    /// Adds literal absolute directory paths to one root's exclude patterns while preserving its existing
    /// include and exclude globs. Paths are normalized, de-duplicated case-insensitively, and serialized
    /// with the same semicolon delimiter consumed by <see cref="IndexIngestionPolicy.FromSettings(AppSettings, IndexedRootFilter?, int)"/>.
    /// </summary>
    public static List<IndexedRootFilter> AddExcludedPaths(
        IEnumerable<IndexedRootFilter>? filters,
        string root,
        IEnumerable<string>? excludedPaths)
    {
        List<IndexedRootFilter> normalized = Normalize(filters);
        string key = IndexScopeIdentity.NormalizePath(root);
        if (key.Length == 0 || excludedPaths is null)
            return normalized;

        IndexedRootFilter? existing = Find(normalized, key);
        var excludes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in SplitPatterns(existing?.ExcludeGlobs))
        {
            if (seen.Add(item))
                excludes.Add(item);
        }
        foreach (string? path in excludedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            string normalizedPath = IndexScopeIdentity.NormalizePath(path);
            if (seen.Add(normalizedPath))
                excludes.Add(normalizedPath);
        }
        if (excludes.Count == 0)
            return normalized;

        normalized.RemoveAll(filter => string.Equals(
            IndexScopeIdentity.NormalizePath(filter.Path), key, StringComparison.OrdinalIgnoreCase));
        normalized.Add(new IndexedRootFilter
        {
            Path = key,
            IncludeGlobs = existing?.IncludeGlobs ?? string.Empty,
            ExcludeGlobs = string.Join("; ", excludes),
        });
        return Normalize(normalized);
    }

    private static IEnumerable<string> SplitPatterns(string? value)
        => GlobMatcher.SplitPatternList(value);

    /// <summary>
    /// Registered roots whose effective build filters contain a literal absolute directory path. Such a
    /// pattern used to be inert (it only ever matched a file path equal to the directory itself) and now
    /// excludes the whole subtree, so an index built before that change holds files the filters now reject.
    /// A literal path in the global excluded globs affects every registered root.
    /// </summary>
    public static IReadOnlyList<string> FindRootsAffectedByLiteralPathFilters(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> roots = IndexedRootsPolicy.Normalize(settings.IndexedRoots);
        if (roots.Count == 0)
            return Array.Empty<string>();
        if (HasLiteralPath(settings.IndexExcludedGlobs))
            return roots;

        return [.. roots.Where(root =>
        {
            IndexedRootFilter? filter = Find(settings.IndexedRootFilters, root);
            return HasLiteralPath(filter?.ExcludeGlobs) || HasLiteralPath(filter?.IncludeGlobs);
        })];
    }

    private static bool HasLiteralPath(string? value)
        => SplitPatterns(value).Any(static item =>
            GlobMatcher.IsRootedPathStart(item) && !item.Contains('*') && !item.Contains('?'));
}
