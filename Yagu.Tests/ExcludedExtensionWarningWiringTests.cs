using System;
using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Regression coverage for the "excluded file type" pre-search warning and the separate Semantic
/// autocomplete history. The behavior lives in WinUI window/view-model code that depends on
/// WindowsAppSDK and cannot run in a headless unit test, so these tests pin the source-level
/// integration contracts. The pure decision logic is unit-tested in
/// <see cref="ExcludedExtensionPredictorTests"/>.
/// </summary>
public sealed class ExcludedExtensionWarningWiringTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SearchInputSource = ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");
    private static readonly string AdminSettingsSource = ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdminSettings.cs");
    private static readonly string MainViewModelSource = ReadSource("Yagu", "ViewModels", "MainViewModel.cs");
    private static readonly string SettingsWindowSource = ReadSource("Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");

    [Fact]
    public void SearchEntryPoints_RunExcludedExtensionCheckAsPostTranslationGate()
    {
        // The check is passed as the post-translation gate to SubmitSearchAsync so it runs AFTER any
        // semantic translation (seeing the model's resolved target), ordered after the HDD check.
        int occurrences = CountOccurrences(SearchInputSource, "await ViewModel.SubmitSearchAsync(CheckExcludedExtensionAndWarnAsync);");
        Assert.Equal(2, occurrences);

        foreach (var block in new[] { "StartSearchFromUiAsync", "OnQuerySubmitted" })
        {
            int methodStart = SearchInputSource.IndexOf(block, StringComparison.Ordinal);
            Assert.True(methodStart >= 0, $"Expected method {block} in MainWindow.SearchInput.cs");
            int hdd = SearchInputSource.IndexOf("CheckHddAndWarnAsync", methodStart, StringComparison.Ordinal);
            int gate = SearchInputSource.IndexOf("SubmitSearchAsync(CheckExcludedExtensionAndWarnAsync)", methodStart, StringComparison.Ordinal);
            Assert.True(hdd >= 0 && gate > hdd, $"Excluded-extension gate must follow the HDD check in {block}");
        }
    }

    [Fact]
    public void ViewModel_SubmitSearch_RunsGateAfterSemanticTranslation()
    {
        int idx = MainViewModelSource.IndexOf("public async Task SubmitSearchAsync(", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Expected SubmitSearchAsync in MainViewModel.cs");
        string method = Slice(MainViewModelSource, idx, 2000);

        int translate = method.IndexOf("TranslateSemanticQueryAsync()", StringComparison.Ordinal);
        int gate = method.IndexOf("postTranslationGate is not null", StringComparison.Ordinal);
        int start = method.IndexOf("StartSearchAsync()", StringComparison.Ordinal);
        Assert.True(translate >= 0 && gate > translate && start > gate,
            "The post-translation gate must run after semantic translation and before StartSearchAsync.");
    }

    [Fact]
    public void WarningModal_IsTitlelessWithTwoActionsAndDontWarnCheckbox()
    {
        int start = AdminSettingsSource.IndexOf("CheckExcludedExtensionAndWarnAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected CheckExcludedExtensionAndWarnAsync in MainWindow.AdminSettings.cs");
        string method = AdminSettingsSource[start..];

        Assert.Contains("TryGetExcludedExtensionWarning()", method);
        Assert.Contains("ShowTitleBar = false", method);
        Assert.Contains("PrimaryButtonText = \"Search anyway\"", method);
        Assert.Contains("SecondaryButtonText = $\"Include .{ext} & search\"", method);
        // No Cancel button — the modal only offers the two proceed actions.
        Assert.Contains("CloseButtonText = null", method);
        Assert.DoesNotContain("CloseButtonText = \"Cancel\"", method);
        Assert.Contains("Don't warn me again about excluded file types", method);
        // Secondary applies the fix; the don't-warn checkbox persists suppression.
        Assert.Contains("IncludeExtensionForSearchAsync(warning)", method);
        Assert.Contains("SuppressExcludedExtensionWarnings = true", method);
    }

    [Fact]
    public void ViewModel_TryGetWarning_GuardsSuppressionAndCallsPredictor()
    {
        int start = MainViewModelSource.IndexOf("internal ExcludedExtensionWarning? TryGetExcludedExtensionWarning()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected TryGetExcludedExtensionWarning in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 800);

        Assert.Contains("if (SuppressExcludedExtensionWarnings) return null;", method);
        Assert.Contains("ExcludedExtensionPredictor.Predict(", method);
        // Semantic mode is NOT short-circuited here: the gate runs the check post-translation, when
        // Query/IncludeGlobs already reflect the model's resolved plan.
        Assert.DoesNotContain("if (IsSemanticQueryMode) return null;", method);
    }

    [Fact]
    public void ViewModel_IncludeExtensionFix_HandlesEveryReasonAndPersists()
    {
        int start = MainViewModelSource.IndexOf("internal async Task IncludeExtensionForSearchAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected IncludeExtensionForSearchAsync in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 1800);

        Assert.Contains("ExtensionExclusionReason.BinaryExtensions", method);
        Assert.Contains("ExtensionExclusionReason.SkipExtensions", method);
        Assert.Contains("ExtensionExclusionReason.ExcludeFilter", method);
        Assert.Contains("ExtensionExclusionReason.IncludeFilter", method);
        Assert.Contains("ExcludedExtensionPredictor.RemoveExtensionToken", method);
        Assert.Contains("ExcludedExtensionPredictor.AppendExtensionToken", method);
        Assert.Contains("await PersistSettingsAsync();", method);
    }

    [Fact]
    public void ViewModel_StartSearch_PushesQueryToModeSpecificHistory()
    {
        // Semantic searches record the natural-language query in the separate Semantic history;
        // Traditional searches record the literal query in SearchHistory. Both now carry timestamps.
        Assert.Contains("SettingsService.PushRecent(_settings.SemanticSearchHistory, _settings.SemanticSearchHistoryTimes, _pendingSemanticHistoryEntry!, MaxSemanticRecentItems)", MainViewModelSource);
        Assert.Contains("SettingsService.PushRecent(_settings.SearchHistory, _settings.SearchHistoryTimes, Query, MaxRecentItems)", MainViewModelSource);
    }

    [Fact]
    public void SearchInput_ActiveQueryHistory_IsModeAware()
    {
        Assert.Contains(
            "ViewModel.IsSemanticQueryMode ? ViewModel.SemanticSearchHistory : ViewModel.SearchHistory",
            SearchInputSource);
    }

    [Fact]
    public void Settings_ExposesExcludedExtensionToggleAndSemanticHistoryLimit()
    {
        Assert.Contains("_viewModel.SuppressExcludedExtensionWarnings = !excludedExtToggle.IsOn", SettingsWindowSource);
        Assert.Contains("_viewModel.MaxSemanticRecentItems", SettingsWindowSource);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string Slice(string text, int start, int length)
        => text.Substring(start, Math.Min(length, text.Length - start));

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}
