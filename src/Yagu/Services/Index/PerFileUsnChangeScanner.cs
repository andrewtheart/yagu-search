using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Journal-gap recovery that works <b>without elevation</b>: it crawls the indexed root and asks each file
/// for its own last-change USN via <c>FSCTL_READ_FILE_USN_DATA</c>, which requires nothing more than the
/// <c>FILE_READ_ATTRIBUTES</c> handle the index already opens elsewhere. A file whose persisted USN is at or
/// below the index checkpoint provably has not changed since the index was built, so its existing entry stays
/// trusted and its content is never re-read; everything else is re-indexed.
/// <para>
/// This is exact, not an <c>(mtime, size)</c> heuristic — the USN is written by the filesystem on every
/// change and cannot be restored by an application the way timestamps can.
/// </para>
/// <para>
/// <b>Completeness is mandatory.</b> If any directory fails to enumerate, the sweep did not see every file,
/// so advancing the checkpoint could strand a stale file forever. The scan then fails and the caller falls
/// back to a full rebuild.
/// </para>
/// </summary>
internal sealed class PerFileUsnChangeScanner : IVolumeChangeScanner
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_READ_FILE_USN_DATA = 0x000900EB;
    private const int UsnRecordBufferSize = 1024;
    private const int ProgressEveryFiles = 4096;

    private readonly Func<string, UsnFileRecord?> _readFileUsn;
    private readonly IndexCrawlerFileSystem? _fileSystem;

    public PerFileUsnChangeScanner(
        Func<string, UsnFileRecord?>? readFileUsn = null,
        IndexCrawlerFileSystem? fileSystem = null)
    {
        _readFileUsn = readFileUsn ?? ReadFileUsn;
        _fileSystem = fileSystem;
    }

    public string Name => "per-file USN";

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
        ArgumentNullException.ThrowIfNull(policy);

        if (since.JournalId == 0 || since.NextUsn <= 0)
            return VolumeChangeScanResult.Failed("the index has no usable checkpoint to compare file USNs against");

        var completion = new IndexCrawlCompletion();
        var changed = new List<UsnChange>();
        var unprovable = new List<string>();
        long examined = 0;
        long lastReported = 0;
        var gate = new object();

        try
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelism),
                CancellationToken = cancellationToken,
            };

            Parallel.ForEach(
                IndexFileCrawler.EnumerateFiles(
                    normalizedRoot, policy, excludedStorageRoot, cancellationToken, _fileSystem, completion),
                options,
                entry =>
                {
                    UsnFileRecord? record = _readFileUsn(entry.Path);
                    lock (gate)
                    {
                        examined++;
                        if (record is not { } usn)
                        {
                            // Unreadable now → never keep trusting the entry we already have for it.
                            unprovable.Add(IndexScopeIdentity.NormalizePath(entry.Path));
                        }
                        else if (usn.Usn >= since.NextUsn)
                        {
                            // The checkpoint is the next-unwritten USN, so ">=" is the changed-since test.
                            changed.Add(new UsnChange(usn.Identity, UsnJournalReader.AllReasons));
                        }

                        if (progress is not null && examined - lastReported >= ProgressEveryFiles)
                        {
                            lastReported = examined;
                            progress(examined);
                        }
                    }
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return VolumeChangeScanResult.Failed($"rescan failed ({ex.GetType().Name}: {ex.Message})");
        }

        if (!completion.IsComplete)
        {
            return VolumeChangeScanResult.Failed(
                $"the root was not completely enumerated (directory '{completion.FailedDirectory}': {completion.Failure})");
        }

        progress?.Invoke(examined);
        YaguLog.For("ContentIndex").LogInformation(
            "Per-file USN rescan of '{Root}' examined {Examined} file(s): {Changed} changed since USN {Checkpoint}, {Unprovable} unprovable.",
            normalizedRoot, examined, changed.Count, since.NextUsn, unprovable.Count);

        return new VolumeChangeScanResult(true, null, changed, unprovable, examined);
    }

    public void Dispose()
    {
        // No retained OS resources: every handle is opened and closed per file.
    }

    private static UsnFileRecord? ReadFileUsn(string path)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(
                path,
                FILE_READ_ATTRIBUTES,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);
            if (handle.IsInvalid)
                return null;

            var buffer = new byte[UsnRecordBufferSize];
            if (!DeviceIoControl(handle, FSCTL_READ_FILE_USN_DATA, IntPtr.Zero, 0,
                    buffer, buffer.Length, out int returned, IntPtr.Zero)
                || returned <= 0)
            {
                return null;
            }

            return UsnFileRecordParser.TryParseOne(buffer.AsSpan(0, returned), out UsnFileRecord record)
                ? record
                : null;
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
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
