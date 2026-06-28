using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class OcrEngineFactoryTests
{
    [Theory]
    [InlineData("paddle")]
    [InlineData("PADDLE")]
    [InlineData("paddleocr")]
    [InlineData("paddlesharp")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something-unknown")]
    public void Create_DefaultsToPaddle(string? engineId)
    {
        var engine = OcrEngineFactory.Create(engineId);
        Assert.Equal(OcrEngineFactory.PaddleId, engine.Id);
        Assert.Equal("PaddleSharp", engine.DisplayName);
    }

    [Theory]
    [InlineData("tesseract")]
    [InlineData("Tesseract")]
    [InlineData("  tesseract  ")]
    public void Create_ResolvesTesseract(string engineId)
    {
        var engine = OcrEngineFactory.Create(engineId);
        Assert.Equal(OcrEngineFactory.TesseractId, engine.Id);
        Assert.Equal("Tesseract", engine.DisplayName);
    }

    [Fact]
    public async Task TesseractEngine_DegradesGracefully_WhenWorkerMissing()
    {
        // Authoritative bogus worker path → the engine must report "not installed" instead of
        // probing the standard locations (which would risk launching the real downloader worker).
        await using var engine = new TesseractOcrEngine(
            workerPathOverride: @"C:\does-not-exist\Yagu.OcrWorker.exe");

        Assert.Equal(OcrEngineFactory.TesseractId, engine.Id);

        OcrResult ready = await engine.EnsureReadyAsync(CancellationToken.None);
        Assert.False(ready.Success);
        Assert.False(string.IsNullOrWhiteSpace(ready.Error));

        OcrResult recognized = await engine.RecognizeAsync("x.png", CancellationToken.None);
        Assert.False(recognized.Success);
    }

    [Fact]
    public async Task PaddleEngine_DegradesGracefully_WhenWorkerMissing()
    {
        // Authoritative bogus worker path → the engine must report "not installed" instead of
        // probing the standard locations (which would risk launching the real downloader worker).
        await using var engine = new PaddleOcrEngine(modelName: null,
            workerPathOverride: @"C:\does-not-exist\Yagu.OcrWorker.exe");

        Assert.Equal(OcrEngineFactory.PaddleId, engine.Id);

        OcrResult ready = await engine.EnsureReadyAsync(CancellationToken.None);
        Assert.False(ready.Success);
        Assert.False(string.IsNullOrWhiteSpace(ready.Error));

        OcrResult recognized = await engine.RecognizeAsync("x.png", CancellationToken.None);
        Assert.False(recognized.Success);
    }
}
