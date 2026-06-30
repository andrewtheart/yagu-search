using Yagu.Services.Ocr;

namespace Yagu.Tests;

/// <summary>
/// Covers <see cref="OcrAssetPaths"/>: the presence heuristics that decide whether the bundled OCR
/// payload satisfies an engine (so no download is needed) and the directory resolution that points
/// the worker at the bundled payload when present, otherwise the per-user cache. Uses temp
/// directories laid out the same way the installer stages the payload and the worker writes its
/// cache.
/// </summary>
public sealed class OcrAssetPathsTests
{
    [Fact]
    public void BundledRoot_IsOcrPayloadBesideApp()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "ocr-payload"),
            OcrAssetPaths.BundledRoot);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1024 * 1024, 1)]
    [InlineData(1024 * 1024 + 1, 2)]
    public void ToApproxMb_RoundsUp(long bytes, int expectedMb)
    {
        Assert.Equal(expectedMb, OcrAssetPaths.ToApproxMb(bytes));
    }

    [Fact]
    public void ApproxByteConstants_AreOrderedNativeGreatestTesseractLeast()
    {
        Assert.True(OcrAssetPaths.PaddleNativeApproxBytes > OcrAssetPaths.PaddleModelApproxBytes);
        Assert.True(OcrAssetPaths.PaddleModelApproxBytes > OcrAssetPaths.TesseractDataApproxBytes);
    }

    [Fact]
    public void PaddleNativePresent_FalseWhenDirMissing()
    {
        Assert.False(OcrAssetPaths.PaddleNativePresent(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void PaddleNativePresent_RequiresBothProbeDlls()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "paddle_inference_c.dll"), "x");
        Assert.False(OcrAssetPaths.PaddleNativePresent(dir.Path));

        File.WriteAllText(Path.Combine(dir.Path, "OpenCvSharpExtern.dll"), "x");
        Assert.True(OcrAssetPaths.PaddleNativePresent(dir.Path));
    }

    [Fact]
    public void PaddleModelsPresent_RequiresDetRecClsEachWithWeights()
    {
        using var dir = new TempDir();

        // Only det + rec → still incomplete.
        WriteModel(dir.Path, "en_PP-OCRv4_det");
        WriteModel(dir.Path, "en_PP-OCRv4_rec");
        Assert.False(OcrAssetPaths.PaddleModelsPresent(dir.Path));

        // Add the angle classifier → complete.
        WriteModel(dir.Path, "ch_ppocr_mobile_v2.0_cls");
        Assert.True(OcrAssetPaths.PaddleModelsPresent(dir.Path));
    }

    [Fact]
    public void PaddleModelsPresent_FalseWhenWeightsFileMissing()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "en_PP-OCRv4_det"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "en_PP-OCRv4_rec"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ch_ppocr_mobile_v2.0_cls"));
        // No inference.pdiparams in any → not usable.
        Assert.False(OcrAssetPaths.PaddleModelsPresent(dir.Path));
    }

    [Fact]
    public void TesseractDataPresent_ChecksEngTrainedData()
    {
        using var dir = new TempDir();
        Assert.False(OcrAssetPaths.TesseractDataPresent(dir.Path));

        File.WriteAllText(Path.Combine(dir.Path, "eng.traineddata"), "x");
        Assert.True(OcrAssetPaths.TesseractDataPresent(dir.Path));
    }

    [Fact]
    public void BuildPaddleRequirement_AllPresent_NoDownload()
    {
        var req = OcrAssetPaths.BuildPaddleRequirement("PaddleSharp", nativePresent: true, modelsPresent: true);

        Assert.Equal("PaddleSharp", req.EngineDisplayName);
        Assert.False(req.DownloadNeeded);
        Assert.Equal(0, req.ApproxBytes);
        Assert.Empty(req.MissingComponents);
    }

    [Fact]
    public void BuildPaddleRequirement_NativeMissingOnly_ListsRuntime()
    {
        var req = OcrAssetPaths.BuildPaddleRequirement("PaddleSharp", nativePresent: false, modelsPresent: true);

        Assert.True(req.DownloadNeeded);
        Assert.Equal(OcrAssetPaths.PaddleNativeApproxBytes, req.ApproxBytes);
        Assert.Single(req.MissingComponents);
        Assert.Contains("OCR engine runtime", req.MissingComponents[0]);
    }

    [Fact]
    public void BuildPaddleRequirement_ModelsMissingOnly_ListsModels()
    {
        var req = OcrAssetPaths.BuildPaddleRequirement("PaddleSharp", nativePresent: true, modelsPresent: false);

        Assert.True(req.DownloadNeeded);
        Assert.Equal(OcrAssetPaths.PaddleModelApproxBytes, req.ApproxBytes);
        Assert.Single(req.MissingComponents);
        Assert.Contains("language models", req.MissingComponents[0]);
    }

    [Fact]
    public void BuildPaddleRequirement_BothMissing_SumsBytesAndListsBoth()
    {
        var req = OcrAssetPaths.BuildPaddleRequirement("PaddleSharp", nativePresent: false, modelsPresent: false);

        Assert.True(req.DownloadNeeded);
        Assert.Equal(
            OcrAssetPaths.PaddleNativeApproxBytes + OcrAssetPaths.PaddleModelApproxBytes,
            req.ApproxBytes);
        Assert.Equal(2, req.MissingComponents.Count);
    }

    [Fact]
    public void BuildTesseractRequirement_DataPresent_NoDownload()
    {
        var req = OcrAssetPaths.BuildTesseractRequirement("Tesseract", dataPresent: true);

        Assert.Equal("Tesseract", req.EngineDisplayName);
        Assert.False(req.DownloadNeeded);
        Assert.Equal(0, req.ApproxBytes);
        Assert.Empty(req.MissingComponents);
    }

    [Fact]
    public void BuildTesseractRequirement_DataMissing_ListsEnglishData()
    {
        var req = OcrAssetPaths.BuildTesseractRequirement("Tesseract", dataPresent: false);

        Assert.True(req.DownloadNeeded);
        Assert.Equal(OcrAssetPaths.TesseractDataApproxBytes, req.ApproxBytes);
        Assert.Single(req.MissingComponents);
        Assert.Contains("English language data", req.MissingComponents[0]);
    }

    [Fact]
    public void DirResolvers_PreferBundledPayloadWhenPresent()
    {
        // The bundled-payload arm of the dir resolvers only triggers when the payload sits beside the
        // app (AppContext.BaseDirectory\ocr-payload). The clean test host has none, so stage one,
        // assert each resolver points at it, then remove it (no payload → resolvers fall back to cache).
        using var payload = new BundledPayload();

        Assert.True(OcrAssetPaths.PaddleRuntimeDir().StartsWith(OcrAssetPaths.BundledRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(OcrAssetPaths.PaddleModelDir().StartsWith(OcrAssetPaths.BundledRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(OcrAssetPaths.TesseractDataDir().StartsWith(OcrAssetPaths.BundledRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DirResolvers_FallBackToCacheWhenNoBundledPayload()
    {
        // No payload beside the app → resolvers return the per-user cache root under LocalAppData.
        Assert.False(Directory.Exists(OcrAssetPaths.BundledRoot), "test host must not ship a bundled OCR payload");
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yagu", "ocr-runtime");

        Assert.StartsWith(cacheRoot, OcrAssetPaths.PaddleRuntimeDir(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(cacheRoot, OcrAssetPaths.PaddleModelDir(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(cacheRoot, OcrAssetPaths.TesseractDataDir(), StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteModel(string modelsDir, string folderName)
    {
        string sub = Path.Combine(modelsDir, folderName);
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "inference.pdiparams"), "x");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yagu-ocr-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Stages a complete bundled OCR payload beside the test host (the location the OCR-bundled
    /// installer ships) so the dir resolvers take their bundled-payload arm, and removes it on dispose.
    /// </summary>
    private sealed class BundledPayload : IDisposable
    {
        private readonly string _root = OcrAssetPaths.BundledRoot;

        public BundledPayload()
        {
            string native = System.IO.Path.Combine(_root, "paddle", "native");
            Directory.CreateDirectory(native);
            File.WriteAllText(System.IO.Path.Combine(native, "paddle_inference_c.dll"), "x");
            File.WriteAllText(System.IO.Path.Combine(native, "OpenCvSharpExtern.dll"), "x");

            string models = System.IO.Path.Combine(_root, "paddle", "models");
            foreach (string m in new[] { "en_PP-OCRv4_det", "en_PP-OCRv4_rec", "ch_ppocr_mobile_v2.0_cls" })
            {
                string sub = System.IO.Path.Combine(models, m);
                Directory.CreateDirectory(sub);
                File.WriteAllText(System.IO.Path.Combine(sub, "inference.pdiparams"), "x");
            }

            string tessdata = System.IO.Path.Combine(_root, "tesseract", "tessdata");
            Directory.CreateDirectory(tessdata);
            File.WriteAllText(System.IO.Path.Combine(tessdata, "eng.traineddata"), "x");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
