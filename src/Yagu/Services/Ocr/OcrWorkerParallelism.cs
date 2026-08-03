namespace Yagu.Services.Ocr;

/// <summary>Normalizes and resolves the number of independent OCR worker processes used by one
/// image-text search. Each lane owns a separate process/model instance; this is deliberately much
/// more conservative than ordinary file-scan parallelism because one OCR process can consume
/// hundreds of megabytes and Paddle/oneDNN already uses internal CPU parallelism.</summary>
public static class OcrWorkerParallelism
{
    public const int Automatic = 0;
    public const int Minimum = 1;
    public const int Maximum = 4;

    /// <summary>Preserves 0 as automatic and clamps explicit values to the supported process range.</summary>
    public static int Normalize(int value)
        => value <= Automatic ? Automatic : Math.Clamp(value, Minimum, Maximum);

    /// <summary>Resolves the effective process count for one search root. The existing global HDD
    /// safeguard is authoritative: an HDD root gets one process even when the OCR setting is explicit.
    /// Automatic Paddle stays at one process because its inference runtime is internally parallel;
    /// automatic Tesseract uses two processes only on machines with at least eight logical processors.</summary>
    public static int Resolve(
        int configured,
        string? engineId,
        int logicalProcessorCount,
        bool limitParallelismOnHdd,
        bool isHardDisk)
    {
        if (limitParallelismOnHdd && isHardDisk)
            return Minimum;

        int normalized = Normalize(configured);
        if (normalized != Automatic)
            return normalized;

        int logical = Math.Max(1, logicalProcessorCount);
        bool tesseract = string.Equals(engineId?.Trim(), OcrEngineFactory.TesseractId, StringComparison.OrdinalIgnoreCase);
        return tesseract && logical >= 8 ? 2 : 1;
    }
}
