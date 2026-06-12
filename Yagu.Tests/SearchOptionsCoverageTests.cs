using Yagu.Models;

namespace Yagu.Tests;

public sealed class SearchOptionsCoverageTests
{
    [Theory]
    [InlineData(0, 8, 0)]   // Default/auto
    [InlineData(1, 8, 1)]   // Single-threaded
    [InlineData(2, 8, 4)]   // Half cores
    [InlineData(3, 8, 16)]  // 2x cores
    [InlineData(4, 8, 8)]   // All cores
    [InlineData(5, 8, 0)]   // Unknown index → default
    public void ResolveContentSearchParallelism_ReturnsExpected(int index, int processorCount, int expected)
    {
        Assert.Equal(expected, SearchOptions.ResolveContentSearchParallelism(index, processorCount));
    }

    [Fact]
    public void ResolveContentSearchParallelism_MinimumOneCore()
    {
        // Edge case: processorCount = 0 → should be clamped to at least 1
        Assert.Equal(1, SearchOptions.ResolveContentSearchParallelism(1, 0));
        Assert.Equal(1, SearchOptions.ResolveContentSearchParallelism(2, 1)); // half of 1 = max(1, 0) = 1? Actually floor(1/2)=0→max(1,0)=1? Let's verify
    }

    [Fact]
    public void SearchOptions_DefaultValues()
    {
        var options = new SearchOptions
        {
            Directory = @"C:\test",
            Query = "search",
        };
        Assert.True(options.ExactMatch);
        Assert.Equal(3, options.ContextLines);
        Assert.Equal(SearchMode.Both, options.SearchMode);
        Assert.True(options.SkipBinary);
        Assert.Equal(50_000, options.MaxResults);
        Assert.False(options.CaseSensitive);
        Assert.False(options.UseRegex);
        Assert.Equal(FilterPatternMode.GlobPath, options.IncludeFilterMode);
        Assert.Equal(FilterPatternMode.GlobPath, options.ExcludeFilterMode);
    }

    [Fact]
    public void SearchOptions_CustomValues()
    {
        var options = new SearchOptions
        {
            Directory = @"C:\code",
            Query = "TODO",
            CaseSensitive = true,
            UseRegex = true,
            ExactMatch = false,
            ContextLines = 5,
            SearchMode = SearchMode.Content,
            MinFileSizeBytes = 100,
            MaxFileSizeBytes = 1_000_000,
            MaxResults = 1000,
            MaxMatchesPerFile = 50,
            SkipBinary = false,
            MaxSearchDepth = 3,
            ObeyGitignore = true,
            MaxDegreeOfParallelism = 4,
        };
        Assert.Equal(@"C:\code", options.Directory);
        Assert.Equal("TODO", options.Query);
        Assert.True(options.CaseSensitive);
        Assert.True(options.UseRegex);
        Assert.False(options.ExactMatch);
        Assert.Equal(5, options.ContextLines);
        Assert.Equal(SearchMode.Content, options.SearchMode);
        Assert.Equal(100, options.MinFileSizeBytes);
        Assert.Equal(1_000_000, options.MaxFileSizeBytes);
        Assert.Equal(1000, options.MaxResults);
        Assert.Equal(50, options.MaxMatchesPerFile);
        Assert.False(options.SkipBinary);
        Assert.Equal(3, options.MaxSearchDepth);
        Assert.True(options.ObeyGitignore);
        Assert.Equal(4, options.MaxDegreeOfParallelism);
    }
}
