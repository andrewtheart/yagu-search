using System.Security.Cryptography;
using Yagu.Services.Ocr;

namespace Yagu.Services.Index;

/// <summary>Builds the exact configuration fingerprint for a positive-only image OCR namespace.
/// OCR nonmembers are never pruned, but a matching fingerprint is still required before old positive
/// postings are used to prioritize work.</summary>
public static class ImageOcrExtractorFingerprint
{
    public static ExtractorFingerprint? TryCompute(string engineId, string? model, int maxSide)
    {
        string normalizedEngine = string.Equals(engineId, OcrEngineFactory.TesseractId, StringComparison.OrdinalIgnoreCase)
            ? OcrEngineFactory.TesseractId
            : OcrEngineFactory.PaddleId;
        string normalizedModel = string.IsNullOrWhiteSpace(model) ? "ChineseV5" : model.Trim();
        string? workerPath = ResolveWorkerPath();
        string? workerHash = TryHashFile(workerPath);
        if (workerHash is null)
            return null;

        return new ExtractorFingerprint(
            SpecialSourceKind.ImageOcr,
            engineId: normalizedEngine,
            engineVersion: string.Empty,
            runtime: "cpu",
            binaryHashes: [new ExtractorFileHash("worker", workerHash)],
            options:
            [
                new("model", normalizedEngine == OcrEngineFactory.PaddleId ? normalizedModel : "fixed"),
                new("maxSide", normalizedEngine == OcrEngineFactory.PaddleId ? maxSide.ToString(System.Globalization.CultureInfo.InvariantCulture) : "fixed"),
            ]);
    }

    internal static string? ResolveWorkerPath()
    {
        string? configured = Environment.GetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        string candidate = WorkerOcrEngine.ResolveBundledWorkerPath(AppContext.BaseDirectory);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? TryHashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}