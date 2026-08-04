namespace Yagu.Tests;

public sealed class CliRunnerRegressionTests
{
    [Fact]
    public void CliSearch_ForcesConsoleLoggingToCritical()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("LogService.InitFromSettings((YaguLogLevel)settings.LogLevelIndex, YaguLogLevel.Critical);", source);
        Assert.DoesNotContain("LogService.InitFromSettings((YaguLogLevel)settings.LogLevelIndex, (YaguLogLevel)settings.ConsoleLogLevelIndex);", source);
    }

    [Fact]
    public void CliParser_RecognizesDashHelpAlias()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("Eq(tok, \"--help\", \"-help\"", source);
    }

    [Fact]
    public void CliRunner_RunsFirstRunPromptsBeforeSearch()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // The first-run prompts run after settings load and before the search options are built, and use
        // the settings service bound to the same file the settings were loaded from.
        Assert.Contains("var settingsService = ResolveSettingsService(args);", source);
        Assert.Contains("CliFirstRunPrompts.RunAsync(settings, settingsService).GetAwaiter().GetResult();", source);
        Assert.Contains("private static SettingsService ResolveSettingsService(CliArgs args)", source);
        int promptsCall = source.IndexOf("CliFirstRunPrompts.RunAsync(", StringComparison.Ordinal);
        int buildOptions = source.IndexOf("var perRootOptions = BuildPerRootSearchOptions(args, settings);", StringComparison.Ordinal);
        Assert.True(promptsCall >= 0 && buildOptions > promptsCall,
            "First-run prompts must run before the per-root search options are built.");
    }

    [Fact]
    public void CliFirstRunPrompts_MirrorGuiStartupPromptsGatedBySameSettings()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliFirstRunPrompts.cs"));

        // No-op for automated/piped runs.
        Assert.Contains("if (Console.IsInputRedirected)", source);
        // Each prompt gates on the SAME persisted flag as its GUI counterpart, so answering on either
        // surface suppresses it on the other (true parity).
        Assert.Contains("if (settings.TelemetryConsentPromptShown)", source);
        Assert.Contains("if (settings.HasChosenSearchResultTempDirectory &&", source);
        Assert.Contains("if (settings.HasCompletedFirstRun)", source);
        Assert.Contains("if (!settings.SemanticSearchEnabled || settings.CpuSemanticWarningShown)", source);
        Assert.Contains("if (settings.HasPromptedIndexOnboarding)", source);
        Assert.Contains("DefaultContentIndexPathProvider.TryGetPreservedStorageDirectory", source);
        Assert.Contains("DefaultContentIndexPathProvider.ClearPreservedStorageDirectory();", source);
        Assert.Contains("settings.IndexedRoots.Count > 0", source);
        Assert.Contains(".GetReusableStoredIndexRoots()", source);
        Assert.Contains("Existing content indexes found:", source);
        Assert.Contains("Use these indexes again without rebuilding them? [Y/n]", source);
        Assert.Contains("settings.IndexedRoots = IndexedRootsPolicy.Add(settings.IndexedRoots, root);", source);
        Assert.DoesNotContain(".HasReadableStoredIndex()", source);
        Assert.Contains("FoundryModelUpdateChecker.ShouldCheck(", source);
        // Actions reuse the same services as the GUI.
        Assert.Contains("ExplorerContextMenu.Register();", source);
        Assert.Contains("settings.IndexedRoots = IndexedRootsPolicy.Add(settings.IndexedRoots, folder);", source);
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(settings, effectiveRoot, rebuild: false)", source);
        Assert.Contains("coordinator.BuildFullScopePreferWorkerAsync(", source);
    }

    [Fact]
    public void CliFirstRunIndexOnboarding_AllowsMultipleFoldersAndABuildTrigger()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliFirstRunPrompts.cs"));

        // Parity with the GUI onboarding dialog: enter one or more folders, then choose build trigger(s).
        Assert.Contains("Enter one or more folder paths to index", source);
        Assert.Contains("var effectiveRoots = new List<string>();", source);
        Assert.Contains("foreach (string effectiveRoot in effectiveRoots)", source);
        // The build trigger prompt normalizes the combined selection (Manual when none).
        Assert.Contains("settings.IndexBuildTrigger = PromptIndexBuildTrigger(settings.IndexBuildTrigger);", source);
        Assert.Contains("private static string PromptIndexBuildTrigger(string currentTrigger)", source);
        Assert.Contains("AppSettings.NormalizeIndexBuildTrigger(string.Join(\",\", selected))", source);
    }

    [Fact]
    public void CliFirstRunIndexOnboarding_PicksAnAutomaticUpdateModeForAnAutomaticTrigger()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliFirstRunPrompts.cs"));

        // Parity with the GUI onboarding dialog: a recurring trigger must not be left on the default
        // ManualFullRebuild, which only ever CREATES missing indexes (existing ones silently go stale).
        Assert.Contains("settings.IndexUpdateMode = PromptIndexUpdateMode(settings.IndexBuildTrigger, settings.IndexUpdateMode);", source);
        Assert.Contains("private static string PromptIndexUpdateMode(string buildTrigger, string currentUpdateMode)", source);
        Assert.Contains("ContentIndexBuildScheduler.RecommendedUpdateMode(buildTrigger, currentUpdateMode)", source);
        // The recommendation is a DEFAULT, not a lock-in — the user can still pick another mode.
        Assert.Contains("(recommended)", source);
        Assert.Contains("AppSettings.NormalizeIndexUpdateMode(mode)", source);
        Assert.Contains("ContentIndexBuildScheduler.IsStaleAutomaticCombination(buildTrigger, mode)", source);
        // Ordering: the trigger is chosen first, because it drives the recommended update mode.
        int trigger = source.IndexOf("settings.IndexBuildTrigger = PromptIndexBuildTrigger(", StringComparison.Ordinal);
        int mode = source.IndexOf("settings.IndexUpdateMode = PromptIndexUpdateMode(", StringComparison.Ordinal);
        Assert.True(trigger >= 0 && mode > trigger, "The update-mode prompt must follow the build-trigger prompt.");
    }

    [Fact]
    public void ExplorerContextMenu_IsSharedByGuiAndCli()
    {
        string gui = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SettingsMenus.cs"));
        string service = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "ExplorerContextMenu.cs"));

        // Both surfaces write the identical registry keys via the shared service (no drift).
        Assert.Contains("private static bool IsContextMenuRegistered() => ExplorerContextMenu.IsRegistered();", gui);
        Assert.Contains("private static void RegisterContextMenu() => ExplorerContextMenu.Register();", gui);
        Assert.Contains(@"Software\Classes\Directory\shell\Yagu", service);
        Assert.Contains(@"Software\Classes\Directory\Background\shell\Yagu", service);
        Assert.Contains("\"Search with Yagu\"", service);
    }

    [Fact]
    public void ProgramHelpShortcut_ExitsProcessAfterPrintingHelp()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Program.cs"));

        Assert.Matches("CliRunner\\.RunHelp\\(\\);\\s*Environment\\.Exit\\(0\\);", source);
    }

    [Fact]
    public void YaguExecutable_UsesConsoleSubsystemForCliHelp()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Yagu.csproj"));

        Assert.Contains("<OutputType>Exe</OutputType>", source);
        Assert.DoesNotContain("<OutputType>WinExe</OutputType>", source);
    }

    [Fact]
    public void ProgramGuiMode_RelaunchesDetachedBeforeStartingWinUi()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Program.cs"));

        Assert.Contains("TryRelaunchDetachedGui(args)", source);
        Assert.Contains("CreateNoWindow = true", source);
        Assert.Contains("FreeConsole();", source);
    }

    [Fact]
    public void CliHelp_IncludesTwoHundredExamples()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        int exampleCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+\d{3}\. ").Count;
        int explanationCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+Does: ").Count;
        int commandCount = System.Text.RegularExpressions.Regex.Matches(source, @"(?m)^\s+Cmd:\s+Yagu\.exe --cli ").Count;

        Assert.Equal(217, exampleCount);
        Assert.Equal(217, explanationCount);
        Assert.Equal(217, commandCount);
        Assert.Contains("EXAMPLES (217):", source);
        Assert.Contains("001. Basic search in the current folder", source);
        Assert.Contains("Does: Finds TODO anywhere under the current directory.", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory . \"TODO\"", source);
        Assert.Contains("101. Search for API key patterns", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory . -e \"api[_-]?key\"", source);
        Assert.Contains("201. Search source and write a compact audit", source);
        Assert.Contains("Cmd:  Yagu.exe --cli --directory src \"TODO\" -g \"*.cs\" --export .\\reports\\todo-audit.json --export-no-markers", source);
        Assert.Contains("204. Semantic search with a specific local model", source);
        Assert.Contains("205. Semantic search in a script (auto-download the recommended model)", source);
        Assert.Contains("206. Exclude hidden files and folders", source);
        Assert.Contains("207. Force-include hidden files", source);
        Assert.Contains("208. Group results by directory", source);
        Assert.Contains("210. Group by modified date, oldest groups first", source);
        Assert.Contains("211. Search text inside images (OCR)", source);
        Assert.Contains("212. Search image text with the Tesseract engine", source);
        Assert.Contains("216. Search text inside PDFs", source);
        Assert.Contains("217. Search PDFs and images together", source);
    }
    [Fact]
    public void CliSettings_LoadsCurrentDirectoryThenProcessLaunchDirectoryThenGlobal()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFileName)",
            "var launchSettings = ResolveProcessLaunchSettingsPath();",
            "return new SettingsService();");
        Assert.Contains("Environment.ProcessPath", source);
        Assert.Contains("AppContext.BaseDirectory", source);
        Assert.Contains("If not, Yagu checks the running process launch", source);
        Assert.Contains("directory next, then falls back to global AppData settings", source);
    }

    [Fact]
    public void CliParser_RecognizesAcceptModelDownloadFlag()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("public bool             AcceptModelDownload { get; private set; }", source);
        Assert.Contains("Eq(tok, \"--accept-model-download\", \"--yes-download\")", source);
    }

    [Fact]
    public void CliParser_RecognizesGroupFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // The --group value flag plus the asc/desc orientation flags are parsed.
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--group\")", source);
        Assert.Contains("Eq(tok, \"--group-desc\", \"--group-descending\")", source);
        Assert.Contains("Eq(tok, \"--group-asc\", \"--group-ascending\")", source);
        // Backing args properties exist.
        Assert.Contains("public string?          GroupBy { get; private set; }", source);
        Assert.Contains("public bool             GroupDescending { get; private set; }", source);
        // The canonical-key normalizer exists and maps the documented keys.
        Assert.Contains("internal static string? NormalizeGroupKey(string raw)", source);
        // Help documents the flag and its keys.
        Assert.Contains("--group <key>", source);
        Assert.Contains("directory, extension, size, modified,", source);
    }

    [Fact]
    public void CliParser_SortFlagAcceptsDirectoryKey()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // --sort now accepts the directory/dir keys (previously rejected as unknown).
        Assert.Contains("or \"size\" or \"name\" or \"filename\" or \"directory\" or \"dir\" or \"path\")", source);
    }

    [Fact]
    public void CliParser_RecognizesContentIndexFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Per-search flags and management-command flags are parsed.
        Assert.Contains("Eq(tok, \"--use-index\")", source);
        Assert.Contains("Eq(tok, \"--no-index\")", source);
        Assert.Contains("Eq(tok, \"--build-index\")", source);
        Assert.Contains("Eq(tok, \"--rebuild-index\")", source);
        Assert.Contains("Eq(tok, \"--index-status\")", source);
        Assert.Contains("Eq(tok, \"--index-config\")", source);
        Assert.Contains("Eq(tok, \"--clear-indexes\", \"--clear-index\")", source);
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--delete-index\")", source);
        Assert.Contains("stored records:", source);
        Assert.Contains("created (UTC):", source);
        Assert.Contains("active generation built (UTC):", source);
        Assert.Contains("last incremental update (UTC):", source);

        // Indexed-folder root management commands.
        Assert.Contains("Eq(tok, \"--index-list-roots\")", source);
        Assert.Contains("Eq(tok, \"--index-add-root\")", source);
        Assert.Contains("Eq(tok, \"--index-remove-root\")", source);
        Assert.Contains("return RunIndexRoots(args);", source);
        Assert.Contains("IndexedRootsPolicy.Add(settings.IndexedRoots", source);
        Assert.Contains("IndexedRootsPolicy.Remove(settings.IndexedRoots", source);

        // Per-folder glob overrides (plan §6.1): set/clear a root's include/exclude globs + list them.
        Assert.Contains("Eq(tok, \"--index-set-root-filter\")", source);
        Assert.Contains("Eq(tok, \"--index-clear-root-filter\")", source);
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--root-include\")", source);
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--root-exclude\")", source);
        Assert.Contains("IndexedRootFilterPolicy.Normalize(filters)", source);
        Assert.Contains("IndexedRootFilterPolicy.Find(settings.IndexedRootFilters, root)", source);

        // Backing args + the management short-circuit dispatch.
        Assert.Contains("public bool?            UseContentIndex { get; private set; }", source);
        Assert.Contains("public bool IsIndexManagementCommand", source);
        Assert.Contains("if (args.IsIndexManagementCommand)", source);
        Assert.Contains("return RunIndexManagement(args);", source);
    }

    [Fact]
    public void CliIndexStatus_ExplainsRepairableAndMissingSourceStates()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("status.Health == IndexStorageHealth.SourceMissing", source);
        Assert.Contains("ContentIndexUiStatus.StorageHealthLabel(status.Health)", source);
        Assert.Contains("Yagu.exe --cli --rebuild-index", source);
        Assert.Contains("Yagu.exe --cli --delete-index", source);
        Assert.Contains("searches safely live-scan until this is resolved", source);
    }

    [Fact]
    public void CliSearchOptions_AttachContentIndexGateFactoryWhenOptedIn()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // The CLI accelerates the same way the GUI does: when the search opts into the index, it attaches
        // a closure that builds the pruning gate off-thread at discovery start (plan §5).
        Assert.Contains("if (searchOptions.UseContentIndex)", source);
        Assert.Contains("searchOptions.ContentIndexGateFactory = () =>", source);
        Assert.Contains("return ContentIndexSearchGate.TryCreate(", source);
        Assert.Contains("DefaultContentIndexPathProvider.Create(gateStorageDir)", source);
        Assert.Contains("return searchOptions;", source);

        // Size gate (plan §6.1): the CLI never loads a layered index larger than the in-process size limit
        // (a multi-GB deserialize would be slower than a live scan) — it live-scans that scope instead.
        Assert.Contains("int gateMaxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(gateSettings.IndexMaxInProcessSizeMB);", source);
        Assert.Contains("ResolveBestAvailableIndexRoot(gateOptions.Directory, gateSettings.IndexedRoots)", source);
        Assert.Contains("if (!ContentIndexSearchGate.IsScopeWithinInProcessSizeLimit(gatePathProvider, indexRoot, gateRetained, gateMaxInProcessSizeMB))", source);


        // Opt-in worker path (plan §3.3): when IndexUseNativeWorker is on, the CLI passes an
        // IndexWorkerQuerySource so the query runs in the isolated worker (falls back in-process on failure).
        Assert.Contains("gateSettings.IndexUseNativeWorker", source);
        Assert.Contains("new IndexWorkerQuerySource(new IndexWorkerClient())", source);
        Assert.Contains("candidateSource: gateCandidateSource", source);

        // Stage-5 worker pruning (plan §5.8): gated by the IndexUseWorkerQuerySessions setting, the CLI
        // sets a ContentIndexPruningScanFactory that builds+opens a PRUNING worker session via
        // ContentIndexShadowScopeBuilder.TryCreatePruningScan (the in-process gate returns null when on).
        Assert.Contains("if (gateSettings.IndexUseWorkerQuerySessions)", source);
        Assert.Contains("searchOptions.ContentIndexPruningScanFactory = survivorSink =>", source);
        Assert.Contains("ContentIndexShadowScopeBuilder.TryCreatePruningScan(", source);
        Assert.Contains("string pruningSpoolDir = ContentIndexRecoverySpool.ResolveDirectory(pruningPathProvider);", source);
        Assert.DoesNotContain("Path.Combine(gateStorageDir, \"query-spool\")", source);
        // Out-of-process size cap (IndexMaxWorkerQuerySizeMB, default 30 GB): the CLI worker path is bounded
        // too — an index over the worker cap live-scans instead of engaging the worker.
        Assert.Contains("int gateMaxWorkerQuerySizeMB = AppSettings.NormalizeIndexMaxWorkerQuerySizeMB(gateSettings.IndexMaxWorkerQuerySizeMB);", source);
        Assert.Contains("if (!ContentIndexSearchGate.IsScopeWithinWorkerMappedSizeLimit(pruningPathProvider, indexRoot, gateRetained, gateMaxWorkerQuerySizeMB))", source);
    }

    [Fact]
    public void CliIndexManagement_ConsolidatesOverlapsAndSkipsDuplicateChildBuilds()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("IndexedRootsPolicy.FindBestCoveringRoot(settings.IndexedRoots, requested)", source);
        Assert.Contains("is already covered by indexed root", source);
        Assert.Contains("consolidated {coveredDescendants.Count} narrower covered root(s)", source);
        Assert.Contains("Skipped duplicate index build", source);
        Assert.Contains("parent and child indexes are never opened together", source);
    }

    [Fact]
    public void CliCompletionSummary_ReportsContentIndexCoverage()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Parity with the GUI post-search coverage indicator (plan §6.2): the CLI prints a content-index
        // line from the aggregated summary's IndexAcceleration when the index participated.
        Assert.Contains("s.IndexAcceleration is { RequestedRoots: > 0 } idx", source);
        Assert.Contains("ContentIndexUiStatus.CoverageCliSummary(coverage, idx.FilesPruned)", source);
    }

    [Fact]
    public void CliWarnings_AvoidDuplicatePressureAndEmptyPowerShellErrorRecords()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Repeated pressure events still acknowledge eviction, but only the first emits a warning.
        Assert.Contains("bool memoryPressureWarningShown = false;", source);
        Assert.Contains("if (!memoryPressureWarningShown)", source);
        Assert.Contains("memoryPressureWarningShown = true;", source);

        // Empty stderr records can appear as the literal RemoteException type in PowerShell hosts.
        Assert.Contains("msg = msg.TrimStart('\\r', '\\n');", source);
        Assert.Contains("if (msg.Length == 0)", source);
        Assert.Contains("msg = \" \";", source);
    }

    [Fact]
    public void CliPlainStreaming_PrintsManagedFilenameMatchesThroughDirectOutputStream()
    {
        string cli = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));
        string search = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "SearchService.cs"));

        Assert.Contains("object? directOutputLock = directStream is null ? null : new object();", cli);
        Assert.Contains("rootOptions.DirectOutputLock = directOutputLock;", cli);
        Assert.Contains("directStream?.Flush();", cli);
        Assert.Contains("DirectOutputSink.WriteFileNameMatches(", search);
        Assert.Contains("outputLock: options.DirectOutputLock", search);
    }

    [Fact]
    public void CliRunner_ThreadsUseContentIndexIntoSearchOptions()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Default derives from settings and is gated on the master feature; per-search flag overrides.
        Assert.Contains("bool useContentIndex = (args.UseContentIndex ?? s.UseContentIndexByDefault) && s.EnableContentIndex;", source);
        Assert.Contains("UseContentIndex       = useContentIndex,", source);
    }

    [Fact]
    public void CliRunner_IndexManagementUsesSharedServicesAndExitCodes()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Management commands go through the shared managed services and stable exit codes.
        Assert.Contains("new ContentIndexManager(pathProvider, retained)", source);
        Assert.Contains("ContentIndexConfigService.Reset(settings)", source);
        Assert.Contains("ContentIndexConfigService.SetMany(settings, pairs)", source);
        Assert.Contains("ContentIndexConfigService.GetAll(settings)", source);
        Assert.Contains("(int)ContentIndexExitCode.UnsupportedScope", source);
        Assert.Contains("(int)ContentIndexExitCode.BuildFailure", source);
        Assert.Contains("(int)ContentIndexExitCode.InvalidArguments", source);
    }

    [Fact]
    public void CliIndexSettingsChanges_PrintExactRebuildRecommendations()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);", source);
        Assert.Contains("WriteIndexRebuildRecommendation(ContentIndexSettingsChangeAdvisor.Analyze(", source);
        Assert.Contains("private static void WriteIndexRebuildRecommendation(ContentIndexSettingsChangeAdvice advice)", source);
        Assert.Contains("Rebuild recommended for {advice.AffectedRoots.Count} maintained index(es):", source);
        Assert.Contains("Yagu.exe --cli --rebuild-index", source);
    }

    [Fact]
    public void CliHelp_DocumentsContentIndexFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.DoesNotContain("sidecar", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONTENT INDEX (opt-in accelerator):", source);
        Assert.Contains("--build-index [<path>]", source);
        Assert.Contains("--index-config <k>=<v>", source);
        Assert.Contains("IndexPostBuildCatchUpThresholdChanges=30000", source);
        Assert.Contains("--index-add-root <path>", source);
    }

    [Fact]
    public void HelpMarkdown_DocumentsContentIndexCli()
    {
        string help = File.ReadAllText(Path.Combine(FindRepoRoot(), "HELP.md"));

        Assert.Contains("### Content Index (CLI)", help);
        Assert.Contains("`--use-index`", help);
        Assert.Contains("`--build-index [<path>]`", help);
        Assert.Contains("`--index-config reset`", help);
        Assert.Contains("IndexPostBuildCatchUpThresholdChanges=30000", help);
        Assert.Contains("Changes during a full build", help);
        Assert.Contains("`--index-add-root <path>`", help);
        Assert.Contains("Build-output changes print the affected roots", help);
    }

    [Fact]
    public void CliSearch_GroupingWaitsForCompletionThenRendersGrouped()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Grouping forces result collection (no live streaming) and renders via WriteGroupedResults.
        Assert.Contains("bool grouping = !string.IsNullOrWhiteSpace(args.GroupBy);", source);
        Assert.Contains("exporting || replacing || sorting || grouping || savingSession", source);
        Assert.Contains("WriteGroupedResults(collectedResults, args, useColor);", source);
        Assert.Contains("private static void WriteGroupedResults(", source);
    }

    [Fact]
    public void CliSemanticOverlay_FoldsSortAndGroupWhenUnset()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // The semantic overlay only fills sort/group when the user did not set them explicitly.
        Assert.Contains("if (overlay.SortBy is { } sortKey && SortBy is null)", source);
        Assert.Contains("if (overlay.GroupBy is { } groupKey && GroupBy is null)", source);
    }

    [Fact]
    public void CliSemanticOverlay_UnskipsBinaryWhenPlanTargetsBinaryExtensions()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // A semantic plan targeting known-binary extensions (.exe/.com/.cpl) must stop skipping binary
        // content so a content search over those files actually reads them (GUI/CLI parity).
        Assert.Contains("if (overlay.SearchBinary == true && SkipBinary is null) SkipBinary = false;", source);
    }

    [Fact]
    public void SemanticSystemPrompt_DocumentsSortAndGroupFields()
    {
        string prompt = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Yagu", "Services", "Ai", "Prompts", "SemanticSearchSystemPrompt.prompt.md"));

        Assert.Contains("\"sortBy\"", prompt);
        Assert.Contains("\"sortDirection\"", prompt);
        Assert.Contains("\"groupBy\"", prompt);
        Assert.Contains("\"groupDirection\"", prompt);
        Assert.Contains("SORTING & GROUPING", prompt);
    }

    [Fact]
    public void CliParser_RecognizesHiddenFileFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Parser recognizes both enable and disable forms (with aliases).
        Assert.Contains("Eq(tok, \"--hidden\", \"--search-hidden\")", source);
        Assert.Contains("Eq(tok, \"--no-hidden\", \"--no-search-hidden\")", source);
        // Nullable arg property exists so settings default applies when the flag is omitted.
        Assert.Contains("public bool?            SearchHiddenFiles { get; private set; }", source);
        // Built into SearchOptions with the settings value as the fallback.
        Assert.Contains("SearchHiddenFiles     = args.SearchHiddenFiles ?? s.SearchHiddenFiles", source);
        // Help mentions the flags.
        Assert.Contains("--no-hidden", source);
    }

    [Fact]
    public void CliParser_RecognizesImageTextFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Parser recognizes both enable and disable forms (with aliases) plus the engine option.
        Assert.Contains("Eq(tok, \"--image-text\", \"--search-image-text\", \"--ocr\")", source);
        Assert.Contains("Eq(tok, \"--no-image-text\", \"--no-search-image-text\", \"--no-ocr\")", source);
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--ocr-engine\")", source);
        // Nullable arg properties exist so the settings default applies when the flag is omitted.
        Assert.Contains("public bool?            SearchImageText { get; private set; }", source);
        Assert.Contains("public string?          ImageOcrEngine { get; private set; }", source);
        // Built into SearchOptions with the settings value as the fallback.
        Assert.Contains("SearchImageText       = searchImageText", source);
        Assert.Contains("ImageOcrEngine        = imageOcrEngine", source);
        // Help mentions the flags.
        Assert.Contains("--image-text", source);
        Assert.Contains("--ocr-engine <name>", source);
    }

    [Fact]
    public void CliParser_RecognizesPdfTextFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Parser recognizes both enable and disable forms (with aliases).
        Assert.Contains("Eq(tok, \"--pdf-text\", \"--search-pdf-text\", \"--pdf\")", source);
        Assert.Contains("Eq(tok, \"--no-pdf-text\", \"--no-search-pdf-text\", \"--no-pdf\")", source);
        // Nullable arg property exists so the settings default applies when the flag is omitted.
        Assert.Contains("public bool?            SearchPdfText { get; private set; }", source);
        // Built into SearchOptions with the settings value as the fallback.
        Assert.Contains("SearchPdfText         = searchPdfText", source);
        Assert.Contains("PdfTextExtensions     = SplitSemi(AppSettings.DefaultPdfTextExtensions)", source);
        // Help mentions the flag.
        Assert.Contains("--pdf-text", source);
    }

    [Fact]
    public void CliParser_RecognizesImageOcrQualityAndWorkerFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Parser recognizes the model and detection-resolution options.
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--ocr-model\")", source);
        Assert.Contains("TryGetInt(raw, ref i, out n, \"--ocr-max-side\")", source);
        Assert.Contains("TryGetInt(raw, ref i, out n, \"--ocr-workers\")", source);
        // Nullable arg properties exist so the settings default applies when the flag is omitted.
        Assert.Contains("public string?          ImageOcrModel { get; private set; }", source);
        Assert.Contains("public int?             ImageOcrMaxSide { get; private set; }", source);
        Assert.Contains("public int?             ImageOcrWorkerParallelism { get; private set; }", source);
        // Built into SearchOptions with the settings value as the fallback.
        Assert.Contains("ImageOcrModel         = imageOcrModel", source);
        Assert.Contains("ImageOcrMaxSide       = imageOcrMaxSide", source);
        Assert.Contains("ImageOcrWorkerParallelism = imageOcrWorkerParallelism", source);
        Assert.Contains("args.ImageOcrModel ?? s.ImageOcrModel", source);
        Assert.Contains("args.ImageOcrMaxSide ?? s.ImageOcrMaxSide", source);
        Assert.Contains("args.ImageOcrWorkerParallelism ?? s.ImageOcrWorkerParallelism", source);
        Assert.Contains("OcrWorkerParallelism.Resolve(", source);
        Assert.Contains("s.LimitParallelismOnHdd,", source);
        // Help mentions the flags.
        Assert.Contains("--ocr-model <name>", source);
        Assert.Contains("--ocr-max-side <px>", source);
        Assert.Contains("--ocr-workers <0-4>", source);
    }

    [Fact]
    public void CliPerRootOptions_ApplyHddLimiterToExplicitAndAllDriveSearches()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        Assert.Contains("bool isHardDisk = Yagu.Helpers.DiskTypeDetector.IsHardDisk(root);", source);
        Assert.Contains("if (s.LimitParallelismOnHdd && isHardDisk)", source);
        Assert.Contains("BuildSearchOptions(args, s, root, parallelism, isHardDisk: isHardDisk)", source);
        Assert.Contains("BuildSearchOptions(args, s, root, p, backendOverride, isHardDisk)", source);
    }

    [Fact]
    public void SemanticFirstRun_OffersModelPickWithTraditionalFallback()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // First-run runs the on-device model check under the SAME condition as the GUI
        // (SemanticModelQualificationCoordinator.ShouldOffer), and is skipped for an explicit
        // --semantic-model. Declining falls back to a literal Traditional search.
        AssertContainsInOrder(source,
            "if (!explicitModel && SemanticModelQualificationCoordinator.ShouldOffer(settings, translator.IsAvailable))",
            "RunModelQualificationCliAsync(translator, args, settings, CancellationToken.None)",
            "case SemanticModelSetup.Declined:",
            "return FallBackToTraditional(args);");

        // Declining drops to a literal Traditional search of the typed text.
        Assert.Contains("args.FallBackSemanticToTraditional();", source);
        Assert.Contains("Using Traditional search for:", source);
    }

    [Fact]
    public void SemanticSingleToken_BypassesModelAndRunsTraditionalSearch()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "string semanticText = args.SemanticPattern?.Trim() ?? string.Empty;",
            "if (SemanticQuerySalvage.IsSingleTokenQuery(semanticText))",
            "return FallBackToTraditional(args);",
            "new FoundryLocalSemanticQueryTranslator");
    }

    [Fact]
    public void SemanticFirstRun_RunsTextBasedModelProbe_MatchingTheGui()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // The CLI runs the SAME qualification engine the GUI uses (SemanticProbeSet.Default through
        // SemanticModelQualificationRunner), keeps the model resident across probes, streams a text
        // transcript to stderr, prints the report, and adopts the coordinator's suggestion.
        AssertContainsInOrder(source,
            "private static async Task<SemanticModelSetup> RunModelQualificationCliAsync(",
            "translator.SetUnloadAfterUse(false);",
            "var runner = new SemanticModelQualificationRunner(",
            "SemanticModelQualificationRunner.DefaultMaxCandidates",
            "runner.RunAsync(SemanticProbeSet.Default, ModelQualificationThresholds.Default, qualProgress, ct)",
            "PrintQualificationReport(result);",
            "SemanticModelQualificationCoordinator.Suggestion(result)");

        // Nothing usable -> mirror the GUI's switch-to-Traditional (disable AI search + mark complete).
        AssertContainsInOrder(source,
            "SemanticModelQualificationCoordinator.MarkDeclined(settings);",
            "settings.SemanticSearchEnabled = false;",
            "PersistQualificationState(settings, disableSemantic: true);");

        // The per-probe transcript labels PASS / SLOW / FAIL with latency.
        Assert.Contains("string status = p.ProbePassed ? (p.ProbeSlowWarning ? \"SLOW\" : \"PASS\") : \"FAIL\";", source);
    }

    [Fact]
    public void FallBackSemanticToTraditional_ReusesSemanticTextAsLiteralPattern()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "internal void FallBackSemanticToTraditional()",
            "if (string.IsNullOrWhiteSpace(Pattern) && !string.IsNullOrWhiteSpace(SemanticPattern))",
            "Pattern = SemanticPattern;",
            "// An empty Directory is intentionally preserved: it means \"search all drives\".");
    }

    [Fact]
    public void SemanticModelSetup_NonInteractiveConsoleFallsBackWithoutAcceptFlag()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        AssertContainsInOrder(source,
            "bool interactive = !Console.IsInputRedirected;",
            "if (!interactive && !args.AcceptModelDownload)",
            "return SemanticModelSetup.Declined;");
    }

    [Fact]
    public void SemanticModelSetup_PersistsChoiceToGlobalSettings()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "CliRunner.cs"));

        // Applying the check mirrors the GUI (SemanticModelQualificationCoordinator.ApplyResult) and sets
        // the downloaded flag so the legacy first-run gate stays consistent; both surfaces persist to the
        // same global settings store.
        AssertContainsInOrder(source,
            "SemanticModelQualificationCoordinator.ApplyResult(settings, result, accepted: true, chosenAlias);",
            "settings.SemanticModelDownloaded = true;",
            "PersistQualificationState(settings, disableSemantic: false);");
        AssertContainsInOrder(source,
            "private static void PersistQualificationState(AppSettings applied, bool disableSemantic)",
            "global.SemanticModelQualificationCompleted = applied.SemanticModelQualificationCompleted;",
            "global.SemanticModelAlias = applied.SemanticModelAlias;",
            "global.SemanticModelDownloaded = applied.SemanticModelDownloaded;",
            "service.Save(global);");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int position = 0;
        foreach (var item in expected)
        {
            int found = text.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }
}