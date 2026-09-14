using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// A glob typed into the search box in file-name mode.
/// </summary>
/// <remarks>
/// Windows file names cannot contain <c>*</c> or <c>?</c>, so matching such a query as text is a
/// guaranteed-empty search — which is exactly what made <c>*.ps1</c> look broken. Routing it to the
/// include filter is only safe while the conditions below hold, so each one is pinned here.
/// </remarks>
public class FileNameGlobQueryTests
{
    private static bool Route(
        string? query,
        SearchMode mode = SearchMode.FileNames,
        bool useRegex = false,
        IReadOnlyList<string>? existingIncludes = null) =>
        FileNameGlobQuery.TryRoute(mode, useRegex, query, existingIncludes, out _);

    private static IReadOnlyList<string> Globs(string query)
    {
        Assert.True(FileNameGlobQuery.TryRoute(SearchMode.FileNames, false, query, null, out var globs));
        return globs;
    }

    [Theory]
    [InlineData("*.ps1")]
    [InlineData("*.PS1")]
    [InlineData("report*")]
    [InlineData("?ata.txt")]
    [InlineData("**/bin/*.dll")]
    public void AGlobQueryInFileNameMode_IsRoutedToTheIncludeFilter(string query)
    {
        Assert.True(Route(query));
    }

    [Fact]
    public void TheRoutedGlobIsTheQueryItself()
    {
        Assert.Equal(["*.ps1"], Globs("*.ps1"));
    }

    [Fact]
    public void SeveralGlobs_AreAllRouted()
    {
        Assert.Equal(["*.ps1", "*.psm1"], Globs("*.ps1 *.psm1"));
        Assert.Equal(["*.ps1", "*.psm1"], Globs("*.ps1;*.psm1"));
    }

    [Fact]
    public void SurroundingWhitespace_IsTrimmed()
    {
        Assert.Equal(["*.ps1"], Globs("   *.ps1   "));
    }

    [Theory]
    [InlineData("report")]
    [InlineData("TODO")]
    [InlineData("readme.md")]
    public void OrdinaryText_IsLeftAlone(string query)
    {
        Assert.False(Route(query));
    }

    [Fact]
    public void AMixedQuery_IsLeftAlone()
    {
        // "named report AND a pdf" cannot be expressed by an OR-combined include list, so guessing
        // here would quietly change what the user asked for.
        Assert.False(Route("report *.pdf"));
    }

    [Fact]
    public void RegexMode_IsLeftAlone()
    {
        // With the regex toggle on, * and ? are the user's own syntax.
        Assert.False(Route("*.ps1", useRegex: true));
        Assert.False(Route(".*\\.ps1$", useRegex: true));
    }

    [Theory]
    [InlineData(SearchMode.Content)]
    [InlineData(SearchMode.Both)]
    public void ContentSearches_AreLeftAlone(SearchMode mode)
    {
        // A content search for "*.ps1" is a legitimate literal search of file contents.
        Assert.False(Route("*.ps1", mode: mode));
    }

    [Fact]
    public void AnExistingIncludeFilter_SuppressesRouting()
    {
        // Include patterns combine as OR, so adding to them would widen a search the user was
        // narrowing. Leave their filter as the single source of truth.
        Assert.False(Route("*.ps1", existingIncludes: ["*.cs"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyQuery_IsNotRouted(string? query)
    {
        Assert.False(Route(query));
    }

    [Theory]
    [InlineData(";")]
    [InlineData(";;\t ")]
    public void SeparatorOnlyQuery_IsNotRoutedAndReturnsNoGlobs(string query)
    {
        Assert.False(FileNameGlobQuery.TrySplitGlobTokens(query, out var globs));
        Assert.Empty(globs);
        Assert.False(Route(query));
        Assert.False(FileNameGlobQuery.LooksLikeGlob(query));
    }

    [Fact]
    public void TheMatchAllPattern_IsAValidRegexThatMatchesAnyName()
    {
        // The query is replaced with this so the run still enumerates names; an empty query would
        // make SearchService return immediately.
        Assert.False(string.IsNullOrWhiteSpace(FileNameGlobQuery.MatchAllPattern));
        Assert.Matches(FileNameGlobQuery.MatchAllPattern, "anything.ps1");
    }

    [Fact]
    public void TheRoutingMessage_NamesEveryGlobApplied()
    {
        string message = FileNameGlobQuery.DescribeRouting(["*.ps1", "*.psm1"]);

        // Routing changes what was searched, so it must never be silent.
        Assert.Contains("*.ps1", message);
        Assert.Contains("*.psm1", message);
    }

    // ── The offer made where routing deliberately stands down ──

    [Theory]
    [InlineData("*.ps1")]
    [InlineData("report*")]
    [InlineData("*.ps1 *.psm1")]
    public void AGlobShapedQuery_IsRecognisedEvenWhenItIsNotRouted(string query)
    {
        Assert.True(FileNameGlobQuery.LooksLikeGlob(query));
    }

    [Theory]
    [InlineData("report")]
    [InlineData(@".*\.ps1$")]
    [InlineData("report *.pdf")]
    [InlineData(null)]
    public void OrdinaryAndValidRegexQueries_AreNotMistakenForGlobs(string? query)
    {
        Assert.False(FileNameGlobQuery.LooksLikeGlob(query));
    }

    [Fact]
    public void AnInvalidRegexThatIsReallyAGlob_SaysSo()
    {
        string message = FileNameGlobQuery.DescribeInvalidRegex("*.ps1", "quantifier '*' following nothing.");

        // The bare parser message describes the failure without naming what the user was trying to do.
        Assert.Contains("Invalid regex", message);
        Assert.Contains("quantifier", message);
        Assert.Contains("*.ps1", message);
        Assert.Contains("file glob", message);
    }

    [Fact]
    public void AnOrdinaryInvalidRegex_IsReportedWithoutAGlobGuess()
    {
        string message = FileNameGlobQuery.DescribeInvalidRegex("(unclosed", "Not enough close parens.");

        Assert.Contains("Invalid regex", message);
        Assert.DoesNotContain("file glob", message);
    }

    [Fact]
    public void TheEngineReportsInvalidRegexThroughTheSharedDescription()
    {
        string service = Read("src", "Yagu", "Services", "SearchService.cs");

        Assert.Contains("FileNameGlobQuery.DescribeInvalidRegex(options.Query, ex.Message)", service);
    }

    [Fact]
    public void TheFailedSearch_OffersTheFileNameSearchItWasReallyAskingFor()
    {
        string viewModel = MainViewModelPartials.Text;

        // Raised on the failure, and cleared when the next search starts so it cannot outlive its error.
        Assert.Contains("UpdateGlobFileNameSuggestion(Query);", viewModel);
        Assert.Contains("GlobFileNameSuggestion = null;", viewModel);

        // Suppressed while an include filter is set, because routing stands down in that case and the
        // offer would promise a search it cannot run.
        Assert.Contains("string.IsNullOrWhiteSpace(IncludeGlobs)", viewModel);

        // The fix moves the two toggles and leaves the typed query alone — no synthesized pattern is
        // written into the box the user is looking at.
        Assert.Contains("UseRegex = false;", viewModel);
        Assert.Contains("SearchModeIndex = (int)SearchMode.FileNames;", viewModel);
    }

    [Fact]
    public void TheOfferIsAButtonBesideTheError()
    {
        string xaml = Read("src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml");

        Assert.Contains("ViewModel.HasGlobFileNameSuggestion", xaml);
        Assert.Contains("ViewModel.UseGlobAsFileNameSearchCommand", xaml);
        Assert.Contains("ViewModel.GlobFileNameSuggestionLabel", xaml);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRepoRoot(), .. parts]));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.slnx)");
    }
}
