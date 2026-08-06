using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using System.Globalization;

namespace Yagu.ViewModels;

/// <summary>
/// Computing content-index status: resolving the target roots, refreshing the current and
/// all-drive index health, recording which roots the last search actually used the index for, and
/// building the detailed status/bypass tooltips.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Resolves the directory roots a search will target. When the <see cref="Directory"/> box has a
    /// value, that single directory is used; when it is empty the user is asking to "search all
    /// drives", so every eligible drive root is returned (fixed always; network/removable/cloud per
    /// the corresponding settings). An empty result means there is nothing to search.
    /// </summary>
    public IReadOnlyList<string> ResolveTargetRoots()
    {
        string normalizedDirectory = DriveEnumerator.NormalizeSearchRoot(Directory);
        if (normalizedDirectory.Length > 0)
            return [normalizedDirectory];

        return DriveEnumerator.GetSearchRoots(
            SearchAllDrivesIncludesNetwork,
            SearchAllDrivesIncludesRemovable,
            SearchAllDrivesIncludesCloud);
    }

    /// <summary>
    /// Updates the main-window content-index availability indicator (plan §6.2) for the folders a
    /// search covers. It reports only whether a usable index <em>exists</em> for each root — a fact
    /// knowable today from generation existence alone, with no USN journal, worker, or pruning — so it
    /// is safe and honest before the deferred hot-path integration lands. The read runs off the UI
    /// thread through the managed <see cref="ContentIndexManager"/> (crash-safe: it never memory-maps
    /// an index file and validates checksums), and a missing/corrupt scope counts as "no index" rather
    /// than throwing into the UI. The indicator never implies acceleration; its tooltip states files
    /// are still read live in this build.
    /// </summary>
    private async Task RefreshIndexStatusAsync(IReadOnlyList<string> roots, bool useThisSearch)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
        {
            // Keep a muted "Index: off" indicator visible after a menu-driven persistent disable (this
            // session only) so the status menu — and its "Enable indexing" command — stays reachable.
            if (_indexOffIndicatorSticky && !_settings.EnableContentIndex && _settings.ShowIndexStatusInMainWindow)
                ShowIndexDisabledIndicator();
            else
                ShowIndexStatus = false;
            IndexStatusFoldersWithoutIndex = Array.Empty<string>();
            IndexStatusRegisteredFoldersWithoutIndex = Array.Empty<string>();
            _currentIndexBuiltRoots = Array.Empty<string>();
            _currentIndexBuiltUtc = null;
            _currentIndexDatesByRoot.Clear();
            _currentIndexFreshnessByRoot.Clear();
            OnPropertyChanged(nameof(IndexStatusCanAddFolder));
            OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));
            return;
        }

        var rootsCopy = roots.ToArray();
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount);
        string storageDir = _settings.IndexStorageDirectory;
        bool masterEnabled = _settings.EnableContentIndex;
        int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(_settings.IndexMaxJournalCatchupRecords);

        // Remember the search context so a finishing background build can recompute the indicator for it.
        _lastIndexStatusRoots = rootsCopy;
        _lastIndexStatusUseThisSearch = useThisSearch;

        IndexAvailability availability;
        List<string> missingRoots;
        List<(string Root, DateTimeOffset? BuiltUtc, DateTimeOffset? CreatedUtc, DateTimeOffset? LastIncrementalUpdateUtc, ContentIndexManager.ScopeFreshnessStatus Freshness)> builtRoots;
        try
        {
            (availability, missingRoots, builtRoots) = await Task.Run(() =>
            {
                var provider = DefaultContentIndexPathProvider.Create(storageDir);
                var manager = new ContentIndexManager(provider, retained);
                int withIndex = 0;
                var missing = new List<string>();
                var built = new List<(string, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?, ContentIndexManager.ScopeFreshnessStatus)>();
                foreach (string root in rootsCopy)
                {
                    try
                    {
                        string indexRoot = manager.ResolveBestAvailableIndexRoot(root, _settings.IndexedRoots);
                        IndexMetadataStatus meta = manager.GetMetadataStatusForRoot(indexRoot);
                        if (meta.Exists && meta.MetadataReadable && meta.Health == IndexStorageHealth.Healthy)
                        {
                            ContentIndexManager.ScopeFreshnessStatus freshness = manager.GetScopeFreshnessStatus(
                                indexRoot,
                                ContentIndexFreshnessEvaluator.CreateReader(
                                    maxCatchupRecords,
                                    TimeSpan.FromSeconds(AppSettings.NormalizeFileIoTimeoutSeconds(_settings.FileIoTimeoutSeconds))));
                            if (!freshness.NeedsAttention)
                                withIndex++;
                            if (!built.Any(item => string.Equals(item.Item1, indexRoot, StringComparison.OrdinalIgnoreCase)))
                                built.Add((indexRoot, meta.BuiltUtc, meta.CreatedUtc, meta.LastIncrementalUpdateUtc, freshness));
                        }
                        else
                        {
                            missing.Add(root);
                        }
                    }
                    catch
                    {
                        // A missing/corrupt scope simply counts as "no index"; never throw into the UI.
                        missing.Add(root);
                    }
                }
                return (ContentIndexUiStatus.Availability(masterEnabled, useThisSearch, withIndex, rootsCopy.Length), missing, built);
            }).ConfigureAwait(true);
        }
        catch
        {
            ShowIndexStatus = false;
            IndexStatusFoldersWithoutIndex = Array.Empty<string>();
            IndexStatusRegisteredFoldersWithoutIndex = Array.Empty<string>();
            _currentIndexBuiltRoots = Array.Empty<string>();
            _currentIndexBuiltUtc = null;
            _currentIndexDatesByRoot.Clear();
            _currentIndexFreshnessByRoot.Clear();
            OnPropertyChanged(nameof(IndexStatusCanAddFolder));
            OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));
            ApplyAllDriveIndexHealthStatus(force: !IsSearchActive);
            return;
        }

        // Capture which searched roots currently have a readable index and the oldest of their build times,
        // so the status-bar right-click menu can show "Index date … (click to rebuild)" for them.
        _currentIndexBuiltRoots = builtRoots.Select(b => b.Root).ToArray();
        _currentIndexDatesByRoot.Clear();
        _currentIndexFreshnessByRoot.Clear();
        foreach (var built in builtRoots)
        {
            _currentIndexDatesByRoot[IndexScopeIdentity.NormalizePath(built.Root)] =
                (built.CreatedUtc ?? built.BuiltUtc, built.BuiltUtc, built.LastIncrementalUpdateUtc);
            _currentIndexFreshnessByRoot[IndexScopeIdentity.NormalizePath(built.Root)] = built.Freshness;
        }
        DateTimeOffset? oldestBuilt = null;
        foreach (var built in builtRoots)
        {
            DateTimeOffset? builtUtc = built.BuiltUtc;
            if (builtUtc is { } t && (oldestBuilt is null || t < oldestBuilt))
                oldestBuilt = t;
        }
        _currentIndexBuiltUtc = oldestBuilt;

        bool addable = availability is IndexAvailability.None or IndexAvailability.Partial;
        string[] registeredMissing = addable
            ? missingRoots.Where(root => IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is not null).ToArray()
            : Array.Empty<string>();
        string[] unregisteredMissing = addable
            ? missingRoots.Where(root => IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is null).ToArray()
            : Array.Empty<string>();
        // Only genuinely unregistered roots flow into Add folder. A registered-but-unbuilt root opens
        // Settings ▸ Indexing instead, where Build now can create its first on-disk generation.
        IndexStatusFoldersWithoutIndex = unregisteredMissing;
        IndexStatusRegisteredFoldersWithoutIndex = registeredMissing;
        OnPropertyChanged(nameof(IndexStatusCanAddFolder));
        OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));

        // Background build/warm activity owns the indicator until it finishes.
        if (_activeIndexBuilds > 0 || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return;
        // A B0 gate attempt has already produced a more precise status for this search (accelerating or
        // bypassed). Do not replace it with the coarser presence-only "Index: available" result.
        if (_indexRuntimeStatusRunId == Volatile.Read(ref _searchRunId)
            && _indexRuntimeAttemptedRoots.Count > 0)
            return;

        KeyValuePair<string, ContentIndexManager.ScopeFreshnessStatus>[] freshnessFailures = _currentIndexFreshnessByRoot
            .Where(static pair => pair.Value.NeedsAttention)
            .ToArray();
        if (freshnessFailures.Length > 0)
        {
            int rebuildCount = freshnessFailures.Count(static pair => pair.Value.RequiresRebuild);
            IndexStatusGlyph = ContentIndexUiStatus.StatusWarningGlyph;
            IndexStatusText = rebuildCount switch
            {
                1 => "Index: rebuild required",
                > 1 => $"Index: {rebuildCount} rebuilds required",
                _ => "Index: freshness unavailable",
            };
            IndexStatusTooltip = "One or more index files are structurally valid, but their drive change-journal freshness can no longer be proven. "
                + string.Join(" ", freshnessFailures.Select(static pair => $"{pair.Key}: {pair.Value.Problem}"))
                + BuildIndexRootStatusDetails()
                + BuildIndexDateDetails()
                + (rebuildCount > 0
                    ? " Hover to rebuild the repairable index, or open Settings \u25B8 Indexing for details."
                    : " Open Settings \u25B8 Indexing for details.");
            ShowIndexStatus = true;
            ApplyAllDriveIndexHealthStatus(force: !IsSearchActive);
            return;
        }

        bool onlyRegisteredUnbuilt = availability == IndexAvailability.None
            && registeredMissing.Length > 0
            && unregisteredMissing.Length == 0;
        IndexStatusGlyph = onlyRegisteredUnbuilt
            ? ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Bypassed)
            : ContentIndexUiStatus.AvailabilityGlyph(availability);
        IndexStatusText = onlyRegisteredUnbuilt
            ? (rootsCopy.Length == 1 ? "Index: not built for this folder" : "Index: registered but not built")
            : (rootsCopy.Length > 1 && availability == IndexAvailability.None
                ? "Index: none"
                : ContentIndexUiStatus.AvailabilityLabel(availability));
        string tooltip = ContentIndexUiStatus.AvailabilityTooltip(availability);
        if (registeredMissing.Length > 0)
            tooltip = (registeredMissing.Length == 1 && rootsCopy.Length == 1
                    ? "This folder is in your indexed-folders list, but it has no usable index yet. "
                    : registeredMissing.Length == 1
                        ? "One searched folder is in your indexed-folders list but has no usable index yet. "
                    : "Some searched folders are in your indexed-folders list but have no usable index yet. ")
                + "Click to open Settings \u25B8 Indexing and choose Build now.";
        if (unregisteredMissing.Length > 0)
            tooltip += " Click to add a folder to the index.";
        tooltip += BuildIndexRootStatusDetails();
        tooltip += BuildIndexDateDetails();
        // Not currently building: explain when indexing runs (manual / at startup / when idle).
        tooltip += BuildIndexSchedulingDetails();
        IndexStatusTooltip = tooltip;
        ShowIndexStatus = ContentIndexUiStatus.ShouldShowAvailability(availability);
        ApplyAllDriveIndexHealthStatus(force: !IsSearchActive);
    }

    /// <summary>Refreshes search-context index health for the directory currently shown in the search
    /// box. Called when the user commits a directory and around searches/builds; launch-time global
    /// visibility is handled separately by <see cref="RefreshAllDriveIndexStatus"/>.</summary>
    public void RefreshCurrentIndexStatus()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(RefreshCurrentIndexStatus);
            return;
        }
        if (_disposed)
            return;

        _ = RefreshIndexStatusAsync(
            ResolveTargetRoots(),
            UseContentIndex && _settings.EnableContentIndex);
    }

    /// <summary>Builds a launch-time health snapshot for every ready local fixed drive plus every
    /// explicitly maintained index root. The snapshot is deliberately independent of the current
    /// search directory, so changing/searching one folder cannot hide a bad index on another drive.</summary>
    public void RefreshAllDriveIndexStatus()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(RefreshAllDriveIndexStatus);
            return;
        }
        if (_disposed)
            return;

        int generation = Interlocked.Increment(ref _allDriveIndexHealthRefreshGeneration);
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
        {
            _allDriveIndexHealth = Array.Empty<IndexRootHealthEntry>();
            AllDriveIndexStatusText = string.Empty;
            return;
        }

        AllDriveIndexStatusText = "Checking local drive index health…";
        if (!IsIndexBuildActive && !IsIndexWarmActive && !IsIndexWarmPausedForSearch && !IsSearchActive)
        {
            IndexStatusGlyph = "\uE895"; // sync/checking
            IndexStatusText = "Index: checking all drives";
            IndexStatusTooltip = "Yagu is checking the content-index metadata and change-journal freshness for every ready local drive.";
            ShowIndexStatus = true;
        }

        string[] registeredRoots = IndexedRootsPolicy.Normalize(_settings.IndexedRoots).ToArray();
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(_settings.IndexRetainedGenerationCount);
        string storageDir = _settings.IndexStorageDirectory;
        int maxCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(_settings.IndexMaxJournalCatchupRecords);
        int fileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(_settings.FileIoTimeoutSeconds);
        _ = RefreshAllDriveIndexStatusAsync(
            generation,
            registeredRoots,
            retained,
            storageDir,
            maxCatchupRecords,
            fileIoTimeoutSeconds);
    }

    private async Task RefreshAllDriveIndexStatusAsync(
        int generation,
        string[] registeredRoots,
        int retained,
        string storageDir,
        int maxCatchupRecords,
        int fileIoTimeoutSeconds)
    {
        IReadOnlyList<IndexRootHealthEntry> health;
        try
        {
            health = await Task.Run(() =>
            {
                string[] roots = DriveEnumerator.GetSearchRoots(
                        includeNetwork: false,
                        includeRemovable: false,
                        includeCloud: false)
                    .Concat(registeredRoots)
                    .Where(static root => !string.IsNullOrWhiteSpace(root))
                    .Select(IndexScopeIdentity.NormalizePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var provider = DefaultContentIndexPathProvider.Create(storageDir);
                var manager = new ContentIndexManager(provider, retained);
                var rows = new List<IndexRootHealthEntry>(roots.Length);
                foreach (string root in roots)
                {
                    try
                    {
                        rows.Add(ReadAllDriveIndexHealth(
                            manager,
                            root,
                            registeredRoots,
                            maxCatchupRecords,
                            fileIoTimeoutSeconds));
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new IndexRootHealthEntry(
                            root,
                            IndexRootHealthKind.StorageProblem,
                            $"health check failed ({ex.GetType().Name}) — searches scan live"));
                    }
                }
                return (IReadOnlyList<IndexRootHealthEntry>)rows;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            health = new IndexRootHealthEntry[]
            {
                new IndexRootHealthEntry(
                    "Local drives",
                    IndexRootHealthKind.StorageProblem,
                    $"health check failed ({ex.GetType().Name}) — searches scan live"),
            };
        }

        if (_disposed || generation != Volatile.Read(ref _allDriveIndexHealthRefreshGeneration))
            return;

        _allDriveIndexHealth = health;
        AllDriveIndexStatusText = string.Join(
            Environment.NewLine,
            health.Select(static row => $"{row.Root} — {row.Status}"));
        ApplyAllDriveIndexHealthStatus(force: true);
    }

    private static IndexRootHealthEntry ReadAllDriveIndexHealth(
        ContentIndexManager manager,
        string root,
        IReadOnlyList<string> registeredRoots,
        int maxCatchupRecords,
        int fileIoTimeoutSeconds)
    {
        bool registered = IndexedRootsPolicy.FindBestCoveringRoot(registeredRoots, root) is not null;
        if (!registered)
        {
            // A ready drive remains in the all-drive overview after it is removed from IndexedRoots, but
            // any exact on-disk scope is now leftover/unmaintained data. Do not keep evaluating its journal
            // or let it raise a global freshness warning; Settings ▸ Indexing still surfaces it for add/delete.
            IndexMetadataStatus leftover = manager.GetMetadataStatusForRoot(root);
            return ContentIndexUiStatus.UnregisteredRootHealth(root, leftover.Exists);
        }

        string indexRoot = manager.ResolveBestAvailableIndexRoot(root, registeredRoots);
        IndexMetadataStatus metadata = manager.GetMetadataStatusForRoot(indexRoot);

        if (metadata.Exists && metadata.MetadataReadable && metadata.Health == IndexStorageHealth.Healthy)
        {
            ContentIndexManager.ScopeFreshnessStatus freshness = manager.GetScopeFreshnessStatus(
                indexRoot,
                ContentIndexFreshnessEvaluator.CreateReader(
                    maxCatchupRecords,
                    TimeSpan.FromSeconds(fileIoTimeoutSeconds)));
            string date = FormatAllDriveIndexDate(metadata);
            return freshness.State switch
            {
                ContentIndexManager.ScopeFreshnessState.Fresh => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.Healthy,
                    "healthy — up to date" + date),
                ContentIndexManager.ScopeFreshnessState.Dirty => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.ChangesPending,
                    "healthy — "
                        + (freshness.DirtyCount == 1
                            ? "1 recent filesystem change pending indexing"
                            : $"{freshness.DirtyCount:N0} recent filesystem changes pending indexing")
                        + "; affected files scan live until the next update"
                        + date),
                ContentIndexManager.ScopeFreshnessState.Uncertain when freshness.RequiresRebuild => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.RebuildRequired,
                    "rebuild required — " + (freshness.Problem ?? "freshness cannot be proven"),
                    indexRoot),
                _ => new IndexRootHealthEntry(
                    root,
                    IndexRootHealthKind.FreshnessUnavailable,
                    "freshness unavailable — live scan only — "
                        + (freshness.Problem ?? "freshness cannot be proven"),
                    IncrementalRoot: freshness.RawStatus == UsnReadStatus.Incomplete ? indexRoot : null),
            };
        }

        if (metadata.Exists)
        {
            bool canRebuild = metadata.Health != IndexStorageHealth.SourceMissing
                && System.IO.Directory.Exists(indexRoot);
            string problem = metadata.Problem ?? "The active index metadata is not usable.";
            return new IndexRootHealthEntry(
                root,
                canRebuild ? IndexRootHealthKind.RebuildRequired : IndexRootHealthKind.StorageProblem,
                ContentIndexUiStatus.StorageHealthLabel(metadata.Health) + " — " + problem,
                canRebuild ? indexRoot : null);
        }

        return new IndexRootHealthEntry(
            root,
            IndexRootHealthKind.BuildRequired,
            "registered, but the index is not built",
            BuildRoot: indexRoot);
    }

    private static string FormatAllDriveIndexDate(IndexMetadataStatus metadata)
    {
        DateTimeOffset? timestamp = metadata.LastIncrementalUpdateUtc ?? metadata.CreatedUtc ?? metadata.BuiltUtc;
        if (timestamp is not { } value)
            return string.Empty;
        string label = metadata.LastIncrementalUpdateUtc is not null ? "last updated" : "created";
        return $" · {label} {value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}";
    }

    /// <summary>Applies global warning precedence without replacing the current-search explanation.
    /// A forced call owns the idle/startup indicator; ordinary search refreshes invoke the non-forced
    /// form, which preserves active acceleration in the label while also reporting how many other roots
    /// need attention.</summary>
    private bool ApplyAllDriveIndexHealthStatus(
        bool force = false,
        IndexSearchCoverage? activeSearchCoverage = null)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow
            || _allDriveIndexHealth.Count == 0
            || IsIndexBuildActive || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return false;

        bool needsAttention = _allDriveIndexHealth.Any(static root => root.NeedsAttention);
        if (!force && !needsAttention)
            return false;
        if (force && !needsAttention && IsSearchActive)
            return false; // healthy global state must not hide active search coverage

        // A drive-health refresh can finish after B0 has already reported acceleration. Recover the
        // current activity here as well as accepting the immediate caller's value, so that late refresh
        // never collapses "accelerating (x of y need attention)" back to only the warning count.
        if (activeSearchCoverage is null
            && IsSearchActive
            && _indexRuntimeStatusRunId == Volatile.Read(ref _searchRunId)
            && _indexRuntimeAcceleratedRootPaths.Count > 0)
        {
            int searchedRoots = _lastIndexStatusRoots.Count > 0
                ? _lastIndexStatusRoots.Count
                : _indexRuntimeAttemptedRoots.Count;
            activeSearchCoverage = _indexRuntimeAcceleratedRootPaths.Count == searchedRoots
                ? IndexSearchCoverage.Full
                : IndexSearchCoverage.Partial;
        }

        IndexStatusGlyph = ContentIndexUiStatus.AllDriveHealthGlyph(_allDriveIndexHealth);
        IndexStatusText = ContentIndexUiStatus.AllDriveHealthLabel(_allDriveIndexHealth, activeSearchCoverage);
        if (force)
        {
            IndexStatusTooltip = ContentIndexUiStatus.AllDriveHealthSummary(_allDriveIndexHealth)
                + " Hover for the status of each drive and indexed folder."
                + BuildIndexSchedulingDetails();
        }
        ShowIndexStatus = true;
        return true;
    }

    private void ResetRuntimeIndexStatus(int runId)
    {
        _indexRuntimeStatusRunId = runId;
        _indexRuntimeAttemptedRoots.Clear();
        _indexRuntimeAcceleratedRootPaths.Clear();
        _indexRuntimeBypassReasonsByRoot.Clear();
        _indexRuntimeBypassReason = null;
    }

    /// <summary>Receives the per-root gate decision at B0 (off the UI thread) and immediately replaces
    /// the availability-only indicator with the truthful state for the active search.</summary>
    private void ReportContentIndexAttempt(int runId, string root, bool accelerated, string reason)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ReportContentIndexAttempt(runId, root, accelerated, reason));
            return;
        }
        if (runId != Volatile.Read(ref _searchRunId))
            return; // stale callback from a superseded search
        if (_indexRuntimeStatusRunId != runId)
            ResetRuntimeIndexStatus(runId);
        string normalizedRoot = IndexScopeIdentity.NormalizePath(root);
        _indexRuntimeAttemptedRoots.Add(normalizedRoot);
        bool registeredButUnbuilt = !accelerated
            && reason.Contains("no trusted index", StringComparison.OrdinalIgnoreCase)
            && IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, normalizedRoot) is not null;

        if (accelerated)
        {
            _indexRuntimeAcceleratedRootPaths.Add(normalizedRoot);
            _indexRuntimeBypassReasonsByRoot.Remove(normalizedRoot);
        }
        else
        {
            // A gate can begin accelerated and later fail safe at B1. Replace that root's optimistic B0
            // status instead of ignoring the repeated callback, so the indicator never claims that a
            // full live-scan fallback is still accelerating.
            _indexRuntimeAcceleratedRootPaths.Remove(normalizedRoot);
            _indexRuntimeBypassReasonsByRoot[normalizedRoot] = reason;
            _indexRuntimeBypassReason = reason;
        }

        if (registeredButUnbuilt)
        {
            IndexStatusFoldersWithoutIndex = IndexStatusFoldersWithoutIndex
                .Where(path => !string.Equals(IndexScopeIdentity.NormalizePath(path), normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            IndexStatusRegisteredFoldersWithoutIndex = IndexStatusRegisteredFoldersWithoutIndex
                .Append(normalizedRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            OnPropertyChanged(nameof(IndexStatusCanAddFolder));
            OnPropertyChanged(nameof(IndexStatusCanBuildRegisteredFolder));
        }

        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow
            || IsIndexBuildActive || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return;

        int attempted = _indexRuntimeAttemptedRoots.Count;
        int acceleratedRoots = _indexRuntimeAcceleratedRootPaths.Count;
        int searchedRoots = _lastIndexStatusRoots.Count > 0 ? _lastIndexStatusRoots.Count : attempted;
        IndexSearchCoverage? activeSearchCoverage = null;
        if (acceleratedRoots > 0 && acceleratedRoots == searchedRoots)
        {
            activeSearchCoverage = IndexSearchCoverage.Full;
            IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Full);
            IndexStatusText = ContentIndexUiStatus.AcceleratingLabel;
            IndexStatusTooltip = "The content index is actively pruning files for this search. Matching candidates are still verified live."
                + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                + BuildIndexDateDetails();
        }
        else if (acceleratedRoots > 0)
        {
            activeSearchCoverage = IndexSearchCoverage.Partial;
            IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Partial);
            IndexStatusText = "Index: partially accelerating";
            IndexStatusTooltip = "The content index is accelerating some searched roots; other roots are being scanned live. "
                + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                + BuildIndexDateDetails();
        }
        else
        {
            IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(IndexSearchCoverage.Bypassed);
            if (registeredButUnbuilt)
            {
                IndexStatusText = "Index: not built for this folder";
                IndexStatusTooltip = $"{normalizedRoot} is in your indexed-folders list, but it has no usable index yet. "
                    + "Click to open Settings \u25B8 Indexing and choose Build now."
                    + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                    + BuildIndexDateDetails();
            }
            else
            {
                bool catchupLimitFailure = IsIndexCatchupLimitReason(_indexRuntimeBypassReason);
                bool freshnessFailure = IsIndexFreshnessRepairReason(_indexRuntimeBypassReason);
                if (catchupLimitFailure)
                {
                    IndexStatusText = "Index: update needed";
                    IndexStatusTooltip = $"The index for {root} is beyond the configured change-journal catch-up limit. "
                        + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                        + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                        + BuildIndexDateDetails()
                        + " Open Settings \u25B8 Indexing to increase the catch-up limit, or rebuild explicitly.";
                }
                else if (freshnessFailure)
                {
                    IndexStatusText = "Index: rebuild required";
                    IndexStatusTooltip = $"The index for {root} cannot prove change-journal freshness. "
                        + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                        + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                        + BuildIndexDateDetails()
                        + " Hover to rebuild the affected index.";
                }
                else
                {
                    IndexStatusText = "Index: available \u00b7 not accelerated";
                    IndexStatusTooltip = $"An index is available for {root}, but it cannot accelerate this query. "
                        + DescribeIndexBypassReason(_indexRuntimeBypassReason)
                        + BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)
                        + BuildIndexDateDetails();
                }
            }
        }
        ShowIndexStatus = true;
        ApplyAllDriveIndexHealthStatus(activeSearchCoverage: activeSearchCoverage);
    }

    private static string DescribeIndexBypassReason(string? reason)
    {
        if (reason?.Contains("no required trigram", StringComparison.OrdinalIgnoreCase) == true)
            return "The query has no safe required trigram, so Yagu is scanning files live.";
        if (reason?.Contains("not selective", StringComparison.OrdinalIgnoreCase) == true)
            return "The query would leave too many candidates, so a live scan is faster.";
        if (reason?.Contains("Incomplete", StringComparison.OrdinalIgnoreCase) == true)
            return "The index checkpoint is more than the configured change-journal catch-up limit behind, so Yagu cannot prove the layer is fresh. Increase the catch-up limit and update the index, or rebuild it.";
        if (reason?.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase) == true)
            return "The saved index checkpoint is ahead of the drive's live change journal, usually because the journal was reset or recreated. Rebuild the affected index to establish a valid checkpoint.";
        if (reason?.Contains("GapDetected", StringComparison.OrdinalIgnoreCase) == true)
            return "The drive change journal no longer contains every change since this index layer was built. Rebuild the affected index to restore freshness.";
        if (reason?.Contains("JournalIdChanged", StringComparison.OrdinalIgnoreCase) == true)
            return "The drive change journal was reset after this index layer was built. Rebuild the affected index to establish a new freshness checkpoint.";
        if (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true)
            return "Yagu cannot prove that this index layer includes every recent file change. Rebuild the affected index to restore freshness.";
        return string.IsNullOrWhiteSpace(reason)
            ? "Yagu is scanning files live."
            : $"Yagu is scanning files live: {reason}.";
    }

    /// <summary>
    /// Builds a user-facing per-root breakdown for multi-root/all-drives index tooltips. Availability
    /// comes from the cheap manifest refresh; runtime callbacks add exact accelerated/bypass states. The
    /// aggregate completion summary only carries a count, so when the worker path did not callback per
    /// root, remaining accelerated slots are assigned to the built roots (the only roots that could have
    /// accelerated). A single-root search returns an empty suffix to keep its tooltip compact.
    /// </summary>
    private string BuildIndexRootStatusDetails(int acceleratedRootCount = 0, bool postSearch = false)
    {
        string[] roots = _lastIndexStatusRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length <= 1)
            return string.Empty;

        var builtRoots = _currentIndexBuiltRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registeredUnbuilt = IndexStatusRegisteredFoldersWithoutIndex
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(IndexScopeIdentity.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceleratedRoots = new HashSet<string>(_indexRuntimeAcceleratedRootPaths, StringComparer.OrdinalIgnoreCase);

        // Worker pruning reports aggregate coverage even when it does not invoke the in-process per-root
        // attempt callback. Infer those roots only from manifest-backed roots, never from an unindexed root.
        int remainingAccelerated = Math.Max(0, acceleratedRootCount - acceleratedRoots.Count);
        if (remainingAccelerated > 0)
        {
            foreach (string root in roots)
            {
                if (remainingAccelerated == 0) break;
                if (builtRoots.Contains(root) && acceleratedRoots.Add(root))
                    remainingAccelerated--;
            }
        }

        var lines = new List<string>(roots.Length + 1) { "Drive/folder index status:" };
        foreach (string root in roots)
        {
            string state;
            if (acceleratedRoots.Contains(root))
                state = postSearch ? "accelerated this search" : "accelerating this search";
            else if (_indexRuntimeBypassReasonsByRoot.TryGetValue(root, out string? reason))
                state = "scanned live — " + FormatIndexRootBypassReason(reason);
            else if (TryGetCurrentIndexFreshnessForSearchRoot(root, out var freshness) && freshness.RequiresRebuild)
                state = "rebuild required — " + (freshness.Problem ?? "freshness cannot be proven");
            else if (TryGetCurrentIndexFreshnessForSearchRoot(root, out freshness) && freshness.NeedsAttention)
                state = "freshness unavailable — scanning live — " + (freshness.Problem ?? "freshness cannot be proven");
            else if (registeredUnbuilt.Contains(root))
                state = "registered, but the index is not built";
            else if (!builtRoots.Contains(root))
                state = "not indexed";
            else
                state = postSearch ? "index available, but scanned live" : "index available";
            lines.Add($"  {root} — {state}");
        }
        return Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string FormatIndexRootBypassReason(string? reason)
    {
        if (reason?.Contains("no required trigram", StringComparison.OrdinalIgnoreCase) == true)
            return "query has no safe required trigram";
        if (reason?.Contains("not selective", StringComparison.OrdinalIgnoreCase) == true)
            return "a live scan is faster for this query";
        if (reason?.Contains("no trusted index", StringComparison.OrdinalIgnoreCase) == true)
            return "no trusted index";
        if (reason?.Contains("Incomplete", StringComparison.OrdinalIgnoreCase) == true)
            return "change-journal catch-up limit reached";
        if (reason?.Contains("CheckpointAhead", StringComparison.OrdinalIgnoreCase) == true)
            return "saved checkpoint is ahead of the live change journal";
        if (reason?.Contains("GapDetected", StringComparison.OrdinalIgnoreCase) == true)
            return "change journal no longer covers the index checkpoint";
        if (reason?.Contains("JournalIdChanged", StringComparison.OrdinalIgnoreCase) == true)
            return "change journal was reset after the index was built";
        if (reason?.Contains("layer not fresh", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("JournalDiscontinuity", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("CheckpointInvalid", StringComparison.OrdinalIgnoreCase) == true)
            return "index freshness cannot be proven";
        return string.IsNullOrWhiteSpace(reason) ? "index was not used" : reason.Trim().TrimEnd('.');
    }

    private bool TryGetCurrentIndexFreshnessForSearchRoot(
        string searchRoot,
        out ContentIndexManager.ScopeFreshnessStatus freshness)
    {
        string normalized = IndexScopeIdentity.NormalizePath(searchRoot);
        if (_currentIndexFreshnessByRoot.TryGetValue(normalized, out freshness))
            return true;
        string? covering = IndexedRootsPolicy.FindBestCoveringRoot(
            _currentIndexFreshnessByRoot.Keys.ToArray(), normalized);
        return covering is not null && _currentIndexFreshnessByRoot.TryGetValue(covering, out freshness);
    }

    /// <summary>Builds the timestamp section shared by every index-status hover state. A single index
    /// gets compact Created/Active generation/Last updated lines; multi-root searches identify each indexed root.</summary>
    private string BuildIndexDateDetails()
    {
        if (_currentIndexDatesByRoot.Count == 0)
            return string.Empty;

        static string Format(DateTimeOffset value)
            => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

        if (_currentIndexDatesByRoot.Count == 1)
        {
            var dates = _currentIndexDatesByRoot.Values.First();
            var lines = new List<string>(2);
            if (dates.CreatedUtc is { } created)
                lines.Add($"Created: {Format(created)}");
            if (dates.BuiltUtc is { } built && built != dates.CreatedUtc)
                lines.Add($"Active generation built: {Format(built)}");
            if (dates.LastIncrementalUpdateUtc is { } updated)
                lines.Add($"Last incremental update: {Format(updated)}");
            return lines.Count == 0 ? string.Empty : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }

        var rootLines = new List<string>(_currentIndexDatesByRoot.Count + 1) { "Index dates:" };
        foreach (var pair in _currentIndexDatesByRoot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var parts = new List<string>(2);
            if (pair.Value.CreatedUtc is { } created)
                parts.Add($"created {Format(created)}");
            if (pair.Value.BuiltUtc is { } built && built != pair.Value.CreatedUtc)
                parts.Add($"active generation built {Format(built)}");
            if (pair.Value.LastIncrementalUpdateUtc is { } updated)
                parts.Add($"updated incrementally {Format(updated)}");
            if (parts.Count > 0)
                rootLines.Add($"  {pair.Key} — {string.Join(" · ", parts)}");
        }
        return rootLines.Count == 1 ? string.Empty : Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, rootLines);
    }

    /// <summary>Places the automatic-indexing schedule in its own paragraph below the date section so
    /// it cannot run into the Created/Last updated line in the status hover surface.</summary>
    private string BuildIndexSchedulingDetails()
        => Environment.NewLine + Environment.NewLine
            + ContentIndexUiStatus.SchedulingHint(_settings.IndexBuildTrigger);

    /// <summary>
    /// Upgrades the main-window index indicator from pre-search <em>availability</em> to real post-search
    /// <em>coverage</em> (plan §6.2): once a search finishes, its <see cref="IndexAccelerationInfo"/> says
    /// how many searched roots the index actually accelerated, so the glyph honestly reflects Full/Partial/
    /// Bypassed. Leaves the availability indicator untouched when the feature/setting is off or the index
    /// did not participate (a null summary or no opted-in root).
    /// </summary>
    private void UpdateIndexCoverageStatus(IndexAccelerationInfo? acceleration)
    {
        if (!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow)
            return;
        if (acceleration is null || acceleration.RequestedRoots <= 0)
            return;
        // Background build/warm activity owns the indicator instead of coverage.
        if (_activeIndexBuilds > 0 || IsIndexWarmActive || IsIndexWarmPausedForSearch)
            return;

        int accelerated = acceleration.AcceleratedRoots;
        int liveScanned = Math.Max(0, acceleration.RequestedRoots - accelerated);
        IndexSearchCoverage coverage = ContentIndexUiStatus.Coverage(
            enabled: true, usedThisSearch: true, accelerated, liveScanned);

        IndexStatusGlyph = ContentIndexUiStatus.CoverageGlyph(coverage);
        IndexStatusText = ContentIndexUiStatus.CoverageLabel(coverage);
        IndexStatusTooltip = ContentIndexUiStatus.CoverageTooltip(coverage, acceleration.FilesPruned)
            + BuildIndexRootStatusDetails(accelerated, postSearch: true)
            + BuildIndexDateDetails()
            + BuildIndexSchedulingDetails();
        ShowIndexStatus = ContentIndexUiStatus.ShouldShowStatus(true, _settings.ShowIndexStatusInMainWindow);
        ApplyAllDriveIndexHealthStatus();
    }
}
