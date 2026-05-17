using Yagu.Models;

namespace Yagu.Tests;

public class SearchOptionsTests
{
    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(1, 16, 1)]
    [InlineData(2, 16, 8)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 16, 32)]
    [InlineData(4, 16, 16)]
    [InlineData(99, 16, 0)]
    public void ResolveContentSearchParallelism_MapsSettingIndexToWorkerCount(int index, int processorCount, int expectedParallelism)
    {
        Assert.Equal(expectedParallelism, SearchOptions.ResolveContentSearchParallelism(index, processorCount));
    }

    [Fact]
    public void ExactMatch_DefaultsToTrue()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x" };
        Assert.True(opts.ExactMatch);
    }

    [Fact]
    public void ObeyGitignore_DefaultsToFalse()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x" };
        Assert.False(opts.ObeyGitignore);
    }

    [Fact]
    public void GitignoreTakesPrecedence_DefaultsToTrue()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x" };
        Assert.True(opts.GitignoreTakesPrecedence);
    }

    [Fact]
    public void ExactMatch_CanBeSetToFalse()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x", ExactMatch = false };
        Assert.False(opts.ExactMatch);
    }

    [Fact]
    public void ObeyGitignore_CanBeSetToTrue()
    {
        var opts = new SearchOptions { Directory = ".", Query = "x", ObeyGitignore = true };
        Assert.True(opts.ObeyGitignore);
    }
}
