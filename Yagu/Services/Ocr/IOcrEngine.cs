namespace Yagu.Services.Ocr;

/// <summary>
/// Outcome of an OCR operation (engine preparation or recognition).
/// </summary>
/// <param name="Success">True when the operation succeeded.</param>
/// <param name="Text">Recognized text (empty for non-recognition operations or on failure).</param>
/// <param name="Error">Human-readable failure reason, or <c>null</c> on success.</param>
public readonly record struct OcrResult(bool Success, string Text, string? Error)
{
    public static OcrResult Ok(string text) => new(true, text ?? string.Empty, null);

    public static OcrResult Fail(string error) => new(false, string.Empty, error);
}

/// <summary>
/// An OCR engine that recognizes text inside an image file. Implementations run on background
/// threads and must be safe to call concurrently from multiple OCR workers.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Stable identifier, e.g. <c>"paddle"</c> or <c>"tesseract"</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name for logs and UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Prepares the engine for use, lazily downloading any native runtime and models the first
    /// time it is called. Called once per search before the first recognition. Implementations
    /// must be idempotent and safe to await concurrently.
    /// </summary>
    Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Recognizes text in <paramref name="imagePath"/>. Returns the recognized text on success,
    /// or a failure result describing why recognition could not be performed.
    /// </summary>
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken);
}
