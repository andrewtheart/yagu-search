using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Win32;
using Yagu.Native;

namespace Yagu.Services;

/// <summary>Abstraction over the Everything SDK P/Invoke surface for testability.</summary>
internal interface IEverythingSdkOps
{
    object SyncLock { get; }
    bool IsDBLoaded();
    void Reset();
    void SetSearch(string searchString);
    void SetMatchCase(bool matchCase);
    void SetMatchPath(bool matchPath);
    void SetOffset(uint offset);
    void SetMax(uint max);
    void SetRequestFlags(uint flags);
    bool Query(bool bWait);
    uint GetLastError();
    string ErrorMessage(uint error);
    uint GetNumResults();
    uint GetTotResults();
    bool GetResultSize(uint index, out long size);
    bool GetResultDateCreated(uint index, out long fileTime);
    bool GetResultDateModified(uint index, out long fileTime);
    uint GetResultFullPathName(uint index, char[] buffer, uint capacity);
}

/// <summary>Real implementation delegating to the static <see cref="EverythingSdk"/> P/Invoke class.</summary>
[ExcludeFromCodeCoverage]
internal sealed class RealEverythingSdkOps : IEverythingSdkOps
{
    internal static readonly RealEverythingSdkOps Instance = new();
    public object SyncLock => EverythingSdk.Lock;
    public bool IsDBLoaded() => EverythingSdk.IsDBLoaded();
    public void Reset() => EverythingSdk.Reset();
    public void SetSearch(string searchString) => EverythingSdk.SetSearch(searchString);
    public void SetMatchCase(bool matchCase) => EverythingSdk.SetMatchCase(matchCase);
    public void SetMatchPath(bool matchPath) => EverythingSdk.SetMatchPath(matchPath);
    public void SetOffset(uint offset) => EverythingSdk.SetOffset(offset);
    public void SetMax(uint max) => EverythingSdk.SetMax(max);
    public void SetRequestFlags(uint flags) => EverythingSdk.SetRequestFlags(flags);
    public bool Query(bool bWait) => EverythingSdk.Query(bWait);
    public uint GetLastError() => EverythingSdk.GetLastError();
    public string ErrorMessage(uint error) => EverythingSdk.ErrorMessage(error);
    public uint GetNumResults() => EverythingSdk.GetNumResults();
    public uint GetTotResults() => EverythingSdk.GetTotResults();
    public bool GetResultSize(uint index, out long size) => EverythingSdk.GetResultSize(index, out size);
    public bool GetResultDateCreated(uint index, out long fileTime) => EverythingSdk.GetResultDateCreated(index, out fileTime);
    public bool GetResultDateModified(uint index, out long fileTime) => EverythingSdk.GetResultDateModified(index, out fileTime);
    public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
        => EverythingSdk.GetResultFullPathName(index, buffer, capacity);
}

public interface IFileLister
{
    /// <summary>
    /// Yields full paths of every file under <paramref name="directory"/>.
    /// Implementations should respect <paramref name="cancellationToken"/>.
    /// </summary>
    IAsyncEnumerable<string> ListFilesAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        CancellationToken cancellationToken);

    /// <summary>Reason fallback enumeration was used (null means es.exe was used).</summary>
    string? FallbackReason { get; }

    /// <summary>Directories that were discovered but could not be enumerated.</summary>
    int SkippedDirectories { get; }

    /// <summary>Skipped directories whose failure reason was access denied.</summary>
    int AccessDeniedDirectories { get; }

    /// <summary>Known total file count from the listing backend, or 0 when it cannot be known up front.</summary>
    int KnownTotalFiles { get; }

    /// <summary>Files skipped early during listing that should be included in skip counts.</summary>
    int EarlySkippedFiles { get; }

    /// <summary>Files skipped early during listing because they exceeded the configured size limit.</summary>
    int EarlySkippedTooLargeFiles { get; }

    /// <summary>Files excluded early by extension; these are deliberately not included in skip counts.</summary>
    int EarlyExcludedByExtensionFiles { get; }

    /// <summary>Files or directories skipped because they matched .gitignore rules.</summary>
    int GitignoreSkipped { get; }

    /// <summary>Cloud-only placeholder files skipped to avoid blocking on hydration.</summary>
    int CloudOnlySkippedFiles { get; }
}

/// <summary>Selects which file-listing backend to use.</summary>
public enum FileListerBackend
{
    /// <summary>SDK → es.exe → .NET fallback (default).</summary>
    Auto = 0,
    /// <summary>Force the in-process Everything SDK only.</summary>
    EverythingSdk = 1,
    /// <summary>Force es.exe (voidtools Everything CLI) only.</summary>
    EsExe = 2,
    /// <summary>Force the built-in .NET recursive enumeration (no Everything).</summary>
    Managed = 3,
}

internal sealed record EverythingReadinessResult(
    bool IsReady,
    uint ReturnedCount,
    uint TotalCount,
    IReadOnlyList<string> SamplePaths,
    string? Error)
{
    internal static EverythingReadinessResult Ready(uint returnedCount, uint totalCount, IReadOnlyList<string> samplePaths) =>
        new(true, returnedCount, totalCount, samplePaths, null);

    internal static EverythingReadinessResult NotReady(string error) =>
        new(false, 0, 0, Array.Empty<string>(), error);
}

/// <summary>
/// Lists files under a directory using <c>es.exe</c> (voidtools Everything) when available,
/// and falls back to .NET recursive enumeration with cycle protection otherwise.
/// </summary>
public sealed class FileLister : IFileLister
{
    /// <summary>
    /// Hard ceiling for content search: files larger than this are skipped to
    /// prevent the native scanner from blocking for minutes on huge files.
    /// Configurable via Settings (ContentSearchFileSizeMB). 0 = no ceiling.
    /// </summary>
    internal static long ContentSearchFileSizeCeiling { get; set; } = 100L * 1024 * 1024; // 100 MB

    private readonly Func<string, ProcessStartInfo, IProcess>? _processFactory;
    private readonly IEverythingSdkOps _sdkOps;
    private int _skippedDirectories;
    private int _accessDeniedDirectories;
    private int _knownTotalFiles;
    private int _earlySkippedFiles;
    private int _earlySkippedTooLargeFiles;
    private int _earlyExcludedByExtensionFiles;
    private int _cloudOnlySkippedFiles;
    public string? FallbackReason { get; private set; }
    public int SkippedDirectories => Volatile.Read(ref _skippedDirectories);
    public int AccessDeniedDirectories => Volatile.Read(ref _accessDeniedDirectories);
    public int KnownTotalFiles => Volatile.Read(ref _knownTotalFiles);
    public int EarlySkippedFiles => Volatile.Read(ref _earlySkippedFiles);
    public int EarlySkippedTooLargeFiles => Volatile.Read(ref _earlySkippedTooLargeFiles);
    public int EarlyExcludedByExtensionFiles => Volatile.Read(ref _earlyExcludedByExtensionFiles);
    public int CloudOnlySkippedFiles => Volatile.Read(ref _cloudOnlySkippedFiles);
    public int GitignoreSkipped => GitignoreMatcher?.Skipped ?? 0;

    /// <summary>Files smaller than this are skipped during listing. 0 disables.</summary>
    public long EarlyMinFileSizeBytes { get; set; }

    /// <summary>Files larger than this are skipped during listing. 0 disables.</summary>
    public long EarlyMaxFileSizeBytes { get; set; }

    /// <summary>Files created before this date are skipped during listing. Null disables.</summary>
    public DateTimeOffset? EarlyCreatedAfterDate { get; set; }

    /// <summary>Files created after this date are skipped during listing. Null disables.</summary>
    public DateTimeOffset? EarlyCreatedBeforeDate { get; set; }

    /// <summary>Files modified before this date are skipped during listing. Null disables.</summary>
    public DateTimeOffset? EarlyModifiedAfterDate { get; set; }

    /// <summary>Files modified after this date are skipped during listing. Null disables.</summary>
    public DateTimeOffset? EarlyModifiedBeforeDate { get; set; }

    /// <summary>Extensions (without dots, case-insensitive) to skip during listing (SDK path only).</summary>
    public IReadOnlySet<string> EarlySkipExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exclude glob patterns to push into the Everything SDK query (SDK path only).</summary>
    public IReadOnlyList<string> EarlyExcludeGlobs { get; set; } = [];

    /// <summary>Dynamic gitignore matcher — when set, directories and files are checked against
    /// <c>.gitignore</c> rules loaded lazily during the scan.</summary>
    internal DynamicGitignoreMatcher? GitignoreMatcher { get; set; }

    /// <summary>Literal file-name terms to apply during listing when the search mode first gates by file name.</summary>
    public IReadOnlyList<string> EarlyFileNameLiteralTerms { get; set; } = [];

    /// <summary>When false (the default), cloud-only placeholder files (OneDrive
    /// Files On-Demand / Google Drive online-only) are skipped during listing so
    /// the scan never blocks on a hydration that may never complete. When true,
    /// placeholders are listed and the scanner gates them by provider liveness.</summary>
    public bool SearchOnlineOnlyFiles { get; set; }

    /// <summary>
    /// When true and the current process is NOT elevated, skip directories that
    /// typically require administrator rights (e.g. <c>C:\Windows\System32\config</c>,
    /// <c>C:\System Volume Information</c>, <c>$Recycle.Bin</c>). Avoids burning
    /// time on access-denied trees during listing. Default true.
    /// </summary>
    public bool ExcludeAdminProtectedPaths { get; set; } = true;

    // Lazily evaluated once per process. Process elevation cannot change at runtime.

#pragma warning disable CS0649
    internal static Func<bool>? ElevationOverride; // test seam
#pragma warning restore CS0649
    internal static bool SdkAvailable { get => _sdkAvailable; set => _sdkAvailable = value; } // test seam
#pragma warning disable CS0649
    internal static Func<List<string>>? GetEverythingInstallDirsOverride; // test seam
#pragma warning restore CS0649
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal static bool CheckIsElevated()
    {
        if (ElevationOverride is not null) return ElevationOverride();
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return true; /* fail-open: assume elevated, do not exclude */ }
    }
    private static readonly Lazy<bool> s_isElevated = new(CheckIsElevated);

    /// <summary>
    /// Path-segment substrings (always wrapped in <c>\</c>) that are excluded when
    /// <see cref="ExcludeAdminProtectedPaths"/> is true and the process is not elevated.
    /// Format chosen so the same list works for both Everything (<c>!"\segment\"</c>)
    /// and the .NET fallback (substring match against the directory path).
    /// Override via <see cref="AdminProtectedPathSegmentsOverride"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAdminProtectedPathSegments =
    [
        @"\Windows\System32\config",
        @"\Windows\System32\LogFiles\WMI",
        @"\Windows\System32\Microsoft\Protect",
        @"\Windows\System32\sru",
        @"\Windows\CSC",
        @"\Windows\Installer",
        @"\Windows\ServiceProfiles",
        @"\Windows\security",
        @"\Windows\Minidump",
        @"\Windows\appcompat\Programs\Install",
        @"\Windows\PrintService",
        @"\Windows\WaaS",
        @"\Windows\ModemLogs",
        @"\System Volume Information",
        @"\$Recycle.Bin",
        @"\Recovery",
        @"\Config.Msi",
    ];

    /// <summary>
    /// User-configured admin-protected path segments. When null or empty,
    /// <see cref="DefaultAdminProtectedPathSegments"/> is used.
    /// </summary>
    public IReadOnlyList<string>? AdminProtectedPathSegmentsOverride { get; set; }

    /// <summary>Effective list of admin-protected path segments after considering the override.</summary>

    internal IReadOnlyList<string> EffectiveAdminProtectedPathSegments =>
        AdminProtectedPathSegmentsOverride is { Count: > 0 } o ? o : DefaultAdminProtectedPathSegments;

    /// <summary>Returns true if listing should skip well-known admin-protected paths.</summary>

    internal bool ShouldExcludeAdminPaths => ExcludeAdminProtectedPaths && !(ElevationOverride?.Invoke() ?? s_isElevated.Value);

    /// <summary>
    /// Returns true if <paramref name="path"/> matches one of the effective admin-protected segments.
    /// Used by the .NET fallback enumerator to avoid recursing into protected trees.
    /// </summary>

    internal bool IsAdminProtectedPath(string path)
    {
        foreach (var raw in EffectiveAdminProtectedPathSegments)
        {
            var seg = NormalizeAdminSegment(raw);
            if (seg is null) continue;
            // Match either the segment as-is (path ends with the segment, e.g. "C:\Windows\Installer")
            // or the segment followed by a separator (path ends with the segment dir).
            if (path.EndsWith(seg, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains(seg + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Normalises a user-entered admin-protected segment to the canonical
    /// <c>\Folder\Subfolder</c> form: ensures it starts with <c>\</c>, strips any
    /// trailing slash, and trims whitespace. Returns null for empty input.
    /// </summary>

    internal static string? NormalizeAdminSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace('/', '\\');
        if (!s.StartsWith('\\')) s = "\\" + s;
        s = s.TrimEnd('\\');
        return s.Length > 1 ? s : null;
    }

    /// <summary>
    /// Splits a user-entered string of admin-protected segments (one per line or
    /// semicolon-separated) into a list of normalised segments.
    /// </summary>
    public static List<string> ParseAdminProtectedSegments(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = NormalizeAdminSegment(part);
            if (n is not null && !list.Contains(n, StringComparer.OrdinalIgnoreCase))
                list.Add(n);
        }
        return list;
    }

    /// <summary>Bounded channel buffer capacity for the Everything SDK streaming path. Default 4096.</summary>
    public int SdkChannelBufferSize { get; set; } = 4096;

    /// <summary>Maximum directory depth to recurse. 0 = unlimited (default).</summary>
    public int MaxSearchDepth { get; set; }

    /// <summary>
    /// Forced backend selection. <c>Auto</c> tries SDK → es.exe → .NET in order.
    /// Other values restrict to a single backend.
    /// </summary>
    public static FileListerBackend Backend { get; set; } = FileListerBackend.Auto;

    // EnumerationOptions used by the managed fallback path. IgnoreInaccessible suppresses
    // UnauthorizedAccessException / IOException from the kernel enumerator, eliminating tens of
    // thousands of exception objects (and the GC pressure they cause) on system-wide scans.
    private static readonly EnumerationOptions s_enumOpts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public FileLister() : this(null, null) { }

    internal FileLister(Func<string, ProcessStartInfo, IProcess>? processFactory) : this(processFactory, null) { }

    internal FileLister(Func<string, ProcessStartInfo, IProcess>? processFactory, IEverythingSdkOps? sdkOps)
    {
        _processFactory = processFactory;
        _sdkOps = sdkOps ?? RealEverythingSdkOps.Instance;
    }


    public async IAsyncEnumerable<string> ListFilesAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FallbackReason = null;
        Volatile.Write(ref _skippedDirectories, 0);
        Volatile.Write(ref _accessDeniedDirectories, 0);
        Volatile.Write(ref _knownTotalFiles, 0);
        Volatile.Write(ref _earlySkippedFiles, 0);
        Volatile.Write(ref _earlySkippedTooLargeFiles, 0);
        Volatile.Write(ref _cloudOnlySkippedFiles, 0);
        Volatile.Write(ref _earlyExcludedByExtensionFiles, 0);
        if (string.IsNullOrWhiteSpace(directory)) yield break;

        // Normalize drive-letter-only paths ("C:" → "C:\"). On Windows,
        // Path.GetFullPath("C:") returns the *current directory of the C drive*
        // (e.g., "C:\Program Files\Yagu" when launched from there), not "C:\".
        // Always anchor a bare drive letter to its root.
        if (directory.Length == 2 && directory[1] == ':' && char.IsLetter(directory[0]))
            directory += "\\";

        var fullDir = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDir))
        {
            FallbackReason = "Directory does not exist";
            yield break;
        }

        var backend = Backend;

        // ── Tier 1: Everything SDK (in-process, fastest) ──
        bool sdkYielded = false;
        int maxDepth = MaxSearchDepth;
        int rootSeparators = maxDepth > 0 ? CountSeparators(fullDir) : 0;
        if ((_processFactory is null || _sdkOps is not RealEverythingSdkOps) && (backend == FileListerBackend.Auto || backend == FileListerBackend.EverythingSdk)) // skip SDK in test mode unless mock SDK injected
        {
            var sdkResults = TryCreateSdkEnumerable(fullDir, includeExtensions, maxFiles, cancellationToken);

            if (sdkResults is not null)
            {
                await foreach (var p in sdkResults.WithCancellation(cancellationToken))
                {
                    if (maxDepth > 0 && GetRelativeDepth(p, rootSeparators) > maxDepth)
                        continue;
                    sdkYielded = true;
                    yield return p;
                }
                if (sdkYielded) yield break;
            }
            if (backend == FileListerBackend.EverythingSdk)
            {
                if (FallbackReason is null) FallbackReason = "Everything SDK returned no results (forced backend)";
                yield break;
            }
        }

        // ── Tier 2: es.exe (process spawn) ──
        if (backend == FileListerBackend.Auto || backend == FileListerBackend.EsExe)
        {
            var esPath = FindEsExe();
            bool esYielded = false;
            if (esPath is not null)
            {
                IAsyncEnumerable<string>? esResults = TryCreateEsEnumerable(esPath, fullDir, includeExtensions, maxFiles, cancellationToken);

                if (esResults is not null)
                {
                    await foreach (var p in esResults.WithCancellation(cancellationToken))
                    {
                        if (maxDepth > 0 && GetRelativeDepth(p, rootSeparators) > maxDepth)
                            continue;
                        esYielded = true;
                        yield return p;
                    }
                    if (esYielded) yield break;
                    if (FallbackReason is null) FallbackReason = "es.exe returned no results";
                }
            }
            else
            {
                if (FallbackReason is null) FallbackReason = "es.exe not found";
            }
            if (backend == FileListerBackend.EsExe)
                yield break;
        }

        // ── Tier 3: .NET recursive enumeration ──
        await foreach (var p in EnumerateFallbackAsync(fullDir, includeExtensions, maxFiles, cancellationToken))
        {
            yield return p;
        }
    }

    // ── Everything SDK (in-process) ────────────────────────────────
    private static bool _sdkAvailable = true; // set to false on first load failure


    private async IAsyncEnumerable<string> RunEverythingSdkAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_sdkAvailable)
        {
            LogService.Instance.Warning("FileLister", "RunEverythingSdkAsync: _sdkAvailable=false, skipping SDK tier");
            FallbackReason = "Everything SDK not available";
            yield break;
        }

        // Build the Everything search string.
        // "file:" modifier ensures only file results (not folders).
        // Include the directory path with trailing '\' to limit to that folder tree.
        // Paths containing spaces must be double-quoted.
        var normalizedDir = directory.TrimEnd('\\') + "\\";
        var pathPart = normalizedDir.Contains(' ')
            ? $"\"{normalizedDir}\""
            : normalizedDir;

        // Always start with "file:" to exclude folder results
        var query = $"file: {pathPart}";

        if (includeExtensions is { Count: > 0 })
        {
            var exts = string.Join(';', includeExtensions
                .Select(NormalizeExtension)
                .Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(exts))
                query += $" ext:{exts}";
        }

        // Exclude extensions at query level so Everything never returns them.
        var skipExts = EarlySkipExtensions;
        if (skipExts.Count > 0)
        {
            var excludeExts = string.Join(';', skipExts);
            query += $" !ext:{excludeExts}";
        }
        foreach (var sizeTerm in BuildEverythingSizeFilterTerms(EarlyMinFileSizeBytes, EarlyMaxFileSizeBytes))
            query += $" {sizeTerm}";
        // NOTE: Do NOT add the ContentSearchFileSizeCeiling as a size: predicate here.
        // Everything's size: filter can make Query() block for 10+ seconds on large drives.
        // Instead, the ceiling is enforced in the per-result loop via GetResultSize().
        // Everything date predicates (dc:/dm:) can make Everything_Query block for
        // tens of seconds before returning any paths. Request date metadata instead
        // and filter results below, keeping first-result latency low without per-file stat calls.

        var fileNameFilter = BuildEverythingFileNameFilter(EarlyFileNameLiteralTerms);
        if (fileNameFilter is not null)
            query += $" {fileNameFilter}";

        // Translate exclude globs into Everything search syntax.
        // Extension globs ("*.log") → !ext:log   (already handled above for SkipExtensions)
        // Segment names ("node_modules", ".git") → !"\segment\"
        // Complex globs with wildcards/paths are left to the GlobMatcher post-filter.
        foreach (var raw in EarlyExcludeGlobs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = part;
                // "*.ext" → exclude extension
                if (p.StartsWith("*.", StringComparison.Ordinal) && p[2..].All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    query += $" !ext:{p[2..]}";
                }
                // Bare token with no wildcards/path separators → path segment exclusion
                else if (!p.Contains('*') && !p.Contains('?') && !p.Contains('/') && !p.Contains('\\'))
                {
                    // Everything path search: !"\segment\" matches the segment as a folder
                    // in any position in the path.
                    query += $" !\"\\{p}\\\"";
                }
                // Complex globs are left to the GlobMatcher post-filter.
            }
        }

        // Exclude well-known admin-protected paths when not elevated.
        if (ShouldExcludeAdminPaths)
        {
            foreach (var seg in EffectiveAdminProtectedPathSegments)
            {
                var s = NormalizeAdminSegment(seg);
                if (s is null) continue;
                // Trailing '\' anchors the segment as a folder boundary so a folder
                // named "Recovery" deep in a project tree is excluded too — that
                // matches user intent: skip protected trees.
                query += $" !\"{s}\\\"";
            }
        }

        // Always exclude .git from the SDK query (unconditional).
        query += " !\"\\.git\\\"";

        LogService.Instance.Warning("FileLister", $"Everything SDK query: {query}");

        // Stream results through a bounded channel instead of collecting all
        // into a List<string>. This avoids allocating a multi-million-entry
        // List (with LOH-sized backing arrays) that caused 1.4 GB LOH spikes
        // and triggered 97%-time-in-GC storms.
        int channelCapacity = Math.Max(16, SdkChannelBufferSize);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(channelCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        string? error = null;

        // Producer: runs the Everything SDK query and writes paths into the channel.
        _ = Task.Run(() =>
        {
            try
            {
                lock (_sdkOps.SyncLock)
                {
                    try
                    {
                        if (!_sdkOps.IsDBLoaded())
                        {
                            error = "Everything database not loaded (is Everything running?)";
                            return;
                        }

                        _sdkOps.Reset();
                        _sdkOps.SetSearch(query);
                        _sdkOps.SetMatchCase(false);
                        // Request size alongside paths so we can pre-filter by file size
                        // and extension without per-file FileInfo calls.
                        // Always request size: even without user size filters, the 100 MB
                        // ContentSearchFileSizeCeiling needs it to avoid per-file stat calls.
                        bool wantSize = true;
                        bool wantCreatedDate = EarlyCreatedAfterDate.HasValue || EarlyCreatedBeforeDate.HasValue;
                        bool wantModifiedDate = true; // Always fetch so FileGroup can display it without a per-file stat
                        bool filterByModifiedDate = EarlyModifiedAfterDate.HasValue || EarlyModifiedBeforeDate.HasValue;
                        uint requestFlags = EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME;
                        if (wantSize)
                            requestFlags |= EverythingSdk.EVERYTHING_REQUEST_SIZE;
                        if (wantCreatedDate)
                            requestFlags |= EverythingSdk.EVERYTHING_REQUEST_DATE_CREATED;
                        if (wantModifiedDate)
                            requestFlags |= EverythingSdk.EVERYTHING_REQUEST_DATE_MODIFIED;
                        _sdkOps.SetRequestFlags(requestFlags);
                        // Do NOT call SetSort: requesting an explicit sort (especially
                        // path-ascending) forces a slow non-indexed sort unless the user
                        // has enabled fast-sort in Everything. Letting the SDK return
                        // results in its native (unsorted) order is much faster.

                        // Page through results to avoid blocking 20+ seconds on large drives.
                        // Each page of 10,000 returns in ~1 second, immediately flowing
                        // paths to the content scanner.
                        const uint SdkPageSize = 10_000;
                        uint userMax = maxFiles > 0 ? (uint)maxFiles : uint.MaxValue;
                        uint pageSize = Math.Min(SdkPageSize, userMax);
                        uint offset = 0;
                        uint totalMatches = 0;

                        var buffer = new char[1024];
                        long earlyMinSize = EarlyMinFileSizeBytes;
                        long earlyMaxSize = EarlyMaxFileSizeBytes;
                        var earlySkipExts = EarlySkipExtensions;
                        bool hasSkipExts = earlySkipExts.Count > 0;
                        var extLookup = hasSkipExts && earlySkipExts is HashSet<string> hs
                            ? hs.GetAlternateLookup<ReadOnlySpan<char>>()
                            : default(HashSet<string>.AlternateLookup<ReadOnlySpan<char>>);
                        int skippedTooSmall = 0;
                        int skippedTooLarge = 0;
                        int skippedBySize = 0;
                        int skippedByDate = 0;
                        int excludedByExtension = 0;

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            _sdkOps.SetOffset(offset);
                            _sdkOps.SetMax(pageSize);

                            if (!_sdkOps.Query(bWait: true))
                            {
                                var err = _sdkOps.GetLastError();
                                error = err == EverythingSdk.EVERYTHING_ERROR_IPC
                                    ? "Everything is not running"
                                    : $"Everything SDK query failed: {_sdkOps.ErrorMessage(err)}";
                                return;
                            }

                            uint count = _sdkOps.GetNumResults();
                            if (offset == 0)
                            {
                                totalMatches = _sdkOps.GetTotResults();
                                SetKnownTotalFiles(totalMatches);
                                LogService.Instance.Warning("FileLister", $"Everything SDK: page0={count}, total={totalMatches}, last error={_sdkOps.GetLastError()}");
                            }

                            if (count == 0) break;

                        for (uint i = 0; i < count; i++)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            // ── Always read file size for caching + ceiling check ──
                            long sdkFileSize = -1;
                            if (wantSize)
                                _sdkOps.GetResultSize(i, out sdkFileSize);

                            // ── Early skip by file size (user-configured filter) ──
                            if (wantSize && sdkFileSize >= 0)
                            {
                                if (IsOutsideEarlyFileSizeRange(sdkFileSize, earlyMinSize, earlyMaxSize, out bool tooLarge))
                                {
                                    skippedBySize++;
                                    if (tooLarge)
                                        skippedTooLarge++;
                                    else
                                        skippedTooSmall++;
                                    Volatile.Write(ref _earlySkippedTooLargeFiles, skippedTooLarge);
                                    Volatile.Write(ref _earlySkippedFiles, skippedBySize + skippedByDate);
                                    continue;
                                }
                            }

                            bool needsFileInfoDateFallback = false;
                            if (wantCreatedDate)
                            {
                                if (_sdkOps.GetResultDateCreated(i, out long createdFileTime) && createdFileTime > 0)
                                {
                                    if (IsOutsideDateRange(DateTime.FromFileTime(createdFileTime), EarlyCreatedAfterDate, EarlyCreatedBeforeDate))
                                    {
                                        skippedByDate++;
                                        Volatile.Write(ref _earlySkippedFiles, skippedBySize + skippedByDate);
                                        continue;
                                    }
                                }
                                else
                                {
                                    needsFileInfoDateFallback = true;
                                }
                            }

                            DateTime modifiedDateFromSdk = default;
                            if (wantModifiedDate)
                            {
                                if (_sdkOps.GetResultDateModified(i, out long modifiedFileTime) && modifiedFileTime > 0)
                                {
                                    modifiedDateFromSdk = DateTime.FromFileTime(modifiedFileTime);
                                    if (filterByModifiedDate && IsOutsideDateRange(modifiedDateFromSdk, EarlyModifiedAfterDate, EarlyModifiedBeforeDate))
                                    {
                                        skippedByDate++;
                                        Volatile.Write(ref _earlySkippedFiles, skippedBySize + skippedByDate);
                                        continue;
                                    }
                                }
                                else
                                {
                                    if (filterByModifiedDate)
                                        needsFileInfoDateFallback = true;
                                }
                            }

                            string path = ReadSdkFullPath(_sdkOps, i, ref buffer);
                            if (path.Length == 0) continue;

                            // Cache size + modified date so downstream ShouldSkipByFileMetadata
                            // and FileGroup.LoadMetadata can use them without a per-file stat call.
                            if (sdkFileSize >= 0)
                                FileMetadataCache.Set(path, new FileMetadata(sdkFileSize, modifiedDateFromSdk, default));

                            if (needsFileInfoDateFallback)
                            {
                                try
                                {
                                    var fileInfo = new FileInfo(path);
                                    if (!fileInfo.Exists || IsOutsideEarlyDateRange(fileInfo.CreationTime, fileInfo.LastWriteTime,
                                            EarlyCreatedAfterDate, EarlyCreatedBeforeDate, EarlyModifiedAfterDate, EarlyModifiedBeforeDate))
                                    {
                                        skippedByDate++;
                                        Volatile.Write(ref _earlySkippedFiles, skippedBySize + skippedByDate);
                                        continue;
                                    }
                                }
                                catch
                                {
                                    skippedByDate++;
                                    Volatile.Write(ref _earlySkippedFiles, skippedBySize + skippedByDate);
                                    continue;
                                }
                            }

                            // ── Early skip by extension blocklist ──
                            if (hasSkipExts)
                            {
                                var ext = System.IO.Path.GetExtension(path.AsSpan());
                                if (ext.Length > 1 && extLookup.Contains(ext.Slice(1)))
                                {
                                    excludedByExtension++;
                                    Volatile.Write(ref _earlyExcludedByExtensionFiles, excludedByExtension);
                                    continue;
                                }
                                ChannelWrite(channel.Writer, path, cancellationToken);
                            }
                            else
                            {
                                // Write into the channel. TryWrite succeeds until the 4096
                                // buffer is full; if full, use the async path with a sync wait.
                                ChannelWrite(channel.Writer, path, cancellationToken);
                            }
                        }

                            // Advance to next page
                            offset += count;
                            if (count < pageSize || offset >= totalMatches || offset >= userMax)
                                break;
                        } // end while (paging loop)

                        if (skippedBySize > 0 || skippedByDate > 0 || excludedByExtension > 0)
                        {
                            LogService.Instance.Warning("FileLister", $"Everything SDK: {skippedTooSmall:N0} too-small files skipped, {skippedTooLarge:N0} too-large files skipped, {skippedByDate:N0} date-filtered files skipped, {excludedByExtension:N0} files excluded by extension");
                        }

                        _sdkOps.Reset();
                    }
                    catch (DllNotFoundException)
                    {
                        _sdkAvailable = false;
                        error = "Everything64.dll not found";
                    }
                    catch (Exception ex)
                    {
                        error = $"Everything SDK error: {ex.Message}";
                    }
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        if (error is not null)
        {
            FallbackReason = error;
            LogService.Instance.Warning("FileLister", error);
            yield break;
        }

        bool anyYielded = false;
        var matcher = GitignoreMatcher;
        await foreach (var path in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (error is not null) break;
            if (matcher is not null && matcher.ShouldSkipPath(path)) continue;
            if (CloudFileHelper.ShouldSkipDiscoveredPath(path, SearchOnlineOnlyFiles))
            {
                Interlocked.Increment(ref _cloudOnlySkippedFiles);
                continue;
            }
            anyYielded = true;
            yield return path;
        }

        if (error is not null)
        {
            FallbackReason = error;
            LogService.Instance.Warning("FileLister", error);
            yield break;
        }

        if (!anyYielded)
        {
            FallbackReason = "Everything SDK returned no results";
            yield break;
        }
    }


    public static string? FindEsExe()
    {
        var candidates = new List<string>();
        var log = LogService.Instance;

        log.Info("FileLister", "FindEsExe: beginning Everything detection");

        // 1. PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            log.Info("FileLister", $"FindEsExe: PATH has {pathDirs.Length} entries");
            foreach (var dir in pathDirs)
            {
                candidates.Add(Path.Combine(dir.Trim('"'), "es.exe"));
            }
        }
        else
        {
            log.Info("FileLister", "FindEsExe: PATH environment variable is empty/null");
        }

        // 2. Registry — Everything writes its install location to Uninstall keys
        var registryDirs = GetEverythingInstallDirsFromRegistry();
        log.Info("FileLister", $"FindEsExe: registry returned {registryDirs.Count} install dir(s): [{string.Join(", ", registryDirs)}]");
        foreach (var installDir in registryDirs)
        {
            candidates.Add(Path.Combine(installDir, "es.exe"));
        }

        // 3. LocalAppData
        var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "es.exe");
        candidates.Add(localAppData);
        log.Info("FileLister", $"FindEsExe: LocalAppData candidate: {localAppData}");

        // 4. Program Files
        candidates.Add(@"C:\Program Files\Everything\es.exe");
        candidates.Add(@"C:\Program Files (x86)\Everything\es.exe");
        // 5. C:\tools
        candidates.Add(@"C:\tools\es.exe");

        log.Info("FileLister", $"FindEsExe: total {candidates.Count} candidate paths to check");

        var result = FindEsExe(candidates, File.Exists);
        if (result != null)
            log.Info("FileLister", $"FindEsExe: FOUND es.exe at: {result}");
        else
            log.Warning("FileLister", $"FindEsExe: es.exe NOT FOUND in any of {candidates.Count} candidates. Non-PATH candidates checked: {localAppData}, C:\\Program Files\\Everything\\es.exe, C:\\Program Files (x86)\\Everything\\es.exe, C:\\tools\\es.exe");

        return result;
    }

    internal static List<string> GetEverythingInstallDirsFromRegistry()
    {
        if (GetEverythingInstallDirsOverride is not null) return GetEverythingInstallDirsOverride();
        return GetEverythingInstallDirsFromRegistryCore();
    }

    [ExcludeFromCodeCoverage]
    internal static List<string> GetEverythingInstallDirsFromRegistryCore()
    {
        var dirs = new List<string>();
        var log = LogService.Instance;
        string[] registryPaths =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        RegistryKey[] roots = [Registry.LocalMachine, Registry.CurrentUser];

        foreach (var root in roots)
        {
            foreach (var subPath in registryPaths)
            {
                try
                {
                    using var key = root.OpenSubKey(subPath);
                    if (key == null)
                    {
                        log.Info("FileLister", $"Registry: {root.Name}\\{subPath} — key not found");
                        continue;
                    }
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey == null) continue;
                            var displayName = subKey.GetValue("DisplayName") as string;
                            if (displayName == null || !displayName.Contains("Everything", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var installLocation = subKey.GetValue("InstallLocation") as string
                                ?? Path.GetDirectoryName(subKey.GetValue("UninstallString") as string ?? "");
                            log.Info("FileLister", $"Registry: found Everything entry '{displayName}' in {root.Name}\\{subPath}\\{subKeyName}, InstallLocation='{installLocation}'");
                            if (!string.IsNullOrWhiteSpace(installLocation))
                                dirs.Add(installLocation.Trim('"'));
                            else
                                log.Warning("FileLister", $"Registry: Everything entry '{displayName}' has no InstallLocation or UninstallString");
                        }
                        catch (Exception ex)
                        {
                            log.Verbose("FileLister", $"Registry: error reading subkey {subKeyName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Warning("FileLister", $"Registry: cannot open {root.Name}\\{subPath}: {ex.Message}");
                }
            }
        }

        log.Info("FileLister", $"Registry: total {dirs.Count} install dir(s) found");
        return dirs;
    }

    internal static string? FindEsExe(IReadOnlyList<string> candidates, Func<string, bool> fileExists)
    {
        foreach (var c in candidates)
        {
            try
            {
                if (fileExists(c))
                {
                    LogService.Instance.Info("FileLister", $"FindEsExe: candidate EXISTS: {c}");
                    return c;
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("FileLister", $"FindEsExe: exception checking candidate: {c} — {ex.Message}", ex);
            }
        }
        return null;
    }


    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal static Task<EverythingReadinessResult> WaitForEverythingSdkReadyAsync(
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken) =>
        WaitForEverythingSdkReadyAsync(() => ProbeEverythingSdkReadiness(RealEverythingSdkOps.Instance), timeout, pollInterval, cancellationToken);

    internal static async Task<EverythingReadinessResult> WaitForEverythingSdkReadyAsync(
        Func<EverythingReadinessResult> readinessProbe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
        if (pollInterval <= TimeSpan.Zero) pollInterval = TimeSpan.FromSeconds(1);

        var stopwatch = Stopwatch.StartNew();
        EverythingReadinessResult lastResult;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = readinessProbe();
            if (lastResult.IsReady)
            {
                return lastResult;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < pollInterval ? remaining : pollInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var status = string.IsNullOrWhiteSpace(lastResult.Error)
            ? "Timed out waiting for Everything Search to return indexed files or folders."
            : $"Timed out waiting for Everything Search to return indexed files or folders. Last status: {lastResult.Error}";
        return lastResult with { Error = status };
    }


    internal static EverythingReadinessResult ProbeEverythingSdkReadiness(IEverythingSdkOps sdk)
    {
        if (!_sdkAvailable)
        {
            return EverythingReadinessResult.NotReady("Everything SDK not available");
        }

        lock (sdk.SyncLock)
        {
            try
            {
                if (!sdk.IsDBLoaded())
                {
                    return EverythingReadinessResult.NotReady("Everything database is still loading");
                }

                sdk.Reset();
                sdk.SetSearch(string.Empty);
                sdk.SetMatchPath(false);
                sdk.SetMatchCase(false);
                sdk.SetOffset(0);
                sdk.SetMax(25);
                sdk.SetRequestFlags(EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);
                // Do NOT call SetSort — see note in the main query path; requesting an
                // explicit sort can be slow when fast-sort is not enabled in Everything.

                if (!sdk.Query(bWait: true))
                {
                    var err = sdk.GetLastError();
                    return EverythingReadinessResult.NotReady(err == EverythingSdk.EVERYTHING_ERROR_IPC
                        ? "Everything is not running"
                        : $"Everything SDK query failed: {sdk.ErrorMessage(err)}");
                }

                uint returnedCount = sdk.GetNumResults();
                uint totalCount = sdk.GetTotResults();
                if (returnedCount == 0)
                {
                    return EverythingReadinessResult.NotReady("Everything returned no files or folders yet");
                }

                var samples = new List<string>((int)Math.Min(returnedCount, 25));
                var buffer = new char[1024];
                for (uint i = 0; i < returnedCount && samples.Count < 25; i++)
                {
                    var path = ReadSdkFullPath(sdk, i, ref buffer);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        samples.Add(path);
                    }
                }

                if (samples.Count == 0)
                {
                    return EverythingReadinessResult.NotReady("Everything returned results but no file or folder paths yet");
                }

                return EverythingReadinessResult.Ready(returnedCount, totalCount, samples.ToArray());
            }
            catch (DllNotFoundException)
            {
                _sdkAvailable = false;
                return EverythingReadinessResult.NotReady("Everything64.dll not found");
            }
            catch (Exception ex)
            {
                return EverythingReadinessResult.NotReady($"Everything SDK readiness check failed: {ex.Message}");
            }
            finally
            {
                try { sdk.Reset(); } catch { }
            }
        }
    }


    private async IAsyncEnumerable<string> RunEverythingAsync(
        string esPath,
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // es.exe expects a path-prefix query. Wrap in quotes and ensure trailing slash for prefix match.
        var dirArg = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        var args = new List<string>
        {
            dirArg,
            "/a-d",       // exclude directories
        };
        if (includeExtensions is { Count: > 0 })
        {
            var exts = string.Join(';', includeExtensions.Select(NormalizeExtension).Where(s => !string.IsNullOrEmpty(s))!);
            if (!string.IsNullOrEmpty(exts)) args.Add($"ext:{exts}");
        }
        foreach (var sizeTerm in BuildEverythingSizeFilterTerms(EarlyMinFileSizeBytes, EarlyMaxFileSizeBytes))
            args.Add(sizeTerm);
        foreach (var dateTerm in BuildEverythingDateFilterTerms(EarlyCreatedAfterDate, EarlyCreatedBeforeDate, EarlyModifiedAfterDate, EarlyModifiedBeforeDate))
            args.Add(dateTerm);
        var fileNameFilter = BuildEverythingFileNameFilter(EarlyFileNameLiteralTerms);
        if (fileNameFilter is not null)
            args.Add(fileNameFilter);
        if (maxFiles > 0) { args.Add("-n"); args.Add(maxFiles.ToString(CultureInfo.InvariantCulture)); }

        int resultCount = await TryGetEverythingResultCountAsync(esPath, args, cancellationToken).ConfigureAwait(false);
        if (resultCount > 0) SetKnownTotalFiles(resultCount);

        var psi = new ProcessStartInfo
        {
            FileName = esPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        IProcess proc = _processFactory is not null ? _processFactory(esPath, psi) : new RealProcess(psi);
        using var procDisposer = proc as IDisposable;

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            FallbackReason = $"es.exe could not start: {ex.Message}";
            LogService.Instance.Warning("FileLister", FallbackReason, ex);
            yield break;
        }

        string? line;
        int yielded = 0;
        var matcher = GitignoreMatcher;
        while ((line = await proc.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (matcher is not null && matcher.ShouldSkipPath(line)) continue;
            if (CloudFileHelper.ShouldSkipDiscoveredPath(line, SearchOnlineOnlyFiles))
            {
                Interlocked.Increment(ref _cloudOnlySkippedFiles);
                continue;
            }
            yield return line;
            yielded++;
        }

        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (proc.ExitCode == 8)
        {
            FallbackReason = "Everything is not running";
            // Re-yield via fallback if we didn't get any results.
            if (yielded == 0) yield break;
        }
        else if (proc.ExitCode != 0 && yielded == 0)
        {
            FallbackReason = $"es.exe exited with code {proc.ExitCode}";
        }
    }


    private async Task<int> TryGetEverythingResultCountAsync(
        string esPath,
        IReadOnlyList<string> queryArgs,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = esPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-get-result-count");
        foreach (var argument in queryArgs) psi.ArgumentList.Add(argument);

        IProcess countProcess = _processFactory is not null ? _processFactory(esPath, psi) : new RealProcess(psi);
        using var countProcessDisposer = countProcess as IDisposable;
        try
        {
            countProcess.Start();
            string? line = await countProcess.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            await countProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (countProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(line)) return 0;
            return int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0
                ? count
                : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Instance.Verbose("FileLister", $"Could not get es.exe result count: {ex.Message}", ex);
            return 0;
        }
    }


    internal void SetKnownTotalFiles(uint count) =>
        SetKnownTotalFiles(count > int.MaxValue ? int.MaxValue : (int)count);

    private void SetKnownTotalFiles(int count) =>
        Volatile.Write(ref _knownTotalFiles, Math.Max(0, count));

    private static string ReadSdkFullPath(IEverythingSdkOps sdk, uint index, ref char[] buffer)
    {
        Array.Clear(buffer);
        uint length = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Length);
        if (length == 0) return string.Empty;

        if (length >= buffer.Length)
        {
            buffer = new char[(int)length + 1];
            Array.Clear(buffer);
            length = sdk.GetResultFullPathName(index, buffer, (uint)buffer.Length);
            if (length == 0) return string.Empty;
        }

        int charCount = (int)Math.Min(length, (uint)buffer.Length);
        if (charCount > 0 && buffer[charCount - 1] == '\0') charCount--;
        return new string(buffer, 0, charCount);
    }

    internal static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        var s = ext.Trim();
        // Strip leading "*." or "*" or "."
        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        else s = s.TrimStart('.', '*');
        return s;
    }

    /// <summary>Counts backslash separators in a path.</summary>
    private static int CountSeparators(string path)
    {
        int count = 0;
        foreach (var c in path)
            if (c == '\\') count++;
        return count;
    }

    /// <summary>
    /// Returns how many directory levels deep <paramref name="filePath"/> is
    /// relative to the root (whose separator count is <paramref name="rootSeparators"/>).
    /// A file directly inside the root returns 1.
    /// </summary>
    private static int GetRelativeDepth(string filePath, int rootSeparators)
    {
        // Total separators minus root separators = depth.
        // Example: root = "C:\src" (1 sep), file = "C:\src\a\b\file.txt" (4 seps) → depth 3
        // But we want directory depth, not counting the filename segment.
        // File in root dir: "C:\src\file.txt" → 2 seps → 2-1=1 depth = immediate child (depth 1 means 1 folder level)
        return CountSeparators(filePath) - rootSeparators;
    }

    internal static IEnumerable<string> BuildEverythingSizeFilterTerms(long minBytes, long maxBytes)
    {
        minBytes = Math.Max(0, minBytes);
        maxBytes = Math.Max(0, maxBytes);

        if (minBytes > 0)
            yield return $"size:>={minBytes}";
        if (maxBytes > 0)
            yield return $"size:<={maxBytes}";
    }

    internal static IEnumerable<string> BuildEverythingDateFilterTerms(
        DateTimeOffset? createdAfter,
        DateTimeOffset? createdBefore,
        DateTimeOffset? modifiedAfter,
        DateTimeOffset? modifiedBefore)
    {
        if (createdAfter.HasValue)
            yield return $"dc:>={FormatEverythingDate(createdAfter.Value)}";
        if (createdBefore.HasValue)
            yield return $"dc:<={FormatEverythingDate(createdBefore.Value)}";
        if (modifiedAfter.HasValue)
            yield return $"dm:>={FormatEverythingDate(modifiedAfter.Value)}";
        if (modifiedBefore.HasValue)
            yield return $"dm:<={FormatEverythingDate(modifiedBefore.Value)}";
    }

    internal static string FormatEverythingDate(DateTimeOffset date)
        => date.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string? BuildEverythingFileNameFilter(IReadOnlyList<string> literalTerms)
    {
        if (literalTerms.Count == 0)
            return null;

        var quotedTerms = new List<string>(literalTerms.Count);
        foreach (var term in literalTerms)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Contains('"'))
                return null;

            quotedTerms.Add($"\"{term}\"");
        }

        return quotedTerms.Count switch
        {
            1 => quotedTerms[0],
            _ => $"<{string.Join('|', quotedTerms)}>"
        };
    }

    internal static bool FileNameMatchesLiteralTerms(string path, IReadOnlyList<string> literalTerms)
    {
        if (literalTerms.Count == 0)
            return true;

        var fileName = Path.GetFileName(path);
        foreach (var term in literalTerms)
        {
            if (fileName.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool IsOutsideEarlyFileSizeRange(long fileSize, long minBytes, long maxBytes, out bool tooLarge)
    {
        minBytes = Math.Max(0, minBytes);
        maxBytes = Math.Max(0, maxBytes);
        tooLarge = false;

        if (minBytes > 0 && fileSize < minBytes)
            return true;
        if (maxBytes > 0 && fileSize > maxBytes)
        {
            tooLarge = true;
            return true;
        }

        return false;
    }

    internal static bool IsOutsideEarlyDateRange(
        DateTime created,
        DateTime modified,
        DateTimeOffset? createdAfter,
        DateTimeOffset? createdBefore,
        DateTimeOffset? modifiedAfter,
        DateTimeOffset? modifiedBefore)
    {
        return IsOutsideDateRange(created, createdAfter, createdBefore)
            || IsOutsideDateRange(modified, modifiedAfter, modifiedBefore);
    }

    internal static bool IsOutsideDateRange(DateTime value, DateTimeOffset? after, DateTimeOffset? before)
    {
        if (!after.HasValue && !before.HasValue)
            return false;
        if (value == default)
            return true;

        DateTime date = value.Date;
        if (after.HasValue && date < after.Value.LocalDateTime.Date)
            return true;
        if (before.HasValue && date > before.Value.LocalDateTime.Date)
            return true;

        return false;
    }


    private async IAsyncEnumerable<string> EnumerateFallbackAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Pre-size visited to avoid repeated backing-array resize on large trees.
        var visited = new HashSet<string>(capacity: 4096, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string path, int depth)>();
        stack.Push((directory, 0));

        bool excludeAdminPaths = ShouldExcludeAdminPaths;
        int maxDepth = MaxSearchDepth;

        var extSet = includeExtensions is { Count: > 0 }
            ? new HashSet<string>(includeExtensions.Select(e => "." + NormalizeExtension(e)).Where(s => s.Length > 1), StringComparer.OrdinalIgnoreCase)
            : null;

        int yielded = 0;
        int dirCount = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, currentDepth) = stack.Pop();

            var canonical = TryResolvePath(current);
            if (canonical is null) continue;
            if (!visited.Add(canonical)) continue;

            var entries = TryGetDirectoryEnumerator(canonical);
            if (entries is null) continue;

            using (entries)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fsiResult = TryMoveNextEntry(entries, canonical);
                    if (fsiResult is null) break;
                    var (fsi, entry) = fsiResult.Value;

                    var attrs = TryGetAttributes(fsi, entry);
                    if (attrs is null) continue;

                    if ((attrs.Value & FileAttributes.Directory) != 0)
                    {
                        // Skip reparse points we've already followed.
                        if ((attrs.Value & FileAttributes.ReparsePoint) != 0
                            && !TryResolveReparseTarget(entry, visited))
                        {
                            continue;
                        }
                        if (excludeAdminPaths && IsAdminProtectedPath(entry))
                        {
                            // Don't even attempt to recurse — these always fail with
                            // access-denied for non-elevated processes.
                            continue;
                        }
                        // Dynamic gitignore: check this directory against ancestor rules.
                        if (GitignoreMatcher is not null && GitignoreMatcher.ShouldSkipDirectory(entry))
                        {
                            continue;
                        }
                        if (maxDepth > 0 && currentDepth >= maxDepth)
                        {
                            continue;
                        }
                        stack.Push((entry, currentDepth + 1));
                    }
                    else
                    {
                        // Cloud-only placeholder guard: skip dehydrated OneDrive/Google
                        // Drive online-only files (the attribute is already in hand from
                        // enumeration — no extra syscall, no hydration) unless the user
                        // opted in and a live provider can hydrate them.
                        if (CloudFileHelper.ShouldSkipPlaceholder(entry, attrs.Value, SearchOnlineOnlyFiles))
                        {
                            Interlocked.Increment(ref _cloudOnlySkippedFiles);
                            continue;
                        }
                        // Dynamic gitignore: check file against ancestor extension rules.
                        if (GitignoreMatcher is not null && GitignoreMatcher.ShouldSkipFile(entry))
                        {
                            continue;
                        }
                        if (extSet is not null)
                        {
                            var ext = Path.GetExtension(entry);
                            if (ext.Length == 0 || !extSet.Contains(ext)) continue;
                        }
                        if (!FileNameMatchesLiteralTerms(entry, EarlyFileNameLiteralTerms))
                            continue;
                        // Cache file metadata from the enumeration data — avoids a
                        // second stat call in ContentSearcher.
                        if (fsi is FileInfo fileInfo)
                        {
                            FileMetadataCache.Set(entry, new FileMetadata(fileInfo.Length, fileInfo.LastWriteTime, fileInfo.CreationTime));
                            if (IsOutsideEarlyFileSizeRange(fileInfo.Length, EarlyMinFileSizeBytes, EarlyMaxFileSizeBytes, out bool tooLarge))
                            {
                                Interlocked.Increment(ref _earlySkippedFiles);
                                if (tooLarge)
                                    Interlocked.Increment(ref _earlySkippedTooLargeFiles);
                                continue;
                            }
                            if (IsOutsideEarlyDateRange(fileInfo.CreationTime, fileInfo.LastWriteTime, EarlyCreatedAfterDate, EarlyCreatedBeforeDate, EarlyModifiedAfterDate, EarlyModifiedBeforeDate))
                            {
                                Interlocked.Increment(ref _earlySkippedFiles);
                                continue;
                            }
                        }
                        yield return entry;
                        yielded++;
                        if (maxFiles > 0 && yielded >= maxFiles) yield break;
                    }
                }
            }
            // Yield to interleave with content workers, but only every 64 directories to avoid
            // flooding the threadpool with short-lived continuations on large scans.
            if (++dirCount % 64 == 0)
                await Task.Yield();
        }
    }

    // ---- Process abstraction so tests can substitute es.exe ----
    public interface IProcess
    {
        void Start();
        Task<string?> ReadLineAsync(CancellationToken cancellationToken);
        Task WaitForExitAsync(CancellationToken cancellationToken);
        int ExitCode { get; }
    }


    // ---- Excluded JIT/construction exception helpers ----

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private static void ChannelWrite(System.Threading.Channels.ChannelWriter<string> writer, string value, CancellationToken ct)
    {
        if (!writer.TryWrite(value))
            writer.WriteAsync(value, ct).AsTask().GetAwaiter().GetResult();
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private IAsyncEnumerable<string>? TryCreateSdkEnumerable(
        string fullDir, IReadOnlyList<string>? includeExtensions, int maxFiles, CancellationToken ct)
    {
        try { return RunEverythingSdkAsync(fullDir, includeExtensions ?? Array.Empty<string>(), maxFiles, ct); }
        catch (Exception ex)
        {
            LogService.Instance.Warning("FileLister", $"Everything SDK unavailable: {ex.Message}", ex);
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private IAsyncEnumerable<string>? TryCreateEsEnumerable(
        string esPath, string fullDir, IReadOnlyList<string>? includeExtensions, int maxFiles, CancellationToken ct)
    {
        try { return RunEverythingAsync(esPath, fullDir, includeExtensions ?? Array.Empty<string>(), maxFiles, ct); }
        catch (Exception ex)
        {
            FallbackReason = $"es.exe failed: {ex.Message}";
            LogService.Instance.Warning("FileLister", FallbackReason, ex);
            return null;
        }
    }

    // ---- Excluded OS exception helpers (untestable without filesystem injection) ----

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private static FileAttributes? TryGetAttributes(FileSystemInfo fsi, string entry)
    {
        try { return fsi.Attributes; }
        catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot get attrs: {entry}", ex); return null; }
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private string? TryResolvePath(string current)
    {
        try { return Path.GetFullPath(current); }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _skippedDirectories);
            LogService.Instance.Verbose("FileLister", $"Cannot resolve path: {current}", ex);
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private IEnumerator<FileSystemInfo>? TryGetDirectoryEnumerator(string canonical)
    {
        try
        {
            // s_enumOpts has IgnoreInaccessible=true: access-denied entries are silently
            // skipped by the OS enumerator rather than thrown as exceptions.
            return new DirectoryInfo(canonical).EnumerateFileSystemInfos("*", s_enumOpts).GetEnumerator();
        }
        catch (UnauthorizedAccessException ex)
        {
            Interlocked.Increment(ref _skippedDirectories);
            Interlocked.Increment(ref _accessDeniedDirectories);
            LogService.Instance.Verbose("FileLister", $"Access denied: {canonical}", ex);
            return null;
        }
        catch (DirectoryNotFoundException ex)
        {
            Interlocked.Increment(ref _skippedDirectories);
            LogService.Instance.Verbose("FileLister", $"Dir not found: {canonical}", ex);
            return null;
        }
        catch (IOException ex)
        {
            Interlocked.Increment(ref _skippedDirectories);
            LogService.Instance.Verbose("FileLister", $"IO error enumerating: {canonical}", ex);
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private (FileSystemInfo fsi, string entry)? TryMoveNextEntry(IEnumerator<FileSystemInfo> entries, string canonical)
    {
        try
        {
            if (!entries.MoveNext()) return null;
            var fsi = entries.Current;
            return (fsi, fsi.FullName);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Rare with IgnoreInaccessible=true; kept as a last-resort guard.
            Interlocked.Increment(ref _skippedDirectories);
            Interlocked.Increment(ref _accessDeniedDirectories);
            LogService.Instance.Verbose("FileLister", $"Access denied while enumerating: {canonical}", ex);
            return null;
        }
        catch (DirectoryNotFoundException ex)
        {
            Interlocked.Increment(ref _skippedDirectories);
            LogService.Instance.Verbose("FileLister", $"Dir not found while enumerating: {canonical}", ex);
            return null;
        }
        catch (IOException ex)
        {
            Interlocked.Increment(ref _skippedDirectories);
            LogService.Instance.Verbose("FileLister", $"IO error while enumerating: {canonical}", ex);
            return null;
        }
    }

    /// <summary>
    /// Returns true if the reparse point at <paramref name="entry"/> resolves to
    /// a target not yet in <paramref name="visited"/>; false if already visited
    /// or resolution fails.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private static bool TryResolveReparseTarget(string entry, HashSet<string> visited)
    {
        try
        {
            var resolved = Path.GetFullPath(entry);
            return !visited.Contains(resolved);
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("FileLister", $"Cannot resolve reparse: {entry}", ex);
            return false;
        }
    }


    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class RealProcess : IProcess, IDisposable
    {
        private readonly Process _p;
        public RealProcess(ProcessStartInfo psi)
        {
            _p = new Process { StartInfo = psi };
        }
        public int ExitCode => _p.ExitCode;
        public void Start() => _p.Start();
        public Task<string?> ReadLineAsync(CancellationToken ct) => _p.StandardOutput.ReadLineAsync(ct).AsTask();
        public Task WaitForExitAsync(CancellationToken ct) => _p.WaitForExitAsync(ct);
        public void Dispose() => _p.Dispose();
    }
}
