using System;
using System.IO;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pins for the morphing Search/Cancel button's "Canceling.." feedback: while a search or
/// semantic translation is draining after Cancel is clicked, the button must disable itself and show
/// "Canceling..". Both the ViewModel (which pulls in WindowsAppSDK/Foundry) and the MainWindow partial
/// are WinUI-coupled and can't run headless, so this behavior is validated by reading their source.
/// </summary>
public sealed class SearchCancelButtonRegressionTests
{
    private static readonly string MainViewModelSource = File.ReadAllText(
        Path.Combine(Root, "src", "Yagu", "ViewModels", "MainViewModel.cs"));

    private static readonly string MainWindowXamlCsSource = File.ReadAllText(
        Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs"));

    private static readonly string SearchInputSource = File.ReadAllText(
        Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));

    private static readonly string SlowSemanticModelSource = File.ReadAllText(
        Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SlowSemanticModel.cs"));

    private static readonly string MainWindowXamlSource = File.ReadAllText(
        Path.Combine(Root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

    [Fact]
    public void ViewModel_ExposesIsCancellingObservableState()
    {
        Assert.Contains("public partial bool IsCancelling { get; set; }", MainViewModelSource);
    }

    [Fact]
    public void CancelAsync_MarksCancelling_OnlyWhenSearching()
    {
        string cancel = ExtractMethodWindow(MainViewModelSource, "public Task CancelAsync()", 400);
        Assert.Contains("if (IsSearching) IsCancelling = true;", cancel);
        Assert.Contains("_cts?.Cancel();", cancel);
    }

    [Fact]
    public void CancelSemanticTranslation_MarksCancelling_WhenTranslating()
    {
        string cancel = ExtractMethodWindow(MainViewModelSource, "public void CancelSemanticTranslation()", 400);
        Assert.Contains("if (IsTranslatingSemanticQuery) IsCancelling = true;", cancel);
        Assert.Contains("_semanticCts?.Cancel();", cancel);
    }

    [Fact]
    public void CancellingState_ResetsWhenRunEnds()
    {
        // Ending a file scan clears the flag (unless a translation is still winding down)...
        string onSearching = ExtractMethodWindow(MainViewModelSource, "partial void OnIsSearchingChanged(bool value)", 800);
        Assert.Contains("if (!IsTranslatingSemanticQuery) IsCancelling = false;", onSearching);

        // ...and ending the translation clears it too (unless a real scan is still running).
        string onTranslating = ExtractMethodWindow(
            MainViewModelSource, "partial void OnIsTranslatingSemanticQueryChanged(bool value)", 800);
        Assert.Contains("if (!value && !IsSearching) IsCancelling = false;", onTranslating);
    }

    [Fact]
    public void MorphHandler_ReactsToIsCancelling_ShowsCancelingAndDisables()
    {
        // The morph handler must listen for IsCancelling changes in addition to IsSearching/translation.
        Assert.Contains("e.PropertyName != nameof(ViewModel.IsCancelling)", MainWindowXamlCsSource);

        // While busy, a cancelling run shows the "Canceling.." label and disables the button.
        Assert.Contains("bool cancelling = ViewModel.IsCancelling;", MainWindowXamlCsSource);
        Assert.Contains("SearchCancelLabel.Text = cancelling ? \"Canceling..\" : \"Cancel\";", MainWindowXamlCsSource);
        Assert.Contains("SearchCancelButton.IsEnabled = !cancelling;", MainWindowXamlCsSource);

        // Back to idle, the button is re-enabled so it can start the next search.
        Assert.Contains("SearchCancelButton.IsEnabled = true;", MainWindowXamlCsSource);
    }

    // Immediate search-start feedback: the Search button must morph to Cancel and the progress bar must
    // appear the instant a search is initiated, WITHOUT waiting for the multi-second pre-search gate work
    // (e.g. content-index journal replay) that runs before IsSearching flips. This is driven by a separate
    // IsPreparingSearch flag; these pins guard that wiring end-to-end.
    [Fact]
    public void ViewModel_ExposesPreparingSearchStateAndActiveAggregate()
    {
        Assert.Contains("public partial bool IsPreparingSearch { get; set; }", MainViewModelSource);
        Assert.Contains("public bool IsSearchActive => IsSearching || IsPreparingSearch;", MainViewModelSource);
    }

    [Fact]
    public void ViewModel_ExposesPreparationLifecycleAndCancellation()
    {
        Assert.Contains("public CancellationToken BeginSearchPreparation()", MainViewModelSource);
        Assert.Contains("public void EndSearchPreparation()", MainViewModelSource);
        Assert.Contains("public void CancelSearchPreparation()", MainViewModelSource);
        Assert.Contains("public bool IsSearchPreparationCancellationRequested", MainViewModelSource);

        // Begin shows the preparing state; End clears it (and the Canceling.. flag when no scan is running).
        string begin = ExtractMethodWindow(MainViewModelSource, "public CancellationToken BeginSearchPreparation()", 300);
        Assert.Contains("IsPreparingSearch = true;", begin);
        string end = ExtractMethodWindow(MainViewModelSource, "public void EndSearchPreparation()", 300);
        Assert.Contains("IsPreparingSearch = false;", end);
        Assert.Contains("if (!IsSearching) IsCancelling = false;", end);
    }

    [Fact]
    public void ResetStateForNewSearch_HandsOffFromPreparingToSearching()
    {
        // When the scan actually commits, IsSearching takes over and the preparing flag is cleared.
        string reset = ExtractMethodWindow(MainViewModelSource, "private void ResetStateForNewSearch()", 3200);
        int searching = reset.IndexOf("IsSearching = true;", StringComparison.Ordinal);
        int preparing = reset.IndexOf("IsPreparingSearch = false;", StringComparison.Ordinal);
        Assert.True(searching >= 0 && preparing > searching,
            "ResetStateForNewSearch must set IsSearching = true and then clear IsPreparingSearch.");
    }

    [Fact]
    public void SubmitPath_ShowsFeedbackBeforeGates_AndClearsInFinally()
    {
        // The submit chokepoint must begin preparation (immediate feedback) before the gate work runs,
        // guard against a re-entrant initiation, and end preparation in a finally.
        Assert.Contains("if (ViewModel.IsPreparingSearch)", SlowSemanticModelSource);
        int begin = SlowSemanticModelSource.IndexOf("ViewModel.BeginSearchPreparation();", StringComparison.Ordinal);
        int offer = SlowSemanticModelSource.IndexOf("await MaybeOfferSemanticSuggestionAsync();", StringComparison.Ordinal);
        int end = SlowSemanticModelSource.IndexOf("ViewModel.EndSearchPreparation();", StringComparison.Ordinal);
        Assert.True(begin >= 0 && offer > begin, "BeginSearchPreparation must run before the pre-search offers/gates.");
        Assert.True(end > offer, "EndSearchPreparation must run in the finally, after the gate chain.");
    }

    [Fact]
    public void MorphHandler_TreatsPreparingAsBusy()
    {
        Assert.Contains("e.PropertyName != nameof(ViewModel.IsPreparingSearch)", MainWindowXamlCsSource);
        Assert.Contains(
            "bool busy = ViewModel.IsSearching || ViewModel.IsTranslatingSemanticQuery || ViewModel.IsPreparingSearch;",
            MainWindowXamlCsSource);
    }

    [Fact]
    public void CancelClick_AbortsPreparation_BeforeTheScanStarts()
    {
        string click = ExtractMethodWindow(SearchInputSource, "private async void OnSearchCancelClick", 800);
        Assert.Contains("if (ViewModel.IsPreparingSearch)", click);
        Assert.Contains("ViewModel.CancelSearchPreparation();", click);
    }

    [Fact]
    public void PreSearchGates_HonorPreparationCancellation()
    {
        Assert.Contains("if (ViewModel.IsSearchPreparationCancellationRequested) return false;", SearchInputSource);
    }

    [Fact]
    public void ProgressOverlay_ShowsDuringPreparation_AsIndeterminate()
    {
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsSearchActive, Mode=OneWay}\"", MainWindowXamlSource);
        Assert.Contains("IsIndeterminate=\"{x:Bind ViewModel.SearchProgressIndeterminate, Mode=OneWay}\"", MainWindowXamlSource);
    }

    private static string ExtractMethodWindow(string source, string marker, int window)
    {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Marker '{marker}' not found in source.");
        int end = Math.Min(source.Length, index + window);
        return source[index..end];
    }

    private static string Root => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.slnx).");
    }
}
