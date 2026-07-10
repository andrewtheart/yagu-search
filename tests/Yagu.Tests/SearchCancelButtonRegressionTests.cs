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
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
