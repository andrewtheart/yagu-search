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
}
