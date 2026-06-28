using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class UnavailableOcrEngineTests
{
    [Fact]
    public void ExposesIdAndDisplayNameFromConstructor()
    {
        var engine = new UnavailableOcrEngine("paddle", "PaddleSharp", "runtime not installed");

        Assert.Equal("paddle", engine.Id);
        Assert.Equal("PaddleSharp", engine.DisplayName);
    }

    [Fact]
    public async Task EnsureReadyAsync_AlwaysFailsWithReason()
    {
        var engine = new UnavailableOcrEngine("tesseract", "Tesseract", "models missing");

        OcrResult result = await engine.EnsureReadyAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("models missing", result.Error);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task RecognizeAsync_AlwaysFailsWithReason()
    {
        var engine = new UnavailableOcrEngine("paddle", "PaddleSharp", "no worker");

        OcrResult result = await engine.RecognizeAsync(@"C:\some\image.png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("no worker", result.Error);
        Assert.Equal(string.Empty, result.Text);
    }
}
