namespace Yagu.Services.Index;

/// <summary>
/// Pure normalization/mutation helpers for the persisted <c>AppSettings.IndexedRoots</c> list (plan
/// §6.1): the folders the user has registered for content indexing. Entries are canonicalized with
/// <see cref="IndexScopeIdentity.NormalizePath"/>, de-duplicated case-insensitively (NTFS is
/// case-insensitive by default), blank-dropped, capped, and reduced to a non-overlapping coverage set:
/// when one registered root contains another, only the broader ancestor is maintained. Nothing here
/// touches disk; the Indexing tab and the CLI root commands consume it, and a build/auto-build iterates it.
/// </summary>
public static class IndexedRootsPolicy
{
    /// <summary>Maximum number of registered roots (keeps the list and status UI bounded).</summary>
    public const int MaxIndexedRoots = 64;

    /// <summary>
    /// Canonicalizes a list of roots: normalize each path, drop blanks, de-dup case-insensitively, and
    /// collapse ancestor/descendant overlaps to the broader ancestor. This prevents automatic maintenance
    /// from crawling and storing the same subtree twice (for example <c>C:\</c> plus <c>C:\src</c>).
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? roots)
    {
        var result = new List<string>();
        if (roots is null)
            return result;

        foreach (string? raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            string normalized = IndexScopeIdentity.NormalizePath(raw);
            if (normalized.Length == 0)
                continue;

            // An existing broader/equal root already covers this path; a second physical index would
            // duplicate its subtree without improving correctness or search coverage.
            if (result.Any(existing => Covers(existing, normalized)))
                continue;

            // A newly-added broader root supersedes any narrower roots. Preserve the earliest covered
            // root's list position so normalization remains stable and mostly order-preserving.
            int insertAt = result.FindIndex(existing => Covers(normalized, existing));
            if (insertAt >= 0)
            {
                result.RemoveAll(existing => Covers(normalized, existing));
                result.Insert(insertAt, normalized);
            }
            else
            {
                result.Add(normalized);
            }
        }
        return result.Count <= MaxIndexedRoots ? result : result.Take(MaxIndexedRoots).ToList();
    }

    /// <summary>True when <paramref name="path"/> (canonicalized) is already registered.</summary>
    public static bool Contains(IEnumerable<string> roots, string path)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string normalized = IndexScopeIdentity.NormalizePath(path);
        foreach (string root in roots)
        {
            if (string.Equals(IndexScopeIdentity.NormalizePath(root), normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Returns a normalized copy of <paramref name="roots"/> with <paramref name="path"/> added (if new and under the cap).</summary>
    public static List<string> Add(IEnumerable<string> roots, string path)
    {
        var list = Normalize(roots);
        if (string.IsNullOrWhiteSpace(path))
            return list;
        string normalized = IndexScopeIdentity.NormalizePath(path);
        if (normalized.Length == 0)
            return list;
        // Normalize after appending so a broader new root replaces covered descendants and a narrower
        // new root is ignored when an existing ancestor already covers it.
        if (list.Count >= MaxIndexedRoots && !list.Any(existing => Covers(normalized, existing)))
            return list;
        list.Add(normalized);
        return Normalize(list);
    }

    /// <summary>Returns a normalized copy of <paramref name="roots"/> with <paramref name="path"/> removed.</summary>
    public static List<string> Remove(IEnumerable<string> roots, string path)
    {
        var list = Normalize(roots);
        if (string.IsNullOrWhiteSpace(path))
            return list;
        string normalized = IndexScopeIdentity.NormalizePath(path);
        list.RemoveAll(r => string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>True when <paramref name="ancestor"/> is equal to or contains <paramref name="path"/>.</summary>
    public static bool Covers(string ancestor, string path)
    {
        if (string.IsNullOrWhiteSpace(ancestor) || string.IsNullOrWhiteSpace(path))
            return false;
        string normalizedAncestor = IndexScopeIdentity.NormalizePath(ancestor);
        string normalizedPath = IndexScopeIdentity.NormalizePath(path);
        if (normalizedAncestor.Length == 0 || normalizedPath.Length == 0)
            return false;
        if (string.Equals(normalizedAncestor, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = normalizedAncestor.EndsWith('\\') ? normalizedAncestor : normalizedAncestor + "\\";
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the most specific registered root that contains <paramref name="path"/>, or null when no
    /// registered root covers it. The input need not already be normalized.
    /// </summary>
    public static string? FindBestCoveringRoot(IEnumerable<string>? roots, string path)
    {
        if (roots is null || string.IsNullOrWhiteSpace(path))
            return null;
        string normalizedPath = IndexScopeIdentity.NormalizePath(path);
        string? best = null;
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
            if (Covers(normalizedRoot, normalizedPath)
                && (best is null || normalizedRoot.Length > best.Length))
            {
                best = normalizedRoot;
            }
        }
        return best;
    }

    /// <summary>Returns registered descendants that would be superseded by adding <paramref name="ancestor"/>.</summary>
    public static IReadOnlyList<string> FindCoveredDescendants(IEnumerable<string>? roots, string ancestor)
    {
        if (roots is null || string.IsNullOrWhiteSpace(ancestor))
            return Array.Empty<string>();
        string normalizedAncestor = IndexScopeIdentity.NormalizePath(ancestor);
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .Where(root => !string.Equals(root, normalizedAncestor, StringComparison.OrdinalIgnoreCase)
                && Covers(normalizedAncestor, root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
