using System;
using System.IO;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the "this looks like a multiline search" prompt that appears when a Traditional query
/// contains a literal "\n" escape while Multiline search is off, offering to switch Multiline (and
/// Regex) on: the persisted "Don't warn me again" flag (exercised at runtime), plus source-pins for the
/// ViewModel gating/apply logic, the titleless modal wiring, and the Developer Options reset button —
/// the ViewModel and MainWindow pull in WindowsAppSDK/Foundry so they cannot run headless here.
/// </summary>
public sealed class MultilineNewlineSuggestionTests
{
    [Fact]
    public void MultilineNewlineSuggestionDismissed_DefaultsFalse_AndRoundTrips()
    {
        Assert.False(new AppSettings().MultilineNewlineSuggestionDismissed);

        var tmp = Path.Combine(Path.GetTempPath(), "qg-mlsuggest-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            svc.Save(new AppSettings { MultilineNewlineSuggestionDismissed = true });
            Assert.True(svc.Load().MultilineNewlineSuggestionDismissed);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void ViewModel_GatesSuggestion_AndAppliesChoice()
    {
        string src = File.ReadAllText(Path.Combine(Root, "src", "Yagu", "ViewModels", "MainViewModel.cs"));

        // Offered only in Traditional mode, when Multiline is off, the user hasn't dismissed it, and the
        // query actually contains the literal "\n" escape.
        Assert.Contains("public bool ShouldOfferMultilineSuggestion(string? query)", src);
        Assert.Contains("IsTraditionalQueryMode", src);
        Assert.Contains("&& !Multiline", src);
        Assert.Contains("&& !_settings.MultilineNewlineSuggestionDismissed", src);
        Assert.Contains("query.Contains(\"\\\\n\", StringComparison.Ordinal)", src);

        // Applying the choice: "don't warn" persists suppression; accepting turns Multiline on (which via
        // OnMultilineChanged also enables Regex and disables Exact match).
        Assert.Contains("public async Task ApplyMultilineSuggestionAsync(bool switchToMultiline, bool dontRemind)", src);
        Assert.Contains("_settings.MultilineNewlineSuggestionDismissed = true;", src);
        Assert.Contains("Multiline = true;", src);

        // Developer Options reset re-enables the prompt and persists.
        Assert.Contains("public async Task ResetMultilineNewlineSuggestionAsync()", src);
        Assert.Contains("_settings.MultilineNewlineSuggestionDismissed = false;", src);
        Assert.Contains("public bool MultilineNewlineSuggestionDismissed => _settings.MultilineNewlineSuggestionDismissed;", src);
    }

    [Fact]
    public void Submit_OffersMultilineSuggestionAfterSemanticBeforeSplit()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SlowSemanticModel.cs"));

        // The multiline offer runs after the semantic offer (so a switch to Semantic suppresses it) but
        // before the Semantic/Traditional split, so accepting can flip Multiline on for this run.
        int semantic = src.IndexOf("await MaybeOfferSemanticSuggestionAsync();", StringComparison.Ordinal);
        int multiline = src.IndexOf("await MaybeOfferMultilineSuggestionAsync();", StringComparison.Ordinal);
        int split = src.IndexOf("!ViewModel.IsSemanticQueryMode || !ViewModel.SemanticSearchAvailable", StringComparison.Ordinal);
        Assert.True(semantic >= 0, "Expected the semantic suggestion to be offered.");
        Assert.True(multiline > semantic, "The multiline offer must run after the semantic offer.");
        Assert.True(split >= 0 && multiline < split, "The multiline offer must run before the Semantic/Traditional split.");
    }

    [Fact]
    public void MainWindow_ShowsTitlelessMultilineModalWithDontWarn()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));

        Assert.Contains("private async Task MaybeOfferMultilineSuggestionAsync()", src);
        Assert.Contains("if (!ViewModel.ShouldOfferMultilineSuggestion(ViewModel.Query))", src);
        // Don't stack on another owned modal.
        Assert.Contains("YaguDialog.HasOpenOwnedWindow(_hwnd)", src);
        // Titleless modal with a switch/keep choice.
        Assert.Contains("ShowTitleBar = false", src);
        Assert.Contains("PrimaryButtonText = \"Switch to Multiline\"", src);
        Assert.Contains("CloseButtonText = \"Search as-is\"", src);
        // "Don't warn me again" checkbox wired into the apply call.
        Assert.Contains("Content = \"Don't warn me again\"", src);
        Assert.Contains("dontRemind: dontWarn.IsChecked == true", src);
        Assert.Contains("switchToMultiline: result == YaguDialogResult.Primary", src);
        // Shared dialog only — never the WinUI ContentDialog.
        Assert.DoesNotContain("new ContentDialog", src);
    }

    [Fact]
    public void DeveloperOptions_ExposeResettableMultilinePrompt()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.xaml.cs"));

        Assert.Contains("Content = \"Reset multiline search prompt\"", src);
        Assert.Contains("await _viewModel.ResetMultilineNewlineSuggestionAsync();", src);
        Assert.Contains("() => !_viewModel.MultilineNewlineSuggestionDismissed", src);
    }

    private static string Root => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
