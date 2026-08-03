namespace Yagu.Tests;

/// <summary>
/// Source-pins the WinUI query box's Enter behavior. The wrapping inner TextBox can consume Enter
/// without raising AutoSuggestBox.QuerySubmitted, so MainWindow must observe handled key events and
/// route both submission events through one guarded search path.
/// </summary>
public sealed class SearchInputEnterKeyRegressionTests
{
    private static readonly string MainWindowSource = Read(
        "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml.cs");

    private static readonly string SearchInputSource = Read(
        "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");

    private static readonly string MainWindowXaml = Read(
        "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml");

    [Fact]
    public void QueryBox_ObservesHandledEnterEvents()
    {
        AssertContainsInOrder(MainWindowSource,
            "QueryBox.AddHandler(UIElement.KeyDownEvent,",
            "new KeyEventHandler(OnQueryKeyDown),",
            "handledEventsToo: true);");
    }

    [Fact]
    public void Enter_RoutesThroughTheCompleteUiSearchPath()
    {
        string handler = ExtractWindow(SearchInputSource, "private async void OnQueryKeyDown", 900);
        AssertContainsInOrder(handler,
            "if (e.Key == VirtualKey.Enter)",
            "e.Handled = true;",
            "await SubmitQueryAsync(sender as AutoSuggestBox ?? QueryBox);");

        string submit = ExtractWindow(SearchInputSource, "private async Task SubmitQueryAsync", 2600);
        AssertContainsInOrder(submit,
            "if (_querySubmitInProgress)",
            "_querySubmitInProgress = true;",
            "HideQuerySuggestions(sender);",
            "if (!await ClearPreviewPanelForNewSearchAsync()) return;",
            "CollapseAdvancedOptionsForSearch();",
            "await SubmitSearchWithSlowModelWatchAsync();",
            "_querySubmitInProgress = false;");
    }

    [Fact]
    public void QuerySubmitted_UsesTheSameGuardedPath()
    {
        string handler = ExtractWindow(SearchInputSource, "private async void OnQuerySubmitted", 700);
        Assert.Contains("await SubmitQueryAsync(sender, submittedQuery);", handler);
    }

    [Fact]
    public void QueryTextBox_DoesNotAcceptEnterAsText()
    {
        int queryBox = MainWindowXaml.IndexOf("x:Name=\"QueryBox\"", StringComparison.Ordinal);
        Assert.True(queryBox >= 0, "QueryBox was not found in MainWindow.xaml.");
        string queryXaml = MainWindowXaml[queryBox..Math.Min(MainWindowXaml.Length, queryBox + 2600)];

        Assert.Contains("<Setter Property=\"AcceptsReturn\" Value=\"False\" />", queryXaml);
        Assert.DoesNotContain("KeyDown=\"OnQueryKeyDown\"", queryXaml);
    }

    private static string ExtractWindow(string source, string marker, int length)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker '{marker}' was not found.");
        return source[start..Math.Min(source.Length, start + length)];
    }

    private static void AssertContainsInOrder(string source, params string[] expected)
    {
        int position = 0;
        foreach (string item in expected)
        {
            int found = source.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Yagu.slnx.");
    }
}
