using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services.Ai;

namespace Yagu.Tests;

public sealed class SemanticWholeFileExclusionTests
{
    [Theory]
    [InlineData("log files with error but without warning", "*.log", "error", "warning")]
    [InlineData("python files containing import but not test", "*.py", "import", "test")]
    [InlineData("c# files that contain 'async' but not 'Legacy'", "*.cs", "async", "Legacy")]
    [InlineData("find text files mentioning 'total.cost' but not 'refund+fee'", "*.txt", "total.cost", "refund+fee")]
    public void Resolve_TypeScopedExclusionChecksTheEntireFile(string query, string glob, string required, string excluded)
    {
        var resolved = SemanticPlanApplier.Resolve(new SemanticSearchPlan { ExcludeGlobs = ["*"], Pattern = "wrong" },
            new SemanticTranslationContext { OriginalQuery = query, DefaultDirectory = @"C:\scope" });

        Assert.Equal(new[] { glob }, resolved.IncludeGlobs);
        Assert.Null(resolved.ExcludeGlobs);
        Assert.Equal(@"C:\scope", resolved.Directory);
        Assert.True(resolved.Multiline);
        var regex = new Regex(resolved.Pattern!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        Assert.Matches(regex, $"before\n{required}\nafter");
        Assert.DoesNotMatch(regex, $"{excluded}\n{required}");
        Assert.DoesNotMatch(regex, $"{required}\n{excluded}");
        Assert.DoesNotMatch(regex, "unrelated");
    }

    [Theory]
    [InlineData("strange files containing a but not b")]
    [InlineData("files in another folder containing a but not b")]
    [InlineData("files named a but not b")]
    [InlineData("python files containing a but not b modified today")]
    public void TryBuildPlan_DoesNotDiscardUnknownConstraints(string query)
        => Assert.False(SemanticWholeFileExclusion.TryBuildPlan(query, out _));
}