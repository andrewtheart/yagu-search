using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>Free/total space on the volume that holds the index, as measured for the disk guard.</summary>
internal readonly record struct IndexVolumeSpace(string DriveName, long TotalBytes, long AvailableBytes)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : (double)(TotalBytes - AvailableBytes) / TotalBytes * 100;
}

/// <summary>
/// Raised when a streaming merge must stop because writing more spool/output bytes would breach the
/// user's configured disk limits. The caller deletes its private workspace and leaves the live index
/// exactly as it was.
/// </summary>
public sealed class IndexCompactionDiskGuardException : Exception
{
    internal IndexCompactionDiskGuardException(string driveName, string reason)
        : base($"Index maintenance stopped: {reason}")
    {
        DriveName = driveName;
    }

    /// <summary>The volume that ran out of headroom.</summary>
    public string DriveName { get; }
}

/// <summary>
/// Bounds how much disk a streaming merge may consume, using the settings the user already has
/// (<c>IndexMinimumFreeSpaceMB</c> and <c>IndexMaxDiskUsagePercent</c>) rather than a new hidden ceiling.
/// <para>
/// Bytes are accounted as they are actually created, so the guard reflects the run and output files the
/// merge is writing right now, not an estimate. The volume is re-probed periodically (and always before
/// declaring a breach) so a concurrent writer filling the drive is noticed. A volume that cannot be
/// probed fails open: maintenance is never blocked by an unreadable drive.
/// </para>
/// </summary>
internal sealed class IndexCompactionDiskGuard
{
    /// <summary>Re-probe the volume at least this often, in bytes created.</summary>
    private const long ProbeIntervalBytes = 64L * 1024 * 1024;

    private readonly string _path;
    private readonly long _minimumFreeBytes;
    private readonly int _maxUsedPercent;
    private readonly Func<string, IndexVolumeSpace?> _probe;

    private IndexVolumeSpace? _snapshot;
    private long _bytesSinceProbe;

    public IndexCompactionDiskGuard(
        string path,
        int minimumFreeSpaceMB,
        int maxDiskUsagePercent,
        Func<string, IndexVolumeSpace?>? probe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _minimumFreeBytes = minimumFreeSpaceMB > 0 ? (long)minimumFreeSpaceMB * 1024 * 1024 : 0;
        _maxUsedPercent = maxDiskUsagePercent is > 0 and < 100 ? maxDiskUsagePercent : 0;
        _probe = probe ?? ProbeVolume;
    }

    /// <summary>Total bytes this merge has reported creating.</summary>
    public long BytesCreated { get; private set; }

    /// <summary>True when neither configured limit is in force, so probing can be skipped entirely.</summary>
    public bool IsDisabled => _minimumFreeBytes <= 0 && _maxUsedPercent <= 0;

    /// <summary>
    /// Throws <see cref="IndexCompactionDiskGuardException"/> when creating
    /// <paramref name="additionalBytes"/> more would breach a configured limit.
    /// </summary>
    public void EnsureHeadroomFor(long additionalBytes)
    {
        if (IsDisabled || additionalBytes < 0)
            return;
        if (_snapshot is null || _bytesSinceProbe >= ProbeIntervalBytes)
            Reprobe();
        if (_snapshot is not { } snapshot || snapshot.TotalBytes <= 0)
            return; // unreadable volume: fail open

        long projectedAvailable = snapshot.AvailableBytes - _bytesSinceProbe - additionalBytes;
        if (!Breaches(snapshot, projectedAvailable))
            return;

        // Re-measure before aborting: the cached projection may be stale after other work freed space.
        Reprobe();
        if (_snapshot is not { } confirmed || confirmed.TotalBytes <= 0)
            return;
        projectedAvailable = confirmed.AvailableBytes - additionalBytes;
        if (!Breaches(confirmed, projectedAvailable))
            return;

        string reason = _minimumFreeBytes > 0 && projectedAvailable < _minimumFreeBytes
            ? $"drive {confirmed.DriveName} would drop below the {_minimumFreeBytes / (1024 * 1024):N0} MB of free space you require"
            : $"drive {confirmed.DriveName} would exceed the {_maxUsedPercent}% full limit you set";
        YaguLog.For("ContentIndex").LogWarning(
            "Streaming index maintenance aborted for disk headroom: {Reason} (created {CreatedMB} MB so far).",
            reason, BytesCreated / (1024 * 1024));
        throw new IndexCompactionDiskGuardException(confirmed.DriveName, reason);
    }

    /// <summary>Records bytes that were actually written, so the projection tracks reality.</summary>
    public void RecordCreated(long bytes)
    {
        if (bytes <= 0)
            return;
        BytesCreated += bytes;
        _bytesSinceProbe += bytes;
    }

    private bool Breaches(IndexVolumeSpace snapshot, long projectedAvailable)
    {
        if (_minimumFreeBytes > 0 && projectedAvailable < _minimumFreeBytes)
            return true;
        if (_maxUsedPercent <= 0)
            return false;
        long projectedUsed = snapshot.TotalBytes - Math.Max(0, projectedAvailable);
        return (double)projectedUsed / snapshot.TotalBytes * 100 >= _maxUsedPercent;
    }

    private void Reprobe()
    {
        _snapshot = _probe(_path);
        _bytesSinceProbe = 0;
    }

    internal static IndexVolumeSpace? ProbeVolume(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return null;
            return new IndexVolumeSpace(drive.Name, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Write-through stream that charges every byte to an <see cref="IndexCompactionDiskGuard"/> before it
/// reaches the underlying stream, so a merge's bulk output files are bounded by the same configured
/// limits as its spool rather than being able to fill the volume between spool checks. The inner stream
/// is never disposed here: it stays owned by the writer that supplied it.
/// </summary>
internal sealed class DiskGuardedStream(Stream inner, IndexCompactionDiskGuard? guard) : Stream
{
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        guard?.EnsureHeadroomFor(buffer.Length);
        inner.Write(buffer);
        guard?.RecordCreated(buffer.Length);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value)
    {
        Span<byte> one = [value];
        Write(one);
    }

    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
