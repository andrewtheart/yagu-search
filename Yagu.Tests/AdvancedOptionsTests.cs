namespace Yagu.Tests;

/// <summary>
/// Source-scraping tests for MainWindow.AdvancedOptions.cs and MainWindow.xaml —
/// tab switching, reset-to-defaults, and Filters-tab dropdown alignment.
/// </summary>
public sealed class AdvancedOptionsTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string AdvancedOptionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptions.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

    // ══════════════════════════════════════════════════════════════════
    // Tab switching logic
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void AdvancedOptionsFile_Exists()
    {
        string path = Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptions.cs");
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
    public void ResetClick_LoadsSettingsFromService()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2000);
        Assert.Contains("AppSettings settings = new SettingsService().Load();", method);
    }

    [Fact]
    public void ResetClick_ResetsSearchMode()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2000);
        Assert.Contains("ViewModel.SearchModeIndex = 0;", method);
    }

    [Fact]
    public void ResetClick_ResetsFilterModes()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2000);
        Assert.Contains("ViewModel.IncludeFilterModeIndex = settings.IncludeFilterModeIndex;", method);
        Assert.Contains("ViewModel.ExcludeFilterModeIndex = settings.ExcludeFilterModeIndex;", method);
    }

    [Fact]
    public void ResetClick_ResetsGlobs()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2000);
        Assert.Contains("ViewModel.IncludeGlobs = settings.IncludeGlobs;", method);
        Assert.Contains("ViewModel.ExcludeGlobs = settings.ExcludeGlobs;", method);
    }

    [Fact]
    public void ResetClick_ResetsExtensionSettings()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2000);
        Assert.Contains("ViewModel.SkipExtensions = settings.SkipExtensions;", method);
        Assert.Contains("ViewModel.BinaryExtensions = settings.BinaryExtensions;", method);
        Assert.Contains("ViewModel.ArchiveExtensions = settings.ArchiveExtensions;", method);
    }

    [Fact]
    public void ResetClick_ResetsFileSizeAndDateFilters()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2000);
        Assert.Contains("ViewModel.MinFileSizeBytes = settings.DefaultMinFileSizeBytes;", method);
        Assert.Contains("ViewModel.MaxFileSizeBytes = settings.DefaultMaxFileSizeBytes;", method);
        Assert.Contains("ViewModel.CreatedAfterDate = settings.DefaultCreatedAfterDate;", method);
        Assert.Contains("ViewModel.ModifiedAfterDate = settings.DefaultModifiedAfterDate;", method);
    }

    [Fact]
    public void ResetClick_ResetsMaxSearchDepthToNaN()
    {
        string method = ExtractMethodWindow("OnAdvancedOptionsResetClick", 2500);
        Assert.Contains("ViewModel.MaxSearchDepth = double.NaN;", method);
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
