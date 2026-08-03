using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class OcrWorkerParallelismTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(99, 4)]
    public void Normalize_PreservesAutomaticAndClampsExplicitValues(int input, int expected)
        => Assert.Equal(expected, OcrWorkerParallelism.Normalize(input));

    [Theory]
    [InlineData("paddle", 4, 1)]
    [InlineData("paddle", 64, 1)]
    [InlineData("tesseract", 7, 1)]
    [InlineData("tesseract", 8, 2)]
    [InlineData("TESSERACT", 64, 2)]
    [InlineData(null, 64, 1)]
    public void Resolve_AutomaticIsEngineAndCpuAware(string? engine, int processors, int expected)
        => Assert.Equal(expected, OcrWorkerParallelism.Resolve(
            configured: 0,
            engine,
            processors,
            limitParallelismOnHdd: false,
            isHardDisk: false));

    [Theory]
    [InlineData("paddle")]
    [InlineData("tesseract")]
    public void Resolve_HddSafeguardOverridesAutomaticAndExplicitValues(string engine)
    {
        Assert.Equal(1, OcrWorkerParallelism.Resolve(0, engine, 32, true, true));
        Assert.Equal(1, OcrWorkerParallelism.Resolve(4, engine, 32, true, true));
        Assert.Equal(4, OcrWorkerParallelism.Resolve(4, engine, 32, false, true));
    }

    [Fact]
    public void Resolve_ExplicitValueIsHonoredOnNonHddRoot()
        => Assert.Equal(3, OcrWorkerParallelism.Resolve(3, "paddle", 1, true, false));
}
