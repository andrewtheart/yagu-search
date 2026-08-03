using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Production <see cref="IFileIdPathResolver"/> (plan §3.5): resolves a <c>FILE_ID_128</c> to its current
/// DOS path via <c>OpenFileById</c> (against a per-volume hint handle) + <c>GetFinalPathNameByHandle</c>,
/// non-elevated. Opens with <c>FILE_READ_ATTRIBUTES</c> + backup semantics and shares read/write/delete so
/// it never blocks other access and also resolves directories. Returns null for a deleted / inaccessible
/// identity — so the incremental resolver treats it as a deletion. One instance per volume/root; dispose to
/// release the hint handle.
/// </summary>
public sealed class FileIdPathResolver : IFileIdPathResolver, IDisposable
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileIdType = 0; // FILE_ID_TYPE.FileIdType (V2 64-bit file reference number)
    private const int ExtendedFileIdType = 2; // FILE_ID_TYPE.ExtendedFileIdType
    private const uint FILE_NAME_NORMALIZED = 0x0;
    private const uint VOLUME_NAME_DOS = 0x0;
    private const string ExtendedLengthPrefix = @"\\?\";

    private readonly SafeFileHandle _volumeHint;
    private readonly bool _isWindows;
    private readonly FileByIdOpener _openFileById;
    private readonly FinalPathReader _getFinalPathName;
    private bool _disposed;

    internal delegate SafeFileHandle VolumeHintOpener(
        string path, uint access, FileShare share, IntPtr securityAttributes,
        FileMode mode, uint flagsAndAttributes, IntPtr templateFile);
    internal delegate SafeFileHandle FileByIdOpener(
        SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR fileId, uint access,
        FileShare share, IntPtr securityAttributes, uint flagsAndAttributes);
    internal delegate uint FinalPathReader(
        SafeFileHandle handle, char[]? path, uint pathLength, uint flags);

    internal FileIdPathResolver(
        SafeFileHandle volumeHint,
        bool isWindows,
        FileByIdOpener openFileById,
        FinalPathReader getFinalPathName)
    {
        _volumeHint = volumeHint;
        _isWindows = isWindows;
        _openFileById = openFileById;
        _getFinalPathName = getFinalPathName;
    }

    /// <summary>
    /// Opens a resolver anchored to the volume of <paramref name="rootPath"/> (any handle on the volume acts
    /// as the hint <c>OpenFileById</c> needs). Returns null when the root can't be opened (the caller then
    /// falls back to a full rebuild rather than an incremental update).
    /// </summary>
    public static FileIdPathResolver? ForRoot(string rootPath)
        => ForRoot(
            rootPath,
            OperatingSystem.IsWindows(),
            CreateFileW,
            OpenFileById,
            GetFinalPathNameByHandleW);

    internal static FileIdPathResolver? ForRoot(
        string rootPath,
        bool isWindows,
        VolumeHintOpener openVolumeHint,
        FileByIdOpener openFileById,
        FinalPathReader getFinalPathName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !isWindows)
            return null;

        try
        {
            SafeFileHandle hint = openVolumeHint(
                rootPath,
                FILE_READ_ATTRIBUTES,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);
            if (hint.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                hint.Dispose();
                YaguLog.For("ContentIndex").LogWarning("FileIdPathResolver: could not open volume hint handle for '{Root}' (Win32 error {Win32Error}); incremental refresh will fall back to a full rebuild.", rootPath, err);
                return null;
            }
            YaguLog.For("ContentIndex").LogDebug("FileIdPathResolver: opened volume hint handle for '{Root}'.", rootPath);
            return new FileIdPathResolver(hint, isWindows, openFileById, getFinalPathName);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "FileIdPathResolver: opening a volume hint handle for '{Root}' threw; incremental refresh will fall back to a full rebuild.", rootPath);
            return null;
        }
    }

    /// <inheritdoc/>
    public string? TryResolvePath(UsnFileIdentity identity)
    {
        if (_disposed || !_isWindows)
            return null;

        SafeFileHandle? handle = null;
        try
        {
            var descriptor = new FILE_ID_DESCRIPTOR
            {
                dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                // Unprivileged ReFS journal reads emit V2 64-bit file reference numbers even when the
                // caller permits V3. FileIdType resolves those; true 128-bit identities keep the extended path.
                Type = identity.High == 0 ? FileIdType : ExtendedFileIdType,
                FileIdLow = identity.Low,
                FileIdHigh = identity.High,
            };

            handle = _openFileById(
                _volumeHint,
                ref descriptor,
                FILE_READ_ATTRIBUTES,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FILE_FLAG_BACKUP_SEMANTICS);
            if (handle.IsInvalid)
                return null;

            return GetFinalPath(handle, _getFinalPathName);
        }
        catch
        {
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static string? GetFinalPath(SafeFileHandle handle, FinalPathReader getFinalPathName)
    {
        // First call sizes the buffer (returns the required length excluding the NUL).
        uint needed = getFinalPathName(handle, null, 0, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (needed == 0)
            return null;

        var buffer = new char[needed + 1];
        uint written = getFinalPathName(handle, buffer, (uint)buffer.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (written == 0 || written >= buffer.Length)
            return null;

        string path = new string(buffer, 0, (int)written);
        // GetFinalPathNameByHandle returns the "\\?\C:\..." extended form; strip the prefix for a plain path.
        if (path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal))
            path = path[ExtendedLengthPrefix.Length..];
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _volumeHint.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;
        // ExtendedFileIdType union member: FILE_ID_128 (16 bytes) at the 8-byte-aligned union offset.
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint,
        ref FILE_ID_DESCRIPTOR lpFileId,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        char[]? lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
