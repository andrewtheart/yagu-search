using Yagu.Services.Ocr;

namespace Yagu.Services.Index;

public sealed record ImageOcrPopulationResult(
    ExtendedSourceNamespace Namespace,
    int ImagesSeen,
    int Admitted,
    int Failed);

/// <summary>Builds positive OCR postings. Recognized text is reduced to trigrams and discarded;
/// failures persist nothing, and OCR nonmembers can never be negatively pruned.</summary>
public sealed class ImageOcrExtendedSourcePopulator
{
    private readonly IOcrEngine _engine;
    private readonly ExtractorFingerprint _fingerprint;
    private readonly Func<string, UsnFileIdentity?> _identityProvider;

    public ImageOcrExtendedSourcePopulator(
        IOcrEngine engine,
        ExtractorFingerprint fingerprint,
        Func<string, UsnFileIdentity?> identityProvider)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        if (fingerprint.Source != SpecialSourceKind.ImageOcr)
            throw new ArgumentException("Fingerprint must be for the ImageOcr source.", nameof(fingerprint));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
    }

    public async Task<ImageOcrPopulationResult> PopulateAsync(
        IReadOnlyList<string> imagePaths,
        string normalizedRootPath,
        UsnCheckpoint freshnessCheckpoint,
        CancellationToken cancellationToken = default,
        Action<ImageOcrBuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentException.ThrowIfNullOrEmpty(normalizedRootPath);
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.ImageOcr, _fingerprint);
        int admitted = 0;
        int failed = 0;

        OcrResult ready = await _engine.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!ready.Success)
            throw new ImageOcrIndexUnavailableException(ready.Error ?? "OCR engine unavailable.");

        for (int i = 0; i < imagePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = imagePaths[i];
            OcrResult result = await _engine.RecognizeAsync(path, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                UsnFileIdentity? identity = SafeIdentity(path);
                if (builder.AddSource(IndexScopeIdentity.NormalizePath(path), new ExtractionOutcome.Success(result.Text), identity) >= 0)
                    admitted++;
            }
            else
            {
                failed++;
                // Deliberately persist nothing. An OCR failure/empty recognition is never a durable negative.
            }
            progress?.Invoke(new ImageOcrBuildProgress(i + 1, imagePaths.Count));
        }

        return new ImageOcrPopulationResult(
            builder.Build(normalizedRootPath, freshnessCheckpoint),
            imagePaths.Count,
            admitted,
            failed);
    }

    private UsnFileIdentity? SafeIdentity(string path)
    {
        try { return _identityProvider(path); }
        catch { return null; }
    }
}

public sealed class ImageOcrIndexUnavailableException(string message) : Exception(message);