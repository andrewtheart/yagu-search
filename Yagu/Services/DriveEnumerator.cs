using System.IO;

namespace Yagu.Services;

/// <summary>
/// Immutable description of one logical drive, used to decide whether it should be included in an
/// "all drives" search. Kept as pure data (no live hardware access) so the selection logic in
/// <see cref="DriveEnumerator.SelectRoots"/> can be unit-tested deterministically.
/// </summary>
public sealed record DriveDescriptor(
    string Root,
    DriveType Type,
    bool IsReady,
    bool IsCloud);

/// <summary>
/// Enumerates and classifies the machine's drives for the "search all drives" feature (used when the
/// user leaves the directory empty). Fixed local drives are always eligible; removable, network, and
/// cloud-backed drives are opt-in via settings because they can be slow, metered, or download files.
/// </summary>
public static class DriveEnumerator
{
    // Volume-label / format fragments that strongly suggest a lettered drive is actually a
    // cloud-provider virtual drive (e.g. Google Drive for desktop mounts as a drive letter).
    private static readonly string[] CloudMarkers =
    [
        "google drive", "googledrive", "onedrive", "dropbox", "icloud", "box drive", "pcloud",
    ];

    /// <summary>
    /// Pure selection: given a set of drive descriptors and the user's opt-in toggles, returns the
    /// distinct root paths (e.g. <c>C:\</c>) that should be searched. Cloud-detected drives are
    /// included only when <paramref name="includeCloud"/> is set, regardless of their underlying
    /// <see cref="DriveType"/>, so a Google-Drive "fixed" mount is excluded by default.
    /// </summary>
    public static List<string> SelectRoots(
        IEnumerable<DriveDescriptor> drives,
        bool includeNetwork,
        bool includeRemovable,
        bool includeCloud)
    {
        var roots = new List<string>();
        if (drives is null) return roots;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drives)
        {
            if (d is null || !d.IsReady || string.IsNullOrWhiteSpace(d.Root)) continue;

            bool include;
            if (d.IsCloud)
            {
                include = includeCloud;
            }
            else
            {
                include = d.Type switch
                {
                    DriveType.Fixed => true,
                    DriveType.Removable => includeRemovable,
                    DriveType.Network => includeNetwork,
                    _ => false,
                };
            }

            if (include && seen.Add(d.Root))
                roots.Add(d.Root);
        }

        return roots;
    }

    /// <summary>Queries the live machine and returns the root paths to search for an all-drives run.</summary>
    public static List<string> GetSearchRoots(bool includeNetwork, bool includeRemovable, bool includeCloud)
        => SelectRoots(EnumerateDrives(), includeNetwork, includeRemovable, includeCloud);

    /// <summary>Snapshots every drive the OS reports, classifying readiness, type, and best-effort cloud status.</summary>
    public static IReadOnlyList<DriveDescriptor> EnumerateDrives()
    {
        var list = new List<DriveDescriptor>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return list; }
        catch (UnauthorizedAccessException) { return list; }

        foreach (var drive in drives)
        {
            bool ready;
            try { ready = drive.IsReady; }
            catch { ready = false; }

            DriveType type;
            try { type = drive.DriveType; }
            catch { type = DriveType.Unknown; }

            string root;
            try { root = drive.RootDirectory.FullName; }
            catch { root = drive.Name; }

            list.Add(new DriveDescriptor(root, type, ready, ready && IsLikelyCloudDrive(drive)));
        }

        return list;
    }

    /// <summary>
    /// Best-effort detection of a lettered drive that is really a cloud-provider mount (e.g. Google
    /// Drive for desktop). Network drives and drives whose volume label or filesystem name matches a
    /// known provider are treated as cloud. Detection is intentionally conservative; cloud drives are
    /// off by default, so a false negative only means the drive is searched as a normal fixed drive.
    /// </summary>
    public static bool IsLikelyCloudDrive(DriveInfo drive)
    {
        if (drive is null) return false;
        try
        {
            if (!drive.IsReady) return false;
            return MatchesCloudMarker(SafeGet(() => drive.VolumeLabel), SafeGet(() => drive.DriveFormat));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pure cloud-marker match: true when the volume label or filesystem name contains a known
    /// cloud-provider marker (case-insensitive). Extracted from <see cref="IsLikelyCloudDrive"/> so the
    /// detection logic is unit-testable without a live <see cref="DriveInfo"/>.
    /// </summary>
    internal static bool MatchesCloudMarker(string? label, string? format)
    {
        string l = (label ?? string.Empty).ToLowerInvariant();
        string f = (format ?? string.Empty).ToLowerInvariant();
        foreach (var marker in CloudMarkers)
        {
            if (l.Contains(marker, StringComparison.Ordinal) || f.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string SafeGet(Func<string?> get)
    {
        try { return get() ?? string.Empty; }
        catch { return string.Empty; }
    }
}
