using Yagu.Services;

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
    /// <summary>Search file contents only in files whose names match the query.</summary>
    FileNameThenContent = 3,
}

/// <summary>How include/exclude file filters are interpreted.</summary>
public enum FilterPatternMode
{
    /// <summary>Extensions, path segments, and glob wildcards.</summary>
    GlobPath = 0,
    /// <summary>A regular expression matched against the normalized full path.</summary>
    Regex = 1,
}

/// <summary>
/// Native multiline (cross-line) search backend selector (Phase 2, plan §5). Both engines scan the
/// identical LF-normalized buffer and produce identical results, so this is a pure performance knob
/// (only meaningful under <see cref="SearchOptions.Multiline"/> and when the native engine is loaded).
/// </summary>
public enum MultilineEngineKind
{
    /// <summary>Default: hand-rolled <c>regex::bytes</c> whole-buffer scan (spike-measured ~1.7× faster).</summary>
    Regex = 0,
    /// <summary>Alternate: ripgrep's vendored grep-searcher. Byte-identical results.</summary>
    Grep = 1,
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
    public bool ExactMatch { get; init; } = true;
    public int ContextLines { get; init; } = 3;
    public SearchMode SearchMode { get; init; } = SearchMode.Both;

    /// <summary>
    /// When true, the query regex runs over the whole file buffer so a single match can span
    /// line breaks (ripgrep <c>-U</c> / <c>--multiline</c>). Strictly opt-in: default false.
    /// Multiline reads whole files into memory, runs at a lower parallelism, and skips files
    /// larger than <see cref="MaxMultilineBytes"/>. Distinct from <c>RegexOptions.Multiline</c>
    /// anchor semantics — this flag makes matches cross physical lines.
    /// </summary>
    public bool Multiline { get; init; }

    /// <summary>
    /// When true and <see cref="Multiline"/> is on, <c>.</c> also matches newlines
    /// (ripgrep <c>--multiline-dotall</c> / inline <c>(?s)</c>). Only meaningful under multiline.
    /// </summary>
    public bool MultilineDotAll { get; init; }

    /// <summary>
    /// Dedicated size cap (in raw file bytes) for multiline search. Files larger than this are
    /// skipped and counted (never degraded to line mode). Default 50 MB. The same value and the
    /// same measure (raw file bytes) are consumed identically by the managed and native paths so
    /// both skip the exact same files.
    /// </summary>
    public long MaxMultilineBytes { get; init; } = DefaultMaxMultilineBytes;

    /// <summary>Default multiline size cap: 50 MB.</summary>
    public const long DefaultMaxMultilineBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Selects the native multiline backend (Phase 2): <see cref="MultilineEngineKind.Regex"/> (default,
    /// hand-rolled whole-buffer scan) or <see cref="MultilineEngineKind.Grep"/> (ripgrep's grep-searcher).
    /// Both produce identical results — a pure performance knob. Only meaningful under
    /// <see cref="Multiline"/> and when the native engine is available.
    /// </summary>
    public MultilineEngineKind MultilineEngine { get; init; } = MultilineEngineKind.Regex;

    /// <summary>Comma-separated extensions or globs (e.g. "ts,js" or "*.ts,*.js").</summary>
    public IReadOnlyList<string> IncludeGlobs { get; init; } = [];
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];
    public FilterPatternMode IncludeFilterMode { get; init; } = FilterPatternMode.GlobPath;
    public FilterPatternMode ExcludeFilterMode { get; init; } = FilterPatternMode.GlobPath;

    /// <summary>Files smaller than this are skipped. 0 disables the lower bound.</summary>
    public long MinFileSizeBytes { get; init; }

    /// <summary>Files larger than this are skipped. 0 disables the upper bound.</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>Files created before this date are skipped. Null disables the lower bound.</summary>
    public DateTimeOffset? CreatedAfterDate { get; init; }

    /// <summary>Files created after this date are skipped. Null disables the upper bound.</summary>
    public DateTimeOffset? CreatedBeforeDate { get; init; }

    /// <summary>Files modified before this date are skipped. Null disables the lower bound.</summary>
    public DateTimeOffset? ModifiedAfterDate { get; init; }

    /// <summary>Files modified after this date are skipped. Null disables the upper bound.</summary>
    public DateTimeOffset? ModifiedBeforeDate { get; init; }

    /// <summary>Stop streaming after this many matches. 0 disables.</summary>
    public int MaxResults { get; init; } = 50_000;

    /// <summary>Maximum matches per individual file before moving to the next. 0 disables.</summary>
    public int MaxMatchesPerFile { get; init; }

    /// <summary>Maximum matches emitted from a single line before the scanner moves to the next line.
    /// Bounds a pathological pattern (e.g. the regex <c>.</c>, which matches every character) on a very
    /// long minified line from emitting millions of matches. 0 disables (unlimited per line).</summary>
    public int MaxMatchesPerLine { get; init; }

    /// <summary>Absolute safety ceiling on total matches that applies EVEN WHEN <see cref="MaxResults"/>
    /// is 0 (unlimited). When &gt; 0, an unbounded content search (e.g. a match-everything regex over huge
    /// files) stops once reached and the result is marked truncated. Default 0 (disabled — no truncation):
    /// memory-pressure eviction (results paged to disk) and the per-line <see cref="MaxMatchesPerLine"/>
    /// cap still protect against runaway usage.</summary>
    public int AbsoluteMaxResults { get; init; }

    public bool SkipBinary { get; init; } = true;

    /// <summary>
    /// When true (the default), files and folders carrying the Windows Hidden
    /// attribute are included in the search. When false, hidden items are excluded:
    /// the managed file walker skips hidden entries (and does not recurse hidden
    /// folders), and the Everything backends append <c>!attrib:h</c> so hidden files
    /// are filtered natively. Pure-system files are unaffected by this flag (they are
    /// handled separately). The default preserves existing behavior — the Everything
    /// index already returns hidden files — so no extra per-file work is added.
    /// </summary>
    public bool SearchHiddenFiles { get; init; } = true;

    /// <summary>
    /// When true, the scanner may open cloud-only placeholder files (OneDrive
    /// Files On-Demand / Google Drive online-only files), hydrating them on
    /// demand — but only when a live sync provider is present to service the
    /// download. When false (the default), cloud-only files are skipped entirely
    /// so the scan can never block on a hydration that may never complete.
    /// See <see cref="Services.CloudFileHelper"/>.
    /// </summary>
    public bool SearchOnlineOnlyFiles { get; init; }

    /// <summary>Maximum directory depth to recurse into. 0 = unlimited.</summary>
    public int MaxSearchDepth { get; init; }

    /// <summary>When true, recursively read .gitignore files and exclude matching paths from listing.</summary>
    public bool ObeyGitignore { get; init; }

    /// <summary>When true, .gitignore exclusions override explicit include filters. When false, include filters take precedence.</summary>
    public bool GitignoreTakesPrecedence { get; init; } = true;

    /// <summary>Absolute ceiling for <see cref="MaxResults"/> regardless of user settings. Configurable via Settings.</summary>
    public static int MaxResultsCeiling { get; set; } = 50_000;

    /// <summary>
    /// Number of concurrent file scans. 0 = service-selected safe cap chosen by <see cref="Services.SearchService"/>.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    /// Optional per-search override of the global <see cref="Services.FileLister.Backend"/>. Used by
    /// the "search all drives" sweep to force the built-in managed walker (<see cref="FileListerBackend.Managed"/>)
    /// on drives Everything does not reliably auto-index (non-NTFS, removable, network), so their
    /// files are never silently missed. Null = use the global backend selection.
    /// </summary>
    public FileListerBackend? FileListerBackendOverride { get; init; }

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

    /// <summary>
    /// Resolves the dedicated multiline file-concurrency degree. Multiline holds whole files in
    /// memory (managed footprint ≈ 2× the UTF-16 blowup: original decoded string + LF shadow copy),
    /// so it MUST run at a much lower parallelism than the line path's up-to-64-way concurrency and
    /// independently of whether the native engine is available. The degree is memory-derived:
    /// available RAM ÷ (size cap × UTF-16 blowup × 2), clamped to a small range (default ~2–4).
    /// Pure function for testability.
    /// </summary>
    /// <param name="processorCount">Logical processor count (upper bound for the degree).</param>
    /// <param name="availableBytes">Available physical memory in bytes.</param>
    /// <param name="maxMultilineBytes">Per-file multiline size cap in raw bytes.</param>
    public static int ResolveMultilineParallelism(int processorCount, long availableBytes, long maxMultilineBytes)
    {
        int cores = Math.Max(1, processorCount);
        long cap = maxMultilineBytes > 0 ? maxMultilineBytes : 50 * 1024 * 1024;
        // UTF-16 blowup (≤2× file bytes) × 2 managed copies (original + LF shadow) ≈ 4× the cap.
        long perFileBudget = cap * 4;
        int memoryDerived = availableBytes > 0
            ? (int)Math.Max(1, availableBytes / Math.Max(1, perFileBudget))
            : 2;
        // Cap between 2 and 4 (and never exceed the core count), matching the plan's ~2–4 default.
        int degree = Math.Clamp(memoryDerived, 2, 4);
        return Math.Max(1, Math.Min(degree, cores));
    }

    /// <summary>
    /// Streaming-scanner I/O worker oversubscription mode. The native file-scan
    /// worker thread count is <see cref="MaxDegreeOfParallelism"/> multiplied by a
    /// factor selected here: 0 = Auto (SSD/NVMe → 1×, rotational HDD → 2×),
    /// 1 = 1×, 2 = 2×, 3 = 3×. Oversubscription overlaps per-file open/read latency
    /// on cold sweeps but burns extra CPU when data is already cached, so SSDs
    /// default to 1×.
    /// </summary>
    public int IoOversubscriptionIndex { get; init; }

    /// <summary>
    /// Resolves the streaming-scanner worker multiplier from the configured
    /// <paramref name="index"/> and whether the search target is a rotational hard
    /// disk. Pure function for testability.
    /// </summary>
    public static int ResolveIoOversubscriptionMultiplier(int index, bool isHardDisk)
    {
        return index switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            _ => isHardDisk ? 2 : 1, // Auto
        };
    }

    /// <summary>Hard process working-set cap in bytes. 0 = automatic sub-GB paging target.</summary>
    public long MaxProcessMemoryBytes { get; init; }

    /// <summary>System-wide memory pressure threshold (0-100). 0 = disabled.</summary>
    public int MemoryPressurePercent { get; init; } = 75;

    /// <summary>Set of file extensions (without dots, case-insensitive) to skip entirely — no binary check, no content read.</summary>
    public IReadOnlySet<string> SkipExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, detect supported archives by file header and search their contents recursively.</summary>
    public bool SearchInsideArchives { get; init; }

    /// <summary>Set of file extensions (with or without leading dots, case-insensitive) that should be routed to archive-aware scanning.</summary>
    public IReadOnlySet<string> ArchiveExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, raster image files are OCR'd on a background queue and their recognized
    /// text is matched against the query. The image extensions in <see cref="ImageOcrExtensions"/>
    /// are bypassed from the skip-extension prefilter so the scanner surfaces them for OCR.</summary>
    public bool SearchImageText { get; init; }

    /// <summary>Set of raster image extensions (without dots, case-insensitive) that are OCR'd when
    /// <see cref="SearchImageText"/> is on.</summary>
    public IReadOnlySet<string> ImageOcrExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>OCR engine id used when <see cref="SearchImageText"/> is on ("paddle" or "tesseract").</summary>
    public string ImageOcrEngine { get; init; } = "paddle";

    /// <summary>PaddleOCR model name used when <see cref="SearchImageText"/> is on and the engine is
    /// PaddleSharp (e.g. "EnglishV4"). Null/empty = the worker's default model. Ignored by Tesseract.</summary>
    public string? ImageOcrModel { get; init; }

    /// <summary>PaddleOCR detection resolution cap (longest image side, in pixels) when the engine is
    /// PaddleSharp. 0 = unlimited. Higher = better small-text accuracy, slower. Ignored by Tesseract.</summary>
    public int ImageOcrMaxSide { get; init; } = 960;

    /// <summary>Bounded channel buffer capacity for the Everything SDK streaming path.</summary>
    public int SdkChannelBufferSize { get; init; } = 4096;

    /// <summary>
    /// When true (default) and the current process is not elevated, file listing
    /// excludes well-known admin-protected paths (System Volume Information,
    /// $Recycle.Bin, Windows\System32\config, etc.) to avoid wasting time on
    /// guaranteed-access-denied trees.
    /// </summary>
    public bool ExcludeAdminProtectedPaths { get; init; } = true;

    /// <summary>
    /// Optional override list of admin-protected path segments. Each entry is a
    /// substring like <c>\Windows\System32\config</c>. When null/empty the
    /// built-in <see cref="Services.FileLister.DefaultAdminProtectedPathSegments"/>
    /// is used.
    /// </summary>
    public IReadOnlyList<string>? AdminProtectedPathSegments { get; init; }

    /// <summary>
    /// When set, the streaming scanner writes grep-style-formatted UTF-8 output
    /// directly to this stream, bypassing SearchResult allocation entirely.
    /// Used by CLI mode for maximum throughput.
    /// </summary>
    public Stream? DirectOutputStream { get; set; }

    /// <summary>Whether to emit ANSI color codes in direct output mode.</summary>
    public bool DirectOutputColor { get; set; }

    /// <summary>
    /// When set, the native streaming scanner can use degraded metadata-only results:
    /// the hot path sends source-backed stubs instead of materializing match-line strings.
    /// Non-native fallback paths may still use the store for pre-evicted payloads.
    /// Set by the ViewModel to its active ResultStore before starting the search.
    /// </summary>
    public ResultStore? DegradedResultStore { get; set; }
}
