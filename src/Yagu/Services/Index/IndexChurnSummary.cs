namespace Yagu.Services.Index;

/// <summary>One directory that produced a share of an update pass's changes.</summary>
/// <param name="RootRelativeDirectory">Path relative to the indexed root, or
/// <see cref="IndexChurnSummary.OutsideRootBucket"/> when it could not be expressed that way.</param>
/// <param name="Count">How many changed or deleted paths it accounted for.</param>
public readonly record struct IndexChurnEntry(string RootRelativeDirectory, int Count);

/// <summary>
/// Summarizes where an incremental update's churn came from, so a user investigating an index that grows
/// faster than it can be cleaned up can see which folders to exclude.
/// <para>
/// The output is deliberately narrow: bounded to a few entries, truncated to a shallow depth, and always
/// expressed <b>relative to the indexed root</b>. It is written to the local log at Verbose only. It is
/// never sent anywhere, never used to change filters automatically, and never contains an absolute path.
/// </para>
/// </summary>
public static class IndexChurnSummary
{
    /// <summary>Stands in for any path that is not under the indexed root or cannot be interpreted.</summary>
    public const string OutsideRootBucket = "(outside this folder)";

    /// <summary>Stands in for changes directly in the indexed root itself.</summary>
    public const string RootBucket = "(this folder)";

    /// <summary>
    /// The busiest directories among <paramref name="paths"/>, most changes first, ties broken by name so
    /// the output is deterministic.
    /// </summary>
    /// <param name="paths">Changed and deleted paths from one pass.</param>
    /// <param name="root">The indexed root every path is expressed relative to.</param>
    /// <param name="depth">How many path segments below the root to keep (minimum 1).</param>
    /// <param name="take">Maximum number of entries to return (minimum 1).</param>
    public static IReadOnlyList<IndexChurnEntry> TopRootRelativeDirectories(
        IEnumerable<string> paths,
        string root,
        int depth,
        int take)
    {
        ArgumentNullException.ThrowIfNull(paths);
        depth = Math.Max(1, depth);
        take = Math.Max(1, take);

        string normalizedRoot = string.IsNullOrWhiteSpace(root)
            ? string.Empty
            : IndexScopeIdentity.NormalizePath(root).TrimEnd('\\');
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string bucket = Bucket(path, normalizedRoot, depth);
            counts[bucket] = counts.TryGetValue(bucket, out int existing) ? existing + 1 : 1;
        }

        return counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(entry => new IndexChurnEntry(entry.Key, entry.Value))
            .ToList();
    }

    /// <summary>A single bounded log line, or null when there was nothing to report.</summary>
    public static string? Describe(IReadOnlyList<IndexChurnEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Count == 0
            ? null
            : string.Join(", ", entries.Select(entry => $"{entry.RootRelativeDirectory} x{entry.Count}"));
    }

    private static string Bucket(string? path, string normalizedRoot, int depth)
    {
        if (string.IsNullOrWhiteSpace(path))
            return OutsideRootBucket;

        string normalized = IndexScopeIdentity.NormalizePath(path);

        if (normalizedRoot.Length == 0
            || !normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return OutsideRootBucket;
        }

        string relative = normalized[normalizedRoot.Length..].TrimStart('\\');
        if (relative.Length == 0)
            return RootBucket;

        string[] segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // The last segment is the file itself; only its containing directories identify a folder.
        int directorySegments = Math.Min(depth, segments.Length - 1);
        return directorySegments <= 0 ? RootBucket : string.Join('\\', segments.Take(directorySegments));
    }
}
