using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Win32;
using Yagu.Native;
using Yagu.Services;

namespace Yagu.Services;

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
    private readonly Func<string, ProcessStartInfo, IProcess>? _processFactory;
    private int _skippedDirectories;
    private int _accessDeniedDirectories;
    private int _knownTotalFiles;
    private int _earlySkippedFiles;
    private int _earlySkippedTooLargeFiles;
    private int _earlyExcludedByExtensionFiles;
    public string? FallbackReason { get; private set; }
    public int SkippedDirectories => Volatile.Read(ref _skippedDirectories);
    public int AccessDeniedDirectories => Volatile.Read(ref _accessDeniedDirectories);
    public int KnownTotalFiles => Volatile.Read(ref _knownTotalFiles);
    public int EarlySkippedFiles => Volatile.Read(ref _earlySkippedFiles);
    public int EarlySkippedTooLargeFiles => Volatile.Read(ref _earlySkippedTooLargeFiles);
    public int EarlyExcludedByExtensionFiles => Volatile.Read(ref _earlyExcludedByExtensionFiles);

    /// <summary>Files smaller than this are skipped during listing. 0 disables.</summary>
    public long EarlyMinFileSizeBytes { get; set; }

    /// <summary>Files larger than this are skipped during listing. 0 disables.</summary>
    public long EarlyMaxFileSizeBytes { get; set; }

    /// <summary>Extensions (without dots, case-insensitive) to skip during listing (SDK path only).</summary>
    public IReadOnlySet<string> EarlySkipExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exclude glob patterns to push into the Everything SDK query (SDK path only).</summary>
    public IReadOnlyList<string> EarlyExcludeGlobs { get; set; } = [];

    /// <summary>
    /// When true and the current process is NOT elevated, skip directories that
    /// typically require administrator rights (e.g. <c>C:\Windows\System32\config</c>,
    /// <c>C:\System Volume Information</c>, <c>$Recycle.Bin</c>). Avoids burning
    /// time on access-denied trees during listing. Default true.
    /// </summary>
    public bool ExcludeAdminProtectedPaths { get; set; } = true;

    // Lazily evaluated once per process. Process elevation cannot change at runtime.

    internal static Func<bool>? ElevationOverride = null; // test seam
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

    internal bool ShouldExcludeAdminPaths => ExcludeAdminProtectedPaths && !s_isElevated.Value;

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
            if (path.IndexOf(seg + "\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
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

    public FileLister() { }

    internal FileLister(Func<string, ProcessStartInfo, IProcess>? processFactory) => _processFactory = processFactory;


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
        if (_processFactory is null && (backend == FileListerBackend.Auto || backend == FileListerBackend.EverythingSdk)) // skip SDK in test mode
        {
            IAsyncEnumerable<string>? sdkResults = null;
            try
            {
                sdkResults = RunEverythingSdkAsync(fullDir, includeExtensions, maxFiles, cancellationToken);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("FileLister", $"Everything SDK unavailable: {ex.Message}", ex);
            }

            if (sdkResults is not null)
            {
                await foreach (var p in sdkResults.WithCancellation(cancellationToken))
                {
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
                IAsyncEnumerable<string>? esResults = null;
                try
                {
                    esResults = RunEverythingAsync(esPath, fullDir, includeExtensions, maxFiles, cancellationToken);
                }
                catch (Exception ex)
                {
                    FallbackReason = $"es.exe failed: {ex.Message}";
                    LogService.Instance.Warning("FileLister", FallbackReason, ex);
                }

                if (esResults is not null)
                {
                    await foreach (var p in esResults.WithCancellation(cancellationToken))
                    {
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
                if (p.StartsWith("*.") && p[2..].All(c => char.IsLetterOrDigit(c) || c == '_'))
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
                lock (EverythingSdk.Lock)
                {
                    try
                    {
                        if (!EverythingSdk.IsDBLoaded())
                        {
                            error = "Everything database not loaded (is Everything running?)";
                            return;
                        }

                        EverythingSdk.Reset();
                        EverythingSdk.SetSearch(query);
                        EverythingSdk.SetMatchCase(false);
                        // Request size alongside paths so we can pre-filter by file size
                        // and extension without per-file FileInfo calls.
                        bool wantSize = EarlyMinFileSizeBytes > 0 || EarlyMaxFileSizeBytes > 0;
                        uint requestFlags = EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME;
                        if (wantSize)
                            requestFlags |= EverythingSdk.EVERYTHING_REQUEST_SIZE;
                        EverythingSdk.SetRequestFlags(requestFlags);
                        // Do NOT call SetSort: requesting an explicit sort (especially
                        // path-ascending) forces a slow non-indexed sort unless the user
                        // has enabled fast-sort in Everything. Letting the SDK return
                        // results in its native (unsorted) order is much faster.
                        if (maxFiles > 0)
                            EverythingSdk.SetMax((uint)maxFiles);
                        else
                            EverythingSdk.SetMax(0xFFFFFFFF);

                        if (!EverythingSdk.Query(bWait: true))
                        {
                            var err = EverythingSdk.GetLastError();
                            error = err == EverythingSdk.EVERYTHING_ERROR_IPC
                                ? "Everything is not running"
                                : $"Everything SDK query failed: {EverythingSdk.ErrorMessage(err)}";
                            return;
                        }

                        uint count = EverythingSdk.GetNumResults();
                        uint total = EverythingSdk.GetTotResults();
                        SetKnownTotalFiles(count);
                        LogService.Instance.Warning("FileLister", $"Everything SDK: {count} returned, {total} total matches, last error={EverythingSdk.GetLastError()}");
                        var buf = new System.Text.StringBuilder(1024);
                        long earlyMinSize = EarlyMinFileSizeBytes;
                        long earlyMaxSize = EarlyMaxFileSizeBytes;
                        var earlySkipExts = EarlySkipExtensions;
                        bool hasSkipExts = earlySkipExts.Count > 0;
                        int skippedTooSmall = 0;
                        int skippedTooLarge = 0;
                        int skippedBySize = 0;
                        int excludedByExtension = 0;

                        for (uint i = 0; i < count; i++)
                        {
                            if (cancellationToken.IsCancellationRequested) break;

                            // ── Early skip by file size ──
                            if (wantSize)
                            {
                                if (EverythingSdk.GetResultSize(i, out long fileSize)
                                    && IsOutsideEarlyFileSizeRange(fileSize, earlyMinSize, earlyMaxSize, out bool tooLarge))
                                {
                                    skippedBySize++;
                                    if (tooLarge)
                                        skippedTooLarge++;
                                    else
                                        skippedTooSmall++;
                                    Volatile.Write(ref _earlySkippedTooLargeFiles, skippedTooLarge);
                                    Volatile.Write(ref _earlySkippedFiles, skippedBySize);
                                    continue;
                                }
                            }

                            buf.Clear();
                            uint len = EverythingSdk.GetResultFullPathName(i, buf, (uint)buf.Capacity);
                            if (len == 0) continue;
                            if (len >= buf.Capacity)
                            {
                                buf.Capacity = (int)len + 1;
                                buf.Clear();
                                EverythingSdk.GetResultFullPathName(i, buf, (uint)buf.Capacity);
                            }

                            // ── Early skip by extension blocklist ──
                            if (hasSkipExts)
                            {
                                var path = buf.ToString();
                                var ext = System.IO.Path.GetExtension(path.AsSpan());
                                if (ext.Length > 1 && earlySkipExts.Contains(ext.Slice(1).ToString()))
                                {
                                    excludedByExtension++;
                                    Volatile.Write(ref _earlyExcludedByExtensionFiles, excludedByExtension);
                                    continue;
                                }
                                if (!channel.Writer.TryWrite(path))
                                {
                                    channel.Writer.WriteAsync(path, cancellationToken)
                                        .AsTask().GetAwaiter().GetResult();
                                }
                            }
                            else
                            {
                                // Write into the channel. TryWrite succeeds until the 4096
                                // buffer is full; if full, use the async path with a sync wait.
                                if (!channel.Writer.TryWrite(buf.ToString()))
                                {
                                    channel.Writer.WriteAsync(buf.ToString(), cancellationToken)
                                        .AsTask().GetAwaiter().GetResult();
                                }
                            }
                        }

                        if (skippedBySize > 0 || excludedByExtension > 0)
                        {
                            LogService.Instance.Warning("FileLister", $"Everything SDK: {skippedTooSmall:N0} too-small files skipped, {skippedTooLarge:N0} too-large files skipped, {excludedByExtension:N0} files excluded by extension");
                        }

                        EverythingSdk.Reset();
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
        await foreach (var path in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (error is not null) break;
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

        // 1. PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim('"'), "es.exe"));
            }
        }
        // 2. Registry — Everything writes its install location to Uninstall keys
        foreach (var installDir in GetEverythingInstallDirsFromRegistry())
        {
            candidates.Add(Path.Combine(installDir, "es.exe"));
        }
        // 3. LocalAppData
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "es.exe"));
        // 4. Program Files
        candidates.Add(@"C:\Program Files\Everything\es.exe");
        candidates.Add(@"C:\Program Files (x86)\Everything\es.exe");
        // 5. C:\tools
        candidates.Add(@"C:\tools\es.exe");

        return FindEsExe(candidates, File.Exists);
    }

    internal static List<string> GetEverythingInstallDirsFromRegistry()
    {
        var dirs = new List<string>();
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
                    if (key == null) continue;
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
                            if (!string.IsNullOrWhiteSpace(installLocation))
                                dirs.Add(installLocation.Trim('"'));
                        }
                        catch { /* skip individual key errors */ }
                    }
                }
                catch { /* skip if registry hive not accessible */ }
            }
        }

        return dirs;
    }

    internal static string? FindEsExe(IReadOnlyList<string> candidates, Func<string, bool> fileExists)
    {
        foreach (var c in candidates)
        {
            try { if (fileExists(c)) return c; } catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot check es.exe path: {c}", ex); }
        }
        return null;
    }


    internal static Task<EverythingReadinessResult> WaitForEverythingSdkReadyAsync(
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken) =>
        WaitForEverythingSdkReadyAsync(ProbeEverythingSdkReadiness, timeout, pollInterval, cancellationToken);

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


    internal static EverythingReadinessResult ProbeEverythingSdkReadiness()
    {
        if (!_sdkAvailable)
        {
            return EverythingReadinessResult.NotReady("Everything SDK not available");
        }

        lock (EverythingSdk.Lock)
        {
            try
            {
                if (!EverythingSdk.IsDBLoaded())
                {
                    return EverythingReadinessResult.NotReady("Everything database is still loading");
                }

                EverythingSdk.Reset();
                EverythingSdk.SetSearch(string.Empty);
                EverythingSdk.SetMatchPath(false);
                EverythingSdk.SetMatchCase(false);
                EverythingSdk.SetOffset(0);
                EverythingSdk.SetMax(25);
                EverythingSdk.SetRequestFlags(EverythingSdk.EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME);
                // Do NOT call SetSort — see note in the main query path; requesting an
                // explicit sort can be slow when fast-sort is not enabled in Everything.

                if (!EverythingSdk.Query(bWait: true))
                {
                    var err = EverythingSdk.GetLastError();
                    return EverythingReadinessResult.NotReady(err == EverythingSdk.EVERYTHING_ERROR_IPC
                        ? "Everything is not running"
                        : $"Everything SDK query failed: {EverythingSdk.ErrorMessage(err)}");
                }

                uint returnedCount = EverythingSdk.GetNumResults();
                uint totalCount = EverythingSdk.GetTotResults();
                if (returnedCount == 0)
                {
                    return EverythingReadinessResult.NotReady("Everything returned no files or folders yet");
                }

                var samples = new List<string>((int)Math.Min(returnedCount, 25));
                var buffer = new System.Text.StringBuilder(1024);
                for (uint i = 0; i < returnedCount && samples.Count < 25; i++)
                {
                    buffer.Clear();
                    uint length = EverythingSdk.GetResultFullPathName(i, buffer, (uint)buffer.Capacity);
                    if (length == 0) continue;
                    if (length >= buffer.Capacity)
                    {
                        buffer.Capacity = (int)length + 1;
                        buffer.Clear();
                        EverythingSdk.GetResultFullPathName(i, buffer, (uint)buffer.Capacity);
                    }

                    var path = buffer.ToString();
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
                try { EverythingSdk.Reset(); } catch { }
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
        if (maxFiles > 0) { args.Add("-n"); args.Add(maxFiles.ToString()); }

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
        while ((line = await proc.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
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

    internal static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        var s = ext.Trim();
        // Strip leading "*." or "*" or "."
        if (s.StartsWith("*.")) s = s[2..];
        else s = s.TrimStart('.', '*');
        return s;
    }

    private static IEnumerable<string> BuildEverythingSizeFilterTerms(long minBytes, long maxBytes)
    {
        minBytes = Math.Max(0, minBytes);
        maxBytes = Math.Max(0, maxBytes);

        if (minBytes > 0)
            yield return $"size:>={minBytes}";
        if (maxBytes > 0)
            yield return $"size:<={maxBytes}";
    }

    private static bool IsOutsideEarlyFileSizeRange(long fileSize, long minBytes, long maxBytes, out bool tooLarge)
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


    private async IAsyncEnumerable<string> EnumerateFallbackAsync(
        string directory,
        IReadOnlyList<string> includeExtensions,
        int maxFiles,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Pre-size visited to avoid repeated backing-array resize on large trees.
        var visited = new HashSet<string>(capacity: 4096, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(directory);

        bool excludeAdminPaths = ShouldExcludeAdminPaths;

        var extSet = includeExtensions is { Count: > 0 }
            ? new HashSet<string>(includeExtensions.Select(e => "." + NormalizeExtension(e)).Where(s => s.Length > 1), StringComparer.OrdinalIgnoreCase)
            : null;

        int yielded = 0;
        int dirCount = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            string canonical;
            try { canonical = Path.GetFullPath(current); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                LogService.Instance.Verbose("FileLister", $"Cannot resolve path: {current}", ex);
                continue;
            }
            if (!visited.Add(canonical)) continue;

            IEnumerator<FileSystemInfo> entries;
            try
            {
                // s_enumOpts has IgnoreInaccessible=true: access-denied entries are silently
                // skipped by the OS enumerator rather than thrown as exceptions.
                entries = new DirectoryInfo(canonical).EnumerateFileSystemInfos("*", s_enumOpts).GetEnumerator();
            }
            catch (UnauthorizedAccessException ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                Interlocked.Increment(ref _accessDeniedDirectories);
                LogService.Instance.Verbose("FileLister", $"Access denied: {canonical}", ex);
                continue;
            }
            catch (DirectoryNotFoundException ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                LogService.Instance.Verbose("FileLister", $"Dir not found: {canonical}", ex);
                continue;
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _skippedDirectories);
                LogService.Instance.Verbose("FileLister", $"IO error enumerating: {canonical}", ex);
                continue;
            }

            using (entries)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string entry;
                    FileSystemInfo fsi;
                    try
                    {
                        if (!entries.MoveNext()) break;
                        fsi = entries.Current;
                        entry = fsi.FullName;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // Rare with IgnoreInaccessible=true; kept as a last-resort guard.
                        Interlocked.Increment(ref _skippedDirectories);
                        Interlocked.Increment(ref _accessDeniedDirectories);
                        LogService.Instance.Verbose("FileLister", $"Access denied while enumerating: {canonical}", ex);
                        break;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        Interlocked.Increment(ref _skippedDirectories);
                        LogService.Instance.Verbose("FileLister", $"Dir not found while enumerating: {canonical}", ex);
                        break;
                    }
                    catch (IOException ex)
                    {
                        Interlocked.Increment(ref _skippedDirectories);
                        LogService.Instance.Verbose("FileLister", $"IO error while enumerating: {canonical}", ex);
                        break;
                    }

                    FileAttributes attrs;
                    try { attrs = fsi.Attributes; }
                    catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot get attrs: {entry}", ex); continue; }

                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        // Skip reparse points we've already followed.
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            try
                            {
                                var resolved = Path.GetFullPath(entry);
                                if (visited.Contains(resolved)) continue;
                            }
                            catch (Exception ex) { LogService.Instance.Verbose("FileLister", $"Cannot resolve reparse: {entry}", ex); continue; }
                        }
                        if (excludeAdminPaths && IsAdminProtectedPath(entry))
                        {
                            // Don't even attempt to recurse — these always fail with
                            // access-denied for non-elevated processes.
                            continue;
                        }
                        stack.Push(entry);
                    }
                    else
                    {
                        if (extSet is not null)
                        {
                            var ext = Path.GetExtension(entry);
                            if (ext.Length == 0 || !extSet.Contains(ext)) continue;
                        }
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


    internal sealed class RealProcess : IProcess
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
    }
}
