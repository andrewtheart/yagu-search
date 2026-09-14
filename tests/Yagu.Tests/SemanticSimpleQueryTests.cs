using Yagu.Models;
using Yagu.Services.Ai;

namespace Yagu.Tests;

public sealed class SemanticSimpleQueryTests
{
    [Theory]
    [InlineData("all pdf files", "*.pdf")]
    [InlineData("all png files", "*.png")]
    [InlineData("every markdown file", "*.md")]
    [InlineData("javascript files", "*.js")]
    [InlineData("text files", "*.txt")]
    [InlineData("show typescript files", "*.ts")]
    [InlineData("find all log files", "*.log")]
    public void Resolve_FileListingsUseCurrentScopeAndOnlyTheRequestedType(string query, string glob)
    {
        Assert.True(SemanticSimpleQuery.TryBuildPlan(query, out var plan));
        var resolved = SemanticPlanApplier.Resolve(plan!, new SemanticTranslationContext { OriginalQuery = query, DefaultDirectory = @"C:\current" });

        Assert.Equal(@"C:\current", resolved.Directory);
        Assert.Equal(SearchMode.FileNames, resolved.SearchMode);
        Assert.Equal(".", resolved.Pattern);
        Assert.True(resolved.UseRegex);
        Assert.Equal(new[] { glob }, resolved.IncludeGlobs);
        Assert.Null(resolved.SearchImageText);
    }

    [Theory]
    [InlineData("files with the word invoice in them", "invoice")]
    [InlineData("any file that has a password in it", "password")]
    [InlineData("documents mentioning budget", "budget")]
    [InlineData("code files containing a TODO comment", "TODO")]
    [InlineData("python files mentioning logs", "logs")]
    [InlineData("files containing 'phone'", "phone")]
    public void Resolve_ContentRequestsKeepTheUsersLiteralTerm(string query, string term)
    {
        Assert.True(SemanticSimpleQuery.TryBuildPlan(query, out var plan));
        var resolved = SemanticPlanApplier.Resolve(plan!,
            new SemanticTranslationContext { OriginalQuery = query });
        Assert.Equal(term, resolved.Pattern);
        Assert.False(resolved.UseRegex);
        Assert.Equal(SearchMode.Content, resolved.SearchMode);
    }

    [Fact]
    public void TryBuildPlan_QuotedPhrasesRemainOneLiteralPhrase()
    {
        Assert.True(SemanticSimpleQuery.TryBuildPlan("text files containing 'connection refused'", out var plan));
        Assert.Equal("connection refused", plan!.Pattern);
        Assert.True(plan.ExactMatch);
        Assert.False(plan.UseRegex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pdf")]
    [InlineData("all files")]
    [InlineData("python files modified today")]
    [InlineData("python files on C:")]
    [InlineData("python files containing error but not warning")]
    [InlineData("files containing ' '")]
    [InlineData("files containing foo'bar")]
    [InlineData("unknown files")]
    public void TryBuildPlan_LeavesPartialOrAmbiguousInterpretationsToTheModel(string? query)
    {
        Assert.False(SemanticSimpleQuery.TryBuildPlan(query, out var plan));
        Assert.Null(plan);
    }
}