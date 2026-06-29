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
        => Create(engineId, model: null, maxSide: -1);

    /// <summary>
    /// Creates the engine for the given id with PaddleSharp quality options. <paramref name="model"/>
    /// selects the PaddleOCR model (e.g. "EnglishV4"; null/empty = worker default) and
    /// <paramref name="maxSide"/> caps the detection resolution (longest image side in pixels; 0 =
    /// unlimited, negative = use the worker default). Both are ignored by the Tesseract engine.
    /// </summary>
    public static IOcrEngine Create(string? engineId, string? model, int maxSide)
    {
        string id = Normalize(engineId);
        return id switch
        {
            TesseractId => CreateTesseract(),
            _ => CreatePaddle(model, maxSide),
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
    private static IOcrEngine CreatePaddle(string? model = null, int maxSide = -1)
        => new PaddleOcrEngine(model, maxSide);

    // Tesseract also runs out-of-process in Yagu.OcrWorker.exe (selected via YAGU_OCR_ENGINE).
    // TesseractOcrEngine is pure managed and degrades gracefully when the worker is unavailable.
    private static IOcrEngine CreateTesseract()
        => new TesseractOcrEngine();
#pragma warning restore CA1859
}
