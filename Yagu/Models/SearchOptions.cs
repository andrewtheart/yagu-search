namespace Yagu.Models;

/// <summary>What to match the query against.</summary>
public enum SearchMode
{
    /// <summary>Search file contents and file names.</summary>
    Both = 0,
    /// <summary>Search file contents only.</summary>
    Content = 1,
    /// <summary>Search file names only.</summary>
    FileNames = 2,
}

/// <summary>
/// Configuration for a single search invocation.
/// </summary>
public sealed class SearchOptions
{
    public required string Directory { get; init; }
    public required string Query { get; init; }
    public bool CaseSensitive { get; init; }
    public bool UseRegex { get; init; }
    public int ContextLines { get; init; } = 3;
    public SearchMode SearchMode { get; init; } = SearchMode.Both;

    /// <summary>Comma-separated extensions or globs (e.g. "ts,js" or "*.ts,*.js").</summary>
    public IReadOnlyList<string> IncludeGlobs { get; init; } = [];
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];

    /// <summary>Files larger than this are skipped. 0 disables the limit.</summary>
    public long MaxFileSizeBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Stop streaming after this many matches. 0 disables.</summary>
    public int MaxResults { get; init; } = 50_000;

    /// <summary>Maximum matches per individual file before moving to the next. 0 disables.</summary>
    public int MaxMatchesPerFile { get; init; } = 0;

    public bool SkipBinary { get; init; } = true;

    /// <summary>Absolute ceiling for <see cref="MaxResults"/> regardless of user settings.</summary>
    public const int MaxResultsCeiling = 50_000;

    /// <summary>
    /// Number of concurrent file scans. 0 = auto safe cap chosen by <see cref="Services.SearchService"/>.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 0;

    public static int ResolveContentSearchParallelism(int index, int processorCount)
    {
        int cores = Math.Max(1, processorCount);
        return index switch
        {
            1 => 1,
            2 => Math.Max(1, cores / 2),
            3 => cores * 2,
            4 => cores,
            _ => 0,
        };
    }

    /// <summary>Hard process working-set cap in bytes. 0 = auto (50% of physical RAM, min 2 GB).</summary>
    public long MaxProcessMemoryBytes { get; init; }

    /// <summary>System-wide memory pressure threshold (0-100). 0 = disabled.</summary>
    public int MemoryPressurePercent { get; init; } = 80;

    /// <summary>Set of file extensions (without dots, case-insensitive) to skip entirely — no binary check, no content read.</summary>
    public IReadOnlySet<string> SkipExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, detect ZIP archives by file header and search their contents recursively (including nested zips).</summary>
    public bool SearchInsideArchives { get; init; } = true;

    /// <summary>Set of file extensions (with leading dots, case-insensitive) that are known ZIP-like containers. Used to bypass skip-extensions at the file-lister layer when archive search is enabled.</summary>
    public IReadOnlySet<string> ArchiveExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bounded channel buffer capacity for the Everything SDK streaming path.</summary>
    public int SdkChannelBufferSize { get; init; } = 4096;
}
