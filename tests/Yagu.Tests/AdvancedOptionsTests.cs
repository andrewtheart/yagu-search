namespace Yagu.Tests;

/// <summary>
/// Source-scraping tests for MainWindow.AdvancedOptions.cs and MainWindow.xaml —
/// tab switching, reset-to-defaults, and Filters-tab dropdown alignment.
/// </summary>
public sealed class AdvancedOptionsTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string AdvancedOptionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptions.cs"));
    private static readonly string AdvancedOptionPlacementSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptionPlacement.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string MainViewModelSource = MainViewModelPartials.Text;
    private static readonly string SearchInputSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));
    private static readonly string StartupChecksSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));
    private static readonly string MainWindowCodeBehindSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));

    // ══════════════════════════════════════════════════════════════════
    // Tab switching logic
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void AdvancedOptionsFile_Exists()
    {
        string path = Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptions.cs");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TabSelectionChanged_GuardsNullFields()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsTabSelectionChanged", 400);
        // Also guards mid-reorder: moving items raises SelectionChanged with a transient empty
        // selection that must not snap the drawer back to another tab.
        Assert.Contains("if (AdvancedOptionsSearchTabContent is null || _reorderingAdvancedOptionsTabs)", method);
        Assert.Contains("return;", method);
    }

    [Fact]
    public void TabSelectionChanged_ResolvesTabByIdentityNotIndex()
    {
        // The tab column is drag-reorderable, so a tab's list position says nothing about which
        // content pane it owns. Selection must resolve through the item's stable key.
        string method = ExtractMethodWindow("OnAdvancedOptionsTabSelectionChanged", 900);
        AssertContainsInOrder(method,
            "string? tabKey = AdvancedOptionsTabKeyOf(AdvancedOptionsTabList.SelectedItem);",
            "if (tabKey is null)",
            "SelectAdvancedOptionsTab(AdvancedOptionsSearchTabKey);",
            "SetAdvancedOptionsTab(tabKey);");
        Assert.DoesNotContain("SelectedIndex", method);
    }

    [Fact]
    public void SetAdvancedOptionsTab_TogglesVisibilityForSixTabs()
    {
        string method = ExtractMethodWindow("ResolveAdvancedOptionsTabContent", 700);
        Assert.Contains("AdvancedOptionsSearchTabContent", method);
        Assert.Contains("AdvancedOptionsQuickSearchesTabContent", method);
        Assert.Contains("AdvancedOptionsFiltersTabContent", method);
        Assert.Contains("AdvancedOptionsSizeTabContent", method);
        Assert.Contains("AdvancedOptionsDatesTabContent", method);
        Assert.Contains("AdvancedOptionsAdvancedTabContent", method);
    }

    [Fact]
    public void SetAdvancedOptionsTab_CallsUpdateDrawerMaxHeight()
    {
        string method = ExtractMethodWindow("private void SetAdvancedOptionsTab", 1200);
        Assert.Contains("UpdateAdvancedOptionsDrawerMaxHeight();", method);
    }

    [Fact]
    public void SetAdvancedOptionsTabVisibility_IsStaticHelper()
    {
        Assert.Contains("private static void SetAdvancedOptionsTabVisibility(FrameworkElement tabContent, bool isVisible)", AdvancedOptionsSource);
        Assert.Contains("tabContent.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed", AdvancedOptionsSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // Drag-reorderable tab column
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void TabColumn_IsBoundAsItemsSourceSoWinUiCanReorderIt()
    {
        // WinUI's built-in reorder rewrites the bound collection and silently does nothing for
        // inline ListViewItem containers, so the tabs MUST stay data-bound.
        string list = ExtractXamlElement("x:Name=\"AdvancedOptionsTabList\"", 2600);
        Assert.Contains("ItemsSource=\"{x:Bind AdvancedOptionsTabs}\"", list);
        Assert.Contains("CanDragItems=\"True\"", list);
        Assert.Contains("CanReorderItems=\"True\"", list);
        Assert.Contains("AllowDrop=\"True\"", list);
        Assert.Contains("DragItemsCompleted=\"OnAdvancedOptionsTabsReordered\"", list);
        Assert.Contains("x:DataType=\"models:AdvancedOptionsTabItem\"", list);

        // A hard-coded SelectedIndex would fight the restored order; selection is set in code.
        Assert.DoesNotContain("SelectedIndex=", list);
        Assert.DoesNotContain("<ListViewItem", list);
    }

    [Fact]
    public void TabColumn_NavIsWideEnoughForTheLongestLabel()
    {
        // A 132px column clipped "Quick searches" to "Quick search".
        string column = ExtractXamlElement("Wide enough for the longest tab label", 400);
        Assert.Contains("<ColumnDefinition Width=\"160\" />", column);
    }

    [Fact]
    public void TabItems_CarryStableKeysInShippedOrder()
    {
        Assert.Contains("[\"search\", \"quick\", \"filters\", \"size\", \"dates\", \"advanced\"]", AdvancedOptionsSource);
        foreach (string key in new[] { "search", "quick", "filters", "size", "dates", "advanced" })
            Assert.Contains($"new(\"{key}\",", AdvancedOptionsSource);
    }

    [Fact]
    public void TabReorder_PersistsTheNewOrder()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsTabsReordered", 900);
        // Ignore non-move drops so a cancelled drag never rewrites the setting.
        Assert.Contains("if (args.DropResult != DataPackageOperation.Move)", method);
        AssertContainsInOrder(method,
            "ViewModel.AdvancedOptionsTabOrder = order;",
            "SetAdvancedOptionsTab(selectedKey);",
            "await ViewModel.PersistSettingsAsync();");
    }

    [Fact]
    public void TabOrder_IsRestoredLazilyAndNeverDropsATab()
    {
        string method = ExtractMethodWindow("private void ApplySavedAdvancedOptionsTabOrder()", 1800);
        // Applied once, on first drawer open, to keep it off the startup path.
        Assert.Contains("if (_advancedOptionsTabOrderApplied)", method);
        // Unknown keys are ignored and unmentioned tabs keep their shipped position.
        Assert.Contains("if (desired.Count != AdvancedOptionsTabs.Count)", method);
        Assert.Contains("return; // defensive: never drop a tab", method);
        // Reorder must be suppressed from the selection handler.
        Assert.Contains("_reorderingAdvancedOptionsTabs = true;", method);
        Assert.Contains("_reorderingAdvancedOptionsTabs = false;", method);
    }

    [Fact]
    public void TabOrder_IsPersistedAndReloadedThroughSettings()
    {
        string settings = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public List<string> AdvancedOptionsTabOrder { get; set; } = [];", settings);
        Assert.Contains("AdvancedOptionsTabOrder = _settings.AdvancedOptionsTabOrder ?? [];", MainViewModelSource);
        Assert.Contains("_settings.AdvancedOptionsTabOrder = AdvancedOptionsTabOrder;", MainViewModelSource);
    }

    [Fact]
    public void OptionPlacement_UsesDedicatedGripAndPrivatePayload()
    {
        Assert.Contains("CanDrag = true,", AdvancedOptionPlacementSource);
        Assert.Contains("grip.DragStarting += OnAdvancedOptionDragStarting;", AdvancedOptionPlacementSource);
        Assert.Contains("wrapper.Children.Add(content);", AdvancedOptionPlacementSource);
        Assert.Contains("private const string AdvancedOptionDragDataFormat = \"Yagu.AdvancedOption\";", AdvancedOptionPlacementSource);
        Assert.Contains("args.Data.SetData(AdvancedOptionDragDataFormat, id);", AdvancedOptionPlacementSource);
        Assert.Contains("e.DataView.Contains(AdvancedOptionDragDataFormat)", AdvancedOptionPlacementSource);
    }

    [Fact]
    public void OptionPlacement_RequiresSustainedCrossTabHoverAndConfirmation()
    {
        Assert.Contains("private const int AdvancedOptionTabHoverArmMilliseconds = 650;", AdvancedOptionPlacementSource);
        Assert.Contains("e.AcceptedOperation = DataPackageOperation.None;", AdvancedOptionPlacementSource);
        Assert.Contains("string.Equals(_armedAdvancedOptionTargetTabKey, targetTabKey, StringComparison.Ordinal)", AdvancedOptionPlacementSource);

        string drop = ExtractSourceWindow(AdvancedOptionPlacementSource, "private async void OnAdvancedOptionTabDrop", 3200);
        AssertContainsInOrder(drop,
            "var result = await YaguDialog.ShowAsync(",
            "ShowTitleBar = false,",
            "if (result != YaguDialogResult.Primary)",
            "ViewModel.AdvancedOptionPlacements[registration.Id] = targetTabKey;",
            "RebuildAdvancedOptionPlacement();",
            "await ViewModel.PersistSettingsAsync();");
    }

    [Fact]
    public void OptionPlacement_MovesIntactRowsAndFallsBackForUnknownSettings()
    {
        Assert.Contains("parent.Children.RemoveAt(homeIndex);", AdvancedOptionPlacementSource);
        Assert.Contains("targetHost.Children.Insert(AdvancedOptionTargetInsertIndex(targetTabKey, targetHost), registration.Wrapper);", AdvancedOptionPlacementSource);
        Assert.Contains("ShippedAdvancedOptionsTabOrder.Contains(target, StringComparer.Ordinal)", AdvancedOptionPlacementSource);
        Assert.Contains(": registration.HomeTabKey;", AdvancedOptionPlacementSource);
        Assert.Contains("ViewModel.AdvancedOptionPlacements.Remove(registration.Id);", AdvancedOptionPlacementSource);
        Assert.Contains("DragOver=\"OnAdvancedOptionTabDragOver\"", MainWindowXaml);
        Assert.Contains("Drop=\"OnAdvancedOptionTabDrop\"", MainWindowXaml);
    }

    [Fact]
    public void OptionPlacement_IsPersistedAndReloadedThroughSettings()
    {
        string settings = File.ReadAllText(Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
        Assert.Contains("public Dictionary<string, string> AdvancedOptionPlacements { get; set; } = [];", settings);
        Assert.Contains("AdvancedOptionPlacements = new Dictionary<string, string>(", MainViewModelSource);
        Assert.Contains("_settings.AdvancedOptionPlacements ?? [], StringComparer.Ordinal", MainViewModelSource);
        Assert.Contains("_settings.AdvancedOptionPlacements = new Dictionary<string, string>(AdvancedOptionPlacements, StringComparer.Ordinal);", MainViewModelSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // Quick searches tab (developer one-click presets)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void QuickSearchesTab_IsListedSecondInTheNav()
    {
        // The nav is data-driven (drag-reorderable), so the shipped order lives in the
        // AdvancedOptionsTabs collection initializer rather than in XAML.
        int search = AdvancedOptionsSource.IndexOf("new(\"search\",", StringComparison.Ordinal);
        int quick = AdvancedOptionsSource.IndexOf("new(\"quick\",", StringComparison.Ordinal);
        int filters = AdvancedOptionsSource.IndexOf("new(\"filters\",", StringComparison.Ordinal);
        Assert.True(search >= 0 && quick > search && filters > quick,
            "The Quick searches nav item must sit between Search and Filters.");
        Assert.Contains("\"Quick searches\"", AdvancedOptionsSource);
    }

    [Fact]
    public void QuickSearchesTab_HostsCodeAnnotationButtonAndTheUserManagedList()
    {
        // Slice exactly the Quick searches panel (from its anchor to the next tab's anchor).
        int panelStart = MainWindowXaml.IndexOf("x:Name=\"AdvancedOptionsQuickSearchesTabContent\"", StringComparison.Ordinal);
        int panelEnd = MainWindowXaml.IndexOf("x:Name=\"AdvancedOptionsFiltersTabContent\"", StringComparison.Ordinal);
        Assert.True(panelStart >= 0 && panelEnd > panelStart, "Quick searches panel must precede the Filters panel.");
        string panel = MainWindowXaml[panelStart..panelEnd];

        // The canonical code-annotation button stays a fixed action (it is the GUI twin of CLI --todos).
        Assert.Contains("x:Name=\"FindCodeAnnotationsButton\"", panel);
        Assert.Contains("Click=\"OnFindCodeAnnotations\"", panel);
        Assert.Contains("x:Name=\"QuickSearchesPanel\" Width=\"500\" HorizontalAlignment=\"Left\"", panel);

        // The remaining presets became a user-managed list: rows are built in code so they can be
        // added, inline-edited, reordered and deleted, so they are no longer static XAML buttons.
        Assert.Contains("x:Name=\"UserQuickSearchesScrollViewer\"", panel);
        Assert.Contains("Width=\"500\" MaxHeight=\"196\"", panel);
        Assert.Contains("VerticalScrollMode=\"Enabled\"", panel);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", panel);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", panel);
        Assert.Contains("x:Name=\"UserQuickSearchesPanel\" Spacing=\"4\"", panel);
        Assert.Contains("x:Name=\"AddQuickSearchButton\"", panel);
        Assert.Contains("Click=\"OnAddQuickSearch\"", panel);
        // "Save current options" turns the live drawer into a quick search that restores all of it later.
        Assert.Contains("x:Name=\"SaveCurrentOptionsAsQuickSearchButton\"", panel);
        Assert.Contains("Click=\"OnSaveCurrentOptionsAsQuickSearch\"", panel);
        foreach (string key in new[] { "merge-conflicts", "debug-output", "secrets", "urls", "emails", "empty-catch", "deprecated", "guids" })
            Assert.DoesNotContain($"Tag=\"{key}\" Click=\"OnQuickSearch\"", panel);

        // Those keys still ship as the seeded defaults, so nothing was dropped from the catalog.
        var defaults = Yagu.Helpers.QuickSearchCatalog.Defaults().Select(i => i.Id).ToList();
        foreach (string key in new[] { "merge-conflicts", "debug-output", "secrets", "urls", "emails", "empty-catch", "deprecated", "guids" })
            Assert.Contains(key, defaults);
    }

    [Fact]
    public void QuickSearchesTab_MovedTheCodeAnnotationButtonOutOfTheSearchTab()
    {
        int quickPanel = MainWindowXaml.IndexOf("AdvancedOptionsQuickSearchesTabContent", StringComparison.Ordinal);
        int filtersPanel = MainWindowXaml.IndexOf("AdvancedOptionsFiltersTabContent", StringComparison.Ordinal);
        int button = MainWindowXaml.IndexOf("FindCodeAnnotationsButton", StringComparison.Ordinal);
        Assert.True(quickPanel >= 0 && quickPanel < button && button < filtersPanel,
            "The code-annotation button must live inside the Quick searches tab, not the Search tab.");
    }

    [Fact]
    public void QuickSearch_HandlerLooksUpPresetByTagThenSearches()
    {
        string handler = ExtractFrom(SearchInputSource, "private async void OnQuickSearch", 700);
        AssertContainsInOrder(handler,
            "is string key",
            "Yagu.Helpers.QuickSearchPresets.Find(key) is { } preset",
            "ViewModel.ApplyQuickSearchPreset(preset);",
            "await StartSearchFromUiAsync();");

        string vm = ExtractViewModelMethod("public void ApplyQuickSearchPreset", 400);
        AssertContainsInOrder(vm,
            "IsSemanticQueryMode = false;",
            "UseRegex = true;",
            "CaseSensitive = preset.CaseSensitive;",
            "Query = preset.Pattern;");
    }

    // ══════════════════════════════════════════════════════════════════
    // Reset to defaults
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ResetClick_DelegatesToViewModel()
    {
        // The reset button reuses the single ResetAdvancedOptionsToSavedDefaults implementation in the
        // view-model (shared with the post-search auto-reset) rather than duplicating reset logic.
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 600);
        Assert.Contains("ViewModel.ResetAdvancedOptionsToSavedDefaults();", method);
    }

    [Fact]
    public void ResetDefaults_LoadsSettingsFromService()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("AppSettings settings = _settingsService.Load();", method);
    }

    [Fact]
    public void ResetDefaults_ResetsSearchMode()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("SearchModeIndex = 0;", method);
    }

    [Fact]
    public void ResetDefaults_ResetsFilterModes()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("IncludeFilterModeIndex = settings.IncludeFilterModeIndex;", method);
        Assert.Contains("ExcludeFilterModeIndex = settings.ExcludeFilterModeIndex;", method);
    }

    [Fact]
    public void ResetDefaults_ResetsGlobs()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("IncludeGlobs = settings.IncludeGlobs;", method);
        // The default exclude globs are stripped to empty so the box shows the greyed placeholder
        // instead of the literal default as real, search-affecting text.
        Assert.Contains("ExcludeGlobs = IsDefaultExcludeGlobs(settings.ExcludeGlobs) ? string.Empty : settings.ExcludeGlobs;", method);
    }

    [Fact]
    public void ResetDefaults_ResetsExtensionSettings()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("SkipExtensions = settings.SkipExtensions;", method);
        Assert.Contains("BinaryExtensions = settings.BinaryExtensions;", method);
        Assert.Contains("ArchiveExtensions = settings.ArchiveExtensions;", method);
    }

    [Fact]
    public void ResetDefaults_ResetsFileSizeAndDateFilters()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("MinFileSizeBytes = settings.DefaultMinFileSizeBytes;", method);
        Assert.Contains("MaxFileSizeBytes = settings.DefaultMaxFileSizeBytes;", method);
        Assert.Contains("CreatedAfterDate = settings.DefaultCreatedAfterDate;", method);
        Assert.Contains("ModifiedAfterDate = settings.DefaultModifiedAfterDate;", method);
    }

    [Fact]
    public void ResetDefaults_ResetsMaxSearchDepthToNaN()
    {
        string method = ExtractViewModelMethod("public void ResetAdvancedOptionsToSavedDefaults()");
        Assert.Contains("MaxSearchDepth = double.NaN;", method);
    }

    [Fact]
    public void SearchBinaryToggle_SelectsAllBinaryTypesWhenTurnedOn()
    {
        // Turning "Search binary" ON must select ALL binary types (the dropdown shows N/N, not 0/N).
        // BinaryExtensions is internally the SKIP list, so "search all" == an empty skip list; OFF
        // restores the full skip list. The change is guarded so it does not run during construction.
        string method = ExtractViewModelMethod("partial void OnSkipBinaryChanged(bool value)");
        Assert.Contains("if (!_binaryExtensionsInitialized) return;", method);
        Assert.Contains("BinaryExtensions = value ? SettingsBinaryExtensions : string.Empty;", method);
        Assert.Contains("SyncBinaryExtensionItems();", method);
    }

    // ══════════════════════════════════════════════════════════════════
    // Save as Defaults
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SaveAsDefaultsButton_ExistsAndIsWiredInTheActionBar()
    {
        Assert.Contains("Content=\"Save as Defaults\"", MainWindowXaml);
        Assert.Contains("Click=\"OnAdvancedOptionsSaveDefaultsClick\"", MainWindowXaml);
    }

    [Fact]
    public void SaveDefaultsClick_ShowsTitlelessConfirmThenDelegatesToViewModelOnlyOnConfirm()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsSaveDefaultsClick", 2400);
        // Summary of exactly what will be saved.
        Assert.Contains("ViewModel.DescribeAdvancedOptionDefaults()", method);
        // Title-bar-less confirm/cancel modal (per the modal-no-title-bar rule).
        Assert.Contains("ShowTitleBar = false", method);
        Assert.Contains("PrimaryButtonText = \"Save as defaults\"", method);
        Assert.Contains("CloseButtonText = \"Cancel\"", method);
        // Persists ONLY when the user confirms.
        AssertContainsInOrder(method,
            "if (result != YaguDialogResult.Primary)",
            "return;",
            "await ViewModel.SaveAdvancedOptionsAsDefaultsAsync();");
    }

    [Fact]
    public void SaveAdvancedOptionsAsDefaults_ClearsTransientGuardsPromotesMirrorsAndPersists()
    {
        string method = ExtractViewModelMethod("public async Task SaveAdvancedOptionsAsDefaultsAsync()");
        // The visible values become the real defaults: drop the snapshot/transient guards so the
        // persisted value is what's shown and a later Reset won't undo it.
        Assert.Contains("_semanticResolutionVisible = false;", method);
        Assert.Contains("_advancedOptionsTransientlyChanged = false;", method);
        // Promote the active filter values into the persisted-default mirrors Reset/launch read from.
        Assert.Contains("SettingsSkipExtensions = SkipExtensions;", method);
        // Binary is a SKIP list (empty when searching all types), so Save-as-Defaults preserves the
        // universe of known binary types rather than overwrite it with the inverted active list.
        Assert.Contains("SettingsBinaryExtensions = string.Join(';', ParseExtensionSet(SettingsBinaryExtensions)", method);
        Assert.Contains("SettingsArchiveExtensions = ArchiveExtensions;", method);
        Assert.Contains("DefaultMinFileSizeBytes = MinFileSizeBytes;", method);
        Assert.Contains("DefaultModifiedBeforeDate = ModifiedBeforeDate;", method);
        // Writes straight to disk via the canonical persist path.
        Assert.Contains("await PersistSettingsAsync()", method);
    }

    [Fact]
    public void DescribeAdvancedOptionDefaults_SummarizesKeyOptions()
    {
        string method = ExtractViewModelMethod("internal IReadOnlyList<string> DescribeAdvancedOptionDefaults()");
        Assert.Contains("Match case:", method);
        Assert.Contains("Respect .gitignore:", method);
        Assert.Contains("Search hidden files:", method);
        Assert.Contains("Search image text (OCR):", method);
        Assert.Contains("Include filter:", method);
        Assert.Contains("Exclude filter:", method);
    }

    [Fact]
    public void DescribeAdvancedOptionDefaults_SizeDateAndByteHelpers_CoverEveryRangeShapeAndUnit()
    {
        // The size/date/byte formatting helpers back the confirmation summary lines. They live in the
        // WinUI-coupled MainViewModel (not unit-instantiable), so pin each helper's branch structure so
        // every range shape (two-sided, min-only, max-only, none) and byte unit (GB/MB/KB/bytes) stays.
        string size = ExtractViewModelMethod("private static string DescribeSizeRange(long minBytes, long maxBytes)", 600);
        AssertContainsInOrder(size,
            "if (hasMin && hasMax) return $\"between {FormatBytes(minBytes)} and {FormatBytes(maxBytes)}\";",
            "if (hasMin) return $\"at least {FormatBytes(minBytes)}\";",
            "if (hasMax) return $\"at most {FormatBytes(maxBytes)}\";",
            "return string.Empty;");

        string date = ExtractViewModelMethod("private static string DescribeDateRange(DateTimeOffset? after, DateTimeOffset? before)", 700);
        AssertContainsInOrder(date,
            "if (after.HasValue && before.HasValue) return $\"between {D(after.Value)} and {D(before.Value)}\";",
            "if (after.HasValue) return $\"after {D(after.Value)}\";",
            "if (before.HasValue) return $\"before {D(before.Value)}\";",
            "return string.Empty;");

        string bytes = ExtractViewModelMethod("private static string FormatBytes(long bytes)", 500);
        AssertContainsInOrder(bytes,
            "if (bytes >= gb) return $\"{bytes / (double)gb:0.##} GB\";",
            "if (bytes >= mb) return $\"{bytes / (double)mb:0.##} MB\";",
            "if (bytes >= kb) return $\"{bytes / (double)kb:0.##} KB\";",
            "return $\"{bytes} bytes\";");
    }

    // ── Image-text (OCR) option mapping ──
    // The OCR Advanced Option flows view-model ⇄ settings ⇄ SearchOptions. These three pins lock that
    // bridge (load, persist, and build) since MainViewModel is WinUI-coupled and not unit-instantiable.

    [Fact]
    public void Ctor_LoadsImageTextOptionsFromSettings()
    {
        Assert.Contains("SearchImageText = _settings.SearchImageText;", MainViewModelSource);
        Assert.Contains("ImageOcrEngine = _settings.ImageOcrEngine;", MainViewModelSource);
    }

    [Fact]
    public void PdfText_FlowsViewModelSettingsAndSearchOptions()
    {
        // Load from settings, build into SearchOptions, and persist back — the same bridge as image OCR.
        Assert.Contains("SearchPdfText = _settings.SearchPdfText;", MainViewModelSource);
        AssertContainsInOrder(MainViewModelSource,
            "SearchPdfText = SearchPdfText,",
            "PdfTextExtensions = ParseExtensionSet(AppSettings.DefaultPdfTextExtensions),");
        Assert.Contains("_settings.SearchPdfText = SearchPdfText;", MainViewModelSource);
    }

    [Fact]
    public void BuildSearchOptions_MapsImageTextEngineAndExtensions()
    {
        AssertContainsInOrder(MainViewModelSource,
            "SearchImageText = SearchImageText,",
            "ImageOcrExtensions = ParseExtensionSet(AppSettings.DefaultImageOcrExtensions),",
            "ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),");
    }

    [Fact]
    public void SaveSettings_PersistsImageTextAndNormalizesEngine()
    {
        AssertContainsInOrder(MainViewModelSource,
            "_settings.SearchImageText = d is null ? SearchImageText : d.SearchImageText;",
            "_settings.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine);");
    }

    // ── OCR quality (model + detection resolution) option mapping ──
    // The OCR tab's Recognition model and Detection resolution flow view-model ⇄ settings ⇄
    // SearchOptions exactly like the engine selection. These pins lock that bridge.

    [Fact]
    public void Ctor_LoadsImageOcrQualityFromSettings()
    {
        Assert.Contains("ImageOcrModel = _settings.ImageOcrModel;", MainViewModelSource);
        Assert.Contains("ImageOcrMaxSide = _settings.ImageOcrMaxSide;", MainViewModelSource);
        Assert.Contains("ImageOcrWorkerParallelism = _settings.ImageOcrWorkerParallelism;", MainViewModelSource);
    }

    [Fact]
    public void BuildSearchOptions_MapsImageOcrQuality()
    {
        AssertContainsInOrder(MainViewModelSource,
            "ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),",
            "ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel),",
            "ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide),",
            "ImageOcrWorkerParallelism = OcrWorkerParallelism.Resolve(");
        Assert.Contains("LimitParallelismOnHdd,\r\n                    isHardDisk)", MainViewModelSource);
        Assert.Contains("bool isHardDisk = Yagu.Helpers.DiskTypeDetector.IsHardDisk(root);", MainViewModelSource);
    }

    [Fact]
    public void SaveSettings_PersistsImageOcrQuality()
    {
        Assert.Contains("_settings.ImageOcrModel", MainViewModelSource);
        Assert.Contains("_settings.ImageOcrMaxSide", MainViewModelSource);
        Assert.Contains("_settings.ImageOcrWorkerParallelism = AppSettings.NormalizeImageOcrWorkerParallelism(ImageOcrWorkerParallelism);", MainViewModelSource);
    }

    // ── Startup directory pin ──
    // The pin star persists the current directory and auto-selects it on next launch. These pins
    // lock the view-model bridge (startup resolution, load, persist, and the pin toggle method).

    [Fact]
    public void Ctor_ResolvesStartupDirectory()
    {
        Assert.Contains("Directory = ResolveStartupDirectory();", MainViewModelSource);
    }

    [Fact]
    public void ResolveStartupDirectory_HonorsPinnedDirectory()
    {
        string method = ExtractViewModelMethod("private string ResolveStartupDirectory", 600);
        Assert.Contains("PinStartupDirectory", method);
        Assert.Contains("PinnedStartupDirectory", method);
    }

    [Fact]
    public void Ctor_LoadsPinStartupDirectoryFromSettings()
    {
        Assert.Contains("PinStartupDirectory = _settings.PinStartupDirectory;", MainViewModelSource);
    }

    [Fact]
    public void SetStartupDirectoryPinned_SnapshotsCurrentDirectory()
    {
        string method = ExtractViewModelMethod("public async Task SetStartupDirectoryPinnedAsync", 900);
        Assert.Contains("_settings.PinStartupDirectory = pinned;", method);
        Assert.Contains("_settings.PinnedStartupDirectory", method);
    }

    // ── Star highlight tracks the CURRENTLY shown directory, not just "a pin exists" ──
    // The star is only highlighted while the box shows the saved directory; switching to a different
    // folder clears the highlight even though the pin stays saved. These pins lock the derived
    // IsCurrentDirectoryPinned bridge across the view-model, XAML binding, and code-behind glue.

    [Fact]
    public void IsCurrentDirectoryPinned_ComparesBoxToPinnedSnapshot()
    {
        // The highlight is true ONLY when the pin is on AND the box currently equals the saved
        // pinned directory (case-insensitive, trailing-separator-insensitive). Without all three
        // conditions, switching the box away from the pinned folder would leave the star lit.
        string property = ExtractViewModelMethod("public bool IsCurrentDirectoryPinned", 600);
        AssertContainsInOrder(property,
            "PinStartupDirectory",
            "!string.IsNullOrWhiteSpace(_settings.PinnedStartupDirectory)",
            "string.Equals(",
            "(Directory ?? string.Empty).Trim().TrimEnd('\\\\', '/')",
            "_settings.PinnedStartupDirectory!.Trim().TrimEnd('\\\\', '/')",
            "StringComparison.OrdinalIgnoreCase);");
    }

    [Fact]
    public void DirectoryAndPin_NotifyIsCurrentDirectoryPinned()
    {
        // The highlight must re-evaluate whenever the box directory OR the pin flag changes, so both
        // observable properties are decorated with [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))].
        AssertNotifiesHighlight("public partial string Directory { get; set; }");
        AssertNotifiesHighlight("public partial bool PinStartupDirectory { get; set; }");
    }

    [Fact]
    public void SetStartupDirectoryPinned_RaisesHighlightForRepinToDifferentFolder()
    {
        // Re-pinning to a different folder leaves PinStartupDirectory true, so NotifyPropertyChangedFor
        // won't fire; the snapshot lives on _settings (not observable). The method must nudge the
        // derived highlight explicitly so the star reflects the new snapshot immediately.
        string method = ExtractViewModelMethod("public async Task SetStartupDirectoryPinnedAsync", 900);
        Assert.Contains("OnPropertyChanged(nameof(IsCurrentDirectoryPinned));", method);
    }

    [Fact]
    public void PinStar_DrivesCheckedStateFromCodeBehindNotOneWayBind()
    {
        // REGRESSION: the star toggle's IsChecked must NOT be a OneWay x:Bind. The framework permanently
        // disables a OneWay x:Bind to a user-toggleable control the first time the user clicks it (a
        // OneWay binding can't write back, so it stops fighting user input). Once disabled the star froze
        // on its last value and never un-highlighted when the box moved off the pinned folder. The checked
        // state is instead driven from code-behind (UpdatePinStartupDirectoryIcon).
        string toggle = ExtractXamlElement("x:Name=\"PinStartupDirectoryButton\"", 600);
        Assert.DoesNotContain("IsChecked=\"{x:Bind", toggle);

        // The highlight is set in code-behind, keyed off the derived IsCurrentDirectoryPinned value.
        string updater = ExtractFrom(SearchInputSource, "private void UpdatePinStartupDirectoryIcon", 700);
        Assert.Contains("PinStartupDirectoryButton.IsChecked = pinned;", updater);
    }

    [Fact]
    public void PinStarHandlers_DriveCheckedAndGlyphFromDerivedHighlight()
    {
        // Startup seeds the full star (checked + glyph) from the derived highlight; the click handler
        // re-syncs to it (a raw toggle can differ, e.g. trying to pin an empty box pins nothing); and the
        // PropertyChanged subscription refreshes it whenever the box directory changes. All three route
        // through UpdatePinStartupDirectoryIcon, which now also owns PinStartupDirectoryButton.IsChecked.
        Assert.Contains("UpdatePinStartupDirectoryIcon(ViewModel.IsCurrentDirectoryPinned);", StartupChecksSource);

        string handler = ExtractFrom(SearchInputSource, "private async void OnPinStartupDirectory", 1300);
        AssertContainsInOrder(handler,
            "await ViewModel.SetStartupDirectoryPinnedAsync(pinned);",
            "UpdatePinStartupDirectoryIcon(ViewModel.IsCurrentDirectoryPinned);");

        string subscription = ExtractFrom(MainWindowCodeBehindSource, "nameof(ViewModel.IsCurrentDirectoryPinned)", 900);
        Assert.Contains("UpdatePinStartupDirectoryIcon(ViewModel.IsCurrentDirectoryPinned);", subscription);
    }

    // ══════════════════════════════════════════════════════════════════
    // Directory-bar content-index toggle (next to the pin star)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void IndexDirectoryButton_SitsBetweenPinStarAndBrowse()
    {
        // The indexing glyph is a ToggleButton immediately to the right of the pin star and left of Browse.
        string overlay = ExtractXamlElement("x:Name=\"PinStartupDirectoryButton\"", 2600);
        AssertContainsInOrder(overlay,
            "x:Name=\"PinStartupDirectoryButton\"",
            "x:Name=\"IndexDirectoryButton\"",
            "Click=\"OnIndexCurrentDirectory\"",
            "x:Name=\"IndexDirectoryIcon\"",
            "x:Name=\"BrowseDirectoryButton\"");

        // It is a ToggleButton (so it can show a "selected" highlight) with a hover tooltip explaining it,
        // and its checked state is NOT a self-disabling OneWay x:Bind (driven from code-behind instead).
        string button = ExtractXamlElement("x:Name=\"IndexDirectoryButton\"", 600);
        Assert.StartsWith("<ToggleButton", button);
        Assert.Contains("ToolTipService.ToolTip=\"Add this directory to the content index\"", button);
        Assert.DoesNotContain("IsChecked=\"{x:Bind", button);
    }

    [Fact]
    public void DirectoryCommandButtons_HaveNoSeparatingBorders()
    {
        foreach (string name in new[]
        {
            "PinStartupDirectoryButton",
            "IndexDirectoryButton",
            "BrowseDirectoryButton",
        })
        {
            string button = ExtractXamlElement($"x:Name=\"{name}\"", 700);
            Assert.Contains("BorderThickness=\"0\"", button);
            Assert.DoesNotContain("BorderThickness=\"1,0,0,0\"", button);
        }
    }

    [Fact]
    public void IndexDirectoryButton_HighlightDrivenFromDerivedIndexedState()
    {
        // The "selected" highlight tracks the derived IsCurrentDirectoryIndexed value (recomputed whenever
        // the box directory changes), driven from code-behind for the same reason as the pin star.
        string vmProp = ExtractViewModelMethod("public bool IsCurrentDirectoryIndexed", 400);
        Assert.Contains("CurrentDirectoryIndexRoot is not null", vmProp);
        Assert.Contains("IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, Directory!)", MainViewModelSource);

        // Directory changes recompute both derived highlights.
        Assert.Contains("[NotifyPropertyChangedFor(nameof(IsCurrentDirectoryIndexed))]", MainViewModelSource);

        // Startup seeds the toggle; the PropertyChanged subscription refreshes it on directory change.
        Assert.Contains("UpdateIndexDirectoryIcon(ViewModel.IsCurrentDirectoryIndexed);", StartupChecksSource);
        string subscription = ExtractFrom(MainWindowCodeBehindSource, "nameof(ViewModel.IsCurrentDirectoryIndexed)", 900);
        Assert.Contains("UpdateIndexDirectoryIcon(ViewModel.IsCurrentDirectoryIndexed);", subscription);

        string updater = ExtractFrom(SearchInputSource, "private void UpdateIndexDirectoryIcon", 700);
        Assert.Contains("IndexDirectoryButton.IsChecked = indexed;", updater);
    }

    [Fact]
    public void DirectoryToggleButtons_SelectedTintsGlyphNotChrome()
    {
        // The pin/index toggles' selected (checked) state must NOT paint a blue fill/border; instead the
        // glyph itself is tinted with the accent colour. Achieved by overriding the checked-state
        // ToggleButton theme brushes scoped to the directory-overlay command group.
        Assert.Contains("<SolidColorBrush x:Key=\"ToggleButtonForegroundChecked\" Color=\"{ThemeResource SystemAccentColor}\" />", MainWindowXaml);
        Assert.Contains("<SolidColorBrush x:Key=\"ToggleButtonBackgroundChecked\" Color=\"Transparent\" />", MainWindowXaml);
        Assert.Contains("<SolidColorBrush x:Key=\"ToggleButtonBorderBrushChecked\" Color=\"{ThemeResource CardStrokeColorDefault}\" />", MainWindowXaml);
    }

    [Fact]
    public void AutocompleteDropdowns_HaveConfigurableVisibleItemCount()
    {
        // Both the directory and search-pattern AutoSuggestBoxes cap their suggestion list to a
        // ViewModel-computed height, so a configurable number of items shows before scrolling (default 5).
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            MainWindowXaml, "MaxSuggestionListHeight=\"\\{x:Bind ViewModel.AutocompleteDropdownMaxHeight").Count);

        // The height derives from a configurable visible-item count (default 5), distinct from the
        // "max ... to remember" history caps.
        Assert.Contains("public partial int AutocompleteDropdownVisibleItems { get; set; } = 5;", MainViewModelSource);
        Assert.Contains("public double AutocompleteDropdownMaxHeight =>", MainViewModelSource);
    }

    [Fact]
    public void IndexDirectoryButtonHandler_AddsOrRemovesTheCurrentDirectory()
    {
        // Clicking toggles membership: add (with a large-folder confirmation) when it becomes checked,
        // remove when it becomes unchecked, then re-sync the highlight to the derived indexed state.
        string handler = ExtractFrom(SearchInputSource, "private async void OnIndexCurrentDirectory", 3000);
        AssertContainsInOrder(handler,
            "bool wantIndexed = (sender as ToggleButton)?.IsChecked == true;",
            "if (!await ConfirmLargeFolderIfNeededAsync(folder))",
            "await ViewModel.AddFolderToIndexAndBuildAsync(folder);",
            "await ViewModel.RemoveFolderFromIndexAsync(folder);",
            "UpdateIndexDirectoryIcon(ViewModel.IsCurrentDirectoryIndexed);");
        Assert.Contains("if (!IndexedRootsPolicy.Contains(ViewModel.Settings.IndexedRoots, folder))", handler);
        Assert.Contains("is covered by the broader index root", handler);

        // The VM add/remove paths notify the derived indexed state so the toggle updates immediately.
        // Registration was extracted into RegisterFolderForIndexAsync (shared with the blocking
        // pre-search path); it now owns the settings opt-in, persist, and IsCurrentDirectoryIndexed
        // notification, while AddFolderToIndexAndBuildAsync delegates to it and then kicks off the
        // background build. Behavior is unchanged (the add path still fires the notification via the
        // helper) — pin both halves so the delegation can't silently drop it.
        string add = ExtractViewModelMethod("public async Task AddFolderToIndexAndBuildAsync", 1500);
        Assert.Contains("await RegisterFolderForIndexAsync(folder)", add);
        Assert.Contains("StartBackgroundIndexBuild(effectiveRoot);", add);
        string register = ExtractViewModelMethod("private async Task<string?> RegisterFolderForIndexAsync", 1500);
        Assert.Contains("OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));", register);
        string remove = ExtractViewModelMethod("public async Task RemoveFolderFromIndexAsync", 900);
        AssertContainsInOrder(remove,
            "IndexedRootsPolicy.Remove(_settings.IndexedRoots, root)",
            "OnPropertyChanged(nameof(IsCurrentDirectoryIndexed));");
    }

    // ══════════════════════════════════════════════════════════════════
    // Alt access keys on the primary directory-bar / results-toolbar commands
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void PrimaryCommands_HaveAltAccessKeys()
    {
        // Pressing Alt reveals a key tip on each primary command so it can be activated by keyboard.
        Assert.Contains("AccessKey=\"P\"", ExtractXamlElement("x:Name=\"PinStartupDirectoryButton\"", 300));
        Assert.Contains("AccessKey=\"I\"", ExtractXamlElement("x:Name=\"IndexDirectoryButton\"", 300));
        Assert.Contains("AccessKey=\"B\"", ExtractXamlElement("x:Name=\"BrowseDirectoryButton\"", 300));
        Assert.Contains("AccessKey=\"A\"", ExtractXamlElement("x:Name=\"AdvancedOptionsToggle\"", 200));

        // The three results-toolbar dropdowns (Sort/Group/Filter) each carry a unique access key.
        Assert.Contains("AccessKey=\"S\" ToolTipService.ToolTip=\"Sort results\"", MainWindowXaml);
        Assert.Contains("AccessKey=\"G\" ToolTipService.ToolTip=\"Group results by…\"", MainWindowXaml);
        Assert.Contains("x:Name=\"ResultsFilterButton\" AccessKey=\"F\"", MainWindowXaml);

        // The documented Alt+C/R/M/E option-toggle shortcuts are wired via AccessKey.
        Assert.Contains("AccessKey=\"C\"", ExtractXamlElement("x:Name=\"CaseSensitiveToggle\"", 200));
        Assert.Contains("AccessKey=\"R\"", ExtractXamlElement("x:Name=\"RegexToggle\"", 200));
        Assert.Contains("AccessKey=\"M\"", ExtractXamlElement("x:Name=\"MultilineToggle\"", 200));
        Assert.Contains("AccessKey=\"E\"", ExtractXamlElement("x:Name=\"ExactMatchToggle\"", 200));
    }

    [Fact]
    public void ResetClick_UpdatesPlaceholderText()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2500);
        Assert.Contains("IncludeFilterBox.PlaceholderText = ViewModel.IncludeFilterPlaceholder;", method);
        Assert.Contains("ExcludeFilterBox.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;", method);
    }

    // ══════════════════════════════════════════════════════════════════
    // Filters-tab extension dropdown alignment (vertical, half-width offset)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ContentOptions_AreArrangedInTwoColumns()
    {
        string flyout = ExtractXamlElement("x:Name=\"AdvancedOptionsFlyout\"", 1000);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"740\" />", flyout);
        string scrollViewer = ExtractXamlElement("x:Name=\"AdvancedOptionsScrollViewer\"", 500);
        Assert.Contains("MinWidth=\"740\"", scrollViewer);
        string drawer = ExtractXamlElement("x:Name=\"AdvancedOptionsDrawerBodyBorder\"", 400);
        Assert.Contains("MinWidth=\"740\"", drawer);
        Assert.Contains("<Grid x:Name=\"ContentOptionsGrid\" ColumnSpacing=\"12\" RowSpacing=\"10\">", MainWindowXaml);
        string grid = ExtractXamlElement("x:Name=\"ContentOptionsGrid\"", 600);
        AssertContainsInOrder(grid,
            "<ColumnDefinition Width=\"250\" />",
            "<ColumnDefinition Width=\"*\" MinWidth=\"250\" />");
        Assert.Contains("x:Name=\"BinaryExtRow\" Grid.Row=\"0\" Grid.Column=\"0\"", MainWindowXaml);
        Assert.Contains("x:Name=\"ArchiveExtRow\" Grid.Row=\"0\" Grid.Column=\"1\"", MainWindowXaml);
        Assert.Contains("x:Name=\"CloudFilesRow\" Grid.Row=\"1\" Grid.Column=\"0\"", MainWindowXaml);
        Assert.Contains("x:Name=\"HiddenFilesRow\" Grid.Row=\"1\" Grid.Column=\"1\"", MainWindowXaml);
        Assert.Contains("x:Name=\"ImageTextRow\" Grid.Row=\"2\" Grid.Column=\"0\"", MainWindowXaml);
        Assert.Contains("x:Name=\"PdfTextRow\" Grid.Row=\"2\" Grid.Column=\"1\"", MainWindowXaml);
        Assert.Contains("x:Name=\"UseContentIndexRow\" Grid.Row=\"3\" Grid.Column=\"0\"", MainWindowXaml);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            MainWindowXaml, "<ColumnDefinition Width=\"290\" />").Count);
    }

    [Theory]
    [InlineData("SkipExtRow")]
    [InlineData("BinaryExtRow")]
    [InlineData("ArchiveExtRow")]
    public void ExtensionRow_IsGridWithTwoStarColumns(string rowName)
    {
        string row = ExtractXamlElement($"x:Name=\"{rowName}\"", 700);
        Assert.StartsWith("<Grid", row);
        AssertContainsInOrder(row,
            "<ColumnDefinition Width=\"*\" />",
            "<ColumnDefinition Width=\"*\" />");
    }

    [Theory]
    [InlineData("SkipExtRow")]
    [InlineData("BinaryExtRow")]
    [InlineData("ArchiveExtRow")]
    public void ExtensionRowDropdown_SitsDirectlyBelowToggle(string rowName)
    {
        // Dropdown lives in the second row, first column, i.e. directly below its toggle
        // and left-aligned under it (not offset into the right column).
        string row = ExtractXamlElement($"x:Name=\"{rowName}\"", 3000);
        Assert.Contains("<DropDownButton Grid.Row=\"1\" Grid.Column=\"0\" HorizontalAlignment=\"Left\"", row);
    }

    [Theory]
    [InlineData("SkipExtensionsSummary")]
    [InlineData("BinaryExtensionsSummary")]
    [InlineData("ArchiveExtensionsSummary")]
    public void ExtensionRowFlyout_OpensBelow(string summaryBinding)
    {
        string dropdown = ExtractXamlElement(summaryBinding, 1600);
        Assert.Contains("Placement=\"Bottom\"", dropdown);
        Assert.Contains("ShouldConstrainToRootBounds=\"False\"", dropdown);
    }

    [Fact]
    public void FiltersTab_NoLongerUsesOrientationReflow()
    {
        Assert.DoesNotContain("OnFiltersTabSizeChanged", AdvancedOptionsSource);
        Assert.DoesNotContain("SkipExtRow.Orientation", AdvancedOptionsSource);
        Assert.DoesNotContain("FiltersTabWrapThreshold", AdvancedOptionsSource);
    }

    [Fact]
    public void ApplyClick_CollapsesForSearch()
    {
        Assert.Contains("private void OnAdvancedOptionsApplyClick(object sender, RoutedEventArgs e)", AdvancedOptionsSource);
        Assert.Contains("CollapseAdvancedOptionsForSearch()", AdvancedOptionsSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════

    private string ExtractMethodWindow(string methodName, int windowSize = 600)
    {
        int start = AdvancedOptionsSource.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method '{methodName}' in AdvancedOptions source.");
        int end = Math.Min(start + windowSize, AdvancedOptionsSource.Length);
        return AdvancedOptionsSource[start..end];
    }

    private static string ExtractSourceWindow(string source, string anchor, int windowSize)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{anchor}' in source.");
        return source[start..Math.Min(start + windowSize, source.Length)];
    }

    private static string ExtractViewModelMethod(string anchor, int windowSize = 2400)
    {
        int start = MainViewModelSource.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{anchor}' in MainViewModel.cs.");
        int end = Math.Min(start + windowSize, MainViewModelSource.Length);
        return MainViewModelSource[start..end];
    }

    private static string ExtractFrom(string source, string anchor, int windowSize)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{anchor}' in source.");
        int end = Math.Min(start + windowSize, source.Length);
        return source[start..end];
    }

    private static void AssertNotifiesHighlight(string propertyDeclaration)
    {
        int idx = MainViewModelSource.IndexOf(propertyDeclaration, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Could not find '{propertyDeclaration}' in MainViewModel.cs.");
        // The [NotifyPropertyChangedFor(...)] attribute sits on the lines immediately preceding the
        // property declaration, so scan the short run of text just above it.
        int windowStart = Math.Max(0, idx - 200);
        string preceding = MainViewModelSource[windowStart..idx];
        Assert.Contains("[NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))]", preceding);
    }

    private static string ExtractXamlElement(string anchor, int windowSize)
    {
        int anchorIndex = MainWindowXaml.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(anchorIndex >= 0, $"Could not find '{anchor}' in MainWindow.xaml.");
        int tagStart = MainWindowXaml.LastIndexOf('<', anchorIndex);
        if (tagStart < 0) tagStart = anchorIndex;
        int end = Math.Min(tagStart + windowSize, MainWindowXaml.Length);
        return MainWindowXaml[tagStart..end];
    }

    private static void AssertContainsInOrder(string text, params string[] parts)
    {
        int index = 0;
        foreach (var part in parts)
        {
            int found = text.IndexOf(part, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{part}' after index {index}.");
            index = found + part.Length;
        }
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
