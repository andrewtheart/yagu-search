using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Covers the per-index size-management policy: canonicalization of the persisted per-root overrides,
/// resolution against the global settings, and the two decisions that actually bound index growth —
/// whether an index may be compacted, and when it has hit its storage ceiling.
/// </summary>
public class IndexSizeManagementPolicyTests
{
    private static AppSettings Settings() => new()
    {
        IndexSizeManagementMode = IndexSizeManagementModes.CoalesceThenCompact,
        IndexMaxDiskSizeMB = 4096,
        IndexMaxAutoCompactionSizeMB = 512,
        IndexCoalesceMaxSegmentMB = 128,
        IndexCoalesceMaxBatchMB = 512,
        IndexCoalesceMinRun = 4,
        IndexCoalesceMaxRunsPerPass = 8,
    };

    [Theory]
    [InlineData("Off", IndexSizeManagementModes.Off)]
    [InlineData("coalesce", IndexSizeManagementModes.Coalesce)]
    [InlineData("COMPACT", IndexSizeManagementModes.Compact)]
    [InlineData("  CoalesceThenCompact  ", IndexSizeManagementModes.CoalesceThenCompact)]
    [InlineData("nonsense", IndexSizeManagementModes.CoalesceThenCompact)]
    [InlineData("", IndexSizeManagementModes.CoalesceThenCompact)]
    [InlineData(null, IndexSizeManagementModes.CoalesceThenCompact)]
    public void NormalizeMode_CoercesToAKnownMode(string? value, string expected)
        => Assert.Equal(expected, IndexSizeManagementModes.Normalize(value));

    [Fact]
    public void Normalize_DropsInertEntriesAndDeduplicatesByPathLastWins()
    {
        var normalized = IndexSizeManagementPolicy.Normalize(
        [
            new IndexedRootSizePolicy { Path = "", Mode = IndexSizeManagementModes.Off },
            // Every axis left at its inherit sentinel -> nothing pinned -> inert.
            new IndexedRootSizePolicy { Path = @"C:\a", Mode = "", SizeBudgetMB = -1, MaxAutoCompactionSizeMB = -1 },
            new IndexedRootSizePolicy { Path = @"C:\b", SizeBudgetMB = 100 },
            new IndexedRootSizePolicy { Path = @"c:\B\", SizeBudgetMB = 200 },
        ]);

        IndexedRootSizePolicy only = Assert.Single(normalized);
        Assert.Equal(200, only.SizeBudgetMB);
        Assert.Equal(@"C:\b", only.Path, ignoreCase: true);
    }

    [Fact]
    public void Resolve_WithoutAnOverride_UsesTheGlobalSettings()
    {
        EffectiveIndexSizePolicy effective = IndexSizeManagementPolicy.Resolve(Settings(), @"C:\src");

        Assert.Equal(IndexSizeManagementModes.CoalesceThenCompact, effective.Mode);
        Assert.Equal(4096, effective.SizeBudgetMB);
        Assert.Equal(512, effective.MaxAutoCompactionSizeMB);
        Assert.Equal(128, effective.CoalesceMaxSegmentMB);
        Assert.Equal(4, effective.CoalesceMinRun);
    }

    [Fact]
    public void Resolve_AppliesOnlyThePinnedAxesOfAnOverride()
    {
        AppSettings settings = Settings();
        settings.IndexedRootSizePolicies =
        [
            new IndexedRootSizePolicy { Path = @"C:\src", Mode = IndexSizeManagementModes.Coalesce, SizeBudgetMB = 256 },
        ];

        EffectiveIndexSizePolicy effective = IndexSizeManagementPolicy.Resolve(settings, @"C:\src\");

        Assert.Equal(IndexSizeManagementModes.Coalesce, effective.Mode);
        Assert.Equal(256, effective.SizeBudgetMB);
        Assert.Equal(512, effective.MaxAutoCompactionSizeMB); // not pinned -> inherited
        // A different root keeps the global values.
        Assert.Equal(IndexSizeManagementModes.CoalesceThenCompact, IndexSizeManagementPolicy.Resolve(settings, @"D:\").Mode);
    }

    [Theory]
    [InlineData(IndexSizeManagementModes.Off, false, false)]
    [InlineData(IndexSizeManagementModes.Coalesce, true, false)]
    [InlineData(IndexSizeManagementModes.Compact, false, true)]
    [InlineData(IndexSizeManagementModes.CoalesceThenCompact, true, true)]
    public void Mode_DecidesWhichReclamationIsAllowed(string mode, bool coalesce, bool compact)
    {
        var policy = EffectiveIndexSizePolicy.Default with { Mode = mode };
        Assert.Equal(coalesce, policy.AllowsCoalescing);
        Assert.Equal(compact, policy.AllowsCompaction);
    }

    [Fact]
    public void ExceedsBudget_OnlyAppliesWhenABudgetIsSet()
    {
        var unlimited = EffectiveIndexSizePolicy.Default with { SizeBudgetMB = 0 };
        Assert.False(unlimited.ExceedsBudget(500L * 1024 * 1024 * 1024));

        var bounded = EffectiveIndexSizePolicy.Default with { SizeBudgetMB = 100 };
        Assert.False(bounded.ExceedsBudget(100L * 1024 * 1024));
        Assert.True(bounded.ExceedsBudget(101L * 1024 * 1024));
    }

    [Fact]
    public void AllowsCompactingIndexOf_HonoursTheCapAndIsNotLiftedByTheBudget()
    {
        var policy = EffectiveIndexSizePolicy.Default with { MaxAutoCompactionSizeMB = 512, SizeBudgetMB = 100 };

        Assert.True(policy.AllowsCompactingIndexOf(400L * 1024 * 1024));

        // Far over both the cap and the budget. Being over budget must NOT authorize the fold: compaction
        // re-materializes every layer in memory, so that would trade unbounded disk for unbounded memory.
        long oversized = 33L * 1024 * 1024 * 1024;
        Assert.True(policy.ExceedsBudget(oversized));
        Assert.False(policy.AllowsCompactingIndexOf(oversized));
    }

    [Fact]
    public void AllowsCompactingIndexOf_ZeroCapMeansNoLimit()
    {
        var policy = EffectiveIndexSizePolicy.Default with { MaxAutoCompactionSizeMB = 0 };
        Assert.True(policy.AllowsCompactingIndexOf(33L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void AllowsAutomaticCompactionOf_OverCap_FallsBackOnlyWhenBoundedMergeCannotProgress()
    {
        long oversized = 33L * 1024 * 1024 * 1024;
        var policy = EffectiveIndexSizePolicy.Default with { MaxAutoCompactionSizeMB = 512 };

        Assert.False(policy.AllowsAutomaticCompactionOf(oversized, boundedMergeCanProgress: true));
        Assert.True(policy.AllowsAutomaticCompactionOf(oversized, boundedMergeCanProgress: false));

        EffectiveIndexSizePolicy coalesceOnly = policy with { Mode = IndexSizeManagementModes.Coalesce };
        Assert.False(coalesceOnly.AllowsAutomaticCompactionOf(oversized, boundedMergeCanProgress: false));
    }

    [Fact]
    public void CoalescingBounds_AreConfigurableRatherThanFixed()
    {
        // The shipped defaults previously could not match a real whole-drive index, whose segments run to
        // tens of MB, so coalescing never found an eligible run and such indexes had no reclamation path.
        var policy = EffectiveIndexSizePolicy.Default;
        Assert.True(policy.CoalesceMaxSegmentBytes >= 64L * 1024 * 1024);
        Assert.True(policy.CoalesceMaxBatchBytes > policy.CoalesceMaxSegmentBytes);
    }

    [Fact]
    public void SetAndRemove_RoundTripOneRootsOverride()
    {
        var list = IndexSizeManagementPolicy.Set(
            [],
            new IndexedRootSizePolicy { Path = @"C:\src", Mode = IndexSizeManagementModes.Compact });
        Assert.Single(list);

        var replaced = IndexSizeManagementPolicy.Set(
            list,
            new IndexedRootSizePolicy { Path = @"C:\SRC\", Mode = IndexSizeManagementModes.Off });
        Assert.Equal(IndexSizeManagementModes.Off, Assert.Single(replaced).Mode);

        Assert.Empty(IndexSizeManagementPolicy.Remove(replaced, @"c:\src"));
    }

    [Fact]
    public void MaintenanceSettings_ResolveTheSamePolicyAsTheApp_ForTheWorker()
    {
        // The worker never sees AppSettings, so it re-resolves from the operation snapshot. Both sides must
        // agree or an index would be managed differently depending on where maintenance ran.
        AppSettings settings = Settings();
        settings.IndexedRootSizePolicies =
        [
            new IndexedRootSizePolicy { Path = @"C:\src", Mode = IndexSizeManagementModes.Coalesce, SizeBudgetMB = 256 },
        ];
        EffectiveIndexSizePolicy app = IndexSizeManagementPolicy.Resolve(settings, @"C:\src");

        var snapshot = new IndexMaintenanceSettings
        {
            SizeManagementMode = settings.IndexSizeManagementMode,
            SizeBudgetMB = settings.IndexMaxDiskSizeMB,
            MaxAutoCompactionSizeMB = settings.IndexMaxAutoCompactionSizeMB,
            CoalesceMaxSegmentMB = settings.IndexCoalesceMaxSegmentMB,
            CoalesceMaxBatchMB = settings.IndexCoalesceMaxBatchMB,
            CoalesceMinRun = settings.IndexCoalesceMinRun,
            CoalesceMaxRunsPerPass = settings.IndexCoalesceMaxRunsPerPass,
            RootSizePolicies = IndexSizeManagementPolicy.Normalize(settings.IndexedRootSizePolicies),
        };

        Assert.Equal(app, snapshot.ResolveSizePolicy(@"C:\src"));
    }
}
