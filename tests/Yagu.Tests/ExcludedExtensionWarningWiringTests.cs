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
    private static readonly string SlowSemanticModelSource = ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.SlowSemanticModel.cs");
    private static readonly string AdminSettingsSource = ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdminSettings.cs");
    private static readonly string AdvancedOptionsSource = ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.AdvancedOptions.cs");
    private static readonly string StartupChecksSource = ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs");
    private static readonly string MainViewModelSource = ReadSource("Yagu", "ViewModels", "MainViewModel.cs");
    private static readonly string SettingsWindowSource = ReadSource("Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs");

    [Fact]
    public void SearchEntryPoints_RunHddAndExcludedChecksAsPostTranslationGate()
    {
        // Both interactive entry points route through the slow-model watchdog wrapper, which passes the
        // combined warning gate to SubmitSearchAsync, so the HDD + excluded-extension notices run AFTER
        // any semantic translation — against the directory the model resolved. A semantic query resolving
        // to an SSD must not show a spurious HDD warning before the model has run, so neither entry point
        // may call the HDD check directly.
        int occurrences = CountOccurrences(SearchInputSource, "await SubmitSearchWithSlowModelWatchAsync();");
        Assert.Equal(2, occurrences);

        // The watchdog wrapper threads the same gate into SubmitSearchAsync.
        Assert.Contains("await ViewModel.SubmitSearchAsync(RunPreSearchWarningGatesAsync);", SlowSemanticModelSource);

        // The gate itself runs the HDD check first, then the excluded-extension check.
        int gateStart = SearchInputSource.IndexOf("private async Task<bool> RunPreSearchWarningGatesAsync(", StringComparison.Ordinal);
        Assert.True(gateStart >= 0, "Expected RunPreSearchWarningGatesAsync in MainWindow.SearchInput.cs");
        string gate = Slice(SearchInputSource, gateStart, 600);
        int hdd = gate.IndexOf("CheckHddAndWarnAsync", StringComparison.Ordinal);
        int excluded = gate.IndexOf("CheckExcludedExtensionAndWarnAsync", StringComparison.Ordinal);
        Assert.True(hdd >= 0 && excluded > hdd, "The gate must run the HDD check before the excluded-extension check.");
    }

    [Fact]
    public void PreSearchGate_RunsMatchEverythingCheckLast_ModalIsTitlelessAndOffersFilenameListing()
    {
        // The combined gate runs HDD -> excluded-extension -> match-everything (broad regex) check.
        int gateStart = SearchInputSource.IndexOf("private async Task<bool> RunPreSearchWarningGatesAsync(", StringComparison.Ordinal);
        Assert.True(gateStart >= 0, "Expected RunPreSearchWarningGatesAsync in MainWindow.SearchInput.cs");
        string gate = Slice(SearchInputSource, gateStart, 700);
        int excluded = gate.IndexOf("CheckExcludedExtensionAndWarnAsync", StringComparison.Ordinal);
        int broad = gate.IndexOf("CheckMatchEverythingPatternAndWarnAsync", StringComparison.Ordinal);
        Assert.True(excluded >= 0 && broad > excluded,
            "The match-everything check must run after the excluded-extension check.");

        // The gate fires only in regex mode, only when content is scanned (not a FileNames-only search),
        // and only for a whole-pattern match-all; the modal is title-bar-less and offers a name listing.
        int start = AdminSettingsSource.IndexOf(
            "private async Task<bool> CheckMatchEverythingPatternAndWarnAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected CheckMatchEverythingPatternAndWarnAsync in MainWindow.AdminSettings.cs");
        string method = Slice(AdminSettingsSource, start, 3600);
        Assert.Contains("if (!ViewModel.UseRegex) return true;", method);
        Assert.Contains("SearchMode.FileNames) return true;", method);
        Assert.Contains("SearchPatternClassifier.IsMatchEverythingRegex(ViewModel.Query)", method);
        Assert.Contains("ShowTitleBar = false", method);
        Assert.Contains("PrimaryButtonText = \"Search file names\"", method);
        Assert.Contains("ViewModel.SearchModeIndex = (int)Yagu.Models.SearchMode.FileNames;", method);
    }

    [Fact]
    public void ViewModel_SubmitSearch_RunsGateAfterSemanticTranslation()
    {
        int idx = MainViewModelSource.IndexOf("public async Task SubmitSearchAsync(", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Expected SubmitSearchAsync in MainViewModel.cs");
        string method = Slice(MainViewModelSource, idx, 3000);

        int translate = method.IndexOf("TranslateSemanticQueryAsync()", StringComparison.Ordinal);
        int gate = method.IndexOf("postTranslationGate is not null", StringComparison.Ordinal);
        int start = method.IndexOf("StartSearchAsync()", StringComparison.Ordinal);
        Assert.True(translate >= 0 && gate > translate && start > gate,
            "The post-translation gate must run after semantic translation and before StartSearchAsync.");
    }

    [Fact]
    public void WarningModal_IsTitlelessWithTwoActionsAndRemembersTheChosenAction()
    {
        int start = AdminSettingsSource.IndexOf("CheckExcludedExtensionAndWarnAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected CheckExcludedExtensionAndWarnAsync in MainWindow.AdminSettings.cs");
        string method = AdminSettingsSource[start..];

        Assert.Contains("TryGetExcludedExtensionWarning()", method);
        Assert.Contains("ShowTitleBar = false", method);
        Assert.Contains("PrimaryButtonText = $\"Include .{ext} & search\"", method);
        Assert.Contains("SecondaryButtonText = \"Search anyway\"", method);
        // No Cancel button — the modal only offers the two proceed actions.
        Assert.Contains("CloseButtonText = null", method);
        Assert.DoesNotContain("CloseButtonText = \"Cancel\"", method);
        // The checkbox now REMEMBERS the chosen button (clearer than the old ambiguous "Don't warn me again").
        Assert.Contains("don't ask again for excluded file types", method);
        Assert.DoesNotContain("Don't warn me again about excluded file types", method);
        // A saved "Always do this" preference is applied without showing the dialog (include vs. search-without).
        Assert.Contains("if (ViewModel.SuppressExcludedExtensionWarnings)", method);
        Assert.Contains("if (ViewModel.IncludeExcludedExtensionByDefault)", method);
        // Remembering the Primary button = always include; the Secondary = always search without.
        Assert.Contains("IncludeExcludedExtensionByDefault = true", method);
        Assert.Contains("IncludeExcludedExtensionByDefault = false", method);
        Assert.Contains("SuppressExcludedExtensionWarnings = true", method);
        Assert.Contains("IncludeExtensionForSearchAsync(warning)", method);
    }

    [Fact]
    public void ViewModel_TryGetWarning_DoesNotGateOnSuppression_AndCallsPredictor()
    {
        int start = MainViewModelSource.IndexOf("internal ExcludedExtensionWarning? TryGetExcludedExtensionWarning()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected TryGetExcludedExtensionWarning in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 1400);

        // Suppression is NO LONGER gated here — the caller (CheckExcludedExtensionAndWarnAsync) needs this
        // warning's data even when suppressed, to apply the remembered default (which may be "always include").
        Assert.DoesNotContain("if (SuppressExcludedExtensionWarnings) return null;", method);
        Assert.Contains("ExcludedExtensionPredictor.Predict(", method);
        // Semantic mode is NOT short-circuited here: the gate runs the check post-translation, when
        // Query/IncludeGlobs already reflect the model's resolved plan.
        Assert.DoesNotContain("if (IsSemanticQueryMode) return null;", method);
    }

    [Fact]
    public void ViewModel_LoadsAndSaves_IncludeExcludedExtensionByDefault()
    {
        Assert.Contains("IncludeExcludedExtensionByDefault = _settings.IncludeExcludedExtensionByDefault;", MainViewModelSource);
        Assert.Contains("_settings.IncludeExcludedExtensionByDefault = IncludeExcludedExtensionByDefault;", MainViewModelSource);
    }

    [Fact]
    public void ViewModel_IncludeExtensionFix_HandlesEveryReasonTransiently()
    {
        int start = MainViewModelSource.IndexOf("internal Task IncludeExtensionForSearchAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected IncludeExtensionForSearchAsync in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 1800);

        Assert.Contains("ExtensionExclusionReason.BinaryExtensions", method);
        Assert.Contains("ExtensionExclusionReason.SkipExtensions", method);
        Assert.Contains("ExtensionExclusionReason.ArchiveExtensions", method);
        Assert.Contains("ExtensionExclusionReason.ExcludeFilter", method);
        Assert.Contains("ExtensionExclusionReason.IncludeFilter", method);
        Assert.Contains("ExcludedExtensionPredictor.RemoveExtensionToken", method);
        Assert.Contains("ExcludedExtensionPredictor.AppendExtensionToken", method);
        // Transient: the fix applies to the current search only and must NOT write to saved settings.
        Assert.Contains("_advancedOptionsTransientlyChanged = true;", method);
        Assert.DoesNotContain("PersistSettingsAsync", method);
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
    public void Settings_ExposesExcludedExtensionChoiceAndSemanticHistoryLimit()
    {
        // Settings offers the 3-way choice (ask / always include / always search without), driving the
        // same two settings the warning dialog remembers.
        Assert.Contains("Always include the excluded file type", SettingsWindowSource);
        Assert.Contains("Always search without it", SettingsWindowSource);
        Assert.Contains("_viewModel.IncludeExcludedExtensionByDefault = true", SettingsWindowSource);
        Assert.Contains("_viewModel.SuppressExcludedExtensionWarnings = true", SettingsWindowSource);
        Assert.Contains("_viewModel.MaxSemanticRecentItems", SettingsWindowSource);
    }

    [Fact]
    public void ViewModel_TransientFix_ResetsAdvancedOptionsWhenSearchEnds()
    {
        // The "Include & search" fix marks the search as transiently changed; when IsSearching flips
        // back to false (search finished OR canceled) the options are reset to the SAVED defaults,
        // unless a semantic resolution is intentionally being shown in Advanced Options.
        int start = MainViewModelSource.IndexOf("partial void OnIsSearchingChanged(bool value)", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected OnIsSearchingChanged in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 700);

        Assert.Contains("if (value) return;", method);                       // act only when a search ENDS
        Assert.Contains("if (!_advancedOptionsTransientlyChanged) return;", method);
        Assert.Contains("_advancedOptionsTransientlyChanged = false;", method);
        Assert.Contains("if (_semanticResolutionVisible) return;", method);  // don't fight semantic display
        Assert.Contains("ResetAdvancedOptionsToSavedDefaults();", method);
    }

    [Fact]
    public void ViewModel_ResetAdvancedOptions_RestoresFromSavedSettings()
    {
        int start = MainViewModelSource.IndexOf("public void ResetAdvancedOptionsToSavedDefaults()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected ResetAdvancedOptionsToSavedDefaults in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 2400);

        // Reset reads the SAVED settings (not hard-coded app defaults) ...
        Assert.Contains("AppSettings settings = _settingsService.Load();", method);
        // ... restores the three extension lists and their toggles ...
        Assert.Contains("SkipExtensions = settings.SkipExtensions;", method);
        Assert.Contains("BinaryExtensions = settings.BinaryExtensions;", method);
        Assert.Contains("ArchiveExtensions = settings.ArchiveExtensions;", method);
        Assert.Contains("SearchBinary = !settings.SkipBinary;", method);
        Assert.Contains("SearchInsideArchives = settings.SearchInsideArchives;", method);
        // ... and rebuilds every dropdown so the checkboxes reflect the restored lists.
        Assert.Contains("SyncSkipExtensionItems();", method);
        Assert.Contains("SyncBinaryExtensionItems();", method);
        Assert.Contains("SyncArchiveExtensionItems();", method);
    }

    [Fact]
    public void ViewModel_TransientHelpers_ApplyPerListRuleSessionOnly()
    {
        // Skip rule: keep skipping everything EXCEPT the searched extension, then resync the dropdown.
        int unskip = MainViewModelSource.IndexOf("private void UnskipExtensionForSearch(", StringComparison.Ordinal);
        Assert.True(unskip >= 0, "Expected UnskipExtensionForSearch in MainViewModel.cs");
        string unskipBody = Slice(MainViewModelSource, unskip, 600);
        Assert.Contains("universe.Remove(ext);", unskipBody);
        Assert.Contains("SyncSkipExtensionItems();", unskipBody);

        // Binary rule: enable binary search and select ONLY the searched binary type.
        int binary = MainViewModelSource.IndexOf("private void EnableBinarySearchForExtension(", StringComparison.Ordinal);
        Assert.True(binary >= 0, "Expected EnableBinarySearchForExtension in MainViewModel.cs");
        string binaryBody = Slice(MainViewModelSource, binary, 700);
        Assert.Contains("SearchBinary = true;", binaryBody);
        Assert.Contains("SyncBinaryExtensionItems();", binaryBody);

        // Archive rule: enable search-inside-archives and select ONLY the searched archive type.
        int archive = MainViewModelSource.IndexOf("private void EnableArchiveSearchForExtension(", StringComparison.Ordinal);
        Assert.True(archive >= 0, "Expected EnableArchiveSearchForExtension in MainViewModel.cs");
        string archiveBody = Slice(MainViewModelSource, archive, 400);
        Assert.Contains("SearchInsideArchives = true;", archiveBody);
        Assert.Contains("ArchiveExtensions = ext;", archiveBody);
        Assert.Contains("SyncArchiveExtensionItems();", archiveBody);
    }

    [Fact]
    public void ViewModel_TryGetWarning_FeedsArchiveUniverseToPredictor()
    {
        int start = MainViewModelSource.IndexOf("internal ExcludedExtensionWarning? TryGetExcludedExtensionWarning()", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected TryGetExcludedExtensionWarning in MainViewModel.cs");
        string method = Slice(MainViewModelSource, start, 1400);

        // The archive "universe" is the saved defaults plus whatever is active; contents only count as
        // "searched" for the active types when Search-inside-archives is on.
        Assert.Contains("ParseExtensionSet(SettingsArchiveExtensions)", method);
        Assert.Contains("SearchInsideArchives", method);
        Assert.Contains("archiveUniverse", method);
        Assert.Contains("archiveSearched", method);
    }

    [Fact]
    public void AdvancedOptionsResetButton_DelegatesToViewModel()
    {
        // The Reset button must reuse the single ResetAdvancedOptionsToSavedDefaults implementation
        // (the same code path as the post-search auto-reset) instead of duplicating reset logic.
        Assert.Contains("ViewModel.ResetAdvancedOptionsToSavedDefaults();", AdvancedOptionsSource);
    }

    [Fact]
    public void AutoSearchOnLoad_RunsFullPreSearchWarningGate()
    {
        // An auto-search launched on startup (a pinned directory, --dir, or the Explorer context menu)
        // must run the SAME pre-search warning gate as an interactive search — not just the HDD check —
        // so it also warns before a doomed full-tree scan for a file whose extension is excluded.
        int idx = StartupChecksSource.IndexOf("_autoSearchOnLoad = false;", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Expected the auto-search-on-load block in MainWindow.StartupChecks.cs");
        string block = Slice(StartupChecksSource, idx, 800);
        Assert.Contains("await RunPreSearchWarningGatesAsync()", block);
        Assert.Contains("await ViewModel.StartSearchAsync();", block);
        // It must NOT gate only on the HDD check (the prior gap that skipped the excluded-extension notice).
        Assert.DoesNotContain("if (await CheckHddAndWarnAsync())", block);
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
        => File.ReadAllText(Path.Combine(new[] { RepoRoot, "src" }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}
