using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Yagu.Services;

/// <summary>
/// Lazily loads <c>.gitignore</c> files as directories are encountered during a scan,
/// applying rules hierarchically — each <c>.gitignore</c> applies only to its own subtree.
/// </summary>
internal sealed class DynamicGitignoreMatcher
{
    private readonly string _rootDirectory;
    private readonly Dictionary<string, ParsedGitignore?> _rulesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _dirExclusionCache = new(StringComparer.OrdinalIgnoreCase);
    private int _skipped;

    /// <summary>Total files/directories skipped by gitignore rules.</summary>
    public int Skipped => Volatile.Read(ref _skipped);

    /// <summary>
    /// Include-extension overrides that take precedence over gitignore extension
    /// exclusions when <c>GitignoreTakesPrecedence</c> is false.
    /// Extensions without dot (e.g. <c>"cs"</c>).
    /// </summary>
    public IReadOnlySet<string> IncludeExtensionOverrides { get; set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public DynamicGitignoreMatcher(string rootDirectory)
    {
        _rootDirectory = rootDirectory.TrimEnd('\\');
    }

    /// <summary>
    /// Returns <c>true</c> if the directory should be skipped.
    /// Used by the managed-fallback walk where directories are entered one by one.
    /// </summary>
    public bool ShouldSkipDirectory(string dirFullPath)
    {
        var dirName = Path.GetFileName(dirFullPath);

        // .git is always excluded.
        if (dirName.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _skipped);
            return true;
        }

        if (MatchesAnyAncestorFolderRule(dirFullPath, dirName))
        {
            Interlocked.Increment(ref _skipped);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if the file should be skipped based on gitignore rules
    /// in its ancestor directories.
    /// Used by the managed-fallback walk (directories are already filtered).
    /// </summary>
    public bool ShouldSkipFile(string fileFullPath)
    {
        var fileName = Path.GetFileName(fileFullPath);
        var dir = Path.GetDirectoryName(fileFullPath);
        if (dir is null) return false;

        if (MatchesAnyAncestorFileRule(dir, fileName))
        {
            Interlocked.Increment(ref _skipped);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Full path check for flat-path backends (Everything SDK, es.exe) that don't
    /// walk directories. Checks every intermediate directory segment for folder
    /// exclusions and the file itself for extension exclusions.
    /// </summary>
    public bool ShouldSkipPath(string fileFullPath)
    {
        // Check each intermediate directory from the file up to the root.
        var dir = Path.GetDirectoryName(fileFullPath);
        while (dir is not null && dir.Length > _rootDirectory.Length)
        {
            if (IsDirExcludedCached(dir))
            {
                Interlocked.Increment(ref _skipped);
                return true;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Check the file itself (extension rules from any ancestor .gitignore).
        var fileName = Path.GetFileName(fileFullPath);
        var fileDir = Path.GetDirectoryName(fileFullPath);
        if (fileDir is not null && MatchesAnyAncestorFileRule(fileDir, fileName))
        {
            Interlocked.Increment(ref _skipped);
            return true;
        }

        return false;
    }

    // ─── Private helpers ────────────────────────────────────────────

    /// <summary>Cached directory-exclusion decision (memoised).</summary>
    private bool IsDirExcludedCached(string dirFullPath)
    {
        if (_dirExclusionCache.TryGetValue(dirFullPath, out bool cached))
            return cached;

        var dirName = Path.GetFileName(dirFullPath);
        if (string.IsNullOrEmpty(dirName))
        {
            _dirExclusionCache[dirFullPath] = false;
            return false;
        }

        if (dirName.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            _dirExclusionCache[dirFullPath] = true;
            return true;
        }

        // If the parent is excluded, so is this directory.
        var parent = Path.GetDirectoryName(dirFullPath);
        if (parent is not null && parent.Length > _rootDirectory.Length && IsDirExcludedCached(parent))
        {
            _dirExclusionCache[dirFullPath] = true;
            return true;
        }

        bool excluded = MatchesAnyAncestorFolderRule(dirFullPath, dirName);
        _dirExclusionCache[dirFullPath] = excluded;
        return excluded;
    }

    /// <summary>
    /// Walk from the item's parent directory up to <c>_rootDirectory</c>,
    /// checking each level's <c>.gitignore</c> folder rules against <paramref name="dirName"/>.
    /// </summary>
    private bool MatchesAnyAncestorFolderRule(string dirFullPath, string dirName)
    {
        var checkDir = Path.GetDirectoryName(dirFullPath);

        while (checkDir is not null && checkDir.Length >= _rootDirectory.Length)
        {
            var rules = GetOrLoadRules(checkDir);
            if (rules is not null)
            {
                if (rules.ExcludedFolders.Contains(dirName))
                    return true;

                foreach (var ep in rules.ExcludedFullPaths)
                {
                    if (dirFullPath.Equals(ep, StringComparison.OrdinalIgnoreCase) ||
                        dirFullPath.StartsWith(ep + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (checkDir.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
                break;
            checkDir = Path.GetDirectoryName(checkDir);
        }

        return false;
    }

    /// <summary>
    /// Walk from <paramref name="startDir"/> up to <c>_rootDirectory</c>,
    /// checking each level's <c>.gitignore</c> extension rules against <paramref name="fileName"/>.
    /// </summary>
    private bool MatchesAnyAncestorFileRule(string startDir, string fileName)
    {
        var checkDir = startDir;

        while (checkDir is not null && checkDir.Length >= _rootDirectory.Length)
        {
            var rules = GetOrLoadRules(checkDir);
            if (rules is not null && rules.ExcludesFile(fileName, IncludeExtensionOverrides))
                return true;

            if (checkDir.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
                break;
            checkDir = Path.GetDirectoryName(checkDir);
        }

        return false;
    }

    private ParsedGitignore? GetOrLoadRules(string directory)
    {
        if (_rulesCache.TryGetValue(directory, out var cached))
            return cached;

        ParsedGitignore? rules = null;
        var gitignorePath = Path.Combine(directory, ".gitignore");

        try
        {
            if (File.Exists(gitignorePath))
                rules = ParsedGitignore.Load(gitignorePath, directory);
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("DynamicGitignoreMatcher",
                $"Could not read {gitignorePath}: {ex.Message}");
        }

        _rulesCache[directory] = rules;
        return rules;
    }
}

/// <summary>
/// Parsed rules from a single <c>.gitignore</c> file, scoped to its directory.
/// </summary>
internal sealed class ParsedGitignore
{
    /// <summary>Bare folder-name segments (e.g. <c>node_modules</c>). Match anywhere in the subtree.</summary>
    public HashSet<string> ExcludedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions without dot (e.g. <c>log</c>). Match anywhere in the subtree.</summary>
    public HashSet<string> ExcludedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full resolved directory paths to exclude.</summary>
    public List<string> ExcludedFullPaths { get; } = [];

    /// <summary>Check whether a file name is excluded by extension rules.</summary>
    public bool ExcludesFile(string fileName, IReadOnlySet<string> includeExtOverrides)
    {
        if (ExcludedExtensions.Count == 0) return false;

        var ext = Path.GetExtension(fileName);
        if (ext.Length <= 1) return false;

        var extNoDot = ext.Substring(1);
        if (!ExcludedExtensions.Contains(extNoDot)) return false;

        // If include extensions take precedence, don't exclude.
        if (includeExtOverrides.Count > 0 && includeExtOverrides.Contains(extNoDot))
            return false;

        return true;
    }

    /// <summary>
    /// Parse a <c>.gitignore</c> file and return its rules.
    /// Reuses the same classification logic as <see cref="GitignoreService"/>.
    /// </summary>
    public static ParsedGitignore Load(string gitignorePath, string directory)
    {
        var rules = new ParsedGitignore();

        foreach (var rawLine in File.ReadLines(gitignorePath))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '!') continue; // negation — too complex for pre-filtering

            // Remove trailing unescaped spaces.
            while (line.Length > 0 && line[^1] == ' ' && (line.Length < 2 || line[^2] != '\\'))
                line = line[..^1];
            if (line.Length == 0) continue;

            // "*.ext" → extension exclusion.
            if (line.StartsWith("*.", StringComparison.Ordinal) && !line.Contains('/') && !line.Contains('\\'))
            {
                var ext = line[2..];
                if (ext.Length > 0 && ext.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                {
                    rules.ExcludedExtensions.Add(ext);
                    continue;
                }
            }

            bool dirOnly = line.EndsWith('/');
            var pattern = dirOnly ? line[..^1] : line;

            // Bare name → folder segment exclusion.
            if (!pattern.Contains('/') && !pattern.Contains('\\') &&
                !pattern.Contains('*') && !pattern.Contains('?'))
            {
                if (dirOnly || IsLikelyFolderName(pattern))
                {
                    rules.ExcludedFolders.Add(pattern);
                    continue;
                }
            }

            // Path-relative pattern → resolve to full path.
            if (pattern.Contains('/') || pattern.Contains('\\'))
            {
                var cleaned = pattern.TrimStart('/').Replace('/', '\\');
                var fullPath = Path.GetFullPath(Path.Combine(directory, cleaned));
                if (Directory.Exists(fullPath))
                {
                    rules.ExcludedFullPaths.Add(fullPath);
                }
                continue;
            }
        }

        return rules;
    }

    private static bool IsLikelyFolderName(string name)
    {
        if (name.StartsWith('.')) return true;
        if (!name.Contains('.')) return true;
        return false;
    }
}
