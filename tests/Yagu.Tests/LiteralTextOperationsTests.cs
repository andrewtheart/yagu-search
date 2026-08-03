using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class LiteralTextOperationsTests
{
    [Theory]
    [InlineData("one two one", "one", 2)]
    [InlineData("aaaaaaa", "aaa", 2)]
    [InlineData("nothing", "needle", 0)]
    [InlineData("", "needle", 0)]
    [InlineData("anything", "", 0)]
    public void CountNonOverlapping_CountsExpectedMatches(string text, string needle, long expected)
    {
        using var reader = new StringReader(text);
        Assert.Equal(expected, LiteralTextOperations.CountNonOverlapping(
            reader, needle, StringComparison.Ordinal, bufferLength: 4));
    }

    [Fact]
    public void CountNonOverlapping_PreservesMatchAcrossBufferBoundary()
    {
        using var reader = new StringReader("xxNEEDLExxNEEDLE");
        Assert.Equal(2, LiteralTextOperations.CountNonOverlapping(
            reader, "NEEDLE", StringComparison.Ordinal, bufferLength: 3));
    }

    [Fact]
    public void CountNonOverlapping_DoesNotDoubleCountSelfOverlapAcrossBoundary()
    {
        using var reader = new StringReader("aaaaaaaa");
        Assert.Equal(2, LiteralTextOperations.CountNonOverlapping(
            reader, "aaaa", StringComparison.Ordinal, bufferLength: 3));
    }

    [Fact]
    public void CountNonOverlapping_HonorsComparisonMode()
    {
        using var reader = new StringReader("Needle nEeDlE NEEDLE");
        Assert.Equal(3, LiteralTextOperations.CountNonOverlapping(
            reader, "needle", StringComparison.OrdinalIgnoreCase, bufferLength: 5));
    }

    [Fact]
    public void CountNonOverlapping_RejectsInvalidBufferLength()
    {
        using var reader = new StringReader("text");
        Assert.Throws<ArgumentOutOfRangeException>(() => LiteralTextOperations.CountNonOverlapping(
            reader, "t", StringComparison.Ordinal, bufferLength: 0));
    }
}
