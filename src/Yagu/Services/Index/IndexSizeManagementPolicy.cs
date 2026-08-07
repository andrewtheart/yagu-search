namespace Yagu.Services.Index;

/// <summary>
/// Pure resolution of the <b>effective</b> size-management policy for one indexed root: the global
/// <c>AppSettings.Index*</c> size settings merged with that root's <see cref="IndexedRootSizePolicy"/>
/// override. Nothing here touches disk.
/// <para>
/// Yagu's index is an append-mostly structure: every incremental refresh publishes an immutable delta
/// segment, and only <b>coalescing</b> (merging bounded contiguous runs of small segments) or
/// <b>compaction</b> (folding every layer into a fresh base) ever gives storage back. This type decides
/// which of those an index is allowed to use and when its storage ceiling has been reached.
/// </para>
/// <para>
/// None of these choices can change search results. Leaving an index segmented, or halting maintenance
/// because it hit its budget, only reduces how much the index can prune; every candidate it cannot prove
/// safe to skip is still read live from disk.
/// </para>
/// <para>
/// App-side only (it reads <see cref="AppSettings"/>). The resolved <see cref="EffectiveIndexSizePolicy"/>
/// it produces is worker-safe and is what travels into the maintenance worker.
/// </para>
/// </summary>
public static class IndexSizeManagementPolicy
{
    /// <summary>Maximum retained per-root size overrides (bounds the settings list and the Indexing-tab UI).</summary>
    public const int MaxPolicies = 256;

    /// <summary>Coerces <paramref name="value"/> to a known mode; unknown/blank becomes <c>CoalesceThenCompact</c>.</summary>
    public static string NormalizeMode(string? value) => IndexSizeManagementModes.Normalize(value);

    /// <summary>
    /// Canonicalizes a list of overrides: normalize each path, coerce the mode, clamp the sentinels, drop
    /// blank-path or fully-inherited (inert) entries, de-duplicate by normalized path (last wins), and cap
    /// at <see cref="MaxPolicies"/>. Order-preserved.
    /// </summary>
    public static List<IndexedRootSizePolicy> Normalize(IEnumerable<IndexedRootSizePolicy>? policies)
    {
        var result = new List<IndexedRootSizePolicy>();
        if (policies is null)
            return result;

        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (IndexedRootSizePolicy? policy in policies)
        {
            if (policy is null)
                continue;
            string path = IndexScopeIdentity.NormalizePath(policy.Path ?? string.Empty);
            if (path.Length == 0)
                continue;

            // Empty mode and -1 budgets mean "inherit", so an entry with all three is inert -> drop it.
            string mode = string.IsNullOrWhiteSpace(policy.Mode) ? string.Empty : NormalizeMode(policy.Mode);
            int budget = policy.SizeBudgetMB < 0 ? -1 : policy.SizeBudgetMB;
            int compactCap = policy.MaxAutoCompactionSizeMB < 0 ? -1 : policy.MaxAutoCompactionSizeMB;
            if (mode.Length == 0 && budget < 0 && compactCap < 0)
                continue;

            var entry = new IndexedRootSizePolicy
            {
                Path = path,
                Mode = mode,
                SizeBudgetMB = budget,
                MaxAutoCompactionSizeMB = compactCap,
            };
            if (byPath.TryGetValue(path, out int existing))
            {
                result[existing] = entry; // last wins
                continue;
            }
            if (result.Count >= MaxPolicies)
                continue;
            byPath[path] = result.Count;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>The override registered for <paramref name="root"/> (matched by canonical path), or null.</summary>
    public static IndexedRootSizePolicy? Find(IEnumerable<IndexedRootSizePolicy>? policies, string root)
    {
        if (policies is null || string.IsNullOrWhiteSpace(root))
            return null;
        string key = IndexScopeIdentity.NormalizePath(root);
        foreach (IndexedRootSizePolicy? policy in policies)
        {
            if (policy is null)
                continue;
            if (string.Equals(IndexScopeIdentity.NormalizePath(policy.Path ?? string.Empty), key, StringComparison.OrdinalIgnoreCase))
                return policy;
        }
        return null;
    }

    /// <summary>Adds or replaces <paramref name="root"/>'s override, returning the canonicalized list.</summary>
    public static List<IndexedRootSizePolicy> Set(IEnumerable<IndexedRootSizePolicy>? policies, IndexedRootSizePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var merged = new List<IndexedRootSizePolicy>(Remove(policies, policy.Path)) { policy };
        return Normalize(merged);
    }

    /// <summary>Drops <paramref name="root"/>'s override, returning the canonicalized list.</summary>
    public static List<IndexedRootSizePolicy> Remove(IEnumerable<IndexedRootSizePolicy>? policies, string root)
    {
        var result = Normalize(policies);
        if (string.IsNullOrWhiteSpace(root))
            return result;
        string key = IndexScopeIdentity.NormalizePath(root);
        result.RemoveAll(p => string.Equals(p.Path, key, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// The effective policy for <paramref name="root"/>: global size settings with that root's override
    /// applied. Roots without an override get the global values.
    /// </summary>
    public static EffectiveIndexSizePolicy Resolve(AppSettings settings, string root)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IndexedRootSizePolicy? over = Find(settings.IndexedRootSizePolicies, root);

        string mode = over is not null && !string.IsNullOrWhiteSpace(over.Mode)
            ? NormalizeMode(over.Mode)
            : NormalizeMode(settings.IndexSizeManagementMode);
        int budgetMB = over is not null && over.SizeBudgetMB >= 0
            ? over.SizeBudgetMB
            : AppSettings.NormalizeIndexMaxDiskSizeMB(settings.IndexMaxDiskSizeMB);
        int compactCapMB = over is not null && over.MaxAutoCompactionSizeMB >= 0
            ? over.MaxAutoCompactionSizeMB
            : AppSettings.NormalizeIndexMaxAutoCompactionSizeMB(settings.IndexMaxAutoCompactionSizeMB);

        return new EffectiveIndexSizePolicy(
            mode,
            budgetMB,
            compactCapMB,
            AppSettings.NormalizeIndexCoalesceMaxSegmentMB(settings.IndexCoalesceMaxSegmentMB),
            AppSettings.NormalizeIndexCoalesceMaxBatchMB(settings.IndexCoalesceMaxBatchMB),
            AppSettings.NormalizeIndexCoalesceMinRun(settings.IndexCoalesceMinRun),
            AppSettings.NormalizeIndexCoalesceMaxRunsPerPass(settings.IndexCoalesceMaxRunsPerPass));
    }
}
