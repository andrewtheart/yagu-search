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
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.cs"));
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
        Assert.Contains("if (AdvancedOptionsSearchTabContent is null)", method);
        Assert.Contains("return;", method);
    }

    [Fact]
    public void TabSelectionChanged_ClampsInvalidIndex()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsTabSelectionChanged", 600);
        AssertContainsInOrder(method,
            "int selectedIndex = AdvancedOptionsTabList.SelectedIndex;",
            "if (selectedIndex < 0 || selectedIndex > 4)",
            "AdvancedOptionsTabList.SelectedIndex = 0;",
            "selectedIndex = 0;");
    }

    [Fact]
    public void SetAdvancedOptionsTab_TogglesVisibilityForFiveTabs()
    {
        string method = ExtractMethodWindow("SetAdvancedOptionsTab", 600);
        Assert.Contains("AdvancedOptionsSearchTabContent", method);
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
    }

    [Fact]
    public void BuildSearchOptions_MapsImageOcrQuality()
    {
        AssertContainsInOrder(MainViewModelSource,
            "ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(ImageOcrEngine),",
            "ImageOcrModel = AppSettings.NormalizeImageOcrModel(ImageOcrModel),",
            "ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(ImageOcrMaxSide),");
    }

    [Fact]
    public void SaveSettings_PersistsImageOcrQuality()
    {
        Assert.Contains("_settings.ImageOcrModel", MainViewModelSource);
        Assert.Contains("_settings.ImageOcrMaxSide", MainViewModelSource);
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
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
