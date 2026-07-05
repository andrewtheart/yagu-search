using System;
using System.IO;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for the "this looks like an AI search" suggestion that offers to switch a natural-language
/// Traditional query to AI (Semantic) search: the persisted "Don't remind me again" flag (exercised at
/// runtime), plus source-pins for the ViewModel gating/apply logic and the titleless modal wiring — the
/// ViewModel and MainWindow pull in WindowsAppSDK/Foundry so they cannot run headless here.
/// </summary>
public sealed class SemanticQuerySuggestionTests
{
    [Fact]
    public void SemanticSuggestionDismissed_DefaultsFalse_AndRoundTrips()
    {
        Assert.False(new AppSettings().SemanticSuggestionDismissed);

        var tmp = Path.Combine(Path.GetTempPath(), "qg-semsuggest-" + Guid.NewGuid() + ".json");
        try
        {
            var svc = new SettingsService(tmp);
            svc.Save(new AppSettings { SemanticSuggestionDismissed = true });
            Assert.True(svc.Load().SemanticSuggestionDismissed);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void ViewModel_GatesSuggestion_AndAppliesChoice()
    {
        string src = File.ReadAllText(Path.Combine(Root, "src", "Yagu", "ViewModels", "MainViewModel.cs"));

        // Offered in Traditional mode whenever a model is downloaded (the AI-search toggle may be off),
        // the user hasn't dismissed it, and the query passes the heuristic.
        Assert.Contains("public bool ShouldOfferSemanticSuggestion(string? query)", src);
        Assert.Contains("IsTraditionalQueryMode", src);
        Assert.Contains("&& IsSemanticModelDownloaded", src);
        Assert.Contains("&& !_settings.SemanticSuggestionDismissed", src);
        Assert.Contains("SemanticQueryHeuristicDetector.LooksLikeSemanticQuery(query)", src);

        // Applying the choice: "don't remind" persists suppression; accepting enables AI search if it was
        // off and switches to Semantic now.
        Assert.Contains("public async Task ApplySemanticSuggestionAsync(bool switchToSemantic, bool dontRemind)", src);
        Assert.Contains("_settings.SemanticSuggestionDismissed = true;", src);
        Assert.Contains("if (!SemanticSearchAvailable)", src);
        Assert.Contains("SemanticSearchAvailable = true;", src);
        Assert.Contains("IsSemanticQueryMode = true;", src);
    }

    [Fact]
    public void Submit_OffersSuggestionBeforeRunning()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SlowSemanticModel.cs"));

        // The suggestion runs at the very start of the submit path, before the Semantic/Traditional split,
        // so accepting can flip the mode for this run.
        int call = src.IndexOf("await MaybeOfferSemanticSuggestionAsync();", StringComparison.Ordinal);
        int split = src.IndexOf("!ViewModel.IsSemanticQueryMode || !ViewModel.SemanticSearchAvailable", StringComparison.Ordinal);
        Assert.True(call >= 0, "Expected SubmitSearchWithSlowModelWatchAsync to offer the semantic suggestion.");
        Assert.True(split >= 0 && call < split, "The suggestion must run before the Semantic/Traditional split.");
    }

    [Fact]
    public void MainWindow_ShowsTitlelessSuggestionModalWithDontRemind()
    {
        string src = File.ReadAllText(
            Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));

        Assert.Contains("private async Task MaybeOfferSemanticSuggestionAsync()", src);
        Assert.Contains("if (!ViewModel.ShouldOfferSemanticSuggestion(ViewModel.Query))", src);
        // Don't stack on another owned modal.
        Assert.Contains("YaguDialog.HasOpenOwnedWindow(_hwnd)", src);
        // Titleless modal with the semantic-mode glyph and a switch/keep choice.
        Assert.Contains("TitleGlyph = \"\\uF4A5\"", src);
        Assert.Contains("ShowTitleBar = false", src);
        Assert.Contains("PrimaryButtonText = \"Switch to AI search\"", src);
        Assert.Contains("CloseButtonText = \"Keep Traditional\"", src);
        // "Don't remind me again" checkbox wired into the apply call.
        Assert.Contains("Content = \"Don't remind me again\"", src);
        Assert.Contains("dontRemind: dontRemind.IsChecked == true", src);
        Assert.Contains("switchToSemantic: result == YaguDialogResult.Primary", src);
        // Shared dialog only — never the WinUI ContentDialog.
        Assert.DoesNotContain("new ContentDialog", src);
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
