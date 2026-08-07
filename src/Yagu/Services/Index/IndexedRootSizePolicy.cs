namespace Yagu.Services.Index;

/// <summary>
/// Per-folder <b>size-management</b> overrides for one registered indexed root (resolved by
/// <see cref="IndexSizeManagementPolicy"/>). These layer on top of the global size settings so each index
/// can use a different strategy: a small project folder can afford full compaction, while a whole-drive
/// index may only be allowed bounded segment coalescing.
/// <para>
/// Every field supports an "inherit the global setting" sentinel (empty string / <c>-1</c>) so an override
/// only pins the axes you actually care about.
/// </para>
/// <para>
/// Size management only decides how an index's own storage is reorganized or when maintenance stops. An
/// index that is left segmented, or one that stops being maintained because it exceeded its budget, is
/// still a valid <b>accelerator</b>: anything it cannot prove safe to prune is live-scanned, so no setting
/// here can hide a search match.
/// </para>
/// </summary>
public sealed class IndexedRootSizePolicy
{
    /// <summary>The registered root path these overrides apply to (canonicalized via <see cref="IndexScopeIdentity.NormalizePath"/>).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The size-management strategy for this root, or empty to inherit <c>AppSettings.IndexSizeManagementMode</c>.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Storage ceiling in MB for this index; <c>-1</c> inherits <c>AppSettings.IndexMaxDiskSizeMB</c> and <c>0</c> means no ceiling.</summary>
    public int SizeBudgetMB { get; set; } = -1;

    /// <summary>Largest index this root may fold in one full compaction; <c>-1</c> inherits <c>AppSettings.IndexMaxAutoCompactionSizeMB</c> and <c>0</c> removes the cap.</summary>
    public int MaxAutoCompactionSizeMB { get; set; } = -1;
}
