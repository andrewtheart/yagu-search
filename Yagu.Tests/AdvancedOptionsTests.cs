namespace Yagu.Tests;

/// <summary>
/// Source-scraping tests for MainWindow.AdvancedOptions.cs —
/// tab switching, reset-to-defaults, and responsive dropdown reflow.
/// </summary>
public sealed class AdvancedOptionsTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string AdvancedOptionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptions.cs"));

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
    // Responsive dropdown reflow
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void FiltersTabWrapThreshold_IsDefined()
    {
        Assert.Contains("private const double FiltersTabWrapThreshold = 280;", AdvancedOptionsSource);
    }

    [Fact]
    public void FiltersTabSizeChanged_GuardsNullRows()
    {
        string method = ExtractMethodWindow("OnFiltersTabSizeChanged", 500);
        Assert.Contains("if (SkipExtRow is null) return;", method);
    }

    [Fact]
    public void FiltersTabSizeChanged_SetsOrientationBasedOnWidth()
    {
        string method = ExtractMethodWindow("OnFiltersTabSizeChanged", 500);
        AssertContainsInOrder(method,
            "e.NewSize.Width < FiltersTabWrapThreshold",
            "Orientation.Vertical",
            "Orientation.Horizontal");
    }

    [Fact]
    public void FiltersTabSizeChanged_AffectsAllThreeExtensionRows()
    {
        string method = ExtractMethodWindow("OnFiltersTabSizeChanged", 500);
        Assert.Contains("SkipExtRow.Orientation = orientation;", method);
        Assert.Contains("BinaryExtRow.Orientation = orientation;", method);
        Assert.Contains("ArchiveExtRow.Orientation = orientation;", method);
    }

    [Fact]
    public void FiltersTabSizeChanged_AdjustsSpacing()
    {
        string method = ExtractMethodWindow("OnFiltersTabSizeChanged", 900);
        AssertContainsInOrder(method,
            "orientation == Orientation.Vertical ? 6.0 : 12.0",
            "SkipExtRow.Spacing = spacing;",
            "BinaryExtRow.Spacing = spacing;",
            "ArchiveExtRow.Spacing = spacing;");
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
