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
    /// Internal orchestration hint for an all-roots search. A priority prepass sets this so a per-root
    /// <see cref="SearchMode.Both"/> search stops after its Everything filename query and the immediate
    /// content scans of those name-hit files. The later full sweep runs separately. Never user-facing or
    /// persisted.
    /// </summary>
    internal bool StopAfterNameFirstPass { get; init; }

    /// <summary>Internal orchestration hint: skip the per-root name-first query because an all-roots prepass
    /// already ran it. Never user-facing or persisted.</summary>
    internal bool SuppressNameFirstPass { get; init; }

    /// <summary>Filename paths already emitted by the all-roots priority prepass. The full sweep suppresses
    /// duplicate filename-only rows for these paths.</summary>
    internal IReadOnlySet<string>? PreEmittedFileNamePaths { get; init; }

    /// <summary>Paths whose content was already scanned by the all-roots priority prepass. The full sweep
    /// counts but does not rescan them, preventing duplicate content matches.</summary>
    internal IReadOnlySet<string>? PreScannedContentPaths { get; init; }

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

    /// <summary>Root-level safety hint: use owned source reads instead of source-file memory maps. Set for
    /// removable/optical volumes, where unplugging during a mapped page fault can terminate the process.</summary>
    public bool AvoidSourceMemoryMap { get; init; }

    /// <summary>Maximum time for one file's managed I/O work. Native streaming scans enforce the same
    /// deadline cooperatively and report a timeout status. Default 30 seconds.</summary>
    public int FileIoTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// When true, this search may use the persistent content index (plan §5) to prune the ordinary
    /// raw-text candidate set before <see cref="Services.ContentSearcher"/> runs. The default is
    /// derived from settings (<c>AppSettings.UseContentIndexByDefault</c> gated by the master
    /// <c>AppSettings.EnableContentIndex</c>). This is a pure performance accelerator: when the index
    /// is disabled, missing, ineligible, or the query is unsupported, the pipeline is byte-for-byte the
    /// current live-scan path. It is <b>orthogonal</b> to the content-source toggles
    /// (<see cref="SearchImageText"/>/<see cref="SearchPdfText"/>/<see cref="SearchInsideArchives"/>) —
    /// it never enables or disables image/PDF/archive extraction. Session-only, never persisted, and
    /// never mutated by a semantic plan (plan §5).
    /// </summary>
    public bool UseContentIndex { get; init; }

    /// <summary>
    /// Optional factory that creates the content-index pruning gate for this search (plan §5). The GUI and
    /// CLI set it when <see cref="UseContentIndex"/> is on and the master feature is enabled; the search
    /// pipeline invokes it once, off the UI thread, at the start of discovery. A null factory or a null
    /// result means no pruning (the pipeline is byte-for-byte the live-scan path). Never persisted.
    /// </summary>
    public Func<Services.Index.ContentIndexSearchGate?>? ContentIndexGateFactory { get; set; }

    /// <summary>
    /// Optional factory for the Stage-3 <b>shadow</b> classification pipeline (plan §5.3). When set (only when
    /// <c>IndexUseWorkerQuerySessions</c> is on and a mapped worker session can serve the
    /// scope), the search offers every content-scan candidate path to it and completes it once discovery
    /// drains. It runs in shadow — it never prunes, so the result set is unchanged — to validate the worker
    /// pipeline and measure it before Stage 4 enables pruning. A null factory/result is a complete no-op
    /// (byte-for-byte the current path). Never persisted; invoked once, off the UI thread, at discovery start.
    /// </summary>
    public Func<Services.Index.IContentIndexShadowScan?>? ContentIndexShadowScanFactory { get; set; }

    /// <summary>
    /// Optional factory for the Stage-4 <b>pruning</b> pipeline (plan §5.3/§5.5). When set (only when the
    /// <c>IndexUseWorkerQuerySessions</c> setting is on and a mapped worker session can serve the
    /// scope), the search offers every content-scan candidate to it <b>instead of</b> the in-process gate; the
    /// pipeline forwards survivors to the provided content-scan sink and prunes proven-nonmembers, rescuing
    /// the dirty subset at B1. The factory is invoked once, off the UI thread, at discovery start with the
    /// survivor sink (the search's pending-file writer). A null factory/result means no worker pruning (the
    /// in-process gate or a live scan is used). Never persisted.
    /// </summary>
    public Func<Func<string, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>, Services.Index.IContentIndexPruningScan?>? ContentIndexPruningScanFactory { get; set; }

    /// <summary>
    /// Optional factory that creates the extended-source (archive / PDF-text / OCR) pruning gate for this
    /// search (plan §7 Phase 4). Set only when an extended-source namespace exists for the scope; the
    /// pipeline invokes it once, off the UI thread, at discovery start and consults it before enqueuing an
    /// image/PDF candidate to its extractor. A null factory or null result means every candidate is
    /// extracted exactly as today (fail-safe). Never persisted.
    /// </summary>
    public Func<Services.Index.ExtendedSourceSearchGate?>? ExtendedSourceGateFactory { get; set; }

    /// <summary>Optional test/host factory for the OCR engine used by this search. Null uses the configured
    /// production engine. Never persisted.</summary>
    internal Func<Services.Ocr.IOcrEngine>? ImageOcrEngineFactory { get; set; }

    /// <summary>Optional test/host factory for the PDF text extractor used by this search. Null uses the
    /// bundled production extractor. Never persisted.</summary>
    internal Func<Services.Pdf.PdfTextExtractor>? PdfTextExtractorFactory { get; set; }

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

    /// <summary>Effective number of independent OCR worker processes for this root. Per-root callers
    /// resolve the persisted automatic/explicit setting and HDD safeguard before constructing options.</summary>
    public int ImageOcrWorkerParallelism { get; init; } = 1;

    /// <summary>When true, PDF files are converted to text on a background queue (via the bundled Xpdf
    /// <c>pdftotext</c>) and their extracted text is matched against the query. The extensions in
    /// <see cref="PdfTextExtensions"/> are bypassed from the skip-extension prefilter so the scanner
    /// surfaces them for extraction. Analogous to <see cref="SearchImageText"/> for images.</summary>
    public bool SearchPdfText { get; init; }

    /// <summary>Set of document extensions (without dots, case-insensitive) that are converted to text
    /// when <see cref="SearchPdfText"/> is on.</summary>
    public IReadOnlySet<string> PdfTextExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Shared lock for all writers targeting <see cref="DirectOutputStream"/>. CLI filename
    /// events are managed while content matches come from native callbacks, so they must serialize
    /// complete output records rather than interleave byte fragments.</summary>
    internal object? DirectOutputLock { get; set; }

    /// <summary>
    /// When set, the native streaming scanner can use degraded metadata-only results:
    /// the hot path sends source-backed stubs instead of materializing match-line strings.
    /// Non-native fallback paths may still use the store for pre-evicted payloads.
    /// Set by the ViewModel to its active ResultStore before starting the search.
    /// </summary>
    public ResultStore? DegradedResultStore { get; set; }
}
