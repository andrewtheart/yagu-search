using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin tests for the WinUI-coupled first-run AI-model qualification flow (the dialog, the
/// MainWindow startup partial, and the view-model wiring). These files are not compiled into the test
/// assembly, so they are validated by asserting on their source text — matching the project's
/// convention for UI/Foundry-coupled code that can't get runtime coverage.
/// </summary>
public sealed class SemanticModelQualificationFlowTests
{
    // ── Startup wiring ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StartupChain_OffersQualificationAfterCpuWarning()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.StartupChecks.cs"));

        int cpu = src.IndexOf("ShowCpuSemanticWarningIfNeededAsync()", StringComparison.Ordinal);
        int qual = src.IndexOf("OfferSemanticModelQualificationIfNeededAsync()", StringComparison.Ordinal);
        Assert.True(cpu >= 0, "CPU warning step should be present in the startup chain.");
        Assert.True(qual > cpu, "Qualification offer should be sequenced after the CPU warning.");
    }

    [Fact]
    public void OfferFlow_GatesOnShouldOfferAndDoesNotStackModals()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        Assert.Contains("if (!ViewModel.ShouldOfferSemanticModelQualification)", src);
        Assert.Contains("if (YaguDialog.HasOpenOwnedWindow(_hwnd))", src);
    }

    [Fact]
    public void OfferFlow_IntroIsTitleBarLessAndRefusalDisablesSemantic()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        Assert.Contains("ShowTitleBar = false", src);
        Assert.Contains("if (intro != YaguDialogResult.Primary)", src);
        // Refusing the check turns AI search off and shows the opt-back-in notice.
        Assert.Contains("await ViewModel.DeclineAndDisableSemanticSearchAsync();", src);
        Assert.Contains("await ShowSemanticSearchDisabledNoticeAsync();", src);
    }

    [Fact]
    public void OfferFlow_CentersIntroOverPositionedWindow()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        // The Low-priority yield must run before the intro is shown so the modal centers on the backing
        // window rather than its pre-positioned launch location.
        int yield = src.IndexOf("YieldUntilWindowPositionedAsync()", StringComparison.Ordinal);
        int intro = src.IndexOf("Title = \"Set up AI (Semantic) search?\"", StringComparison.Ordinal);
        Assert.True(yield >= 0, "Expected a window-positioned yield before the intro.");
        Assert.True(intro > yield, "The intro should be shown after awaiting the window-positioned yield.");
        Assert.Contains("DispatcherQueuePriority.Low", src);
    }

    [Fact]
    public void OfferFlow_DisabledNoticeLinksToAiSettingsTab()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        Assert.Contains("private async Task ShowSemanticSearchDisabledNoticeAsync()", src);
        // Inline hyperlink that opens Settings to the AI tab after the notice closes.
        Assert.Contains("var link = new Hyperlink();", src);
        Assert.Contains("dialogRef?.AcceptClose();", src);
        Assert.Contains("OpenSettingsToAiTab();", src);
        Assert.Contains("_settingsWindow?.SelectTabByHeader(\"AI\");", src);
    }

    [Fact]
    public void OfferFlow_RunsDialogAndRoutesEachOutcome()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        Assert.Contains("SemanticModelQualificationDialog.ShowAsync(", src);
        Assert.Contains("ViewModel.RunSemanticModelQualificationAsync(thresholds, progress, token)", src);
        // Cancelled → no persistence.
        Assert.Contains("if (result.Cancelled)", src);
        // Accepted → apply with the chosen alias.
        Assert.Contains("ApplySemanticModelQualificationAsync(result.Result, accepted: true, result.ChosenAlias)", src);
        // Skipped-with-result → record recommendation without switching.
        Assert.Contains("ApplySemanticModelQualificationAsync(result.Result, accepted: false)", src);
        // No result → mark declined so it isn't re-offered.
        Assert.Contains("DeclineSemanticModelQualificationAsync()", src);
    }

    [Fact]
    public void OfferFlow_SuppressesQueryDropdownWhileDialogOpen()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        // The non-YaguDialog qualification window parks the suggestion dropdowns shut for its lifetime so a
        // windowed suggestion popup can't float above it, then restores suppression in a finally.
        Assert.Contains("_suppressQuerySuggestionsUntilTick = long.MaxValue;", src);
        Assert.Contains("CollapseInputSuggestionDropdowns();", src);
        Assert.Contains("_ownedModalWindowDepth++;", src);
        Assert.Contains("HideQuerySuggestions(QueryBox);", src);
        Assert.Contains("_ownedModalWindowDepth = Math.Max(0, _ownedModalWindowDepth - 1);", src);
    }

    // ── Dialog ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dialog_IsTitleBarLessOwnedWindow()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        Assert.Contains("ExtendsContentIntoTitleBar = true", src);
        Assert.Contains("SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false)", src);
        Assert.Contains("WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd)", src);
        Assert.Contains("AppThemeService.ApplyThemedDialogSurface(", src);
    }

    [Fact]
    public void Dialog_RunsSweepWithProgressAndCancellation()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        Assert.Contains("private async Task RunSweepAsync()", src);
        Assert.Contains("await _run(_thresholds, progress, _cts.Token)", src);
        Assert.Contains("new Progress<SemanticQualificationProgress>(OnProgress)", src);
        Assert.Contains("catch (OperationCanceledException)", src);
    }

    [Fact]
    public void Dialog_ShowsThresholdConfigBeforeSweep()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        // The dialog opens in the config state (not the running sweep) and only starts the sweep on Run.
        Assert.Contains("ShowConfigState();", src);
        Assert.Contains("private void ShowConfigState()", src);
        // Three seconds-based NumberBox inputs with spin buttons, defaulted from the thresholds type.
        Assert.Contains("new NumberBox", src);
        Assert.Contains("SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline", src);
        Assert.Contains("ModelQualificationThresholds.DefaultModelLoadMaxMs / 1000", src);
        Assert.Contains("ModelQualificationThresholds.DefaultSimpleQueryMaxMs / 1000", src);
        Assert.Contains("ModelQualificationThresholds.DefaultComplexQueryMaxMs / 1000", src);
        // The Run button builds the thresholds from the inputs and starts the sweep.
        Assert.Contains("AddFooterButton(\"Run\", accent: true, StartSweepFromConfig)", src);
        Assert.Contains("_thresholds = new ModelQualificationThresholds", src);
        Assert.Contains("_ = RunSweepAsync();", src);
    }

    [Fact]
    public void Dialog_GuardsAgainstDoubleClickStartingSweep()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        // A double-click on "Run" must not close the dialog by landing on the "Cancel" button that
        // replaces it. Starting the sweep suppresses Cancel for the system double-click interval, so the
        // buffered second click is swallowed regardless of dispatcher timing.
        Assert.Contains("private void StartSweepGuarded()", src);
        Assert.Contains("_suppressCancelUntilTick = Environment.TickCount64 + (doubleClickMs > 0 ? doubleClickMs : 500);", src);
        Assert.Contains("GetDoubleClickTime()", src);
        // Cancel honors the suppression window.
        Assert.Contains("if (Environment.TickCount64 < _suppressCancelUntilTick)", src);
        // Both the config "Run" and the "Try again" buttons route through the guard.
        Assert.Contains("StartSweepGuarded();", src);
        Assert.Contains("{ _result = null; StartSweepGuarded(); }", src);
    }

    [Fact]
    public void Dialog_ResultScreenOffersAcceptSkipAndPicksSuggestion()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        Assert.Contains("SemanticModelQualificationCoordinator.Suggestion(_result)", src);
        Assert.Contains("AddFooterButton(\"Skip\", accent: false, Skip)", src);
        Assert.Contains("AddFooterButton(\"Use this model\", accent: true, Accept)", src);
        // Only candidates that produced output are selectable.
        Assert.Contains("!report.Crashed && report.Probes.Any(p => p.Completed)", src);
    }

    [Fact]
    public void Dialog_CompletionCarriesDecision()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        Assert.Contains("Accepted = _accepted", src);
        Assert.Contains("ChosenAlias = _accepted ? _selectedAlias : null", src);
        Assert.Contains("Declined = _declined", src);
        Assert.Contains("Cancelled = _cancelled || (!_accepted && !_declined && !_switchToTraditional)", src);
        Assert.Contains("SwitchToTraditional = _switchToTraditional", src);
        Assert.Contains("OpenAiSettingsRequested = _openAiSettings", src);
    }

    [Fact]
    public void Dialog_NoUsableModel_DefaultsToTraditionalNotAutoPick()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "SemanticModelQualificationDialog.cs"));

        // When no candidate produced usable results, show the dedicated state (not the generic error one)
        // and never promise to auto-pick a model.
        Assert.Contains("ShowNoUsableModelState();", src);
        Assert.Contains("private void ShowNoUsableModelState()", src);
        Assert.DoesNotContain("pick a model automatically", src);
        Assert.DoesNotContain("picks a model automatically", src);
        // It defaults to Traditional search and links to AI settings.
        Assert.Contains("Use Traditional search", src);
        Assert.Contains("AI (Semantic) search has been turned off and Yagu will use Traditional", src);
        Assert.Contains("_openAiSettings = true; UseTraditional();", src);
        // The Traditional path flags the result rather than accepting/declining a model.
        Assert.Contains("private void UseTraditional()", src);
        Assert.Contains("_switchToTraditional = true;", src);
    }

    [Fact]
    public void OfferFlow_NoUsableModel_DisablesSemanticAndOffersSettings()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "MainWindow", "MainWindow.SemanticQualification.cs"));

        // A failed check must default the app to Traditional (disable AI search) instead of auto-picking.
        Assert.Contains("if (result.SwitchToTraditional)", src);
        Assert.Contains("await ViewModel.DeclineAndDisableSemanticSearchAsync();", src);
        Assert.Contains("if (result.OpenAiSettingsRequested)", src);
        Assert.Contains("OpenSettingsToAiTab();", src);
    }

    // ── View-model wiring ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ViewModel_ShouldOfferDelegatesToCoordinator()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("public bool ShouldOfferSemanticModelQualification =>", src);
        Assert.Contains("SemanticModelQualificationCoordinator.ShouldOffer(_settings, SemanticSearchAvailable)", src);
    }

    [Fact]
    public void ViewModel_RunUsesRunnerOverDefaultProbeSet()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("public async Task<ModelQualificationResult> RunSemanticModelQualificationAsync(", src);
        Assert.Contains("new SemanticModelQualificationRunner(", src);
        // The first-run sweep is capped to the best-first top candidates so it never grinds through the
        // machine's entire Foundry catalog (dozens of models) when none of the top picks qualifies.
        Assert.Contains("maxCandidates: SemanticModelQualificationRunner.DefaultMaxCandidates", src);
        Assert.Contains("runner.RunAsync(SemanticProbeSet.Default, thresholds, progress, cancellationToken)", src);
    }

    [Fact]
    public void ViewModel_RunSuspendsUnloadAfterUseSoProbesMeasureWarmLatency()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        // The sweep must keep the model resident across a candidate's probes; otherwise "release after
        // each search" reloads the model inside every timed probe and disqualifies large models on latency.
        int method = src.IndexOf("public async Task<ModelQualificationResult> RunSemanticModelQualificationAsync(", StringComparison.Ordinal);
        Assert.True(method >= 0);
        int restore = src.IndexOf("_semanticTranslator.SetUnloadAfterUse(restoreUnloadAfterUse);", method, StringComparison.Ordinal);
        int suppress = src.IndexOf("_semanticTranslator.SetUnloadAfterUse(false);", method, StringComparison.Ordinal);
        Assert.True(suppress >= 0, "The sweep should disable unload-after-use before running.");
        Assert.True(restore > suppress, "The user's unload-after-use setting must be restored after the sweep.");
        Assert.Contains("bool restoreUnloadAfterUse = _settings.SemanticUnloadModelAfterUse;", src);
        Assert.Contains("await _semanticTranslator.UnloadCurrentModelAsync(CancellationToken.None)", src);
    }

    [Fact]
    public void ViewModel_ApplyPersistsAndSelectsModelLive()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("public async Task ApplySemanticModelQualificationAsync(", src);
        Assert.Contains("SemanticModelQualificationCoordinator.ApplyResult(_settings, result, accepted, chosenAlias)", src);
        Assert.Contains("_semanticTranslator?.SetModelOverride(", src);
        Assert.Contains("await PersistSettingsAsync()", src);
    }

    [Fact]
    public void ViewModel_DeclineMarksCompleteAndPersists()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("public async Task DeclineSemanticModelQualificationAsync()", src);
        Assert.Contains("SemanticModelQualificationCoordinator.MarkDeclined(_settings)", src);
    }

    [Fact]
    public void ViewModel_DeclineAndDisableTurnsSemanticOffAndPersists()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("public async Task DeclineAndDisableSemanticSearchAsync()", src);
        int mark = src.IndexOf("SemanticModelQualificationCoordinator.MarkDeclined(_settings);", StringComparison.Ordinal);
        int disable = src.IndexOf("SemanticSearchAvailable = false;", StringComparison.Ordinal);
        Assert.True(mark >= 0 && disable > mark, "Should mark the check complete before turning the toggle off.");
        Assert.Contains("await PersistSettingsAsync().ConfigureAwait(true);", src);
    }

    [Fact]
    public void ViewModel_ResetReenablesSemanticAndClearsModel()
    {
        string src = ReadAppFile(Path.Combine("ViewModels", "MainViewModel.cs"));

        Assert.Contains("public async Task ResetSemanticModelQualificationAsync()", src);
        Assert.Contains("SemanticModelQualificationCoordinator.Reset(_settings);", src);
        // Re-enable AI search so the offer returns, and drop the live model override.
        Assert.Contains("SemanticSearchAvailable = true;", src);
        Assert.Contains("SemanticModelAlias = string.Empty;", src);
        Assert.Contains("_semanticTranslator?.SetModelOverride(null);", src);
        // Button-enable state helper.
        Assert.Contains("public bool HasSemanticModelQualificationState =>", src);
    }

    [Fact]
    public void Settings_DeveloperOptionsHasResetModelCheckButton()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains("Content = \"Reset AI model check (re-prompt on startup)\"", src);
        Assert.Contains("await _viewModel.ResetSemanticModelQualificationAsync();", src);
        Assert.Contains("() => !_viewModel.HasSemanticModelQualificationState", src);
    }

    [Fact]
    public void Settings_ModelSectionHasReRunProbeButton()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        // A "Re-run model probe" button in the AI → Model section runs the same qualification sweep the
        // first-run flow uses and routes each outcome the same way.
        Assert.Contains("Content = \"Re-run model probe\\u2026\"", src);
        Assert.Contains("SemanticModelQualificationDialog.ShowAsync(", src);
        Assert.Contains("_viewModel.RunSemanticModelQualificationAsync(thresholds, progress, token)", src);
        Assert.Contains("_viewModel.ApplySemanticModelQualificationAsync(result.Result, accepted: true, result.ChosenAlias)", src);
        Assert.Contains("_viewModel.ApplySemanticModelQualificationAsync(result.Result, accepted: false)", src);
        Assert.Contains("await _viewModel.DeclineAndDisableSemanticSearchAsync();", src);
    }

    [Fact]
    public void SettingsWindow_ResolvesAiTabByHeaderName()
    {
        string src = ReadAppFile(Path.Combine("UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains("public void SelectTabByHeader(string header)", src);
        // Resolves via header text because tabs are sorted alphabetically (index isn't fixed).
        Assert.Contains("_tabHeaders.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase))", src);
        // Deferred content: remember the requested header until the tabs are built.
        Assert.Contains("_pendingSelectTabHeader = header;", src);
    }

    private static string ReadAppFile(string relativePath)
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", relativePath));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
