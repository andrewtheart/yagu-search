using Yagu.Services;
using Yagu.Services.Index;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.ViewModels;

/// <summary>
/// Content-index lifecycle commands: registering folders, building and rebuilding indexes
/// (including the blocking foreground rebuild), incremental refresh, freshness repair, and index
/// deletion.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Enables the content-index feature (if it is off), registers <paramref name="folder"/> as an indexed
    /// root, persists settings, and starts a background build of that folder. Backs the main-window
    /// "add this folder to the index" affordances (the clickable status indicator and the first-run
    /// onboarding prompt). Never throws — the build runs off the UI thread and a failure only logs; the
    /// caller is responsible for any large-folder confirmation before calling this.
    /// </summary>
    public async Task AddFolderToIndexAndBuildAsync(string folder)
    {
        string? effectiveRoot = await RegisterFolderForIndexAsync(folder).ConfigureAwait(true);
        if (effectiveRoot is null)
            return;

        YaguLog.For("ContentIndex").LogInformation(
            "Onboarding: registered effective root '{EffectiveRoot}' for requested folder '{RequestedRoot}' and starting a background index build.",
            effectiveRoot, folder.Trim());
        StartBackgroundIndexBuild(effectiveRoot);
    }

    /// <summary>
    /// Registers several folders as indexed roots at once (first-run onboarding lets the user pick more
    /// than one), optionally sets which automatic build trigger(s) maintain them and the update mode those
    /// passes use, persists settings a single time, then starts a background build for each distinct
    /// effective root. Folders already covered by a broader registered root are skipped. Never throws.
    /// </summary>
    public async Task AddFoldersToIndexAndBuildAsync(IReadOnlyList<string> folders, string? buildTrigger, string? updateMode = null)
    {
        if (folders is null || folders.Count == 0)
            return;

        _settings.EnableContentIndex = true;
        UseContentIndex = true;
        if (!string.IsNullOrWhiteSpace(buildTrigger))
            _settings.IndexBuildTrigger = AppSettings.NormalizeIndexBuildTrigger(buildTrigger);
        // Onboarding decides the update mode alongside the trigger, so an automatic trigger cannot be left
        // paired with ManualFullRebuild (which would only ever create missing indexes).
        if (!string.IsNullOrWhiteSpace(updateMode))
            _settings.IndexUpdateMode = AppSettings.NormalizeIndexUpdateMode(updateMode);

        var effectiveRoots = new List<string>(folders.Count);
        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            string root = folder.Trim();
            // Skip a folder already covered by an equal/broader root registered so far (including ones
            // added earlier in this same loop), so we never register or build a redundant child.
            if (IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is not null)
                continue;
            _settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, root);
            string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) ?? root;
            if (!effectiveRoots.Contains(effectiveRoot, StringComparer.OrdinalIgnoreCase))
                effectiveRoots.Add(effectiveRoot);
        }

        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));

        foreach (string effectiveRoot in effectiveRoots)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Onboarding: registered effective root '{EffectiveRoot}' and starting a background index build.",
                effectiveRoot);
            StartBackgroundIndexBuild(effectiveRoot);
        }
    }

    /// <summary>Registers <paramref name="folder"/> and awaits its initial build behind the same
    /// full-window blocking overlay used by an explicit rebuild. This is the pre-search readiness
    /// dialog path: the user chose "Add to index", so Yagu must stay blocked until that requested
    /// operation completes rather than silently starting the ordinary onboarding background build.</summary>
    public async Task AddFolderToIndexAndBuildBlockingAsync(string folder)
    {
        string? effectiveRoot = await RegisterFolderForIndexAsync(folder).ConfigureAwait(true);
        if (effectiveRoot is null)
            return;

        YaguLog.For("ContentIndex").LogInformation(
            "Pre-search readiness: registered effective root '{EffectiveRoot}' for requested folder '{RequestedRoot}' and starting a blocking index build.",
            effectiveRoot, folder.Trim());
        await RunCurrentIndexBlockingAsync(new[] { effectiveRoot }, rebuild: false).ConfigureAwait(true);
    }

    private async Task<string?> RegisterFolderForIndexAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        string root = folder.Trim();
        string? existingCover = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root);

        // Opt in: turn the master feature on, default the per-search toggle on, and register the root.
        _settings.EnableContentIndex = true;
        _settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, root);
        string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) ?? root;
        UseContentIndex = true;
        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));

        if (existingCover is not null)
        {
            StatusText = $"{root} is already covered by the content index root {existingCover}.";
            return null;
        }

        return effectiveRoot;
    }

    /// <summary>Enrolls an existing leftover index in the maintained-root list without rebuilding it.
    /// The next automatic maintenance pass evaluates its freshness and applies a safe incremental update
    /// when possible. This settings-only action is safe while another root is being indexed.</summary>
    public async Task MaintainExistingIndexAsync(string folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = MaintainExistingIndexAsync(folder));
            return;
        }
        if (_disposed || string.IsNullOrWhiteSpace(folder))
            return;

        string requestedRoot = IndexScopeIdentity.NormalizePath(folder);
        string? existingCover = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, requestedRoot);
        _settings.EnableContentIndex = true;
        _settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, requestedRoot);
        string effectiveRoot = IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, requestedRoot)
            ?? requestedRoot;
        UseContentIndex = true;
        await PersistSettingsAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));
        OnPropertyChanged(nameof(CurrentDirectoryIndexRoot));
        StatusText = existingCover is null
            ? $"Added {effectiveRoot} to maintained index folders. Its existing index will be checked by the next maintenance pass."
            : $"{requestedRoot} is already maintained by the covering index root {existingCover}.";
        RefreshCurrentIndexStatus();
        RefreshAllDriveIndexStatus();
    }

    /// <summary>Deletes the exact stored index for <paramref name="folder"/> without changing maintained
    /// roots. The caller supplies confirmation. A concurrent writer is rejected by the index lease.</summary>
    public async Task DeleteStoredIndexAsync(string folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = DeleteStoredIndexAsync(folder));
            return;
        }
        if (_disposed || string.IsNullOrWhiteSpace(folder))
            return;
        if (IsIndexBuildActive || IsIndexRebuildBlocking)
        {
            StatusText = "Wait for the current index operation to finish before deleting stored index data.";
            return;
        }

        string root = IndexScopeIdentity.NormalizePath(folder);
        var provider = DefaultContentIndexPathProvider.Create(_settings.IndexStorageDirectory);
        var manager = new ContentIndexManager(
            provider,
            AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount));
        try
        {
            bool existed = await Task.Run(() => manager.DeleteScope(ContentIndexManager.ScopeIdForRoot(root)))
                .ConfigureAwait(true);
            StatusText = existed
                ? $"Deleted the stored content index for {root}."
                : $"No stored content index existed for {root}.";
        }
        catch (IndexWriteBusyException)
        {
            StatusText = "Another index operation is running; delete the stored index after it finishes.";
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Deleting the stored index for '{Root}' failed.", root);
            StatusText = $"Deleting the stored index for {root} failed: {ex.Message}";
        }
        finally
        {
            RefreshCurrentIndexStatus();
            RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>
    /// Starts an immediate rebuild for an already-registered root from the status indicator's context
    /// menu. Does not modify registration or settings. The operation uses the same worker-backed,
    /// cancellable background path as onboarding and exposes normal progress/pause behavior.
    /// </summary>
    public void RebuildRegisteredIndexNow(string folder)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => RebuildRegisteredIndexNow(folder));
            return;
        }
        if (_disposed || IsIndexBuildActive || IsIndexingPaused || string.IsNullOrWhiteSpace(folder))
            return;

        string root = IndexScopeIdentity.NormalizePath(folder);
        if (!IndexedRootsPolicy.Contains(_settings.IndexedRoots, root))
            return; // context action is only valid for a registered root

        YaguLog.For("ContentIndex").LogInformation(
            "Status menu: rebuilding registered index root '{Root}'.", root);
        StartBackgroundIndexBuild(root, rebuild: true);
    }

    /// <summary>
    /// Describes the on-disk index for the currently searched roots for the status-bar indicator's
    /// "Index date … (click to rebuild)" menu item. Returns <c>true</c> (with a formatted
    /// <paramref name="dateLabel"/> and the <paramref name="roots"/> to rebuild) only when at least one
    /// searched root currently has a readable index; the date is the oldest of those roots' build times,
    /// rendered in local time (or "unknown" when a manifest carries no timestamp).
    /// </summary>
    public bool TryGetCurrentIndexRebuildTarget(out string dateLabel, out IReadOnlyList<string> roots)
    {
        roots = _currentIndexBuiltRoots;
        if (_currentIndexBuiltRoots.Count == 0)
        {
            dateLabel = string.Empty;
            return false;
        }

        string date = _currentIndexBuiltUtc is { } built
            ? built.ToLocalTime().ToString("MM/ddd/yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : "unknown";
        dateLabel = $"Index date: {date} (click to rebuild)";
        return true;
    }

    /// <summary>
    /// Returns indexed roots whose active-search bypass or all-drive health snapshot identifies as a
    /// repairable freshness/storage failure. Query-shape/selectivity bypasses and unsupported journals
    /// are intentionally excluded because rebuilding cannot help them.
    /// </summary>
    public bool TryGetCurrentIndexFreshnessRepairTarget(
        out string actionLabel,
        out IReadOnlyList<string> roots)
    {
        var builtRoots = _currentIndexBuiltRoots
            .Select(IndexScopeIdentity.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] repairRoots = _indexRuntimeBypassReasonsByRoot
            .Where(pair => IsIndexFreshnessRepairReason(pair.Value)
                && (!TryGetCurrentIndexFreshnessForSearchRoot(pair.Key, out var freshness)
                    || !freshness.NeedsAttention
                    || freshness.RequiresRebuild))
            .Select(pair =>
            {
                string searchedRoot = IndexScopeIdentity.NormalizePath(pair.Key);
                return builtRoots.Contains(searchedRoot)
                    ? searchedRoot
                    : IndexedRootsPolicy.FindBestCoveringRoot(builtRoots, searchedRoot);
            })
            .Where(root => root is not null)
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        repairRoots = repairRoots
            .Concat(_currentIndexFreshnessByRoot
                .Where(static pair => pair.Value.RequiresRebuild)
                .Select(static pair => pair.Key))
            .Concat(_allDriveIndexHealth
                .Where(static root => root.CanRepair)
                .Select(static root => root.RepairRoot!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        roots = repairRoots;
        actionLabel = repairRoots.Length switch
        {
            1 => $"Rebuild {repairRoots[0]} index",
            > 1 => $"Rebuild {repairRoots.Length} indexes",
            _ => string.Empty,
        };
        return repairRoots.Length > 0;
    }

    private static bool IsIndexFreshnessRepairReason(string? reason)
        => !IsIndexCatchupLimitReason(reason)
            && (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsIndexCatchupLimitReason(string? reason)
        => reason?.Contains("Incomplete", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Rebuilds the content index for <paramref name="roots"/> while a full-window blocking overlay
    /// prevents any other interaction, updating that overlay with live progress. Invoked from the
    /// status-bar indicator's "Index date … (click to rebuild)" menu item. The build uses the same
    /// worker-backed path as a background build, but here it is awaited and the rest of the UI is
    /// intentionally blocked until it finishes. Never throws.
    /// </summary>
    public async Task RebuildCurrentIndexBlockingAsync(IReadOnlyList<string> roots)
        => await RunCurrentIndexBlockingAsync(roots, rebuild: true).ConfigureAwait(true);

    private async Task RunCurrentIndexBlockingAsync(IReadOnlyList<string> roots, bool rebuild)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = RunCurrentIndexBlockingAsync(roots, rebuild));
            return;
        }
        if (_disposed || IsIndexRebuildBlocking || IsIndexBuildActive || IsIndexingPaused
            || roots is null || roots.Count == 0)
            return;

        var targets = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
        if (targets.Length == 0)
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(IndexBuildCancellationToken);
        _indexRebuildCancellation = cancellation;
        _indexBlockingOperationIsRebuild = rebuild;
        OnPropertyChanged(nameof(IndexRebuildOverlayTitle));
        OnPropertyChanged(nameof(IndexRebuildCancelButtonText));
        IsIndexRebuildCancelling = false;
        IsIndexRebuildBlocking = true;
        IndexRebuildProgressPercent = 0;
        string operation = rebuild ? "rebuild" : "build";
        IndexRebuildProgressText = targets.Length == 1
            ? $"Preparing to {operation} the index for {targets[0]}…"
            : $"Preparing to {operation} {targets.Length} indexes…";
        await Task.Yield(); // allow the full-window overlay to paint before worker startup

        try
        {
            for (int i = 0; i < targets.Length && !cancellation.IsCancellationRequested; i++)
                await BuildOneBlockingAsync(targets[i], i, targets.Length, rebuild, cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_indexRebuildCancellation, cancellation))
                _indexRebuildCancellation = null;
            IsIndexRebuildBlocking = false;
            IsIndexRebuildCancelling = false;
            IndexRebuildProgressPercent = 0;
            IndexRebuildProgressText = string.Empty;
            if (_lastIndexStatusRoots.Count > 0)
                await RefreshIndexStatusAsync(_lastIndexStatusRoots, _lastIndexStatusUseThisSearch).ConfigureAwait(true);
            RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>Runs an explicit incremental maintenance pass for one physical index root. This never
    /// falls back to a full rebuild: if journal continuity still cannot be proven, the existing index is
    /// retained unchanged and the user can search live or explicitly choose Rebuild. When
    /// <paramref name="increasedCatchupLimit"/> is supplied, that user-approved larger bounded journal
    /// replay limit is persisted before the pass.</summary>
    public async Task RefreshCurrentIndexIncrementallyAsync(string root, int? increasedCatchupLimit = null)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => _ = RefreshCurrentIndexIncrementallyAsync(root, increasedCatchupLimit));
            return;
        }
        if (_disposed || IsIndexBuildActive || IsIndexRebuildBlocking || IsIndexingPaused
            || string.IsNullOrWhiteSpace(root))
            return;

        string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
        if (increasedCatchupLimit is { } requested)
        {
            int normalized = AppSettings.NormalizeIndexMaxJournalCatchupRecords(requested);
            if (normalized > _settings.IndexMaxJournalCatchupRecords)
            {
                _settings.IndexMaxJournalCatchupRecords = normalized;
                await PersistSettingsAsync().ConfigureAwait(true);
            }
        }

        BeginIndexBuildActivity(normalizedRoot, isIncremental: true);
        StatusText = $"Updating the {normalizedRoot} content index incrementally…";
        try
        {
            IndexMaintenanceOperation operation = IndexBuildOperationFactory.CreateMaintenance(
                _settings,
                new[] { normalizedRoot },
                IndexMaintenanceOperation.ModeIncremental,
                rebuildWhenDirty: false);
            operation.AllowFullRebuildFallback = false;
            operation.AllowCompatibilityRebuild = false;
            operation.ForceRefresh = true;
            var coordinator = new IndexBuildCoordinator();
            IndexMaintenanceSuccess result = await coordinator.RunMaintenancePreferWorkerAsync(
                operation,
                _settings.IndexUseNativeWorker,
                IndexBuildCancellationToken,
                (progressRoot, percent, stage) => ReportIndexBuildProgress(progressRoot, percent, stage)).ConfigureAwait(true);

            IndexMaintenanceRootResult? rootResult = result.Roots.FirstOrDefault();
            StatusText = rootResult?.Action switch
            {
                IndexMaintenanceActions.DeltaAppended => $"Updated the {normalizedRoot} index incrementally.",
                IndexMaintenanceActions.Compacted => $"Updated and compacted the {normalizedRoot} index.",
                IndexMaintenanceActions.Reanchored => $"The {normalizedRoot} index was already current; its checkpoint was refreshed.",
                IndexMaintenanceActions.Skipped => $"The {normalizedRoot} index is already up to date.",
                _ when rootResult?.Outcome == "needsFullRebuild" =>
                    $"The incremental update could not establish journal continuity for {normalizedRoot}; the existing index was kept unchanged. Search live or explicitly rebuild it.",
                _ => $"The incremental update for {normalizedRoot} did not complete; the existing index was kept unchanged.",
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Incremental update for {normalizedRoot} was cancelled; the existing index was kept unchanged.";
        }
        catch (IndexWriteBusyException)
        {
            StatusText = "Another index operation is already running.";
        }
        catch (IndexDiskFullException ex)
        {
            OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "On-demand incremental refresh failed for '{Root}'.", normalizedRoot);
            StatusText = $"Incremental update for {normalizedRoot} failed; the existing index was kept unchanged.";
        }
        finally
        {
            EndIndexBuildActivity();
            if (_lastIndexStatusRoots.Count > 0)
                await RefreshIndexStatusAsync(_lastIndexStatusRoots, _lastIndexStatusUseThisSearch).ConfigureAwait(true);
            RefreshAllDriveIndexStatus();
        }
    }

    /// <summary>Requests cooperative cancellation of only the on-demand blocking rebuild. The previously
    /// published index remains available because the builder publishes staged generations atomically.</summary>
    public void CancelCurrentIndexRebuild()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(CancelCurrentIndexRebuild);
            return;
        }
        if (!IsIndexRebuildBlocking || IsIndexRebuildCancelling)
            return;

        IsIndexRebuildCancelling = true;
        IndexRebuildProgressText = _indexBlockingOperationIsRebuild
            ? "Canceling the rebuild… The existing index remains available."
            : "Canceling the build… No incomplete index will be published.";
        _indexRebuildCancellation?.Cancel();
        YaguLog.For("ContentIndex").LogInformation(
            "User cancelled the blocking index {Action}.", _indexBlockingOperationIsRebuild ? "rebuild" : "build");
    }

    private async Task BuildOneBlockingAsync(string root, int index, int total, bool rebuild, CancellationToken token)
    {
        IndexBuildOperation operation = IndexBuildOperationFactory.CreateBuild(_settings, root, rebuild);
        bool useWorker = _settings.IndexUseNativeWorker;
        long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);

        BeginIndexBuildActivity(root);
        try
        {
            var coordinator = new IndexBuildCoordinator();
            await coordinator.BuildFullScopePreferWorkerAsync(
                operation,
                useWorker,
                token,
                progress: p => ReportRebuildBlockingProgress(root, index, total,
                    IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes)),
                pdfProgress: p => ReportRebuildBlockingProgress(root, index, total,
                    p.Total <= 0 ? -1 : 90 + Math.Clamp(p.Processed * 5 / p.Total, 0, 5)),
                imageOcrProgress: p => ReportRebuildBlockingProgress(root, index, total,
                    p.Total <= 0 ? -1 : 95 + Math.Clamp(p.Processed * 4 / p.Total, 0, 4))).ConfigureAwait(true);
            YaguLog.For("ContentIndex").LogInformation("Blocking index {Action} complete for '{Root}'.", rebuild ? "rebuild" : "build", root);
        }
        catch (OperationCanceledException)
        {
            YaguLog.For("ContentIndex").LogInformation("Blocking index {Action} for '{Root}' was paused/cancelled.", rebuild ? "rebuild" : "build", root);
        }
        catch (IndexDiskFullException ex)
        {
            YaguLog.For("ContentIndex").LogWarning("Blocking index {Action} for '{Root}' stopped: {Error}", rebuild ? "rebuild" : "build", root, ex.Message);
            OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent);
        }
        catch (IndexWriteBusyException)
        {
            YaguLog.For("ContentIndex").LogInformation("Blocking index {Action} for '{Root}' skipped because another index operation is running.", rebuild ? "rebuild" : "build", root);
        }
        catch (Exception ex)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Blocking index {Action} failed for '{Root}'.", rebuild ? "rebuild" : "build", root);
        }
        finally
        {
            EndIndexBuildActivity();
        }
    }

    /// <summary>Self-marshalling progress sink for <see cref="RebuildCurrentIndexBlockingAsync"/>: folds a
    /// per-root 0–99 estimate (or -1 unknown) into the overall 0–100 overlay progress across all roots and
    /// refreshes the overlay's status line.</summary>
    private void ReportRebuildBlockingProgress(string root, int index, int total, int percent)
    {
        void apply()
        {
            if (!IsIndexRebuildBlocking)
                return;
            if (percent >= 0)
            {
                double overall = (index * 100.0 + Math.Clamp(percent, 0, 100)) / Math.Max(1, total);
                IndexRebuildProgressPercent = Math.Clamp(overall, 0, 100);
            }
            string suffix = percent >= 0 ? $" {percent}%" : string.Empty;
            string verb = _indexBlockingOperationIsRebuild ? "Rebuilding" : "Building";
            IndexRebuildProgressText = total > 1
                ? $"{verb} {root} ({index + 1} of {total})…{suffix}"
                : $"{verb} {root}…{suffix}";
        }
        if (!_dispatcher.TryEnqueue(apply))
            apply();
    }
}
