using System.Text.RegularExpressions;

namespace Yagu.Tests;

/// <summary>
/// Pins the App.xaml CheckBox theme-brush recipe that permanently prevents a two-state
/// CheckBox from ever painting an indeterminate "dash" glyph.
///
/// Root cause: WinUI can momentarily drop a two-state CheckBox into the indeterminate
/// VISUAL state when its implicit style/template is re-applied (IsChecked resets to its
/// bool? default). The default template paints that state using the
/// CheckBoxCheckGlyphForegroundIndeterminate* / CheckBoxCheckBackgroundFillIndeterminate*
/// theme brushes. If those brushes are visible, a stray dash appears. C#-side IsChecked /
/// GoToState corrections cannot reliably win the race, so the fix lives at the brush level:
/// the indeterminate brushes are made identical to the unchecked brushes and the glyph
/// foreground is made invisible. No CheckBox in the app uses three-state intentionally.
/// </summary>
public sealed class CheckBoxIndeterminateGlyphRegressionTests
{
    private static string AppXaml() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "App.xaml"));

    [Fact]
    public void IndeterminateGlyphForeground_IsTransparent_InDefaultAndLightThemes()
    {
        string xaml = AppXaml();

        // Every Light/Default CheckBoxCheckGlyphForegroundIndeterminate* brush must be
        // fully transparent (#00000000) so a phantom indeterminate state cannot draw a dash.
        var matches = Regex.Matches(
            xaml,
            "<SolidColorBrush x:Key=\"CheckBoxCheckGlyphForegroundIndeterminate[A-Za-z]*\" Color=\"([^\"]+)\" />");

        Assert.True(matches.Count >= 8, $"Expected at least 8 indeterminate glyph brushes, found {matches.Count}.");
        foreach (Match m in matches)
        {
            Assert.Equal("#00000000", m.Groups[1].Value);
        }
    }

    [Fact]
    public void IndeterminateBoxFill_MatchesUncheckedTransparent_InDefaultAndLightThemes()
    {
        string xaml = AppXaml();

        // The primary indeterminate fill must be transparent (identical to the unchecked
        // fill) so the box never appears "filled" when the phantom state triggers.
        var fills = Regex.Matches(
            xaml,
            "<SolidColorBrush x:Key=\"CheckBoxCheckBackgroundFillIndeterminate\" Color=\"([^\"]+)\" />");

        Assert.True(fills.Count >= 2, $"Expected the indeterminate fill in both Default and Light themes, found {fills.Count}.");
        foreach (Match m in fills)
        {
            Assert.Equal("#00000000", m.Groups[1].Value);
        }
    }

    [Fact]
    public void IndeterminateGlyph_IsNotAccentColored_Anywhere()
    {
        string xaml = AppXaml();

        // Guard against the old recipe coming back: the indeterminate glyph must NOT use the
        // accent/dark glyph colors that produced the visible dash.
        Assert.DoesNotContain(
            "<SolidColorBrush x:Key=\"CheckBoxCheckGlyphForegroundIndeterminate\" Color=\"#0B3141\" />",
            xaml);
        Assert.DoesNotContain(
            "<SolidColorBrush x:Key=\"CheckBoxCheckGlyphForegroundIndeterminate\" Color=\"#062635\" />",
            xaml);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
