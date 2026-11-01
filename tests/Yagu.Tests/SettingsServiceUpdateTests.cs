using Yagu.Services;
using Yagu.Services.Cli.MachineOutput;
using Xunit;

namespace Yagu.Tests;

public class SettingsServiceUpdateTests
{
    [Fact]
    public void IndexNormalizationHelpers_CoerceInvalidValuesAndAcceptDocumentedValues()
    {
        // CLI --index-config and the Settings page share these helpers; invalid persisted text must fall
        // back to documented defaults while valid user choices survive round-trip normalization.
        Assert.Equal(AppSettings.DefaultIndexMaxWorkerQuerySizeMB, AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(-1));
        Assert.Equal(0, AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(0));
        Assert.Equal(AppSettings.MaximumIndexMaxWorkerQuerySizeMB, AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(int.MaxValue));

        Assert.Equal(AppSettings.DefaultIndexBuildTrigger, AppSettings.NormalizeIndexBuildTrigger(" "));
        Assert.Equal("AtStartup, Continuous", AppSettings.NormalizeIndexBuildTrigger("continuous + atstartup + manual"));
        Assert.False(AppSettings.IndexBuildTriggerHas(null, "AtStartup"));
        Assert.False(AppSettings.IndexBuildTriggerHas("AtStartup", " "));
        Assert.True(AppSettings.IndexBuildTriggerHas("continuous; atstartup", "AtStartup"));
        Assert.False(AppSettings.IndexBuildTriggerHas("Continuous", "AtStartup"));

        Assert.Equal(AppSettings.DefaultIndexScheduleMode, AppSettings.NormalizeIndexScheduleMode("nope"));
        Assert.Equal(AppSettings.IndexScheduleModeWeekly, AppSettings.NormalizeIndexScheduleMode(" weekly "));
        Assert.Equal(AppSettings.DefaultIndexScheduleTimeOfDay, AppSettings.NormalizeIndexScheduleTimeOfDay("25:00"));
        Assert.Equal("09:30", AppSettings.NormalizeIndexScheduleTimeOfDay("09:30:45"));
        Assert.Equal(AppSettings.DefaultIndexRemovableDrivePolicy, AppSettings.NormalizeIndexRemovableDrivePolicy("sometimes"));
        Assert.Equal("ExplicitRootsOnly", AppSettings.NormalizeIndexRemovableDrivePolicy(" explicitrootsonly "));
        Assert.Equal(AppSettings.DefaultIndexUpdateMode, AppSettings.NormalizeIndexUpdateMode("unknown"));
        Assert.Equal(AppSettings.IndexUpdateModeAutomaticIncremental, AppSettings.NormalizeIndexUpdateMode(" automaticincremental "));
    }

    [Fact]
    public void EffectiveIndexPropertiesReflectNormalizedPersistedValues()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = true,
            UseContentIndexByDefault = true,
            IndexQueryMemoryBudgetMB = -1,
            IndexMaxDiskSizeMB = -1,
            IndexMaxDiskUsagePercent = 500,
            IndexBuildMemoryBudgetMB = -1,
        };

        // The UI reads the effective properties directly; they must never expose raw invalid persisted
        // values after a hand edit to settings.json.
        Assert.Equal(AppSettings.DefaultIndexQueryMemoryBudgetMB, settings.EffectiveIndexQueryMemoryBudgetMB);
        Assert.Equal(AppSettings.DefaultIndexMaxDiskSizeMB, settings.EffectiveIndexMaxDiskSizeMB);
        Assert.Equal(AppSettings.MaximumIndexMaxDiskUsagePercent, settings.EffectiveIndexMaxDiskUsagePercent);
        Assert.Equal(AppSettings.DefaultIndexBuildMemoryBudgetMB, settings.EffectiveIndexBuildMemoryBudgetMB);
        Assert.True(settings.ContentIndexActiveByDefault);
    }

    [Fact]
    public void ResetMcpServerSettings_RestoresEveryMcpDefault()
    {
        var settings = new AppSettings
        {
            McpAllowedRoots = [@"C:\work"],
            McpCapabilities = ["search"],
            McpMaxConcurrentCalls = 99,
            McpMaxLineBytes = 1,
            McpMaxContextLineBytes = 1,
            McpMaxPathBytes = 1,
            McpMaxContextLines = 1,
            McpMaxRecords = 1,
            McpMaxResponseBytes = 1,
            McpHttpEnabled = true,
            McpHttpPort = 12345,
            McpMaxExportResults = 1,
            McpMaxRetainedJobs = 1,
            McpMaxRunningJobs = 1,
            McpMaxJobResultChars = 1,
            McpCallTimeoutSeconds = 1,
            McpMaintenanceTimeoutMinutes = 1,
            McpModelTimeoutMinutes = 1,
            McpStartAtLogin = true,
            McpAllowReplaceWrites = true,
        };

        settings.ResetMcpServerSettings();

        // Reset must remove every authorization and return resource ceilings to safe defaults; otherwise
        // a "reset server" action can leave stale write permissions or unusably tiny responses behind.
        Assert.Empty(settings.McpAllowedRoots);
        Assert.Equal(AppSettings.CreateDefaultMcpCapabilities(), settings.McpCapabilities);
        Assert.Equal(AppSettings.DefaultMcpMaxConcurrentCalls, settings.McpMaxConcurrentCalls);
        Assert.Equal(MachineOutputBudget.Default.MaxLineBytes, settings.McpMaxLineBytes);
        Assert.Equal(MachineOutputBudget.Default.MaxContextLineBytes, settings.McpMaxContextLineBytes);
        Assert.Equal(MachineOutputBudget.Default.MaxPathBytes, settings.McpMaxPathBytes);
        Assert.Equal(MachineOutputBudget.Default.MaxContextLines, settings.McpMaxContextLines);
        Assert.Equal(MachineOutputBudget.Default.MaxRecords, settings.McpMaxRecords);
        Assert.Equal(MachineOutputBudget.Default.MaxResponseBytes, settings.McpMaxResponseBytes);
        Assert.False(settings.McpHttpEnabled);
        Assert.Equal(AppSettings.DefaultMcpHttpPort, settings.McpHttpPort);
        Assert.Equal(AppSettings.DefaultMcpMaxExportResults, settings.McpMaxExportResults);
        Assert.Equal(AppSettings.DefaultMcpMaxRetainedJobs, settings.McpMaxRetainedJobs);
        Assert.Equal(AppSettings.DefaultMcpMaxRunningJobs, settings.McpMaxRunningJobs);
        Assert.Equal(AppSettings.DefaultMcpMaxJobResultChars, settings.McpMaxJobResultChars);
        Assert.Equal(AppSettings.DefaultMcpCallTimeoutSeconds, settings.McpCallTimeoutSeconds);
        Assert.Equal(AppSettings.DefaultMcpMaintenanceTimeoutMinutes, settings.McpMaintenanceTimeoutMinutes);
        Assert.Equal(AppSettings.DefaultMcpModelTimeoutMinutes, settings.McpModelTimeoutMinutes);
        Assert.False(settings.McpStartAtLogin);
        Assert.False(settings.McpAllowReplaceWrites);
    }

    [Fact]
    public async Task LoadAsync_ValidSettingsFileReturnsTheNormalizedSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-load-async-good-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "ThemeModeIndex": 2, "ExcludeGlobs": null }""");

            AppSettings loaded = await new SettingsService(path).LoadAsync();

            // The async startup path must apply the same normalization as the sync loader.
            Assert.Equal(2, loaded.ThemeModeIndex);
            Assert.Equal(AppSettings.DefaultExcludeGlobs, loaded.ExcludeGlobs);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_MigratesIndexWorkerAndStreamingCompactionDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-index-default-lifts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(
                path,
                $$"""
                {
                  "IndexMappedWorkerDefaultsMigrated": false,
                  "IndexProduceV3QueryStructures": false,
                  "IndexUseWorkerQuerySessions": false,
                  "IndexStreamingMergeDefaultsMigrated": false,
                  "IndexCoalesceMaxSegmentMB": {{AppSettings.PreStreamingDefaultIndexCoalesceMaxSegmentMB}},
                  "IndexCoalesceMaxBatchMB": {{AppSettings.PreStreamingDefaultIndexCoalesceMaxBatchMB}},
                  "IndexAutomaticCompactionDefaultsMigrated": false,
                  "IndexCompactionThresholdMB": {{AppSettings.PreBalancedDefaultIndexCompactionThresholdMB}},
                  "IndexMaxAutoCompactionSizeMB": {{AppSettings.PreBalancedDefaultIndexMaxAutoCompactionSizeMB}},
                  "IndexCoalesceMinRun": {{AppSettings.PreBalancedDefaultIndexCoalesceMinRun}}
                }
                """);

            AppSettings loaded = new SettingsService(path).Load();

            // Old profiles with all legacy index defaults need the safe worker/v3 and compaction lifts;
            // otherwise a whole-drive index can stay over budget forever after upgrade.
            Assert.True(loaded.IndexProduceV3QueryStructures);
            Assert.True(loaded.IndexUseWorkerQuerySessions);
            Assert.True(loaded.IndexMappedWorkerDefaultsMigrated);
            Assert.Equal(AppSettings.DefaultIndexCoalesceMaxSegmentMB, loaded.IndexCoalesceMaxSegmentMB);
            Assert.Equal(AppSettings.DefaultIndexCoalesceMaxBatchMB, loaded.IndexCoalesceMaxBatchMB);
            Assert.True(loaded.IndexStreamingMergeDefaultsMigrated);
            Assert.Equal(AppSettings.DefaultIndexCompactionThresholdMB, loaded.IndexCompactionThresholdMB);
            Assert.Equal(AppSettings.DefaultIndexMaxAutoCompactionSizeMB, loaded.IndexMaxAutoCompactionSizeMB);
            Assert.Equal(AppSettings.DefaultIndexCoalesceMinRun, loaded.IndexCoalesceMinRun);
            Assert.True(loaded.IndexAutomaticCompactionDefaultsMigrated);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ContentIndexLiveScanWarningDismissals_NullInputNormalizesToEmpty()
    {
        // A null list from an older settings snapshot must mean "no dismissed roots", not crash the
        // settings load path before the user can clear or rebuild their index.
        Assert.Empty(AppSettings.NormalizeContentIndexLiveScanWarningDismissedRoots(null));
    }

    [Fact]
    public void Constructor_WithAnUnparseablePathStillKeepsTheOriginalSettingsPath()
    {
        // Bad command-line input can be reported safely later; constructing the service must not throw
        // just because Windows refuses to canonicalize a path-shaped string.
        var service = new SettingsService("\0bad-settings-path");

        Assert.Equal("\0bad-settings-path", service.SettingsPath);
    }

    [Fact]
    public async Task LoadAsync_MalformedSettingsFileFallsBackToDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-load-async-bad-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json");

            AppSettings loaded = await new SettingsService(path).LoadAsync();

            // A single bad character in settings.json must not make Yagu unlaunchable.
            Assert.Empty(loaded.RecentDirectories);
            Assert.Equal(AppSettings.DefaultExcludeGlobs, loaded.ExcludeGlobs);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_NullAutomaticCompactionRetryStateNormalizesToEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-null-compaction-retry-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "IndexAutomaticCompactionRetryAfterUtcByRoot": null }""");

            AppSettings loaded = new SettingsService(path).Load();

            // A null retry map from a hand-edited file must not keep automatic compaction disabled.
            Assert.Empty(loaded.IndexAutomaticCompactionRetryAfterUtcByRoot);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_BlankPreviewShowMoreColorUsesTheDocumentedDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-preview-more-color-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "PreviewShowMoreEllipsisColor": "   " }""");

            AppSettings loaded = new SettingsService(path).Load();

            // A blank persisted color should fall back to a readable theme color instead of persisting an
            // invisible or unparsable preview affordance.
            Assert.Equal(AppSettings.DefaultPreviewShowMoreEllipsisColor, loaded.PreviewShowMoreEllipsisColor);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task UpdateAsync_MissingFileCreatesDefaultsBeforeApplyingTheUpdate()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-update-missing-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SettingsService(path);

            bool updated = await service.UpdateAsync(settings => settings.TelemetryConsentPromptShown = true);

            // A first-run consent change must be committed without requiring a broad Save that could
            // materialize stale in-memory search-session state.
            Assert.True(updated);
            Assert.True(File.Exists(path));
            Assert.True(service.Load().TelemetryConsentPromptShown);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task UpdateAsync_AfterCommitFailureDoesNotRollBackTheSavedSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), "qg-settings-update-aftercommit-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SettingsService(path);

            bool updated = await service.UpdateAsync(
                settings => settings.TelemetryConsentPromptShown = true,
                afterCommit: () => throw new InvalidOperationException("view-model refresh failed"));

            // The disk commit is authoritative: an in-memory refresh failure should be logged but must
            // not turn a successful persisted update into a false failure for the caller.
            Assert.True(updated);
            Assert.True(service.Load().TelemetryConsentPromptShown);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void CommitTempFile_RetriesTransientFailuresBeforeSucceeding()
    {
        int attempts = 0;
        var delays = new List<int>();

        SettingsService.CommitTempFile(
            "settings.tmp",
            "settings.json",
            (string _, string _, bool isLastAttempt, out Exception? error) =>
            {
                attempts++;
                error = attempts == 1 ? new IOException("scanner lock") : null;
                Assert.False(isLastAttempt);
                return attempts > 1;
            },
            delays.Add);

        // A transient replace failure from a scanner must be retried instead of silently dropping the save.
        Assert.Equal(2, attempts);
        Assert.Equal([SettingsService.CommitRetryDelayMs(1)], delays);
    }

    [Fact]
    public async Task CommitTempFileAsync_RetriesTransientFailuresBeforeSucceeding()
    {
        int attempts = 0;
        var delays = new List<int>();

        await SettingsService.CommitTempFileAsync(
            "settings.tmp",
            "settings.json",
            (string _, string _, bool isLastAttempt, out Exception? error) =>
            {
                attempts++;
                error = attempts == 1 ? new IOException("scanner lock") : null;
                Assert.False(isLastAttempt);
                return attempts > 1;
            },
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        // Async saves share the same retry contract as sync saves so a short-lived reader cannot lose a
        // settings change made from the UI.
        Assert.Equal(2, attempts);
        Assert.Equal([SettingsService.CommitRetryDelayMs(1)], delays);
    }
}
