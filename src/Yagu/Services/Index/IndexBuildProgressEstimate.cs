namespace Yagu.Services.Index;

/// <summary>
/// Estimates how far a content-index build has progressed. A build enumerates every file under the root
/// exactly once, but the file/byte total is not known up front (a pre-count pass would double the I/O),
/// so progress is estimated against a cheap physical quantity: the <b>used space of the drive</b> the
/// root lives on. As the crawl reads each file's size, the running sum of crawled bytes divided by the
/// drive's used bytes approaches 1.0 as the whole drive is indexed — which is exactly the "indexing a
/// drive" case this targets. For a subfolder the drive's used space over-estimates the total, so the
/// percentage is a lower bound (it may finish before reaching 99%); it stays monotonic and is always
/// labelled as an estimate.
/// </summary>
public static class IndexBuildProgressEstimate
{
    /// <summary>
    /// Estimated percent complete (0–99) from <paramref name="bytesCrawled"/> vs.
    /// <paramref name="driveUsedBytes"/>. Returns -1 (unknown) when the denominator is unusable. Capped at
    /// 99 so a full 100% is only ever shown once the build actually completes (the indicator then reverts).
    /// </summary>
    public static int Percent(long bytesCrawled, long driveUsedBytes)
    {
        if (driveUsedBytes <= 0 || bytesCrawled < 0)
            return -1;
        double ratio = (double)bytesCrawled / driveUsedBytes;
        return (int)Math.Min(ratio * 100.0, 99.0); // 100 is reserved for a completed build
    }

    /// <summary>
    /// The used bytes (total − free) of the volume containing <paramref name="root"/>, or -1 when it can't
    /// be read. Cheap — a single <see cref="System.IO.DriveInfo"/> query, no enumeration. Never throws.
    /// </summary>
    public static long DriveUsedBytes(string? root)
        => DriveUsedBytes(root, static driveRoot => new System.IO.DriveInfo(driveRoot));

    internal static long DriveUsedBytes(string? root, Func<string, System.IO.DriveInfo> driveFactory)
    {
        try
        {
            string? driveRoot = System.IO.Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(driveRoot))
                return -1;
            System.IO.DriveInfo drive = driveFactory(driveRoot);
            if (!drive.IsReady)
                return -1;
            long used = drive.TotalSize - drive.TotalFreeSpace;
            return Math.Max(used, 0);
        }
        catch
        {
            return -1;
        }
    }
}
