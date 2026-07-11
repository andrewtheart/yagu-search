namespace Yagu.Tests;

/// <summary>
/// Source-pins for the inline calculator / unit-converter wiring, which lives in WinUI/VM-coupled
/// files that are not compiled into Yagu.Tests. The pure evaluation logic is covered by
/// <see cref="InlineCalculatorTests"/>.
/// </summary>
public sealed class InlineCalculatorRegressionTests
{
    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void ViewModel_EvaluatesCalculatorOnQueryChange()
    {
        string src = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.cs");

        // OnQueryChanged re-evaluates the inline calculator.
        AssertContainsInOrder(src,
            "partial void OnQueryChanged(string value)",
            "OnPropertyChanged(nameof(HasQueryText));",
            "UpdateInlineCalculatorResult(value);");

        // The banner text and copy value are driven by the helper, gated to Traditional mode.
        Assert.Contains("public partial string InlineCalculatorResultText { get; set; }", src);
        Assert.Contains("public string InlineCalculatorCopyValue { get; private set; }", src);
        Assert.Contains("InlineCalculatorResultVisibility", src);
        AssertContainsInOrder(src,
            "private void UpdateInlineCalculatorResult(string? query)",
            "IsSemanticQueryMode ? null : Yagu.Helpers.InlineCalculator.Evaluate(query)");
    }

    [Fact]
    public void ViewModel_ClearsCalculatorWhenSwitchingToSemanticMode()
    {
        string src = ReadSource("src", "Yagu", "ViewModels", "MainViewModel.cs");

        AssertContainsInOrder(src,
            "partial void OnIsSemanticQueryModeChanged(bool value)",
            "UpdateInlineCalculatorResult(Query);");
    }

    [Fact]
    public void MainWindowXaml_HasCalculatorBannerBoundToViewModel()
    {
        string xaml = ReadSource("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml");

        Assert.Contains("x:Name=\"InlineCalculatorBanner\"", xaml);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.InlineCalculatorResultVisibility, Mode=OneWay}\"", xaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.InlineCalculatorResultText, Mode=OneWay}\"", xaml);
        Assert.Contains("Click=\"OnCopyInlineCalculatorResult\"", xaml);
    }

    [Fact]
    public void SearchInput_CopyHandlerCopiesTheAnswer()
    {
        string src = ReadSource("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs");

        AssertContainsInOrder(src,
            "private void OnCopyInlineCalculatorResult(object sender, RoutedEventArgs e)",
            "ViewModel.InlineCalculatorCopyValue",
            "SetClipboardText(value, \"calculator result\");");
    }

    [Fact]
    public void CliRunner_HasCalcFlagAndShortCircuit()
    {
        string src = ReadSource("src", "Yagu", "CliRunner.cs");

        Assert.Contains("public string?          CalcExpression { get; private set; }", src);
        Assert.Contains("TryGetVal(raw, ref i, out v, \"--calc\")", src);
        AssertContainsInOrder(src,
            "if (!string.IsNullOrWhiteSpace(args.CalcExpression))",
            "return RunCalc(args.CalcExpression!);");
        AssertContainsInOrder(src,
            "private static int RunCalc(string expression)",
            "Yagu.Helpers.InlineCalculator.Evaluate(expression)",
            "Console.Out.WriteLine(result.Display);");
    }

    [Fact]
    public void HelpText_DocumentsCalcFlag()
    {
        string src = ReadSource("src", "Yagu", "CliRunner.cs");
        Assert.Contains("--calc <expr>", src);

        string help = ReadSource("HELP.md");
        Assert.Contains("`--calc \"<expr>\"`", help);
        Assert.Contains("Inline calculator & unit converter", help);
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
