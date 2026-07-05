using Yagu.Helpers;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Runtime tests for <see cref="SemanticQueryHeuristicDetector"/>, the conservative heuristic that flags
/// a Traditional-mode query reading like a natural-language request meant for AI (Semantic) search.
/// </summary>
public sealed class SemanticQueryHeuristicDetectorTests
{
    [Theory]
    [InlineData("files on c containing the word \"test\"")]
    [InlineData("files on C: containing the word test")]
    [InlineData("show me my recent invoices")]
    [InlineData("find all logs from yesterday")]
    [InlineData("documents modified last week")]
    [InlineData("search for budget spreadsheets")]
    [InlineData("folders named backup created this month")]
    public void LooksLikeSemanticQuery_FlagsNaturalLanguageRequests(string query)
    {
        Assert.True(SemanticQueryHeuristicDetector.LooksLikeSemanticQuery(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("test")]
    [InlineData("public static void")]
    [InlineData("TODO fix later")]
    [InlineData("public static void main")]
    [InlineData("the quick brown fox")]
    [InlineData("\"files on c containing the word test\"")] // fully quote-wrapped = explicit literal
    public void LooksLikeSemanticQuery_IgnoresLiteralOrShortQueries(string? query)
    {
        Assert.False(SemanticQueryHeuristicDetector.LooksLikeSemanticQuery(query));
    }

    [Fact]
    public void LooksLikeSemanticQuery_RequiresTwoWeakCues()
    {
        // A single weak cue is not enough (conservative — avoids nagging on ordinary literal searches).
        Assert.False(SemanticQueryHeuristicDetector.LooksLikeSemanticQuery("report containing budget"));
        // Two weak cues together cross the threshold.
        Assert.True(SemanticQueryHeuristicDetector.LooksLikeSemanticQuery("documents containing budget numbers"));
    }
}
