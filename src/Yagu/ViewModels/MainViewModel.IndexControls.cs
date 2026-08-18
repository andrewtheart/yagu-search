using Yagu.Services;
using Yagu.Services.Index;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// User-facing index controls: removing a folder from the index, background builds, pause/resume,
/// turning the index off or on for this run or persistently, automatic-indexing presets, and the
/// build-activity progress/status indicators.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Unregisters <paramref name="folder"/> from the content-index roots (the inverse of
    /// <see cref="AddFolderToIndexAndBuildAsync"/>) and persists settings. This only removes it from the
    /// auto-index list — it does NOT delete any already-built on-disk index data (that is managed from
    /// Settings ▸ Indexing), matching the "Remove selected folder" behavior there. Never throws.
    /// </summary>
    public async Task RemoveFolderFromIndexAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        string root = folder.Trim();

        if (!IndexedRootsPolicy.Contains(_settings.IndexedRoots, root))
            return;

        _settings.IndexedRoots = IndexedRootsPolicy.Remove(_settings.IndexedRoots, root);
        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));
        RefreshAllDriveIndexStatus();

        YaguLog.For("ContentIndex").LogInformation("Unregistered '{Root}' from the content-index roots.", root);
    }

    /// <summary>
    /// Starts a cancellable background build of <paramref name="folder"/> (using the shared
    /// <see cref="IndexBuildCancellationToken"/> so a right-click pause stops it), and brackets it with the
    /// "Indexing…" indicator activity. Never throws; a failure or pause only logs.
    /// </summary>
    private void StartBackgroundIndexBuild(string folder, bool rebuild = false)
    {
        string root = folder.Trim();
        if (_shutdownRequested || root.Length == 0)
            return;

        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(_settings, root, rebuild);
        bool useWorker = _settings.IndexUseNativeWorker;
        CancellationToken token = IndexBuildCancellationToken;
        // Denominator for the "% complete" estimate: the used space of the drive this root lives on
        // (cheap, no pre-count). Captured once here on the UI thread; the build reports crawled bytes.
        long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);

        BeginIndexBuildActivity(root);
        _ = Task.Run(async () =>
        {
            try
            {
                var coordinator = new IndexBuildCoordinator();
                await coordinator.BuildFullScopePreferWorkerAsync(
                    operation,
                    useWorker,
                    token,
                    progress: p => ReportIndexBuildProgress(root, IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes), IndexBuildStages.RawBuild),
                    pdfProgress: p => ReportIndexBuildProgress(root, p.Total <= 0 ? -1 : 90 + Math.Clamp(p.Processed * 5 / p.Total, 0, 5), IndexBuildStages.Pdf),
                    imageOcrProgress: p => ReportIndexBuildProgress(root, p.Total <= 0 ? -1 : 95 + Math.Clamp(p.Processed * 4 / p.Total, 0, 4), IndexBuildStages.Ocr),
                    postBuildCatchUpProgress: _ => ReportIndexBuildProgress(root, 99, IndexBuildStages.PostBuildCatchUp));
                _dispatcher.TryEnqueue(() => _ = ClearAutomaticCompactionBackoffAsync(root));
                YaguLog.For("ContentIndex").LogInformation(
                    "Background index {Action} complete for '{Root}'.", rebuild ? "rebuild" : "build", root);
            }
            catch (OperationCanceledException)
            {
                YaguLog.For("ContentIndex").LogInformation("Background index build for '{Root}' was paused/cancelled.", root);
            }
            catch (IndexDiskFullException ex)
            {
                YaguLog.For("ContentIndex").LogWarning("Background index build for '{Root}' stopped: {Error}", root, ex.Message);
                OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
            }
            catch (IndexWriteBusyException)
            {
                YaguLog.For("ContentIndex").LogInformation("Background index build for '{Root}' skipped because another index operation is running.", root);
            }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "Background index build failed for '{Root}'.", root);
            }
            finally
            {
                EndIndexBuildActivity();
            }
        });
    }

    /// <summary>
    /// Pauses indexing (from the status-bar indicator's right-click menu): cancels the running tracked
    /// build(s) and holds off auto/startup/watcher builds until <see cref="ResumeIndexing"/>. Safe from any
    /// thread. Session-only — a relaunch starts unpaused.
    /// </summary>
    public void PauseIndexing()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(PauseIndexing);
            return;
        }
        if (IsIndexingPaused)
            return;

        IsIndexingPaused = true;
        _pausedIndexBuildFolder = _activeIndexBuildFolder;
        _indexBuildCancellation?.Cancel();
        YaguLog.For("ContentIndex").LogInformation("User paused indexing.");
        ShowIndexBuildingStatus();
        OnPropertyChanged(nameof(CanPauseIndexing));
    }

    /// <summary>How long <see cref="CancelActiveIndexBuildForReplacementAsync"/> waits for the cancelled
    /// operation to release its worker lease before giving up.</summary>
    public static readonly TimeSpan IndexBuildDrainTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Stops the currently tracked index operation so an explicitly approved rebuild can replace it.
    /// Unlike <see cref="PauseIndexing"/>, this does not enter the paused state or re-kick the cancelled
    /// operation. It waits until the operation has released its worker lease, then rotates the shared
    /// cancellation source so the replacement receives a fresh token. Returns false when the operation did
    /// not drain within <see cref="IndexBuildDrainTimeout"/>, leaving the shared source untouched so the
    /// caller can abandon the replacement instead of racing the single writer.
    /// </summary>
    public async Task<bool> CancelActiveIndexBuildForReplacementAsync(CancellationToken cancellationToken)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await CancelActiveIndexBuildForReplacementAsync(cancellationToken).ConfigureAwait(true));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }))
            {
                throw new InvalidOperationException("Could not dispatch index cancellation to the UI thread.");
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (IsIndexBuildActive)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Stopping the active index operation before an explicitly approved replacement rebuild.");
            _indexBuildCancellation?.Cancel();
            DateTimeOffset deadline = DateTimeOffset.UtcNow + IndexBuildDrainTimeout;
            while (IsIndexBuildActive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    YaguLog.For("ContentIndex").LogWarning(
                        "The active index operation did not stop within {Seconds}s; the replacement rebuild was abandoned.",
                        (int)IndexBuildDrainTimeout.TotalSeconds);
                    return false;
                }
                await Task.Delay(50, cancellationToken).ConfigureAwait(true);
            }
        }

        _indexBuildCancellation?.Dispose();
        _indexBuildCancellation = null;
        return true;
    }

    /// <summary>
    /// Resumes indexing after a pause: clears the pause, replaces the cancellation source, and re-starts the
    /// build for the folder that was building when paused (if any). Safe from any thread.
    /// </summary>
    public void ResumeIndexing()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(ResumeIndexing);
            return;
        }
        if (!IsIndexingPaused)
            return;

        IsIndexingPaused = false;
        _indexBuildCancellation?.Dispose();
        _indexBuildCancellation = null;
        string? folder = _pausedIndexBuildFolder;
        _pausedIndexBuildFolder = null;
        _indexDiskFullMessage = null;
        YaguLog.For("ContentIndex").LogInformation("User resumed indexing.");
        OnPropertyChanged(nameof(CanPauseIndexing));

        if (!string.IsNullOrWhiteSpace(folder))
        {
            StartBackgroundIndexBuild(folder!);
        }
        else if (IndexedRootsPolicy.Normalize(_settings.IndexedRoots).Count > 0 && ResumeAutoIndexBuildAsync is { } resumeAutoBuild)
        {
            // The paused build was a multi-root auto/startup/scheduled pass with no single tracked folder.
            // Reset the indicator baseline, then re-run that pass over the registered folders via the
            // view-installed hook so a resume actually resumes (it skips folders whose index is already
            // fresh, and re-shows "Indexing…" as soon as it starts) instead of just clearing the indicator.
            RevertIndexIndicatorAfterBuild();
            _ = resumeAutoBuild();
        }
        else
        {
            RevertIndexIndicatorAfterBuild();
        }
    }

    /// <summary>
    /// Stops using the content index for searches for the rest of this session WITHOUT changing the saved
    /// setting — the feature and any registered roots stay, and a relaunch uses the index again. Backs the
    /// status indicator's "Disable index ▸ Disable index (this run)" command. Safe from any thread.
    /// </summary>
    public void DisableContentIndexThisRun()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(DisableContentIndexThisRun);
            return;
        }
        if (_disposed)
            return;

        UseContentIndex = false;
        YaguLog.For("ContentIndex").LogInformation("Status menu: disabled content-index use for this session (not persisted).");
        StatusText = "Content index off for this session — it will be used again next launch.";
        _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, false);
    }

    /// <summary>
    /// Turns the content-index feature OFF and SAVES the setting so it stays off across launches. Cancels any
    /// running tracked build and hides the status indicator. Registered roots and the index files on disk are
    /// kept, so re-enabling in Settings ▸ Indexing restores them. Backs the status indicator's
    /// "Disable index ▸ Disable indexing (persistent)" command. Safe from any thread.
    /// </summary>
    public async Task DisableContentIndexPersistentlyAsync()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = DisableContentIndexPersistentlyAsync());
            return;
        }
        if (_disposed)
            return;

        // Stop any in-flight tracked build promptly, then clear the paused state (we are turning it off,
        // not pausing).
        _indexBuildCancellation?.Cancel();
        IsIndexingPaused = false;

        _settings.EnableContentIndex = false;
        UseContentIndex = false;
        await PersistSettingsAsync().ConfigureAwait(true);

        Interlocked.Increment(ref _allDriveIndexHealthRefreshGeneration);
        _allDriveIndexHealth = Array.Empty<IndexRootHealthEntry>();
        AllDriveIndexStatusText = string.Empty;

        // Keep a muted "Index: off" indicator this session so the status menu (which now offers "Enable
        // indexing") stays reachable — otherwise the user could only re-enable via Settings ▸ Indexing.
        _indexOffIndicatorSticky = true;
        ShowIndexDisabledIndicator();
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        YaguLog.For("ContentIndex").LogInformation("Status menu: disabled content indexing persistently.");
        StatusText = "Content indexing turned off. Right-click ▸ Enable indexing to turn it back on.";
    }

    /// <summary>
    /// Re-enables using the content index for searches this session after "Disable index (this run)". Sets
    /// the session <see cref="UseContentIndex"/> flag back on without touching saved settings. Backs the
    /// status indicator's "Use index (this run)" command. Safe from any thread.
    /// </summary>
    public void EnableContentIndexThisRun()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(EnableContentIndexThisRun);
            return;
        }
        if (_disposed)
            return;

        UseContentIndex = true;
        YaguLog.For("ContentIndex").LogInformation("Status menu: re-enabled content-index use for this session.");
        StatusText = "Content index on for this session.";
        _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, UseContentIndex && _settings.EnableContentIndex);
        RefreshAllDriveIndexStatus();
    }

    /// <summary>
    /// Turns the content-index feature back ON and SAVES it after "Disable indexing (persistent)". Clears
    /// the sticky "Index: off" indicator and refreshes the status. Registered folders and their on-disk
    /// indexes were kept, so they become usable again immediately. Backs the status indicator's
    /// "Enable indexing" command. Safe from any thread.
    /// </summary>
    public async Task EnableContentIndexFromStatusMenuAsync()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = EnableContentIndexFromStatusMenuAsync());
            return;
        }
        if (_disposed)
            return;

        _settings.EnableContentIndex = true;
        UseContentIndex = true;
        _indexOffIndicatorSticky = false;
        await PersistSettingsAsync().ConfigureAwait(true);

        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        YaguLog.For("ContentIndex").LogInformation("Status menu: re-enabled content indexing (persistent).");
        StatusText = "Content indexing turned on.";
        _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, UseContentIndex && _settings.EnableContentIndex);
        RefreshAllDriveIndexStatus();
    }

    /// <summary>
    /// Applies and immediately persists one of the simple automatic-indexing presets offered by the
    /// main-window status overlay. If automatic passes were still configured to build only missing
    /// indexes, upgrades them to incremental maintenance so existing indexes are kept current.
    /// </summary>
    public async Task SetAutomaticIndexingPresetAsync(string trigger)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = SetAutomaticIndexingPresetAsync(trigger));
            return;
        }
        if (_disposed)
            return;

        bool supported =
            string.Equals(trigger, ContentIndexBuildScheduler.TriggerContinuous, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, ContentIndexBuildScheduler.TriggerWhenIdle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, ContentIndexBuildScheduler.TriggerAtStartup, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, ContentIndexBuildScheduler.TriggerOnSchedule, StringComparison.OrdinalIgnoreCase);
        if (!supported)
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown automatic-indexing preset.");

        string normalizedTrigger = AppSettings.NormalizeIndexBuildTrigger(trigger);
        _settings.IndexBuildTrigger = normalizedTrigger;
        if (string.Equals(
                AppSettings.NormalizeIndexUpdateMode(_settings.IndexUpdateMode),
                AppSettings.DefaultIndexUpdateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            _settings.IndexUpdateMode = AppSettings.IndexUpdateModeAutomaticIncremental;
        }

        await PersistSettingsAsync().ConfigureAwait(true);

        YaguLog.For("ContentIndex").LogInformation(
            "Status overlay: automatic indexing saved with trigger {Trigger} and update mode {UpdateMode}.",
            _settings.IndexBuildTrigger,
            _settings.IndexUpdateMode);
        StatusText = ContentIndexUiStatus.SchedulingHint(_settings.IndexBuildTrigger) + " Setting saved.";
        RefreshCurrentIndexStatus();
        RefreshAllDriveIndexStatus();

        if (AppSettings.IndexBuildTriggerHas(
                normalizedTrigger,
                ContentIndexBuildScheduler.TriggerContinuous)
            && RequestIdleIndexMaintenanceAsync is { } requestMaintenance)
        {
            _ = requestMaintenance();
        }
    }

    /// <summary>Shows the muted "Index: off" status indicator (used after a menu-driven persistent disable),
    /// unless the user has turned the indicator off entirely in settings.</summary>
    private void ShowIndexDisabledIndicator()
    {
        if (!_settings.ShowIndexStatusInMainWindow)
        {
            ShowIndexStatus = false;
            return;
        }
        IndexStatusGlyph = "\uEA39"; // Blocked
        IndexStatusText = "Index: off";
        IndexStatusTooltip = "Content indexing is off. Right-click \u25B8 Enable indexing to turn it back on."
            + BuildIndexDateDetails();
        ShowIndexStatus = true;
    }

    /// <summary>
    /// Called when an index build is stopped because the index drive reached its used-space limit
    /// (plan §11.2). Auto-pauses indexing (so auto/watcher builds don't immediately retry) and shows a
    /// disk-full warning in the status-bar indicator. The user frees space then right-clicks ▸ Resume.
    /// Safe from any thread.
    /// </summary>
    public void OnIndexBuildStoppedForDiskSpace(string driveDisplayName, double usedPercent, int thresholdPercent)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnIndexBuildStoppedForDiskSpace(driveDisplayName, usedPercent, thresholdPercent));
            return;
        }

        _indexDiskFullMessage =
            $"Indexing stopped: {driveDisplayName} is {usedPercent:F0}% full (limit {thresholdPercent}%). "
            + "Free disk space, then right-click ▸ Resume indexing — or raise the limit in Settings ▸ Indexing.";
        if (!IsIndexingPaused)
        {
            IsIndexingPaused = true;
            _pausedIndexBuildFolder = _activeIndexBuildFolder;
        }
        YaguLog.For("ContentIndex").LogWarning(
            "Indexing stopped for disk space: {Drive} {UsedPercent:F1}% full (limit {ThresholdPercent}%).",
            driveDisplayName, usedPercent, thresholdPercent);
        ShowIndexBuildingStatus();
        OnPropertyChanged(nameof(CanPauseIndexing));
    }

    /// <summary>
    /// Marks that a background index build has started and shows an "Indexing…" state in the main-window
    /// index indicator (overriding availability/coverage until every active build finishes). Safe to call
    /// from any thread. Each call MUST be paired with <see cref="EndIndexBuildActivity"/>.
    /// </summary>
    public void BeginIndexBuildActivity(string? folder = null, bool isIncremental = false)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => BeginIndexBuildActivity(folder, isIncremental));
            return;
        }

        _activeIndexBuilds++;
        if (!string.IsNullOrWhiteSpace(folder))
            _activeIndexBuildFolder = folder;
        _activeIndexBuildIsIncremental = isIncremental;
        _activeIndexBuildPhase = isIncremental ? IndexUpdateStages.Incremental : IndexBuildStages.RawBuild;
        _indexBuildPercent = -1; // fresh build starts at an unknown estimate
        ShowIndexBuildingStatus();
        OnPropertyChanged(nameof(IsIndexBuildActive));
        OnPropertyChanged(nameof(ActiveIndexBuildStage));
        OnPropertyChanged(nameof(IsActiveIndexBuildIncremental));
        OnPropertyChanged(nameof(CanPauseIndexing));
    }

    /// <summary>
    /// Updates the estimated percent-complete (0–100, or -1 for unknown) shown at the end of the "Indexing…"
    /// tooltip. Called periodically from a running build (off the UI thread), so it self-marshals and only
    /// refreshes the tooltip when the value actually changed and a build is still active and unpaused.
    /// </summary>
    public void ReportIndexBuildProgress(int percent) => ReportIndexBuildProgress(null, percent, null);

    /// <summary>
    /// Reports which folder a multi-root pass is currently indexing (so the tooltip names the drive) together
    /// with its percent-complete. Passing a non-empty <paramref name="folder"/> updates the active folder
    /// without changing the active-build count; <paramref name="percent"/> is the 0–100 estimate (or -1 when
    /// unknown). Self-marshals; a late report after the build finished is ignored.
    /// </summary>
    public void ReportIndexBuildProgress(string? folder, int percent) => ReportIndexBuildProgress(folder, percent, null);

    /// <summary>Reports folder, progress, and worker stage. The incremental stage is retained so the status
    /// bar says the existing index is being updated rather than implying a full rebuild.</summary>
    public void ReportIndexBuildProgress(string? folder, int percent, string? stage)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ReportIndexBuildProgress(folder, percent, stage));
            return;
        }

        if (_activeIndexBuilds <= 0)
            return; // build finished (or none active) — ignore a late report

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(folder)
            && !string.Equals(folder, _activeIndexBuildFolder, StringComparison.OrdinalIgnoreCase))
        {
            _activeIndexBuildFolder = folder;
            changed = true;
        }
        if (percent != _indexBuildPercent)
        {
            _indexBuildPercent = percent;
            changed = true;
        }
        bool incremental = IndexUpdateStages.IsIncremental(stage);
        if (stage is not null && incremental != _activeIndexBuildIsIncremental)
        {
            _activeIndexBuildIsIncremental = incremental;
            OnPropertyChanged(nameof(IsActiveIndexBuildIncremental));
            changed = true;
        }
        if (stage is not null && !string.Equals(stage, _activeIndexBuildPhase, StringComparison.Ordinal))
        {
            _activeIndexBuildPhase = stage;
            OnPropertyChanged(nameof(ActiveIndexBuildStage));
            changed = true;
        }

        if (changed && !IsIndexingPaused)
            ShowIndexBuildingStatus();
    }

    /// <summary>
    /// Marks that a background index build has finished. When the last active build completes, the
    /// main-window index indicator reverts to the availability/coverage status for the last search
    /// context (or is hidden if none) — unless indexing is paused, in which case the paused state stays.
    /// Safe to call from any thread.
    /// </summary>
    public void EndIndexBuildActivity()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(EndIndexBuildActivity);
            return;
        }

        _activeIndexBuilds = Math.Max(0, _activeIndexBuilds - 1);
        OnPropertyChanged(nameof(IsIndexBuildActive));
        OnPropertyChanged(nameof(CanPauseIndexing));

        if (_activeIndexBuilds > 0)
        {
            ShowIndexBuildingStatus();
            return;
        }

        // While paused, keep the "Indexing paused" indicator until the user resumes.
        if (IsIndexingPaused)
        {
            ShowIndexBuildingStatus();
            return;
        }

        _activeIndexBuildFolder = null;
        _activeIndexBuildIsIncremental = false;
        _activeIndexBuildPhase = null;
        OnPropertyChanged(nameof(ActiveIndexBuildStage));
        OnPropertyChanged(nameof(IsActiveIndexBuildIncremental));
        RevertIndexIndicatorAfterBuild();
    }

    /// <summary>Reverts the main-window index indicator from a build state back to availability/coverage for
    /// the last search context (or hides it / shows a one-shot "Index: ready" when there is none).</summary>
    private void RevertIndexIndicatorAfterBuild()
    {
        ShowIndexBuildPercent = false;
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
        {
            ShowIndexStatus = false;
            return;
        }

        if (IsIndexWarmActive && !string.IsNullOrWhiteSpace(_activeIndexWarmFolder))
        {
            ShowIndexWarmPreparingStatus(_activeIndexWarmFolder);
            return;
        }
        if (IsIndexWarmPausedForSearch)
        {
            ShowIndexWarmPausedStatus();
            return;
        }

        if (_lastIndexStatusRoots.Count > 0)
        {
            _ = RefreshIndexStatusAsync(_lastIndexStatusRoots, _lastIndexStatusUseThisSearch);
        }
        else
        {
            IndexStatusGlyph = ContentIndexUiStatus.AvailabilityGlyph(IndexAvailability.Available);
            IndexStatusText = ContentIndexUiStatus.ReadyLabel;
            IndexStatusTooltip = "The content index finished building. Matching files are always read live from disk. "
                + BuildIndexDateDetails()
                + BuildIndexSchedulingDetails();
            ShowIndexStatus = true;
        }
        RefreshAllDriveIndexStatus();
    }

    /// <summary>Renders the "Indexing…" (or "Indexing paused") state on the main-window index indicator
    /// (no-op when the user has hidden index status).</summary>
    private void ShowIndexBuildingStatus()
    {
        if (!_settings.ShowIndexStatusInMainWindow)
            return;

        if (IsIndexingPaused)
        {
            ShowIndexBuildPercent = false;
            if (_indexDiskFullMessage is { } diskFull)
            {
                IndexStatusGlyph = ContentIndexUiStatus.StatusWarningGlyph;
                IndexStatusText = "Index: disk full";
                IndexStatusTooltip = diskFull + BuildIndexDateDetails();
                ShowIndexStatus = true;
                return;
            }

            IndexStatusGlyph = "\uE769"; // Pause
            IndexStatusText = "Indexing paused";
            IndexStatusTooltip = (string.IsNullOrWhiteSpace(_activeIndexBuildFolder)
                ? "Indexing is paused. Right-click to resume."
                : $"Indexing of {_activeIndexBuildFolder} is paused. Right-click to resume.")
                + BuildIndexDateDetails();
            ShowIndexStatus = true;
            return;
        }

        IndexStatusGlyph = "\uE895"; // Sync
        // Surface the estimate right in the status-bar text so the progress is visible at a glance, and
        // populate the custom tooltip's big percent + progress bar (below).
        IndexStatusText = ContentIndexUiStatus.BuildActivityLabel(
            _activeIndexBuildIsIncremental,
            _activeIndexBuildPhase,
            _indexBuildPercent);
        if (_activeIndexBuildIsIncremental)
        {
            IndexStatusTooltip = string.IsNullOrWhiteSpace(_activeIndexBuildFolder)
                ? "Updating the existing content index incrementally\u2026 This runs in the background; searches keep working and the current index remains available. Right-click to pause."
                : $"Updating the existing content index for {_activeIndexBuildFolder} incrementally\u2026 This runs in the background; searches keep working and the current index remains available. Right-click to pause.";
            if (ContentIndexUiStatus.BuildActivityDetail(_activeIndexBuildPhase) is { } phaseDetail)
                IndexStatusTooltip = phaseDetail + "\n\n" + IndexStatusTooltip;
        }
        else
        {
            IndexStatusTooltip = string.IsNullOrWhiteSpace(_activeIndexBuildFolder)
                ? "Building the content index\u2026 This runs in the background; searches keep working and results never change. Right-click to pause."
                : $"Building a content index for {_activeIndexBuildFolder}\u2026 This runs in the background; searches keep working and results never change. Right-click to pause.";
        }
        if (ContentIndexUiStatus.BuildActivityDetail(_activeIndexBuildPhase) is { } stageDetail
            && !IndexStatusTooltip.StartsWith(stageDetail, StringComparison.Ordinal))
        {
            IndexStatusTooltip = stageDetail + "\n\n" + IndexStatusTooltip;
        }
        IndexStatusTooltip += BuildIndexDateDetails();
        if (_indexBuildPercent >= 0)
        {
            IndexBuildPercentText = $"{_indexBuildPercent}%";
            IndexBuildPercentValue = _indexBuildPercent;
            ShowIndexBuildPercent = true;
        }
        else
        {
            ShowIndexBuildPercent = false;
        }
        ShowIndexStatus = true;
    }
}
