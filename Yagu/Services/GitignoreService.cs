using System.IO;

namespace Yagu.Services;

/// <summary>
/// Reads <c>.gitignore</c> files recursively under a directory and extracts
/// folder-segment and extension exclusion patterns suitable for pushing into
/// the Everything SDK query or the managed-fallback directory filter.
/// </summary>
internal static class GitignoreService
{
    /// <summary>Results of scanning a directory tree for <c>.gitignore</c> rules.</summary>
    internal sealed class GitignoreRules
    {
        /// <summary>
        /// Folder-name segments to exclude everywhere (e.g. <c>node_modules</c>, <c>.git</c>).
        /// Already de-duplicated, no slashes, lowercase-normalized.
        /// These map to Everything SDK <c>!"\segment\"</c> terms.
        /// </summary>
        public IReadOnlySet<string> ExcludedFolders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Extensions to exclude (without dot, e.g. <c>log</c>, <c>tmp</c>).
        /// These map to Everything SDK <c>!ext:ext</c> terms.
        /// </summary>
        public IReadOnlySet<string> ExcludedExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Full directory paths that should be excluded (from path-relative gitignore rules).
        /// These map to Everything SDK <c>!"C:\full\path\"</c> terms.
        /// </summary>
        public IReadOnlyList<string> ExcludedFullPaths { get; init; } = [];
    }

    /// <summary>
    /// Scans <paramref name="rootDirectory"/> recursively for <c>.gitignore</c> files
    /// and returns aggregated exclusion rules. Always includes <c>.git</c> as an
    /// excluded folder. Limits scan depth and file count for performance.
    /// </summary>
    public static GitignoreRules Scan(string rootDirectory)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git" };
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullPaths = new List<string>();

        try
        {
            ScanRecursive(rootDirectory, rootDirectory, folders, extensions, fullPaths, depth: 0, maxDepth: 20);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("GitignoreService", $"Error scanning gitignore files: {ex.Message}");
        }

        LogService.Instance.Info("GitignoreService",
            $"Scanned gitignore: {folders.Count} excluded folders, {extensions.Count} excluded extensions, {fullPaths.Count} full-path exclusions");

        return new GitignoreRules
        {
            ExcludedFolders = folders,
            ExcludedExtensions = extensions,
            ExcludedFullPaths = fullPaths,
        };
    }

    private static void ScanRecursive(
        string rootDir,
        string currentDir,
        HashSet<string> folders,
        HashSet<string> extensions,
        List<string> fullPaths,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth) return;

        var gitignorePath = Path.Combine(currentDir, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            try
            {
                ParseGitignoreFile(gitignorePath, currentDir, folders, extensions, fullPaths);
            }
            catch (Exception ex)
            {
                LogService.Instance.Info("GitignoreService", $"Could not read {gitignorePath}: {ex.Message}");
            }
        }

        // Recurse into subdirectories, skipping already-known excluded folders.
        try
        {
            foreach (var subDir in System.IO.Directory.EnumerateDirectories(currentDir))
            {
                var dirName = Path.GetFileName(subDir);
                if (folders.Contains(dirName)) continue;
                ScanRecursive(rootDir, subDir, folders, extensions, fullPaths, depth + 1, maxDepth);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void ParseGitignoreFile(
        string path,
        string gitignoreDir,
        HashSet<string> folders,
        HashSet<string> extensions,
        List<string> fullPaths)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            // Skip empty lines and comments.
            if (line.Length == 0 || line[0] == '#') continue;

            // Skip negation rules (these un-ignore files — too complex for pre-filtering).
            if (line[0] == '!') continue;

            // Remove trailing spaces (unless escaped with backslash).
            while (line.Length > 0 && line[^1] == ' ' && (line.Length < 2 || line[^2] != '\\'))
                line = line[..^1];
            if (line.Length == 0) continue;

            // Classify the pattern.

            // "*.ext" → extension exclusion (global, not path-relative).
            if (line.StartsWith("*.") && !line.Contains('/') && !line.Contains('\\'))
            {
                var ext = line[2..];
                if (ext.Length > 0 && ext.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                {
                    extensions.Add(ext);
                    continue;
                }
            }

            // Strip trailing slash (used to indicate directory-only patterns).
            bool dirOnly = line.EndsWith('/');
            var pattern = dirOnly ? line[..^1] : line;

            // Bare name with no path separators and no wildcards → folder segment exclusion.
            if (!pattern.Contains('/') && !pattern.Contains('\\') &&
                !pattern.Contains('*') && !pattern.Contains('?'))
            {
                // Could be a file or folder name. Gitignore treats bare names as
                // matching anywhere in the tree. For performance, we treat them as
                // folder exclusions (this is the common case: node_modules, dist, etc.)
                // When dirOnly is set, this is definitely a directory.
                // Even without dirOnly, excluding common folder names is a net win.
                if (dirOnly || IsLikelyFolderName(pattern))
                {
                    folders.Add(pattern);
                    continue;
                }
            }

            // Pattern with leading slash → relative to gitignore directory.
            // Pattern with embedded slash → relative path.
            if (pattern.Contains('/') || pattern.Contains('\\'))
            {
                // Resolve to a full path relative to the gitignore's directory.
                var cleaned = pattern.TrimStart('/').Replace('/', '\\');
                var fullPath = Path.GetFullPath(Path.Combine(gitignoreDir, cleaned));
                if (System.IO.Directory.Exists(fullPath))
                {
                    fullPaths.Add(fullPath);
                }
                continue;
            }
        }
    }

    /// <summary>
    /// Heuristic: a bare name without an extension (no dots) is likely a folder name.
    /// Names with dots (like <c>thumbs.db</c>) are likely files.
    /// </summary>
    private static bool IsLikelyFolderName(string name)
    {
        // Names starting with a dot (like .cache, .vs, .idea) are likely folders.
        if (name.StartsWith('.')) return true;
        // Names without any dot are likely folders (node_modules, dist, build, etc.).
        if (!name.Contains('.')) return true;
        return false;
    }
}
