namespace Yagu.Helpers;

/// <summary>
/// Detects whether a Traditional search query is exactly one complete file path (and nothing else),
/// so the UI can short-circuit to displaying just that file regardless of the Directory box.
/// </summary>
public static class SingleFilePathQueryDetector
{
    /// <summary>
    /// Returns the fully-qualified path when <paramref name="query"/> is a single complete path to an
    /// existing file (optionally wrapped in one pair of surrounding double quotes); otherwise null.
    /// Bare filenames and relative paths return null so an ordinary content search is never hijacked.
    /// </summary>
    public static string? Resolve(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var candidate = query.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
            candidate = candidate[1..^1].Trim();
        if (candidate.Length == 0)
            return null;

        try
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        catch
        {
            // Invalid path characters, too-long path, etc. — treat as an ordinary search query.
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="query"/> looks like a fully-qualified Windows file/directory
    /// path (drive-qualified like <c>C:\...</c> or a UNC path like <c>\\server\share</c>), regardless of
    /// whether the target exists. Used to suppress backslash-escape heuristics — notably the literal
    /// "\n" multiline suggestion — that would otherwise false-positive on a path segment such as
    /// <c>\net6.0</c> or <c>\node_modules</c>, where the backslash is a separator, not a regex escape.
    /// </summary>
    public static bool LooksLikePath(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var candidate = query.Trim();
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
            candidate = candidate[1..^1].Trim();
        if (candidate.Length == 0)
            return false;

        try
        {
            return Path.IsPathFullyQualified(candidate);
        }
        catch
        {
            return false;
        }
    }
}
