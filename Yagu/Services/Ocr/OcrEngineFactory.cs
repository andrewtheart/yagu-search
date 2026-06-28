namespace Yagu.Services.Ocr;

/// <summary>
/// Resolves an <see cref="IOcrEngine"/> for a normalized engine id (<c>"paddle"</c> or
/// <c>"tesseract"</c>). The concrete engines lazily download their native runtime and models on
/// first use; until those engines are wired in, an <see cref="UnavailableOcrEngine"/> is returned
/// so image-text search degrades gracefully.
/// </summary>
public static class OcrEngineFactory
{
    public const string PaddleId = "paddle";
    public const string TesseractId = "tesseract";

    /// <summary>Creates the engine for the given id (case-insensitive; unknown ids fall back to PaddleSharp).</summary>
    public static IOcrEngine Create(string? engineId)
    {
        string id = Normalize(engineId);
        return id switch
        {
            TesseractId => CreateTesseract(),
            _ => CreatePaddle(),
        };
    }

    private static string Normalize(string? engineId)
    {
        if (string.IsNullOrWhiteSpace(engineId)) return PaddleId;
        string v = engineId.Trim().ToLowerInvariant();
        return v switch
        {
            "tesseract" => TesseractId,
            "paddle" or "paddleocr" or "paddlesharp" => PaddleId,
            _ => PaddleId,
        };
    }

    // CA1859: these intentionally return the IOcrEngine interface so Create() can compose them via a
    // switch. The concrete engine types differ, so the interface return type is by design.
#pragma warning disable CA1859
    // PaddleSharp runs out-of-process in Yagu.OcrWorker.exe (it is not Native-AOT compatible).
    // PaddleOcrEngine itself is pure managed (Process + stdio) and degrades gracefully when the
    // worker binary or its lazily-downloaded native runtime is unavailable.
    private static IOcrEngine CreatePaddle()
        => new PaddleOcrEngine();

    // Tesseract also runs out-of-process in Yagu.OcrWorker.exe (selected via YAGU_OCR_ENGINE).
    // TesseractOcrEngine is pure managed and degrades gracefully when the worker is unavailable.
    private static IOcrEngine CreateTesseract()
        => new TesseractOcrEngine();
#pragma warning restore CA1859
}
