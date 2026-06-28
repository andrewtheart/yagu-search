namespace Yagu.Services.Ocr;

/// <summary>
/// Placeholder engine used while a real OCR engine's runtime/models are not yet wired up or
/// could not be installed. It reports a clear reason instead of throwing, so image-text search
/// degrades gracefully (no matches) rather than crashing the search.
/// </summary>
public sealed class UnavailableOcrEngine : IOcrEngine
{
    private readonly string _reason;

    public UnavailableOcrEngine(string id, string displayName, string reason)
    {
        Id = id;
        DisplayName = displayName;
        _reason = reason;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
        => Task.FromResult(OcrResult.Fail(_reason));

    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
        => Task.FromResult(OcrResult.Fail(_reason));
}
