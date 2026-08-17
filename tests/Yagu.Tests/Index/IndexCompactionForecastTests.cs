using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexCompactionForecastTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 20, 40, 0, TimeSpan.Zero);
    private static readonly string RepoRoot = FindRepoRoot();

    private static AppSettings AutomaticSettings() => new()
    {
        IndexUpdateMode = AppSettings.IndexUpdateModeAutomaticIncremental,
        IndexBuildTrigger = AppSettings.IndexBuildTriggerContinuous,
        IndexContinuousIntervalMinutes = 5,
    };

    private static EffectiveIndexSizePolicy Policy(
        int capMB = 8192,
        string mode = IndexSizeManagementModes.CoalesceThenCompact) => new(
        mode,
        SizeBudgetMB: 51200,
        MaxAutoCompactionSizeMB: capMB,
        CoalesceMaxSegmentMB: 1024,
        CoalesceMaxBatchMB: 4096,
        CoalesceMinRun: 3,
        CoalesceMaxRunsPerPass: 8);

    [Fact]
    public void MediumIndex_ForecastsAutomaticCompactionWithDateAndExplanation()
    {
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(
                BaseBytes: 4_755 * MiB, BaseCount: 1,
                FullBuildPagingBytes: 0, FullBuildPagingCount: 0,
                IncrementalBytes: 184 * MiB, IncrementalCount: 5),
            Now.AddMinutes(-25),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(), maxDeltaSegments: 8, compactionThresholdMB: 512,
            AutomaticSettings(), Now);

        Assert.Equal(IndexCompactionForecastKind.AutomaticCompaction, forecast.Kind);
        Assert.NotNull(forecast.EstimatedUtc);
        Assert.True(forecast.EstimatedUtc > Now);
        Assert.Contains("Estimated auto-compaction", forecast.Summary);
        Assert.Contains("within the 8,192 MiB", forecast.Details);
        Assert.Contains("smaller bounded coalescing pass", forecast.Details);
        Assert.Contains("remains open", forecast.Details);
    }

    [Fact]
    public void OversizedIndex_ForecastsAttentionInsteadOfAutomaticCompaction()
    {
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(
                BaseBytes: 25_861 * MiB, BaseCount: 1,
                FullBuildPagingBytes: 0, FullBuildPagingCount: 0,
                IncrementalBytes: 251 * MiB, IncrementalCount: 8),
            Now.AddMinutes(-51),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"C:\", trend, Policy(), maxDeltaSegments: 8, compactionThresholdMB: 512,
            AutomaticSettings(), Now);

        Assert.Equal(IndexCompactionForecastKind.CleanupAttentionLikely, forecast.Kind);
        Assert.Contains("Estimated cleanup warning", forecast.Summary);
        Assert.Contains("outside the 8,192 MiB", forecast.Details);
        Assert.Contains("ask what to do", forecast.Details);
    }

    /// <summary>
    /// Compaction also triggers on the active layer COUNT. An index that may not coalesce reaches that
    /// limit long before its size threshold, so the estimate must follow whichever fires first.
    /// </summary>
    [Fact]
    public void CompactOnlyIndex_ForecastsTheLayerLimitWhenItArrivesBeforeTheSizeThreshold()
    {
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(
                BaseBytes: 4_755 * MiB, BaseCount: 1,
                FullBuildPagingBytes: 0, FullBuildPagingCount: 0,
                IncrementalBytes: 20 * MiB, IncrementalCount: 5),
            Now.AddMinutes(-25),
            Now.AddMinutes(-5));

        IndexCompactionForecast compactOnly = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(mode: IndexSizeManagementModes.Compact),
            maxDeltaSegments: 8, compactionThresholdMB: 512, AutomaticSettings(), Now);

        // Four more 5-minute layers reach the 8-layer limit; the 512 MiB threshold is hours away.
        Assert.Equal(Now.AddMinutes(20), compactOnly.EstimatedUtc);
        Assert.Contains("8-layer clean-up limit", compactOnly.Details);

        IndexCompactionForecast coalescing = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(), maxDeltaSegments: 8, compactionThresholdMB: 512, AutomaticSettings(), Now);

        // Coalescing absorbs the layer trigger, so that index still forecasts the size threshold.
        Assert.True(coalescing.EstimatedUtc > compactOnly.EstimatedUtc);
        Assert.Contains("512 MiB full-cleanup threshold", coalescing.Details);
    }

    /// <summary>Full-build paging layers are not update history, but ShouldCompact still counts their
    /// bytes, so an index already over the threshold must not be forecast months out.</summary>
    [Fact]
    public void FullBuildPagingBytes_CountTowardTheCleanupThreshold()
    {
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(
                BaseBytes: 4_000 * MiB, BaseCount: 1,
                FullBuildPagingBytes: 600 * MiB, FullBuildPagingCount: 3,
                IncrementalBytes: 10 * MiB, IncrementalCount: 2),
            Now.AddMinutes(-20),
            Now.AddMinutes(-10));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"C:\", trend, Policy(), maxDeltaSegments: 8, compactionThresholdMB: 512,
            AutomaticSettings(), Now);

        // 600 + 10 MiB of active segments is already past the 512 MiB threshold: due on the next pass.
        Assert.Equal(Now.AddMinutes(5), forecast.EstimatedUtc);
        Assert.Contains("3 full-build page layer(s)", forecast.Details);
    }

    [Fact]
    public void OneLayer_CollectsMoreHistoryBeforeShowingADate()
    {
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(4 * GiB, 1, 0, 0, 20 * MiB, 1),
            Now.AddMinutes(-5),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(), 8, 512, AutomaticSettings(), Now);

        Assert.Equal(IndexCompactionForecastKind.CollectingHistory, forecast.Kind);
        Assert.Null(forecast.EstimatedUtc);
        Assert.Contains("collecting update history", forecast.Summary);
    }

    [Fact]
    public void ManualMaintenance_DoesNotPromiseADate()
    {
        AppSettings settings = AutomaticSettings();
        settings.IndexBuildTrigger = ContentIndexBuildScheduler.TriggerManual;
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(4 * GiB, 1, 0, 0, 184 * MiB, 5),
            Now.AddMinutes(-25),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(), 8, 512, settings, Now);

        Assert.Equal(IndexCompactionForecastKind.AutomaticMaintenanceOff, forecast.Kind);
        Assert.Null(forecast.EstimatedUtc);
        Assert.Contains("not scheduled", forecast.Summary);
    }

    [Fact]
    public void NonIncrementalMaintenance_DoesNotPromiseADate_EvenWithAnAutomaticTrigger()
    {
        AppSettings settings = AutomaticSettings();
        settings.IndexUpdateMode = AppSettings.DefaultIndexUpdateMode;
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(4 * GiB, 1, 0, 0, 184 * MiB, 5),
            Now.AddMinutes(-25),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(), 8, 512, settings, Now);

        Assert.Equal(IndexCompactionForecastKind.AutomaticMaintenanceOff, forecast.Kind);
        Assert.Null(forecast.EstimatedUtc);
    }

    [Fact]
    public void ZeroIncrementalBytes_CollectsMoreHistoryDespiteValidLayerTimes()
    {
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(4 * GiB, 1, 0, 0, 0, 2),
            Now.AddMinutes(-20),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(), 8, 512, AutomaticSettings(), Now);

        Assert.Equal(IndexCompactionForecastKind.CollectingHistory, forecast.Kind);
        Assert.Contains("do not yet span enough time", forecast.Details);
    }

    [Fact]
    public void AlreadyOverLayerLimit_CoalescesImmediately_AndUncappedPolicyStaysAutomatic()
    {
        AppSettings settings = AutomaticSettings();
        settings.IndexBuildTrigger = ContentIndexBuildScheduler.TriggerAtStartup;
        var trend = new ActiveLayerStorageTrend(
            new ActiveLayerStorageBreakdown(
                BaseBytes: 4 * GiB, BaseCount: 1,
                FullBuildPagingBytes: 0, FullBuildPagingCount: 0,
                IncrementalBytes: 90 * MiB, IncrementalCount: 9),
            Now.AddMinutes(-45),
            Now.AddMinutes(-5));

        IndexCompactionForecast forecast = IndexCompactionForecaster.Estimate(
            @"D:\", trend, Policy(capMB: 0), maxDeltaSegments: 8,
            compactionThresholdMB: 512, settings, Now);

        Assert.Equal(IndexCompactionForecastKind.AutomaticCompaction, forecast.Kind);
        Assert.Contains("uncapped automatic-compaction cap", forecast.Details);
        Assert.Contains("smaller bounded coalescing pass", forecast.Details);
        Assert.Contains("configured automatic trigger", forecast.Details);
        Assert.DoesNotContain("Continuous maintenance checks", forecast.Details);
    }

    [Fact]
    public void OverlayAndSettings_ShowTheSameSummaryAndTitlelessDetails()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.IndexStatusRefresh.cs"));
        string overlay = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.IndexOnboarding.cs"));
        string settings = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.IndexingActions.cs"));

        Assert.Contains("TryReadActiveLayerStorageTrendForRoot(indexRoot)", viewModel);
        Assert.Contains("IndexCompactionForecaster.Estimate(", viewModel);
        Assert.Contains("CompactionForecast: forecast", viewModel);

        Assert.Contains("Text = forecast.Summary", overlay);
        Assert.Contains("\"More details\"", overlay);
        Assert.Contains("ShowIndexCompactionForecastDetailsAsync", overlay);
        Assert.Contains("Text = forecast.Details", overlay);
        Assert.Contains("ShowTitleBar = false", overlay);

        Assert.Contains("_rootCompactionForecastByPath", settings);
        Assert.Contains("Text = forecast.Summary", settings);
        Assert.Contains("\"More details\"", settings);
        Assert.Contains("ShowIndexCompactionForecastDetailsAsync", settings);
        Assert.Contains("Text = forecast.Details", settings);
        Assert.Contains("ShowTitleBar = false", settings);
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Yagu.slnx")))
            directory = Directory.GetParent(directory)?.FullName;
        return directory ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}