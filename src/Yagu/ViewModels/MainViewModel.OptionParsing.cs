using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// Pure parsing helpers shared by the search and persistence paths: splitting CSV and filter
/// pattern lists, extension-set parsing, the default-exclude check, and parallelism resolution.
/// </summary>
public sealed partial class MainViewModel
{
    public void SetDirectoryFromArgs(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!System.IO.Directory.Exists(dir))
        {
            ErrorText = $"--dir path does not exist or is not a directory: {dir}";
            return;
        }
        Directory = dir;
    }

    private static List<string> SplitCsv(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<string> SplitFilterPatterns(string s, FilterPatternMode mode) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : mode == FilterPatternMode.Regex
                ? [s.Trim()]
                : SplitCsv(s);

    private static bool IsDefaultExcludeGlobs(string value) =>
        string.Equals(value?.Trim(), AppSettings.DefaultExcludeGlobs, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ParseExtensionSet(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(e => e.TrimStart('.', '*')),
                StringComparer.OrdinalIgnoreCase);

    private HashSet<string> BuildEffectiveSkipExtensionSet()
    {
        var effective = ParseExtensionSet(SkipExtensions);
        // Binary extensions only suppress CONTENT searching (handled by SkipBinary's header sniff in
        // ContentSearcher). They must NOT be early-skipped from file listing in name-matching modes, or a
        // search like "dnGrep.exe" finds nothing even though the file is right there in the index. Fold
        // them into the skip set only for Content-only mode, where file names are never matched anyway.
        if ((SearchMode)SearchModeIndex == SearchMode.Content)
            foreach (var ext in ParseExtensionSet(BinaryExtensions))
                effective.Add(ext);
        return effective;
    }

    /// <summary>Parse a semicolon-separated extension string into a set WITH leading dots (e.g. ".zip", ".docx").</summary>
    private static HashSet<string> ParseDottedExtensionSet(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(e => e.TrimStart('.', '*'))
             .Select(e => "." + e),
            StringComparer.OrdinalIgnoreCase);
    }

    private static int ResolveParallelism(int index)
    {
        return SearchOptions.ResolveContentSearchParallelism(index, Environment.ProcessorCount);
    }
}
