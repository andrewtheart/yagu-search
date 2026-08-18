using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// The one-time "where should Tab go?" callout for the directory and search-pattern boxes: real unit
/// tests for the label derivation, plus source pins for the WinUI wiring that cannot be exercised here.
/// </summary>
public sealed class SearchInputTabNavigationTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MainWindowTabTargetsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.TabTargets.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string MainViewModelTabTargetsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "ViewModels", "MainViewModel.TabTargets.cs"));
    private static readonly string SettingsServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Services", "SettingsService.cs"));
    private static readonly string SettingsWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

    [Fact]
    public void TabTargetLabel_PrefersAutomationNameOverToolTipAndElementName()
        => Assert.Equal("Pin directory", TabTargetLabel.For(" Pin directory ", "Some tooltip", "PinStartupDirectoryButton"));

    [Theory]
    [InlineData("Match Case (Alt+C)", "Match Case")]
    [InlineData("Pin this directory as the startup default", "Pin this directory as the startup default")]
    [InlineData("Add this directory to the content index", "Add this directory to the content index")]
    [InlineData("Use Regular Expression (Alt+R). This will be enabled if Multiline is enabled", "Use Regular Expression")]
    [InlineData("Exact Match \u2014 on: treat the whole query as one term", "Exact Match")]
    [InlineData("Browse for folder.", "Browse for folder")]
    public void TabTargetLabel_TrimsAcceleratorsAndExplanationsFromToolTips(string toolTip, string expected)
        => Assert.Equal(expected, TabTargetLabel.For(automationName: null, toolTip, elementName: "AnyButton"));

    [Theory]
    [InlineData("PinStartupDirectoryButton", "Pin Startup Directory")]
    [InlineData("CaseSensitiveToggle", "Case Sensitive")]
    [InlineData("BrowseDirectoryButton", "Browse Directory")]
    [InlineData("Button", "Button")]
    public void TabTargetLabel_HumanizesElementNameWhenNoAutomationNameOrToolTip(string elementName, string expected)
        => Assert.Equal(expected, TabTargetLabel.For(automationName: null, toolTipText: null, elementName));

    [Fact]
    public void TabTargetLabel_FallsBackWhenNothingIsAvailable()
        => Assert.Equal("the next control", TabTargetLabel.For(null, null, null));

    [Fact]
    public void MainWindow_ResolvesTheTabDestinationFromTheFirstControlInsideEachBox()
    {
        // Generic by construction: the prompt names whichever control currently sits first inside the
        // box's inline panel, so inserting a new control at the head of the panel re-points the prompt.
        Assert.Contains("FirstTabbableDescendant(route.InlineControls)", MainWindowTabTargetsSource);
        Assert.Contains("SearchInputTabScope.Directory, DirectoryBox, DirectoryInlineControls, \"the search pattern box\"", MainWindowTabTargetsSource);
        Assert.Contains("SearchInputTabScope.SearchPattern, QueryBox, InlineSearchToggles, \"the Search button\"", MainWindowTabTargetsSource);
        Assert.Contains("string inlineLabel = DescribeTabTarget(inlineTarget);", MainWindowTabTargetsSource);

        Assert.Contains("x:Name=\"DirectoryInlineControls\"", MainWindowXaml);
        Assert.Contains("x:Name=\"InlineSearchToggles\"", MainWindowXaml);
    }

    [Fact]
    public void MainWindow_PromptsOnceThenHonoursTheRememberedTabDestination()
    {
        Assert.Contains("PreviewKeyDown=\"OnSearchInputPreviewKeyDown\"", MainWindowXaml);
        Assert.Contains("x:Name=\"TabTargetTeachingTip\"", MainWindowXaml);

        Assert.Contains("if (e.Key != VirtualKey.Tab || e.Handled)", MainWindowTabTargetsSource);
        Assert.Contains("IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)", MainWindowTabTargetsSource);
        // No inline control to offer (semantic mode hides the toggles) => plain Tab, no callout.
        Assert.Contains("if (inlineTarget is null || skipTarget is null)", MainWindowTabTargetsSource);
        Assert.Contains("if (ViewModel.HasPromptedTabTarget(route.Scope))", MainWindowTabTargetsSource);
        Assert.Contains("MoveFocusTo(ViewModel.TabSkipsInlineControls(route.Scope) ? skipTarget : inlineTarget);", MainWindowTabTargetsSource);
        Assert.Contains("ViewModel.RecordTabTargetChoiceAsync(scope, skipInlineControls);", MainWindowTabTargetsSource);
    }

    [Fact]
    public void TabDestinationChoiceIsPersistedAndResettableFromSettings()
    {
        Assert.Contains("public bool HasPromptedDirectoryTabTarget { get; set; }", SettingsServiceSource);
        Assert.Contains("public bool DirectoryTabSkipsInlineControls { get; set; }", SettingsServiceSource);
        Assert.Contains("public bool HasPromptedSearchPatternTabTarget { get; set; }", SettingsServiceSource);
        Assert.Contains("public bool SearchPatternTabSkipsInlineControls { get; set; }", SettingsServiceSource);

        Assert.Contains("public bool HasPromptedTabTarget(SearchInputTabScope scope)", MainViewModelTabTargetsSource);
        Assert.Contains("public bool TabSkipsInlineControls(SearchInputTabScope scope)", MainViewModelTabTargetsSource);
        Assert.Contains("public async Task RecordTabTargetChoiceAsync(SearchInputTabScope scope, bool skipInlineControls)", MainViewModelTabTargetsSource);
        Assert.Contains("await _settingsService.SaveAsync(_settings).ConfigureAwait(false);", MainViewModelTabTargetsSource);
        Assert.Contains("public async Task ResetTabTargetPromptsAsync()", MainViewModelTabTargetsSource);
        Assert.Contains("await PersistPromptResetAsync(settings =>", MainViewModelTabTargetsSource);
        Assert.DoesNotContain("await PersistSettingsAsync()", MainViewModelTabTargetsSource);

        Assert.Contains("Content = \"Reset Tab destination prompts\"", SettingsWindowSource);
        Assert.Contains("await _viewModel.ResetTabTargetPromptsAsync();", SettingsWindowSource);
        Assert.Contains("RegisterDefaultResetButton(resetTabTargetPrompts, () => _viewModel.AreTabTargetPromptsReset);", SettingsWindowSource);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
