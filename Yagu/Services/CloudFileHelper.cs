using System.Collections.Concurrent;
using System.Runtime.InteropServices;

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
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            return false;

        string dir;
        try { dir = Path.GetDirectoryName(path) ?? path; }
        catch { return false; }

        return s_providerLiveByDir.GetOrAdd(dir, static d => QueryProviderLive(d));
    }

    /// <summary>Clears cached liveness/sync-root state (call when a new search starts).</summary>
    public static void ResetProviderCache()
    {
        s_providerLiveByDir.Clear();
        s_syncRootPrefixes = null;
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
    {
        string[] prefixes = SyncRootPrefixes;
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
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();
        var prefixes = new List<string>();
        try
        {
            using var srm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
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
            LogService.Instance.Verbose("CloudFileHelper", "Could not enumerate cloud sync roots", ex);
        }
        return prefixes.ToArray();
    }

    private static bool QueryProviderLive(string path)
    {
        const int bufLen = 1024;
        IntPtr buf = Marshal.AllocHGlobal(bufLen);
        try
        {
            int hr = CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_PROVIDER, buf, bufLen, out int _);
            if (hr != 0)
                return false; // not under a sync root, or the query failed → don't risk a read

            // CF_SYNC_ROOT_PROVIDER_INFO begins with CF_PROVIDER_STATUS ProviderStatus.
            uint status = unchecked((uint)Marshal.ReadInt32(buf));
            return status >= CF_PROVIDER_STATUS_IDLE && status <= CF_PROVIDER_STATUS_SYNC_FULL;
        }
        catch (DllNotFoundException)
        {
            return false; // cldapi.dll unavailable → treat as not hydratable
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("CloudFileHelper", $"Provider liveness query failed for {path}", ex);
            return false;
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
