using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="SemanticQuerySalvage"/> — the deterministic best-guess translator used
/// when the local model returns no usable plan. Each case pins a query the model face-planted on in the
/// eval (or a representative shape) to the file-type / content / OCR / hidden / folder intent the
/// salvage must recover, plus the negative cases where it should decline (so the caller falls back to a
/// literal search).
/// </summary>
public sealed class SemanticQuerySalvageTests
{
    [Theory]
    [InlineData("powershell scripts", "*.ps1")]
    [InlineData("shell scripts", "*.sh")]
    [InlineData("python files", "*.py")]
    [InlineData("zip files", "*.zip")]
    [InlineData("zip archives", "*.zip")]
    [InlineData("f# scripts", "*.fs")]
    [InlineData("c# files", "*.cs")]
    [InlineData("log files", "*.log")]
    [InlineData("text files", "*.txt")]
    public void TryBuildPlan_TypeWord_RecoversGlob(string query, string expectedGlob)
    {
        Assert.True(SemanticQuerySalvage.TryBuildPlan(query, out var plan));
        Assert.NotNull(plan.IncludeGlobs);
        Assert.Contains(expectedGlob, plan.IncludeGlobs!);
    }

    [Fact]
    public void TryBuildPlan_ImageFiles_RecoversImageGlobSet()
    {
        Assert.True(SemanticQuerySalvage.TryBuildPlan("image files", out var plan));
        Assert.NotNull(plan.IncludeGlobs);
        Assert.Contains("*.png", plan.IncludeGlobs!);
        Assert.Contains("*.jpg", plan.IncludeGlobs!);
    }

    [Fact]
    public void TryBuildPlan_JpgContainingWordSecret_RecoversGlobContentAndOcr()
    {
        // The exact query phi-mini deterministically fails on.
        Assert.True(SemanticQuerySalvage.TryBuildPlan("jpg files containing the word secret", out var plan));
        Assert.NotNull(plan.IncludeGlobs);
        Assert.Contains("*.jpg", plan.IncludeGlobs!);
        Assert.Equal("secret", plan.Pattern);
        Assert.Equal("content", plan.SearchMode);
        Assert.True(plan.SearchImageText);   // image + content term -> OCR
    }

    [Fact]
    public void TryBuildPlan_PythonContainingQuotedTerm_RecoversGlobAndTerm()
    {
        Assert.True(SemanticQuerySalvage.TryBuildPlan("python files containing 'TODO'", out var plan));
        Assert.Contains("*.py", plan.IncludeGlobs!);
        Assert.Equal("TODO", plan.Pattern);
        Assert.Null(plan.SearchImageText);   // not an image type
    }

    [Fact]
    public void TryBuildPlan_WordDocuments_MapsToWordFormatsNotGenericDocs()
    {
        Assert.True(SemanticQuerySalvage.TryBuildPlan("word documents mentioning budget", out var plan));
        Assert.Contains("*.docx", plan.IncludeGlobs!);
        Assert.Equal("budget", plan.Pattern);
    }

    [Fact]
    public void TryBuildPlan_MyDesktop_RecoversKnownFolder()
    {
        Assert.True(SemanticQuerySalvage.TryBuildPlan("files on my desktop", out var plan));
        Assert.Equal(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), plan.Directory);
    }

    [Fact]
    public void TryBuildPlan_HiddenFiles_RecoversHiddenPreference()
    {
        Assert.True(SemanticQuerySalvage.TryBuildPlan("show hidden files", out var plan));
        Assert.True(plan.SearchHidden);
    }

    [Theory]
    [InlineData("config stuff")]           // no file noun, no content cue, no folder
    [InlineData("something about")]        // dangling cue, nothing to capture
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuildPlan_NothingSalvageable_ReturnsFalse(string query)
        => Assert.False(SemanticQuerySalvage.TryBuildPlan(query, out _));

    [Fact]
    public void TryBuildPlan_NullQuery_ReturnsFalse()
        => Assert.False(SemanticQuerySalvage.TryBuildPlan(null, out _));
}
