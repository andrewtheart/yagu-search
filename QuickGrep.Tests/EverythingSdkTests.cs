using QuickGrep.Native;

namespace QuickGrep.Tests;

public class EverythingSdkCoverageTests
{
    [Theory]
    [InlineData(0u, "OK")]
    [InlineData(1u, "Out of memory")]
    [InlineData(2u, "Everything is not running")]
    [InlineData(3u, "Unable to register window class")]
    [InlineData(4u, "Unable to create listening window")]
    [InlineData(5u, "Unable to create listening thread")]
    [InlineData(6u, "Invalid index")]
    [InlineData(7u, "Invalid call")]
    [InlineData(8u, "Invalid request data")]
    [InlineData(9u, "Invalid parameter")]
    public void ErrorMessage_KnownCodes(uint code, string expected)
    {
        Assert.Equal(expected, EverythingSdk.ErrorMessage(code));
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(99u)]
    [InlineData(uint.MaxValue)]
    public void ErrorMessage_UnknownCode(uint code)
    {
        var msg = EverythingSdk.ErrorMessage(code);
        Assert.Contains("Unknown error", msg);
        Assert.Contains(code.ToString(), msg);
    }
}
