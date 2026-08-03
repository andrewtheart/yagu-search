using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yagu.Services.Index;

/// <summary>
/// The durable content identity of a file (plan §3.6): the volume serial number plus the filesystem's
/// journal-compatible file reference number. This is stable across renames and shared by hard links to
/// the same content. NTFS and ReFS unprivileged USN reads both emit V2 reference numbers; persisting that
/// same 64-bit value (in <see cref="UsnFileIdentity.Low"/>, with High=0) lets journal changes map exactly.
/// A path replaced by a different file has a different identity.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerialNumber, UsnFileIdentity FileId);

/// <summary>
/// Reads a file's durable <see cref="FileIdentity"/> without elevation via
/// <c>GetFileInformationByHandleEx(FileIdInfo)</c> (64-bit volume serial) plus
/// <c>GetFileInformationByHandle</c> (the V2-USN-compatible file index; plan §3.6). It opens the file with only
/// <c>FILE_READ_ATTRIBUTES</c> + backup semantics (so it also works for directories) and shares
/// read/write/delete so it never blocks other access. Returns null when the file cannot be opened or the
/// identity cannot be read. The captured <see cref="FileIdentity.FileId"/> matches the identity the USN
/// journal reports for the same file, which is what lets <see cref="FileIdMap"/> map journal changes back
/// to indexed content.
/// </summary>
public static class FileIdentityReader
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileIdInfo = 18; // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo
    private const int FileIdInfoSize = 24; // ULONGLONG VolumeSerialNumber (8) + FILE_ID_128 FileId (16)

    /// <summary>Reads the durable identity of <paramref name="path"/>, or null when unavailable.</summary>
    public static FileIdentity? TryGetIdentity(string path)
        => TryGetIdentity(path, OpenHandle, TryGetIdentity);

    internal static FileIdentity? TryGetIdentity(
        string path,
        Func<string, SafeFileHandle> openHandle,
        Func<SafeFileHandle, FileIdentity?> readIdentity)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        SafeFileHandle? handle = null;
        try
        {
            handle = openHandle(path);
            if (handle.IsInvalid)
                return null;

            return readIdentity(handle);
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

    /// <summary>
    /// Reads the durable identity from an <b>already-open</b> handle (plan §5.4). This is what lets an
    /// index build capture a file's identity from the very same handle whose bytes it indexed — no second
    /// <c>CreateFileW</c>, and the identity is guaranteed to belong to the exact file object that was read,
    /// even if the path is concurrently replaced. The handle only needs <c>FILE_READ_ATTRIBUTES</c> (any
    /// read handle qualifies). Returns null when the handle is invalid or the identity cannot be read.
    /// </summary>
    public static FileIdentity? TryGetIdentity(SafeFileHandle handle)
        => TryGetIdentity(handle, ReadFileIdInfo, GetFileInformationByHandle);

    internal static FileIdentity? TryGetIdentity(
        SafeFileHandle handle,
        FileIdInfoReader readFileIdInfo,
        LegacyFileInfoReader readLegacyFileInfo)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
            return null;

        try
        {
            var buffer = new byte[FileIdInfoSize];
            if (!readFileIdInfo(handle, buffer))
                return null;

            ulong volumeSerial = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            if (!readLegacyFileInfo(handle, out BY_HANDLE_FILE_INFORMATION legacy))
                return null;

            ulong fileReferenceNumber = ((ulong)legacy.FileIndexHigh << 32) | legacy.FileIndexLow;
            var fileId = UsnFileIdentity.FromFileReferenceNumber(fileReferenceNumber);
            return new FileIdentity(volumeSerial, fileId);
        }
        catch
        {
            return null;
        }
    }

    internal delegate bool FileIdInfoReader(SafeFileHandle handle, byte[] buffer);
    internal delegate bool LegacyFileInfoReader(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION information);

    private static SafeFileHandle OpenHandle(string path)
        => CreateFileW(
            path,
            FILE_READ_ATTRIBUTES,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

    private static bool ReadFileIdInfo(SafeFileHandle handle, byte[] buffer)
        => GetFileInformationByHandleEx(handle, FileIdInfo, buffer, (uint)buffer.Length);

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
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        byte[] lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
