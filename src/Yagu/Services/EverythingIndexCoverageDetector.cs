namespace Yagu.Services;

/// <summary>
/// Reads voidtools Everything's active INI and determines whether a search path is covered by an enabled
/// NTFS/ReFS/FAT/remote volume index or a recursive folder index. Everything.db is a proprietary index,
/// not SQLite; Everything.ini is the supported, human-readable source for these settings. Detection is
/// read-only — the INI explicitly says Everything must be stopped before editing it, and the official CLI
/// can rescan/reindex existing roots but has no safe live "add this root" option.
/// </summary>
internal static class EverythingIndexCoverageDetector
{
    internal static Func<string, bool> FileExists { get; set; } = File.Exists;
    internal static Func<string, string> ReadAllText { get; set; } = File.ReadAllText;
    internal static Func<string, IEnumerable<string>> ReadLines { get; set; } = File.ReadLines;

    internal sealed record IndexedRoot(string Path, bool Recursive);

    internal sealed class Configuration(IReadOnlyList<IndexedRoot> indexedRoots)
    {
        internal IReadOnlyList<IndexedRoot> IndexedRoots { get; } = indexedRoots;

        internal bool Covers(string path)
        {
            string? target = NormalizePath(path);
            if (target is null)
                return false;

            foreach (IndexedRoot indexed in IndexedRoots)
            {
                string? root = NormalizePath(indexed.Path);
                if (root is null)
                    continue;
                if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                    return indexed.Recursive;
                if (indexed.Recursive && IsDescendantOf(target, root))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reads the active Everything configuration for <paramref name="everythingExePath"/> and returns
    /// searched paths that are not covered. Returns <c>null</c> when the active config cannot be located or
    /// parsed, allowing the caller to fail open (no warning rather than a false warning).
    /// </summary>
    internal static IReadOnlyList<string>? FindUncoveredPaths(
        IReadOnlyList<string> searchPaths,
        string everythingExePath,
        string? roamingAppData = null)
    {
        string? configPath = FindActiveConfigPath(everythingExePath, roamingAppData);
        if (configPath is null)
            return null;
        try
        {
            Configuration config = Parse(ReadAllText(configPath));
            return searchPaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && !config.Covers(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns only roots whose absence can be stated confidently. Everything keeps its current database
    /// and settings in memory and saves them on exit; while it is running, a root absent from the on-disk
    /// INI may have been added in the UI after startup (and a live zero-result query may simply mean its
    /// FAT/folder scan is still pending). Therefore an INI-negative result is <em>unknown</em>, not uncovered,
    /// while the process is running. Positive INI coverage remains trustworthy. Once Everything exits, the
    /// saved INI is authoritative and negative roots can be warned about.
    /// </summary>
    internal static IReadOnlyList<string>? FindConfirmedUncoveredPaths(
        IReadOnlyList<string> searchPaths,
        string everythingExePath,
        bool everythingRunning,
        string? roamingAppData = null)
    {
        IReadOnlyList<string>? uncovered = FindUncoveredPaths(searchPaths, everythingExePath, roamingAppData);
        if (uncovered is null || uncovered.Count == 0)
            return uncovered;
        return everythingRunning ? Array.Empty<string>() : uncovered;
    }

    /// <summary>
    /// Resolves the active INI. Installed builds commonly keep a small beside-exe stub with
    /// <c>app_data=1</c>, which redirects to <c>%APPDATA%\Everything\Everything.ini</c>. Portable builds
    /// use the beside-exe INI directly.
    /// </summary>
    internal static string? FindActiveConfigPath(string everythingExePath, string? roamingAppData = null)
    {
        if (string.IsNullOrWhiteSpace(everythingExePath))
            return null;
        string? exeDir = Path.GetDirectoryName(everythingExePath);
        if (string.IsNullOrWhiteSpace(exeDir))
            return null;

        string besideExe = Path.Combine(exeDir, "Everything.ini");
        bool appDataMode = false;
        if (FileExists(besideExe))
        {
            try
            {
                appDataMode = ReadIniValue(ReadLines(besideExe), "app_data") == "1";
            }
            catch { /* use candidate fallback below */ }
        }

        string appData = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string roaming = Path.Combine(appData, "Everything", "Everything.ini");
        if (appDataMode)
            return FileExists(roaming) ? roaming : null;
        if (FileExists(besideExe))
            return besideExe;
        return FileExists(roaming) ? roaming : null;
    }

    internal static Configuration Parse(string iniText)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inEverythingSection = false;
        using var reader = new StringReader(iniText ?? string.Empty);
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inEverythingSection = line.Equals("[Everything]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEverythingSection)
                continue;
            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        var roots = new List<IndexedRoot>();
        foreach ((string key, string rawPaths) in values)
        {
            if (!key.EndsWith("_volume_paths", StringComparison.OrdinalIgnoreCase))
                continue;

            string prefix = key[..^"paths".Length]; // e.g. "ntfs_volume_"
            List<string> paths = SplitCsv(rawPaths);
            List<string> includes = GetCsv(values, prefix + "includes");
            List<string> configuredRoots = GetCsv(values, prefix + "roots");
            List<string> includeOnly = GetCsv(values, prefix + "include_onlys");
            for (int i = 0; i < paths.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(paths[i]) || !GetBool(includes, i, defaultValue: true))
                    continue;
                string basePath = GetAt(configuredRoots, i);
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = paths[i];

                string only = GetAt(includeOnly, i);
                if (string.IsNullOrWhiteSpace(only))
                {
                    roots.Add(new IndexedRoot(basePath, Recursive: true));
                }
                else
                {
                    // Everything stores multiple include-only subpaths inside one aligned field. Accept
                    // the delimiters used by current/legacy builds; an unknown value fails conservative.
                    foreach (string child in only.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string combined = Path.IsPathFullyQualified(child) ? child : Path.Combine(basePath, child);
                        roots.Add(new IndexedRoot(combined, Recursive: true));
                    }
                }
            }
        }

        List<string> folders = GetCsv(values, "folders");
        List<string> folderSubfolders = GetCsv(values, "folder_subfolders");
        for (int i = 0; i < folders.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(folders[i]))
                roots.Add(new IndexedRoot(folders[i], GetBool(folderSubfolders, i, defaultValue: true)));
        }

        return new Configuration(roots);
    }

    private static string? ReadIniValue(IEnumerable<string> lines, string wantedKey)
    {
        bool inEverythingSection = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inEverythingSection = line.Equals("[Everything]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEverythingSection || line.Length == 0 || line[0] is ';' or '#')
                continue;
            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals(wantedKey, StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..].Trim();
        }
        return null;
    }

    private static List<string> GetCsv(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? value) ? SplitCsv(value) : [];

    /// <summary>CSV splitter that preserves empty aligned fields and supports quoted paths containing commas.</summary>
    internal static List<string> SplitCsv(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < (value?.Length ?? 0); i++)
        {
            char c = value![i];
            if (c == '"')
            {
                if (quoted && i + 1 < value.Length && value[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(c);
        }
        result.Add(current.ToString().Trim());
        return result;
    }

    private static bool GetBool(IReadOnlyList<string> values, int index, bool defaultValue)
        => index >= values.Count || string.IsNullOrWhiteSpace(values[index])
            ? defaultValue
            : values[index] is "1" or "true" or "yes";

    private static string GetAt(IReadOnlyList<string> values, int index)
        => index < values.Count ? values[index] : string.Empty;

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            string value = path.Trim().Replace('/', '\\');
            if (value.Length == 2 && value[1] == ':' && char.IsLetter(value[0]))
                value += "\\";
            value = Path.GetFullPath(value);
            string root = Path.GetPathRoot(value)!;
            return value.Length > root.Length ? value.TrimEnd('\\') : value;
        }
        catch { return null; }
    }

    private static bool IsDescendantOf(string path, string root)
    {
        string prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
