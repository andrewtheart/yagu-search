using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Journal-gap recovery via a single <c>FSCTL_ENUM_USN_DATA</c> sweep of the MFT. The filesystem itself
/// filters the sweep to records whose last-change USN is at or above the index checkpoint, so this returns
/// the changed set directly — no directory crawl and no per-file handle at all.
/// <para>
/// <b>Requires elevation.</b> Unlike <c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c> there is no unprivileged
/// variant of this control code, so a normal user session gets <c>ERROR_ACCESS_DENIED</c>. That is not an
/// error condition: the scan reports failure and the caller falls back to
/// <see cref="PerFileUsnChangeScanner"/>, which needs no privileges. This type is therefore a pure
/// optimisation for sessions that happen to be elevated — Yagu never prompts for elevation to use it.
/// </para>
/// </summary>
internal sealed class MftUsnChangeScanner : IVolumeChangeScanner
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorHandleEof = 38;
    private const int EnumBufferSize = 512 * 1024;
    private const int MftEnumDataV1Size = 28;

    private readonly Func<string, SafeFileHandle?> _openVolume;
    private readonly EnumUsnData? _enumerate;

    /// <summary>Issues one <c>FSCTL_ENUM_USN_DATA</c> call; returns false with the Win32 error on failure.</summary>
    internal delegate bool EnumUsnData(
        SafeFileHandle volume, byte[] input, byte[] output, out int bytesReturned, out int error);

    public MftUsnChangeScanner(
        Func<string, SafeFileHandle?>? openVolume = null,
        EnumUsnData? enumerate = null)
    {
        _openVolume = openVolume ?? OpenVolumeRoot;
        _enumerate = enumerate;
    }

    public string Name => "MFT sweep";

    public VolumeChangeScanResult Scan(
        string normalizedRoot,
        UsnCheckpoint since,
        IndexIngestionPolicy policy,
        string excludedStorageRoot,
        int parallelism,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRoot);

        if (since.JournalId == 0 || since.NextUsn <= 0)
            return VolumeChangeScanResult.Failed("the index has no usable checkpoint to compare file USNs against");

        SafeFileHandle? volume = null;
        try
        {
            volume = _openVolume(normalizedRoot);
            if (volume is null || volume.IsInvalid)
                return VolumeChangeScanResult.Failed("the volume root could not be opened for an MFT sweep");

            EnumUsnData enumerate = _enumerate ?? Enumerate;
            var input = new byte[MftEnumDataV1Size];
            var output = new byte[EnumBufferSize];
            var changed = new List<UsnChange>();
            var records = new List<UsnFileRecord>();
            ulong startFileReferenceNumber = 0;
            long examined = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // MFT_ENUM_DATA_V1: StartFileReferenceNumber, LowUsn, HighUsn, Min/MaxMajorVersion.
                // Min/Max 2..3 matches the record versions the rest of the index already understands.
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0), startFileReferenceNumber);
                BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8), since.NextUsn);
                BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
                BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(24), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(26), 3);

                if (!enumerate(volume, input, output, out int returned, out int error))
                {
                    if (error == ErrorHandleEof)
                        break;
                    return VolumeChangeScanResult.Failed(error == ErrorAccessDenied
                        ? "an MFT sweep requires elevation on this volume"
                        : $"the MFT sweep failed (Win32 error {error})");
                }

                if (returned <= sizeof(ulong))
                    break; // only the continuation cursor came back → no more records

                startFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0));

                records.Clear();
                UsnParseStatus status = UsnFileRecordParser.ParseRecords(
                    output.AsSpan(sizeof(ulong), returned - sizeof(ulong)), records);
                if (status != UsnParseStatus.Ok)
                    return VolumeChangeScanResult.Failed($"the MFT sweep returned an unusable record ({status})");

                foreach (UsnFileRecord record in records)
                {
                    examined++;
                    // Directories are not indexed; a renamed directory leaves its files at new paths that
                    // simply classify as unindexed and live-scan, which is safe.
                    if (record.Attributes.HasFlag(FileAttributes.Directory))
                        continue;
                    if (record.Usn >= since.NextUsn)
                        changed.Add(new UsnChange(record.Identity, UsnJournalReader.AllReasons));
                }

                progress?.Invoke(examined);
            }

            YaguLog.For("ContentIndex").LogInformation(
                "MFT rescan of '{Root}' examined {Examined} record(s): {Changed} changed since USN {Checkpoint}.",
                normalizedRoot, examined, changed.Count, since.NextUsn);

            return new VolumeChangeScanResult(true, null, changed, Array.Empty<string>(), examined);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return VolumeChangeScanResult.Failed($"the MFT sweep failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            volume?.Dispose();
        }
    }

    public void Dispose()
    {
        // The volume handle is scoped to a single Scan call.
    }

    private static SafeFileHandle? OpenVolumeRoot(string path)
    {
        string? volumeRoot = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(volumeRoot))
            return null;
        SafeFileHandle handle = CreateFileW(
            volumeRoot,
            FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        return handle.IsInvalid ? null : handle;
    }

    private static bool Enumerate(
        SafeFileHandle volume, byte[] input, byte[] output, out int bytesReturned, out int error)
    {
        error = 0;
        if (DeviceIoControl(volume, FSCTL_ENUM_USN_DATA, input, input.Length,
                output, output.Length, out bytesReturned, IntPtr.Zero))
        {
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
