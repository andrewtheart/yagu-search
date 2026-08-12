namespace Yagu.Services.Index;

/// <summary>A conservative build-time exclusion proposed during first-run drive indexing.</summary>
public readonly record struct IndexOnboardingFilterSuggestion(string Path, string Description);

/// <summary>
/// Pure helpers for the "add a folder to the content index" onboarding flow — the clickable status-bar
/// prompt (shown when a searched folder has no index) and the one-time first-run prompt. It computes the
/// ancestor <em>"subpart of the path"</em> choices a user may pick to index (indexing a broader ancestor
/// covers more searches), and a cheap no-IO heuristic for whether a chosen path is a very large root that
/// warrants a warning before an unattended build. Pure and side-effect free so it is unit-tested; the
/// WinUI layer renders the choices and does any bounded on-disk size probe itself.
/// </summary>
public static class IndexOnboardingPlan
{
    /// <summary>Maximum number of ancestor path choices offered (keeps the picker small and legible).</summary>
    public const int MaxPathChoices = 8;

    /// <summary>Bounded file count at/above which a folder is treated as "very large" and warned about
    /// before an unattended index build (the UI probes up to this many files within a time budget).</summary>
    public const int LargeFolderFileThreshold = 200_000;

    // Well-known huge directories that sit directly under a drive root. Indexing one of these (or a whole
    // drive) can take a long time and a lot of disk, so the UI warns first.
    private static readonly HashSet<string> KnownLargeLeaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "Users",
    };

    /// <summary>
    /// The folder plus each of its ancestors up to (and including) the drive/UNC root — normalized and
    /// most-specific-first, de-duplicated, capped at <see cref="MaxPathChoices"/>. These are the
    /// "subpart of the path" options a user can choose to add to the index. Returns an empty list for a
    /// null/blank/invalid path.
    /// </summary>
    public static IReadOnlyList<string> PathChoices(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Array.Empty<string>();

        string node = SafeNormalize(folder);
        if (node.Length == 0)
            return Array.Empty<string>();

        var choices = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (choices.Count < MaxPathChoices)
        {
            if (seen.Add(node))
                choices.Add(node);

            string? parent = TryGetParent(node);
            if (string.IsNullOrEmpty(parent))
                break;
            string parentNorm = SafeNormalize(parent);
            if (parentNorm.Length == 0)
                break;
            node = parentNorm;
        }

        return choices;
    }

    /// <summary>
    /// A cheap (no-IO) heuristic for whether <paramref name="path"/> is a very large root that warrants a
    /// warning before an unattended index build: a bare drive root (e.g. <c>C:\</c>), a UNC share root, or a
    /// well-known huge directory (Windows, Program Files, Program Files (x86), ProgramData, Users) sitting
    /// directly under a drive root. It is intentionally conservative — the UI may additionally run a bounded
    /// file-count probe and warn on <see cref="LargeFolderFileThreshold"/>.
    /// </summary>
    public static bool IsLikelyLargeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string norm = SafeNormalize(path);
        if (norm.Length == 0)
            return false;

        if (IsBareDriveRoot(norm))
            return true;

        // A UNC share root ("\\server\share") — no further ancestor to narrow to.
        if (norm.StartsWith(@"\\", StringComparison.Ordinal) && TryGetParent(norm) is null)
            return true;

        string leaf = LeafName(norm);
        if (KnownLargeLeaves.Contains(leaf))
        {
            // Only a TOP-LEVEL well-known dir (directly under a drive root) is treated as huge — not a
            // nested folder that merely happens to be named "Users"/"Windows" deep in a tree.
            string? parent = TryGetParent(norm);
            return !string.IsNullOrEmpty(parent) && IsBareDriveRoot(SafeNormalize(parent!));
        }

        return false;
    }

    /// <summary>
    /// Returns conservative system-path exclusions covered by at least one candidate index root. Windows,
    /// installed-program, and package-cache folders are proposed only on the Windows drive; filesystem
    /// metadata, recovery, recycle-bin, and performance-log folders are proposed on every covered drive.
    /// The caller presents these as optional, preselected choices rather than silently applying them.
    /// </summary>
    public static IReadOnlyList<IndexOnboardingFilterSuggestion> SuggestedSystemExclusions(
        IEnumerable<string>? candidateRoots,
        string? windowsDirectory = null)
    {
        List<string> roots = IndexedRootsPolicy.Normalize(candidateRoots);
        if (roots.Count == 0)
            return Array.Empty<IndexOnboardingFilterSuggestion>();

        var suggestions = new List<IndexOnboardingFilterSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfCovered(string path, string description)
        {
            string normalized = SafeNormalize(path);
            if (!roots.Any(root => IndexedRootsPolicy.Covers(root, normalized))
                || !seen.Add(normalized))
            {
                return;
            }
            suggestions.Add(new IndexOnboardingFilterSuggestion(normalized, description));
        }

        string windows = SafeNormalize(windowsDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string windowsDrive = Path.GetPathRoot(windows) ?? string.Empty;
        if (windowsDrive.Length > 0)
        {
            AddIfCovered(windows, "Windows operating-system files");
            AddIfCovered(Path.Combine(windowsDrive, "Program Files"), "installed 64-bit applications");
            AddIfCovered(Path.Combine(windowsDrive, "Program Files (x86)"), "installed 32-bit applications");
            AddIfCovered(Path.Combine(windowsDrive, "ProgramData", "Package Cache"), "installer package cache");
        }

        foreach (string driveRoot in roots
            .Select(Path.GetPathRoot)
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => SafeNormalize(root!))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddIfCovered(Path.Combine(driveRoot, "$Recycle.Bin"), "deleted-file storage");
            AddIfCovered(Path.Combine(driveRoot, "System Volume Information"), "restore points and filesystem metadata");
            AddIfCovered(Path.Combine(driveRoot, "Recovery"), "Windows recovery files");
            AddIfCovered(Path.Combine(driveRoot, "PerfLogs"), "system performance logs");
        }

        return suggestions;
    }

    private static string SafeNormalize(string path)
        => IndexScopeIdentity.NormalizePath(path);

    private static string? TryGetParent(string normalizedPath)
        => Path.GetDirectoryName(normalizedPath);

    private static string LeafName(string normalizedPath)
        => Path.GetFileName(normalizedPath);

    private static bool IsBareDriveRoot(string s)
        => s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\';
}
