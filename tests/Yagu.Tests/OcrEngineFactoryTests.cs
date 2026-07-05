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
    public void Create_WithModelAndMaxSide_ThreadsThemIntoPaddleWorkerEnvironment()
    {
        var engine = OcrEngineFactory.Create("paddle", "ChineseV5", maxSide: 1536);

        var paddle = Assert.IsType<PaddleOcrEngine>(engine);
        var env = new Dictionary<string, string?>();
        paddle.ConfigureWorkerEnvironmentForTest(env);

        Assert.Equal("ChineseV5", env[PaddleOcrEngine.ModelEnvVar]);
        Assert.Equal("1536", env[PaddleOcrEngine.MaxSideEnvVar]);
    }

    [Fact]
    public void Create_WithNegativeMaxSide_OmitsMaxSideEnvVar()
    {
        // The 1-arg Create overload uses maxSide = -1 (unspecified) so the worker default is kept.
        var engine = OcrEngineFactory.Create("paddle");

        var paddle = Assert.IsType<PaddleOcrEngine>(engine);
        var env = new Dictionary<string, string?>();
        paddle.ConfigureWorkerEnvironmentForTest(env);

        Assert.False(env.ContainsKey(PaddleOcrEngine.MaxSideEnvVar));
    }

    [Fact]
    public void Create_TesseractIgnoresModelAndMaxSide()
    {
        var engine = OcrEngineFactory.Create("tesseract", "ChineseV5", maxSide: 1536);

        Assert.Equal(OcrEngineFactory.TesseractId, engine.Id);
        Assert.IsType<TesseractOcrEngine>(engine);
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
