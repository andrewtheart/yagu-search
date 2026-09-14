namespace Yagu.Tests;

/// <summary>
/// Source-pins for wrapping the greyed-out placeholder hint inside the tall query box. WinUI's
/// TextBox template hands the hint to a <c>ContentControl</c> with no TextWrapping, so a long hint —
/// notably the semantic-mode example — stayed on one line and was clipped however tall the box was.
/// The behavior lives in WinUI-coupled code that Yagu.Tests does not compile, so it is source-pinned.
/// </summary>
public sealed class PlaceholderHintWrapRegressionTests
{
    private static readonly string StylerSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "PlaceholderTextStyler.cs"));

    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

    [Fact]
    public void WrapIsOptIn_SoSingleLineHintsStillEllipsize()
    {
        AssertContainsInOrder(StylerSource,
            "\"Wrap\",",
            "typeof(bool),",
            "typeof(PlaceholderTextStyler),",
            "new PropertyMetadata(false, OnStyleChanged));");
    }

    [Fact]
    public void WrappingTemplateCarriesTextWrappingAndRebindsWhenTheHintChanges()
    {
        // A plain string Content cannot wrap, and replacing Content outright would freeze the hint
        // on whichever mode was active first, so the wrap has to arrive as a re-bindable template.
        AssertContainsInOrder(StylerSource,
            "private static DataTemplate WrappedPlaceholder",
            "XamlReader.Load(",
            "<TextBlock Text=\\\"{Binding}\\\" TextWrapping=\\\"Wrap\\\" /></DataTemplate>");
    }

    [Fact]
    public void ContentControlCaseIsMatchedBeforeControl_AndStretchesSoTheHintCanWrap()
    {
        // ContentControl derives from Control, so a Control-first switch would swallow the part and
        // the hint would keep its single line. Stretch stops the templated TextBlock measuring
        // unbounded, which would defeat TextWrapping on its own.
        AssertContainsInOrder(StylerSource,
            "case ContentControl content:",
            "if (wrap)",
            "content.HorizontalContentAlignment = HorizontalAlignment.Stretch;",
            "content.ContentTemplate = WrappedPlaceholder;",
            "case Control control:");
    }

    [Fact]
    public void ItalicAndWrapShareOneLoadedHook_SoBothSurviveALateTemplatePass()
    {
        AssertContainsInOrder(StylerSource,
            "if (GetItalic(fe) || GetWrap(fe))",
            "fe.Loaded += OnLoaded;",
            "root.DispatcherQueue?.TryEnqueue(() => TryApply(root));");
    }

    [Fact]
    public void QueryBoxOptsIntoTheWrappingHint()
    {
        AssertContainsInOrder(MainWindowXaml,
            "x:Name=\"QueryBox\"",
            "ui:PlaceholderTextStyler.Wrap=\"True\"",
            "PlaceholderText=\"{x:Bind ViewModel.QueryPlaceholderText, Mode=OneWay}\"");
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
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
