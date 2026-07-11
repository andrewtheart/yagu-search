namespace Yagu.Tests;

/// <summary>
/// Source-pins for narrowing the query/pattern history dropdown so its right edge lines up with the
/// "Match case" (Aa) toggle instead of running the full box width under the overlaid toggle strip.
/// The behavior lives in the WinUI-coupled <c>MainWindow.SearchInput.cs</c> (not compiled into
/// Yagu.Tests), so it is validated by source-pin.
/// </summary>
public sealed class QuerySuggestionListWidthRegressionTests
{
    private static readonly string SearchInputSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));

    [Fact]
    public void OpenChangedHandler_SchedulesTheWidthConstraintForTheQueryBox()
    {
        // The modal force-close must still run first, then a Low-priority constraint for QueryBox only.
        AssertContainsInOrder(SearchInputSource,
            "private void OnInputSuggestionListOpenChanged(DependencyObject sender, DependencyProperty dp)",
            "box.IsSuggestionListOpen = false;",
            "return;",
            "if (box.IsSuggestionListOpen && ReferenceEquals(box, QueryBox))",
            "DispatcherQueuePriority.Low,",
            "ConstrainQuerySuggestionListWidth);");
    }

    [Fact]
    public void ConstrainWidth_TargetsTheMatchCaseToggleLeftEdgeAndClampsThePopupCard()
    {
        AssertContainsInOrder(SearchInputSource,
            "private void ConstrainQuerySuggestionListWidth()",
            // Only when the toggle strip is actually overlaid (Traditional mode).
            "InlineSearchToggles.Visibility != Visibility.Visible || CaseSensitiveToggle.ActualWidth <= 0",
            // Target width = left edge of the Match-case (Aa) toggle relative to the box.
            "CaseSensitiveToggle",
            ".TransformToVisual(QueryBox)",
            "_querySuggestionTargetWidth = aaLeft;",
            // Match this box's own popup, then clamp its card.
            "GetOpenPopupsForXamlRoot(xamlRoot)",
            "IsDescendantOf(popup, QueryBox)",
            "ApplyQuerySuggestionCardWidth(card);");
    }

    [Fact]
    public void ClampSetsMaxWidthAndReAppliesIfTheFrameworkWidensItBack()
    {
        AssertContainsInOrder(SearchInputSource,
            "private void ApplyQuerySuggestionCardWidth(FrameworkElement card)",
            "card.MinWidth = 0;",
            "card.MaxWidth = _querySuggestionTargetWidth;");

        // A SizeChanged belt re-clamps when the framework re-pins the card to the full box width.
        AssertContainsInOrder(SearchInputSource,
            "private void OnQuerySuggestionCardSizeChanged(object sender, SizeChangedEventArgs e)",
            "card.ActualWidth > _querySuggestionTargetWidth + 0.5",
            "ApplyQuerySuggestionCardWidth(card);");
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int position = 0;
        foreach (var item in expected)
        {
            int found = text.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
