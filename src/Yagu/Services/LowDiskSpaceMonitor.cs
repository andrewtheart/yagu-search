namespace Yagu.Services;

internal readonly record struct DiskSpaceSnapshot(string RootPath, long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;
    public double UsedPercent => UsedFraction * 100;

    public string DriveDisplayName
    {
        get
        {
            var trimmed = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(trimmed) ? RootPath : trimmed;
        }
    }
}

internal static class LowDiskSpaceMonitor
{
    public const double DefaultFullThreshold = AppSettings.DefaultLowDiskSpaceWarningPercent / 100d;
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(30);

    internal static double PercentToThreshold(int fullThresholdPercent)
        => AppSettings.NormalizeLowDiskSpaceWarningPercent(fullThresholdPercent) / 100d;

    public static Task StartAsync(
        string tempFilePath,
        Action<DiskSpaceSnapshot> onDiskTooFull,
        CancellationToken cancellationToken)
        => StartAsync(tempFilePath, DefaultFullThreshold, DefaultCheckInterval, onDiskTooFull, cancellationToken);

    internal static Task StartAsync(
        string tempFilePath,
        double fullThreshold,
        TimeSpan checkInterval,
        Action<DiskSpaceSnapshot> onDiskTooFull,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempFilePath);
        ArgumentNullException.ThrowIfNull(onDiskTooFull);

        if (fullThreshold <= 0 || fullThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(fullThreshold), "Threshold must be greater than 0 and less than or equal to 1.");
        if (checkInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(checkInterval), "Check interval must be positive.");

        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (TryGetSnapshot(tempFilePath, out var snapshot) && IsOverThreshold(snapshot, fullThreshold))
                    {
                        onDiskTooFull(snapshot);
                        return;
                    }

                    await Task.Delay(checkInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogService.Instance.Warning("LowDiskSpaceMonitor", $"Disk-space monitor failed for temp file '{tempFilePath}'", ex);
            }
        }, CancellationToken.None);
    }

    internal static bool TryGetSnapshot(string path, out DiskSpaceSnapshot snapshot)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var rootPath = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                snapshot = default;
                return false;
            }

            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                snapshot = default;
                return false;
            }

            snapshot = new DiskSpaceSnapshot(rootPath, drive.TotalSize, drive.AvailableFreeSpace);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            snapshot = default;
            return false;
        }
    }

    internal static bool IsOverThreshold(DiskSpaceSnapshot snapshot, double fullThreshold)
        => snapshot.TotalBytes > 0 && snapshot.UsedFraction > fullThreshold;

    internal static string BuildTerminationMessage(DiskSpaceSnapshot snapshot)
        => $"Search terminated due to low disk space — temp-file drive {snapshot.DriveDisplayName} is {snapshot.UsedPercent:F1}% full. " +
           "Free disk space or choose another search result temp-file drive before searching again.";
}