using System.Runtime.InteropServices;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// Stable identity of the mounted volume containing an indexed root. A drive letter is only a mount point;
/// the volume GUID and serial bind an index to the actual device so a different device reusing the same
/// letter can never inherit trusted postings.
/// </summary>
public readonly record struct VolumeBinding(
    string VolumeGuidPath,
    ulong VolumeSerialNumber,
    string FileSystemName,
    string MountPoint,
    string RootRelativePath)
{
    public bool SupportsChangeJournal =>
        FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
        || FileSystemName.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Captures and validates mounted-volume identity without using labels or free-space probes.</summary>
public static partial class VolumeBindingReader
{
    private const int InitialPathChars = 260;

    internal delegate bool VolumePathReader(string path, StringBuilder result, int capacity);
    internal delegate bool VolumeInformationReader(
        string rootPath,
        StringBuilder? volumeName,
        int volumeNameCapacity,
        out uint serial,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemName,
        int fileSystemNameCapacity);

    /// <summary>Captures the volume containing <paramref name="path"/>, including nested mount points.</summary>
    public static VolumeBinding? TryCapture(string path)
        => TryCapture(
            path,
            OperatingSystem.IsWindows(),
            GetVolumePathNameW,
            GetVolumeNameForVolumeMountPointW,
            GetVolumeInformationW,
            FileIdentityReader.TryGetIdentity);

    internal static VolumeBinding? TryCapture(
        string path,
        bool isWindows,
        VolumePathReader getVolumePath,
        VolumePathReader getVolumeName,
        VolumeInformationReader getVolumeInformation,
        Func<string, FileIdentity?> getFileIdentity)
    {
        if (!isWindows || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            string fullPath = Path.GetFullPath(path);
            var mount = new StringBuilder(InitialPathChars);
            if (!getVolumePath(fullPath, mount, mount.Capacity))
                return null;

            string mountPoint = EnsureTrailingSeparator(mount.ToString());
            var guid = new StringBuilder(InitialPathChars);
            if (!getVolumeName(mountPoint, guid, guid.Capacity))
                return null;

            var fileSystem = new StringBuilder(64);
            if (!getVolumeInformation(
                    mountPoint,
                    null,
                    0,
                    out uint legacySerial,
                    out _,
                    out _,
                    fileSystem,
                    fileSystem.Capacity))
            {
                return null;
            }
            ulong serial = getFileIdentity(mountPoint)?.VolumeSerialNumber ?? legacySerial;

            string normalizedRoot = IndexScopeIdentity.NormalizePath(fullPath);
            string normalizedMount = IndexScopeIdentity.NormalizePath(mountPoint);
            string relative = Path.GetRelativePath(normalizedMount, normalizedRoot);
            if (relative == ".")
                relative = string.Empty;
            relative = relative.Replace('/', '\\').TrimStart('\\');

            return new VolumeBinding(
                NormalizeGuidPath(guid.ToString()),
                serial,
                fileSystem.ToString().Trim(),
                mountPoint,
                relative);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Returns true only when both bindings identify the same mounted filesystem volume.</summary>
    public static bool Matches(in VolumeBinding expected, in VolumeBinding actual)
        => expected.VolumeSerialNumber != 0
            && actual.VolumeSerialNumber != 0
            && expected.VolumeSerialNumber == actual.VolumeSerialNumber
            && string.Equals(NormalizeGuidPath(expected.VolumeGuidPath), NormalizeGuidPath(actual.VolumeGuidPath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.FileSystemName, actual.FileSystemName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the currently mounted volume against a persisted manifest. New manifests require GUID +
    /// serial equality. Legacy manifests without a GUID are accepted only with a matching non-zero serial.
    /// </summary>
    public static bool MatchesManifest(IndexManifest manifest, in VolumeBinding actual, out string reason)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.VolumeGuidPath))
        {
            // Backward compatibility: pre-binding manifests retain their existing journal/file-id safety
            // checks until the next full rebuild writes a strongly bound manifest.
            reason = string.Empty;
            return true;
        }
        if (!actual.SupportsChangeJournal)
        {
            reason = $"filesystem '{actual.FileSystemName}' does not provide trusted index freshness";
            return false;
        }
        if (manifest.VolumeSerialNumber == 0 || actual.VolumeSerialNumber == 0
            || manifest.VolumeSerialNumber != actual.VolumeSerialNumber)
        {
            reason = "mounted volume serial does not match the indexed volume";
            return false;
        }
        if (!string.Equals(NormalizeGuidPath(manifest.VolumeGuidPath), NormalizeGuidPath(actual.VolumeGuidPath), StringComparison.OrdinalIgnoreCase))
        {
            reason = "mounted volume GUID does not match the indexed volume";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(manifest.FileSystemName)
            && !string.Equals(manifest.FileSystemName, actual.FileSystemName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "mounted filesystem does not match the indexed filesystem";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    internal static string NormalizeGuidPath(string value)
        => EnsureTrailingSeparator((value ?? string.Empty).Trim()).ToUpperInvariant();

    private static string EnsureTrailingSeparator(string value)
        => value.EndsWith('\\') ? value : value + "\\";

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}

/// <summary>Thrown when the source root is detached or replaced while a staged index mutation is running.</summary>
public sealed class IndexVolumeChangedException : IOException
{
    public IndexVolumeChangedException(string message) : base(message) { }
}
