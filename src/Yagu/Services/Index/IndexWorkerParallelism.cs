using System.Runtime.InteropServices;

namespace Yagu.Services.Index;

/// <summary>
/// Resolves the bounded parallelism used by the isolated index workers. A configured value of zero is
/// automatic: full-build file reads use physical cores (CPU-heavy trigram extraction plus I/O), while
/// mapped-query classification uses logical processors. Both are conservatively capped; build parallelism
/// is additionally bounded by the configured build-memory budget. A rotational root is forced to one when
/// the existing HDD parallelism safeguard is enabled.
/// </summary>
internal static class IndexWorkerParallelism
{
    public const int Automatic = 0;
    public const int Maximum = 32;
    public const int MaximumAutomaticBuild = 8;
    public const int MaximumAutomaticQuery = 16;
    public const int BuildLaneReserveMB = 64;

    private static readonly Lazy<int> PhysicalCoreCount = new(DetectPhysicalCoreCount);

    /// <summary>Best-effort physical-core count. Zero means the operating-system probe was unavailable.</summary>
    public static int DetectedPhysicalCoreCount => PhysicalCoreCount.Value;

    public static int NormalizeSetting(int value)
        => value <= Automatic ? Automatic : Math.Clamp(value, 1, Maximum);

    public static int ResolveBuildDegree(
        int configured,
        int logicalProcessorCount,
        int physicalCoreCount,
        int buildMemoryBudgetMB,
        bool limitParallelismOnHardDisks,
        bool isHardDisk)
    {
        if (limitParallelismOnHardDisks && isHardDisk)
            return 1;

        int logical = Math.Max(1, logicalProcessorCount);
        int physical = physicalCoreCount > 0
            ? Math.Min(physicalCoreCount, logical)
            : Math.Max(1, logical / 2);
        int requested = NormalizeSetting(configured) == Automatic
            ? Math.Min(MaximumAutomaticBuild, physical)
            : NormalizeSetting(configured);
        // The existing batch trigram bound reserves roughly half the build budget. Keep concurrent
        // per-file results inside the other half instead of allowing parallelism to multiply the target.
        int memoryBound = Math.Max(1, Math.Max(1, buildMemoryBudgetMB) / (BuildLaneReserveMB * 2));
        return Math.Max(1, Math.Min(Math.Min(requested, logical), memoryBound));
    }

    public static int ResolveQueryDegree(
        int configured,
        int logicalProcessorCount,
        bool limitParallelismOnHardDisks,
        bool isHardDisk)
    {
        if (limitParallelismOnHardDisks && isHardDisk)
            return 1;

        int logical = Math.Max(1, logicalProcessorCount);
        int requested = NormalizeSetting(configured) == Automatic
            ? Math.Min(MaximumAutomaticQuery, logical)
            : NormalizeSetting(configured);
        return Math.Max(1, Math.Min(requested, logical));
    }

    private static int DetectPhysicalCoreCount()
        => DetectPhysicalCoreCount(OperatingSystem.IsWindows(), GetLogicalProcessorInformationEx);

    internal static int DetectPhysicalCoreCount(
        bool isWindows,
        LogicalProcessorInformationReader readInformation)
    {
        if (!isWindows)
            return 0;

        uint bytes = 0;
        _ = readInformation(RelationProcessorCore, IntPtr.Zero, ref bytes);
        if (bytes < 8)
            return 0;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(checked((int)bytes));
            if (!readInformation(RelationProcessorCore, buffer, ref bytes))
                return 0;

            return ParsePhysicalCoreCount(buffer, checked((int)bytes));
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int ParsePhysicalCoreCount(IntPtr buffer, int length)
    {
        int count = 0;
        int offset = 0;
        while (offset <= length - 8)
        {
            IntPtr entry = IntPtr.Add(buffer, offset);
            int relationship = Marshal.ReadInt32(entry, 0);
            int size = Marshal.ReadInt32(entry, 4);
            if (size < 8 || offset > length - size)
                return 0;
            if (relationship == RelationProcessorCore)
                count++;
            offset += size;
        }
        return offset == length ? count : 0;
    }

    internal delegate bool LogicalProcessorInformationReader(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
