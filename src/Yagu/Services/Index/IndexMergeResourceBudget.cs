namespace Yagu.Services.Index;

/// <summary>
/// The resource envelope a streaming index merge runs inside. It reuses settings the user already has —
/// the build memory budget bounds the external sort's in-memory chunks, and the free-space floor and
/// full-drive percentage bound its spool and output — instead of introducing separate hidden ceilings.
/// </summary>
/// <param name="MemoryBudgetBytes">Peak bytes one external-sort chunk may hold before it spills.</param>
/// <param name="MinimumFreeSpaceMB">Free space that must remain on the index volume; 0 disables the check.</param>
/// <param name="MaxDiskUsagePercent">How full the index volume may get; 0 disables the check.</param>
public readonly record struct IndexMergeResourceBudget(
    long MemoryBudgetBytes,
    int MinimumFreeSpaceMB,
    int MaxDiskUsagePercent)
{
    /// <summary>Used by callers with no settings (tests, internal helpers): a modest chunk, no disk limits.</summary>
    public static IndexMergeResourceBudget Default { get; } = new(32L * 1024 * 1024, 0, 0);

    /// <summary>
    /// Derives the envelope from a maintenance operation's snapshot. Only half the build memory budget is
    /// given to the sort so the surrounding read/write buffers stay inside the same overall allowance.
    /// </summary>
    public static IndexMergeResourceBudget FromSettings(IndexMaintenanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        long half = (long)Math.Max(0, settings.BuildMemoryBudgetMB) * 1024 * 1024 / 2;
        return new IndexMergeResourceBudget(
            Math.Max(16L * 1024 * 1024, half),
            Math.Max(0, settings.MinimumFreeSpaceMB),
            settings.MaxDiskUsagePercent);
    }
}
