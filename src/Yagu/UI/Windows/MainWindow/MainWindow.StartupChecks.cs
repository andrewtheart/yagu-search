using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yagu.Services;
using Yagu.Services.Ai;
using Yagu.Services.Index;
using Yagu.Services.Logging;
using System.Globalization;
namespace Yagu;

/// <summary>
/// Content-loaded startup flow, Everything detection, and first-run result-store location prompts.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Optional FileSystemWatcher latency-hint service (plan §11.4); null unless the user opted in.</summary>
    private ContentIndexWatcherHintService? _indexWatcherHints;
    private int _indexWatcherHintsGeneration;

    /// <summary>Ticks while Yagu runs so the OnSchedule build trigger can fire on time; null until started.</summary>
    private DispatcherTimer? _indexScheduleTimer;
    /// <summary>When the last scheduled build pass ran (local). Seeded to launch time so an interval counts
    /// from launch and a weekly slot only fires if it is still upcoming today (never run retroactively).</summary>
    private DateTimeOffset _lastScheduledIndexRun = DateTimeOffset.Now;
    /// <summary>Prevents WhenIdle/Continuous maintenance from launching on every 30-second timer tick.
    /// Either trigger may run another pass after its own configured cadence elapses.</summary>
    private DateTimeOffset _lastIdleIndexRunUtc = DateTimeOffset.MinValue;

    private async void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnContentLoaded;
        SyncWrapModeToggles(ViewModel.PreviewWrapModeIndex);
        ApplyWordWrap(ViewModel.PreviewWordWrap);
        ApplyPreviewColors();
        UpdatePinStartupDirectoryIcon(ViewModel.IsCurrentDirectoryPinned);
        UpdateIndexDirectoryIcon(ViewModel.IsCurrentDirectoryIndexed);
        ViewModel.RefreshAllDriveIndexStatus();
        if (_launcherMode) PositionLauncherWindow();

        // Apply maximize-on-startup setting (only in non-launcher mode)
        if (!_launcherMode && ViewModel.MaximizeOnStartup &&
            AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        else if (!_launcherMode)
        {
            // Place the window per the user's launch-position setting once its size has settled.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                PositionWindowOnLaunch);
        }

        if (_autoSearchOnLoad)
        {
            // Suppress dropdowns so the query/directory suggestion lists
            // don't pop open during an auto-search launch.
            SuppressQuerySuggestionsFor(3000);
            DirectoryBox.IsSuggestionListOpen = false;
        }

        FocusSearchOnLaunch();
        // Start a cold query-index load immediately at launch. The view model performs all metadata/size
        // checks off the UI thread after showing "Indexing: preparing..." and makes the load cancellable
        // so a user-started search can pause it instead of competing for memory and disk.
        ViewModel.StartContentIndexWarmup(ViewModel.Directory);
        StartupDialogPlan startupDialogPlan = await PrepareStartupDialogPlanAsync();
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.TelemetryConsent, ShowTelemetryConsentIfNeededAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.WindowMode, CheckFirstRunWindowModeAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.ResultTempLocation, CheckFirstRunResultStoreTempLocationAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.Everything, CheckEverythingAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.ContextMenu, CheckFirstRunContextMenuAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.IndexOnboarding, CheckFirstRunIndexOnboardingAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.FontContrast, ShowFontContrastWarningIfNeededAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.CpuSemanticWarning, ShowCpuSemanticWarningIfNeededAsync);
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.SemanticQualification, OfferSemanticModelQualificationIfNeededAsync);
        // Update checks: the one-time consent prompt (only on a fresh install / undecided user) stays in
        // the awaited startup-modal chain so it never races or stacks with first-run, telemetry, indexing,
        // or semantic dialogs. The Automatic-mode background check is fire-and-forget and only ever
        // surfaces a non-modal banner (never a launch modal), so it can't delay startup.
        await RunStartupDialogStepAsync(startupDialogPlan, StartupDialogStep.AppUpdateConsent, MaybeShowAppUpdateConsentPromptAsync);
        _ = MaybeRunAutomaticAppUpdateCheckAsync();

        if (_autoSearchOnLoad)
        {
            _autoSearchOnLoad = false;
            // Run the full pre-search warning gate (HDD + excluded-extension), the same notices an
            // interactive search shows, so an auto-search launched with a directory (a pinned startup
            // folder, --dir, or the Explorer context menu) also warns before a doomed full-tree scan
            // for a file whose extension is currently excluded.
            if (await RunPreSearchWarningGatesAsync())
            {
                CollapseAdvancedOptionsForSearch();
                await ViewModel.StartSearchAsync();
            }
        }
        else
        {
            FocusSearchBox();
        }

        // Non-blocking: alert (once) if Foundry Local has new/updated on-device models available.
        // Fire-and-forget so a slow catalog query never delays the search box or startup focus.
        _ = CheckForNewFoundryModelsAsync();

        // Non-blocking: if content indexing is on and the build trigger is AtStartup, build any registered
        // folders that have no index yet, in the background. Off by default (opt-in) and never blocks the UI
        // or a search; publishing an index changes no results (plan §6.1/§6.2).
        _ = RunAutoIndexBuildIfDueAsync();

        // Start the maintenance timer so OnSchedule, WhenIdle, and Continuous triggers can fire while
        // Yagu runs. Each tick gates on the selected triggers/master switch, so it is otherwise a cheap no-op.
        StartIndexScheduleTimer();
        QueueIndexWatcherHintsRecreation("startup");

        // Let a right-click "Resume indexing" re-run the multi-root auto/scheduled pass (which this view
        // owns) — not just single-folder builds — so resuming a paused auto/scheduled build actually resumes.
        ViewModel.ResumeAutoIndexBuildAsync = ResumeAutoIndexBuildPassAsync;
        ViewModel.RequestIdleIndexMaintenanceAsync = RunIdleIndexBuildIfDueAsync;
    }

    /// <summary>Starts the once-per-30s timer that drives <c>OnSchedule</c>, <c>WhenIdle</c>, and
    /// <c>Continuous</c> maintenance. Idempotent.</summary>
    private void StartIndexScheduleTimer()
    {
        if (_indexScheduleTimer is not null)
            return;
        _lastScheduledIndexRun = DateTimeOffset.Now;
        _indexScheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _indexScheduleTimer.Tick += (_, _) =>
        {
            _ = RunScheduledIndexBuildIfDueAsync();
            _ = RunIdleIndexBuildIfDueAsync();
        };
        _indexScheduleTimer.Start();

        // Continuous means "act as if the PC is always idle". Evaluate it immediately instead of
        // waiting for the first 30-second timer tick; the shared cooldown and active-build gates still
        // prevent overlap and enforce the configured maintenance interval.
        if (AppSettings.IndexBuildTriggerHas(
                ViewModel.Settings.IndexBuildTrigger,
                ContentIndexBuildScheduler.TriggerContinuous))
            _ = RunIdleIndexBuildIfDueAsync();
    }

    /// <summary>Runs idle-style maintenance. WhenIdle uses its configured no-input delay and Continuous uses
    /// its independent repeat interval. When both triggers are selected, whichever becomes due first can
    /// start the next shared maintenance pass. Battery, foreground-search, disk-space, and pause safeguards
    /// remain authoritative.</summary>
    private async Task RunIdleIndexBuildIfDueAsync()
    {
        try
        {
            AppSettings settings = ViewModel.Settings;
            TimeSpan requiredIdle = TimeSpan.FromMinutes(
                AppSettings.NormalizeIndexIdleDelayMinutes(settings.IndexIdleDelayMinutes));
            TimeSpan continuousInterval = TimeSpan.FromMinutes(
                AppSettings.NormalizeIndexContinuousIntervalMinutes(settings.IndexContinuousIntervalMinutes));
            bool idleMaintenance = AppSettings.IndexBuildTriggerHas(
                settings.IndexBuildTrigger,
                ContentIndexBuildScheduler.TriggerWhenIdle);
            bool continuousMaintenance = AppSettings.IndexBuildTriggerHas(
                settings.IndexBuildTrigger,
                ContentIndexBuildScheduler.TriggerContinuous);
            bool developerSimulatedIdle = ViewModel.SimulateSystemIdle;
            TimeSpan? idleTime = developerSimulatedIdle
                ? requiredIdle
                : Yagu.Helpers.SystemIdleDetector.TryGetIdleTime();

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            TimeSpan sinceLastPass = nowUtc - _lastIdleIndexRunUtc;
            bool idleDue = idleMaintenance
                && Yagu.Helpers.SystemIdleDetector.HasBeenIdleFor(idleTime, requiredIdle)
                && sinceLastPass >= requiredIdle;
            bool continuousDue = continuousMaintenance && sinceLastPass >= continuousInterval;
            if ((!idleDue && !continuousDue) || ViewModel.IsIndexBuildActive
                || ViewModel.IsIndexingPaused || ViewModel.IsSearching)
                return;

            IReadOnlyList<string> roots = ContentIndexBuildScheduler.RootsForIdleBuild(settings);
            if (roots.Count == 0)
                return;

            // Mark before dispatch so adjacent timer ticks cannot start a second pass.
            _lastIdleIndexRunUtc = nowUtc;
            if (developerSimulatedIdle)
            {
                YaguLog.For("ContentIndex").LogInformation(
                    "Developer idle simulation active; starting an index-maintenance pass over {RootCount} root(s).",
                    roots.Count);
            }
            else if (continuousDue)
            {
                YaguLog.For("ContentIndex").LogInformation(
                    "Continuous index maintenance due; starting a pass over {RootCount} root(s).",
                    roots.Count);
            }
            else
            {
                YaguLog.For("ContentIndex").LogInformation(
                    "PC idle for {IdleMinutes:F1} minute(s); starting an index-maintenance pass over {RootCount} root(s).",
                    idleTime!.Value.TotalMinutes, roots.Count);
            }
            await RunIndexBuildPassAsync(roots);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Idle index maintenance check failed.");
        }
    }

    /// <summary>
    /// Re-runs the multi-root background build pass over every registered folder — invoked by
    /// <see cref="MainViewModel.ResumeIndexing"/> when the paused build was an auto/startup/scheduled pass
    /// with no single tracked folder, so "Resume indexing" actually resumes. Honors the update mode, so a
    /// root whose index is already fresh is skipped; a no-op when no folders are registered.
    /// </summary>
    private async Task ResumeAutoIndexBuildPassAsync()
    {
        try
        {
            var roots = IndexedRootsPolicy.Normalize(ViewModel.Settings.IndexedRoots);
            if (roots.Count == 0)
                return;
            await RunIndexBuildPassAsync(roots);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Resume index build pass failed.");
        }
    }

    /// <summary>
    /// Timer tick for the <c>OnSchedule</c> build trigger: when the master feature is on, the trigger is
    /// OnSchedule, and the user's schedule (interval, or chosen weekdays at a time) says a pass is due,
    /// runs one background build pass. A no-op otherwise, so the always-on timer costs almost nothing.
    /// </summary>
    private async Task RunScheduledIndexBuildIfDueAsync()
    {
        try
        {
            // Never overlap a running pass, and honor an explicit user pause.
            if (ViewModel.IsIndexBuildActive || ViewModel.IsIndexingPaused)
                return;

            AppSettings settings = ViewModel.Settings;
            var roots = ContentIndexBuildScheduler.RootsForScheduledBuild(settings);
            if (roots.Count == 0)
                return; // not OnSchedule, master off, or no registered folders

            DateTimeOffset now = DateTimeOffset.Now;
            if (!ContentIndexScheduleEvaluator.IsDue(settings, _lastScheduledIndexRun, now))
                return;

            // Mark before building so a long pass isn't re-triggered by the next tick.
            _lastScheduledIndexRun = now;
            YaguLog.For("ContentIndex").LogInformation(
                "Scheduled index build due ({Schedule}); starting a background pass.", ContentIndexScheduleEvaluator.Describe(settings));
            await RunIndexBuildPassAsync(roots);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Scheduled index build check failed.");
        }
    }

    /// <summary>
    /// Startup auto-build (plan §6.1 <c>IndexBuildTrigger = AtStartup</c>). Runs entirely off the UI thread
    /// and swallows failures — a background index build never disrupts the app. Does nothing unless the
    /// master feature is on and the trigger is AtStartup (the default is Manual, so nothing happens).
    /// </summary>
    private async Task RunAutoIndexBuildIfDueAsync()
    {
        AppSettings settings = ViewModel.Settings;
        var roots = ContentIndexBuildScheduler.RootsDueAtStartup(settings);
        if (roots.Count == 0)
            return;
        await RunIndexBuildPassAsync(roots);
    }

    /// <summary>
    /// Runs one background index build/refresh pass over <paramref name="roots"/> — shared by the
    /// <c>AtStartup</c> trigger and the <c>OnSchedule</c> timer. Honors the pause conditions (battery /
    /// foreground search / low disk / user pause), runs entirely off the UI thread, and swallows failures.
    /// Publishing an index changes no search results (plan §6.1/§6.2).
    /// </summary>
    private async Task RunIndexBuildPassAsync(IReadOnlyList<string> roots)
    {
        bool indexBuildActivityStarted = false;
        try
        {
            AppSettings settings = ViewModel.Settings;

            // Respect the pause conditions (plan §6.1): don't drain the battery, fight a running search,
            // or fill a nearly-full disk with an unattended build.
            long indexDriveFreeMb;
            try
            {
                var probeProvider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
                string? indexDriveRoot = Path.GetPathRoot(probeProvider.IndexRoot);
                indexDriveFreeMb = string.IsNullOrEmpty(indexDriveRoot)
                    ? -1
                    : new DriveInfo(indexDriveRoot).AvailableFreeSpace / (1024 * 1024);
            }
            catch
            {
                indexDriveFreeMb = -1; // unknown → fail open (never block on an unreadable drive)
            }

            if (ContentIndexBuildScheduler.ShouldPauseAutoBuild(
                    settings, Yagu.Helpers.PowerLineStatus.IsOnBattery(), ViewModel.IsSearching, indexDriveFreeMb))
            {
                YaguLog.For("ContentIndex").LogInformation(
                    "Startup auto-build paused (on battery, a foreground search is active, or the index drive is low on space).");
                return;
            }

            // Honor a user pause (right-click ▸ Pause indexing) — don't start a background pass while paused.
            if (ViewModel.IsIndexingPaused)
            {
                YaguLog.For("ContentIndex").LogInformation("Startup auto-build skipped: indexing is paused by the user.");
                return;
            }

            var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
            int retained = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount);
            var policy = IndexIngestionPolicy.FromSettings(settings);
            string updateMode = AppSettings.NormalizeIndexUpdateMode(settings.IndexUpdateMode);

            // Surface "Indexing…" in the main-window index indicator while this background pass runs.
            ViewModel.BeginIndexBuildActivity();
            indexBuildActivityStarted = true;

            // The auto-builder reports each root's folder + percent-complete straight into the indicator
            // (the folder so the tooltip names the drive being indexed; the percent for full builds AND
            // incremental refreshes). It caches drive denominators internally, so this is a thin forwarder.
            void ReportRootProgress(string root, int percent, string stage) => ViewModel.ReportIndexBuildProgress(root, percent, stage);

            var buildTimer = Stopwatch.StartNew();
            IndexMaintenanceSuccess result;
            IndexRefreshKind refreshKind;
            string maintenanceMode;
            bool rebuildWhenDirty = false;
            if (string.Equals(updateMode, AppSettings.IndexUpdateModeAutomaticIncremental, StringComparison.Ordinal))
            {
                maintenanceMode = IndexMaintenanceOperation.ModeIncremental;
                refreshKind = IndexRefreshKind.IncrementalSegment;
            }
            else
            {
                // AutomaticFullRebuildWhenDirty (plan §6.1, V1) also rebuilds an indexed root the change
                // journal proves has changed since it was built; ManualFullRebuild (default) only builds
                // missing roots.
                rebuildWhenDirty = string.Equals(
                    updateMode,
                    AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty,
                    StringComparison.Ordinal);
                maintenanceMode = IndexMaintenanceOperation.ModeBuildDue;
                refreshKind = IndexRefreshKind.FullBuild;
            }

            IndexMaintenanceOperation operation = IndexBuildOperationFactory.CreateMaintenance(
                settings, roots, maintenanceMode, rebuildWhenDirty);
            var coordinator = new IndexBuildCoordinator();
            result = await coordinator.RunMaintenancePreferWorkerAsync(
                operation,
                settings.IndexUseNativeWorker,
                ViewModel.IndexBuildCancellationToken,
                ReportRootProgress).ConfigureAwait(true);
            YaguLog.For("ContentIndex").LogInformation(
                "Startup index maintenance ({Mode}): built {Built}, skipped {Skipped}, failed {Failed} of {Total} root(s).",
                maintenanceMode, result.Built, result.Skipped, result.Failed, result.Built + result.Skipped + result.Failed);
            buildTimer.Stop();

            // Aggregate-only, opt-in telemetry (plan §6.4): inert unless the user shared index telemetry AND
            // global telemetry is on/configured. Only counts + timing — never a root/path/query.
            IndexTelemetry.ReportRefresh(
                settings, refreshKind, buildTimer.Elapsed.TotalMilliseconds,
                rootsBuilt: result.Built, rootsSkipped: result.Skipped, rootsFailed: result.Failed);

            // Optional FileSystemWatcher latency hints (plan §11.4): only a hint — USN stays authoritative.
            StartIndexWatcherHintsIfEnabled(settings, provider, retained, policy, roots);
        }
        catch (OperationCanceledException)
        {
            YaguLog.For("ContentIndex").LogInformation("Startup auto-build was paused/cancelled by the user.");
        }
        catch (IndexDiskFullException ex)
        {
            ViewModel.OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Startup auto-build failed");
        }
        finally
        {
            if (indexBuildActivityStarted)
                ViewModel.EndIndexBuildActivity();
        }
    }

    /// <summary>
    /// Starts (or restarts) the optional watcher-hint service (plan §11.4). When a watched root goes quiet,
    /// the service runs a single incremental refresh for just that root — reacting to changes sooner than the
    /// next startup pass without ever bypassing the USN authority check. Registration runs off the UI thread
    /// (deep-tree registration can be slow) and never throws; disabled by default.
    /// </summary>
    private void StartIndexWatcherHintsIfEnabled(
        AppSettings settings, IContentIndexPathProvider provider, int retained, IndexIngestionPolicy policy, IReadOnlyList<string> roots)
    {
        int generation = Interlocked.Increment(ref _indexWatcherHintsGeneration);
        DisposeIndexWatcherHints();
        if (roots.Count == 0 || !ContentIndexWatcherHints.ShouldEnable(settings))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var service = new ContentIndexWatcherHintService(
                    roots,
                    changedRoot =>
                    {
                        bool activityStarted = false;
                        try
                        {
                            // Honor a user pause (right-click ▸ Pause indexing).
                            if (ViewModel.IsIndexingPaused || ViewModel.IsShutdownRequested)
                                return;
                            string mode = AppSettings.NormalizeIndexUpdateMode(settings.IndexUpdateMode);
                            bool incremental = string.Equals(mode, AppSettings.IndexUpdateModeAutomaticIncremental, StringComparison.Ordinal);
                            activityStarted = TryBeginWatcherIndexActivity(changedRoot, incremental);
                            if (!activityStarted)
                                return;
                            IndexMaintenanceOperation operation = IndexBuildOperationFactory.CreateMaintenance(
                                settings,
                                new[] { changedRoot },
                                incremental ? IndexMaintenanceOperation.ModeIncremental : IndexMaintenanceOperation.ModeBuildDue,
                                rebuildWhenDirty: !incremental);
                            // The watcher observed an in-scope path change directly. Force the incremental
                            // journal pass so a newly created file (whose identity is not in the old index
                            // yet) cannot be mistaken for a fresh root by the identity-only preflight.
                            operation.ForceRefresh = incremental;
                            var coordinator = new IndexBuildCoordinator();
                            IndexMaintenanceSuccess r = coordinator.RunMaintenancePreferWorkerAsync(
                                operation,
                                settings.IndexUseNativeWorker,
                                ViewModel.IndexBuildCancellationToken).GetAwaiter().GetResult();
                            if (r.Built > 0)
                                YaguLog.For("ContentIndex").LogInformation("Watcher-hinted incremental refresh updated '{ChangedRoot}'.", changedRoot);
                        }
                        catch (OperationCanceledException)
                        {
                            YaguLog.For("ContentIndex").LogInformation(
                                "Watcher-hinted refresh for '{ChangedRoot}' was cancelled.", changedRoot);
                        }
                        catch (Exception ex)
                        {
                            YaguLog.For("ContentIndex").LogWarning(ex, "Watcher-hinted refresh failed");
                        }
                        finally
                        {
                            if (activityStarted)
                                DispatcherQueue.TryEnqueue(ViewModel.EndIndexBuildActivity);
                        }
                    },
                    excludedStorageRoot: provider.IndexRoot);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed || generation != Volatile.Read(ref _indexWatcherHintsGeneration))
                    {
                        service.Dispose();
                        return;
                    }
                    _indexWatcherHints = service;
                    YaguLog.For("ContentIndex").LogInformation(
                        "Watcher hints active on {ActiveWatchCount} of {RootCount} root(s).",
                        service.ActiveWatchCount,
                        roots.Count);
                });
            }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "Failed to start watcher hints");
            }
        });
    }

    private bool TryBeginWatcherIndexActivity(string root, bool incremental)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || ViewModel.IsShutdownRequested || ViewModel.IsIndexingPaused)
            {
                completion.TrySetResult(false);
                return;
            }

            ViewModel.BeginIndexBuildActivity(root, isIncremental: incremental);
            completion.TrySetResult(true);
        }))
        {
            return false;
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private void QueueIndexWatcherHintsRecreation(string reason)
    {
        if (_disposed)
            return;
        AppSettings settings = ViewModel.Settings;
        var roots = IndexedRootsPolicy.Normalize(settings.IndexedRoots);
        var provider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount);
        IndexIngestionPolicy policy = IndexIngestionPolicy.FromSettings(settings);
        YaguLog.For("ContentIndex").LogDebug("Recreating watcher hints: {Reason}.", reason);
        StartIndexWatcherHintsIfEnabled(settings, provider, retained, policy, roots);
    }

    private void DisposeIndexWatcherHints()
    {
        _indexWatcherHints?.Dispose();
        _indexWatcherHints = null;
    }

    private void FocusSearchBox(bool suppressSuggestions = false)
    {
        if (suppressSuggestions)
            SuppressQuerySuggestionsFor(1000);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (suppressSuggestions)
                SuppressQuerySuggestionsFor(1000);

            QueryBox.Focus(FocusState.Programmatic);

            if (suppressSuggestions)
            {
                QueryBox.IsSuggestionListOpen = false;
                DispatcherQueue.TryEnqueue(() => QueryBox.IsSuggestionListOpen = false);
            }
        });
    }

    /// <summary>
    /// First-run only: ask once whether the user wants to help improve Yagu (anonymized telemetry and/or
    /// bug reports). Sequenced into the startup-modal chain rather than fired from <see cref="App"/>, so it
    /// never stacks on top of another first-run prompt - only one startup modal is shown at a time. The
    /// dialog records "prompt shown" itself; if another owned modal is somehow open it simply retries next
    /// launch (matching the other startup checks).
    /// </summary>
    private async Task ShowTelemetryConsentIfNeededAsync()
    {
        if (ViewModel.TelemetryConsentPromptShown)
            return;
        // Don't stack on another startup prompt; not marked shown yet, so it tries again next launch.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        await TelemetryConsentDialog.RequestConsentAsync(this);
    }

    /// <summary>
    /// First-run only: when AI (Semantic) search is available but no GPU/NPU was detected, warn that the
    /// suggested model would run on the CPU (slower, variable results) and offer to make Traditional the
    /// default search mode. Shown at most once. Titleless modal with a warning glyph, matching the app's
    /// other warning dialogs. Accepting persists the Traditional default and switches the UI immediately.
    /// </summary>
    private async Task ShowCpuSemanticWarningIfNeededAsync()
    {
        if (!ViewModel.ShouldShowCpuSemanticWarning)
            return;
        // Don't stack on another startup prompt; the warning is not marked shown yet, so it simply
        // tries again on the next launch.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "AI search will run on your CPU",
                TitleGlyph = "\uE7BA",
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content = BuildCpuSemanticWarningContent(),
                PrimaryButtonText = "Use Traditional search",
                CloseButtonText = "Keep AI search",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 560,
                Height = 360,
                MaxContentHeight = 240,
            });

        await ViewModel.DismissCpuSemanticWarningAsync(result == YaguDialogResult.Primary);
    }

    /// <summary>Body of the first-run CPU-mode AI-search warning: what CPU mode means and the
    /// recommendation to keep Traditional search as the default.</summary>
    private static StackPanel BuildCpuSemanticWarningContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Yagu didn't find a compatible GPU or NPU on this PC, so AI (Semantic) search would run on "
                 + "your CPU. This model will likely provide a degraded and inconsistent experience, and "
                 + "searches may be slow.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "We recommend keeping Traditional search as your default. You can still switch to AI search "
                 + "any time from the search bar.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        });
        return panel;
    }

    /// <summary>Body of the first-run "Everything Search Not Found" prompt. Leads with a bold,
    /// color + glyph standout line that very strongly recommends installing Everything for the best
    /// experience, then explains why and reassures that the install is verified and safe.</summary>
    private static StackPanel BuildEverythingNotFoundContent()
    {
        var panel = new StackPanel { Spacing = 12 };

        // Standout recommendation: a bold amber line with a star glyph so it clearly stands apart from
        // the body text — installing Everything is the single biggest speed win for Yagu. DarkOrange is
        // chosen because it stays readable on both the light and dark dialog themes.
        var recommendBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
        var recommend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        recommend.Children.Add(new FontIcon
        {
            Glyph = "\uE735", // filled star
            FontSize = 20,
            Foreground = recommendBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        recommend.Children.Add(new TextBlock
        {
            Text = "Very strongly recommended for the best Yagu experience",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = recommendBrush,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(recommend);

        panel.Children.Add(new TextBlock
        {
            Text = "Everything Search by voidtools gives Yagu near-instant file discovery. Without it, Yagu "
                 + "falls back to a much slower built-in file scan — searches over large drives can take "
                 + "far longer.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "It's free, tiny, and safe: Yagu verifies the official voidtools signature before running "
                 + "the installer. Would you like to download and install it now?",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 13,
            Opacity = 0.85,
        });
        return panel;
    }

    private async Task CheckForNewFoundryModelsAsync()
    {
        if (!ViewModel.Settings.NotificationsEnabled || !ViewModel.FoundryModelUpdateAlertsEnabled)
            return;

        // Don't stack on top of another startup prompt (Everything, font-contrast, etc.). If one is
        // open we skip entirely this session — the VM has not committed a baseline yet, so the check
        // simply runs again next launch.
        if (YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        IReadOnlyList<FoundryModelChange> changes;
        try
        {
            changes = await ViewModel.CheckForNewFoundryModelsAsync(CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            YaguLog.For("MainWindow").LogWarning(ex, "CheckForNewFoundryModelsAsync failed: {Error}", ex.Message);
            return;
        }

        if (changes.Count == 0 || YaguDialog.HasOpenOwnedWindow(_hwnd))
            return;

        var (content, dontAlertAgain) = BuildFoundryModelAlertContent(changes);
        var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = changes.Count == 1 ? "New AI model available" : "New AI models available",
                Content = content,
                TitleGlyph = "\uE99A",
                PrimaryButtonText = "Choose a model\u2026",
                CloseButtonText = "Dismiss",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = theme,
                Width = 560,
                Height = 420,
                MaxContentHeight = 320,
            });

        if (dontAlertAgain.IsChecked == true)
            ViewModel.FoundryModelUpdateAlertsEnabled = false;

        if (result == YaguDialogResult.Primary)
        {
            await SemanticModelDownloadDialog.ShowAsync(
                _hwnd,
                theme,
                (progress, token) => ViewModel.GetSemanticModelOptionsAsync(progress, token),
                (alias, progress, token) => ViewModel.PrepareSemanticModelAsync(alias, progress, token),
                ViewModel.SemanticModelAlias);
        }
    }

    /// <summary>Builds the body of the new-model alert: an intro line, a row per new/updated model, and
    /// a "Don't alert me again" checkbox (returned so the caller can read its state after the dialog).</summary>
    private static (FrameworkElement Content, CheckBox DontAlertAgain) BuildFoundryModelAlertContent(
        IReadOnlyList<FoundryModelChange> changes)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = changes.Count == 1
                ? "A new on-device model is available for AI (Semantic) search:"
                : $"{changes.Count} new on-device models are available for AI (Semantic) search:",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (var change in changes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE753",
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var parts = new List<string> { change.Alias };
            if (!string.IsNullOrWhiteSpace(change.DeviceLabel))
                parts.Add(change.DeviceLabel!);
            string size = FormatModelSize(change.SizeBytes);
            if (size.Length > 0)
                parts.Add(size);

            row.Children.Add(new TextBlock
            {
                Text = string.Join("  \u00b7  ", parts),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var tagText = new TextBlock
            {
                Text = change.Kind == FoundryModelChangeKind.New ? "New" : "Updated",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tag = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(change.Kind == FoundryModelChangeKind.New
                    ? Windows.UI.Color.FromArgb(0x40, 0x4C, 0x9E, 0xFF)
                    : Windows.UI.Color.FromArgb(0x40, 0x5C, 0xB8, 0x5C)),
                Child = tagText,
            };
            row.Children.Add(tag);
            list.Children.Add(row);
        }
        panel.Children.Add(list);

        var dontAlertAgain = new CheckBox
        {
            Content = "Don't alert me about new models again",
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(dontAlertAgain);

        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return (scroller, dontAlertAgain);
    }

    private static string FormatModelSize(long? bytes)
    {
        if (bytes is not { } b || b <= 0)
            return string.Empty;
        double gb = b / (1024.0 * 1024 * 1024);
        if (gb >= 1)
            return $"{gb:0.#} GB";
        double mb = b / (1024.0 * 1024);
        return $"{mb:0} MB";
    }

    /// <summary>Builds the body of the "Everything not running" prompt: the explanatory text and a
    /// "Don't show this again" checkbox (returned so the caller can read its state after the dialog).</summary>
    private static (FrameworkElement Content, CheckBox DontShowAgain) BuildEverythingNotRunningContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Everything Search is installed but not currently running.\nIt must be running for fast file discovery.\n\nWould you like to start it now?",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 14,
        });

        var dontShowAgain = new CheckBox
        {
            Content = "Don't show this again",
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(dontShowAgain);

        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return (scroller, dontShowAgain);
    }

    private async Task CheckEverythingAsync()
    {
        EverythingStartupDetection detection;
        if (_preparedEverythingStartupDetection is { } preparedDetection)
            detection = preparedDetection;
        else
            detection = await Task.Run(DetectEverythingStartupState);
        string? esPath = detection.EsPath;
        bool everythingRunning = detection.EverythingRunning;
        YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: esPath={EsPath}, everythingRunning={EverythingRunning}", esPath ?? "(null)", everythingRunning);

        // Everything is running — SDK will work regardless of es.exe presence
        if (everythingRunning)
        {
            YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: Everything process is running — SDK will work, no action needed");
            return;
        }

        // es.exe found but Everything service not running — offer to start it
        if (esPath != null)
        {
            string? everythingExe = detection.EverythingExePath;
            YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: es.exe found at '{EsPath}', Everything.exe resolve={EverythingExe}", esPath, everythingExe ?? "(null)");
            if (everythingExe != null)
            {
                if (ViewModel.SuppressEverythingNotRunningPrompt)
                {
                    YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: 'Everything not running' prompt suppressed by user setting \u2014 skipping");
                    return;
                }

                var (content, dontShowAgain) = BuildEverythingNotRunningContent();
                bool startNow = await YaguDialog.ShowAsync(
                    _hwnd,
                    new YaguDialogOptions
                    {
                        Title = "Everything Search Not Running",
                        TitleGlyph = "\uE721", // Search
                        Content = content,
                        PrimaryButtonText = "Start Everything",
                        CloseButtonText = "Skip",
                        DefaultButton = YaguDialogDefaultButton.Primary,
                        ShowTitleBar = false,
                        Width = 560,
                        Height = 340,
                    }) == YaguDialogResult.Primary;

                if (dontShowAgain.IsChecked == true)
                    ViewModel.SuppressEverythingNotRunningPrompt = true;

                if (startNow)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = everythingExe,
                            UseShellExecute = true,
                        });
                        await WaitForEverythingReadyAndNotifyAsync();
                    }
                    catch (Exception ex)
                    {
                        ViewModel.StatusText = $"Could not start Everything: {ex.Message}. Using built-in file enumeration.";
                        YaguLog.For("MainWindow").LogWarning(ex, "Failed to start Everything");
                    }
                }
                return;
            }
        }

        // Check if Everything.exe exists in standard locations even without es.exe
        string? everythingExeStandalone = detection.EverythingExePath;
        if (everythingExeStandalone != null)
        {
            YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: Everything.exe found at '{EverythingExeStandalone}' (no es.exe), offering to start", everythingExeStandalone);
            if (ViewModel.SuppressEverythingNotRunningPrompt)
            {
                YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: 'Everything not running' prompt suppressed by user setting \u2014 skipping");
                return;
            }

            var (content, dontShowAgain) = BuildEverythingNotRunningContent();
            bool startNow = await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Everything Search Not Running",
                    TitleGlyph = "\uE721", // Search
                    Content = content,
                    PrimaryButtonText = "Start Everything",
                    CloseButtonText = "Skip",
                    DefaultButton = YaguDialogDefaultButton.Primary,
                    ShowTitleBar = false,
                    Width = 560,
                    Height = 340,
                }) == YaguDialogResult.Primary;

            if (dontShowAgain.IsChecked == true)
                ViewModel.SuppressEverythingNotRunningPrompt = true;

            if (startNow)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = everythingExeStandalone,
                        UseShellExecute = true,
                    });
                    await WaitForEverythingReadyAndNotifyAsync();
                }
                catch (Exception ex)
                {
                    ViewModel.StatusText = $"Could not start Everything: {ex.Message}. Using built-in file enumeration.";
                    YaguLog.For("MainWindow").LogWarning(ex, "Failed to start Everything");
                }
            }
            return;
        }

        // Nothing found — offer to download and install
        YaguLog.For("MainWindow").LogWarning("CheckEverythingAsync: Everything not found anywhere — showing install dialog");
        bool installEverything = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Everything Search Not Found",
                TitleGlyph = "\uE721", // Search
                Content = BuildEverythingNotFoundContent(),
                PrimaryButtonText = "Install",
                CloseButtonText = "Skip",
                DefaultButton = YaguDialogDefaultButton.Primary,
                ShowTitleBar = false,
                Width = 560,
                Height = 340,
            }) == YaguDialogResult.Primary;

        if (!installEverything) return;

        bool is64Bit = Environment.Is64BitOperatingSystem;

        // Offline edition: the voidtools Everything setup is pre-bundled beside the app, so run it
        // directly instead of downloading. Consent was already given by the "Install" dialog above;
        // the Authenticode publisher check and elevation below apply to the bundled installer exactly
        // as they do to a downloaded one, so a tampered bundle is still refused.
        string? bundledInstaller = EverythingAssetPaths.BundledInstallerPath(is64Bit);
        bool installerFromBundle = bundledInstaller is not null;
        string installerPath;

        if (installerFromBundle)
        {
            installerPath = bundledInstaller!;
            YaguLog.For("MainWindow").LogInformation("CheckEverythingAsync: using bundled Everything installer at '{InstallerPath}' (offline edition) \u2014 no download", installerPath);
        }
        else
        {
            string url = EverythingAssetPaths.DownloadUrl(is64Bit);
            string fileName = EverythingAssetPaths.SetupFileName(is64Bit);
            string tempPath = Path.Combine(Path.GetTempPath(), fileName);

            // Download the installer behind a modal progress dialog. On cancel or failure (e.g. no
            // internet) a clear message is shown and we fall back to built-in enumeration rather than
            // failing silently with only a status-bar string.
            if (!await DownloadEverythingInstallerAsync(url, tempPath))
                return;

            installerPath = tempPath;
        }

        // Never run the installer elevated without confirming it is a genuine, untampered voidtools
        // binary. HTTPS protects the transport, but a compromised mirror or MITM able to present a
        // trusted certificate could still deliver a malicious payload (OWASP A08); the bundled copy
        // is verified the same way in case it was swapped on disk.
        if (!AuthenticodeVerifier.IsTrustedPublisher(installerPath, EverythingAssetPaths.TrustedPublisher, out string signatureFailure))
        {
            if (!installerFromBundle) TryDeleteFile(installerPath);
            YaguLog.For("MainWindow").LogWarning("Refusing to run Everything installer: {SignatureFailure}", signatureFailure);
            ViewModel.StatusText = "Everything Search installer failed signature verification and was not run. Using built-in file enumeration.";
            return;
        }

        ViewModel.StatusText = "Running Everything Search installer \u2014 please complete the setup wizard\u2026";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Verb = "runas",
                UseShellExecute = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }

            // Everything's own setup does NOT ship es.exe (the ES command-line tool is a separate
            // voidtools download), so keying post-install detection on es.exe made every successful
            // install look like a failure. Fall back to locating Everything.exe itself.
            EverythingStartupDetection installedDetection = await Task.Run(DetectEverythingStartupState);
            string? installedEverythingExe = installedDetection.EverythingExePath;
            if (installedEverythingExe is null)
            {
                ViewModel.StatusText = "Installer completed. Restart Yagu if Everything was installed to a custom location.";
                return;
            }

            if (!installedDetection.EverythingRunning)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installedEverythingExe,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    YaguLog.For("MainWindow").LogWarning(ex, "Failed to start Everything after install");
                }
            }

            await WaitForEverythingReadyAndNotifyAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ViewModel.StatusText = "Everything Search installation was cancelled. Using built-in file enumeration.";
            YaguLog.For("MainWindow").LogInformation("Everything install UAC declined");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Failed to install Everything: {ex.Message}. Using built-in file enumeration.";
            YaguLog.For("MainWindow").LogWarning(ex, "Everything install failed");
        }
    }

    /// <summary>
    /// Downloads the Everything Search installer to <paramref name="tempPath"/> behind a modal
    /// progress dialog. Returns true when the file is ready to run; false when the user cancelled or
    /// the download failed (a clear failure modal is shown for real errors, e.g. no internet). Never
    /// throws — a failed download degrades gracefully to built-in file enumeration.
    /// </summary>
    private async Task<bool> DownloadEverythingInstallerAsync(string url, string tempPath)
    {
        using var cts = new CancellationTokenSource();

        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var statusText = new TextBlock { Text = "Connecting\u2026", Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock
        {
            Text = "Downloading the Everything Search installer from voidtools.com\u2026",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(progressBar);
        body.Children.Add(statusText);

        YaguDialog? dialog = null;
        var dialogTask = YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Getting Everything Search",
                TitleGlyph = "\uE896", // Download
                Content = body,
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Close,
                ShowTitleBar = false,
                Width = 480,
                Height = 240,
            },
            dlg => dialog = dlg);

        // Closing/cancelling the progress modal cancels the in-flight download.
        _ = dialogTask.ContinueWith(
            _ => cts.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        ViewModel.StatusText = "Downloading Everything Search installer\u2026";

        bool cancelled = false;
        Exception? error = null;
        try
        {
            await DownloadFileWithProgressAsync(
                url,
                tempPath,
                (received, total) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (total > 0)
                    {
                        int pct = (int)Math.Clamp(received * 100 / total, 0, 100);
                        progressBar.IsIndeterminate = false;
                        progressBar.Value = pct;
                        statusText.Text = $"{pct}%  \u00b7  {FormatDownloadBytes(received)} of {FormatDownloadBytes(total)}";
                    }
                    else
                    {
                        statusText.Text = $"{FormatDownloadBytes(received)} downloaded";
                    }
                }),
                cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // Close the progress modal (no-op if the user already closed it) and wait for teardown so the
        // owner window is re-enabled before any follow-up modal is shown.
        if (!dialogTask.IsCompleted)
            dialog?.AcceptClose();
        await dialogTask.ConfigureAwait(true);

        if (cancelled)
        {
            TryDeleteFile(tempPath);
            ViewModel.StatusText = "Everything Search download cancelled. Using built-in file enumeration.";
            YaguLog.For("MainWindow").LogInformation("Everything installer download cancelled by user");
            return false;
        }

        if (error is not null)
        {
            TryDeleteFile(tempPath);
            YaguLog.For("MainWindow").LogWarning(error, "Everything installer download failed");
            ViewModel.StatusText = "Could not download Everything Search. Using built-in file enumeration.";
            await ShowEverythingDownloadFailedAsync(error).ConfigureAwait(true);
            return false;
        }

        return true;
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="destinationPath"/>, reporting
    /// (bytesReceived, totalBytes) as it goes (totalBytes is 0 when the server omits Content-Length).</summary>
    private static async Task DownloadFileWithProgressAsync(
        string url, string destinationPath, Action<long, long> onProgress, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await http
            .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;
        onProgress(0, total);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            onProgress(received, total);
        }
    }

    /// <summary>Shows a modal explaining that the Everything Search installer could not be downloaded
    /// (typically no internet), so the user understands why fast discovery is unavailable instead of
    /// only seeing a status-bar line.</summary>
    private async Task ShowEverythingDownloadFailedAsync(Exception error)
    {
        string reason = error switch
        {
            HttpRequestException => "Yagu couldn't reach voidtools.com. Please check your internet connection and try again.",
            TaskCanceledException => "The download timed out. Please check your internet connection and try again.",
            _ => "The download did not complete.",
        };

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Couldn't download Everything Search",
                TitleGlyph = "\uE7BA", // Warning
                TitleGlyphColor = Microsoft.UI.Colors.Gold,
                Content = reason
                        + "\n\nYou can also install Everything Search manually from voidtools.com. In the "
                        + "meantime, Yagu will keep working using built-in file enumeration.",
                CloseButtonText = "OK",
                DefaultButton = YaguDialogDefaultButton.Close,
                ShowTitleBar = false,
                Width = 520,
                Height = 280,
            }).ConfigureAwait(true);
    }

    private static string FormatDownloadBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");
        if (bytes >= 1024L)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB");
        return $"{bytes} B";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            YaguLog.For("MainWindow").LogDebug(ex, "Could not delete temp file '{Path}': {Error}", path, ex.Message);
        }
    }

    private async Task<bool> WaitForEverythingReadyAndNotifyAsync()
    {
        ViewModel.StatusText = "Waiting for Everything Search to return indexed files and folders...";
        var readiness = await FileLister.WaitForEverythingSdkReadyAsync(
            timeout: TimeSpan.FromSeconds(90),
            pollInterval: TimeSpan.FromSeconds(1),
            cancellationToken: CancellationToken.None);

        if (!readiness.IsReady)
        {
            ViewModel.StatusText = $"Everything Search is not ready yet: {readiness.Error}. Using built-in file enumeration.";
            return false;
        }

        uint indexedCount = readiness.TotalCount > 0 ? readiness.TotalCount : readiness.ReturnedCount;
        ViewModel.StatusText = $"Everything Search is ready - {indexedCount:N0} files and folders indexed.";

        await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Everything Search Ready",
                TitleGlyph = "\uE930", // Completed
                Content = $"Everything Search returned indexed files and folders through the SDK. Fast file discovery is ready to use.\n\nIndexed items reported: {indexedCount:N0}",
                CloseButtonText = "OK",
                DefaultButton = YaguDialogDefaultButton.Close,
                ShowTitleBar = false,
                Width = 560,
                Height = 300,
            });
        return true;
    }

    private sealed record EverythingStartupDetection(
        string? EsPath,
        bool EverythingRunning,
        string? EverythingExePath);

    private static EverythingStartupDetection DetectEverythingStartupState()
    {
        string? esPath = FileLister.FindEsExe();
        bool everythingRunning = IsEverythingProcessRunning();
        string? everythingExePath = esPath is not null ? FindEverythingExe(esPath) : null;
        everythingExePath ??= FindEverythingExeStandalone();

        return new EverythingStartupDetection(esPath, everythingRunning, everythingExePath);
    }

    private static bool IsEverythingProcessRunning()
    {
        Process[] processes = Process.GetProcessesByName("Everything");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    private static string? FindEverythingExe(string esPath)
    {
        // Everything.exe is typically in the same directory as es.exe
        var dir = Path.GetDirectoryName(esPath);
        if (dir != null)
        {
            var candidate = Path.Combine(dir, "Everything.exe");
            if (File.Exists(candidate))
            {
                YaguLog.For("MainWindow").LogInformation("FindEverythingExe: found at {Candidate}", candidate);
                return candidate;
            }
        }
        // Check standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path))
            {
                YaguLog.For("MainWindow").LogInformation("FindEverythingExe: found at {Path}", path);
                return path;
            }
        }
        YaguLog.For("MainWindow").LogWarning("FindEverythingExe: NOT FOUND (esPath was '{EsPath}', dir was '{Dir}')", esPath, dir);
        return null;
    }

    private static string? FindEverythingExeStandalone()
    {
        // Check registry install dirs for Everything.exe even when es.exe wasn't found
        foreach (var installDir in FileLister.GetEverythingInstallDirsFromRegistry())
        {
            var candidate = Path.Combine(installDir, "Everything.exe");
            if (File.Exists(candidate))
            {
                YaguLog.For("MainWindow").LogInformation("FindEverythingExeStandalone: found via registry at {Candidate}", candidate);
                return candidate;
            }
        }
        // Standard install locations
        foreach (var path in new[]
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"C:\Program Files (x86)\Everything\Everything.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe"),
        })
        {
            if (File.Exists(path))
            {
                YaguLog.For("MainWindow").LogInformation("FindEverythingExeStandalone: found at {Path}", path);
                return path;
            }
        }
        YaguLog.For("MainWindow").LogInformation("FindEverythingExeStandalone: Everything.exe not found in any standard location");
        return null;
    }

    private async Task CheckFirstRunResultStoreTempLocationAsync()
    {
        var probeTimer = Stopwatch.StartNew();
        ResultStoreTempLocationProbe probe = _preparedResultStoreTempLocationProbe
            ?? await ResultStoreTempLocationService.ProbeForStartupAsync(
                ViewModel.SearchResultTempDirectory,
                ViewModel.HasChosenSearchResultTempDirectory);
        probeTimer.Stop();
        YaguLog.For("MainWindow").LogInformation(
            "Search-result temp-location probe completed off the UI thread in {ElapsedMilliseconds} ms with {DriveCount} eligible drive(s).",
            probeTimer.ElapsedMilliseconds,
            probe.DriveOptions.Count);

        if (probe.CurrentDirectoryIsUsable)
            return;

        string? launchDrive = probe.LaunchDriveRoot;
        IReadOnlyList<ResultStoreTempDriveOption> options = probe.DriveOptions;

        ResultStoreTempLocationWindowResult result;
        _ownedModalWindowDepth++;
        try
        {
            result = await ResultStoreTempLocationWindow.ShowAsync(
                _hwnd,
                launchDrive,
                options,
                ViewModel.SearchResultTempDirectory);
        }
        finally
        {
            _ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);
        }

        if (!result.Accepted)
            return;

        ViewModel.SearchResultTempDirectory = result.SelectedOption?.TempDirectory ?? Path.GetTempPath();
        ViewModel.HasChosenSearchResultTempDirectory = true;
        await ViewModel.PersistSettingsAsync();
    }
}
