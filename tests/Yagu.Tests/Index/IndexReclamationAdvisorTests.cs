using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Pins the wiring that carries the structural size diagnosis into the view-model, the window, and the
/// CLI. Those layers are not compiled into this assembly, so their behavior is source-pinned.
/// </summary>
public sealed class IndexReclamationWiringTests
{
    private static string RepoRoot => FindRepoRoot(AppContext.BaseDirectory);

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] relative)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(relative).ToArray()));

    private static string MainWindowSource => string.Join(
        Environment.NewLine,
        Directory.GetFiles(Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow"), "MainWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));

    [Fact]
    public void PerRootHealth_DetectsAStillUpdatingIndexThatCannotBeCleanedUp()
    {
        string refresh = Read("src", "Yagu", "ViewModels", "MainViewModel.IndexStatusRefresh.cs");
        int method = refresh.IndexOf("private static IndexRootHealthEntry ReadAllDriveIndexHealth", StringComparison.Ordinal);
        Assert.True(method >= 0, "ReadAllDriveIndexHealth is missing.");
        string body = refresh[method..];

        Assert.Contains("manager.DiagnoseReclamation(", body);
        Assert.Contains("IndexRootHealthKind.ReclamationBlocked", body);
        Assert.Contains("ReclamationBlockedRoot: indexRoot", body);
        // The budget halt is the harsher state and keeps precedence.
        int budget = body.IndexOf("IndexSizeBudgetAdvisor.Diagnose(", StringComparison.Ordinal);
        int reclamation = body.IndexOf("manager.DiagnoseReclamation(", StringComparison.Ordinal);
        Assert.True(budget >= 0 && budget < reclamation);
        // Both states are surfaced to the window, not left in a hover tooltip.
        Assert.Contains("row.ReclamationBlockedRoot is { } blockedRoot", refresh);
    }

    [Fact]
    public void Dialog_SaysTheIndexIsStillUpdating_AndCompactsWithoutPersistingAnUncappedSetting()
    {
        Assert.Contains("ShowIndexSizeAttentionDialogAsync", MainWindowSource);
        Assert.Contains("This index is still updating, but it cannot be cleaned up", MainWindowSource);
        Assert.Contains("diagnosis.ExplainWhyAutomaticCleanupIsUnavailable()", MainWindowSource);
        Assert.Contains("ApplyIndexSizeAttentionRemedyAsync", MainWindowSource);
        Assert.Contains("IndexSizeAttentionRemedy.CompactNow", MainWindowSource);
        Assert.Contains("IndexSizeAttentionRemedy.ReviewSizeSettings", MainWindowSource);

        // Compaction is a one-shot worker operation; the legacy remedy persisted an uncapped setting for
        // the index and let a later background pass attempt an unbounded fold.
        Assert.Contains("IndexMaintenanceOperation.ModeCompactOnly", MainWindowSource);
        int compactMethod = MainWindowSource.IndexOf("private async Task CompactIndexNowAsync", StringComparison.Ordinal);
        Assert.True(compactMethod >= 0, "CompactIndexNowAsync is missing.");
        string compactBody = MainWindowSource[compactMethod..(compactMethod + 900)];
        Assert.DoesNotContain("SetIndexSizeOverrideAsync", compactBody);
        Assert.DoesNotContain("compactionCapMB", compactBody);
    }

    [Fact]
    public void Settings_SurfaceTheOptInHaltToggle()
    {
        string indexing = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.Indexing.cs");
        Assert.Contains("s.IndexHaltUpdatesWhenReclamationBlocked", indexing);
        Assert.Contains("Stop updating an index that can no longer be cleaned up", indexing);
    }

    [Fact]
    public void Cli_ReportsTheCohortsAndOffersTheSameCompaction()
    {
        string cli = Read("src", "Yagu", "CliRunner.cs");
        Assert.Contains("--compact-index", cli);
        Assert.Contains("a.IndexCompactRequested = true;", cli);
        Assert.Contains("IndexCompactRequested", cli);
        Assert.Contains("WriteIndexLayerCohorts(manager, settings, root)", cli);
        Assert.Contains("TryReadActiveLayerStorageBreakdownForRoot(root)", cli);
        Assert.Contains("IndexMaintenanceOperation.ModeCompactOnly", cli);

        string help = Read("HELP.md");
        Assert.Contains("`--compact-index [<path>]`", help);
        Assert.Contains("full-build pages", help);
    }

    /// <summary>
    /// The GUI's <b>Validate</b> button has a CLI counterpart, so a compacted or repaired index can be
    /// structurally checked from a script without opening Settings.
    /// </summary>
    [Fact]
    public void Cli_ExposesTheSameStructuralValidationAsTheSettingsButton()
    {
        string cli = Read("src", "Yagu", "CliRunner.cs");
        Assert.Contains("--validate-index", cli);
        Assert.Contains("a.IndexValidateRequested = true;", cli);
        Assert.Contains("IndexBuildOperationFactory.CreateValidation(settings, root)", cli);
        Assert.Contains("ValidatePreferWorkerAsync(", cli);

        string help = Read("HELP.md");
        Assert.Contains("`--validate-index [<path>]`", help);
    }

    /// <summary>
    /// A merged layer is only useful if a reader can load it back. <c>content.bin</c> is streamed (so a
    /// compacted whole-drive layer may exceed the 2 GiB a single array can address), and the whole-file
    /// records are size-checked before the merge hands the layer over — publishing an oversized one would
    /// leave a pointer claiming an index that every reader silently rejects as corrupt.
    /// </summary>
    [Fact]
    public void MergedLayers_StayReadable_ByStreamingContentAndRefusingOversizedWholeFiles()
    {
        string checksummed = Read("src", "Yagu", "Services", "Index", "ChecksummedFile.cs");
        Assert.Contains("MaxReadableBodyBytes", checksummed);
        Assert.Contains("fs.Length - DigestBytes > MaxReadableBodyBytes", checksummed);

        string serializer = Read("src", "Yagu", "Services", "Index", "ContentIndexGenerationSerializer.cs");
        Assert.Contains("TrigramPostingIndex.TryBuildFromContentFile(", serializer);
        Assert.Contains("TryDeserializeContentFile(contentPath", serializer);
        Assert.DoesNotContain("TryReadChecksummed(Path.Combine(generationDir, ContentFile)", serializer);

        string merger = Read("src", "Yagu", "Services", "Index", "StreamingSegmentRunMerger.cs");
        Assert.Contains("EnsureReadableWholeFiles(preparedDirectory)", merger);
        Assert.Contains("ChecksummedFile.MaxReadableBodyBytes", merger);
    }

    [Fact]
    public void ReclamationBlocked_NeedsAttentionButIsNotABrokenIndex()
    {
        var entry = new IndexRootHealthEntry(
            @"C:\", IndexRootHealthKind.ReclamationBlocked, "still updating", ReclamationBlockedRoot: @"C:\");

        Assert.True(entry.NeedsAttention);
        Assert.False(entry.IsHealthy);
        Assert.True(entry.HasStoredIndex);
    }
}

/// <summary>
/// Covers the structural size diagnosis: clean-up is judged on the incremental cohort only, and
/// "blocked" means the exact automatic paths maintenance would attempt are all unavailable.
/// </summary>
public sealed class IndexReclamationAdvisorTests
{
    private static EffectiveIndexSizePolicy Policy(
        string mode = IndexSizeManagementModes.CoalesceThenCompact,
        int maxAutoCompactionSizeMB = 512)
        => new(mode, SizeBudgetMB: 0, maxAutoCompactionSizeMB, CoalesceMaxSegmentMB: 1024,
            CoalesceMaxBatchMB: 4096, CoalesceMinRun: 4, CoalesceMaxRunsPerPass: 8);

    private static ActiveLayerStorageBreakdown Breakdown(
        long baseBytes = 1024L * 1024 * 1024,
        int pagingCount = 0,
        long pagingBytes = 0,
        int incrementalCount = 0,
        long incrementalBytes = 0)
        => new(baseBytes, 1, pagingBytes, pagingCount, incrementalBytes, incrementalCount);

    [Fact]
    public void FullBuildPagingLayers_NeverRaiseTheAlarm()
    {
        // A paged whole-drive build: 200 disjoint pages, no update history at all.
        ActiveLayerStorageBreakdown breakdown = Breakdown(
            pagingCount: 200, pagingBytes: 30L * 1024 * 1024 * 1024);

        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            breakdown, Policy(), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: false);

        Assert.False(diagnosis.CleanupDue);
        Assert.False(diagnosis.ReclamationBlocked);
        Assert.Empty(diagnosis.Remedies());
    }

    [Fact]
    public void TooManyIncrementalLayers_MakesCleanupDue()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 9, incrementalBytes: 1024),
            Policy(), maxDeltaSegments: 8, compactionThresholdMB: 1_000_000,
            hasEligibleIncrementalRun: true);

        Assert.True(diagnosis.CleanupDue);
        Assert.False(diagnosis.ReclamationBlocked); // a bounded merge can still run
        Assert.Contains("worth cleaning up", diagnosis.Explain());
    }

    [Fact]
    public void TooMuchIncrementalHistory_MakesCleanupDue()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 2, incrementalBytes: 4L * 1024 * 1024 * 1024),
            Policy(), maxDeltaSegments: 64, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: true);

        Assert.True(diagnosis.CleanupDue);
        Assert.Equal(4096, diagnosis.IncrementalHistoryMB);
    }

    [Fact]
    public void NoEligibleRunAndCompactionOverTheCap_IsBlocked()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 40, incrementalBytes: 20L * 1024 * 1024 * 1024),
            Policy(maxAutoCompactionSizeMB: 512), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: false);

        Assert.True(diagnosis.ReclamationBlocked);
        Assert.Contains("cannot be reclaimed automatically", IndexReclamationAdvisor.HealthStatus(diagnosis));
        Assert.Contains("merge limits you set", diagnosis.ExplainWhyAutomaticCleanupIsUnavailable());
        Assert.Contains("512", diagnosis.ExplainWhyAutomaticCleanupIsUnavailable());
    }

    [Fact]
    public void TooFewLayersToMerge_SaysSoInsteadOfBlamingLayerSize()
    {
        // Observed live on D:\: only 3 update layers against a minimum run of 4, so no run can ever form.
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 3, incrementalBytes: 300L * 1024 * 1024),
            Policy(maxAutoCompactionSizeMB: 512), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: false);

        string why = diagnosis.ExplainWhyAutomaticCleanupIsUnavailable();
        Assert.True(diagnosis.ReclamationBlocked);
        Assert.Contains("3 update layer(s), fewer than the 4", why);
        Assert.DoesNotContain("individually larger", why);
    }

    [Fact]
    public void BalancedDefaults_DoNotWarnForTheObservedFiveGigThreeLayerIndex()
    {
        // Observed dialog: 5,080 MB total, 267 MB of update history, 3 layers. The balanced defaults
        // wait until 512 MB before declaring cleanup due, so this ordinary state stays silent.
        ActiveLayerStorageBreakdown breakdown = Breakdown(
            baseBytes: 4_813L * 1024 * 1024,
            incrementalCount: 3,
            incrementalBytes: 267L * 1024 * 1024);

        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            breakdown,
            EffectiveIndexSizePolicy.Default,
            maxDeltaSegments: AppSettings.DefaultIndexMaxDeltaSegments,
            compactionThresholdMB: AppSettings.DefaultIndexCompactionThresholdMB,
            hasEligibleIncrementalRun: false);

        Assert.False(diagnosis.CleanupDue);
        Assert.False(diagnosis.ReclamationBlocked);
    }

    [Fact]
    public void BalancedDefaults_AutoCompactMediumIndex_WhenCleanupBecomesDue()
    {
        // Even if no bounded three-layer run is eligible, a medium index remains below the 8 GiB
        // automatic-compaction cap. Maintenance may compact it instead of raising the warning dialog.
        ActiveLayerStorageBreakdown breakdown = Breakdown(
            baseBytes: 4_813L * 1024 * 1024,
            incrementalCount: 3,
            incrementalBytes: 600L * 1024 * 1024);

        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            breakdown,
            EffectiveIndexSizePolicy.Default,
            maxDeltaSegments: AppSettings.DefaultIndexMaxDeltaSegments,
            compactionThresholdMB: AppSettings.DefaultIndexCompactionThresholdMB,
            hasEligibleIncrementalRun: false);

        Assert.True(diagnosis.CleanupDue);
        Assert.False(diagnosis.ReclamationBlocked);
        Assert.True(EffectiveIndexSizePolicy.Default.AllowsCompactingIndexOf(breakdown.TotalBytes));
    }

    [Fact]
    public void AnEligibleRunKeepsItUnblocked_EvenAboveTheCompactionCap()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 40, incrementalBytes: 20L * 1024 * 1024 * 1024),
            Policy(maxAutoCompactionSizeMB: 512), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: true);

        Assert.True(diagnosis.CleanupDue);
        Assert.False(diagnosis.ReclamationBlocked);
    }

    [Fact]
    public void AnUncappedCompactionKeepsItUnblocked()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 40, incrementalBytes: 20L * 1024 * 1024 * 1024),
            Policy(maxAutoCompactionSizeMB: 0), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: false);

        Assert.False(diagnosis.ReclamationBlocked);
    }

    [Fact]
    public void SizeManagementOff_BlocksAndSaysSo()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 40, incrementalBytes: 20L * 1024 * 1024 * 1024),
            Policy(IndexSizeManagementModes.Off), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: true);

        Assert.True(diagnosis.ReclamationBlocked);
        Assert.Contains("turned off", diagnosis.ExplainWhyAutomaticCleanupIsUnavailable());
    }

    [Fact]
    public void Remedies_RecommendCompactingAndNeverOfferAnUncappedSetting()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 40, incrementalBytes: 20L * 1024 * 1024 * 1024),
            Policy(), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: false);

        IReadOnlyList<IndexSizeAttentionRemedy> remedies = diagnosis.Remedies();
        Assert.Equal(IndexSizeAttentionRemedy.CompactNow, remedies[0]);
        Assert.Contains(IndexSizeAttentionRemedy.Rebuild, remedies);
        Assert.Contains(IndexSizeAttentionRemedy.Delete, remedies);
        Assert.Contains(IndexSizeAttentionRemedy.ReviewSizeSettings, remedies);
        foreach (IndexSizeAttentionRemedy remedy in remedies)
        {
            Assert.False(string.IsNullOrWhiteSpace(IndexReclamationAdvisor.RemedyLabel(remedy)));
            Assert.False(string.IsNullOrWhiteSpace(IndexReclamationAdvisor.RemedyDescription(remedy, diagnosis)));
        }
        // A rebuild must not be described as a guaranteed shrink for a whole-drive index.
        Assert.Contains(
            "as large as the content it covers",
            IndexReclamationAdvisor.RemedyDescription(IndexSizeAttentionRemedy.Rebuild, diagnosis));
    }

    [Fact]
    public void ExplanationAndRemedyFallbacks_DegradeSafelyForDefensiveInputs()
    {
        var synthetic = new IndexReclamationDiagnosis(
            CleanupDue: true,
            ReclamationBlocked: true,
            Breakdown(incrementalCount: 10, incrementalBytes: 1024 * 1024),
            IndexSizeManagementModes.CoalesceThenCompact,
            MaxAutoCompactionSizeMB: 512,
            HasEligibleIncrementalRun: true,
            MinimumRunLength: 4);

        Assert.DoesNotContain("merge limits", synthetic.ExplainWhyAutomaticCleanupIsUnavailable());

        IndexReclamationDiagnosis noMinimum = synthetic with
        {
            HasEligibleIncrementalRun = false,
            MinimumRunLength = 0,
            MaxAutoCompactionSizeMB = 0,
        };
        string noMinimumExplanation = noMinimum.ExplainWhyAutomaticCleanupIsUnavailable();
        Assert.Contains("individually larger", noMinimumExplanation);
        Assert.Contains("not permitted", noMinimumExplanation);

        var unknown = (IndexSizeAttentionRemedy)int.MaxValue;
        Assert.Equal(int.MaxValue.ToString(), IndexReclamationAdvisor.RemedyLabel(unknown));
        Assert.Equal(string.Empty, IndexReclamationAdvisor.RemedyDescription(unknown, synthetic));
    }

    [Fact]
    public void Explain_SaysTheIndexIsStillUpdating_NotStopped()
    {
        IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
            Breakdown(incrementalCount: 40, incrementalBytes: 20L * 1024 * 1024 * 1024),
            Policy(), maxDeltaSegments: 8, compactionThresholdMB: 256,
            hasEligibleIncrementalRun: false);

        Assert.Contains("keeps being updated", diagnosis.Explain());
        Assert.Contains("searches stay complete", diagnosis.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, IndexReclamationDiagnosis.Healthy.ExplainWhyAutomaticCleanupIsUnavailable());
        Assert.Contains("does not need", IndexReclamationDiagnosis.Healthy.Explain());
    }
}

/// <summary>
/// Covers the bounded, root-relative churn summary. It exists to help a user choose exclusions, so it must
/// never emit an absolute path and must stay small and deterministic.
/// </summary>
public sealed class IndexChurnSummaryTests
{
    [Fact]
    public void BusiestDirectories_AreRootRelative_AndOrderedByCount()
    {
        string[] paths =
        [
            @"C:\src\repo\obj\a.txt",
            @"C:\src\repo\obj\b.txt",
            @"C:\src\repo\obj\c.txt",
            @"C:\src\repo\bin\a.txt",
            @"C:\src\other\x.txt",
        ];

        IReadOnlyList<IndexChurnEntry> top = IndexChurnSummary.TopRootRelativeDirectories(
            paths, @"C:\src", depth: 2, take: 5);

        Assert.Equal(@"repo\obj", top[0].RootRelativeDirectory);
        Assert.Equal(3, top[0].Count);
        Assert.DoesNotContain(top, entry => entry.RootRelativeDirectory.Contains("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OutputIsBoundedByTake()
    {
        string[] paths = Enumerable.Range(0, 50).Select(i => $@"C:\src\dir{i}\file.txt").ToArray();

        IReadOnlyList<IndexChurnEntry> top = IndexChurnSummary.TopRootRelativeDirectories(
            paths, @"C:\src", depth: 1, take: 5);

        Assert.Equal(5, top.Count);
    }

    [Fact]
    public void PathsOutsideTheRootOrUnusable_CollapseToANonPathBucket()
    {
        string[] paths = [@"D:\elsewhere\file.txt", "", "   ", @"C:\src\kept\file.txt"];

        IReadOnlyList<IndexChurnEntry> top = IndexChurnSummary.TopRootRelativeDirectories(
            paths, @"C:\src", depth: 2, take: 5);

        Assert.Contains(top, entry => entry.RootRelativeDirectory == IndexChurnSummary.OutsideRootBucket && entry.Count == 3);
        Assert.Contains(top, entry => entry.RootRelativeDirectory == "kept");
    }

    [Fact]
    public void FilesDirectlyInTheRoot_UseTheRootBucket()
    {
        IReadOnlyList<IndexChurnEntry> top = IndexChurnSummary.TopRootRelativeDirectories(
            [@"C:\src\a.txt", @"C:\src\b.txt"], @"C:\src", depth: 2, take: 5);

        Assert.Single(top);
        Assert.Equal(IndexChurnSummary.RootBucket, top[0].RootRelativeDirectory);
        Assert.Equal(2, top[0].Count);
    }

    [Fact]
    public void RootPathItself_UsesTheRootBucket()
    {
        IReadOnlyList<IndexChurnEntry> top = IndexChurnSummary.TopRootRelativeDirectories(
            [@"C:\src"], @"C:\src", depth: 2, take: 5);

        Assert.Single(top);
        Assert.Equal(IndexChurnSummary.RootBucket, top[0].RootRelativeDirectory);
    }

    [Fact]
    public void DepthTruncatesDeepTrees()
    {
        IReadOnlyList<IndexChurnEntry> top = IndexChurnSummary.TopRootRelativeDirectories(
            [@"C:\src\a\b\c\d\file.txt"], @"C:\src", depth: 2, take: 5);

        Assert.Equal(@"a\b", top[0].RootRelativeDirectory);
    }

    [Fact]
    public void Describe_IsASingleBoundedLine_OrNullWhenEmpty()
    {
        Assert.Null(IndexChurnSummary.Describe([]));
        Assert.Equal(
            @"repo\obj x3, repo\bin x1",
            IndexChurnSummary.Describe([new IndexChurnEntry(@"repo\obj", 3), new IndexChurnEntry(@"repo\bin", 1)]));
    }

    [Fact]
    public void OrderIsDeterministicForTiedCounts()
    {
        string[] paths = [@"C:\src\b\1.txt", @"C:\src\a\1.txt"];

        IReadOnlyList<IndexChurnEntry> first = IndexChurnSummary.TopRootRelativeDirectories(paths, @"C:\src", 1, 5);
        IReadOnlyList<IndexChurnEntry> second = IndexChurnSummary.TopRootRelativeDirectories(paths.Reverse().ToArray(), @"C:\src", 1, 5);

        Assert.Equal(first.Select(e => e.RootRelativeDirectory), second.Select(e => e.RootRelativeDirectory));
        Assert.Equal("a", first[0].RootRelativeDirectory);
    }

    [Fact]
    public void InvalidPathsAndBlankRoots_StayInTheOutsideBucket_WithMinimumBounds()
    {
        IReadOnlyList<IndexChurnEntry> invalid = IndexChurnSummary.TopRootRelativeDirectories(
            ["\0"], @"C:\src", depth: 0, take: 0);
        Assert.Single(invalid);
        Assert.Equal(IndexChurnSummary.OutsideRootBucket, invalid[0].RootRelativeDirectory);

        IReadOnlyList<IndexChurnEntry> noRoot = IndexChurnSummary.TopRootRelativeDirectories(
            [@"C:\src\repo\file.txt"], "   ", depth: 0, take: 0);
        Assert.Single(noRoot);
        Assert.Equal(IndexChurnSummary.OutsideRootBucket, noRoot[0].RootRelativeDirectory);
    }
}
