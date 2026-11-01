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

    [Theory]
    [InlineData("pdfs containing invoice but not refund")]
    [InlineData("images containing invoice but not refund")]
    [InlineData("archives containing invoice but not refund")]
    public void AScopeWhoseTextOnlyExistsAfterExtraction_DeclinesTheShortcut(string query)
    {
        // This shortcut regex-matches the file's raw bytes, so it can never see PDF-extracted, OCR'd or
        // archived text. Taking it for a scope made entirely of those types would answer "no matches"
        // for a search that should have matched; declining hands the query to the plan path, which
        // turns the corresponding extraction toggle on instead.
        Assert.False(SemanticWholeFileExclusion.TryBuildPlan(query, out _));
    }

    [Fact]
    public void AScopeMixingPlainTextWithAContainerType_StillTakesTheShortcut()
    {
        // Declining is only correct when EVERY resolved type needs extraction, so a scope that also
        // contains a directly-readable type must still take the shortcut.
        Assert.True(SemanticFileTypes.TryResolve("code", out var globs));
        Assert.Contains(globs, g => !SemanticWholeFileExclusion.IsExtractionOnlyGlob(g));
        Assert.True(SemanticWholeFileExclusion.TryBuildPlan("code files containing invoice but not refund", out var plan));

        Assert.False(plan!.SearchInsideArchives);
        Assert.False(plan.SearchImageText);
        Assert.False(plan.SearchPdfText);
    }

    [Theory]
    [InlineData("*.pdf", true)]
    [InlineData("*.PNG", true)]
    [InlineData("*.zip", true)]
    [InlineData("*.cs", false)]
    [InlineData("*.log", false)]
    [InlineData("Makefile", false)]
    [InlineData("*.", false)]
    [InlineData("*. ", false)]
    public void AGlobIsExtractionOnly_OnlyWhenItNamesATypeThisShortcutCannotRead(string glob, bool expected)
    {
        // An entry with no extension at all ("Makefile") or a trailing dot must classify as readable
        // rather than throwing or being treated as a container.
        Assert.Equal(expected, SemanticWholeFileExclusion.IsExtractionOnlyGlob(glob));
    }
}