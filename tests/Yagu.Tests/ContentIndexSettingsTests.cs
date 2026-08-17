using System;
using System.IO;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the persisted content-index settings (plan §6.1): defaults, normalization/validation
/// bounds, enum coercion, save/load round-trip with on-load clamping, and the derived
/// <see cref="AppSettings.ContentIndexActiveByDefault"/> gate — plus the
/// <see cref="SearchOptions.UseContentIndex"/> default and its survival through the
/// <c>SearchService.CopyOptions</c> clone.
/// </summary>
public sealed class ContentIndexSettingsTests
{
    [Fact]
    public void Defaults_MatchPlan()
    {
        var s = new AppSettings();
        Assert.True(s.EnableContentIndex); // content index is ON by default (master switch)
        Assert.True(s.UseContentIndexByDefault);
        Assert.True(s.IndexAccelerateLiterals);
        Assert.True(s.IndexAccelerateWholeWord);
        Assert.True(s.IndexAccelerateRegex);
        Assert.True(s.IndexAccelerateMultiline);
        Assert.True(s.IndexUseNativeWorker); // isolated out-of-process worker is the default; toggleable in Settings
        Assert.Equal(0, s.IndexBuildWorkerParallelism); // hardware/memory-based automatic mode
        Assert.Equal(0, s.IndexQueryWorkerParallelism); // logical-core-based automatic mode
        Assert.False(s.IndexBuildPdfTextExtendedSource); // PDF-text extended-source pruning is opt-in (off by default)
        Assert.False(s.IndexBuildImageTextExtendedSource); // OCR positive-candidate indexing is opt-in
        Assert.True(s.IndexProduceV3QueryStructures); // new indexes include mapped-query structures by default
        Assert.False(s.IndexUseV3QueryReader); // consuming format-v3 during search is experimental/opt-in (off by default)
        Assert.True(s.IndexUseWorkerQuerySessions); // mapped worker keeps large index postings out of Yagu's process
        Assert.Equal(AppSettings.DefaultIndexQueryStartupBudgetMs, s.IndexQueryStartupBudgetMs);
        Assert.Equal(AppSettings.DefaultIndexMaxCandidatePercent, s.IndexMaxCandidatePercent);
        Assert.Equal(AppSettings.DefaultIndexMaxFileSizeMB, s.IndexMaxFileSizeMB);
        Assert.Equal(AppSettings.DefaultIndexRetainedGenerationCount, s.IndexRetainedGenerationCount);
        Assert.Equal(AppSettings.DefaultIndexStorageHistoryRetentionDays, s.IndexStorageHistoryRetentionDays);
        Assert.Equal(AppSettings.DefaultIndexBuildTrigger, s.IndexBuildTrigger);
        Assert.Equal(AppSettings.DefaultIndexUpdateMode, s.IndexUpdateMode);
        Assert.Equal("ManualFullRebuild", s.IndexUpdateMode); // V1 default never auto-rebuilds
        Assert.Equal(AppSettings.DefaultIndexRemovableDrivePolicy, s.IndexRemovableDrivePolicy);
        Assert.Equal(string.Empty, s.IndexStorageDirectory);
        Assert.True(s.IndexPauseOnBattery);
        Assert.True(s.IndexPauseDuringForegroundSearch);
        Assert.True(s.IndexAutoRepair);
        Assert.Equal(30_000, s.IndexPostBuildCatchUpThresholdChanges);
        Assert.True(s.ShowIndexStatusInMainWindow);
        Assert.True(s.ShowIndexProvenanceInResults);
    }

    [Fact]
    public void Defaults_Phase3Maintenance_MatchPlan()
    {
        var s = new AppSettings();
        Assert.False(s.IndexUseWatcherHints); // watcher hints opt-in
        Assert.Equal(8, s.IndexMaxDeltaSegments);
        Assert.Equal(512, s.IndexCompactionThresholdMB);
        Assert.Equal(AppSettings.DefaultIndexMaxDeltaSegments, s.IndexMaxDeltaSegments);
        Assert.Equal(AppSettings.DefaultIndexCompactionThresholdMB, s.IndexCompactionThresholdMB);
        Assert.Equal(8192, s.IndexMaxAutoCompactionSizeMB);
        Assert.Equal(AppSettings.DefaultIndexMaxAutoCompactionSizeMB, s.IndexMaxAutoCompactionSizeMB);
        Assert.False(s.ShareAggregateIndexTelemetry); // aggregate index telemetry is opt-in
    }

    [Fact]
    public void Defaults_StreamingMergeBounds_CoverOrdinaryWholeDriveSegments()
    {
        var s = new AppSettings();
        Assert.Equal(1024, s.IndexCoalesceMaxSegmentMB);
        Assert.Equal(4096, s.IndexCoalesceMaxBatchMB);
        Assert.Equal(3, s.IndexCoalesceMinRun);
        // A minimum-length run must always be able to fit inside one batch.
        Assert.True(s.IndexCoalesceMaxBatchMB >= s.IndexCoalesceMinRun * s.IndexCoalesceMaxSegmentMB);
        // Halting a still-updating index is opt-in: coverage loss is never imposed silently.
        Assert.False(s.IndexHaltUpdatesWhenReclamationBlocked);
        Assert.Equal(s.IndexCoalesceMaxSegmentMB, EffectiveIndexSizePolicy.Default.CoalesceMaxSegmentMB);
        Assert.Equal(s.IndexCoalesceMaxBatchMB, EffectiveIndexSizePolicy.Default.CoalesceMaxBatchMB);
        Assert.Equal(s.IndexCoalesceMinRun, EffectiveIndexSizePolicy.Default.CoalesceMinRun);
        Assert.Equal(s.IndexMaxAutoCompactionSizeMB, EffectiveIndexSizePolicy.Default.MaxAutoCompactionSizeMB);
    }

    [Fact]
    public void AutomaticCompactionMigration_LiftsOnlyExactPreviousDefaults()
    {
        string temp = Path.Combine(Path.GetTempPath(), "yagu-auto-compact-migrate", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            File.WriteAllText(
                temp,
                $$"""{ "IndexSizeDefaultsMigrated": true, "IndexStreamingMergeDefaultsMigrated": true, "IndexCompactionThresholdMB": {{AppSettings.PreBalancedDefaultIndexCompactionThresholdMB}}, "IndexMaxAutoCompactionSizeMB": {{AppSettings.PreBalancedDefaultIndexMaxAutoCompactionSizeMB}}, "IndexCoalesceMinRun": {{AppSettings.PreBalancedDefaultIndexCoalesceMinRun}} }""");
            AppSettings lifted = new SettingsService(temp).Load();
            Assert.Equal(AppSettings.DefaultIndexCompactionThresholdMB, lifted.IndexCompactionThresholdMB);
            Assert.Equal(AppSettings.DefaultIndexMaxAutoCompactionSizeMB, lifted.IndexMaxAutoCompactionSizeMB);
            Assert.Equal(AppSettings.DefaultIndexCoalesceMinRun, lifted.IndexCoalesceMinRun);
            Assert.True(lifted.IndexAutomaticCompactionDefaultsMigrated);

            File.WriteAllText(
                temp,
                """{ "IndexSizeDefaultsMigrated": true, "IndexStreamingMergeDefaultsMigrated": true, "IndexCompactionThresholdMB": 768, "IndexMaxAutoCompactionSizeMB": 0, "IndexCoalesceMinRun": 2 }""");
            AppSettings deliberate = new SettingsService(temp).Load();
            Assert.Equal(768, deliberate.IndexCompactionThresholdMB);
            Assert.Equal(0, deliberate.IndexMaxAutoCompactionSizeMB);
            Assert.Equal(2, deliberate.IndexCoalesceMinRun);

            File.WriteAllText(
                temp,
                $$"""{ "IndexSizeDefaultsMigrated": true, "IndexStreamingMergeDefaultsMigrated": true, "IndexAutomaticCompactionDefaultsMigrated": true, "IndexCompactionThresholdMB": {{AppSettings.PreBalancedDefaultIndexCompactionThresholdMB}}, "IndexMaxAutoCompactionSizeMB": {{AppSettings.PreBalancedDefaultIndexMaxAutoCompactionSizeMB}}, "IndexCoalesceMinRun": {{AppSettings.PreBalancedDefaultIndexCoalesceMinRun}} }""");
            AppSettings alreadyMigrated = new SettingsService(temp).Load();
            Assert.Equal(AppSettings.PreBalancedDefaultIndexCompactionThresholdMB, alreadyMigrated.IndexCompactionThresholdMB);
            Assert.Equal(AppSettings.PreBalancedDefaultIndexMaxAutoCompactionSizeMB, alreadyMigrated.IndexMaxAutoCompactionSizeMB);
            Assert.Equal(AppSettings.PreBalancedDefaultIndexCoalesceMinRun, alreadyMigrated.IndexCoalesceMinRun);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void StreamingMergeMigration_LiftsOnlyUnchangedPreviousDefaults()
    {
        string temp = Path.Combine(Path.GetTempPath(), "yagu-stream-migrate", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            File.WriteAllText(
                temp,
                $$"""{ "IndexSizeDefaultsMigrated": true, "IndexCoalesceMaxSegmentMB": {{AppSettings.PreStreamingDefaultIndexCoalesceMaxSegmentMB}}, "IndexCoalesceMaxBatchMB": {{AppSettings.PreStreamingDefaultIndexCoalesceMaxBatchMB}} }""");
            AppSettings lifted = new SettingsService(temp).Load();
            Assert.Equal(AppSettings.DefaultIndexCoalesceMaxSegmentMB, lifted.IndexCoalesceMaxSegmentMB);
            Assert.Equal(AppSettings.DefaultIndexCoalesceMaxBatchMB, lifted.IndexCoalesceMaxBatchMB);
            Assert.True(lifted.IndexStreamingMergeDefaultsMigrated);

            File.WriteAllText(
                temp,
                """{ "IndexSizeDefaultsMigrated": true, "IndexCoalesceMaxSegmentMB": 300, "IndexCoalesceMaxBatchMB": 1500 }""");
            AppSettings deliberate = new SettingsService(temp).Load();
            Assert.Equal(300, deliberate.IndexCoalesceMaxSegmentMB);
            Assert.Equal(1500, deliberate.IndexCoalesceMaxBatchMB);

            File.WriteAllText(
                temp,
                $$"""{ "IndexSizeDefaultsMigrated": true, "IndexStreamingMergeDefaultsMigrated": true, "IndexCoalesceMaxSegmentMB": {{AppSettings.PreStreamingDefaultIndexCoalesceMaxSegmentMB}} }""");
            AppSettings alreadyMigrated = new SettingsService(temp).Load();
            Assert.Equal(
                AppSettings.PreStreamingDefaultIndexCoalesceMaxSegmentMB,
                alreadyMigrated.IndexCoalesceMaxSegmentMB);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexMaxDeltaSegments)]
    [InlineData(-1, AppSettings.DefaultIndexMaxDeltaSegments)]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(9999, AppSettings.MaximumIndexMaxDeltaSegments)]
    public void NormalizeIndexMaxDeltaSegments_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxDeltaSegments(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexCompactionThresholdMB)]
    [InlineData(-5, AppSettings.DefaultIndexCompactionThresholdMB)]
    [InlineData(8, AppSettings.MinimumIndexCompactionThresholdMB)]
    [InlineData(256, 256)]
    [InlineData(99999, AppSettings.MaximumIndexCompactionThresholdMB)]
    public void NormalizeIndexCompactionThresholdMB_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexCompactionThresholdMB(input));

    [Theory]
    [InlineData(-1, AppSettings.DefaultIndexMaxAutoCompactionSizeMB)] // negative → default
    [InlineData(0, 0)]                                                 // 0 = no cap (kept)
    [InlineData(512, 512)]
    [InlineData(2048, 2048)]
    [InlineData(int.MaxValue, AppSettings.MaximumIndexMaxAutoCompactionSizeMB)]
    public void NormalizeIndexMaxAutoCompactionSizeMB_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxAutoCompactionSizeMB(input));

    [Theory]
    [InlineData("ManualFullRebuild", "ManualFullRebuild")]
    [InlineData("automaticincremental", "AutomaticIncremental")]
    [InlineData("AutomaticFullRebuildWhenDirty", "AutomaticFullRebuildWhenDirty")]
    [InlineData("bogus", "ManualFullRebuild")]
    public void NormalizeIndexUpdateMode_AcceptsIncremental(string input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexUpdateMode(input));

    [Fact]
    public void EffectiveArchitectureAwareDefaults_ArePositive()
    {
        var s = new AppSettings();
        Assert.True(s.EffectiveIndexQueryMemoryBudgetMB > 0);
        Assert.True(s.EffectiveIndexMaxDiskSizeMB > 0);
        Assert.True(s.EffectiveIndexBuildMemoryBudgetMB > 0);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(999, AppSettings.MaximumIndexWorkerParallelism)]
    public void NormalizeIndexWorkerParallelism_PreservesAutomaticAndClamps(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeIndexBuildWorkerParallelism(input));
        Assert.Equal(expected, AppSettings.NormalizeIndexQueryWorkerParallelism(input));
    }

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexQueryStartupBudgetMs)]
    [InlineData(-5, AppSettings.DefaultIndexQueryStartupBudgetMs)]
    [InlineData(10, AppSettings.MinimumIndexQueryStartupBudgetMs)]
    [InlineData(5000, AppSettings.MaximumIndexQueryStartupBudgetMs)]
    [InlineData(200, 200)]
    public void NormalizeIndexQueryStartupBudgetMs_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexQueryStartupBudgetMs(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexMaxCandidatePercent)]
    [InlineData(200, AppSettings.MaximumIndexMaxCandidatePercent)]
    [InlineData(1, 1)]
    public void NormalizeIndexMaxCandidatePercent_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxCandidatePercent(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexMaxFileSizeMB)]
    [InlineData(999999, AppSettings.MaximumIndexMaxFileSizeMB)]
    [InlineData(50, 50)]
    public void NormalizeIndexMaxFileSizeMB_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxFileSizeMB(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexRetainedGenerationCount)]
    [InlineData(1, 1)]
    [InlineData(999, AppSettings.MaximumIndexRetainedGenerationCount)]
    public void NormalizeIndexRetainedGenerationCount_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexRetainedGenerationCount(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexStaleTemporaryHours)]
    [InlineData(1, AppSettings.MinimumIndexStaleTemporaryHours)]
    [InlineData(9999, AppSettings.MaximumIndexStaleTemporaryHours)]
    public void NormalizeIndexStaleTemporaryHours_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexStaleTemporaryHours(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexQuarantineRetentionDays)]
    [InlineData(1, AppSettings.MinimumIndexQuarantineRetentionDays)]
    [InlineData(9999, AppSettings.MaximumIndexQuarantineRetentionDays)]
    public void NormalizeIndexQuarantineRetentionDays_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexQuarantineRetentionDays(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexIdleDelayMinutes)]
    [InlineData(1, AppSettings.MinimumIndexIdleDelayMinutes)]
    [InlineData(9999, AppSettings.MaximumIndexIdleDelayMinutes)]
    public void NormalizeIndexIdleDelayMinutes_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexIdleDelayMinutes(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexContinuousIntervalMinutes)]
    [InlineData(1, AppSettings.MinimumIndexContinuousIntervalMinutes)]
    [InlineData(9999, AppSettings.MaximumIndexContinuousIntervalMinutes)]
    public void NormalizeIndexContinuousIntervalMinutes_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexContinuousIntervalMinutes(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexMinimumFreeSpaceMB)]
    [InlineData(1, AppSettings.MinimumIndexMinimumFreeSpaceMB)]
    [InlineData(8192, 8192)]
    public void NormalizeIndexMinimumFreeSpaceMB_UsesDefaultAndFloor(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMinimumFreeSpaceMB(input));

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, AppSettings.MinimumIndexQueryMemoryBudgetMB)]
    [InlineData(99999, AppSettings.MaximumIndexQueryMemoryBudgetMB)]
    public void NormalizeIndexQueryMemoryBudgetMB_Clamps(int input, int expected)
        => Assert.Equal(expected < 0 ? AppSettings.DefaultIndexQueryMemoryBudgetMB : expected,
            AppSettings.NormalizeIndexQueryMemoryBudgetMB(input));

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, AppSettings.MinimumIndexBuildMemoryBudgetMB)]
    [InlineData(99999, AppSettings.MaximumIndexBuildMemoryBudgetMB)]
    public void NormalizeIndexBuildMemoryBudgetMB_Clamps(int input, int expected)
        => Assert.Equal(expected < 0 ? AppSettings.DefaultIndexBuildMemoryBudgetMB : expected,
            AppSettings.NormalizeIndexBuildMemoryBudgetMB(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexMaxJournalCatchupMB)]
    [InlineData(1, AppSettings.MinimumIndexMaxJournalCatchupMB)]
    [InlineData(99999, AppSettings.MaximumIndexMaxJournalCatchupMB)]
    public void NormalizeIndexMaxJournalCatchupMB_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxJournalCatchupMB(input));

    [Theory]
    [InlineData(0, AppSettings.DefaultIndexMaxJournalCatchupRecords)]
    [InlineData(1, AppSettings.MinimumIndexMaxJournalCatchupRecords)]
    [InlineData(int.MaxValue, AppSettings.MaximumIndexMaxJournalCatchupRecords)]
    public void NormalizeIndexMaxJournalCatchupRecords_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxJournalCatchupRecords(input));

    [Theory]
    [InlineData(-1, AppSettings.DefaultIndexPostBuildCatchUpThresholdChanges)]
    [InlineData(0, 0)]
    [InlineData(30_000, 30_000)]
    [InlineData(int.MaxValue, AppSettings.MaximumIndexPostBuildCatchUpThresholdChanges)]
    public void NormalizeIndexPostBuildCatchUpThresholdChanges_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexPostBuildCatchUpThresholdChanges(input));

    [Theory]
    [InlineData(-5, AppSettings.DefaultIndexMaxInProcessSizeMB)] // negative → default (2048)
    [InlineData(0, 0)]                                            // 0 is valid: never load in-process (always live-scan)
    [InlineData(2048, 2048)]
    [InlineData(int.MaxValue, AppSettings.MaximumIndexMaxInProcessSizeMB)]
    public void NormalizeIndexMaxInProcessSizeMB_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxInProcessSizeMB(input));

    [Theory]
    [InlineData(-5, AppSettings.DefaultIndexMaxWorkerQuerySizeMB)] // negative → default (30720 = 30 GB)
    [InlineData(0, 0)]                                             // 0 is valid: never use the worker (always live-scan)
    [InlineData(30720, 30720)]
    [InlineData(int.MaxValue, AppSettings.MaximumIndexMaxWorkerQuerySizeMB)]
    public void NormalizeIndexMaxWorkerQuerySizeMB_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(input));

    [Fact]
    public void Defaults_IndexSizeCaps_InProcess2Gb_Worker30Gb()
    {
        var s = new AppSettings();
        Assert.Equal(2048, s.IndexMaxInProcessSizeMB);                                   // in-process cap = 2 GB
        Assert.Equal(AppSettings.DefaultIndexMaxInProcessSizeMB, s.IndexMaxInProcessSizeMB);
        Assert.Equal(30720, s.IndexMaxWorkerQuerySizeMB);                                // out-of-process (worker) cap = 30 GB
        Assert.Equal(AppSettings.DefaultIndexMaxWorkerQuerySizeMB, s.IndexMaxWorkerQuerySizeMB);
    }

    [Fact]
    public void NormalizeIndexMaxDiskSizeMB_UsesDefaultAndFloor()
    {
        Assert.Equal(AppSettings.DefaultIndexMaxDiskSizeMB, AppSettings.NormalizeIndexMaxDiskSizeMB(0));
        Assert.Equal(AppSettings.MinimumIndexMaxDiskSizeMB, AppSettings.NormalizeIndexMaxDiskSizeMB(1));
        Assert.Equal(10000, AppSettings.NormalizeIndexMaxDiskSizeMB(10000));
    }

    [Fact]
    public void NormalizeIndexMaxDiskUsagePercent_DefaultsTo90AndClamps()
    {
        Assert.Equal(90, AppSettings.DefaultIndexMaxDiskUsagePercent);
        Assert.Equal(AppSettings.DefaultIndexMaxDiskUsagePercent, AppSettings.NormalizeIndexMaxDiskUsagePercent(0));
        Assert.Equal(AppSettings.MinimumIndexMaxDiskUsagePercent, AppSettings.NormalizeIndexMaxDiskUsagePercent(1));   // below floor
        Assert.Equal(AppSettings.MaximumIndexMaxDiskUsagePercent, AppSettings.NormalizeIndexMaxDiskUsagePercent(150)); // above ceiling
        Assert.Equal(85, AppSettings.NormalizeIndexMaxDiskUsagePercent(85));
    }

    [Theory]
    [InlineData("manual", "Manual")]
    [InlineData("WHENIDLE", "WhenIdle")]
    [InlineData("continuous", "Continuous")]
    [InlineData("AtStartup", "AtStartup")]
    [InlineData("onschedule", "OnSchedule")]
    [InlineData("bogus", "Manual")]
    [InlineData(null, "Manual")]
    // Several triggers can be combined; the result is de-duplicated and reordered canonically.
    [InlineData("AtStartup, OnSchedule", "AtStartup, OnSchedule")]
    [InlineData("onschedule atstartup", "AtStartup, OnSchedule")]
    [InlineData("OnSchedule+AtStartup+OnSchedule", "AtStartup, OnSchedule")]
    [InlineData("Manual, AtStartup", "AtStartup")]
    [InlineData("WhenEnabled, AtStartup, WhenIdle, Continuous, OnSchedule", "WhenEnabled, AtStartup, WhenIdle, Continuous, OnSchedule")]
    [InlineData("OnSchedule+Continuous+WhenIdle", "WhenIdle, Continuous, OnSchedule")]
    public void NormalizeIndexBuildTrigger_CoercesEnum(string? input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexBuildTrigger(input));

    [Theory]
    [InlineData("AtStartup, OnSchedule", "AtStartup", true)]
    [InlineData("AtStartup, OnSchedule", "OnSchedule", true)]
    [InlineData("AtStartup, OnSchedule", "WhenIdle", false)]
    [InlineData("Continuous, OnSchedule", "Continuous", true)]
    [InlineData("Manual", "AtStartup", false)]
    [InlineData("", "OnSchedule", false)]
    [InlineData(null, "OnSchedule", false)]
    [InlineData("onschedule", "OnSchedule", true)]
    public void IndexBuildTriggerHas_ChecksMembership(string? trigger, string flag, bool expected)
        => Assert.Equal(expected, AppSettings.IndexBuildTriggerHas(trigger, flag));

    [Theory]
    [InlineData("interval", "Interval")]
    [InlineData("WEEKLY", "Weekly")]
    [InlineData("bogus", "Interval")]
    [InlineData(null, "Interval")]
    public void NormalizeIndexScheduleMode_CoercesEnum(string? input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexScheduleMode(input));

    [Theory]
    [InlineData(0, 60)]        // unset → default
    [InlineData(1, 5)]         // below floor → clamped up
    [InlineData(60, 60)]
    [InlineData(999999, 10080)] // above ceiling → clamped to one week
    public void NormalizeIndexScheduleIntervalMinutes_Clamps(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexScheduleIntervalMinutes(input));

    [Theory]
    [InlineData(0, 127)]       // empty selection → every day
    [InlineData(0x7F, 0x7F)]
    [InlineData(0x1FF, 0x7F)]  // extra high bits stripped to 7 days
    [InlineData(0b0000101, 0b0000101)] // Sun + Tue
    public void NormalizeIndexScheduleDaysOfWeekMask_KeepsSevenBits(int input, int expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexScheduleDaysOfWeekMask(input));

    [Theory]
    [InlineData("03:30", "03:30")]
    [InlineData("3:30", "03:30")]
    [InlineData("23:59", "23:59")]
    [InlineData("nonsense", "03:00")]
    [InlineData("25:00", "03:00")]
    [InlineData(null, "03:00")]
    public void NormalizeIndexScheduleTimeOfDay_NormalizesOrDefaults(string? input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexScheduleTimeOfDay(input));

    [Theory]
    [InlineData("never", "Never")]
    [InlineData("EXPLICITROOTSONLY", "ExplicitRootsOnly")]
    [InlineData("weird", "Never")]
    public void NormalizeIndexRemovableDrivePolicy_CoercesEnum(string input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexRemovableDrivePolicy(input));

    [Theory]
    [InlineData("manualfullrebuild", "ManualFullRebuild")]
    [InlineData("AUTOMATICFULLREBUILDWHENDIRTY", "AutomaticFullRebuildWhenDirty")]
    [InlineData("AutomaticIncremental", "AutomaticIncremental")] // Phase 3 value now accepted
    [InlineData("bogus", "ManualFullRebuild")]
    [InlineData(null, "ManualFullRebuild")]
    public void NormalizeIndexUpdateMode_CoercesToV1Values(string? input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexUpdateMode(input));

    [Theory]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    [InlineData(@"  D:\idx  ", @"D:\idx")]
    public void NormalizeIndexStorageDirectory_TrimsAndBlanks(string? input, string expected)
        => Assert.Equal(expected, AppSettings.NormalizeIndexStorageDirectory(input));

    [Fact]
    public void ContentIndexActiveByDefault_RequiresMasterAndDefault()
    {
        Assert.False(new AppSettings { EnableContentIndex = false, UseContentIndexByDefault = true }.ContentIndexActiveByDefault);
        Assert.False(new AppSettings { EnableContentIndex = true, UseContentIndexByDefault = false }.ContentIndexActiveByDefault);
        Assert.True(new AppSettings { EnableContentIndex = true, UseContentIndexByDefault = true }.ContentIndexActiveByDefault);
    }

    [Fact]
    public void SaveLoad_RoundTripsAndNormalizesOnLoad()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "qg-index-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            svc.Save(new AppSettings
            {
                EnableContentIndex = true,
                UseContentIndexByDefault = false,
                SuppressIndexWarmSearchWarning = true,
                IndexQueryStartupBudgetMs = 999999,   // out of range → clamps on load
                IndexMaxCandidatePercent = 0,         // → default on load
                IndexBuildTrigger = "whenidle",       // → canonical casing on load
                IndexRemovableDrivePolicy = "explicitrootsonly",
                IndexScheduleMode = "weekly",          // → canonical casing on load
                IndexScheduleIntervalMinutes = 1,      // below floor → clamps to 5 on load
                IndexScheduleDaysOfWeekMask = 0x1FF,   // extra bits stripped to 7 on load
                IndexScheduleTimeOfDay = "3:30",       // → HH:mm on load
                IndexBuildWorkerParallelism = 999,      // → bounded explicit cap
                IndexQueryWorkerParallelism = -2,       // → automatic
                IndexPostBuildCatchUpThresholdChanges = int.MaxValue,
                IndexStorageDirectory = @"  E:\ix  ",
                IndexRetainedGenerationCount = 3,
                IndexStorageHistoryRetentionDays = 9999,
                IndexedRoots = new List<string> { @"C:\Projects", @"C:\Projects\", @"c:\projects", "  ", @"D:\Data" },
            });

            var loaded = svc.Load();
            Assert.True(loaded.EnableContentIndex);
            Assert.False(loaded.UseContentIndexByDefault);
            Assert.True(loaded.SuppressIndexWarmSearchWarning);
            Assert.Equal(AppSettings.MaximumIndexQueryStartupBudgetMs, loaded.IndexQueryStartupBudgetMs);
            Assert.Equal(AppSettings.DefaultIndexMaxCandidatePercent, loaded.IndexMaxCandidatePercent);
            Assert.Equal("WhenIdle", loaded.IndexBuildTrigger);
            Assert.Equal("ExplicitRootsOnly", loaded.IndexRemovableDrivePolicy);
            Assert.Equal("Weekly", loaded.IndexScheduleMode);
            Assert.Equal(AppSettings.MinimumIndexScheduleIntervalMinutes, loaded.IndexScheduleIntervalMinutes);
            Assert.Equal(0x7F, loaded.IndexScheduleDaysOfWeekMask);
            Assert.Equal("03:30", loaded.IndexScheduleTimeOfDay);
            Assert.Equal(AppSettings.MaximumIndexWorkerParallelism, loaded.IndexBuildWorkerParallelism);
            Assert.Equal(0, loaded.IndexQueryWorkerParallelism);
            Assert.Equal(
                AppSettings.MaximumIndexPostBuildCatchUpThresholdChanges,
                loaded.IndexPostBuildCatchUpThresholdChanges);
            Assert.Equal(@"E:\ix", loaded.IndexStorageDirectory);
            Assert.Equal(3, loaded.IndexRetainedGenerationCount);
            Assert.Equal(AppSettings.MaximumIndexStorageHistoryRetentionDays, loaded.IndexStorageHistoryRetentionDays);
            // IndexedRoots normalize + de-dup (case-insensitive, trailing sep) + drop blanks on load.
            Assert.Equal(new[] { @"C:\Projects", @"D:\Data" }, loaded.IndexedRoots);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_MigratesFormerMappedWorkerDefaultsOnce_ThenPreservesUserChoice(bool asyncLoad)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "qg-index-v3-defaults-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(tmp,
                "{\"IndexProduceV3QueryStructures\":false,\"IndexUseWorkerQuerySessions\":false}");
            var svc = new SettingsService(tmp);

            AppSettings migrated = asyncLoad ? await svc.LoadAsync() : svc.Load();

            Assert.True(migrated.IndexProduceV3QueryStructures);
            Assert.True(migrated.IndexUseWorkerQuerySessions);
            Assert.True(migrated.IndexMappedWorkerDefaultsMigrated);

            migrated.IndexProduceV3QueryStructures = false;
            migrated.IndexUseWorkerQuerySessions = false;
            svc.Save(migrated);

            AppSettings reloaded = asyncLoad ? await svc.LoadAsync() : svc.Load();
            Assert.False(reloaded.IndexProduceV3QueryStructures);
            Assert.False(reloaded.IndexUseWorkerQuerySessions);
            Assert.True(reloaded.IndexMappedWorkerDefaultsMigrated);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void SaveLoad_IndexedRootFilters_RoundTripAndNormalizeOnLoad()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "qg-index-filters-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            svc.Save(new AppSettings
            {
                IndexedRootFilters = new List<Yagu.Services.Index.IndexedRootFilter>
                {
                    new() { Path = @"C:\proj\", ExcludeGlobs = "  **/bin/**  " },    // trailing sep + trim
                    new() { Path = @"c:\proj", IncludeGlobs = "**/node_modules/**" }, // same path -> last wins
                    new() { Path = @"C:\empty", IncludeGlobs = "", ExcludeGlobs = "" }, // inert -> dropped
                },
            });

            var loaded = svc.Load();
            Assert.Single(loaded.IndexedRootFilters);
            var filter = loaded.IndexedRootFilters[0];
            Assert.Equal(@"c:\proj", filter.Path); // NormalizePath preserves case; last-wins keeps the last entry
            Assert.Equal("**/node_modules/**", filter.IncludeGlobs);
            Assert.Equal(string.Empty, filter.ExcludeGlobs);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ─────────────────────── SearchOptions.UseContentIndex ───────────────────────

    [Fact]
    public void SearchOptions_UseContentIndex_DefaultsFalse()
    {
        var options = new SearchOptions { Directory = @"C:\x", Query = "q" };
        Assert.False(options.UseContentIndex);
    }

    [Fact]
    public void CopyOptions_CarriesUseContentIndex()
    {
        // SearchService.CopyOptions must propagate UseContentIndex to ordinary sub-search clones while
        // allowing the tiny filename-priority prepass to explicitly disable index startup (so a cold index
        // can never delay an Everything filename hit).
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SearchService.cs"));
        Assert.Contains("UseContentIndex = useContentIndex ?? options.UseContentIndex,", src);
        Assert.Contains("ContentIndexGateFactory = useContentIndex == false ? null : options.ContentIndexGateFactory,", src);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
