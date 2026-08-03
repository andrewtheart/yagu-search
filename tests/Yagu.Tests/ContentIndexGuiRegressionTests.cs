using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the Phase 2 content-index GUI integration (plan §6.1/§6.2/§6.3).
/// The Settings <b>Indexing</b> tab, the Advanced Options toggle, the CLI-command-generator emit, and
/// the view-model wiring live in WinUI/CLI files that are not compiled into the test assembly, so their
/// contracts are pinned here as source substrings — matching the repo's source-pin convention for
/// untestable UI code. The pure decision logic is unit-tested in <c>ContentIndexUiStatusTests</c>.
/// </summary>
public sealed class ContentIndexGuiRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static readonly string MainViewModelSource = Read("src", "Yagu", "ViewModels", "MainViewModel.cs");
    private static readonly string MainWindowXaml = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml");
    private static readonly string CliCommandSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.CliCommand.cs");
    private static readonly string SettingsIndexingSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.Indexing.cs");
    private static readonly string SettingsIndexingActionsSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.IndexingActions.cs");
    private static readonly string SettingsWindowSource = Read("src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");
    private static readonly string StartupChecksSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs");
    private static readonly string IndexOnboardingSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.IndexOnboarding.cs");
    private static readonly string PreviewSectionsSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewSections.cs");
    private static readonly string SearchInputSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");
    private static readonly string MainWindowCodeBehindSource = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs");
    private static readonly string SettingsServiceSource = Read("src", "Yagu", "Services", "SettingsService.cs");
    private static readonly string HelpMarkdown = Read("HELP.md");

    // ── MainViewModel: session-only per-search toggle + settings accessor + SearchOptions wiring ──

    [Fact]
    public void MainViewModel_DeclaresUseContentIndexObservableProperty()
        => Assert.Contains("[ObservableProperty] public partial bool UseContentIndex { get; set; }", MainViewModelSource);

    [Fact]
    public void MainViewModel_SeedsUseContentIndexFromEffectiveDefault()
        => Assert.Contains("UseContentIndex = _settings.ContentIndexActiveByDefault;", MainViewModelSource);

    [Fact]
    public void MainViewModel_ExposesSettingsAccessorForIndexingTab()
        => Assert.Contains("internal AppSettings Settings => _settings;", MainViewModelSource);

    [Fact]
    public void MainViewModel_ThreadsUseContentIndexIntoSearchOptionsGatedByMaster()
        => Assert.Contains("UseContentIndex = UseContentIndex && _settings.EnableContentIndex,", MainViewModelSource);

    [Fact]
    public void MainViewModel_AttachesContentIndexGateFactoryPerRoot()
    {
        // The pruning gate factory is a closure attached per target root and invoked later, off the UI
        // thread, at the start of that root's discovery (plan §5) — no index/journal I/O on the UI thread.
        Assert.Contains("AttachContentIndexGateFactory(rootOptions, root);", MainViewModelSource);
        Assert.Contains("void AttachContentIndexGateFactory(SearchOptions rootOptions, string root)", MainViewModelSource);
        Assert.Contains("if (!rootOptions.UseContentIndex)", MainViewModelSource);
        Assert.Contains("rootOptions.ContentIndexGateFactory = () =>", MainViewModelSource);
        // Size gate (memory safety): a layered index whose on-disk size exceeds the in-process limit is
        // never loaded/warmed — it always live-scans, so a huge index never adds a multi-GB resident
        // footprint that would degrade every search.
        Assert.Contains("int maxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(settings.IndexMaxInProcessSizeMB);", MainViewModelSource);
        Assert.Contains("ResolveBestAvailableIndexRoot(root, settings.IndexedRoots)", MainViewModelSource);
        Assert.Contains("if (!ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(pathProvider, indexRoot, retained, maxInProcessSizeMB))", MainViewModelSource);
        // Cold-open guard: the launch path starts warming immediately; a different cold root requested
        // during a search is queued until that search finishes.
        Assert.Contains("if (!ContentIndexSearchGate.IsScopeWarm(pathProvider, indexRoot, retained))", MainViewModelSource);
        Assert.Contains("StartContentIndexWarmup(indexRoot);", MainViewModelSource);
        Assert.Contains("var gate = ContentIndexSearchGate.TryCreate(", MainViewModelSource);
    }

    [Fact]
    public void StartupWarmup_IsImmediateVisibleCancellableAndWarnedBeforeSearch()
    {
        string adminSettings = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdminSettings.cs");
        string searchInput = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");

        Assert.Contains("ViewModel.StartContentIndexWarmup(ViewModel.Directory);", StartupChecksSource);
        Assert.Contains("IndexStatusText = \"Indexing: preparing...\";", MainViewModelSource);
        Assert.Contains("store.TryOpenLayered(retainDocuments: false, cancellationToken: token)", MainViewModelSource);
        Assert.Contains("PauseContentIndexWarmupForSearch()", MainViewModelSource);
        Assert.Contains("ResumeContentIndexWarmupAfterSearch()", MainViewModelSource);
        Assert.DoesNotContain("for (int i = 0; i < 120", MainViewModelSource);

        Assert.Contains("CheckIndexWarmupAndWarnAsync()", searchInput);
        Assert.Contains("Indexing is warming up. If you proceed with the search, search speed will not be accelerated and indexing will be paused.", adminSettings);
        Assert.Contains("Content = \"Don't show this warning again\"", adminSettings);
        Assert.Contains("PrimaryButtonText = \"Proceed with search\"", adminSettings);
        Assert.Contains("CloseButtonText = \"Wait for index\"", adminSettings);
        Assert.Contains("ShowTitleBar = false", adminSettings);
    }

    [Fact]
    public void HddParallelismLimit_IsPerRootAndDoesNotLeakIntoLaterNvmeSearches()
    {
        string adminSettings = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdminSettings.cs");

        Assert.DoesNotContain("SetSessionParallelismOverride", adminSettings);
        Assert.DoesNotContain("_sessionParallelismOverrideIndex", MainViewModelSource);
        Assert.Contains("int baseParallelism = ResolveParallelism(ParallelismIndex);", MainViewModelSource);
        Assert.Contains("bool isHardDisk = Yagu.Helpers.DiskTypeDetector.IsHardDisk(root);", MainViewModelSource);
        Assert.Contains("if (LimitParallelismOnHdd && isHardDisk)", MainViewModelSource);
        Assert.Contains("parallelism = hddParallelismOverride is int overrideIndex ? ResolveParallelism(overrideIndex) : 1;", MainViewModelSource);
    }

    [Fact]
    public void PreSearchGate_WarnsAboutMissingOrStaleIndexesWithDirectActions()
    {
        Assert.Contains("string normalizedDirectory = DriveEnumerator.NormalizeSearchRoot(Directory);", MainViewModelSource);
        Assert.Contains("return [normalizedDirectory];", MainViewModelSource);
        Assert.Contains("CheckContentIndexReadinessAndWarnAsync()", SearchInputSource);
        Assert.Contains("ContentIndexReadinessChecker.CheckRoot(", SearchInputSource);
        Assert.Contains("Title = \"Content index needs attention\"", SearchInputSource);
        Assert.Contains("PrimaryButtonText = \"Search live\"", SearchInputSource);
        Assert.Contains("AddAction(\"Index & wait\", \"add-wait\")", SearchInputSource);
        Assert.Contains("AddAction(\"Index & search now\", \"add-search\")", SearchInputSource);
        Assert.Contains("AddAction(\"Build & search now\", \"build-search\")", SearchInputSource);
        Assert.Contains("CanAttemptIncrementalIndexRefresh(issue)", SearchInputSource);
        Assert.Contains("AddAction(\"Rebuild index\", \"rebuild\")", SearchInputSource);
        Assert.Contains("await ViewModel.RebuildCurrentIndexBlockingAsync(new[] { actionIssue.IndexRoot });", SearchInputSource);
        Assert.Contains("await ViewModel.AddFolderToIndexAndBuildBlockingAsync(actionIssue.SearchRoot);", SearchInputSource);
        Assert.Contains("return true; // continue the pending search; live scan remains authoritative if repair did not complete", SearchInputSource);
        Assert.DoesNotContain("search again after it completes", SearchInputSource);
        Assert.Contains("await ViewModel.AddFolderToIndexAndBuildAsync(actionIssue.SearchRoot);", SearchInputSource);
        Assert.Contains("return true; // the background build runs while this search uses the authoritative live path", SearchInputSource);
        Assert.Contains("ViewModel.RebuildRegisteredIndexNow(actionIssue.IndexRoot);", SearchInputSource);
        Assert.Contains("_contentIndexReadinessWarningsAcknowledged.Add(issue.WarningKey);", SearchInputSource);
        Assert.Contains("acknowledgedWarnings.Contains($\"{ContentIndexReadinessIssueKind.RefreshRequired}|{warningRoot}\"", SearchInputSource);
        Assert.Contains("freshness inputs unreadable", SearchInputSource);
        Assert.Contains("UnknownRecordVersion", SearchInputSource);
        Assert.Contains("CheckpointInvalid", SearchInputSource);
        Assert.Contains("CheckpointAhead", SearchInputSource);
        Assert.Contains("JournalUnavailable", SearchInputSource);
        Assert.Contains("ShowTitleBar = false", SearchInputSource);
    }

    [Fact]
    public void PreSearchGate_WarnsBeforeCloudDriveHydration()
    {
        Assert.Contains("CheckCloudDriveScanAndWarnAsync()", SearchInputSource);
        Assert.Contains("DriveEnumerator.IsLikelyCloudDrive(new DriveInfo(root))", SearchInputSource);
        Assert.Contains("Cloud drive scan may download files", SearchInputSource);
        Assert.Contains("Scanning can cause the cloud provider to download files or metadata on demand", SearchInputSource);
        Assert.Contains("PrimaryButtonText = \"Search cloud drive\"", SearchInputSource);
        Assert.Contains("DefaultButton = YaguDialogDefaultButton.Close", SearchInputSource);
        Assert.Contains("ShowTitleBar = false", SearchInputSource);
    }

    [Fact]
    public void ZeroResultEviction_DoesNotForceGcOrTrimTheWorkingSet()
    {
        Assert.Contains("if (IsSearching && enqueued > 0)", MainViewModelSource);
        Assert.Contains("SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromSeconds(3));", MainViewModelSource);
        Assert.DoesNotContain("if (IsSearching)\n                                SearchService.CollectForMemoryPressureIfDue", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_DisposeDoesNotInvalidateSearchLifecycleGateDuringCancellation()
    {
        string dispose = ExtractFrom(MainViewModelSource, "public void Dispose()", 1700);
        Assert.Contains("try { _cts?.Cancel(); } catch { }", dispose);
        Assert.DoesNotContain("_searchLifecycleGate.Dispose();", dispose);
        Assert.Contains("_searchLifecycleGate.Release();", MainViewModelSource);
    }

    [Fact]
    public void IndexWarmupWarning_HasPersistedDeveloperReset()
    {
        Assert.Contains("public bool SuppressIndexWarmSearchWarning", SettingsServiceSource);
        Assert.Contains("public bool SuppressIndexWarmSearchWarning", MainViewModelSource);
        Assert.Contains("ResetIndexWarmSearchWarningAsync()", MainViewModelSource);
        Assert.Contains("Reset index warm-up search warning", SettingsWindowSource);
        Assert.Contains("_viewModel.ResetIndexWarmSearchWarningAsync()", SettingsWindowSource);
        Assert.Contains("Reset index warm-up search warning", HelpMarkdown);
    }

    [Fact]
    public void MainViewModel_SkipsInProcessWarmForWorkerPruningPath()
    {
        // Stage-6 (plan §5.8): when the IndexUseWorkerQuerySessions setting is on, StartContentIndexWarmup
        // returns early — the worker maps the v3 lazily and its B0 open is cheap, so a worker-served scope
        // accelerates on the first search WITHOUT deserializing the index into the host (no in-process warm).
        Assert.Contains("public void StartContentIndexWarmup(string? folder)", MainViewModelSource);
        Assert.Contains("if (_settings.IndexUseWorkerQuerySessions)", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_PassesWorkerCandidateSourceWhenOptedIn()
    {
        // Opt-in worker path (plan §3.3): a long-lived worker source is created once and passed to the gate
        // when IndexUseNativeWorker is on; it is disposed with the view model.
        Assert.Contains("settings.IndexUseNativeWorker ? GetOrCreateIndexWorkerSource() : null", MainViewModelSource);
        Assert.Contains("candidateSource: candidateSource", MainViewModelSource);
        Assert.Contains("private Yagu.Services.Index.IIndexCandidateSource GetOrCreateIndexWorkerSource()", MainViewModelSource);
        Assert.Contains("new Yagu.Services.Index.IndexWorkerQuerySource(_indexWorkerClient)", MainViewModelSource);
        Assert.Contains("try { _indexWorkerClient?.Dispose(); } catch { }", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_WiresStage5PruningScanFactoryWhenWorkerQuerySessionsOn()
    {
        // Stage-5 worker pruning (plan §5.8): gated by the IndexUseWorkerQuerySessions setting, the VM
        // sets a ContentIndexPruningScanFactory that builds+opens a PRUNING worker session via
        // ContentIndexShadowScopeBuilder.TryCreatePruningScan over the shared long-lived worker client, with a
        // unique monotonic session id per open. The in-process gate returns null when the flag is on (mutually
        // exclusive — the worker path never triggers a large in-process deserialize).
        Assert.Contains("if (settings.IndexUseWorkerQuerySessions)", MainViewModelSource);
        Assert.Contains("rootOptions.ContentIndexPruningScanFactory = survivorSink =>", MainViewModelSource);
        Assert.Contains("Yagu.Services.Index.ContentIndexShadowScopeBuilder.TryCreatePruningScan(", MainViewModelSource);
        Assert.Contains("GetOrCreateIndexWorkerClient()", MainViewModelSource);
        Assert.Contains("System.Threading.Interlocked.Increment(ref _shadowQuerySessionId)", MainViewModelSource);
        // Out-of-process size cap (IndexMaxWorkerQuerySizeMB, default 30 GB): the worker path is ALSO bounded
        // — an index larger than the worker cap live-scans instead of engaging the worker (mapped, not
        // deserialized, so the cap is far larger than the 2 GB in-process one).
        Assert.Contains("int maxWorkerQuerySizeMB = AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(settings.IndexMaxWorkerQuerySizeMB);", MainViewModelSource);
        Assert.Contains("if (!ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(workerPathProvider, indexRoot, retained, maxWorkerQuerySizeMB))", MainViewModelSource);
        Assert.Contains("mapped query index size {ResourceUsageMonitor.FormatBytes(mappedBytes)} exceeds the configured", MainViewModelSource);
        Assert.Contains("onAttempt: (active, reason) =>", MainViewModelSource);
        Assert.Contains("ReportContentIndexAttempt(runId, root, active, reason)", MainViewModelSource);
    }

    // ── SearchService: pruning gate wired into the live discovery loop (plan §5) ──

    [Fact]
    public void SearchService_CreatesContentIndexGateOffThreadAtDiscoveryStart()
    {
        string source = Read("src", "Yagu", "Services", "SearchService.cs");
        Assert.Contains("Services.Index.ContentIndexSearchGate? contentIndexGate = null;", source);
        Assert.Contains("contentIndexGate = options.ContentIndexGateFactory?.Invoke();", source);
    }

    [Fact]
    public void SearchService_GatesContentCandidatesThroughShouldContentScan()
    {
        string source = Read("src", "Yagu", "Services", "SearchService.cs");
        // Short-circuit keeps the disabled path byte-for-byte the live scan (NormalizePath only runs when
        // the gate is active — the `??=` reuses the shadow's normalized path when present, else computes it),
        // and a pruned file still counts as processed.
        Assert.Contains("if (contentIndexGate is null", source);
        Assert.Contains("|| contentIndexGate.ShouldContentScan(path, normalizedForIndex ??= Services.Index.IndexScopeIdentity.NormalizePath(path)))", source);
    }

    [Fact]
    public void SearchService_DrainsRescuePathsAfterScanDrain()
    {
        string source = Read("src", "Yagu", "Services", "SearchService.cs");
        // Plan §5.4 option (b): the B1 rescue runs in the content-workers finally (after the pending-scan
        // channel drains), in a bounded rescue-and-re-drain loop, rescanning pruned paths via ContentSearcher.
        Assert.Contains("Services.Index.B1RescuePass b1 = contentIndexGate.ReconcileB1Pass();", source);
        Assert.Contains("await RescueContentScanAsync(rescuePath).ConfigureAwait(false);", source);
        Assert.Contains("if (!b1.MorePassesUseful) break;", source);
        // The rescue must NOT be fed back into the discovery `pending` channel (that fixed B1 at end of
        // discovery, before the content scan drained).
        Assert.DoesNotContain("foreach (string rescuePath in contentIndexGate.GetPathsToRescan())", source);
    }

    [Fact]
    public void SearchOptions_ExposesContentIndexGateFactory()
    {
        string source = Read("src", "Yagu", "Models", "SearchOptions.cs");
        Assert.Contains("public Func<Services.Index.ContentIndexSearchGate?>? ContentIndexGateFactory { get; set; }", source);
    }

    // ── Post-search coverage: the index indicator upgrades availability → real coverage (plan §6.2) ──

    [Fact]
    public void SearchService_PopulatesIndexAccelerationInPerRootSummary()
    {
        string source = Read("src", "Yagu", "Services", "SearchService.cs");
        Assert.Contains("int indexAccelerationRequested = options.UseContentIndex ? 1 : 0;", source);
        Assert.Contains("int netPruned = Math.Max(0, grossPruned - rescued);", source);
        Assert.Contains("indexGateEngaged = netPruned > 0 ? 1 : 0;", source);
        Assert.Contains("indexFilesPruned = netPruned;", source);
        Assert.Contains("new IndexAccelerationInfo(1, indexGateEngaged, indexFilesPruned, indexFilesRescued)", source);
    }

    [Fact]
    public void MainViewModel_UpgradesIndicatorToCoverageAfterSearch()
    {
        Assert.Contains("UpdateIndexCoverageStatus(c.Summary.IndexAcceleration);", MainViewModelSource);
        Assert.Contains("private void UpdateIndexCoverageStatus(IndexAccelerationInfo? acceleration)", MainViewModelSource);
        Assert.Contains("ContentIndexUiStatus.Coverage(", MainViewModelSource);
        Assert.Contains("ContentIndexUiStatus.CoverageTooltip(coverage, acceleration.FilesPruned)", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_PartialIndexTooltip_ListsEveryAllDrivesRootAndItsState()
    {
        // "Index: partial" is meaningless without identifying which drives are indexed/accelerated and
        // which are live-scanned. The same per-root breakdown is appended to availability, in-progress,
        // and post-search coverage tooltips.
        Assert.Contains("private string BuildIndexRootStatusDetails(int acceleratedRootCount = 0, bool postSearch = false)", MainViewModelSource);
        Assert.Contains("Drive/folder index status:", MainViewModelSource);
        Assert.Contains("state = \"not indexed\";", MainViewModelSource);
        Assert.Contains("state = postSearch ? \"accelerated this search\" : \"accelerating this search\";", MainViewModelSource);
        Assert.Contains("state = postSearch ? \"index available, but scanned live\" : \"index available\";", MainViewModelSource);
        Assert.Contains("_indexRuntimeBypassReasonsByRoot[normalizedRoot] = reason;", MainViewModelSource);
        Assert.Contains("_indexRuntimeBypassReasonsByRoot.Clear();", MainViewModelSource);

        Assert.Contains("tooltip += BuildIndexRootStatusDetails();", MainViewModelSource);
        Assert.Contains("+ BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)\r\n", MainViewModelSource);
        Assert.Contains("+ BuildIndexRootStatusDetails(accelerated, postSearch: true)", MainViewModelSource);

        // Runtime Full/Partial compares against all searched roots, not merely roots whose gate callback
        // fired (unindexed roots have no gate and therefore no callback).
        Assert.Contains("int searchedRoots = _lastIndexStatusRoots.Count > 0 ? _lastIndexStatusRoots.Count : attempted;", MainViewModelSource);
        Assert.Contains("acceleratedRoots > 0 && acceleratedRoots == searchedRoots", MainViewModelSource);

        // Multi-root no-index state must not say "this folder".
        Assert.Contains("rootsCopy.Length > 1 && availability == IndexAvailability.None", MainViewModelSource);
        Assert.Contains("? \"Index: none\"", MainViewModelSource);
        Assert.Contains("registeredMissing.Length == 1 && rootsCopy.Length == 1", MainViewModelSource);
        Assert.Contains("One searched folder is in your indexed-folders list", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_IndexStatusHover_ShowsAvailableLifecycleDatesInEveryStatusFamily()
    {
        Assert.Contains("_currentIndexDatesByRoot", MainViewModelSource);
        Assert.Contains("meta.BuiltUtc, meta.CreatedUtc, meta.LastIncrementalUpdateUtc", MainViewModelSource);
        Assert.Contains("private string BuildIndexDateDetails()", MainViewModelSource);
        Assert.Contains("lines.Add($\"Created: {Format(created)}\");", MainViewModelSource);
        Assert.Contains("lines.Add($\"Active generation built: {Format(built)}\");", MainViewModelSource);
        Assert.Contains("lines.Add($\"Last incremental update: {Format(updated)}\");", MainViewModelSource);
        Assert.Contains("tooltip += BuildIndexDateDetails();", MainViewModelSource); // availability
        Assert.Contains("+ BuildIndexRootStatusDetails(acceleratedRoots, postSearch: false)\r\n                + BuildIndexDateDetails();", MainViewModelSource); // live acceleration/bypass
        Assert.Contains("+ BuildIndexRootStatusDetails(accelerated, postSearch: true)\r\n            + BuildIndexDateDetails()", MainViewModelSource); // completed coverage: full/partial/bypassed
        Assert.Contains("IndexStatusText = \"Index: ready\";", MainViewModelSource);
        Assert.Contains("+ BuildIndexDateDetails();", MainViewModelSource); // ready/warm states
        Assert.Contains("IndexStatusTooltip += BuildIndexDateDetails();", MainViewModelSource); // active full/incremental build
        Assert.Contains("diskFull + BuildIndexDateDetails()", MainViewModelSource); // paused disk-full build
    }

    [Fact]
    public void MainViewModel_ShowsIndexBypassAsSoonAsGateDecisionIsKnown()
    {
        Assert.Contains("onAttempt: (active, reason) =>", MainViewModelSource);
        Assert.Contains("ReportContentIndexAttempt(runId, root, active, reason)", MainViewModelSource);
        Assert.Contains("private void ReportContentIndexAttempt(int runId, string root, bool accelerated, string reason)", MainViewModelSource);
        Assert.Contains("IndexStatusText = \"Index: update needed\";", MainViewModelSource);
        Assert.Contains("IndexStatusText = \"Index: rebuild required\";", MainViewModelSource);
        Assert.Contains("IndexStatusText = \"Index: available \\u00b7 not accelerated\";", MainViewModelSource);
        Assert.Contains("reason?.Contains(\"no required trigram\"", MainViewModelSource);
        Assert.Contains("The query has no safe required trigram", MainViewModelSource);
        Assert.Contains("_indexRuntimeAttemptedRoots.Count > 0", MainViewModelSource);
        Assert.Contains("_indexRuntimeAcceleratedRootPaths.Remove(normalizedRoot);", MainViewModelSource);
        Assert.DoesNotContain("if (!_indexRuntimeAttemptedRoots.Add(IndexScopeIdentity.NormalizePath(root)))", MainViewModelSource);
    }

    // ── Per-result provenance badge: captured gates classify each result file (plan §6.2) ──

    [Fact]
    public void MainViewModel_CapturesGatesAndTagsResultProvenance()
    {
        // The factory records the live gate so per-file provenance can be classified; the capture list is
        // cleared each new search; and InitializeResultGroup tags each group off the captured gates.
        Assert.Contains("_activeIndexGates.Add(gate);", MainViewModelSource);
        Assert.Contains("_activeIndexGates.Clear();", MainViewModelSource);
        Assert.Contains("TrySetIndexProvenance(group);", MainViewModelSource);
        Assert.Contains("private void TrySetIndexProvenance(FileGroup group)", MainViewModelSource);
        Assert.Contains("gate.ClassifyProvenance(normalized)", MainViewModelSource);
        Assert.Contains("!_settings.ShowIndexProvenanceInResults", MainViewModelSource);
        // Stage-5 worker pruning path: the created pruning scan is captured too, and a worker-classified index
        // member result file is badged via WasIndexMember.
        Assert.Contains("_activePruningScans.Add(scan);", MainViewModelSource);
        Assert.Contains("scan.WasIndexMember(normalized)", MainViewModelSource);
    }

    [Fact]
    public void MainWindowXaml_HasIndexAcceleratedResultBadge()
    {
        Assert.Contains("Visibility=\"{x:Bind IsIndexAccelerated, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("The content index selected this file as a candidate", MainWindowXaml);

        // The index badge occupies the green pill's trailing grid column; the star middle column keeps
        // it pinned at the far-right edge without widening or vertically shifting the pill stack.
        Assert.Contains("<ColumnDefinition Width=\"*\" />", MainWindowXaml);
        Assert.Contains("<FontIcon Grid.Column=\"2\" Glyph=\"&#xE9F5;\"", MainWindowXaml);
    }

    // ── Advanced Options toggle (parity with CLI) ──

    [Fact]
    public void MainWindowXaml_HasUseContentIndexAdvancedOptionsToggle()
    {
        Assert.Contains("x:Name=\"UseContentIndexRow\"", MainWindowXaml);
        Assert.Contains("IsOn=\"{x:Bind ViewModel.UseContentIndex, Mode=TwoWay}\"", MainWindowXaml);
        Assert.Contains("Use content index", MainWindowXaml);
    }

    // ── CLI-command generator emits --use-index/--no-index synced to the saved default ──

    [Fact]
    public void CliCommandGenerator_EmitsUseIndexAndNoIndex()
        => Assert.Contains("parts.Add(ViewModel.UseContentIndex ? \"--use-index\" : \"--no-index\");", CliCommandSource);

    [Fact]
    public void CliCommandGenerator_GatesUseIndexAgainstSavedDefault()
        => Assert.Contains("setting => ViewModel.UseContentIndex == setting.UseContentIndexByDefault", CliCommandSource);

    // ── Settings Indexing tab: registration, groups, master gating, reset ──

    [Fact]
    public void SettingsWindow_BuildsIndexingTab()
    {
        Assert.Contains("BuildIndexingTab();", SettingsWindowSource);
        Assert.Contains("\"Indexing\" => \"\\uE8F1\",", SettingsWindowSource);
    }

    [Fact]
    public void IndexingTab_IsNamedExactlyIndexing()
        => Assert.Contains("AddTab(\"Indexing\")", SettingsIndexingSource);

    [Theory]
    [InlineData("Content Index")]
    [InlineData("Query Acceleration")]
    [InlineData("Scope & Ingestion")]
    [InlineData("Storage")]
    [InlineData("Build Scheduling")]
    [InlineData("Build Resources")]
    [InlineData("Status & Provenance")]
    [InlineData("Manage Indexes")]
    public void IndexingTab_HasGroup(string group)
        => Assert.Contains($"AddSettingsGroupBox(g, \"{group}\")", SettingsIndexingSource);

    [Fact]
    public void IndexingTab_ManageIndexes_IsImmediatelyBelowContentIndex()
    {
        int content = SettingsIndexingSource.IndexOf(
            "var featureGroup = AddSettingsGroupBox(g, \"Content Index\");",
            StringComparison.Ordinal);
        int manage = SettingsIndexingSource.IndexOf(
            "var manageGroup = AddSettingsGroupBox(g, \"Manage Indexes\");",
            StringComparison.Ordinal);
        int acceleration = SettingsIndexingSource.IndexOf(
            "var accelerationGroup = AddSettingsGroupBox(g, \"Query Acceleration\");",
            StringComparison.Ordinal);

        Assert.True(content >= 0 && content < manage && manage < acceleration,
            "Manage Indexes must be the second visual group, directly after Content Index.");
    }

    [Fact]
    public void IndexingTab_V3Copy_DistinguishesMappedWorkerFromInProcessReader()
    {
        Assert.Contains("Isolate index maintenance and candidate evaluation", SettingsIndexingSource);
        Assert.Contains("This is not the mapped query-session setting.", SettingsIndexingSource);
        Assert.Contains("Use memory-mapped worker query sessions (format-v3)", SettingsIndexingSource);
        Assert.Contains("s => s.IndexUseWorkerQuerySessions", SettingsIndexingSource);
        Assert.Contains("s => s.IndexMaxWorkerQuerySizeMB", SettingsIndexingSource);
        Assert.Contains("Mapped worker query size limit (MB, 0 = disabled):", SettingsIndexingSource);
        Assert.Contains("Use format-v3 reader for in-process queries (experimental)", SettingsIndexingSource);
        Assert.Contains("The isolated mapped query-worker path uses v3 independently of this switch.", SettingsIndexingSource);
        Assert.Contains("settings.IndexUseWorkerQuerySessions", SettingsIndexingSource);
        Assert.Contains("Active query path: the isolated mapped query worker is using format-v3 structures", SettingsIndexingSource);
        Assert.Contains("additional build I/O/time and disk space", SettingsIndexingSource);
        Assert.DoesNotContain("No search path reads them yet", SettingsIndexingSource);
    }

    [Fact]
    public void IndexingTab_OffersPositiveOnlyImageTextIndex()
    {
        Assert.Contains("Build an image-text index to prioritize likely OCR matches", SettingsIndexingSource);
        Assert.Contains("s => s.IndexBuildImageTextExtendedSource", SettingsIndexingSource);
        Assert.Contains("never skips OCR", SettingsIndexingSource);
        Assert.Contains("whole-drive builds substantially longer", SettingsIndexingSource);
    }

    [Fact]
    public void OversizedInProcessIndex_ReportsWhyItLiveScans()
    {
        Assert.Contains("active index size {ResourceUsageMonitor.FormatBytes(activeBytes)} exceeds the configured", MainViewModelSource);
        Assert.Contains("enable memory-mapped worker query sessions with format-v3 data", MainViewModelSource);
        Assert.Contains("ReportContentIndexAttempt(runId, root, false, reason);", MainViewModelSource);
    }

    [Fact]
    public void IndexingTab_SurfacesEveryCliConfigKey()
    {
        // The dedicated tab must expose every user-tunable setting (plan §6.1). The authoritative set is
        // the CLI config keys; each must be read/written in the tab source so UI and CLI never drift.
        foreach (string key in ContentIndexConfigService.Keys)
        {
            if (key == "ShareAggregateIndexTelemetry")
                continue; // privacy/telemetry opt-in — surfaced on the Privacy tab instead (see below).
            Assert.Contains($"s.{key}", SettingsIndexingSource);
        }
    }

    [Fact]
    public void IndexingTab_WorkerParallelism_IsAutomaticConfigurableAndHddAware()
    {
        Assert.Contains("Query worker parallelism (0 = automatic):", SettingsIndexingSource);
        Assert.Contains("s.IndexQueryWorkerParallelism", SettingsIndexingSource);
        Assert.Contains("Build worker parallelism (0 = automatic):", SettingsIndexingSource);
        Assert.Contains("s.IndexBuildWorkerParallelism", SettingsIndexingSource);
        Assert.DoesNotContain("Maximum concurrent folder builds:", SettingsIndexingSource);

        Assert.Contains("IndexWorkerParallelism.ResolveQueryDegree(", MainViewModelSource);
        Assert.Contains("settings.LimitParallelismOnHdd", MainViewModelSource);
        Assert.Contains("DiskTypeDetector.IsHardDisk(root)", MainViewModelSource);

        string cli = Read("src", "Yagu", "CliRunner.cs");
        Assert.Contains("IndexWorkerParallelism.ResolveQueryDegree(", cli);
        Assert.Contains("gateSettings.LimitParallelismOnHdd", cli);
        Assert.Contains("DiskTypeDetector.IsHardDisk(gateOptions.Directory)", cli);

        string factory = Read("src", "Yagu", "Services", "Index", "IndexBuildOperationFactory.cs");
        Assert.Contains("IndexWorkerParallelism.ResolveBuildDegree(", factory);
        Assert.Contains("settings.LimitParallelismOnHdd", factory);
        Assert.Contains("DiskTypeDetector.IsHardDisk(root)", factory);
    }

    [Fact]
    public void PrivacyTab_HostsAggregateIndexTelemetryToggle()
    {
        // "Share aggregate content-index metrics" is a telemetry opt-in, so its toggle lives with the
        // other telemetry controls on the Privacy tab (not the Indexing tab). It remains a CLI config key.
        Assert.Contains("_viewModel.Settings.ShareAggregateIndexTelemetry = indexTelemetryToggle.IsOn;", SettingsWindowSource);
        Assert.Contains("Share aggregate content-index metrics", SettingsWindowSource);
        Assert.DoesNotContain("s.ShareAggregateIndexTelemetry", SettingsIndexingSource);
    }

    [Fact]
    public void IndexingTab_MasterAndDefaultToggles()
    {
        Assert.Contains("s.EnableContentIndex", SettingsIndexingSource);
        Assert.Contains("s.UseContentIndexByDefault", SettingsIndexingSource);
        Assert.Contains("useByDefaultToggle.IsEnabled = master;", SettingsIndexingSource);
    }

    [Fact]
    public void IndexingTab_RestoreDefaultsUsesConfigServiceReset()
    {
        Assert.Contains("ContentIndexConfigService.Reset(_viewModel.Settings);", SettingsIndexingSource);
        Assert.Contains("Restore indexing defaults", SettingsIndexingSource);
    }

    [Fact]
    public void IndexingTab_ExposesFixedSafetyPoliciesAsReasonsNotSwitches()
    {
        // Case-sensitive dirs and cloud-only files are fixed live-only policies surfaced as reasons.
        Assert.Contains("case-sensitive", SettingsIndexingSource);
        Assert.Contains("cloud", SettingsIndexingSource);
    }

    // ── Management actions call the pure manager off the UI thread and confirm destructive ops ──

    [Fact]
    public void IndexingActions_BuildRebuildValidateDeleteClear()
    {
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(_viewModel.Settings, root, rebuild)", SettingsIndexingActionsSource);
        Assert.Contains("coordinator.BuildFullScopePreferWorkerAsync(", SettingsIndexingActionsSource);
        Assert.Contains("coordinator.ValidatePreferWorkerAsync(", SettingsIndexingActionsSource);
        Assert.Contains("manager.ClearAll()", SettingsIndexingActionsSource);
        Assert.Contains("catch (IndexWriteBusyException)", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_BuildRunsOffUiThreadAndIsCancellable()
    {
        Assert.Contains("BuildFullScopePreferWorkerAsync(", SettingsIndexingActionsSource);
        Assert.Contains("_indexBuildCts?.Cancel()", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_DestructiveOpsConfirmWithYaguDialogNotContentDialog()
    {
        Assert.Contains("YaguDialog.ShowAsync", SettingsIndexingActionsSource);
        Assert.DoesNotContain("ContentDialog", SettingsIndexingActionsSource);
        Assert.DoesNotContain("ContentDialog", SettingsIndexingSource);
    }

    [Fact]
    public void IndexingActions_UsesWin32FolderPickerNotWinAppSdkFolderPicker()
    {
        Assert.Contains("Win32FileDialog.SelectFolder(_settingsHwnd, \"Select Folder to Index\")", SettingsIndexingActionsSource);
        Assert.DoesNotContain("FolderPicker", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_ManagesIndexedRootsListViaPolicy()
    {
        Assert.Contains("BuildIndexedRootsList", SettingsIndexingActionsSource);
        Assert.Contains("_viewModel.Settings.IndexedRoots = IndexedRootsPolicy.Add(before, requestedRoot);", SettingsIndexingActionsSource);
        Assert.Contains("IndexedRootsPolicy.Remove(_viewModel.Settings.IndexedRoots", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_RefreshesGlobalHealthAfterRemovingOrDeletingAnIndex()
    {
        string remove = ExtractFrom(SettingsIndexingActionsSource, "var removeButton", 1100);
        Assert.Contains("IndexedRootsPolicy.Remove(_viewModel.Settings.IndexedRoots", remove);
        Assert.Contains("_viewModel.RefreshAllDriveIndexStatus();", remove);

        string delete = ExtractFrom(SettingsIndexingActionsSource, "private async Task RunIndexDeleteAsync()", 3300);
        Assert.Contains("manager.DeleteScope(scopeId)", delete);
        Assert.Contains("_viewModel.RefreshAllDriveIndexStatus();", delete);

        string clear = ExtractFrom(SettingsIndexingActionsSource, "private async Task RunIndexClearAllAsync()", 2300);
        Assert.Contains("manager.ClearAll()", clear);
        Assert.Contains("_viewModel.RefreshAllDriveIndexStatus();", clear);

        string vmRemove = ExtractFrom(MainViewModelSource, "public async Task RemoveFolderFromIndexAsync(string folder)", 900);
        Assert.Contains("IndexedRootsPolicy.Remove(_settings.IndexedRoots, root)", vmRemove);
        Assert.Contains("RefreshAllDriveIndexStatus();", vmRemove);
    }

    [Fact]
    public void IndexingActions_HasPerFolderGlobFilterEditor_AndResolvesPerRootPolicy()
    {
        // A "Filters…" button edits per-folder include/exclude globs stored in IndexedRootFilters.
        Assert.Contains("ShowRootFilterEditorAsync", SettingsIndexingActionsSource);
        Assert.Contains("IndexedRootFilterPolicy.Normalize(filters)", SettingsIndexingActionsSource);
        Assert.Contains("new IndexedRootFilter { Path = key", SettingsIndexingActionsSource);
        // The operation factory resolves the per-root effective policy before dispatch.
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(_viewModel.Settings, root, rebuild)", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_FolderRowsShowIndexSizeAndModernTooltip()
    {
        // Each folder row shows its own index size / doc count + a filter marker, plus a rich hover tooltip.
        Assert.Contains("BuildIndexedRootRow", SettingsIndexingActionsSource);
        Assert.Contains("FindStorageStatForRoot", SettingsIndexingActionsSource);
        Assert.Contains("ContentIndexUiStatus.FormatBytes", SettingsIndexingActionsSource);
        Assert.Contains("ToolTipService.SetToolTip(radio, BuildIndexedRootTooltip", SettingsIndexingActionsSource);
        // The per-scope sizes are cached and the rows re-render once they load.
        Assert.Contains("_lastIndexStorageSummary = summary;", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_SurfacesOrphanOnDiskIndexes_NotJustRegisteredRoots()
    {
        // A folder that has an on-disk index but is not in IndexedRoots (an orphan — built once, then
        // unregistered) is shown in the list, marked "leftover index", so the list agrees with the stats.
        Assert.Contains("CollectOrphanIndexRoots", SettingsIndexingActionsSource);
        Assert.Contains("leftover index", SettingsIndexingActionsSource);
        // The hover tooltip explains what a leftover index is and how to keep or delete it.
        Assert.Contains("Leftover index: you have an index for this folder", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_FolderRow_ShowsLiveBuildPercentAndFreshnessMarker()
    {
        // A — the row whose index is actively building overlays a live "Indexing… N%" label + a progress
        // bar (driven by the VM build state), instead of the static size/docs line, without rebuilding the
        // whole folder list on every progress tick.
        Assert.Contains("private void UpdateIndexedRootBuildProgress()", SettingsIndexingActionsSource);
        Assert.Contains("_viewModel.ActiveIndexBuildFolder", SettingsIndexingActionsSource);
        Assert.Contains("$\"Indexing\\u2026 {percent}%\"", SettingsIndexingActionsSource);
        Assert.Contains("new ProgressBar", SettingsIndexingActionsSource);
        Assert.Contains("row.Progress.IsIndeterminate = true;", SettingsIndexingActionsSource);
        Assert.DoesNotContain("row.Progress.IsIndeterminate = percent < 0;", SettingsIndexingActionsSource);
        Assert.Contains("_indexedRootRowVisuals[IndexScopeIdentity.NormalizePath(root)]", SettingsIndexingActionsSource);

        // B — an idle indexed folder shows a USN-proven freshness marker ("up to date" vs "changes
        // detected — rebuild"), computed off the UI thread alongside the storage stats.
        Assert.Contains("manager.GetScopeFreshnessStatus(stat.RootPath, reader)", SettingsIndexingActionsSource);
        Assert.Contains("changes detected \\u2014 rebuild", SettingsIndexingActionsSource);
        Assert.Contains("up to date", SettingsIndexingActionsSource);
        Assert.Contains("ComputeRootStaleness(manager, summary)", SettingsIndexingActionsSource);
        Assert.Contains("freshness lost \\u2014 rebuild required", SettingsIndexingActionsSource);
        Assert.Contains("Rebuild required · freshness lost", SettingsIndexingActionsSource);
        Assert.Contains("FindRootFreshnessStatus(stat.RootPath) is { RequiresRebuild: true }", SettingsIndexingActionsSource);
        Assert.Contains("\"Rebuild required\", () => RunStorageRebuildAsync(stat)", SettingsIndexingActionsSource);
        Assert.Contains("Freshness unavailable · live scan only", SettingsIndexingActionsSource);
        Assert.Contains("FindRootFreshnessStatus(stat.RootPath) is { NeedsAttention: true }", SettingsIndexingActionsSource);
        Assert.Contains("freshness unavailable \\u2014 live scan only", SettingsIndexingActionsSource);
        Assert.Contains("Update needed · catch-up limit reached", SettingsIndexingActionsSource);
        Assert.Contains("catch-up limit reached \\u2014 increase limit and update", SettingsIndexingActionsSource);
        Assert.Contains("freshness.RawStatus == UsnReadStatus.Incomplete", SettingsIndexingActionsSource);

        // The Settings window reacts to VM build-state changes to update the rows.
        Assert.Contains("OnIndexBuildStateChangedForRows(e.PropertyName)", SettingsWindowSource);
        Assert.Contains("nameof(MainViewModel.IsIndexBuildActive)", SettingsWindowSource);
    }

    [Fact]
    public void IndexingActions_HasOneUnifiedSelectableFolderList_NotTwoFolderInputs()
    {
        // The folders are shown as a single selectable radio list; selecting one sets the target the
        // Build/Rebuild/Validate/Delete buttons act on. There must NOT be a second, separate "folder to
        // index" scratch picker or an "automatic" list — that duplication is what confused users.
        Assert.Contains("private void RefreshIndexedRootsRadios()", SettingsIndexingActionsSource);
        Assert.Contains("GroupName = \"YaguIndexedFolders\"", SettingsIndexingActionsSource);
        Assert.Contains("_indexManageRoot = captured;", SettingsIndexingActionsSource);
        // The old two-input labels are gone.
        Assert.DoesNotContain("Folder to index:", SettingsIndexingActionsSource);
        Assert.DoesNotContain("Folders indexed automatically", SettingsIndexingActionsSource);
        Assert.DoesNotContain("ChooseIndexTargetFolder", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_ShowsStorageStats_ComputedOffUiThread_AndRefreshedAfterActions()
    {
        // The storage-stats block computes off the UI thread, then renders structured status cards.
        Assert.Contains("BuildIndexStorageStats", SettingsIndexingActionsSource);
        Assert.Contains("Task.Run(manager.GetStorageStats)", SettingsIndexingActionsSource);
        Assert.Contains("RenderIndexStorageStats(summary)", SettingsIndexingActionsSource);

        // It refreshes when the section is built plus after every build/delete/clear (>= 4 call sites).
        int refreshCalls = System.Text.RegularExpressions.Regex.Matches(
            SettingsIndexingActionsSource, "RefreshIndexStorageStatsAsync\\(\\)").Count;
        Assert.True(refreshCalls >= 5, $"Expected the stats refresh to be invoked from the section build + each action; found {refreshCalls}.");
    }

    [Fact]
    public void IndexingActions_StorageCardsAreGroupedReadableAndDirectlyActionable()
    {
        // Clear visual hierarchy: actionable/broken indexes first, healthy indexes second, with semantic
        // glyphs and colors plus text labels (never color alone).
        Assert.Contains("\"Needs attention\"", SettingsIndexingActionsSource);
        Assert.Contains("\"Healthy indexes\"", SettingsIndexingActionsSource);
        Assert.Contains("glyph = \"\\uE711\";", SettingsIndexingActionsSource);
        Assert.Contains("Microsoft.UI.Colors.Tomato", SettingsIndexingActionsSource);
        Assert.Contains("glyph = \"\\uE930\";", SettingsIndexingActionsSource);
        Assert.Contains("Microsoft.UI.Colors.LimeGreen", SettingsIndexingActionsSource);
        Assert.Contains("state = \"Valid index\";", SettingsIndexingActionsSource);
        Assert.Contains("_ => \"Broken or incomplete index\"", SettingsIndexingActionsSource);

        // Each card uses wrapped Segoe UI text rather than a monospaced diagnostic dump.
        Assert.Contains("private Border BuildIndexStorageCard(IndexStorageStat stat)", SettingsIndexingActionsSource);
        Assert.Contains("FontSize = 14", SettingsIndexingActionsSource);
        Assert.Contains("TextWrapping = TextWrapping.Wrap", SettingsIndexingActionsSource);
        Assert.DoesNotContain("Consolas, Cascadia Mono", SettingsIndexingActionsSource);

        // Explanations have direct links; no need to select a row and find a distant button.
        Assert.Contains("new HyperlinkButton", SettingsIndexingActionsSource);
        Assert.Contains("\"Repair now\", () => RunStorageRepairAsync(stat)", SettingsIndexingActionsSource);
        Assert.Contains("\"Delete stored index\", () => RunStorageDeleteAsync(stat)", SettingsIndexingActionsSource);
        Assert.Contains("\"Validate\", () => RunStorageValidateAsync(stat)", SettingsIndexingActionsSource);
        Assert.Contains("\"Rebuild\", () => RunStorageRebuildAsync(stat)", SettingsIndexingActionsSource);
        Assert.Contains("\"Add to maintained folders\", () => RegisterStorageRootAsync(stat)", SettingsIndexingActionsSource);
        Assert.Contains("parts.Add($\"{stat.DocumentCount:N0} stored content records\");", SettingsIndexingActionsSource);
        Assert.Contains("active generation built", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingActions_ClassifiesAndRepairsPartialIndexes_AndCanDeleteUnidentifiedResidue()
    {
        // Incompatible/corrupt scopes recover their checksum-validated root, show an exact reason, and
        // expose an explicit repair action that reuses the atomic worker-backed rebuild path.
        Assert.Contains("Content = \"Repair index\"", SettingsIndexingActionsSource);
        Assert.Contains("private async Task RunIndexRepairAsync()", SettingsIndexingActionsSource);
        Assert.Contains("selected is not { CanRepair: true }", SettingsIndexingActionsSource);
        Assert.Contains("await RunIndexBuildAsync(rebuild: true);", SettingsIndexingActionsSource);
        Assert.Contains("Title = \"Repair content index\"", SettingsIndexingActionsSource);
        Assert.Contains("ShowTitleBar = false", SettingsIndexingActionsSource);

        // If no trustworthy manifest can recover a root, the scope still appears and can be deleted
        // individually by its existing scope id (never reconstructed from an unknown path).
        Assert.Contains("BuildUnidentifiedIndexRow", SettingsIndexingActionsSource);
        Assert.Contains("Unidentified index data", SettingsIndexingActionsSource);
        Assert.Contains("manager.DeleteScope(scopeId)", SettingsIndexingActionsSource);
        Assert.Contains("_indexManageScopeId = capturedScopeId;", SettingsIndexingActionsSource);
        Assert.DoesNotContain("unreadable or partial", SettingsIndexingActionsSource);
    }

    // ── Startup auto-build (opt-in, fire-and-forget, off the UI thread) ──

    [Fact]
    public void Startup_RunsAutoIndexBuildFireAndForgetOffUiThread()
    {
        Assert.Contains("_ = RunAutoIndexBuildIfDueAsync();", StartupChecksSource);
        Assert.Contains("ContentIndexBuildScheduler.RootsDueAtStartup(settings)", StartupChecksSource);
        Assert.Contains("IndexBuildOperationFactory.CreateMaintenance(", StartupChecksSource);
        Assert.Contains("coordinator.RunMaintenancePreferWorkerAsync(", StartupChecksSource);
        Assert.Contains("settings.IndexUseNativeWorker", StartupChecksSource);
    }

    [Fact]
    public void Startup_AutoBuildHonorsAutomaticFullRebuildWhenDirtyMode()
    {
        // The V1 AutomaticFullRebuildWhenDirty update mode rebuilds dirty roots at startup (plan §6.1).
        Assert.Contains("AppSettings.IndexUpdateModeAutomaticFullRebuildWhenDirty", StartupChecksSource);
        Assert.Contains("maintenanceMode = IndexMaintenanceOperation.ModeBuildDue;", StartupChecksSource);
        Assert.Contains("settings, roots, maintenanceMode, rebuildWhenDirty", StartupChecksSource);
    }

    [Fact]
    public void Startup_AutoBuildRespectsBatteryAndForegroundSearchPause()
    {
        // Background builds pause on battery / during a foreground search / when the index drive is low (plan §6.1).
        Assert.Contains("ContentIndexBuildScheduler.ShouldPauseAutoBuild(", StartupChecksSource);
        Assert.Contains("Yagu.Helpers.PowerLineStatus.IsOnBattery()", StartupChecksSource);
        Assert.Contains("ViewModel.IsSearching", StartupChecksSource);
        Assert.Contains("new DriveInfo(indexDriveRoot).AvailableFreeSpace", StartupChecksSource);
    }

    // ── HELP.md documents the CLI content-index flags (parity) ──

    [Fact]
    public void HelpMarkdown_DocumentsUseIndexFlags()
    {
        Assert.Contains("--use-index", HelpMarkdown);
        Assert.Contains("--no-index", HelpMarkdown);
    }

    // ── Main-window availability status indicator (real, presence-only data) ──

    [Fact]
    public void MainViewModel_DeclaresIndexStatusObservableProperties()
    {
        Assert.Contains("public partial string IndexStatusText { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string IndexStatusGlyph { get; set; }", MainViewModelSource);
        Assert.Contains("public partial string IndexStatusTooltip { get; set; }", MainViewModelSource);
        Assert.Contains("public partial bool ShowIndexStatus { get; set; }", MainViewModelSource);
        Assert.Contains("public Microsoft.UI.Xaml.Visibility IndexStatusVisibility =>", MainViewModelSource);
        Assert.Contains("partial void OnShowIndexStatusChanged(bool value) => OnPropertyChanged(nameof(IndexStatusVisibility));", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_RefreshesIndexStatusFromManagerOffUiThread()
    {
        Assert.Contains("private async Task RefreshIndexStatusAsync(IReadOnlyList<string> roots, bool useThisSearch)", MainViewModelSource);
        // Gated on the master feature + status setting, computed off the UI thread, and never throws into UI.
        Assert.Contains("!_settings.EnableContentIndex || !_settings.ShowIndexStatusInMainWindow", MainViewModelSource);
        Assert.Contains("manager.HasCurrentIndex(root)", MainViewModelSource);
        Assert.Contains("ContentIndexUiStatus.Availability(", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_TriggersIndexStatusRefreshOnSearch()
        => Assert.Contains("RefreshIndexStatusAsync(targetRoots, UseContentIndex && _settings.EnableContentIndex)", MainViewModelSource);

    [Fact]
    public void MainWindowXaml_HasIndexStatusIndicatorBoundToViewModel()
    {
        Assert.Contains("x:Name=\"IndexStatusIndicator\"", MainWindowXaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IndexStatusVisibility, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Glyph=\"{x:Bind ViewModel.IndexStatusGlyph, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexStatusText, Mode=OneWay}\"", MainWindowXaml);
        // The descriptive text is bound inside the indicator's custom tooltip (see the percent-complete pin).
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexStatusTooltip, Mode=OneWay}\"", MainWindowXaml);
    }

    // ── Index onboarding: clickable status indicator + first-run prompt + add-folder dialog ──

    [Fact]
    public void MainWindowXaml_IndexStatusIndicatorIsClickable()
    {
        Assert.Contains("Tapped=\"OnIndexStatusTapped\"", MainWindowXaml);
        Assert.Contains("PointerEntered=\"OnIndexStatusPointerEntered\"", MainWindowXaml);
        Assert.Contains("PointerExited=\"OnIndexStatusPointerExited\"", MainWindowXaml);
        Assert.Contains("x:Name=\"IndexStatusTextBlock\"", MainWindowXaml);
    }

    [Fact]
    public void MainWindowXaml_IndexStatusHasStableLeftAnchoredSlotBetweenResourcesAndSkippedCount()
    {
        Assert.True(MainWindowXaml.IndexOf("x:Name=\"RamUsageBlock\"", StringComparison.Ordinal)
            < MainWindowXaml.IndexOf("x:Name=\"IndexStatusIndicator\"", StringComparison.Ordinal));
        Assert.True(MainWindowXaml.IndexOf("x:Name=\"IndexStatusIndicator\"", StringComparison.Ordinal)
            < MainWindowXaml.IndexOf("x:Name=\"SkipCountBlock\"", StringComparison.Ordinal));

        string indicator = ExtractFrom(MainWindowXaml, "x:Name=\"IndexStatusIndicator\"", 4500);
        Assert.Contains("<Grid Width=\"230\" VerticalAlignment=\"Center\">", indicator);
        Assert.Contains("HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\">", indicator);
        Assert.Contains("<Grid Width=\"36\" Height=\"16\" VerticalAlignment=\"Center\">", indicator);
        Assert.Contains("x:Name=\"IndexHealthyCheckIcon\"", indicator);
        Assert.Contains("Glyph=\"&#xE930;\" Foreground=\"LimeGreen\"", indicator);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IndexHealthyCheckVisibility, Mode=OneWay}\"", indicator);
        Assert.Contains("Width=\"190\" TextTrimming=\"CharacterEllipsis\"", indicator);
        Assert.Contains("string.Equals(IndexStatusText, \"Indexes: all healthy\", StringComparison.Ordinal)", MainViewModelSource);
    }

    [Fact]
    public void IndexStatusHover_ExplainsFreshnessFailureAndOffersDirectRepair()
    {
        string hover = ExtractFrom(MainWindowXaml, "x:Name=\"IndexStatusIndicator\"", 12000);
        Assert.Contains("x:Name=\"IndexStatusHoverOverlay\"", hover);
        Assert.Contains("Background=\"{ThemeResource AcrylicBackgroundFillColorDefaultBrush}\"", hover);
        Assert.Contains("x:Name=\"IndexStatusRepairButton\"", hover);
        Assert.Contains("Click=\"OnIndexStatusRepairClick\"", hover);
        Assert.Contains("Click=\"OnIndexStatusOpenSettingsFromHoverClick\"", hover);
        Assert.Contains("Orientation=\"Horizontal\" Spacing=\"8\" HorizontalAlignment=\"Right\"", hover);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexStatusTooltip, Mode=OneWay}\"", hover);
        Assert.Contains("<Grid Width=\"230\" VerticalAlignment=\"Center\">", hover);
        Assert.Contains("HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\">", hover);
        Assert.Contains("Width=\"190\" TextTrimming=\"CharacterEllipsis\"", hover);

        Assert.DoesNotContain("<FlyoutBase.AttachedFlyout>", hover);
        Assert.DoesNotContain("IndexStatusHoverFlyout", IndexOnboardingSource);
        Assert.Contains("IndexStatusHoverOverlay.Visibility = Visibility.Visible;", IndexOnboardingSource);
        Assert.Contains("IndexStatusHoverOverlay.Visibility = Visibility.Collapsed;", IndexOnboardingSource);
        Assert.Contains("if (!ReferenceEquals(_indexStatusHoverHideTimer, timer))", IndexOnboardingSource);
        Assert.Contains("OnIndexStatusHoverPanelPointerEntered", IndexOnboardingSource);
        Assert.Contains("IndexStatusHoverHideDelayMs = 350", IndexOnboardingSource);
        Assert.Contains("HideIndexStatusHoverOverlay();", PreviewSectionsSource);
        Assert.Contains("ViewModel.TryGetCurrentIndexFreshnessRepairTarget(", IndexOnboardingSource);
        Assert.Contains("await ViewModel.RebuildCurrentIndexBlockingAsync(roots);", IndexOnboardingSource);
        Assert.Contains("OpenSettingsToIndexingTab();", IndexOnboardingSource);

        Assert.Contains("All-drive and indexed-folder health", hover);
        Assert.Contains("x:Name=\"IndexStatusAllDriveHealthRows\"", hover);
        Assert.Contains("ViewModel.AllDriveIndexStatusVisibility", hover);
        Assert.Contains("ViewModel.IndexStatusAccessibleHelpText", hover);
        Assert.Contains("private void RebuildIndexStatusHealthRows()", IndexOnboardingSource);
        Assert.Contains("IReadOnlyList<IndexRootHealthEntry> entries = ViewModel.AllDriveIndexHealth;", IndexOnboardingSource);
        Assert.Contains("entry.CanIncrementallyRefresh", IndexOnboardingSource);
        Assert.Contains("\"Increase limit & update\"", IndexOnboardingSource);
        Assert.Contains("ComputeRaisedJournalCatchupLimit(", IndexOnboardingSource);
        Assert.Contains("await ViewModel.RefreshCurrentIndexIncrementallyAsync(root, raisedLimit);", IndexOnboardingSource);
        Assert.Contains("entry.CanMaintain", IndexOnboardingSource);
        Assert.Contains("\"Maintain\"", IndexOnboardingSource);
        Assert.Contains("await ViewModel.MaintainExistingIndexAsync(root);", IndexOnboardingSource);
        Assert.Contains("entry.CanDeleteStoredIndex", IndexOnboardingSource);
        Assert.Contains("\"Delete index\"", IndexOnboardingSource);
        Assert.Contains("await ViewModel.DeleteStoredIndexAsync(root);", IndexOnboardingSource);
        Assert.Contains("ShowTitleBar = false", ExtractFrom(
            IndexOnboardingSource,
            "private async Task RunIndexStatusDeleteActionAsync(string root)",
            2200));
        Assert.Contains("entry.CanRepair", IndexOnboardingSource);
        Assert.Contains("await ViewModel.RebuildCurrentIndexBlockingAsync(new[] { root });", IndexOnboardingSource);
        Assert.Contains("public IReadOnlyList<IndexRootHealthEntry> AllDriveIndexHealth", MainViewModelSource);
        Assert.Contains("IncrementalRoot: freshness.RawStatus == UsnReadStatus.Incomplete ? indexRoot : null", MainViewModelSource);
        Assert.Contains("public async Task MaintainExistingIndexAsync(string folder)", MainViewModelSource);
        Assert.Contains("Its existing index will be checked by the next maintenance pass.", MainViewModelSource);
        Assert.Contains("public async Task DeleteStoredIndexAsync(string folder)", MainViewModelSource);
        Assert.Contains("manager.DeleteScope(ContentIndexManager.ScopeIdForRoot(root))", MainViewModelSource);
        Assert.Contains("RefreshAllDriveIndexStatus();", ExtractFrom(
            MainViewModelSource,
            "public async Task RefreshCurrentIndexIncrementallyAsync(",
            6000));
        Assert.Contains("nameof(ViewModel.AllDriveIndexStatusText)", MainWindowCodeBehindSource);
        Assert.Contains("UpdateIndexStatusHoverActions();", MainWindowCodeBehindSource);

        Assert.Contains("public bool TryGetCurrentIndexFreshnessRepairTarget(", MainViewModelSource);
        Assert.Contains("IsIndexFreshnessRepairReason(pair.Value)", MainViewModelSource);
        Assert.Contains("$\"Rebuild {repairRoots[0]} index\"", MainViewModelSource);
        Assert.Contains("IndexedRootsPolicy.FindBestCoveringRoot(builtRoots, searchedRoot)", MainViewModelSource);
        Assert.Contains("change-journal catch-up limit behind", MainViewModelSource);
        Assert.Contains("change-journal catch-up limit reached", MainViewModelSource);
        Assert.Contains("reason?.Contains(\"Incomplete\"", MainViewModelSource);
        Assert.Contains("Increase the catch-up limit and update the index, or rebuild it.", MainViewModelSource);
        Assert.Contains("=> !IsIndexCatchupLimitReason(reason)", MainViewModelSource);
        Assert.Contains("private static bool IsIndexCatchupLimitReason(string? reason)", MainViewModelSource);
        Assert.Contains("reason?.Contains(\"CheckpointAhead\"", MainViewModelSource);
        Assert.Contains("_currentIndexFreshnessByRoot", MainViewModelSource);
        Assert.Contains(".Concat(_allDriveIndexHealth", MainViewModelSource);
        Assert.Contains("int rebuildCount = freshnessFailures.Count", MainViewModelSource);
        Assert.Contains("_ => \"Index: freshness unavailable\"", MainViewModelSource);
        Assert.Contains(".Concat(_currentIndexFreshnessByRoot", MainViewModelSource);
        Assert.Contains("public void RefreshCurrentIndexStatus()", MainViewModelSource);
        Assert.Contains("ViewModel.RefreshAllDriveIndexStatus();", StartupChecksSource);
        Assert.DoesNotContain("ViewModel.RefreshCurrentIndexStatus();", StartupChecksSource);
        Assert.Contains("private void OnDirectoryQuerySubmitted", SearchInputSource);
        Assert.Contains("ViewModel.RefreshCurrentIndexStatus();", SearchInputSource);
        Assert.Contains("private string BuildIndexSchedulingDetails()", MainViewModelSource);
        Assert.Contains("=> Environment.NewLine + Environment.NewLine", MainViewModelSource);
        Assert.Contains("+ BuildIndexDateDetails()\r\n                + BuildIndexSchedulingDetails();", MainViewModelSource);
    }

    [Fact]
    public void IndexStatusHover_WhenAutomaticIndexingIsOff_OffersAndPersistsInlinePresets()
    {
        string hover = ExtractFrom(MainWindowXaml, "x:Name=\"IndexStatusHoverOverlay\"", 9000);
        Assert.Contains("x:Name=\"IndexStatusAutomaticIndexingPanel\"", hover);
        Assert.Contains("Visibility=\"Collapsed\"", hover);
        Assert.Contains("x:Name=\"IndexStatusAutomaticIndexingComboBox\"", hover);
        Assert.Contains("SelectionChanged=\"OnIndexStatusAutomaticIndexingSelectionChanged\"", hover);
        Assert.Contains("Tag=\"Continuous\"", hover);
        Assert.Contains("Tag=\"WhenIdle\"", hover);
        Assert.Contains("Tag=\"AtStartup\"", hover);
        Assert.Contains("Tag=\"OnSchedule\"", hover);
        Assert.Contains("The choice is saved immediately.", hover);

        string actions = ExtractFrom(IndexOnboardingSource, "private void UpdateIndexStatusHoverActions()", 1900);
        Assert.Contains("ViewModel.Settings.EnableContentIndex", actions);
        Assert.Contains("AppSettings.NormalizeIndexBuildTrigger(ViewModel.Settings.IndexBuildTrigger)", actions);
        Assert.Contains("AppSettings.DefaultIndexBuildTrigger", actions);
        Assert.Contains("IndexStatusAutomaticIndexingPanel.Visibility =", actions);

        string selection = ExtractFrom(
            IndexOnboardingSource,
            "private async void OnIndexStatusAutomaticIndexingSelectionChanged(",
            1200);
        Assert.Contains("await ViewModel.SetAutomaticIndexingPresetAsync(trigger);", selection);
        Assert.Contains("comboBox.SelectedIndex = -1;", selection);
        Assert.Contains("UpdateIndexStatusHoverActions();", selection);

        string persist = ExtractFrom(
            MainViewModelSource,
            "public async Task SetAutomaticIndexingPresetAsync(string trigger)",
            3000);
        Assert.Contains("_settings.IndexBuildTrigger = normalizedTrigger;", persist);
        Assert.Contains("AppSettings.IndexUpdateModeAutomaticIncremental", persist);
        Assert.Contains("await PersistSettingsAsync()", persist);
        Assert.Contains("RefreshCurrentIndexStatus();", persist);
        Assert.Contains("RefreshAllDriveIndexStatus();", persist);
        Assert.Contains("RequestIdleIndexMaintenanceAsync", persist);
        Assert.Contains("When automatic indexing is off, its status hover panel offers", HelpMarkdown);
    }

    [Fact]
    public void IndexStatusHover_StaysOpenWhileAutomaticIndexingDropDownIsOpen()
    {
        string hover = ExtractFrom(MainWindowXaml, "x:Name=\"IndexStatusHoverOverlay\"", 9000);
        Assert.Contains("DropDownOpened=\"OnIndexStatusAutomaticIndexingDropDownOpened\"", hover);
        Assert.Contains("DropDownClosed=\"OnIndexStatusAutomaticIndexingDropDownClosed\"", hover);

        string opened = ExtractFrom(
            IndexOnboardingSource,
            "private void OnIndexStatusAutomaticIndexingDropDownOpened(",
            500);
        Assert.Contains("CancelIndexStatusHoverHide();", opened);

        string closed = ExtractFrom(
            IndexOnboardingSource,
            "private void OnIndexStatusAutomaticIndexingDropDownClosed(",
            700);
        Assert.Contains("ScheduleIndexStatusHoverHide();", closed);

        string hideTimer = ExtractFrom(
            IndexOnboardingSource,
            "private void ScheduleIndexStatusHoverHide()",
            1200);
        Assert.Contains("IndexStatusAutomaticIndexingComboBox?.IsDropDownOpen != true", hideTimer);
    }

    [Fact]
    public void MainViewModel_LaunchStatusChecksAllLocalDrivesAndMaintainedRoots()
    {
        Assert.Contains("public void RefreshAllDriveIndexStatus()", MainViewModelSource);
        Assert.Contains("DriveEnumerator.GetSearchRoots(", MainViewModelSource);
        Assert.Contains("includeNetwork: false", MainViewModelSource);
        Assert.Contains("includeRemovable: false", MainViewModelSource);
        Assert.Contains("includeCloud: false", MainViewModelSource);
        Assert.Contains(".Concat(registeredRoots)", MainViewModelSource);
        Assert.Contains(".Distinct(StringComparer.OrdinalIgnoreCase)", MainViewModelSource);
        Assert.Contains("ReadAllDriveIndexHealth(", MainViewModelSource);
        Assert.Contains("ContentIndexManager.ScopeFreshnessState.Dirty", MainViewModelSource);
        Assert.Contains("IndexRootHealthKind.ChangesPending", MainViewModelSource);
        Assert.Contains("recent filesystem changes pending indexing", MainViewModelSource);
        Assert.Contains("affected files scan live until the next update", MainViewModelSource);
        Assert.Contains("IndexRootHealthKind.RebuildRequired", MainViewModelSource);
        Assert.Contains("IndexRootHealthKind.FreshnessUnavailable", MainViewModelSource);
        Assert.Contains("IndexRootHealthKind.BuildRequired", MainViewModelSource);
        Assert.Contains("if (!registered)", MainViewModelSource);
        Assert.Contains("ContentIndexUiStatus.UnregisteredRootHealth(root, leftover.Exists)", MainViewModelSource);
        Assert.Contains("ApplyAllDriveIndexHealthStatus(force: true);", MainViewModelSource);
        Assert.Contains("ApplyAllDriveIndexHealthStatus();", MainViewModelSource);
        Assert.Contains("activeSearchCoverage = IndexSearchCoverage.Full;", MainViewModelSource);
        Assert.Contains("activeSearchCoverage = IndexSearchCoverage.Partial;", MainViewModelSource);
        Assert.Contains("ApplyAllDriveIndexHealthStatus(activeSearchCoverage: activeSearchCoverage);", MainViewModelSource);
        Assert.Contains("ContentIndexUiStatus.AllDriveHealthLabel(_allDriveIndexHealth, activeSearchCoverage)", MainViewModelSource);
        Assert.Contains("&& _indexRuntimeAcceleratedRootPaths.Count > 0", MainViewModelSource);
        Assert.Contains("? IndexSearchCoverage.Full\r\n                : IndexSearchCoverage.Partial;", MainViewModelSource);
    }

    [Fact]
    public void Startup_WatcherHintForcesIncrementalRefreshForNewFileIdentities()
    {
        string watcher = ExtractFrom(StartupChecksSource, "private void StartIndexWatcherHintsIfEnabled(", 4200);
        Assert.Contains("operation.ForceRefresh = incremental;", watcher);
        Assert.Contains("newly created file", watcher);
        Assert.Contains("IndexBuildCoordinator", watcher);
    }

    [Fact]
    public void MainViewModel_ExposesAddableFolderStateForClickableIndicator()
    {
        Assert.Contains("public IReadOnlyList<string> IndexStatusFoldersWithoutIndex", MainViewModelSource);
        Assert.Contains("public bool IndexStatusCanAddFolder => IndexStatusFoldersWithoutIndex.Count > 0;", MainViewModelSource);
        Assert.Contains("public IReadOnlyList<string> IndexStatusRegisteredFoldersWithoutIndex", MainViewModelSource);
        Assert.Contains("public bool IndexStatusCanBuildRegisteredFolder => IndexStatusRegisteredFoldersWithoutIndex.Count > 0;", MainViewModelSource);
        Assert.Contains("IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, root) is not null", MainViewModelSource);
        Assert.Contains("IndexStatusFoldersWithoutIndex = unregisteredMissing;", MainViewModelSource);
        Assert.Contains("IndexStatusRegisteredFoldersWithoutIndex = registeredMissing;", MainViewModelSource);
        Assert.Contains("Index: not built for this folder", MainViewModelSource);
        Assert.Contains("choose Build now", MainViewModelSource);
        Assert.Contains("reason.Contains(\"no trusted index\"", MainViewModelSource);
        Assert.Contains("IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, normalizedRoot) is not null", MainViewModelSource);
        Assert.Contains("IndexStatusRegisteredFoldersWithoutIndex = IndexStatusRegisteredFoldersWithoutIndex", MainViewModelSource);
        // The tooltip nudges discoverability of the click affordance when a folder can be added.
        Assert.Contains("Click to add a folder to the index.", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_AddFolderToIndexAndBuild_EnablesFeatureRegistersRootAndBuilds()
    {
        Assert.Contains("public async Task AddFolderToIndexAndBuildAsync(string folder)", MainViewModelSource);
        Assert.Contains("public async Task AddFolderToIndexAndBuildBlockingAsync(string folder)", MainViewModelSource);
        Assert.Contains("await RunCurrentIndexBlockingAsync(new[] { effectiveRoot }, rebuild: false)", MainViewModelSource);
        Assert.Contains("_settings.EnableContentIndex = true;", MainViewModelSource);
        Assert.Contains("_settings.IndexedRoots = IndexedRootsPolicy.Add(_settings.IndexedRoots, root);", MainViewModelSource);
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(_settings, root, rebuild)", MainViewModelSource);
        Assert.Contains("coordinator.BuildFullScopePreferWorkerAsync(", MainViewModelSource);
    }

    [Fact]
    public void OverlappingIndexRoots_UseOneBroaderIndex_AndRedundantChildrenAreDeleteOnly()
    {
        Assert.Contains("ResolveBestAvailableIndexRoot(root, _settings.IndexedRoots)", MainViewModelSource);
        Assert.Contains("ResolveBestAvailableIndexRoot(root, settings.IndexedRoots)", MainViewModelSource);
        Assert.Contains("IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, Directory!)", MainViewModelSource);
        Assert.Contains("existingCover is not null", MainViewModelSource);
        Assert.Contains("StartBackgroundIndexBuild(effectiveRoot);", MainViewModelSource);

        Assert.Contains("FindRegisteredCoveringAncestor", SettingsIndexingActionsSource);
        Assert.Contains("redundant \\u2014 covered by", SettingsIndexingActionsSource);
        Assert.Contains("delete the redundant child index instead of rebuilding it", SettingsIndexingActionsSource);
        Assert.Contains("no duplicate index root was added", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexOnboarding_StatusClickAddsFolderOrOpensSettings()
    {
        Assert.Contains("private async void OnIndexStatusTapped(", IndexOnboardingSource);
        Assert.Contains("if (ViewModel.IndexStatusCanAddFolder)", IndexOnboardingSource);
        Assert.Contains("await ShowAddFolderToIndexDialogAsync(folder);", IndexOnboardingSource);
        Assert.Contains("_settingsWindow?.SelectTabByHeader(\"Indexing\");", IndexOnboardingSource);
    }

    [Fact]
    public void IndexOnboarding_AddFolderDialog_OffersSubpartChoicesAndWarnsForLargeRoot()
    {
        Assert.Contains("IndexOnboardingPlan.PathChoices(folder)", IndexOnboardingSource);
        Assert.Contains("private async Task<bool> ConfirmLargeFolderIfNeededAsync(string folder)", IndexOnboardingSource);
        Assert.Contains("IndexOnboardingPlan.IsLikelyLargeRoot(folder)", IndexOnboardingSource);
        Assert.Contains("BoundedFileCount(folder, IndexOnboardingPlan.LargeFolderFileThreshold)", IndexOnboardingSource);
        Assert.Contains("await ViewModel.AddFoldersToIndexAndBuildAsync(chosen, buildTrigger);", IndexOnboardingSource);
    }

    [Fact]
    public void IndexOnboarding_AddFolderDialog_AllowsMultipleFoldersAndBuildTrigger()
    {
        // The onboarding dialog lets the user add more than one folder (checkboxes, not a single-select
        // radio group) and choose which automatic build trigger(s) keep the indexes up to date.
        Assert.Contains("var folderChecks = new List<CheckBox>(choices.Count);", IndexOnboardingSource);
        Assert.DoesNotContain("var radios = new List<RadioButton>(choices.Count);", IndexOnboardingSource);
        Assert.Contains("var triggerChecks = new List<(CheckBox Check, string Flag)>", IndexOnboardingSource);
        Assert.Contains("AppSettings.IndexBuildTriggerHas(ViewModel.Settings.IndexBuildTrigger, flag)", IndexOnboardingSource);
        Assert.Contains("string buildTrigger = AppSettings.NormalizeIndexBuildTrigger(string.Join(\",\", selectedTriggers));", IndexOnboardingSource);
        // The multi-folder VM entry point registers every chosen root, sets the trigger, and builds each.
        Assert.Contains("public async Task AddFoldersToIndexAndBuildAsync(IReadOnlyList<string> folders, string? buildTrigger)", MainViewModelSource);
    }

    [Fact]
    public void IndexOnboarding_DoesNotOfferToAddAnAlreadyCoveredFolder()
    {
        // A path choice covered by an equal or broader index root is disabled so it cannot create overlap.
        Assert.Contains("bool IsAlreadyCovered(string candidate) => IndexedRootsPolicy.FindBestCoveringRoot(indexedRoots, candidate) is not null;", IndexOnboardingSource);
        Assert.Contains("IsEnabled = !already", IndexOnboardingSource);
        Assert.Contains("already covered by the index", IndexOnboardingSource);

        // If the folder AND every parent choice are already indexed, an explanatory dialog is shown instead
        // of the add-choice dialog, and the add path never proceeds for an already-indexed/unselected choice.
        Assert.Contains("if (choices.All(IsAlreadyCovered))", IndexOnboardingSource);
        Assert.Contains("await ShowFolderAlreadyCoveredDialogAsync(folder);", IndexOnboardingSource);
        Assert.Contains("c.IsChecked == true && c.Tag is string s && !IsAlreadyCovered(s)", IndexOnboardingSource);

        // The explanatory dialog names the covering root without claiming its generation is already built.
        Assert.Contains("private async Task ShowFolderAlreadyCoveredDialogAsync(string folder)", IndexOnboardingSource);
        Assert.Contains("Already covered by the content index", IndexOnboardingSource);
        Assert.Contains("is already covered by the registered index root", IndexOnboardingSource);
        Assert.Contains("no duplicate child index is needed", IndexOnboardingSource);
        Assert.Contains("choose Build now", IndexOnboardingSource);
        Assert.DoesNotContain("is already in your content index", IndexOnboardingSource);
        Assert.Contains("ViewModel.IndexStatusCanBuildRegisteredFolder", IndexOnboardingSource);
        Assert.Contains("OpenSettingsToIndexingTab();", IndexOnboardingSource);
    }

    [Fact]
    public void IndexOnboarding_FirstRunPrompt_IsOnceAndFlowsIntoAddDialog()
    {
        Assert.Contains("private async Task CheckFirstRunIndexOnboardingAsync()", IndexOnboardingSource);
        Assert.Contains("if (ViewModel.Settings.HasPromptedIndexOnboarding)", IndexOnboardingSource);
        Assert.Contains("ViewModel.Settings.IndexedRoots.Count > 0", IndexOnboardingSource);
        Assert.Contains("DefaultContentIndexPathProvider.TryGetPreservedStorageDirectory", IndexOnboardingSource);
        Assert.Contains("DefaultContentIndexPathProvider.ClearPreservedStorageDirectory();", IndexOnboardingSource);
        Assert.Contains(".GetReusableStoredIndexRoots()", IndexOnboardingSource);
        Assert.Contains("Title = \"Existing content indexes found\"", IndexOnboardingSource);
        Assert.Contains("PrimaryButtonText = \"Use existing indexes\"", IndexOnboardingSource);
        Assert.Contains("IndexedRootsPolicy.Add(ViewModel.Settings.IndexedRoots, root)", IndexOnboardingSource);
        Assert.Contains("ViewModel.Settings.EnableContentIndex = true;", IndexOnboardingSource);
        Assert.Contains("ViewModel.Settings.UseContentIndexByDefault = true;", IndexOnboardingSource);
        Assert.DoesNotContain(".HasReadableStoredIndex()", IndexOnboardingSource);
        Assert.Contains("ViewModel.Settings.HasPromptedIndexOnboarding = true;", IndexOnboardingSource);
        Assert.Contains("Win32FileDialog.SelectFolder(_hwnd, \"Select a folder to index\")", IndexOnboardingSource);
        Assert.Contains("await ShowAddFolderToIndexDialogAsync(folder);", IndexOnboardingSource);

        int registeredUseGate = IndexOnboardingSource.IndexOf("ViewModel.Settings.IndexedRoots.Count > 0", StringComparison.Ordinal);
        int preservedPrompt = IndexOnboardingSource.IndexOf("Title = \"Existing content indexes found\"", StringComparison.Ordinal);
        int freshPrompt = IndexOnboardingSource.IndexOf("Title = \"Speed up searches with an index?\"", StringComparison.Ordinal);
        Assert.True(registeredUseGate >= 0 && preservedPrompt > registeredUseGate && freshPrompt > preservedPrompt,
            "Registered roots may suppress onboarding, but unregistered stored indexes must be offered for adoption before fresh setup.");
    }

    [Fact]
    public void Startup_RunsFirstRunIndexOnboardingInAwaitedChain()
        => Assert.Contains("await CheckFirstRunIndexOnboardingAsync();", StartupChecksSource);

    [Fact]
    public void IndexOnboarding_ParksInputSuggestionsAroundPickerAndDialogs()
    {
        // The native folder picker + inter-dialog focus gaps are not covered by the YaguDialog-scoped
        // suppression, so a windowed query/directory suggestion popup could otherwise float above them.
        Assert.Contains("private IDisposable ParkInputSuggestionsForModal()", IndexOnboardingSource);
        Assert.Contains("_suppressQuerySuggestionsUntilTick = long.MaxValue;", IndexOnboardingSource);
        Assert.Contains("using var suppression = ParkInputSuggestionsForModal();", IndexOnboardingSource);
    }

    [Fact]
    public void Settings_DeclaresIndexOnboardingPromptFlag()
        => Assert.Contains("public bool HasPromptedIndexOnboarding { get; set; }", SettingsServiceSource);

    // ── Indexing-in-progress indicator: "Indexing…" in the status bar while a background build runs ──

    [Fact]
    public void MainViewModel_ExposesIndexBuildActivityApi()
    {
        Assert.Contains("public bool IsIndexBuildActive => _activeIndexBuilds > 0;", MainViewModelSource);
        Assert.Contains(
            "public void BeginIndexBuildActivity(string? folder = null, bool isIncremental = false)",
            MainViewModelSource);
        Assert.Contains("public void EndIndexBuildActivity()", MainViewModelSource);
        // Marshals to the UI thread when called from a background build thread.
        Assert.Contains("if (!_dispatcher.HasThreadAccess)", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_ShowsIndexingStateAndGuardsAgainstOverwrite()
    {
        Assert.Contains("string activity = _activeIndexBuildIsIncremental ? \"Updating index\" : \"Indexing\";", MainViewModelSource);
        Assert.Contains("? \"Finalizing index update\\u2026\"", MainViewModelSource);
        Assert.Contains(": _indexBuildPercent >= 0 ? $\"{activity}\\u2026 {_indexBuildPercent}%\" : $\"{activity}\\u2026\";", MainViewModelSource);
        Assert.Contains("private void ShowIndexBuildingStatus()", MainViewModelSource);
        // The availability + coverage updaters must not clobber the "Indexing…" indicator mid-build.
        Assert.Contains("if (_activeIndexBuilds > 0)", MainViewModelSource);
    }

    [Fact]
    public void MainWindow_IndexingTooltip_ShowsHighlyVisiblePercentComplete()
    {
        // The build reports crawled bytes; the VM converts them to a percent estimate (crawled bytes vs.
        // the used space of the drive) and surfaces it both inline in the status text and, big, in a custom
        // tooltip (with a progress bar) modelled on the skip-breakdown overlay.
        Assert.Contains("public void ReportIndexBuildProgress(int percent)", MainViewModelSource);
        Assert.Contains(": _indexBuildPercent >= 0 ? $\"{activity}\\u2026 {_indexBuildPercent}%\" : $\"{activity}\\u2026\";", MainViewModelSource);
        // The VM drives the custom-tooltip percent surface (big number, progress bar value, visibility gate).
        Assert.Contains("IndexBuildPercentText = $\"{_indexBuildPercent}%\";", MainViewModelSource);
        Assert.Contains("IndexBuildPercentValue = _indexBuildPercent;", MainViewModelSource);
        Assert.Contains("ShowIndexBuildPercent = true;", MainViewModelSource);
        Assert.Contains("public Microsoft.UI.Xaml.Visibility IndexBuildPercentVisibility =>", MainViewModelSource);

        // Custom rich tooltip on the indicator: a large accent percent + progress bar, gated on the build
        // percent being known, with the descriptive status text below (replaces the plain string tooltip).
        Assert.Contains("<ToolTipService.ToolTip>", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexBuildPercentText, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Value=\"{x:Bind ViewModel.IndexBuildPercentValue, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IndexBuildPercentVisibility, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexStatusTooltip, Mode=OneWay}\"", MainWindowXaml);
        string indexHover = ExtractFrom(MainWindowXaml, "x:Name=\"IndexStatusHoverOverlay\"", 2500);
        Assert.Contains("IsIndeterminate=\"True\"", indexHover);

        // Both user-initiated build paths capture the drive denominator once and forward per-report progress.
        Assert.Contains("long driveUsedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root);", MainViewModelSource);
        Assert.Contains("progress: p => ReportIndexBuildProgress(IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes))", MainViewModelSource);
        Assert.Contains("progress: p => _viewModel.ReportIndexBuildProgress(IndexBuildProgressEstimate.Percent(p.BytesCrawled, driveUsedBytes))", SettingsIndexingActionsSource);

        // The multi-root auto/scheduled/startup/resume pass also reports each root's folder + percent into the
        // indicator (folder so the tooltip names the drive; percent for full builds AND incremental refreshes).
        Assert.Contains("void ReportRootProgress(string root, int percent, string stage) => ViewModel.ReportIndexBuildProgress(root, percent, stage);", StartupChecksSource);
        Assert.Contains("excludedStorageRoot: provider.IndexRoot", StartupChecksSource);
        Assert.Contains("ReportRootProgress).ConfigureAwait(true);", StartupChecksSource);
        Assert.Contains("(progressRoot, percent, stage) => ReportIndexBuildProgress(progressRoot, percent, stage)", MainViewModelSource);
        Assert.Contains("public void ReportIndexBuildProgress(string? folder, int percent)", MainViewModelSource);
        Assert.Contains("public void ReportIndexBuildProgress(string? folder, int percent, string? stage)", MainViewModelSource);
        Assert.Contains("Updating the existing content index for {_activeIndexBuildFolder} incrementally", MainViewModelSource);
    }

    [Fact]
    public void MainWindow_IndexActivityUsesNativeProgressRing()
    {
        // A native ProgressRing replaces the rotating 12px FontIcon during activity. The fixed icon slot
        // avoids horizontal layout shifts, while the native animated visual avoids rasterized-glyph pixel
        // snapping that looked like stutter even when rotation was compositor-driven.
        Assert.Contains("x:Name=\"IndexStatusIcon\"", MainWindowXaml);
        Assert.Contains("x:Name=\"IndexStatusProgressRing\"", MainWindowXaml);
        Assert.Contains("<Grid Width=\"16\" Height=\"16\"", MainWindowXaml);

        Assert.Contains("private void UpdateIndexBuildSpinAnimation()", IndexOnboardingSource);
        Assert.Contains("(ViewModel.IsIndexBuildActive || ViewModel.IsIndexWarmActive)", IndexOnboardingSource);
        Assert.Contains("&& !ViewModel.IsIndexWarmPausedForSearch", IndexOnboardingSource);
        Assert.Contains("StartIndexBuildSpin();", IndexOnboardingSource);
        Assert.Contains("StopIndexBuildSpin();", IndexOnboardingSource);
        Assert.Contains("if (_indexBuildSpinRunning)", IndexOnboardingSource);
        Assert.Contains("IndexStatusIcon.Visibility = Visibility.Collapsed;", IndexOnboardingSource);
        Assert.Contains("IndexStatusProgressRing.Visibility = Visibility.Visible;", IndexOnboardingSource);
        Assert.Contains("IndexStatusProgressRing.IsActive = true;", IndexOnboardingSource);
        Assert.Contains("IndexStatusProgressRing.IsActive = false;", IndexOnboardingSource);
        Assert.DoesNotContain("RotationAngleInDegrees", IndexOnboardingSource);

        // Wired to fire whenever the build/warm active or paused state changes.
        string subscription = ExtractFrom(MainWindowCodeBehindSource, "nameof(ViewModel.IsIndexBuildActive)", 600);
        Assert.Contains("UpdateIndexBuildSpinAnimation();", subscription);
        Assert.Contains("nameof(ViewModel.IsIndexWarmActive)", MainWindowCodeBehindSource);
    }

    [Fact]
    public void MainWindow_IdleIndicatorTooltip_ExplainsWhenIndexingRuns()
    {
        // When a build is NOT running, the availability / coverage / ready tooltips append the scheduling
        // hint so the user understands why indexing isn't currently happening.
        Assert.Contains("tooltip += BuildIndexSchedulingDetails();", MainViewModelSource);
        Assert.Contains("+ BuildIndexDateDetails()\r\n            + BuildIndexSchedulingDetails();", MainViewModelSource);
        Assert.Contains("=> Environment.NewLine + Environment.NewLine", MainViewModelSource);

        // The pure hint helper lives in ContentIndexUiStatus (unit-tested in ContentIndexUiStatusTests).
        string uiStatus = Read("src", "Yagu", "Services", "Index", "ContentIndexUiStatus.cs");
        Assert.Contains("public static string SchedulingHint(string? buildTrigger)", uiStatus);
    }

    private static string ExtractFrom(string source, string anchor, int windowSize)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{anchor}' in source.");
        int end = Math.Min(start + windowSize, source.Length);
        return source[start..end];
    }

    [Fact]
    public void MainViewModel_OnboardingBuild_BracketsWithBuildActivity()
    {
        Assert.Contains("BeginIndexBuildActivity(root);", MainViewModelSource);
        Assert.Contains("EndIndexBuildActivity();", MainViewModelSource);
    }

    [Fact]
    public void Startup_AutoBuild_ShowsIndexingActivity()
    {
        Assert.Contains("ViewModel.BeginIndexBuildActivity();", StartupChecksSource);
        Assert.Contains("ViewModel.EndIndexBuildActivity();", StartupChecksSource);
    }

    [Fact]
    public void SettingsBuildNow_ShowsIndexingActivityInMainWindow()
    {
        Assert.Contains("_viewModel.BeginIndexBuildActivity(root);", SettingsIndexingActionsSource);
        Assert.Contains("_viewModel.EndIndexBuildActivity();", SettingsIndexingActionsSource);
    }

    [Fact]
    public void IndexingTab_OffersOnScheduleTriggerWithScheduleControls()
    {
        // The Build trigger is a set of checkboxes so several automatic triggers can be active at once
        // (e.g. At startup AND On a schedule); none checked = Manual.
        Assert.Contains("(\"OnSchedule\", \"On a schedule\")", SettingsIndexingSource);
        Assert.Contains("(\"AtStartup\", \"At startup\")", SettingsIndexingSource);
        Assert.Contains("(\"Continuous\", \"Continuously while Yagu is open\")", SettingsIndexingSource);
        Assert.Contains("new (string Flag, string Display)[]", SettingsIndexingSource);
        Assert.Contains("var triggerChecks = new CheckBox[triggerFlags.Length];", SettingsIndexingSource);
        Assert.Contains("void ApplyTriggerSelection()", SettingsIndexingSource);
        Assert.Contains("AppSettings.NormalizeIndexBuildTrigger(string.Join(\",\", selected))", SettingsIndexingSource);
        Assert.Contains("(\"Interval\", \"Every N minutes\")", SettingsIndexingSource);
        Assert.Contains("(\"Weekly\", \"On chosen days at a set time\")", SettingsIndexingSource);

        // Interval + weekly (days-of-week checkboxes + a 24h TimePicker) controls bind the schedule settings.
        Assert.Contains("s.IndexScheduleMode", SettingsIndexingSource);
        Assert.Contains("s.IndexScheduleIntervalMinutes", SettingsIndexingSource);
        Assert.Contains("s.IndexScheduleDaysOfWeekMask", SettingsIndexingSource);
        Assert.Contains("s.IndexScheduleTimeOfDay", SettingsIndexingSource);
        Assert.Contains("new TimePicker", SettingsIndexingSource);
        Assert.Contains("ClockIdentifier = \"24HourClock\"", SettingsIndexingSource);

        // The schedule sub-panel is shown when the OnSchedule trigger is checked; interval vs weekly toggles on mode.
        Assert.Contains("void UpdateScheduleVisibility()", SettingsIndexingSource);
        Assert.Contains("AppSettings.IndexBuildTriggerHas(_viewModel.Settings.IndexBuildTrigger, \"OnSchedule\")", SettingsIndexingSource);
        Assert.Contains("Idle delay / continuous interval (minutes):", SettingsIndexingSource);
        Assert.Contains("pair it with Automatic incremental", SettingsIndexingSource);
    }

    [Fact]
    public void Startup_ScheduleTimer_DrivesTheOnScheduleTrigger()
    {
        // A DispatcherTimer ticks while Yagu runs and fires a build pass when the user's schedule is due.
        Assert.Contains("private void StartIndexScheduleTimer()", StartupChecksSource);
        Assert.Contains("_indexScheduleTimer = new DispatcherTimer", StartupChecksSource);
        Assert.Contains("private async Task RunScheduledIndexBuildIfDueAsync()", StartupChecksSource);
        Assert.Contains("ContentIndexBuildScheduler.RootsForScheduledBuild(settings)", StartupChecksSource);
        Assert.Contains("ContentIndexScheduleEvaluator.IsDue(settings, _lastScheduledIndexRun, now)", StartupChecksSource);

        // Startup and scheduled passes share one build-pass method.
        Assert.Contains("private async Task RunIndexBuildPassAsync(IReadOnlyList<string> roots)", StartupChecksSource);
        Assert.Contains("await RunIndexBuildPassAsync(roots);", StartupChecksSource);
    }

    [Fact]
    public void Startup_IdleTimer_DrivesWhenIdleTriggerUsingLastInputAndConfiguredDelay()
    {
        Assert.Contains("private async Task RunIdleIndexBuildIfDueAsync()", StartupChecksSource);
        Assert.Contains("SystemIdleDetector.TryGetIdleTime()", StartupChecksSource);
        Assert.Contains("NormalizeIndexIdleDelayMinutes(settings.IndexIdleDelayMinutes)", StartupChecksSource);
        Assert.Contains("SystemIdleDetector.HasBeenIdleFor(idleTime, requiredIdle)", StartupChecksSource);
        Assert.Contains("ContentIndexBuildScheduler.RootsForIdleBuild(settings)", StartupChecksSource);
        Assert.Contains("nowUtc - _lastIdleIndexRunUtc < requiredIdle", StartupChecksSource);
        Assert.Contains("_ = RunIdleIndexBuildIfDueAsync();", StartupChecksSource);
        Assert.Contains("await RunIndexBuildPassAsync(roots);", StartupChecksSource);
    }

    [Fact]
    public void Startup_ContinuousTrigger_UsesIdleMaintenanceWithoutRequiringActualIdle()
    {
        Assert.Contains("public const string TriggerContinuous", Read("src", "Yagu", "Services", "Index", "ContentIndexAutoBuilder.cs"));
        Assert.Contains("bool continuousMaintenance = AppSettings.IndexBuildTriggerHas(", StartupChecksSource);
        Assert.Contains("ContentIndexBuildScheduler.TriggerContinuous", StartupChecksSource);
        Assert.Contains("bool bypassIdleGate = continuousMaintenance || developerSimulatedIdle;", StartupChecksSource);
        Assert.Contains("if (!bypassIdleGate && !Yagu.Helpers.SystemIdleDetector.HasBeenIdleFor(idleTime, requiredIdle))", StartupChecksSource);
        Assert.Contains("nowUtc - _lastIdleIndexRunUtc < requiredIdle", StartupChecksSource);
        Assert.Contains("Continuous index maintenance due", StartupChecksSource);
        Assert.Contains("_ = RunIdleIndexBuildIfDueAsync();", StartupChecksSource);
        // The shared build pass retains all unattended-work safety gates.
        Assert.Contains("ContentIndexBuildScheduler.ShouldPauseAutoBuild(", StartupChecksSource);
        Assert.Contains("ViewModel.IsIndexingPaused", StartupChecksSource);
    }

    [Fact]
    public void DeveloperOptions_CanSimulateIdleThroughTheRealSessionOnlyScheduler()
    {
        Assert.Contains("[ObservableProperty] public partial bool SimulateSystemIdle { get; set; }", MainViewModelSource);
        Assert.Contains("public Func<Task>? RequestIdleIndexMaintenanceAsync { get; set; }", MainViewModelSource);
        Assert.DoesNotContain("SimulateSystemIdle", SettingsServiceSource);

        Assert.Contains("ViewModel.RequestIdleIndexMaintenanceAsync = RunIdleIndexBuildIfDueAsync;", StartupChecksSource);
        Assert.Contains("bool developerSimulatedIdle = ViewModel.SimulateSystemIdle;", StartupChecksSource);
        Assert.Contains("bool bypassIdleGate = continuousMaintenance || developerSimulatedIdle;", StartupChecksSource);
        Assert.Contains("if (!bypassIdleGate && !Yagu.Helpers.SystemIdleDetector.HasBeenIdleFor(idleTime, requiredIdle))", StartupChecksSource);
        Assert.Contains("ContentIndexBuildScheduler.RootsForIdleBuild(settings)", StartupChecksSource);
        Assert.Contains("await RunIndexBuildPassAsync(roots);", StartupChecksSource);

        Assert.Contains("Content = _viewModel.SimulateSystemIdle", SettingsWindowSource);
        Assert.Contains("_viewModel.SimulateSystemIdle = !_viewModel.SimulateSystemIdle;", SettingsWindowSource);
        Assert.Contains("_viewModel.RequestIdleIndexMaintenanceAsync is { } requestMaintenance", SettingsWindowSource);
        Assert.Contains("await requestMaintenance();", SettingsWindowSource);
        Assert.Contains("Simulate system idle", HelpMarkdown);
    }

    [Fact]
    public void ReadinessDialog_OffersIncrementalUpdateAndCatchupLimitIncreaseBeforeRebuild()
    {
        Assert.Contains("CanAttemptIncrementalIndexRefresh(issue)", SearchInputSource);
        Assert.Contains("Increase limit & update", SearchInputSource);
        Assert.Contains("AddAction(\"Rebuild index\", \"rebuild\")", SearchInputSource);
        Assert.Contains("ComputeRaisedJournalCatchupLimit", SearchInputSource);
        Assert.Contains("RefreshCurrentIndexIncrementallyAsync(actionIssue.IndexRoot, increasedLimit)", SearchInputSource);
        Assert.Contains("operation.AllowFullRebuildFallback = false;", MainViewModelSource);
        Assert.Contains("operation.AllowCompatibilityRebuild = false;", MainViewModelSource);
        Assert.Contains("operation.ForceRefresh = true;", MainViewModelSource);
        Assert.Contains("the existing index was kept unchanged", MainViewModelSource);
    }

    // ── Right-click "Pause indexing" / "Resume indexing" on the status-bar indicator ──

    [Fact]
    public void MainViewModel_ExposesPauseResumeApi()
    {
        Assert.Contains("public partial bool IsIndexingPaused { get; set; }", MainViewModelSource);
        Assert.Contains("public bool CanPauseIndexing => IsIndexBuildActive && !IsIndexingPaused;", MainViewModelSource);
        Assert.Contains("public CancellationToken IndexBuildCancellationToken", MainViewModelSource);
        Assert.Contains("public void PauseIndexing()", MainViewModelSource);
        Assert.Contains("public void ResumeIndexing()", MainViewModelSource);
        // Pause cancels the shared build token; resume re-kicks the paused folder's build.
        Assert.Contains("_indexBuildCancellation?.Cancel();", MainViewModelSource);
        Assert.Contains("StartBackgroundIndexBuild(folder!);", MainViewModelSource);
    }

    [Fact]
    public void MainViewModel_Resume_RestartsMultiRootAutoBuildPass_NotJustSingleFolder()
    {
        // Resuming a paused build that had no single tracked folder (an auto/startup/scheduled multi-root
        // pass) must re-run that pass via the view-installed hook, not silently revert to "index available".
        Assert.Contains("public Func<Task>? ResumeAutoIndexBuildAsync { get; set; }", MainViewModelSource);
        Assert.Contains("ResumeAutoIndexBuildAsync is { } resumeAutoBuild", MainViewModelSource);
        Assert.Contains("_ = resumeAutoBuild();", MainViewModelSource);
        // The view installs the hook and implements it as the shared multi-root build pass.
        Assert.Contains("ViewModel.ResumeAutoIndexBuildAsync = ResumeAutoIndexBuildPassAsync;", StartupChecksSource);
        Assert.Contains("private async Task ResumeAutoIndexBuildPassAsync()", StartupChecksSource);
        Assert.Contains("await RunIndexBuildPassAsync(roots);", StartupChecksSource);
    }

    [Fact]
    public void MainViewModel_ShowsIndexingPausedState()
        => Assert.Contains("IndexStatusText = \"Indexing paused\";", MainViewModelSource);

    [Fact]
    public void MainWindowXaml_IndexStatusIndicatorHasKeyboardAccessibleMenu()
    {
        // ContextRequested (not RightTapped) so the Menu / Shift+F10 key opens the pause/resume menu, and
        // the indicator is focusable + Enter/Space-activatable for keyboard users.
        Assert.Contains("ContextRequested=\"OnIndexStatusContextRequested\"", MainWindowXaml);
        Assert.Contains("KeyDown=\"OnIndexStatusKeyDown\"", MainWindowXaml);
        Assert.Contains("IsTabStop=\"True\"", MainWindowXaml);
        Assert.DoesNotContain("RightTapped=\"OnIndexStatusRightTapped\"", MainWindowXaml);
    }

    [Fact]
    public void IndexOnboarding_ContextMenuOffersPauseOrResume_AndIsKeyboardAccessible()
    {
        Assert.Contains("private void OnIndexStatusContextRequested(UIElement sender, ContextRequestedEventArgs e)", IndexOnboardingSource);
        Assert.Contains("private async void OnIndexStatusKeyDown(", IndexOnboardingSource);
        Assert.Contains("bool canRebuildRegistered = ViewModel.IndexStatusCanBuildRegisteredFolder", IndexOnboardingSource);
        Assert.Contains("Text = $\"Rebuild now ({root})\"", IndexOnboardingSource);
        Assert.Contains("ViewModel.RebuildRegisteredIndexNow(root)", IndexOnboardingSource);
        Assert.Contains("Text = \"Pause indexing\"", IndexOnboardingSource);
        Assert.Contains("Text = \"Resume indexing\"", IndexOnboardingSource);
        Assert.Contains("ViewModel.PauseIndexing()", IndexOnboardingSource);
        Assert.Contains("ViewModel.ResumeIndexing()", IndexOnboardingSource);
        // Keyboard-opened requests carry no position; anchor to the element instead.
        Assert.Contains("e.TryGetPosition(sender, out Windows.Foundation.Point pos)", IndexOnboardingSource);
    }

    [Fact]
    public void IndexStatusMenu_ShowsIndexDate_AndClickTriggersBlockingRebuild()
    {
        // The right-click menu adds an "Index date: … (click to rebuild)" item for a built index root,
        // and clicking it starts a rebuild behind a full-window blocking overlay.
        Assert.Contains("ViewModel.TryGetCurrentIndexRebuildTarget(out string indexDateLabel, out IReadOnlyList<string> builtRoots)", IndexOnboardingSource);
        Assert.Contains("&& !ViewModel.IsIndexBuildActive", IndexOnboardingSource);
        Assert.Contains("&& !ViewModel.IsIndexRebuildBlocking", IndexOnboardingSource);
        Assert.Contains("Text = indexDateLabel,", IndexOnboardingSource);
        Assert.Contains("ViewModel.RebuildCurrentIndexBlockingAsync(builtRoots)", IndexOnboardingSource);

        // The VM formats the date label and only offers rebuild when a searched root has a readable index.
        Assert.Contains("public bool TryGetCurrentIndexRebuildTarget(out string dateLabel, out IReadOnlyList<string> roots)", MainViewModelSource);
        Assert.Contains("\"MM/ddd/yyyy HH:mm\"", MainViewModelSource);
        Assert.Contains("dateLabel = $\"Index date: {date} (click to rebuild)\";", MainViewModelSource);
        // The status refresh captures the built roots + oldest build time for that menu item.
        Assert.Contains("_currentIndexBuiltRoots = builtRoots.Select(b => b.Root).ToArray();", MainViewModelSource);
        Assert.Contains("DateTimeOffset? builtUtc = built.BuiltUtc;", MainViewModelSource);
        Assert.Contains("string indexRoot = manager.ResolveBestAvailableIndexRoot(root, _settings.IndexedRoots);", MainViewModelSource);
        Assert.Contains("IndexMetadataStatus meta = manager.GetMetadataStatusForRoot(indexRoot);", MainViewModelSource);
    }

    [Fact]
    public void IndexRebuild_BlockingOverlay_CoversContentAndCanCancelSafely()
    {
        // The blocking rebuild is awaited (not fire-and-forget) and toggles the overlay flag around it.
        Assert.Contains("public async Task RebuildCurrentIndexBlockingAsync(IReadOnlyList<string> roots)", MainViewModelSource);
        Assert.Contains("RunCurrentIndexBlockingAsync(roots, rebuild: true)", MainViewModelSource);
        string rebuild = ExtractFrom(MainViewModelSource, "private async Task RunCurrentIndexBlockingAsync(IReadOnlyList<string> roots, bool rebuild)", 3000);
        Assert.Contains("IsIndexRebuildBlocking = true;", rebuild);
        Assert.Contains("await BuildOneBlockingAsync(", rebuild);
        Assert.Contains("IsIndexRebuildBlocking = false;", rebuild);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(IndexBuildCancellationToken)", rebuild);
        Assert.Contains("cancellation.Token", rebuild);
        Assert.Contains("await Task.Yield();", rebuild);
        Assert.Contains("public void CancelCurrentIndexRebuild()", MainViewModelSource);
        Assert.Contains("IsIndexRebuildCancelling = true;", MainViewModelSource);
        Assert.Contains("_indexRebuildCancellation?.Cancel();", MainViewModelSource);
        Assert.DoesNotContain("PauseIndexing();", rebuild);
        Assert.Contains("try { _indexRebuildCancellation?.Cancel(); } catch { }", MainViewModelSource);
        // Progress is folded across roots into the 0–100 overlay value.
        Assert.Contains("private void ReportRebuildBlockingProgress(string root, int index, int total, int percent)", MainViewModelSource);

        // The overlay is a full-content grid (rows 2–5) above everything, whose hit-testable acrylic Border
        // swallows pointer input to the UI below, driven by IsIndexRebuildBlocking.
        Assert.Contains("x:Name=\"IndexRebuildBusyOverlay\"", MainWindowXaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsIndexRebuildBlocking, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Value=\"{x:Bind ViewModel.IndexRebuildProgressPercent, Mode=OneWay}\"", MainWindowXaml);
        string overlay = ExtractFrom(MainWindowXaml, "x:Name=\"IndexRebuildBusyOverlay\"", 1800);
        Assert.Contains("IsIndeterminate=\"True\"", overlay);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexRebuildOverlayTitle, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("{x:Bind ViewModel.IndexRebuildProgressText, Mode=OneWay}", MainWindowXaml);
        Assert.Contains("{x:Bind ViewModel.IndexRebuildProgressPercentLabel, Mode=OneWay}", MainWindowXaml);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanCancelIndexRebuild, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.IndexRebuildCancelButtonText, Mode=OneWay}\"", MainWindowXaml);
        Assert.Contains("Click=\"OnCancelIndexRebuildClick\"", MainWindowXaml);
        Assert.Contains("ViewModel.CancelCurrentIndexRebuild();", IndexOnboardingSource);
    }

    [Fact]
    public void IndexStatusMenu_HasDisableIndexSubmenu_WithPauseThisRunAndPersistentOptions()
    {
        // Right-clicking ANY index label (Index: full, Index: accelerating, …) offers an "Options"
        // submenu (an inert MenuFlyoutSubItem that only expands on hover/click) with Pause indexing /
        // Disable index (this run) / Disable indexing (persistent). The old early-return that hid the menu
        // for a built/idle index is gone so the menu shows on every index label.
        Assert.DoesNotContain("return; // no command applies to this status", IndexOnboardingSource);
        Assert.Contains("var disableSubMenu = new MenuFlyoutSubItem", IndexOnboardingSource);
        Assert.Contains("Text = \"Options\",", IndexOnboardingSource);
        Assert.Contains("Text = \"Disable index (this run)\",", IndexOnboardingSource);
        Assert.Contains("ViewModel.DisableContentIndexThisRun()", IndexOnboardingSource);
        Assert.Contains("Text = \"Disable indexing (persistent)\",", IndexOnboardingSource);
        Assert.Contains("ViewModel.DisableContentIndexPersistentlyAsync()", IndexOnboardingSource);
        Assert.Contains("menu.Items.Add(disableSubMenu);", IndexOnboardingSource);

        // "Disable index (this run)" is session-only: clears UseContentIndex but never persists.
        Assert.Contains("public void DisableContentIndexThisRun()", MainViewModelSource);
        string thisRun = ExtractFrom(MainViewModelSource, "public void DisableContentIndexThisRun()", 600);
        Assert.Contains("UseContentIndex = false;", thisRun);
        Assert.DoesNotContain("_settings.EnableContentIndex = false;", thisRun);
        Assert.DoesNotContain("PersistSettingsAsync", thisRun);

        // "Disable indexing (persistent)" turns the master feature off and saves.
        Assert.Contains("public async Task DisableContentIndexPersistentlyAsync()", MainViewModelSource);
        string persistent = ExtractFrom(MainViewModelSource, "public async Task DisableContentIndexPersistentlyAsync()", 1400);
        Assert.Contains("_settings.EnableContentIndex = false;", persistent);
        Assert.Contains("await PersistSettingsAsync()", persistent);
        // It keeps a sticky "Index: off" indicator (not ShowIndexStatus=false) so the re-enable menu stays reachable.
        Assert.Contains("_indexOffIndicatorSticky = true;", persistent);
        Assert.Contains("ShowIndexDisabledIndicator();", persistent);
    }

    [Fact]
    public void IndexStatusMenu_OffersReEnableAfterDisabling()
    {
        // After a disable the SAME right-click menu must offer the reverse command so the index can be
        // turned back on without opening Settings.
        // (1) Persistently off → a muted "Index: off" indicator stays visible and the Options submenu's
        // persistent toggle reads "Enable indexing (persistent)" (the inverse of "Disable indexing (persistent)").
        Assert.Contains("if (!ViewModel.Settings.EnableContentIndex)", IndexOnboardingSource);
        Assert.Contains("Text = \"Enable indexing (persistent)\"", IndexOnboardingSource);
        Assert.Contains("ViewModel.EnableContentIndexFromStatusMenuAsync()", IndexOnboardingSource);
        // (2) Used-this-run toggles: when off for this run the submenu shows "Use index (this run)".
        Assert.Contains("if (ViewModel.UseContentIndex)", IndexOnboardingSource);
        Assert.Contains("Text = \"Use index (this run)\"", IndexOnboardingSource);
        Assert.Contains("ViewModel.EnableContentIndexThisRun()", IndexOnboardingSource);

        // The sticky "Index: off" indicator survives status refreshes so the menu stays reachable.
        Assert.Contains("private bool _indexOffIndicatorSticky;", MainViewModelSource);
        Assert.Contains("private void ShowIndexDisabledIndicator()", MainViewModelSource);
        string offIndicator = ExtractFrom(MainViewModelSource, "private void ShowIndexDisabledIndicator()", 500);
        Assert.Contains("IndexStatusText = \"Index: off\";", offIndicator);
        Assert.Contains("ShowIndexStatus = true;", offIndicator);
        Assert.Contains("if (_indexOffIndicatorSticky && !_settings.EnableContentIndex && _settings.ShowIndexStatusInMainWindow)", MainViewModelSource);

        // Re-enable methods: this-run flips UseContentIndex on (no persist); persistent flips the master on and saves.
        Assert.Contains("public void EnableContentIndexThisRun()", MainViewModelSource);
        string reEnableThisRun = ExtractFrom(MainViewModelSource, "public void EnableContentIndexThisRun()", 500);
        Assert.Contains("UseContentIndex = true;", reEnableThisRun);
        Assert.DoesNotContain("PersistSettingsAsync", reEnableThisRun);
        Assert.Contains("public async Task EnableContentIndexFromStatusMenuAsync()", MainViewModelSource);
        string reEnablePersistent = ExtractFrom(MainViewModelSource, "public async Task EnableContentIndexFromStatusMenuAsync()", 900);
        Assert.Contains("_settings.EnableContentIndex = true;", reEnablePersistent);
        Assert.Contains("_indexOffIndicatorSticky = false;", reEnablePersistent);
        Assert.Contains("await PersistSettingsAsync()", reEnablePersistent);
    }

    [Fact]
    public void MainViewModel_StatusMenuRebuildsRegisteredRootThroughBackgroundWorkerPath()
    {
        Assert.Contains("public void RebuildRegisteredIndexNow(string folder)", MainViewModelSource);
        Assert.Contains("IndexedRootsPolicy.Contains(_settings.IndexedRoots, root)", MainViewModelSource);
        Assert.Contains("StartBackgroundIndexBuild(root, rebuild: true);", MainViewModelSource);
        Assert.Contains("private void StartBackgroundIndexBuild(string folder, bool rebuild = false)", MainViewModelSource);
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(_settings, root, rebuild)", MainViewModelSource);
    }

    [Fact]
    public void Startup_AutoBuildHonorsPauseAndPassesToken()
    {
        Assert.Contains("if (ViewModel.IsIndexingPaused)", StartupChecksSource);
        Assert.Contains("ViewModel.IndexBuildCancellationToken", StartupChecksSource);
    }

    [Fact]
    public void SettingsBuildNow_LinksToPauseToken()
        => Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _viewModel.IndexBuildCancellationToken)", SettingsIndexingActionsSource);

    // ── Disk-space stop guard: stop indexing when the drive is over the configured % (default 90) ──

    [Fact]
    public void Settings_DeclaresIndexMaxDiskUsagePercent()
    {
        Assert.Contains("public const int DefaultIndexMaxDiskUsagePercent = 90;", SettingsServiceSource);
        Assert.Contains("public int IndexMaxDiskUsagePercent { get; set; }", SettingsServiceSource);
        Assert.Contains("EffectiveIndexMaxDiskUsagePercent", SettingsServiceSource);
    }

    [Fact]
    public void IndexDiskGuard_StopsBuildAndWarnsInStatusBar()
    {
        // The VM surfaces a disk-full warning in the status indicator and auto-pauses.
        Assert.Contains("public void OnIndexBuildStoppedForDiskSpace(", MainViewModelSource);
        Assert.Contains("IndexStatusText = \"Index: disk full\";", MainViewModelSource);
        Assert.Contains("OnIndexBuildStoppedForDiskSpace(ex.DriveDisplayName, ex.UsedPercent, ex.ThresholdPercent)", MainViewModelSource);
        // Every build site routes a disk-full stop into the VM warning.
        Assert.Contains("catch (IndexDiskFullException ex)", StartupChecksSource);
        Assert.Contains("catch (IndexDiskFullException ex)", SettingsIndexingActionsSource);
        // The immutable operation snapshot carries the disk limit into startup/watcher maintenance.
        Assert.Contains("IndexBuildOperationFactory.CreateMaintenance(", StartupChecksSource);
        Assert.Contains("MaxDiskUsagePercent = settings.EffectiveIndexMaxDiskUsagePercent", Read("src", "Yagu", "Services", "Index", "IndexBuildOperationFactory.cs"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
