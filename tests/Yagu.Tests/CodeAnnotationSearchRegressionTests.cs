namespace Yagu.Tests;

/// <summary>
/// Source-pins for the "Find code annotations" (TODO/FIXME…) quick-search wiring across the VM, XAML,
/// and CLI. The canonical pattern itself is covered by <see cref="CodeAnnotationQueryTests"/>.
/// </summary>
public sealed class CodeAnnotationSearchRegressionTests
{
    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void ViewModel_ApplyPresetLoadsTheCanonicalRegexInTraditionalMode()
    {
        string src = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.cs");

        AssertContainsInOrder(src,
            "public void ApplyCodeAnnotationPreset()",
            "IsSemanticQueryMode = false;",
            "UseRegex = true;",
            "Query = Yagu.Helpers.CodeAnnotationQuery.Pattern;");
    }

    [Fact]
    public void MainWindowXaml_HasQuickSearchButton()
    {
        string xaml = ReadSource("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml");

        Assert.Contains("x:Name=\"FindCodeAnnotationsButton\"", xaml);
        Assert.Contains("Click=\"OnFindCodeAnnotations\"", xaml);
    }

    [Fact]
    public void SearchInput_HandlerAppliesPresetThenSearches()
    {
        string src = ReadSource("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");

        AssertContainsInOrder(src,
            "private async void OnFindCodeAnnotations(object sender, RoutedEventArgs e)",
            "ViewModel.ApplyCodeAnnotationPreset();",
            "await StartSearchFromUiAsync();");
    }

    [Fact]
    public void CliRunner_HasTodosFlagUsingSharedPattern()
    {
        string src = ReadSource("src", "Yagu", "CliRunner.cs");

        AssertContainsInOrder(src,
            "if (Eq(tok, \"--todos\", \"--code-annotations\"))",
            "a.Pattern = Yagu.Helpers.CodeAnnotationQuery.Pattern;",
            "a.UseRegex = true;",
            "a.CaseSensitive = true;");
    }

    [Fact]
    public void HelpText_DocumentsTodosFlag()
    {
        string src = ReadSource("src", "Yagu", "CliRunner.cs");
        Assert.Contains("--todos", src);

        string help = ReadSource("HELP.md");
        Assert.Contains("`--todos`", help);
        Assert.Contains("Find code annotations", help);
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
