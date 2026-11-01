using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services;

/// <summary>
/// Detects cloud "placeholder" files — OneDrive Files On-Demand, Google Drive,
/// Dropbox, and any other Windows Cloud Filter (<c>cldflt.sys</c>) provider —
/// whose content is not stored locally, and decides whether the scanner may
/// safely open them.
///
/// <para>Opening a dehydrated placeholder triggers a provider hydration
/// (download). If a connected provider services it, the read completes (slowly).
/// If no provider is connected — e.g. the sync app was uninstalled or signed out —
/// the read blocks <b>forever</b> in an uninterruptible kernel wait, wedging the
/// worker thread and making the whole process unkillable.</para>
///
/// <para>Reading the placeholder <b>attribute</b> (which arrives for free in the
/// directory enumeration find-data) never triggers hydration, so detection is
/// always safe. The liveness gate below is only consulted when the user opts in
/// to searching online-only files.</para>
/// </summary>
internal static class CloudFileHelper
{
    // FILE_ATTRIBUTE flags that mark a not-fully-local placeholder.
    private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
    private const int PlaceholderMask =
        FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;

    /// <summary>
    /// True when the file's data is not stored locally (a cloud-only placeholder).
    /// Pure attribute test — never contacts the cloud, never hydrates.
    /// </summary>
    public static bool IsCloudOnlyPlaceholder(FileAttributes attributes)
        => ((int)attributes & PlaceholderMask) != 0;

    /// <summary>
    /// Decides whether a placeholder file should be skipped by the scanner.
    /// <list type="bullet">
    /// <item>Not a placeholder → never skipped (returns false).</item>
    /// <item>Placeholder, opt-in disabled → always skipped.</item>
    /// <item>Placeholder, opt-in enabled → skipped only when no connected provider
    /// can hydrate it (orphaned), so the scan can never wedge.</item>
    /// </list>
    /// </summary>
    public static bool ShouldSkipPlaceholder(string path, FileAttributes attributes, bool searchOnlineOnlyFiles)
    {
        if (!IsCloudOnlyPlaceholder(attributes))
            return false;
        if (!searchOnlineOnlyFiles)
            return true;
        // Opted in: only open it if a live provider is present to service the
        // hydration; otherwise the read would block forever.
        return !IsProviderLive(path);
    }

    // ── Provider liveness via the Cloud Filter API, cached per directory ──
    // The provider status is a property of the sync root, identical for every
    // file under it, so caching by the file's parent directory amortizes the
    // syscall to one call per folder that actually contains placeholders.
    private static readonly ConcurrentDictionary<string, bool> s_providerLiveByDir =
        new(StringComparer.OrdinalIgnoreCase);

    private const int CF_SYNC_ROOT_INFO_PROVIDER = 2;

    // HRESULT_FROM_WIN32(ERROR_MORE_DATA). CF_SYNC_ROOT_PROVIDER_INFO is a fixed record of WCHAR
    // arrays whose exact size has grown between Windows releases (1028 bytes on Windows 11, i.e.
    // four bytes past the obvious 1 KB guess). Sizing the buffer by hand therefore fails closed on
    // every query, which silently reduced IsProviderLive to a constant false and made every
    // "search/index online-only files" opt-in a no-op. Ask, then retry with the length the API reports.
    private const int HresultMoreData = unchecked((int)0x800700EA);
    private const int SyncRootProviderInfoInitialBytes = 2048;

    // CF_PROVIDER_STATUS values (cfapi.h). "Live" = a connected provider that can
    // hydrate: IDLE plus the active populate/sync states. DISCONNECTED(0),
    // CONNECTIVITY_LOST(64) and the TERMINATE/ERROR high-bit sentinels are not live.
    private const uint CF_PROVIDER_STATUS_IDLE = 1;
    private const uint CF_PROVIDER_STATUS_SYNC_FULL = 32;

    /// <summary>
    /// True when a connected sync provider owns <paramref name="path"/> and can
    /// hydrate it on demand. Any failure or ambiguity returns false (safe default:
    /// treat as orphaned and skip). Result is cached per parent directory.
    /// </summary>
    public static bool IsProviderLive(string path)
        => IsProviderLive(path, CloudFilterApiAvailable, Path.GetDirectoryName);

    /// <remarks>
    /// Takes the platform gate and the directory resolver so both refusal arms are reachable: the
    /// Cloud Filter API does not exist before Windows 10 1709, and a path the platform refuses to
    /// parse must answer "not hydratable" rather than propagating out of a scan.
    /// </remarks>
    internal static bool IsProviderLive(
        string path, bool cloudFilterAvailable, Func<string, string?> getDirectoryName)
    {
        if (!cloudFilterAvailable)
            return false;

        string dir;
        try { dir = getDirectoryName(path) ?? path; }
        catch { return false; }

        return s_providerLiveByDir.GetOrAdd(dir, static d => QueryProviderLive(d));
    }

    private static bool CloudFilterApiAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);

    /// <summary>Clears cached liveness/sync-root state (call when a new search starts).</summary>
    public static void ResetProviderCache()
    {
        s_providerLiveByDir.Clear();
        s_cloudSyncDirectory.Clear();
        s_syncRootPrefixes = null;
    }

    // ── Sync-root membership for DIRECTORIES ──
    private static readonly ConcurrentDictionary<string, bool> s_cloudSyncDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="directory"/> is managed by a Windows Cloud Filter sync provider
    /// (OneDrive Files On-Demand, Google Drive, Dropbox, …).
    ///
    /// <para>Such a directory carries <see cref="FileAttributes.ReparsePoint"/> but is <b>not</b> a link:
    /// it has no link target, cannot form a cycle, and never leaves the volume. It also does <b>not</b>
    /// carry the <c>RECALL_ON_*</c> attributes that mark placeholder <em>files</em>, so it cannot be
    /// identified from attributes alone — only this API distinguishes it from a junction/symlink.</para>
    ///
    /// <para>Answering this question never hydrates anything; it inspects sync-root metadata only.</para>
    /// </summary>
    public static bool IsCloudSyncDirectory(string directory)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) || string.IsNullOrEmpty(directory))
            return false;

        return s_cloudSyncDirectory.GetOrAdd(directory, static d => TryQuerySyncRootProvider(d, out _));
    }

    // ── Sync-root prefix gate for paths discovered WITHOUT attributes (e.g. the
    // Everything backend yields bare path strings). We resolve the machine's cloud
    // sync-root directories once, then only stat files that actually live under one
    // — every other path costs a single prefix comparison. ──
    private static volatile string[]? s_syncRootPrefixes;
    private static string[] SyncRootPrefixes => s_syncRootPrefixes ??= LoadSyncRootPrefixes();

    /// <summary>
    /// For a path discovered without attributes, returns true when it is a
    /// cloud-only placeholder that should be skipped. Paths outside every cloud
    /// sync root cost only a prefix comparison (no syscall, no hydration).
    /// </summary>
    public static bool ShouldSkipDiscoveredPath(string path, bool searchOnlineOnlyFiles)
        => ShouldSkipDiscoveredPath(path, searchOnlineOnlyFiles, SyncRootPrefixes);

    /// <remarks>
    /// Takes the sync-root prefixes so the gate can be exercised against a synthetic root. The real
    /// list comes from the registry, so without this the "no cloud provider installed" arm is only
    /// reachable on a machine that happens to have none.
    /// </remarks>
    internal static bool ShouldSkipDiscoveredPath(string path, bool searchOnlineOnlyFiles, string[] prefixes)
    {
        if (prefixes.Length == 0)
            return false;

        bool underRoot = false;
        foreach (string prefix in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { underRoot = true; break; }
        }
        if (!underRoot)
            return false;

        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); }
        catch { return false; }
        return ShouldSkipPlaceholder(path, attrs, searchOnlineOnlyFiles);
    }

    private static string[] LoadSyncRootPrefixes()
        => LoadSyncRootPrefixes(OperatingSystem.IsWindows(), OpenSyncRootManagerKey);

    /// <remarks>
    /// Only ever invoked from the <c>isWindows</c> branch of <see cref="LoadSyncRootPrefixes(bool, Func{Microsoft.Win32.RegistryKey?})"/>,
    /// so it carries the platform annotation instead of a runtime guard — a guard here would be an
    /// arm that can never run on any platform the app actually ships to.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryKey? OpenSyncRootManagerKey()
        => Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");

    /// <remarks>
    /// Takes the platform gate and the registry accessor so the "no provider registered" and
    /// "registry unreadable" arms are reachable. Both must yield an empty list rather than throwing:
    /// this runs once per search, and a throw here would fail the whole scan over a cache warm-up.
    /// </remarks>
    internal static string[] LoadSyncRootPrefixes(
        bool isWindows, Func<Microsoft.Win32.RegistryKey?> openSyncRootManager)
    {
        if (!isWindows)
            return [];
        var prefixes = new List<string>();
        try
        {
            using var srm = openSyncRootManager();
            if (srm is not null)
            {
                foreach (string rootName in srm.GetSubKeyNames())
                {
                    using var userRoots = srm.OpenSubKey($@"{rootName}\UserSyncRoots");
                    if (userRoots is null) continue;
                    foreach (string valueName in userRoots.GetValueNames())
                    {
                        if (userRoots.GetValue(valueName) is string dir && dir.Length > 0)
                            prefixes.Add(dir.TrimEnd('\\') + "\\");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            YaguLog.For("CloudFileHelper").LogDebug(ex, "Could not enumerate cloud sync roots");
        }
        return prefixes.ToArray();
    }

    private static bool QueryProviderLive(string path)
        => TryQuerySyncRootProvider(path, out uint status)
           && status >= CF_PROVIDER_STATUS_IDLE
           && status <= CF_PROVIDER_STATUS_SYNC_FULL;

    /// <summary>
    /// Reads the Cloud Filter sync-root provider record for <paramref name="path"/>. Returns false when
    /// the path is not under any sync root, when cldapi.dll is unavailable, or when the query fails —
    /// all of which callers treat as "not hydratable". A too-small buffer is retried once at the exact
    /// size the API reports rather than being misread as "not a sync root".
    /// </summary>
    private static bool TryQuerySyncRootProvider(string path, out uint providerStatus)
        => TryQuerySyncRootProvider(path, out providerStatus, TryQuerySyncRootProviderOnce);

    /// <remarks>
    /// Takes the probe so the retry and both failure arms are reachable without a cloud provider.
    /// The buffer retry is the defect this guards: <c>CF_SYNC_ROOT_PROVIDER_INFO</c> is larger than an
    /// obvious round number, and treating <c>ERROR_MORE_DATA</c> as "not a sync root" silently reported
    /// every provider as dead — which disabled every cloud opt-in in both search and indexing.
    /// </remarks>
    internal static bool TryQuerySyncRootProvider(
        string path, out uint providerStatus, SyncRootProbe probe)
    {
        providerStatus = 0;

        try
        {
            if (probe(path, SyncRootProviderInfoInitialBytes, out providerStatus, out int hr, out int required))
                return true;
            if (hr != HresultMoreData || required <= SyncRootProviderInfoInitialBytes)
                return false;

            return probe(path, required, out providerStatus, out _, out _);
        }
        catch (DllNotFoundException)
        {
            return false; // cldapi.dll unavailable → treat as not hydratable
        }
        catch (Exception ex)
        {
            YaguLog.For("CloudFileHelper").LogDebug(ex, "Sync-root provider query failed for {Path}", path);
            return false;
        }
    }

    /// <summary>One attempt at reading the sync-root provider record into a buffer of a given size.</summary>
    internal delegate bool SyncRootProbe(
        string path, int bufferBytes, out uint providerStatus, out int hr, out int requiredBytes);

    private static bool TryQuerySyncRootProviderOnce(
        string path, int bufferBytes, out uint providerStatus, out int hr, out int requiredBytes)
    {
        providerStatus = 0;
        IntPtr buf = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            hr = CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_PROVIDER, buf, bufferBytes, out requiredBytes);
            if (hr != 0)
                return false;

            // CF_SYNC_ROOT_PROVIDER_INFO begins with CF_PROVIDER_STATUS ProviderStatus.
            providerStatus = unchecked((uint)Marshal.ReadInt32(buf));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int CfGetSyncRootInfoByPath(
        string filePath, int infoClass, IntPtr infoBuffer, int infoBufferLength, out int returnedLength);
}
