namespace Yagu.Tests;

/// <summary>
/// Source-scraping tests for SyncGlobalIndexFromSection (global/per-file match counter sync)
/// and the simplified SearchInput filter-example logic.
/// </summary>
public sealed class MatchNavSyncTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MatchNavSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.MatchNav.cs"));
    private static readonly string SearchInputSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.SearchInput.cs"));

    // ══════════════════════════════════════════════════════════════════
    // SyncGlobalIndexFromSection
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SyncGlobalIndexFromSection_MethodExists()
    {
        Assert.Contains("private void SyncGlobalIndexFromSection(SectionMatchNav sn)", MatchNavSource);
    }

    [Fact]
    public void SyncGlobalIndexFromSection_BoundsCheckForSafety()
    {
        string method = ExtractMethodWindow("SyncGlobalIndexFromSection", 800);
        Assert.Contains("if (sn.CurrentIndex < 0 || sn.CurrentIndex >= sn.Matches.Count) return;", method);
    }

    [Fact]
    public void SyncGlobalIndexFromSection_FindsMatchInGlobalList()
    {
        string method = ExtractMethodWindow("SyncGlobalIndexFromSection", 800);
        AssertContainsInOrder(method,
            "var (para, matchInPara) = sn.Matches[sn.CurrentIndex];",
            "for (int i = 0; i < _matchParagraphs.Count; i++)",
            "var entry = _matchParagraphs[i];",
            "ReferenceEquals(entry.block, sn.Block)",
            "ReferenceEquals(entry.para, para)",
            "entry.matchInPara == matchInPara");
    }

    [Fact]
    public void SyncGlobalIndexFromSection_UpdatesCurrentMatchIndexAndLabel()
    {
        string method = ExtractMethodWindow("SyncGlobalIndexFromSection", 800);
        AssertContainsInOrder(method,
            "_currentMatchIndex = i;",
            "MatchNavLabel.Text = FormatMatchNavLabel(i);",
            "return;");
    }

    [Fact]
    public void SyncGlobalIndexFromSection_CalledFromMoveNextSectionMatch()
    {
        string moveNext = ExtractMethodWindowBetween(MatchNavSource,
            "private void OnSectionNextMatch(",
            "private void OnSectionPrevMatch(");
        Assert.Contains("SyncGlobalIndexFromSection(sn);", moveNext);
    }

    [Fact]
    public void SyncGlobalIndexFromSection_CalledFromMovePreviousSectionMatch()
    {
        string movePrev = ExtractMethodWindowBetween(MatchNavSource,
            "private void OnSectionPrevMatch(",
            "private void SyncGlobalIndexFromSection(");
        Assert.Contains("SyncGlobalIndexFromSection(sn);", movePrev);
    }

    [Fact]
    public void SyncGlobalIndexFromSection_HasXmlDocSummary()
    {
        Assert.Contains("After a section-level navigation, update the global _currentMatchIndex", MatchNavSource);
        Assert.Contains("so the top-bar \"Occurrence X/N\" stays in sync with the per-file counter.", MatchNavSource);
    }

    // ══════════════════════════════════════════════════════════════════
    // SearchInput: Simplified filter-example text logic
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void OnFilterBoxTextChanged_AlwaysSetsNormalFontStyle()
    {
        string method = ExtractMethodWindowBetween(SearchInputSource,
            "private void OnFilterBoxTextChanged(",
            "private void OnFilterBoxGotFocus(");
        Assert.Contains("tb.FontStyle = Windows.UI.Text.FontStyle.Normal;", method);
        Assert.DoesNotContain("FontStyle.Italic", method);
    }

    [Fact]
    public void OnFilterBoxTextChanged_NoItalicConditional()
    {
        string method = ExtractMethodWindowBetween(SearchInputSource,
            "private void OnFilterBoxTextChanged(",
            "private void OnFilterBoxGotFocus(");
        Assert.DoesNotContain("IsFilterExampleText", method);
        Assert.DoesNotContain("string.IsNullOrEmpty", method);
    }

    [Fact]
    public void IsFilterExampleText_DoesNotFallBackToDefaultExcludeGlobs()
    {
        string method = ExtractMethodWindowBetween(SearchInputSource,
            "private bool IsFilterExampleText(",
            "private async void OnSearchCancelClick(");
        Assert.DoesNotContain("AppSettings.DefaultExcludeGlobs", method);
        Assert.Contains("ViewModel.ExcludeFilterPlaceholder", method);
    }

    [Fact]
    public void IsFilterExampleText_ChecksIncludeAndExcludePlaceholders()
    {
        string method = ExtractMethodWindowBetween(SearchInputSource,
            "private bool IsFilterExampleText(",
            "private async void OnSearchCancelClick(");
        Assert.Contains("ViewModel.IncludeFilterPlaceholder", method);
        Assert.Contains("ViewModel.ExcludeFilterPlaceholder", method);
    }

    // ══════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════

    private string ExtractMethodWindow(string methodName, int windowSize = 600)
    {
        int start = MatchNavSource.IndexOf($"private void {methodName}(", StringComparison.Ordinal);
        if (start < 0)
            start = MatchNavSource.IndexOf($"private static void {methodName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method '{methodName}' in MatchNav source.");
        int end = Math.Min(start + windowSize, MatchNavSource.Length);
        return MatchNavSource[start..end];
    }

    private static string ExtractMethodWindowBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find start marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + 1, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find end marker '{endMarker}'.");
        return source[start..end];
    }

    private static void AssertContainsInOrder(string text, params string[] parts)
    {
        int index = 0;
        foreach (var part in parts)
        {
            int found = text.IndexOf(part, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{part}' after index {index}.");
            index = found + part.Length;
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
