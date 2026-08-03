using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Yagu.Services.Index;
using Yagu.Services.Ocr;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ImageOcrExtractorFingerprint"/> — builds the exact OCR configuration fingerprint.
/// The worker binary is content-hashed (a swapped worker invalidates the fingerprint); Tesseract pins the
/// model/maxSide options to "fixed" while Paddle records the real values; and a missing/unhashable worker
/// yields a null fingerprint so old positive postings are never trusted.
/// </summary>
public sealed class ImageOcrExtractorFingerprintTests
{
    private static string ExpectedHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Runs <paramref name="body"/> with YAGU_OCR_WORKER set to <paramref name="value"/>, then restores it.</summary>
    private static void WithWorkerEnv(string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, previous);
        }
    }

    [Fact]
    public void TryCompute_TesseractEngine_PinsOptionsToFixed_AndHashesWorker()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "tesseract-worker-bytes");
        try
        {
            WithWorkerEnv(worker, () =>
            {
                ExtractorFingerprint? fp = ImageOcrExtractorFingerprint.TryCompute(OcrEngineFactory.TesseractId, "IgnoredModel", 4096);

                Assert.NotNull(fp);
                Assert.Equal(SpecialSourceKind.ImageOcr, fp!.Source);
                Assert.Equal(OcrEngineFactory.TesseractId, fp.EngineId);
                Assert.Equal(string.Empty, fp.EngineVersion);
                Assert.Equal("cpu", fp.Runtime);

                ExtractorFileHash bin = Assert.Single(fp.BinaryHashes);
                Assert.Equal("worker", bin.Role);
                Assert.Equal(ExpectedHash(worker), bin.Sha256);

                Assert.Contains(fp.Options, o => o.Key == "model" && o.Value == "fixed");
                Assert.Contains(fp.Options, o => o.Key == "maxSide" && o.Value == "fixed");
            });
        }
        finally
        {
            File.Delete(worker);
        }
    }

    [Fact]
    public void TryCompute_PaddleEngine_RecordsModelAndMaxSide()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "paddle-worker-bytes");
        try
        {
            WithWorkerEnv(worker, () =>
            {
                ExtractorFingerprint? fp = ImageOcrExtractorFingerprint.TryCompute(OcrEngineFactory.PaddleId, "  EnglishV4  ", 1600);

                Assert.NotNull(fp);
                Assert.Equal(OcrEngineFactory.PaddleId, fp!.EngineId);
                Assert.Contains(fp.Options, o => o.Key == "model" && o.Value == "EnglishV4");
                Assert.Contains(fp.Options, o => o.Key == "maxSide" && o.Value == "1600");
            });
        }
        finally
        {
            File.Delete(worker);
        }
    }

    [Fact]
    public void TryCompute_UnknownEngineWithBlankModel_NormalizesToPaddleChineseV5()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "default-worker-bytes");
        try
        {
            WithWorkerEnv(worker, () =>
            {
                ExtractorFingerprint? fp = ImageOcrExtractorFingerprint.TryCompute("some-other-engine", model: null, maxSide: 900);

                Assert.NotNull(fp);
                Assert.Equal(OcrEngineFactory.PaddleId, fp!.EngineId);
                Assert.Contains(fp.Options, o => o.Key == "model" && o.Value == "ChineseV5");
                Assert.Contains(fp.Options, o => o.Key == "maxSide" && o.Value == "900");
            });
        }
        finally
        {
            File.Delete(worker);
        }
    }

    [Fact]
    public void TryCompute_MissingWorker_ReturnsNull()
    {
        string missing = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        WithWorkerEnv(missing, () =>
            Assert.Null(ImageOcrExtractorFingerprint.TryCompute(OcrEngineFactory.PaddleId, "ChineseV5", 1024)));
    }

    [Fact]
    public void TryCompute_UnreadableWorker_ReturnsNull()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-locked-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "locked");
        try
        {
            // Hold the file open denying read so File.OpenRead inside TryHashFile throws IOException.
            using FileStream exclusive = new(worker, FileMode.Open, FileAccess.Write, FileShare.None);
            WithWorkerEnv(worker, () =>
                Assert.Null(ImageOcrExtractorFingerprint.TryCompute(OcrEngineFactory.PaddleId, "ChineseV5", 1024)));
        }
        finally
        {
            File.Delete(worker);
        }
    }

    [Fact]
    public void ResolveWorkerPath_ConfiguredExistingFile_ReturnsIt()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "x");
        try
        {
            WithWorkerEnv(worker, () => Assert.Equal(worker, ImageOcrExtractorFingerprint.ResolveWorkerPath()));
        }
        finally
        {
            File.Delete(worker);
        }
    }

    [Fact]
    public void ResolveWorkerPath_ConfiguredMissingFile_ReturnsNull()
    {
        string missing = Path.Combine(Path.GetTempPath(), "yagu-ocr-fp-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        WithWorkerEnv(missing, () => Assert.Null(ImageOcrExtractorFingerprint.ResolveWorkerPath()));
    }

    [Fact]
    public void ResolveWorkerPath_NoEnv_FallsBackToBundledExistenceCheck()
    {
        WithWorkerEnv(null, () =>
        {
            string? resolved = ImageOcrExtractorFingerprint.ResolveWorkerPath();
            // The bundled worker may or may not exist beside the test host; either way the result must be
            // internally consistent: null, or an existing path.
            Assert.True(resolved is null || File.Exists(resolved));
        });
    }

    [Fact]
    public void ResolveWorkerPath_NoEnv_ReturnsBundledWorkerWhenPresent()
    {
        string bundled = WorkerOcrEngine.ResolveBundledWorkerPath(AppContext.BaseDirectory);
        string dir = Path.GetDirectoryName(bundled)!;
        bool createdDir = !Directory.Exists(dir);
        bool createdFile = !File.Exists(bundled);
        if (createdDir)
            Directory.CreateDirectory(dir);
        if (createdFile)
            File.WriteAllText(bundled, "stub-worker");
        try
        {
            WithWorkerEnv(null, () => Assert.Equal(bundled, ImageOcrExtractorFingerprint.ResolveWorkerPath()));
        }
        finally
        {
            if (createdFile)
                File.Delete(bundled);
            if (createdDir)
                Directory.Delete(dir);
        }
    }
}
