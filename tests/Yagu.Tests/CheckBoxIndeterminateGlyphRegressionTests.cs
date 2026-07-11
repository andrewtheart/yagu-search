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

    [Fact]
    public void FileGroupCheckBox_HasIndeterminateCorrectionHandler()
    {
        // The circular per-file-group selection checkbox lives in a virtualizing ListView item
        // container. On container recycle the template can re-apply and transiently reset
        // IsChecked (bool?) to null, sticking the indeterminate "dash". The App.xaml transparent
        // brushes alone did NOT stop it (the box only rendered the phantom because the state
        // stuck), so the checkbox MUST also wire the Indeterminate event that snaps it back —
        // the only hook raised exactly when IsChecked becomes null, whenever that happens.
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

        int idx = mainWindowXaml.IndexOf(
            "AutomationProperties.AutomationId=\"FileGroupCheckBox\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, "FileGroupCheckBox not found in MainWindow.xaml.");
        string checkBoxDecl = mainWindowXaml.Substring(idx, Math.Min(500, mainWindowXaml.Length - idx));
        Assert.Contains("Indeterminate=\"OnFileGroupCheckBoxIndeterminate\"", checkBoxDecl);
    }

    [Fact]
    public void OnFileGroupCheckBoxIndeterminate_SnapsBackToGroupSelectionState()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.PreviewSections.cs"));

        int idx = source.IndexOf("private void OnFileGroupCheckBoxIndeterminate(", StringComparison.Ordinal);
        Assert.True(idx >= 0, "OnFileGroupCheckBoxIndeterminate handler not found.");
        string handler = source.Substring(idx, Math.Min(600, source.Length - idx));

        // It must coerce the box straight back to the group's real selection state, reusing the
        // shared setter (which also re-asserts the correct visual state).
        Assert.Contains("checkBox.DataContext is FileGroup group && group.AllSelected", handler);
        Assert.Contains("SetFileGroupCheckBoxState(checkBox, desired);", handler);
    }

    [Fact]
    public void CircularSelectionCheckBox_TemplateHasNoDashElement_AndIsAppliedToSelectionBoxes()
    {
        // DEFINITIVE fix for the recurring phantom "dash": the content-less circular selection
        // checkboxes (per-file-group header + toolbar select-all) use a custom ControlTemplate
        // whose Indeterminate visual state is EMPTY, rendering identically to Unchecked. Because
        // the template has no dash element at all, a phantom indeterminate state cannot draw one,
        // regardless of when/how IsChecked becomes null on container recycle. This does not depend
        // on theme brushes or a C# GoToState race.
        string appXaml = AppXaml();

        Assert.Contains("<Style x:Key=\"CircularSelectionCheckBoxStyle\" TargetType=\"CheckBox\">", appXaml);

        // The template must define the CheckStates and its Indeterminate visual state must be an
        // EMPTY visual state (no setters) so it can never paint a glyph/fill.
        AssertContainsInOrder(appXaml,
            "<Style x:Key=\"CircularSelectionCheckBoxStyle\" TargetType=\"CheckBox\">",
            "<VisualStateGroup x:Name=\"CheckStates\">",
            "<VisualState x:Name=\"Checked\">",
            "<Setter Target=\"CheckGlyph.Opacity\" Value=\"1\" />",
            "<VisualState x:Name=\"Unchecked\" />",
            "<VisualState x:Name=\"Indeterminate\" />");

        // The check glyph starts hidden (Opacity=0) and is only revealed by the Checked state.
        Assert.Contains("<FontIcon x:Name=\"CheckGlyph\" Glyph=\"&#xE73E;\" FontSize=\"12\" Opacity=\"0\"", appXaml);

        // Both content-less selection checkboxes must use the no-dash template.
        string mainWindowXaml = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

        int fileGroupIdx = mainWindowXaml.IndexOf(
            "AutomationProperties.AutomationId=\"FileGroupCheckBox\"", StringComparison.Ordinal);
        Assert.True(fileGroupIdx >= 0, "FileGroupCheckBox not found.");
        Assert.Contains("Style=\"{StaticResource CircularSelectionCheckBoxStyle}\"",
            mainWindowXaml.Substring(fileGroupIdx, Math.Min(500, mainWindowXaml.Length - fileGroupIdx)));

        int selectAllIdx = mainWindowXaml.IndexOf(
            "x:Name=\"SelectAllFilesCheckBox\"", StringComparison.Ordinal);
        Assert.True(selectAllIdx >= 0, "SelectAllFilesCheckBox not found.");
        Assert.Contains("Style=\"{StaticResource CircularSelectionCheckBoxStyle}\"",
            mainWindowXaml.Substring(selectAllIdx, Math.Min(500, mainWindowXaml.Length - selectAllIdx)));
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int offset = 0;
        foreach (var token in expected)
        {
            int idx = text.IndexOf(token, offset, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"Expected to find '{token}' after offset {offset}.");
            offset = idx + token.Length;
        }
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
