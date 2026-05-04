namespace Yagu.Tests;

/// <summary>
/// Regression tests verifying preview section Expander layout properties.
/// These are source-level checks to catch UI regressions that cause content
/// to be centered instead of left-aligned inside preview sections.
/// </summary>
public class PreviewSectionLayoutTests
{
    private static readonly string MainWindowSource = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "Yagu", "MainWindow.xaml.cs"));

    [Fact]
    public void AddPreviewSection_Expander_HasHorizontalContentAlignmentStretch()
    {
        // Regression: without HorizontalContentAlignment = Stretch, short content
        // (e.g. a file with only 1 match) gets centered in the Expander instead of
        // left-aligned, pushing line numbers to the middle of the panel.
        var expanderBlock = ExtractMethodBody(MainWindowSource, "AddPreviewSection");
        Assert.Contains("HorizontalContentAlignment", expanderBlock);
        Assert.Contains("HorizontalAlignment.Stretch", expanderBlock);
    }

    [Fact]
    public void AddPreviewSection_Expander_HasHorizontalAlignmentStretch()
    {
        var expanderBlock = ExtractMethodBody(MainWindowSource, "AddPreviewSection");
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Stretch", expanderBlock);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        // Find the method definition (not a call site) by looking for the return type prefix
        string marker = $"RichTextBlock {methodName}(";
        int idx = source.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Fallback: try any private method with this name
            marker = $"private ";
            int search = 0;
            while (true)
            {
                int pos = source.IndexOf(marker, search, StringComparison.Ordinal);
                if (pos < 0) break;
                int namePos = source.IndexOf(methodName + "(", pos, Math.Min(200, source.Length - pos), StringComparison.Ordinal);
                if (namePos >= 0) { idx = pos; break; }
                search = pos + 1;
            }
        }
        Assert.True(idx >= 0, $"Method definition '{methodName}' not found in MainWindow.xaml.cs");

        // Take a generous window from the method signature to capture the full body
        int end = Math.Min(source.Length, idx + 5000);
        return source[idx..end];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}
