using Yagu.Services.Ai;

namespace Yagu.Tests;

public sealed class SemanticFileTypesTests
{
    [Theory]
    [InlineData("python files", "*.py")]
    [InlineData("log files", "*.log")]
    [InlineData("logs", "*.log")]
    [InlineData("objective-c files", "*.m")]
    [InlineData("typescript files", "*.ts")]
    [InlineData("yaml files", "*.yaml")]
    [InlineData("  PowerShell\t scripts ", "*.ps1")]
    public void TryResolve_KeepsSingleTypesNarrow(string scope, string extension)
    {
        Assert.True(SemanticFileTypes.TryResolve(scope, out var globs));
        Assert.Equal(new[] { extension }, globs);
    }

    [Fact]
    public void TryResolve_WordDocumentsDoNotIncludeGenericDocumentFormats()
    {
        Assert.True(SemanticFileTypes.TryResolve("word documents", out var globs));
        Assert.Equal(new[] { "*.docx", "*.doc" }, globs);
        globs.Clear();
        Assert.True(SemanticFileTypes.TryResolve("word documents", out globs));
        Assert.Equal(2, globs.Count);
    }

    [Theory]
    [InlineData("documents", "*.pdf")]
    [InlineData("images", "*.jpg")]
    [InlineData("photos", "*.jpg")]
    [InlineData("pictures", "*.jpg")]
    [InlineData("videos", "*.mp4")]
    [InlineData("movies", "*.mp4")]
    [InlineData("music", "*.mp3")]
    [InlineData("code", "*.cs")]
    public void TryResolve_NormalizesEveryCollectionAlias(string scope, string expectedGlob)
    {
        Assert.True(SemanticFileTypes.TryResolve(scope, out var globs));
        Assert.Contains(expectedGlob, globs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("python files modified today")]
    [InlineData("files in C:\\private")]
    [InlineData("strange files")]
    public void TryResolve_RejectsUnknownOrPartiallyUnderstoodScopes(string? scope)
    {
        Assert.False(SemanticFileTypes.TryResolve(scope, out var globs));
        Assert.Empty(globs);
    }

    [Fact]
    public void TryResolve_UnqualifiedFilesHaveNoTypeFilter()
    {
        Assert.True(SemanticFileTypes.TryResolve("files", out var globs));
        Assert.Empty(globs);
    }
}